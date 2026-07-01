using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentController.Api;

/// <summary>
/// Background service that polls for PR review comments and drives the feedback
/// materialization pipeline into <see cref="Application.ReworkCycle"/> rows.
///
/// This is a separate <see cref="BackgroundService"/> from <see cref="PollingWorker"/>
/// with its own poll interval (<see cref="FeedbackOptions.PollIntervalSeconds"/>) and
/// its own concurrency limit (<see cref="FeedbackOptions.MaxConcurrentPolls"/>).
/// The two workers are fully independent: the discovery worker claims and executes
/// work items, while the feedback worker watches open PRs for reviewer comments.
///
/// When <see cref="FeedbackOptions.Enabled"/> is false the worker sleeps indefinitely
/// until shutdown, keeping the host alive without performing any work.
/// </summary>
public sealed partial class FeedbackPollingWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonReadOptions = new(
        JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<FeedbackOptions> _options;
    private readonly ILogger<FeedbackPollingWorker> _logger;

    public FeedbackPollingWorker(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<FeedbackOptions> options,
        ILogger<FeedbackPollingWorker> logger
    )
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;

        if (!options.Enabled)
        {
            Log.WorkerDisabled(_logger);

            // Sleep indefinitely until requested to stop, keeping the host alive.
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when the host shuts down.
            }

            return;
        }

        Log.WorkerStarted(
            _logger,
            options.PollIntervalSeconds,
            options.MaxConcurrentPolls,
            options.SoakMinutes,
            options.Provider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected shutdown.
                break;
            }
            catch (Exception ex)
            {
                Log.PollCycleError(_logger, ex);
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Log.WorkerStopped(_logger);
    }

    /// <summary>
    /// Execute a single feedback poll cycle.
    ///
    /// Each cycle gets its own DI scope so scoped services (EF Core DbContext,
    /// stores, feedback source) share a single unit-of-work.
    ///
    /// Future work items will fill in the body: PR scan query, filter pipeline,
    /// soak-window logic, and ReworkCycle materialization.
    /// </summary>
    private async Task PollCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var options = _options.CurrentValue;

        Log.PollCycleStarted(_logger, options.PollIntervalSeconds);

        var runStore = scope.ServiceProvider.GetRequiredService<Application.IAgentRunStore>();
        var reworkCycleStore = scope.ServiceProvider.GetRequiredService<Application.IReworkCycleStore>();
        var reworkFeedbackStore = scope.ServiceProvider.GetRequiredService<Application.IReworkFeedbackStore>();
        var feedbackSource = scope.ServiceProvider.GetRequiredService<Application.IFeedbackSource>();
        var filterPipeline = scope.ServiceProvider.GetRequiredService<Application.ReviewFeedbackFilterPipeline>();
        var workItemStore = scope.ServiceProvider.GetRequiredService<Application.IWorkItemStore>();
        var workSource = scope.ServiceProvider.GetRequiredService<Application.IWorkSource>();

        // ── Step 1: Query eligible runs ───────────────────────────
        // Runs in {PrOpened, BranchPushed, Completed} with non-null PullRequestUrl.
        var candidateRuns = await runStore.FindRunsForFeedbackAsync(ct);

        if (candidateRuns.Count == 0)
        {
            Log.NoEligibleRuns(_logger);
            Log.PollCycleCompleted(_logger);
            return;
        }

        Log.FoundEligibleRuns(_logger, candidateRuns.Count);

        // ── Step 2: Determine work items with active rework ───────
        // A work item is "blocked" if it has a Consumed ReworkCycle whose
        // NewRunId is non-terminal (not Completed/Failed/Cancelled/CleanedUp).
        var consumedCycles = await reworkCycleStore.ListConsumedAsync(ct);

        var newRunIds = consumedCycles
            .Where(c => c.NewRunId is not null)
            .Select(c => c.NewRunId!)
            .Distinct()
            .ToList();

        HashSet<string>? blockedWorkItemIds = null;

        if (newRunIds.Count > 0)
        {
            // Fetch the status of each new run to check terminal state.
            var nonTerminalNewRunIds = new HashSet<string>();

            foreach (var newRunId in newRunIds)
            {
                var newRun = await runStore.GetByIdAsync(newRunId, ct);
                if (newRun is not null && !IsTerminalState(newRun.Status))
                {
                    nonTerminalNewRunIds.Add(newRunId);
                }
            }

            // Map non-terminal new run IDs back to their work item IDs.
            if (nonTerminalNewRunIds.Count > 0)
            {
                blockedWorkItemIds = new HashSet<string>(
                    consumedCycles
                        .Where(c => c.NewRunId is not null && nonTerminalNewRunIds.Contains(c.NewRunId!))
                        .Select(c => c.WorkItemId));

                Log.WorkItemsWithActiveRework(_logger, blockedWorkItemIds.Count);
            }
        }

        // ── Step 3: Filter candidate runs ─────────────────────────
        // Exclude runs whose work items have active rework in progress.
        var eligibleRuns = blockedWorkItemIds is not null
            ? candidateRuns.Where(r => r.WorkItemId is not null && !blockedWorkItemIds.Contains(r.WorkItemId!)).ToList()
            : candidateRuns.ToList();

        if (eligibleRuns.Count == 0)
        {
            Log.AllRunsBlockedByActiveRework(_logger);
            Log.PollCycleCompleted(_logger);
            return;
        }

        // ── Step 4: Build PrUnderTest[] ───────────────────────────
        var prsUnderTest = new List<Application.PrUnderTest>();

        foreach (var run in eligibleRuns)
        {
            var pullRequestId = ExtractPullRequestId(run.PullRequestUrl);
            if (pullRequestId is null)
            {
                Log.SkippingRunBadPrUrl(_logger, run.RunId, run.PullRequestUrl ?? "(null)");
                continue;
            }

            var repoKey = ExtractRepoKey(run.PullRequestUrl) ?? string.Empty;

            prsUnderTest.Add(new Application.PrUnderTest
            {
                OriginatingRunId = run.RunId,
                WorkItemId = run.WorkItemId ?? string.Empty,
                RepoKey = repoKey,
                PullRequestUrl = run.PullRequestUrl ?? string.Empty,
                PullRequestId = pullRequestId,
                BranchName = run.BranchName ?? string.Empty,
            });
        }

        if (prsUnderTest.Count == 0)
        {
            Log.NoValidPrUrls(_logger, eligibleRuns.Count);
            Log.PollCycleCompleted(_logger);
            return;
        }

        Log.PrUnderTestBuilt(_logger, prsUnderTest.Count);

        // ── Step 5: Poll feedback source ──────────────────────────
        var query = new Application.FeedbackQuery
        {
            OpenPrs = prsUnderTest,
            AllowedReviewers = new HashSet<string>(options.AllowedReviewers),
            ReworkMarkerTag = options.ReworkMarkerTag,
        };

        var signals = await feedbackSource.PollAsync(query, ct);

        Log.SignalsReceived(_logger, signals.Count);

        // ── Step 6: Apply filter pipeline ────────────────────────
        // 5-step load-bearing filter: marker gate, allowlist fail-closed,
        // thread-status, thread-author, comment-content.
        var filteredSignals = await filterPipeline.FilterAsync(query, signals, ct);

        Log.SignalsAfterFilter(_logger, filteredSignals.Count, signals.Count);

        if (filteredSignals.Count == 0)
        {
            Log.NoSignalsAfterFilter(_logger);
            Log.PollCycleCompleted(_logger);
            return;
        }

        // ── Step 7: Soak-window logic ────────────────────────────
        // Per ReworkSignal compute FeedbackBundleId as a stable hash of
        // sorted surviving thread ids. Track bundle state in SQLite via
        // ReworkFeedback rows so soak correctness survives restarts.

        // Group signals by PullRequestId (one PR -> one bundle).
        var signalsByPr = filteredSignals
            .GroupBy(s => s.PullRequestId)
            .ToDictionary(g => g.Key, g => g.First());

        // Fetch existing Watching rows for these PRs.
        var watchingRows = await reworkFeedbackStore.GetWatchingAsync(ct);
        var watchingByPr = watchingRows
            .GroupBy(r => r.PullRequestId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var now = DateTimeOffset.UtcNow;
        var soakThreshold = TimeSpan.FromMinutes(options.SoakMinutes);

        foreach (var (pullRequestId, signal) in signalsByPr)
        {
            ct.ThrowIfCancellationRequested();

            var bundleId = ComputeFeedbackBundleId(signal.Threads);
            var existingRows = watchingByPr.TryGetValue(pullRequestId, out var rows) ? rows : null;
            var currentRow = existingRows?.FirstOrDefault();

            if (currentRow is not null && currentRow.FeedbackBundleId == bundleId)
            {
                // Bundle unchanged — bump LastQualifyingCommentAt.
                var bundleJson = JsonSerializer.Serialize(signal.Threads);
                var updated = await reworkFeedbackStore.UpsertAsync(
                    signal.OriginatingRunId,
                    signal.PullRequestId,
                    bundleId,
                    bundleJson,
                    signal.Threads.Count,
                    signal.FirstQualifyingCommentAt,
                    signal.LastQualifyingCommentAt,
                    ReworkFeedbackStatus.Watching,
                    ct);

                Log.BundleUnchangedBumpedLastComment(_logger, pullRequestId, bundleId);
            }
            else if (currentRow is not null)
            {
                // Bundle changed — supersede prior, start fresh Watching.
                await reworkFeedbackStore.MarkSupersededAsync(currentRow.Id, ct);

                var bundleJsonChanged = JsonSerializer.Serialize(signal.Threads);
                await reworkFeedbackStore.UpsertAsync(
                    signal.OriginatingRunId,
                    signal.PullRequestId,
                    bundleId,
                    bundleJsonChanged,
                    signal.Threads.Count,
                    signal.FirstQualifyingCommentAt,
                    signal.LastQualifyingCommentAt,
                    ReworkFeedbackStatus.Watching,
                    ct);

                Log.BundleChangedSuperseded(_logger, pullRequestId, currentRow.FeedbackBundleId, bundleId);
            }
            else
            {
                // First time seeing this PR — create Watching row.
                var bundleJsonNew = JsonSerializer.Serialize(signal.Threads);
                await reworkFeedbackStore.UpsertAsync(
                    signal.OriginatingRunId,
                    signal.PullRequestId,
                    bundleId,
                    bundleJsonNew,
                    signal.Threads.Count,
                    signal.FirstQualifyingCommentAt,
                    signal.LastQualifyingCommentAt,
                    ReworkFeedbackStatus.Watching,
                    ct);

                Log.NewWatchingRow(_logger, pullRequestId, bundleId);
            }
        }

        // ── Step 8: Check all Watching rows for soak threshold ───
        // Re-fetch after mutations above so we see the latest state.
        var refreshedWatching = await reworkFeedbackStore.GetWatchingAsync(ct);

        foreach (var row in refreshedWatching)
        {
            var timeSinceLastComment = now - row.LastQualifyingCommentAt;

            if (timeSinceLastComment >= soakThreshold)
            {
                await reworkFeedbackStore.MarkSoakedAsync(row.Id, ct);
                Log.FeedbackSoaked(_logger, row.PullRequestId, row.FeedbackBundleId, row.ThreadCount);
            }
        }

        // ── Step 9: Materialize Pending ReworkCycle from soaked feedback ──
        // For each Soaked ReworkFeedback row: compute cycle number from
        // existing cycles, pull BranchName/PullRequestUrl/BaseCommitSha
        // from the prior run, and create the Pending row.
        var soakedRows = await reworkFeedbackStore.GetSoakedAsync(ct);

        foreach (var soaked in soakedRows)
        {
            ct.ThrowIfCancellationRequested();

            // Look up the prior run to get WorkItemId, BranchName, PullRequestUrl, CommitSha.
            var priorRun = await runStore.GetByIdAsync(soaked.OriginatingRunId, ct);
            if (priorRun is null)
            {
                Log.MissingPriorRun(_logger, soaked.OriginatingRunId, soaked.PullRequestId);
                continue;
            }

            if (priorRun.WorkItemId is null)
            {
                Log.MissingWorkItemId(_logger, soaked.OriginatingRunId, soaked.PullRequestId);
                continue;
            }

            if (priorRun.BranchName is null || priorRun.PullRequestUrl is null || priorRun.CommitSha is null)
            {
                Log.IncompletePriorRun(_logger, soaked.OriginatingRunId, soaked.PullRequestId);
                continue;
            }

            // Skip if this bundle was already materialized (e.g. by a prior poll cycle).
            if (await reworkCycleStore.ExistsByFeedbackBundleIdAsync(soaked.FeedbackBundleId, ct))
            {
                await reworkFeedbackStore.MarkMaterializedAsync(soaked.Id, ct);
                Log.ReworkCycleAlreadyMaterialized(_logger, soaked.FeedbackBundleId);
                continue;
            }

            // Compute cycle number = max existing cycle for this work item + 1.
            var maxCycle = await reworkCycleStore.GetMaxCycleNumberAsync(priorRun.WorkItemId, ct);
            var cycleNumber = maxCycle + 1;

            await reworkCycleStore.CreateAsync(
                priorRun.WorkItemId,
                cycleNumber,
                soaked.OriginatingRunId,
                priorRun.BranchName,
                priorRun.PullRequestUrl,
                priorRun.CommitSha,
                soaked.FeedbackBundleJson,
                soaked.FeedbackBundleId,
                ct);

            // Transition the feedback out of Soaked so it's not
            // re-processed on the next poll cycle.
            await reworkFeedbackStore.MarkMaterializedAsync(soaked.Id, ct);

            Log.ReworkCycleMaterialized(
                _logger,
                priorRun.WorkItemId,
                cycleNumber,
                soaked.FeedbackBundleId,
                soaked.ThreadCount);
        }

        // ── Step 10: Reactivate work items for Pending ReworkCycles ──
        // For each Pending cycle that hasn't been consumed yet, reactivate
        // the work item in the external source (state transition + tag cleanup)
        // so the PollingWorker can re-discover and claim it.
        var pendingCycles = await reworkCycleStore.ListPendingAsync(ct);

        foreach (var cycle in pendingCycles)
        {
            ct.ThrowIfCancellationRequested();

            var workItem = await workItemStore.GetByIdAsync(cycle.WorkItemId, ct);
            if (workItem is null)
            {
                Log.MissingWorkItemForRework(_logger, cycle.WorkItemId, cycle.Id);
                continue;
            }

            // Build the external work reference from stored metadata.
            var revision = workItem.SourceMetadata?.TryGetValue("revision", out var rev) == true
                ? rev
                : null;

            var workRef = new Domain.ExternalWorkRef
            {
                Source = workItem.Source,
                ExternalId = workItem.ExternalId,
                Url = workItem.ExternalUrl,
                Revision = revision,
            };

            var request = new Domain.ReworkReactivateRequest
            {
                WorkItemId = cycle.WorkItemId,
                WorkRef = workRef,
                CycleNumber = cycle.CycleNumber,
                ThreadCount = cycle.FeedbackBundleJson != null
                    ? JsonSerializer.Deserialize<Domain.ReviewThread[]>(
                        cycle.FeedbackBundleJson, JsonReadOptions)?.Length ?? 0
                    : 0,
                PullRequestUrl = cycle.PullRequestUrl,
            };

            try
            {
                var result = await workSource.ReactivateForReworkAsync(request, ct);

                if (result.Success)
                {
                    Log.ReworkItemReactivated(_logger, cycle.WorkItemId, cycle.CycleNumber, cycle.Id);
                }
                else
                {
                    Log.ReworkItemReactivationFailed(
                        _logger, cycle.WorkItemId, cycle.CycleNumber, cycle.Id, result.FailureReason ?? "unknown");
                }
            }
            catch (Exception ex)
            {
                Log.ReworkItemReactivationError(_logger, cycle.WorkItemId, cycle.CycleNumber, cycle.Id, ex);
            }
        }

        Log.PollCycleCompleted(_logger);
    }

    /// <summary>
    /// Extract the pull request integer ID from an Azure DevOps PR URL.
    /// Supported formats:
    ///   https://dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/{id}
    ///   https://{org}.visualstudio.com/{project}/_git/{repo}/pullrequest/{id}
    /// Returns null if the URL cannot be parsed.
    /// </summary>
    private static string? ExtractPullRequestId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var segments = uri.Segments;

        // Find the "pullrequest/" segment; the next segment is the ID.
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Equals("pullrequest/", StringComparison.Ordinal)
                && i + 1 < segments.Length)
            {
                return segments[i + 1].TrimEnd('/');
            }
        }

        return null;
    }

    /// <summary>
    /// Extract a repo key ("{project}/{repository}") from an Azure DevOps PR URL.
    /// Returns null if the URL cannot be parsed.
    /// </summary>
    private static string? ExtractRepoKey(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var segments = uri.Segments;

        // Find the "_git/" segment; project is before it, repo is after it.
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Equals("_git/", StringComparison.Ordinal)
                && i > 0 && i + 1 < segments.Length)
            {
                var project = segments[i - 1].TrimEnd('/');
                var repository = segments[i + 1].TrimEnd('/');

                if (!string.IsNullOrWhiteSpace(project) && !string.IsNullOrWhiteSpace(repository))
                {
                    return $"{project}/{repository}";
                }

                break;
            }
        }

        return null;
    }

    /// <summary>
    /// Check whether a run lifecycle state is terminal.
    /// Terminal states: Completed, Failed, Cancelled, CleanedUp.
    /// </summary>
    private static bool IsTerminalState(Domain.RunLifecycleState status)
    {
        return status.IsTerminal();
    }

    /// <summary>
    /// Compute a stable FeedbackBundleId as a SHA-256 hash of sorted thread ids.
    /// Sorting ensures the hash is deterministic regardless of thread ordering
    /// from the feedback source.
    /// </summary>
    private static string ComputeFeedbackBundleId(IReadOnlyList<ReviewThread> threads)
    {
        var sortedIds = threads
            .OrderBy(t => t.ThreadId, StringComparer.Ordinal)
            .Select(t => t.ThreadId)
            .Aggregate((a, b) => a + "|" + b);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sortedIds));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Run a single poll cycle synchronously from test code.
    /// Exposed as internal for integration-style tests.
    /// </summary>
    internal async Task RunPollCycleForTestingAsync(CancellationToken ct = default)
    {
        await PollCycleAsync(ct);
    }

    /// <summary>
    /// Source-generated high-performance logger methods.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Feedback polling worker is disabled (feedback.enabled=false). "
                + "No PR review comments will be polled.")]
        public static partial void WorkerDisabled(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Feedback polling worker started. PollInterval={PollInterval}s, "
                + "MaxConcurrency={MaxConcurrency}, SoakMinutes={SoakMinutes}, Provider='{Provider}'")]
        public static partial void WorkerStarted(
            ILogger logger, int pollInterval, int maxConcurrency, int soakMinutes, string provider);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Unhandled exception in feedback polling cycle. Worker will retry after delay.")]
        public static partial void PollCycleError(ILogger logger, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Feedback polling worker stopped.")]
        public static partial void WorkerStopped(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Feedback poll cycle started (interval={Interval}s).")]
        public static partial void PollCycleStarted(ILogger logger, int interval);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Feedback poll cycle completed.")]
        public static partial void PollCycleCompleted(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "No eligible runs found for feedback polling.")]
        public static partial void NoEligibleRuns(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Found {Count} eligible run(s) for feedback polling.")]
        public static partial void FoundEligibleRuns(ILogger logger, int count);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "{Count} work item(s) have active rework in progress (non-terminal new runs).")]
        public static partial void WorkItemsWithActiveRework(ILogger logger, int count);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "All eligible runs blocked by active rework on their work items.")]
        public static partial void AllRunsBlockedByActiveRework(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Skipping run {RunId}: could not extract PR ID from URL '{PullRequestUrl}'.")]
        public static partial void SkippingRunBadPrUrl(ILogger logger, string runId, string pullRequestUrl);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "No valid PR URLs among {Count} eligible run(s) — cannot build PrUnderTest list.")]
        public static partial void NoValidPrUrls(ILogger logger, int count);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Built PrUnderTest list with {Count} PR(s).")]
        public static partial void PrUnderTestBuilt(ILogger logger, int count);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Feedback source returned {Count} rework signal(s).")]
        public static partial void SignalsReceived(ILogger logger, int count);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Filter pipeline: {FilteredCount} signal(s) survived (of {OriginalCount} raw signal(s)).")]
        public static partial void SignalsAfterFilter(
            ILogger logger, int filteredCount, int originalCount);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "No rework signals survived the filter pipeline.")]
        public static partial void NoSignalsAfterFilter(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "PR {PullRequestId}: bundle unchanged, bumped LastQualifyingCommentAt [bundle={BundleId}].")]
        public static partial void BundleUnchangedBumpedLastComment(
            ILogger logger, string pullRequestId, string bundleId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "PR {PullRequestId}: bundle changed, superseded old bundle [{OldBundleId}] with new [{NewBundleId}].")]
        public static partial void BundleChangedSuperseded(
            ILogger logger, string pullRequestId, string oldBundleId, string newBundleId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "PR {PullRequestId}: new feedback bundle, started Watching [{BundleId}].")]
        public static partial void NewWatchingRow(
            ILogger logger, string pullRequestId, string bundleId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "PR {PullRequestId}: feedback soaked [{BundleId}] — {ThreadCount} thread(s) eligible for materialization.")]
        public static partial void FeedbackSoaked(
            ILogger logger, string pullRequestId, string bundleId, int threadCount);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Cannot materialize rework cycle: prior run {RunId} not found for PR {PullRequestId}.")]
        public static partial void MissingPriorRun(
            ILogger logger, string runId, string pullRequestId);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Cannot materialize rework cycle: prior run {RunId} has no WorkItemId for PR {PullRequestId}.")]
        public static partial void MissingWorkItemId(
            ILogger logger, string runId, string pullRequestId);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Cannot materialize rework cycle: prior run {RunId} missing BranchName/PullRequestUrl/CommitSha for PR {PullRequestId}.")]
        public static partial void IncompletePriorRun(
            ILogger logger, string runId, string pullRequestId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Materialized ReworkCycle #{CycleNumber} for work item {WorkItemId} "
                + "[bundle={BundleId}] — {ThreadCount} thread(s).")]
        public static partial void ReworkCycleMaterialized(
            ILogger logger, string workItemId, int cycleNumber, string bundleId, int threadCount);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "ReworkCycle for bundle [{BundleId}] already materialized (unique constraint).")]
        public static partial void ReworkCycleAlreadyMaterialized(
            ILogger logger, string bundleId);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Cannot reactivate work item {WorkItemId} for rework cycle {CycleId}: work item not found in store.")]
        public static partial void MissingWorkItemForRework(
            ILogger logger, string workItemId, string cycleId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "[rework] Reactivated work item {WorkItemId} for cycle #{CycleNumber} (cycleId={CycleId}).")]
        public static partial void ReworkItemReactivated(
            ILogger logger, string workItemId, int cycleNumber, string cycleId);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "[rework] Failed to reactivate work item {WorkItemId} for cycle #{CycleNumber} (cycleId={CycleId}): {FailureReason}.")]
        public static partial void ReworkItemReactivationFailed(
            ILogger logger, string workItemId, int cycleNumber, string cycleId, string failureReason);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "[rework] Error reactivating work item {WorkItemId} for cycle #{CycleNumber} (cycleId={CycleId}).")]
        public static partial void ReworkItemReactivationError(
            ILogger logger, string workItemId, int cycleNumber, string cycleId, Exception ex);
    }
}
