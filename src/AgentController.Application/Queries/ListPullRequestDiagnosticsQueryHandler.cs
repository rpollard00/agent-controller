using AgentController.Application.Abstractions;

namespace AgentController.Application.Queries;

public sealed class ListPullRequestDiagnosticsQueryHandler(
    IManagedPullRequestDiagnosticDiscovery discovery,
    PullRequestSourceOptionsProvider sourceOptionsProvider,
    PullRequestDiagnosticOptions options,
    PullRequestPickupEvaluator? evaluator = null)
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

        var items = evaluator is null
            ? page.Items.Select(snapshot => ToSummary(snapshot, options)).ToArray()
            : (await evaluator.EvaluatePageAsync(page.Items, cancellationToken))
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
        };

    private static PullRequestDiagnosticSummary ToSummary(
        ManagedPullRequestSnapshot snapshot,
        PullRequestDiagnosticOptions options) => new()
        {
            PullRequestId = snapshot.PullRequestId,
            Title = snapshot.Title,
            Url = Clean(snapshot.PullRequestUrl),
            SourceControlEnvironmentKey = snapshot.EnvironmentKey,
            RepositoryKey = snapshot.RepositoryKey,
            Status = snapshot.Status,
            Request = PullRequestPickupEvaluator.ClassifyRequest(snapshot.Labels, options),
        };

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
