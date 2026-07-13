using AgentController.Application.Abstractions;
using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>Lists managed repository profiles in the store's deterministic key order.</summary>
public sealed class ListRepositoriesQueryHandler(IRepositoryStore repositoryStore)
    : IQueryHandler<ListRepositoriesQuery, IReadOnlyList<RepositoryProfile>>
{
    private readonly IRepositoryStore _repositoryStore = repositoryStore;

    public async Task<IReadOnlyList<RepositoryProfile>> ExecuteAsync(
        ListRepositoriesQuery query,
        CancellationToken cancellationToken
    )
    {
        return await _repositoryStore.ListAsync(cancellationToken);
    }
}
