using AgentController.Application.Abstractions;

namespace AgentController.Application.Queries;

public sealed class ListPullRequestDiagnosticsQueryHandler(
    IManagedPullRequestDiagnosticDiscovery discovery,
    PullRequestSourceOptionsProvider sourceOptionsProvider,
    PullRequestDiagnosticOptions options)
    : IQueryHandler<ListPullRequestDiagnosticsQuery, PullRequestDiagnosticsPage>
{
    public async Task<PullRequestDiagnosticsPage> ExecuteAsync(
        ListPullRequestDiagnosticsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = await discovery.ListAsync(new ManagedPullRequestDiscoveryQuery
        {
            SourceControlEnvironmentKey = query.SourceControlEnvironmentKey,
            IncludeInactive = query.IncludeInactive,
            Page = query.Page,
            PageSize = query.PageSize,
        }, cancellationToken);

        return new PullRequestDiagnosticsPage
        {
            Sources = await sourceOptionsProvider.ListAsync(cancellationToken),
            Items = page.Items.Select(snapshot => new PullRequestDiagnosticSummary
            {
                PullRequestId = snapshot.PullRequestId,
                Title = snapshot.Title,
                Url = Clean(snapshot.PullRequestUrl),
                SourceControlEnvironmentKey = snapshot.EnvironmentKey,
                RepositoryKey = snapshot.RepositoryKey,
                Status = snapshot.Status,
                Request = PullRequestPickupEvaluator.ClassifyRequest(snapshot.Labels, options),
            }).ToArray(),
            Failures = page.Failures,
            Page = page.Page,
            PageSize = page.PageSize,
            Total = page.Total,
        };
    }

    private static string? Clean(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
