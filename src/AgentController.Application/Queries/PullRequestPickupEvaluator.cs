using AgentController.Application.Abstractions;
using AgentController.Domain;

namespace AgentController.Application.Queries;

public sealed class PullRequestPickupEvaluator(
    IRepositoryStore repositoryStore,
    IAgentRunStore runStore,
    IReworkCycleStore cycleStore,
    IReworkFeedbackStore feedbackStore,
    IFeedbackSource feedbackSource,
    ReviewFeedbackFilterPipeline feedbackPipeline,
    PullRequestDiagnosticOptions options,
    IConnectionStore? connectionStore = null,
    IReviewerIdentityPolicyResolver? reviewerIdentityPolicyResolver = null)
{
    public static PullRequestRequestMatch ClassifyRequest(
        IEnumerable<string> labels,
        PullRequestDiagnosticOptions options)
    {
        var revival = labels.Any(label => label.Equals(options.RevivalLabel, StringComparison.Ordinal));
        var assistance = labels.Any(label => label.Equals(
            options.AssistanceLabel,
            StringComparison.OrdinalIgnoreCase));
        return (revival, assistance) switch
        {
            (true, true) => PullRequestRequestMatch.Both,
            (true, false) => PullRequestRequestMatch.Revival,
            (false, true) => PullRequestRequestMatch.Assistance,
            _ => PullRequestRequestMatch.None,
        };
    }

    public async Task<PullRequestDiagnosticDetail> EvaluateAsync(
        ManagedPullRequestSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var evaluated = await EvaluatePageAsync([snapshot], cancellationToken);
        return evaluated.Count == 1
            ? evaluated[0]
            : throw new InvalidOperationException("Pull-request diagnostics did not evaluate the requested snapshot.");
    }

    /// <summary>
    /// Evaluates a discovered page using one shared set of store lookups and one
    /// feedback-provider poll per request mode. The snapshots are already supplied
    /// by diagnostic discovery; this method never performs discovery for individual
    /// rows.
    /// </summary>
    public Task<IReadOnlyList<PullRequestDiagnosticDetail>> EvaluateAsync(
        IReadOnlyList<ManagedPullRequestSnapshot> snapshots,
        CancellationToken cancellationToken) =>
        EvaluatePageAsync(snapshots, cancellationToken);

    public async Task<IReadOnlyList<PullRequestDiagnosticDetail>> EvaluatePageAsync(
        IReadOnlyList<ManagedPullRequestSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();

        var repositories = await LoadRepositoriesAsync(snapshots, cancellationToken);
        var reviewerPolicies = new Dictionary<string, RepositoryReviewerPolicy>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in repositories)
        {
            reviewerPolicies[pair.Key] = await ResolveReviewerPolicyAsync(
                pair.Value,
                cancellationToken);
        }

        // These collections are common to every row in the discovered page.
        var runs = await runStore.FindRunsForFeedbackAsync(cancellationToken);
        var trackedFeedback = await feedbackStore.GetTrackedAsync(cancellationToken);
        var pendingCycles = await cycleStore.ListPendingAsync(cancellationToken);
        var consumedCycles = await cycleStore.ListConsumedAsync(cancellationToken);
        var activeReworkRuns = new Dictionary<string, AgentRunHandle?>(StringComparer.Ordinal);
        var contexts = new List<EvaluationContext>(snapshots.Count);

        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = ClassifyRequest(snapshot.Labels, options);
            var assistance = request is PullRequestRequestMatch.Assistance
                or PullRequestRequestMatch.Both;
            var mode = assistance ? ReworkRequestMode.Assistance : ReworkRequestMode.Revival;
            var repository = repositories[snapshot.RepositoryKey];
            var reviewerPolicy = reviewerPolicies[snapshot.RepositoryKey];
            var originatingRun = runs.FirstOrDefault(run =>
                PullRequestIdentityMatcher.Matches(snapshot.PullRequest, run.PullRequestUrl));
            var blocked = originatingRun is not null
                && await HasActiveReworkAsync(
                    originatingRun.WorkItemId,
                    consumedCycles,
                    activeReworkRuns,
                    cancellationToken);

            var pr = new PrUnderTest
            {
                RequestMode = mode,
                PullRequest = snapshot.PullRequest,
                OriginatingRunId = originatingRun?.RunId,
                WorkItemId = originatingRun?.WorkItemId,
                RepoKey = snapshot.RepositoryKey,
                PullRequestUrl = snapshot.PullRequestUrl,
                PullRequestId = snapshot.PullRequestId,
                BranchName = snapshot.SourceBranch,
                ReviewerIdentityProvider = reviewerPolicy.Provider,
                ReviewerIdentities = reviewerPolicy.Identities,
            };

            contexts.Add(new EvaluationContext
            {
                Snapshot = snapshot,
                Request = request,
                Assistance = assistance,
                Mode = mode,
                Repository = repository,
                OriginatingRun = originatingRun,
                Blocked = blocked,
                PullRequest = pr,
            });
        }

        var traces = new Dictionary<EvaluationContext, ReviewFeedbackCheckTrace>();
        foreach (var group in contexts.GroupBy(context => context.Assistance))
        {
            var grouped = group.ToArray();
            var feedbackQuery = new FeedbackQuery
            {
                OpenPrs = grouped.Select(context => context.PullRequest).ToArray(),
                ReworkMarkerTag = grouped[0].Assistance
                    ? options.AssistanceLabel
                    : options.RevivalLabel,
            };
            var fetched = await feedbackSource.PollAsync(feedbackQuery, cancellationToken);
            var signals = grouped
                .Select(context => FindSignal(context.Snapshot, fetched) ?? new ReworkSignal
                {
                    RequestMode = context.Mode,
                    PullRequest = context.Snapshot.PullRequest,
                    PullRequestId = context.Snapshot.PullRequestId,
                    OriginatingRunId = context.OriginatingRun?.RunId,
                    Threads = [],
                })
                .ToArray();
            var groupTraces = grouped[0].Assistance
                ? await feedbackPipeline.TraceAssistanceAsync(
                    feedbackQuery,
                    signals,
                    cancellationToken)
                : await feedbackPipeline.TraceAsync(
                    feedbackQuery,
                    signals,
                    cancellationToken);

            if (groupTraces.Count != grouped.Length)
            {
                throw new InvalidOperationException(
                    "Feedback diagnostics did not return exactly one trace per pull request.");
            }

            for (var index = 0; index < grouped.Length; index++)
            {
                traces[grouped[index]] = groupTraces[index];
            }
        }

        return contexts
            .Select(context => CreateDetail(
                context,
                traces[context],
                trackedFeedback,
                pendingCycles,
                consumedCycles))
            .ToArray();
    }

    private async Task<IReadOnlyDictionary<string, RepositoryProfile?>> LoadRepositoriesAsync(
        IReadOnlyList<ManagedPullRequestSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var repositories = new Dictionary<string, RepositoryProfile?>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var key in snapshots.Select(snapshot => snapshot.RepositoryKey).Distinct(
                     StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            repositories[key] = await repositoryStore.GetByKeyAsync(key, cancellationToken);
        }

        return repositories;
    }

    private PullRequestDiagnosticDetail CreateDetail(
        EvaluationContext context,
        ReviewFeedbackCheckTrace trace,
        IReadOnlyList<ReworkFeedback> trackedFeedback,
        IReadOnlyList<ReworkCycle> pendingCycles,
        IReadOnlyList<ReworkCycle> consumedCycles)
    {
        var snapshot = context.Snapshot;
        var request = context.Request;
        var assistance = context.Assistance;
        var requested = request != PullRequestRequestMatch.None;
        var active = snapshot.Status.Equals("active", StringComparison.OrdinalIgnoreCase);
        var managedRepository = context.Repository is not null
            && context.Repository.RepositoryHostConnectionKey?.Equals(
                snapshot.EnvironmentKey, StringComparison.OrdinalIgnoreCase) == true;
        var metadataPresent = snapshot.PullRequest.HasCanonicalIdentity
            && !string.IsNullOrWhiteSpace(snapshot.PullRequestUrl);
        var hasEligibleLineage = assistance || context.OriginatingRun is not null;
        var feedbackQualifies = assistance || trace.QualifyingThreadCount > 0;
        var reviewerConfigured = context.PullRequest.ReviewerIdentities.Count > 0;
        var checks = new[]
        {
            Check("active-status", "Active pull request", active,
                active ? "The pull request is active." : $"Status '{snapshot.Status}' is inactive."),
            Check("managed-repository", "Managed repository", managedRepository,
                managedRepository ? $"Repository '{snapshot.RepositoryKey}' is managed by this environment." : "The repository is not managed by this source-control environment."),
            Check("required-metadata", "Required pull request metadata", metadataPresent,
                metadataPresent ? "Canonical identity and URL are available." : "Canonical identity or URL is missing."),
            Check("request-marker", "Configured request label", assistance
                ? requested
                : request == PullRequestRequestMatch.Revival
                    && trace.MarkerStatus == FeedbackMarkerCheckStatus.Present,
                assistance || request == PullRequestRequestMatch.Revival
                    && trace.MarkerStatus == FeedbackMarkerCheckStatus.Present
                    ? $"Found '{(assistance ? options.AssistanceLabel : options.RevivalLabel)}'."
                    : "The configured request label was not found with production matching semantics."),
            Check("assistance-precedence", "Assistance precedence", true,
                request == PullRequestRequestMatch.Both ? "Both labels are present; Assistance takes precedence." : assistance ? "Assistance is the selected request mode." : "Assistance does not override Revival."),
            Check("originating-lineage", "Eligible originating run", hasEligibleLineage,
                assistance ? "Assistance does not require a controller-authored pull request." : hasEligibleLineage ? $"Matched eligible run '{context.OriginatingRun!.RunId}'." : "Revival requires an eligible controller run that produced this pull request."),
            Check("active-rework", "No active rework", assistance || !context.Blocked,
                assistance ? "Assistance pickup is independent of Revival run blocking." : context.Blocked ? "The originating work item already has active rework." : "No active rework blocks the originating work item."),
            Check("reviewer-configuration", "Reviewer configuration", assistance || reviewerConfigured,
                reviewerConfigured ? "At least one reviewer is configured." : assistance ? "No reviewer is configured; zero-comment Assistance remains valid." : "Revival fails closed because no reviewer is configured."),
            Check("qualifying-feedback", "Qualifying feedback", feedbackQualifies,
                assistance ? $"Assistance remains eligible with {trace.QualifyingThreadCount} qualifying feedback thread(s)." : feedbackQualifies ? $"Found {trace.QualifyingThreadCount} qualifying feedback thread(s)." : "Revival requires at least one qualifying feedback thread."),
        };
        var eligible = requested && checks.All(check => check.Passed);

        return new PullRequestDiagnosticDetail
        {
            PullRequestId = snapshot.PullRequestId,
            Title = snapshot.Title,
            Url = string.IsNullOrWhiteSpace(snapshot.PullRequestUrl) ? null : snapshot.PullRequestUrl,
            SourceControlEnvironmentKey = snapshot.EnvironmentKey,
            RepositoryKey = snapshot.RepositoryKey,
            Status = snapshot.Status,
            SourceBranch = snapshot.SourceBranch,
            TargetBranch = snapshot.TargetBranch,
            Labels = snapshot.Labels.ToArray(),
            LinkedWorkItems = snapshot.LinkedWorkItems.ToArray(),
            Request = request,
            Outcome = eligible
                ? request == PullRequestRequestMatch.Both
                    ? PullRequestDiagnosticOutcome.AssistanceTakesPrecedence
                    : PullRequestDiagnosticOutcome.Eligible
                : PullRequestDiagnosticOutcome.NotEligible,
            Eligible = eligible,
            Checks = checks,
            RecognizedRevivalLabel = options.RevivalLabel,
            RecognizedAssistanceLabel = options.AssistanceLabel,
            FeedbackTrace = trace,
            Tracking = FindTracking(
                snapshot.PullRequest,
                context.Mode,
                trackedFeedback,
                pendingCycles,
                consumedCycles),
        };
    }

    private async Task<RepositoryReviewerPolicy> ResolveReviewerPolicyAsync(
        RepositoryProfile? repository,
        CancellationToken cancellationToken)
    {
        if (repository is null)
        {
            return RepositoryReviewerPolicy.Empty;
        }

        var connectionKey = repository.RepositoryHostConnectionKey?.Trim();
        ConnectionProfile? connection = null;
        if (connectionStore is not null && !string.IsNullOrWhiteSpace(connectionKey))
        {
            connection = await connectionStore.GetByKeyAsync(connectionKey, cancellationToken);
        }

        var provider = connection?.Provider?.Trim();
        var hasConnectionStore = connectionStore is not null;

        // A diagnostic query normally has a connection store available. If a caller
        // supplies only the application-layer collaborators (for example, a focused
        // unit test), retain the profile identities rather than guessing that a
        // connection key is a provider name. A supplied policy can still identify an
        // unambiguous provider key.
        if (!hasConnectionStore
            && reviewerIdentityPolicyResolver is not null
            && provider is null
            && !string.IsNullOrWhiteSpace(connectionKey)
            && reviewerIdentityPolicyResolver.Resolve(connectionKey) is not null)
        {
            provider = connectionKey;
        }

        if (hasConnectionStore && connection is null)
        {
            return new RepositoryReviewerPolicy(provider, []);
        }

        var policy = provider is null || reviewerIdentityPolicyResolver is null
            ? null
            : reviewerIdentityPolicyResolver.Resolve(provider);
        if (hasConnectionStore && policy is null)
        {
            // Repository CRUD rejects reviewer identities for unsupported providers.
            // Treat any stale or externally-created data the same way here: fail closed.
            return new RepositoryReviewerPolicy(provider, []);
        }

        return new RepositoryReviewerPolicy(
            provider,
            NormalizeReviewerIdentities(repository.ReviewerIdentities, policy));
    }

    private static IReadOnlyList<ReviewerIdentity> NormalizeReviewerIdentities(
        IReadOnlyList<ReviewerIdentity> identities,
        IReviewerIdentityPolicy? policy)
    {
        if (policy is null)
        {
            return identities.ToArray();
        }

        var normalized = new List<ReviewerIdentity>(identities.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            var result = policy.ValidateAndNormalize(identity);
            if (!result.IsValid || result.NormalizedIdentity is null)
            {
                continue;
            }

            var candidate = result.NormalizedIdentity;
            if (seen.Add($"{candidate.Kind}\0{candidate.Value}"))
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

    private async Task<bool> HasActiveReworkAsync(
        string? workItemId,
        IReadOnlyList<ReworkCycle> consumedCycles,
        Dictionary<string, AgentRunHandle?> activeReworkRuns,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workItemId)) return false;
        foreach (var cycle in consumedCycles)
        {
            if (!cycle.WorkItemId.Equals(workItemId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(cycle.NewRunId)) continue;

            if (!activeReworkRuns.TryGetValue(cycle.NewRunId, out var run))
            {
                run = await runStore.GetByIdAsync(cycle.NewRunId, cancellationToken);
                activeReworkRuns[cycle.NewRunId] = run;
            }

            if (run is not null && !run.Status.IsTerminal()) return true;
        }
        return false;
    }

    private static PullRequestDiagnosticCheck Check(string code, string label, bool passed, string reason) =>
        new() { Code = code, Label = label, Passed = passed, Reason = reason };

    private static ReworkSignal? FindSignal(
        ManagedPullRequestSnapshot snapshot,
        IReadOnlyList<ReworkSignal> fetched)
    {
        return fetched.FirstOrDefault(candidate =>
                   PullRequestIdentityMatcher.Matches(
                       snapshot.PullRequest,
                       candidate.PullRequest))
            ?? fetched.FirstOrDefault(candidate =>
                candidate.PullRequestId.Equals(
                    snapshot.PullRequestId,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static PullRequestTrackingState? FindTracking(
        PullRequestReference pullRequest,
        ReworkRequestMode mode,
        IReadOnlyList<ReworkFeedback> trackedFeedback,
        IReadOnlyList<ReworkCycle> pendingCycles,
        IReadOnlyList<ReworkCycle> consumedCycles)
    {
        var feedback = trackedFeedback
            .Where(item => item.RequestMode == mode
                && PullRequestIdentityMatcher.Matches(item.PullRequest, pullRequest))
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault();
        if (feedback is null) return null;

        var cycle = pendingCycles
            .Concat(consumedCycles)
            .FirstOrDefault(item =>
                item.RequestMode == mode
                && PullRequestIdentityMatcher.Matches(item.PullRequest, pullRequest));
        return new PullRequestTrackingState
        {
            RequestMode = mode,
            FeedbackStatus = feedback.Status,
            CycleStatus = cycle?.Status,
        };
    }

    private sealed class EvaluationContext
    {
        public ManagedPullRequestSnapshot Snapshot { get; init; } = new();
        public PullRequestRequestMatch Request { get; init; }
        public bool Assistance { get; init; }
        public ReworkRequestMode Mode { get; init; }
        public RepositoryProfile? Repository { get; init; }
        public AgentRunHandle? OriginatingRun { get; init; }
        public bool Blocked { get; init; }
        public PrUnderTest PullRequest { get; init; } = new();
    }
}
