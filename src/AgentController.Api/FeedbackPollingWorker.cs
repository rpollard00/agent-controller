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
        var feedbackSource = scope.ServiceProvider.GetRequiredService<Application.IFeedbackSource>();

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

        // TODO: filter pipeline, soak-window logic, and ReworkCycle
        // materialization in subsequent work items.

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
        return status is Domain.RunLifecycleState.Completed
            or Domain.RunLifecycleState.Failed
            or Domain.RunLifecycleState.Cancelled
            or Domain.RunLifecycleState.CleanedUp;
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
    }
}
