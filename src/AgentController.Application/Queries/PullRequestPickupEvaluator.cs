using AgentController.Domain;

namespace AgentController.Application.Queries;

internal sealed class PullRequestPickupEvaluator(
    IRepositoryStore repositoryStore,
    IAgentRunStore runStore,
    IReworkCycleStore cycleStore,
    IReworkFeedbackStore feedbackStore,
    IFeedbackSource feedbackSource,
    ReviewFeedbackFilterPipeline feedbackPipeline,
    PullRequestDiagnosticOptions options)
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
        cancellationToken.ThrowIfCancellationRequested();
        var request = ClassifyRequest(snapshot.Labels, options);
        var assistance = request is PullRequestRequestMatch.Assistance or PullRequestRequestMatch.Both;
        var requested = request != PullRequestRequestMatch.None;
        var mode = assistance ? ReworkRequestMode.Assistance : ReworkRequestMode.Revival;
        var active = snapshot.Status.Equals("active", StringComparison.OrdinalIgnoreCase);

        var repository = await repositoryStore.GetByKeyAsync(snapshot.RepositoryKey, cancellationToken);
        var managedRepository = repository is not null
            && repository.RepositoryHostConnectionKey?.Equals(
                snapshot.EnvironmentKey, StringComparison.OrdinalIgnoreCase) == true;
        var metadataPresent = snapshot.PullRequest.HasCanonicalIdentity
            && !string.IsNullOrWhiteSpace(snapshot.PullRequestUrl);

        var runs = await runStore.FindRunsForFeedbackAsync(cancellationToken);
        var originatingRun = runs.FirstOrDefault(run =>
            PullRequestIdentityMatcher.Matches(snapshot.PullRequest, run.PullRequestUrl));
        var hasEligibleLineage = assistance || originatingRun is not null;
        var blocked = originatingRun is not null
            && await HasActiveReworkAsync(originatingRun.WorkItemId, cancellationToken);

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
            ReviewerIdentities = repository?.ReviewerIdentities ?? [],
        };
        var feedbackQuery = new FeedbackQuery
        {
            OpenPrs = [pr],
            ReworkMarkerTag = assistance ? options.AssistanceLabel : options.RevivalLabel,
        };
        var fetched = await feedbackSource.PollAsync(feedbackQuery, cancellationToken);
        var signal = fetched.FirstOrDefault(candidate =>
                candidate.PullRequestId.Equals(snapshot.PullRequestId, StringComparison.OrdinalIgnoreCase))
            ?? new ReworkSignal
            {
                RequestMode = mode,
                PullRequest = snapshot.PullRequest,
                PullRequestId = snapshot.PullRequestId,
                OriginatingRunId = originatingRun?.RunId,
                Threads = [],
            };
        var trace = AssertSingleTrace(assistance
            ? await feedbackPipeline.TraceAssistanceAsync(feedbackQuery, [signal], cancellationToken)
            : await feedbackPipeline.TraceAsync(feedbackQuery, [signal], cancellationToken));

        var markerPresent = assistance
            ? requested
            : request == PullRequestRequestMatch.Revival
                && trace.MarkerStatus == FeedbackMarkerCheckStatus.Present;
        var reviewerConfigured = pr.ReviewerIdentities.Count > 0;
        var feedbackQualifies = assistance || trace.QualifyingThreadCount > 0;
        var checks = new[]
        {
            Check("active-status", "Active pull request", active,
                active ? "The pull request is active." : $"Status '{snapshot.Status}' is inactive."),
            Check("managed-repository", "Managed repository", managedRepository,
                managedRepository ? $"Repository '{snapshot.RepositoryKey}' is managed by this environment." : "The repository is not managed by this source-control environment."),
            Check("required-metadata", "Required pull request metadata", metadataPresent,
                metadataPresent ? "Canonical identity and URL are available." : "Canonical identity or URL is missing."),
            Check("request-marker", "Configured request label", markerPresent,
                markerPresent ? $"Found '{(assistance ? options.AssistanceLabel : options.RevivalLabel)}'." : "The configured request label was not found with production matching semantics."),
            Check("assistance-precedence", "Assistance precedence", true,
                request == PullRequestRequestMatch.Both ? "Both labels are present; Assistance takes precedence." : assistance ? "Assistance is the selected request mode." : "Assistance does not override Revival."),
            Check("originating-lineage", "Eligible originating run", hasEligibleLineage,
                assistance ? "Assistance does not require a controller-authored pull request." : hasEligibleLineage ? $"Matched eligible run '{originatingRun!.RunId}'." : "Revival requires an eligible controller run that produced this pull request."),
            Check("active-rework", "No active rework", assistance || !blocked,
                assistance ? "Assistance pickup is independent of Revival run blocking." : blocked ? "The originating work item already has active rework." : "No active rework blocks the originating work item."),
            Check("reviewer-configuration", "Reviewer configuration", assistance || reviewerConfigured,
                reviewerConfigured ? "At least one reviewer is configured." : assistance ? "No reviewer is configured; zero-comment Assistance remains valid." : "Revival fails closed because no reviewer is configured."),
            Check("qualifying-feedback", "Qualifying feedback", feedbackQualifies,
                assistance ? $"Assistance remains eligible with {trace.QualifyingThreadCount} qualifying feedback thread(s)." : feedbackQualifies ? $"Found {trace.QualifyingThreadCount} qualifying feedback thread(s)." : "Revival requires at least one qualifying feedback thread."),
        };
        var eligible = requested && checks.All(check => check.Passed);
        var tracking = await FindTrackingAsync(snapshot.PullRequest, mode, cancellationToken);

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
            Tracking = tracking,
        };
    }

    private async Task<bool> HasActiveReworkAsync(string? workItemId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workItemId)) return false;
        foreach (var cycle in await cycleStore.ListConsumedAsync(cancellationToken))
        {
            if (!cycle.WorkItemId.Equals(workItemId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(cycle.NewRunId)) continue;
            var run = await runStore.GetByIdAsync(cycle.NewRunId, cancellationToken);
            if (run is not null && !run.Status.IsTerminal()) return true;
        }
        return false;
    }

    private async Task<PullRequestTrackingState?> FindTrackingAsync(
        PullRequestReference pullRequest,
        ReworkRequestMode mode,
        CancellationToken cancellationToken)
    {
        var feedback = (await feedbackStore.GetTrackedAsync(cancellationToken))
            .Where(item => item.RequestMode == mode
                && PullRequestIdentityMatcher.Matches(item.PullRequest, pullRequest))
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault();
        if (feedback is null) return null;
        var cycles = (await cycleStore.ListPendingAsync(cancellationToken))
            .Concat(await cycleStore.ListConsumedAsync(cancellationToken));
        var cycle = cycles.FirstOrDefault(item =>
            item.RequestMode == mode
            && PullRequestIdentityMatcher.Matches(item.PullRequest, pullRequest));
        return new PullRequestTrackingState
        {
            RequestMode = mode,
            FeedbackStatus = feedback.Status,
            CycleStatus = cycle?.Status,
        };
    }

    private static ReviewFeedbackCheckTrace AssertSingleTrace(IReadOnlyList<ReviewFeedbackCheckTrace> traces) =>
        traces.Count == 1 ? traces[0] : throw new InvalidOperationException("Feedback diagnostics did not return exactly one trace.");

    private static PullRequestDiagnosticCheck Check(string code, string label, bool passed, string reason) =>
        new() { Code = code, Label = label, Passed = passed, Reason = reason };
}
