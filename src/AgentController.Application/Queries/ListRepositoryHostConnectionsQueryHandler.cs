using AgentController.Application.Abstractions;
using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>Lists managed repository host connections in deterministic store order.</summary>
public sealed class ListRepositoryHostConnectionsQueryHandler(
    IRepositoryHostConnectionStore connectionStore
) : IQueryHandler<ListRepositoryHostConnectionsQuery, IReadOnlyList<RepositoryHostConnectionProfile>>
{
    private readonly IRepositoryHostConnectionStore _connectionStore = connectionStore;

    public async Task<IReadOnlyList<RepositoryHostConnectionProfile>> ExecuteAsync(
        ListRepositoryHostConnectionsQuery query,
        CancellationToken cancellationToken
    )
    {
        return await _connectionStore.ListAsync(cancellationToken);
    }
}
