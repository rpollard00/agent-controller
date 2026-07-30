using AgentController.Application.Abstractions;
using AgentController.Domain;

namespace AgentController.Application.Queries;

public sealed class ListPullRequestDiagnosticsQueryHandler(
    IManagedPullRequestDiagnosticDiscovery discovery,
    IRepositoryStore repositoryStore,
    IConnectionStore connectionStore,
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
            Sources = await ListSourcesAsync(cancellationToken),
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

    private async Task<IReadOnlyList<PullRequestSourceOption>> ListSourcesAsync(
        CancellationToken cancellationToken)
    {
        var repositories = await repositoryStore.ListAsync(cancellationToken);
        var owningKeys = repositories
            .Select(repository => repository.RepositoryHostConnectionKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var connections = await connectionStore.ListAsync(cancellationToken);

        return connections
            .Where(connection => owningKeys.Contains(connection.Key)
                && connection.Capabilities.Contains(ConnectionCapability.Repositories))
            .OrderBy(connection => connection.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(connection => connection.Key, StringComparer.OrdinalIgnoreCase)
            .Select(connection => new PullRequestSourceOption
            {
                Key = connection.Key,
                Name = string.IsNullOrWhiteSpace(connection.DisplayName)
                    ? connection.Key
                    : connection.DisplayName,
            })
            .ToArray();
    }

    private static string? Clean(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
