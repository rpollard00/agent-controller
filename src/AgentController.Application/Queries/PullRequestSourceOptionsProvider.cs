using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>Lists repository-capable source-control environments that own managed repositories.</summary>
public sealed class PullRequestSourceOptionsProvider(
    IRepositoryStore repositoryStore,
    IConnectionStore connectionStore)
{
    public async Task<IReadOnlyList<PullRequestSourceOption>> ListAsync(
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
}
