using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentController.Application;
using AgentController.Application.Abstractions;
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
    private static readonly JsonSerializerOptions JsonReadOptions = new(JsonSerializerDefaults.Web)
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
            options.Provider
        );

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
                await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
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
        var reworkCycleStore =
            scope.ServiceProvider.GetRequiredService<Application.IReworkCycleStore>();
        var reworkFeedbackStore =
            scope.ServiceProvider.GetRequiredService<Application.IReworkFeedbackStore>();
        var repositoryStore =
            scope.ServiceProvider.GetRequiredService<Application.IRepositoryStore>();
        var connectionStore =
            scope.ServiceProvider.GetService<Application.IConnectionStore>();
        var reviewerIdentityPolicyResolver =
            scope.ServiceProvider.GetService<IReviewerIdentityPolicyResolver>()
            ?? new ReviewerIdentityPolicyResolver([new AzureDevOpsReviewerIdentityPolicy()]);
        var feedbackSource =
            scope.ServiceProvider.GetRequiredService<Application.IFeedbackSource>();
        var filterPipeline =
            scope.ServiceProvider.GetRequiredService<Application.ReviewFeedbackFilterPipeline>();
        var workItemStore = scope.ServiceProvider.GetRequiredService<Application.IWorkItemStore>();
        var workSource = scope.ServiceProvider.GetRequiredService<Application.IWorkSource>();
        var pullRequestDiscovery =
            scope.ServiceProvider.GetRequiredService<Application.IManagedPullRequestDiscovery>();

        // ── Step 1: Query run-backed revival PRs and managed PRs in parallel ──
        // Assistance discovery is intentionally independent of originating runs so
        // controller-external pull requests can enter the soak lifecycle.
        var candidateRunsTask = runStore.FindRunsForFeedbackAsync(ct);
        var managedPullRequestsTask = DiscoverManagedPullRequestsAsync(
            pullRequestDiscovery,
            ct
        );
        await Task.WhenAll(candidateRunsTask, managedPullRequestsTask);

        var candidateRuns = await candidateRunsTask;
        var managedPullRequests = await managedPullRequestsTask;
        var assistanceSnapshots = managedPullRequests
            .Where(snapshot => HasLabel(snapshot.Labels, options.AssistanceMarkerTag))
            .ToList();
        var managedRepositoryProfiles = await repositoryStore.ListAsync(ct);
        var repositoryReviewerPolicies = await LoadRepositoryReviewerPoliciesAsync(
            managedRepositoryProfiles,
            connectionStore,
            reviewerIdentityPolicyResolver,
            ct
        );

        if (candidateRuns.Count == 0)
        {
            Log.NoEligibleRuns(_logger);
        }
        else
        {
            Log.FoundEligibleRuns(_logger, candidateRuns.Count);
        }

        if (assistanceSnapshots.Count > 0)
        {
            Log.AssistanceRequestsDiscovered(_logger, assistanceSnapshots.Count);
        }

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
                        .Where(c =>
                            c.NewRunId is not null && nonTerminalNewRunIds.Contains(c.NewRunId!)
                        )
                        .Select(c => c.WorkItemId)
                );

                Log.WorkItemsWithActiveRework(_logger, blockedWorkItemIds.Count);
            }
        }

        // ── Step 3: Filter candidate runs ─────────────────────────
        // Exclude runs whose work items have active rework in progress.
        var eligibleRuns = blockedWorkItemIds is not null
            ? candidateRuns
                .Where(r => r.WorkItemId is not null && !blockedWorkItemIds.Contains(r.WorkItemId!))
                .ToList()
            : candidateRuns.ToList();

        if (candidateRuns.Count > 0 && eligibleRuns.Count == 0)
        {
            Log.AllRunsBlockedByActiveRework(_logger);
        }

        // ── Step 4: Build mode-specific PrUnderTest lists ─────────
        var assistancePrs = new List<Application.PrUnderTest>();
        foreach (var snapshot in assistanceSnapshots)
        {
            if (!snapshot.PullRequest.HasCanonicalIdentity)
            {
                Log.AssistanceRequestMissingCanonicalIdentity(
                    _logger,
                    snapshot.PullRequestId,
                    snapshot.PullRequestUrl
                );
                continue;
            }

            // Retain controller lineage when this happens to be a controller-produced
            // PR, but never require it for Assistance.
            var originatingRun = candidateRuns.FirstOrDefault(run =>
                Application.PullRequestIdentityMatcher.Matches(
                    snapshot.PullRequest,
                    run.PullRequestUrl
                )
            );

            var assistanceReviewerPolicy = repositoryReviewerPolicies.TryGetValue(
                snapshot.RepositoryKey,
                out var configuredAssistanceReviewers)
                ? configuredAssistanceReviewers
                : RepositoryReviewerPolicy.Empty;

            assistancePrs.Add(
                new Application.PrUnderTest
                {
                    RequestMode = ReworkRequestMode.Assistance,
                    PullRequest = snapshot.PullRequest,
                    OriginatingRunId = originatingRun?.RunId,
                    WorkItemId = originatingRun?.WorkItemId,
                    RepoKey = snapshot.RepositoryKey,
                    PullRequestUrl = snapshot.PullRequestUrl,
                    PullRequestId = snapshot.PullRequestId,
                    BranchName = snapshot.SourceBranch,
                    ReviewerIdentityProvider = assistanceReviewerPolicy.Provider,
                    ReviewerIdentities = assistanceReviewerPolicy.Identities,
                }
            );
        }

        var revivalPrs = new List<Application.PrUnderTest>();
        foreach (var run in eligibleRuns)
        {
            // Assistance wins before Revival repository resolution. This preserves
            // marker precedence even when a Revival-only repository lookup is missing
            // or ambiguous.
            var assistanceMatch = assistancePrs.FirstOrDefault(pr =>
                Application.PullRequestIdentityMatcher.Matches(
                    pr.PullRequest,
                    run.PullRequestUrl
                ));
            if (assistanceMatch is not null)
            {
                Log.RevivalSuppressedByAssistance(
                    _logger,
                    assistanceMatch.PullRequestId
                );
                continue;
            }

            var resolution = Application.ManagedPullRequestResolver.Resolve(
                run.PullRequestUrl,
                managedPullRequests,
                managedRepositoryProfiles
            );
            if (!resolution.IsResolved || resolution.Snapshot is null)
            {
                var unresolvedPullRequestId =
                    Application.PullRequestIdentityMatcher.ExtractPullRequestId(
                        run.PullRequestUrl
                    ) ?? "(unknown)";
                if (resolution.Status
                    == Application.ManagedPullRequestResolutionStatus.Ambiguous)
                {
                    Log.RevivalPullRequestResolutionAmbiguous(
                        _logger,
                        run.RunId,
                        unresolvedPullRequestId,
                        resolution.CandidateCount
                    );
                }
                else
                {
                    Log.RevivalPullRequestResolutionMissing(
                        _logger,
                        run.RunId,
                        unresolvedPullRequestId
                    );
                }

                continue;
            }

            var managedSnapshot = resolution.Snapshot;
            var pullRequestId = managedSnapshot.PullRequestId;
            var pullRequest = managedSnapshot.PullRequest with
            {
                SourceBranch = FirstNonEmpty(
                    managedSnapshot.SourceBranch,
                    run.BranchName
                ),
                SourceCommitSha = FirstNonEmpty(
                    managedSnapshot.SourceCommitSha,
                    run.CommitSha
                ),
            };
            var repoKey = managedSnapshot.RepositoryKey;
            var revivalReviewerPolicy = repositoryReviewerPolicies.TryGetValue(
                repoKey,
                out var configuredRevivalReviewers)
                ? configuredRevivalReviewers
                : RepositoryReviewerPolicy.Empty;

            revivalPrs.Add(
                new Application.PrUnderTest
                {
                    RequestMode = ReworkRequestMode.Revival,
                    PullRequest = pullRequest,
                    OriginatingRunId = run.RunId,
                    WorkItemId = run.WorkItemId,
                    RepoKey = repoKey,
                    PullRequestUrl = pullRequest.PullRequestUrl,
                    PullRequestId = pullRequestId,
                    BranchName = FirstNonEmpty(
                        managedSnapshot.SourceBranch,
                        run.BranchName
                    ),
                    ReviewerIdentityProvider = revivalReviewerPolicy.Provider,
                    ReviewerIdentities = revivalReviewerPolicy.Identities,
                }
            );
        }

        if (revivalPrs.Count == 0 && assistancePrs.Count == 0)
        {
            if (eligibleRuns.Count > 0)
            {
                Log.NoValidPrUrls(_logger, eligibleRuns.Count);
            }

            Log.PollCycleCompleted(_logger);
            return;
        }

        Log.PrUnderTestBuilt(_logger, revivalPrs.Count + assistancePrs.Count);

        // ── Steps 5-6: Poll and filter each request mode ──────────
        var filteredSignals = new List<Application.ReworkSignal>();
        var rawSignalCount = 0;

        if (revivalPrs.Count > 0)
        {
            var revivalQuery = new Application.FeedbackQuery
            {
                OpenPrs = revivalPrs,
                ReworkMarkerTag = options.ReworkMarkerTag,
            };
            var revivalSignals = await feedbackSource.PollAsync(revivalQuery, ct);
            rawSignalCount += revivalSignals.Count;
            filteredSignals.AddRange(
                await filterPipeline.FilterAsync(revivalQuery, revivalSignals, ct)
            );
        }

        if (assistancePrs.Count > 0)
        {
            var assistanceQuery = new Application.FeedbackQuery
            {
                OpenPrs = assistancePrs,
                ReworkMarkerTag = options.AssistanceMarkerTag,
            };
            var fetchedAssistanceSignals = await feedbackSource.PollAsync(assistanceQuery, ct);
            rawSignalCount += fetchedAssistanceSignals.Count;

            // Feedback sources historically omit PRs with no threads. Synthesize one
            // observation per marker-bearing PR so zero-comment cleanup requests soak.
            var observedAt = DateTimeOffset.UtcNow;
            var assistanceSignals = assistancePrs
                .Select(pr => CreateAssistanceObservation(
                    pr,
                    fetchedAssistanceSignals,
                    observedAt
                ))
                .ToList();

            filteredSignals.AddRange(
                await filterPipeline.FilterAssistanceAsync(
                    assistanceQuery,
                    assistanceSignals,
                    ct
                )
            );
        }

        Log.SignalsReceived(_logger, rawSignalCount);
        Log.SignalsAfterFilter(_logger, filteredSignals.Count, rawSignalCount);

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

        // Assistance is keyed by canonical PR identity; Revival retains its historical
        // provider PR-ID grouping. This prevents same-numbered PRs in different managed
        // repositories from sharing an assistance soak row.
        var signalsByPr = filteredSignals
            .GroupBy(GetObservationKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase
            );

        var watchingRows = (await reworkFeedbackStore.GetWatchingAsync(ct)).ToList();

        // If a PR changes from the revival marker to assistance (or carries both),
        // retire its outstanding revival observation before processing Assistance.
        var suppressedRevivalRows = watchingRows
            .Where(row => row.RequestMode == ReworkRequestMode.Revival)
            .Where(row => assistancePrs.Any(pr =>
                AssistanceMatchesRevivalFeedback(pr.PullRequest, row)
            ))
            .ToList();
        foreach (var row in suppressedRevivalRows)
        {
            await reworkFeedbackStore.MarkSupersededAsync(row.Id, ct);
            watchingRows.Remove(row);
            Log.RevivalWatchingSuppressedByAssistance(_logger, row.PullRequestId);
        }

        // The persisted observation is the source of truth for soak state across
        // polling windows. Watching-only lookups would miss rows that already soaked
        // or materialized and restart the soak every poll, or re-materialize a
        // finished bundle. Tracked rows cover Watching, Soaked, and Materialized.
        var trackedRows = (await reworkFeedbackStore.GetTrackedAsync(ct)).ToList();
        var trackedByPr = trackedRows
            .GroupBy(GetObservationKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToList(),
                StringComparer.OrdinalIgnoreCase
            );

        var now = DateTimeOffset.UtcNow;
        var soakThreshold = TimeSpan.FromMinutes(options.SoakMinutes);

        foreach (var (observationKey, unadjustedSignal) in signalsByPr)
        {
            ct.ThrowIfCancellationRequested();

            var pullRequestId = unadjustedSignal.PullRequestId;
            var bundleId = ComputeFeedbackBundleId(unadjustedSignal.Threads);

            // Prefer the persisted row matching this bundle; otherwise fall back to
            // the most recently updated active row (the bundle-changed case) so a
            // new bundle replaces the prior observation rather than starting a
            // duplicate soak alongside it.
            var persistedForPr = trackedByPr.TryGetValue(observationKey, out var persistedRows)
                ? persistedRows
                : null;
            var currentRow = persistedForPr?.FirstOrDefault(row =>
                    row.FeedbackBundleId == bundleId)
                ?? persistedForPr?.OrderByDescending(row => row.UpdatedAt).FirstOrDefault();

            if (unadjustedSignal.RequestMode == ReworkRequestMode.Assistance)
            {
                var correlationId = ComputeMaterializationCorrelationId(
                    unadjustedSignal.PullRequest,
                    bundleId
                );
                var persisted = await reworkFeedbackStore.GetByCorrelationIdAsync(
                    correlationId,
                    ct
                );

                // A queued/materialized assistance request remains marker-bearing until
                // execution completes. Do not restart its soak on every poll.
                if (persisted is not null
                    && persisted.Status is ReworkFeedbackStatus.Soaked
                        or ReworkFeedbackStatus.Materialized)
                {
                    Log.AssistanceObservationAlreadyAdvanced(
                        _logger,
                        pullRequestId,
                        persisted.Status
                    );
                    continue;
                }

                currentRow ??= persisted;
            }

            var signal = NormalizeObservationTimestamp(unadjustedSignal, currentRow, now);

            // The same bundle already soaked or materialized on a prior poll.
            // Preserve the advanced lifecycle: do not restart the soak and do not
            // re-materialize. (Assistance is handled above by correlation; this is
            // the revival guarantee that repeated polls keep the existing soak.)
            if (currentRow is not null
                && currentRow.FeedbackBundleId == bundleId
                && currentRow.Status is ReworkFeedbackStatus.Soaked
                    or ReworkFeedbackStatus.Materialized)
            {
                Log.RevivalObservationAlreadyAdvanced(
                    _logger,
                    pullRequestId,
                    currentRow.Status
                );
                continue;
            }

            if (currentRow is not null && currentRow.FeedbackBundleId == bundleId)
            {
                // Same qualifying threads: only a genuinely newer qualifying comment
                // advances LastQualifyingCommentAt. Empty bundles retain first observation.
                var bundleJson = JsonSerializer.Serialize(signal.Threads);
                await reworkFeedbackStore.UpsertAsync(
                    CreateFeedbackUpsertRequest(signal, bundleId, bundleJson),
                    ct
                );

                Log.BundleUnchangedBumpedLastComment(_logger, pullRequestId, bundleId);
            }
            else if (currentRow is not null)
            {
                // Bundle changed — retire the prior Watching observation and start a
                // fresh soak. Soaked/Materialized rows are terminal materialization
                // records and must not be regressed; the new bundle simply starts
                // alongside the completed one.
                if (currentRow.Status == ReworkFeedbackStatus.Watching)
                {
                    await reworkFeedbackStore.MarkSupersededAsync(currentRow.Id, ct);
                }

                var bundleJsonChanged = JsonSerializer.Serialize(signal.Threads);
                await reworkFeedbackStore.UpsertAsync(
                    CreateFeedbackUpsertRequest(signal, bundleId, bundleJsonChanged),
                    ct
                );

                Log.BundleChangedSuperseded(
                    _logger,
                    pullRequestId,
                    currentRow.FeedbackBundleId,
                    bundleId
                );
            }
            else
            {
                // First time seeing this PR — create Watching row. For Assistance,
                // the marker observation itself starts the soak even when comments are old.
                var bundleJsonNew = JsonSerializer.Serialize(signal.Threads);
                await reworkFeedbackStore.UpsertAsync(
                    CreateFeedbackUpsertRequest(signal, bundleId, bundleJsonNew),
                    ct
                );

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
                Log.FeedbackSoaked(
                    _logger,
                    row.PullRequestId,
                    row.FeedbackBundleId,
                    row.ThreadCount
                );
            }
        }

        // ── Step 9: Materialize Pending ReworkCycle from soaked feedback ──
        // Assistance creates a fresh, correlation-tagged story and publishes it only
        // after its Pending cycle is durable. Revival retains the original-story path.
        var soakedRows = await reworkFeedbackStore.GetSoakedAsync(ct);
        Application.AssistanceStoryMaterializer? assistanceMaterializer = null;

        foreach (var soaked in soakedRows)
        {
            ct.ThrowIfCancellationRequested();

            if (soaked.RequestMode == ReworkRequestMode.Assistance)
            {
                assistanceMaterializer ??= new Application.AssistanceStoryMaterializer(
                    runStore,
                    workItemStore,
                    workSource,
                    reworkCycleStore,
                    reworkFeedbackStore,
                    scope.ServiceProvider.GetRequiredService<
                        Application.IAssistanceStoryRelationshipResolver>(),
                    scope.ServiceProvider.GetRequiredService<
                        Application.IPullRequestCommentCreator>()
                );
                var pullRequestTitle = managedPullRequests.FirstOrDefault(snapshot =>
                    Application.PullRequestIdentityMatcher.Matches(
                        snapshot.PullRequest,
                        soaked.PullRequest
                    )
                )?.Title;
                var materialized = await assistanceMaterializer.MaterializeAsync(
                    soaked,
                    pullRequestTitle,
                    ct
                );
                Log.AssistanceStoryMaterialized(
                    _logger,
                    soaked.PullRequestId,
                    materialized.Story.ExternalId,
                    materialized.Cycle.CycleNumber,
                    soaked.ThreadCount
                );
                continue;
            }

            if (soaked.RequestMode != ReworkRequestMode.Revival)
            {
                Log.NonRevivalFeedbackSkipped(
                    _logger,
                    soaked.RequestMode,
                    soaked.PullRequestId
                );
                continue;
            }

            if (string.IsNullOrWhiteSpace(soaked.OriginatingRunId))
            {
                Log.MissingPriorRun(_logger, "(none)", soaked.PullRequestId);
                continue;
            }

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

            if (
                priorRun.BranchName is null
                || priorRun.PullRequestUrl is null
                || priorRun.CommitSha is null
            )
            {
                Log.IncompletePriorRun(_logger, soaked.OriginatingRunId, soaked.PullRequestId);
                continue;
            }

            // Skip if this bundle was already materialized (e.g. by a prior poll cycle).
            if (await reworkCycleStore.ExistsAsync(
                    soaked.RequestMode,
                    soaked.PullRequest,
                    soaked.FeedbackBundleId,
                    ct))
            {
                await reworkFeedbackStore.MarkMaterializedAsync(soaked.Id, ct);
                Log.ReworkCycleAlreadyMaterialized(_logger, soaked.FeedbackBundleId);
                continue;
            }

            // Compute cycle number = max existing cycle for this work item + 1.
            var maxCycle = await reworkCycleStore.GetMaxCycleNumberAsync(priorRun.WorkItemId, ct);
            var cycleNumber = maxCycle + 1;

            await reworkCycleStore.CreateAsync(
                new Application.ReworkCycleCreateRequest
                {
                    RequestMode = soaked.RequestMode,
                    PullRequest = soaked.PullRequest,
                    WorkItemId = priorRun.WorkItemId,
                    CycleNumber = cycleNumber,
                    PriorRunId = soaked.OriginatingRunId,
                    BranchName = priorRun.BranchName,
                    PullRequestUrl = priorRun.PullRequestUrl,
                    BaseCommitSha = priorRun.CommitSha,
                    FeedbackBundleJson = soaked.FeedbackBundleJson,
                    FeedbackBundleId = soaked.FeedbackBundleId,
                    CorrelationId = soaked.CorrelationId,
                },
                ct
            );

            // Transition the feedback out of Soaked so it's not
            // re-processed on the next poll cycle.
            await reworkFeedbackStore.MarkMaterializedAsync(soaked.Id, ct);

            Log.ReworkCycleMaterialized(
                _logger,
                priorRun.WorkItemId,
                cycleNumber,
                soaked.FeedbackBundleId,
                soaked.ThreadCount
            );
        }

        // ── Step 10: Reactivate work items for Pending ReworkCycles ──
        // For each Pending cycle that hasn't been consumed yet, reactivate
        // the work item in the external source (state transition + tag cleanup)
        // so the PollingWorker can re-discover and claim it.
        // Skip cycles already reactivated (tracked on the cycle itself,
        // not the work item whose local status may be stale).
        var pendingCycles = await reworkCycleStore.ListPendingAsync(ct);

        foreach (var cycle in pendingCycles)
        {
            ct.ThrowIfCancellationRequested();

            // Assistance stories are newly created and explicitly published only after
            // their cycle is durable. They must never revive an originating story.
            if (cycle.RequestMode == ReworkRequestMode.Assistance)
            {
                continue;
            }

            // Skip: already reactivated by a prior poll cycle.
            if (cycle.ReactivatedAt is not null)
            {
                continue;
            }

            var workItem = await workItemStore.GetByIdAsync(cycle.WorkItemId, ct);
            if (workItem is null)
            {
                Log.MissingWorkItemForRework(_logger, cycle.WorkItemId, cycle.Id);
                continue;
            }

            // Build the external work reference from stored metadata.
            var revision =
                workItem.SourceMetadata?.TryGetValue("revision", out var rev) == true ? rev : null;

            var workRef = new Domain.ExternalWorkRef
            {
                Source = workItem.Source,
                ExternalId = workItem.ExternalId,
                Url = workItem.ExternalUrl,
                Revision = revision,
                EnvironmentKey =
                    workItem.SourceMetadata?.TryGetValue(
                        "workSourceEnvironmentKey",
                        out var environmentKey
                    ) == true
                        ? environmentKey
                        : null,
            };

            var request = new Domain.ReworkReactivateRequest
            {
                WorkItemId = cycle.WorkItemId,
                WorkRef = workRef,
                CycleNumber = cycle.CycleNumber,
                ThreadCount =
                    cycle.FeedbackBundleJson != null
                        ? JsonSerializer
                            .Deserialize<Domain.ReviewThread[]>(
                                cycle.FeedbackBundleJson,
                                JsonReadOptions
                            )
                            ?.Length
                            ?? 0
                        : 0,
                PullRequestUrl = cycle.PullRequestUrl,
            };

            try
            {
                var result = await workSource.ReactivateForReworkAsync(request, ct);

                if (result.Success)
                {
                    // Mark reactivated so we don't repeat on the next poll cycle.
                    await reworkCycleStore.MarkReactivatedAsync(cycle.Id, ct);
                    Log.ReworkItemReactivated(
                        _logger,
                        cycle.WorkItemId,
                        cycle.CycleNumber,
                        cycle.Id
                    );
                }
                else
                {
                    Log.ReworkItemReactivationFailed(
                        _logger,
                        cycle.WorkItemId,
                        cycle.CycleNumber,
                        cycle.Id,
                        result.FailureReason ?? "unknown"
                    );
                }
            }
            catch (Exception ex)
            {
                Log.ReworkItemReactivationError(
                    _logger,
                    cycle.WorkItemId,
                    cycle.CycleNumber,
                    cycle.Id,
                    ex
                );
            }
        }

        Log.PollCycleCompleted(_logger);
    }

    private static async Task<IReadOnlyDictionary<string, RepositoryReviewerPolicy>>
        LoadRepositoryReviewerPoliciesAsync(
            IReadOnlyList<RepositoryProfile> repositories,
            Application.IConnectionStore? connectionStore,
            IReviewerIdentityPolicyResolver policyResolver,
            CancellationToken cancellationToken)
    {
        var connections = connectionStore is null
            ? []
            : await connectionStore.ListAsync(cancellationToken);
        var connectionsByKey = connections.ToDictionary(
            connection => connection.Key,
            StringComparer.OrdinalIgnoreCase);
        var policies = new Dictionary<string, RepositoryReviewerPolicy>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var repository in repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connection = !string.IsNullOrWhiteSpace(repository.RepositoryHostConnectionKey)
                && connectionsByKey.TryGetValue(
                    repository.RepositoryHostConnectionKey,
                    out var resolvedConnection)
                ? resolvedConnection
                : null;
            var policy = connection is null
                ? null
                : policyResolver.Resolve(connection.Provider);
            var identities = policy is null
                && (!string.IsNullOrWhiteSpace(repository.RepositoryHostConnectionKey)
                    || connection is not null)
                ? []
                : NormalizeReviewerIdentities(repository.ReviewerIdentities, policy);

            policies[repository.Key] = new RepositoryReviewerPolicy(
                connection?.Provider,
                identities);
        }

        return policies;
    }

    private static List<ReviewerIdentity> NormalizeReviewerIdentities(
        IReadOnlyList<ReviewerIdentity> identities,
        IReviewerIdentityPolicy? policy)
    {
        var normalized = new List<ReviewerIdentity>(identities.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            var candidate = policy?.Normalize(identity) ?? identity;
            if (candidate is null)
            {
                continue;
            }

            var key = $"{candidate.Kind}\0{candidate.Value}";
            if (seen.Add(key))
            {
                normalized.Add(candidate);
            }
        }

        return normalized;
    }

    private sealed record RepositoryReviewerPolicy(
        string? Provider,
        IReadOnlyList<ReviewerIdentity> Identities)
    {
        public static RepositoryReviewerPolicy Empty { get; } = new(null, []);
    }

    private async Task<IReadOnlyList<Application.ManagedPullRequestSnapshot>>
        DiscoverManagedPullRequestsAsync(
            Application.IManagedPullRequestDiscovery discovery,
            CancellationToken cancellationToken)
    {
        try
        {
            return await discovery.ListActiveAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Assistance discovery is additive; a provider outage must not disrupt the
            // established run-backed Revival path. Avoid logging provider details because
            // transport exceptions can include credential-bearing request metadata.
            Log.AssistanceDiscoveryFailed(_logger);
            return [];
        }
    }

    private static Application.ReworkSignal CreateAssistanceObservation(
        Application.PrUnderTest pr,
        IReadOnlyList<Application.ReworkSignal> fetchedSignals,
        DateTimeOffset observedAt)
    {
        var fetched = fetchedSignals.FirstOrDefault(signal =>
            Application.PullRequestIdentityMatcher.Matches(
                pr.PullRequest,
                signal.PullRequest
            )
        );

        // Preserve compatibility with feedback fetchers that only populated the
        // provider PR ID before canonical references were introduced. Only use this
        // fallback when the ID identifies exactly one returned signal.
        if (fetched is null)
        {
            var matchingIds = fetchedSignals
                .Where(signal => signal.PullRequestId.Equals(
                    pr.PullRequestId,
                    StringComparison.OrdinalIgnoreCase
                ))
                .Take(2)
                .ToList();
            if (matchingIds.Count == 1)
            {
                fetched = matchingIds[0];
            }
        }

        return fetched is null
            ? new Application.ReworkSignal
            {
                RequestMode = ReworkRequestMode.Assistance,
                PullRequest = pr.PullRequest,
                OriginatingRunId = pr.OriginatingRunId,
                PullRequestId = pr.PullRequestId,
                Threads = [],
                FirstQualifyingCommentAt = observedAt,
                LastQualifyingCommentAt = observedAt,
            }
            : fetched with
            {
                RequestMode = ReworkRequestMode.Assistance,
                PullRequest = pr.PullRequest,
                OriginatingRunId = pr.OriginatingRunId,
                PullRequestId = pr.PullRequestId,
            };
    }

    private static Application.ReworkSignal NormalizeObservationTimestamp(
        Application.ReworkSignal signal,
        ReworkFeedback? currentRow,
        DateTimeOffset observedAt)
    {
        if (signal.RequestMode != ReworkRequestMode.Assistance)
        {
            return signal;
        }

        if (currentRow is null)
        {
            var first = signal.Threads.Count > 0
                ? signal.FirstQualifyingCommentAt
                : observedAt;
            var last = signal.Threads.Count > 0 && signal.LastQualifyingCommentAt > observedAt
                ? signal.LastQualifyingCommentAt
                : observedAt;

            return signal with
            {
                FirstQualifyingCommentAt = first,
                LastQualifyingCommentAt = last,
            };
        }

        var latest = signal.Threads.Count > 0
            && signal.LastQualifyingCommentAt > currentRow.LastQualifyingCommentAt
                ? signal.LastQualifyingCommentAt
                : currentRow.LastQualifyingCommentAt;

        return signal with
        {
            FirstQualifyingCommentAt = currentRow.FirstQualifyingCommentAt,
            LastQualifyingCommentAt = latest,
        };
    }

    private static string GetObservationKey(Application.ReworkSignal signal) =>
        GetObservationKey(signal.RequestMode, signal.PullRequest, signal.PullRequestId);

    private static string GetObservationKey(ReworkFeedback feedback) =>
        GetObservationKey(feedback.RequestMode, feedback.PullRequest, feedback.PullRequestId);

    private static string GetObservationKey(
        ReworkRequestMode requestMode,
        PullRequestReference pullRequest,
        string pullRequestId)
    {
        return requestMode == ReworkRequestMode.Assistance
            ? $"assistance|{pullRequest.CanonicalKey}"
            : $"revival|{pullRequestId}";
    }

    private static bool HasLabel(IEnumerable<string> labels, string marker) =>
        labels.Any(label => label.Equals(marker, StringComparison.OrdinalIgnoreCase));

    private static string FirstNonEmpty(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback ?? string.Empty : preferred;

    private static bool AssistanceMatchesRevivalFeedback(
        PullRequestReference assistancePullRequest,
        ReworkFeedback revivalFeedback)
    {
        if (Application.PullRequestIdentityMatcher.Matches(
            assistancePullRequest,
            revivalFeedback.PullRequest
        ))
        {
            return true;
        }

        // Pre-canonical Revival rows only retained the provider PR ID. Their original
        // grouping had the same limitation, so ID matching is the only available way
        // to enforce Assistance precedence for migrated observations.
        return assistancePullRequest.PullRequestId.Equals(
            revivalFeedback.PullRequestId,
            StringComparison.OrdinalIgnoreCase
        );
    }

    /// <summary>
    /// Check whether a run lifecycle state is terminal.
    /// Terminal states: Completed, Failed, Cancelled, CleanedUp.
    /// </summary>
    private static bool IsTerminalState(Domain.RunLifecycleState status)
    {
        return status.IsTerminal();
    }

    private static Application.ReworkFeedbackUpsertRequest CreateFeedbackUpsertRequest(
        Application.ReworkSignal signal,
        string feedbackBundleId,
        string feedbackBundleJson)
    {
        var correlationId = signal.RequestMode == ReworkRequestMode.Assistance
            ? ComputeMaterializationCorrelationId(signal.PullRequest, feedbackBundleId)
            : null;

        return new Application.ReworkFeedbackUpsertRequest
        {
            RequestMode = signal.RequestMode,
            PullRequest = signal.PullRequest,
            OriginatingRunId = signal.OriginatingRunId,
            FeedbackBundleId = feedbackBundleId,
            FeedbackBundleJson = feedbackBundleJson,
            ThreadCount = signal.Threads.Count,
            FirstQualifyingCommentAt = signal.FirstQualifyingCommentAt,
            LastQualifyingCommentAt = signal.LastQualifyingCommentAt,
            Status = ReworkFeedbackStatus.Watching,
            CorrelationId = correlationId,
        };
    }

    private static string ComputeMaterializationCorrelationId(
        PullRequestReference pullRequest,
        string feedbackBundleId)
    {
        var source = $"assistance|{pullRequest.CanonicalKey}|{feedbackBundleId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Compute a stable FeedbackBundleId as a SHA-256 hash of sorted thread ids.
    /// Sorting ensures the hash is deterministic regardless of thread ordering
    /// from the feedback source.
    /// </summary>
    private static string ComputeFeedbackBundleId(IReadOnlyList<ReviewThread> threads)
    {
        var sortedIds = string.Join(
            '|',
            threads
                .OrderBy(t => t.ThreadId, StringComparer.Ordinal)
                .Select(t => t.ThreadId)
        );

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
                + "No PR review comments will be polled."
        )]
        public static partial void WorkerDisabled(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Feedback polling worker started. PollInterval={PollInterval}s, "
                + "MaxConcurrency={MaxConcurrency}, SoakMinutes={SoakMinutes}, Provider='{Provider}'"
        )]
        public static partial void WorkerStarted(
            ILogger logger,
            int pollInterval,
            int maxConcurrency,
            int soakMinutes,
            string provider
        );

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Unhandled exception in feedback polling cycle. Worker will retry after delay."
        )]
        public static partial void PollCycleError(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Feedback polling worker stopped.")]
        public static partial void WorkerStopped(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Feedback poll cycle started (interval={Interval}s)."
        )]
        public static partial void PollCycleStarted(ILogger logger, int interval);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Feedback poll cycle completed.")]
        public static partial void PollCycleCompleted(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "No eligible runs found for feedback polling."
        )]
        public static partial void NoEligibleRuns(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Found {Count} eligible run(s) for feedback polling."
        )]
        public static partial void FoundEligibleRuns(ILogger logger, int count);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Found {Count} managed pull request(s) carrying the assistance marker."
        )]
        public static partial void AssistanceRequestsDiscovered(ILogger logger, int count);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Managed pull request discovery failed; continuing with run-backed Revival polling."
        )]
        public static partial void AssistanceDiscoveryFailed(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Skipping assistance request for PR {PullRequestId} ({PullRequestUrl}): canonical managed identity is incomplete."
        )]
        public static partial void AssistanceRequestMissingCanonicalIdentity(
            ILogger logger,
            string pullRequestId,
            string pullRequestUrl
        );

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "PR {PullRequestId}: Assistance takes precedence; suppressing run-backed Revival polling."
        )]
        public static partial void RevivalSuppressedByAssistance(
            ILogger logger,
            string pullRequestId
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "PR {PullRequestId}: superseded outstanding Revival soak because Assistance takes precedence."
        )]
        public static partial void RevivalWatchingSuppressedByAssistance(
            ILogger logger,
            string pullRequestId
        );

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "PR {PullRequestId}: Assistance observation already advanced to {Status}; soak was not restarted."
        )]
        public static partial void AssistanceObservationAlreadyAdvanced(
            ILogger logger,
            string pullRequestId,
            ReworkFeedbackStatus status
        );

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "PR {PullRequestId}: Revival observation already advanced to {Status}; the existing soak is preserved and the bundle will not be re-materialized."
        )]
        public static partial void RevivalObservationAlreadyAdvanced(
            ILogger logger,
            string pullRequestId,
            ReworkFeedbackStatus status
        );

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "{Count} work item(s) have active rework in progress (non-terminal new runs)."
        )]
        public static partial void WorkItemsWithActiveRework(ILogger logger, int count);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "All eligible runs blocked by active rework on their work items."
        )]
        public static partial void AllRunsBlockedByActiveRework(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Skipping Revival run {RunId} for pull request {PullRequestId}: it did not resolve to exactly one managed repository profile."
        )]
        public static partial void RevivalPullRequestResolutionMissing(
            ILogger logger,
            string runId,
            string pullRequestId
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Skipping Revival run {RunId} for pull request {PullRequestId}: it matched {CandidateCount} managed repository profiles and is ambiguous."
        )]
        public static partial void RevivalPullRequestResolutionAmbiguous(
            ILogger logger,
            string runId,
            string pullRequestId,
            int candidateCount
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "No valid PR URLs among {Count} eligible run(s) — cannot build PrUnderTest list."
        )]
        public static partial void NoValidPrUrls(ILogger logger, int count);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Built PrUnderTest list with {Count} PR(s)."
        )]
        public static partial void PrUnderTestBuilt(ILogger logger, int count);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Feedback source returned {Count} rework signal(s)."
        )]
        public static partial void SignalsReceived(ILogger logger, int count);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Filter pipeline: {FilteredCount} signal(s) survived (of {OriginalCount} raw signal(s))."
        )]
        public static partial void SignalsAfterFilter(
            ILogger logger,
            int filteredCount,
            int originalCount
        );

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "No rework signals survived the filter pipeline."
        )]
        public static partial void NoSignalsAfterFilter(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "PR {PullRequestId}: bundle unchanged, bumped LastQualifyingCommentAt [bundle={BundleId}]."
        )]
        public static partial void BundleUnchangedBumpedLastComment(
            ILogger logger,
            string pullRequestId,
            string bundleId
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "PR {PullRequestId}: bundle changed, superseded old bundle [{OldBundleId}] with new [{NewBundleId}]."
        )]
        public static partial void BundleChangedSuperseded(
            ILogger logger,
            string pullRequestId,
            string oldBundleId,
            string newBundleId
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "PR {PullRequestId}: new feedback bundle, started Watching [{BundleId}]."
        )]
        public static partial void NewWatchingRow(
            ILogger logger,
            string pullRequestId,
            string bundleId
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "PR {PullRequestId}: feedback soaked [{BundleId}] — {ThreadCount} thread(s) eligible for materialization."
        )]
        public static partial void FeedbackSoaked(
            ILogger logger,
            string pullRequestId,
            string bundleId,
            int threadCount
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Materialized assistance story {StoryId} and Pending cycle #{CycleNumber} for PR {PullRequestId} with {ThreadCount} thread(s)."
        )]
        public static partial void AssistanceStoryMaterialized(
            ILogger logger,
            string pullRequestId,
            string storyId,
            int cycleNumber,
            int threadCount
        );

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Skipping {RequestMode} feedback for PR {PullRequestId} in the revival materialization path."
        )]
        public static partial void NonRevivalFeedbackSkipped(
            ILogger logger,
            ReworkRequestMode requestMode,
            string pullRequestId
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Cannot materialize rework cycle: prior run {RunId} not found for PR {PullRequestId}."
        )]
        public static partial void MissingPriorRun(
            ILogger logger,
            string runId,
            string pullRequestId
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Cannot materialize rework cycle: prior run {RunId} has no WorkItemId for PR {PullRequestId}."
        )]
        public static partial void MissingWorkItemId(
            ILogger logger,
            string runId,
            string pullRequestId
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Cannot materialize rework cycle: prior run {RunId} missing BranchName/PullRequestUrl/CommitSha for PR {PullRequestId}."
        )]
        public static partial void IncompletePriorRun(
            ILogger logger,
            string runId,
            string pullRequestId
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Materialized ReworkCycle #{CycleNumber} for work item {WorkItemId} "
                + "[bundle={BundleId}] — {ThreadCount} thread(s)."
        )]
        public static partial void ReworkCycleMaterialized(
            ILogger logger,
            string workItemId,
            int cycleNumber,
            string bundleId,
            int threadCount
        );

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "ReworkCycle for bundle [{BundleId}] already materialized (unique constraint)."
        )]
        public static partial void ReworkCycleAlreadyMaterialized(ILogger logger, string bundleId);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Cannot reactivate work item {WorkItemId} for rework cycle {CycleId}: work item not found in store."
        )]
        public static partial void MissingWorkItemForRework(
            ILogger logger,
            string workItemId,
            string cycleId
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "[rework] Reactivated work item {WorkItemId} for cycle #{CycleNumber} (cycleId={CycleId})."
        )]
        public static partial void ReworkItemReactivated(
            ILogger logger,
            string workItemId,
            int cycleNumber,
            string cycleId
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "[rework] Failed to reactivate work item {WorkItemId} for cycle #{CycleNumber} (cycleId={CycleId}): {FailureReason}."
        )]
        public static partial void ReworkItemReactivationFailed(
            ILogger logger,
            string workItemId,
            int cycleNumber,
            string cycleId,
            string failureReason
        );

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "[rework] Error reactivating work item {WorkItemId} for cycle #{CycleNumber} (cycleId={CycleId})."
        )]
        public static partial void ReworkItemReactivationError(
            ILogger logger,
            string workItemId,
            int cycleNumber,
            string cycleId,
            Exception ex
        );
    }
}
