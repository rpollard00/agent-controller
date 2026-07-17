using AgentController.Application.Abstractions;
using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>Lists managed connections in deterministic store order.</summary>
public sealed class ListConnectionsQueryHandler(
    IConnectionStore connectionStore
) : IQueryHandler<ListConnectionsQuery, IReadOnlyList<ConnectionProfile>>
{
    private readonly IConnectionStore _connectionStore = connectionStore;

    public async Task<IReadOnlyList<ConnectionProfile>> ExecuteAsync(
        ListConnectionsQuery query,
        CancellationToken cancellationToken
    )
    {
        return await _connectionStore.ListAsync(cancellationToken);
    }
}
