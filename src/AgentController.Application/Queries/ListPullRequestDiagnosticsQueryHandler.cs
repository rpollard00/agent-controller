using AgentController.Application.Abstractions;

namespace AgentController.Application.Queries;

public sealed class ListPullRequestDiagnosticsQueryHandler(
    IManagedPullRequestDiagnosticDiscovery discovery,
    PullRequestSourceOptionsProvider sourceOptionsProvider,
    PullRequestPickupEvaluator evaluator)
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

        var items = (await evaluator.EvaluatePageAsync(page.Items, cancellationToken))
            .Select(ToSummary)
            .ToArray();

        return new PullRequestDiagnosticsPage
        {
            Sources = await sourceOptionsProvider.ListAsync(cancellationToken),
            Items = items,
            Failures = page.Failures,
            Page = page.Page,
            PageSize = page.PageSize,
            Total = page.Total,
        };
    }

    private static PullRequestDiagnosticSummary ToSummary(
        PullRequestDiagnosticDetail detail) => new()
        {
            PullRequestId = detail.PullRequestId,
            Title = detail.Title,
            Url = Clean(detail.Url),
            SourceControlEnvironmentKey = detail.SourceControlEnvironmentKey,
            RepositoryKey = detail.RepositoryKey,
            Status = detail.Status,
            Request = detail.Request,
            Eligible = detail.Eligible,
        };

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
