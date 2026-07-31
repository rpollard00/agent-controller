using AgentController.Application.Abstractions;

namespace AgentController.Application.Queries;

public sealed class GetPullRequestDiagnosticsQueryHandler(
    IManagedPullRequestDiagnosticDiscovery discovery,
    IRepositoryStore repositoryStore,
    IAgentRunStore runStore,
    IReworkCycleStore cycleStore,
    IReworkFeedbackStore feedbackStore,
    IFeedbackSource feedbackSource,
    ReviewFeedbackFilterPipeline feedbackPipeline,
    PullRequestDiagnosticOptions options,
    IConnectionStore? connectionStore = null,
    IReviewerIdentityPolicyResolver? reviewerIdentityPolicyResolver = null)
    : IQueryHandler<GetPullRequestDiagnosticsQuery, PullRequestDiagnosticDetail?>
{
    public async Task<PullRequestDiagnosticDetail?> ExecuteAsync(
        GetPullRequestDiagnosticsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageNumber = 1;
        ManagedPullRequestSnapshot? snapshot = null;

        do
        {
            var page = await discovery.ListAsync(new ManagedPullRequestDiscoveryQuery
            {
                SourceControlEnvironmentKey = query.SourceControlEnvironmentKey,
                IncludeInactive = true,
                Page = pageNumber,
                PageSize = ManagedPullRequestDiscoveryQuery.MaximumPageSize,
            }, cancellationToken);
            snapshot = page.Items.FirstOrDefault(candidate =>
                candidate.EnvironmentKey.Equals(query.SourceControlEnvironmentKey, StringComparison.OrdinalIgnoreCase)
                && candidate.RepositoryKey.Equals(query.RepositoryKey, StringComparison.OrdinalIgnoreCase)
                && candidate.PullRequestId.Equals(query.PullRequestId, StringComparison.OrdinalIgnoreCase));
            if (snapshot is not null || pageNumber * page.PageSize >= page.Total) break;
            pageNumber++;
        } while (true);

        if (snapshot is null) return null;

        var evaluator = new PullRequestPickupEvaluator(
            repositoryStore,
            runStore,
            cycleStore,
            feedbackStore,
            feedbackSource,
            feedbackPipeline,
            options,
            connectionStore,
            reviewerIdentityPolicyResolver);
        return await evaluator.EvaluateAsync(snapshot, cancellationToken);
    }
}
