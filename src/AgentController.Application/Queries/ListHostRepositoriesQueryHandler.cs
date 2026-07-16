using AgentController.Application.Abstractions;
using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>
/// Resolves a managed repository host connection by key and dispatches repository
/// enumeration through the provider-keyed host resolver.
/// </summary>
public sealed class ListHostRepositoriesQueryHandler(
    IRepositoryHostConnectionStore connectionStore,
    IRepositoryHostResolver hostResolver
) : IQueryHandler<ListHostRepositoriesQuery, IReadOnlyList<HostRepository>>
{
    private readonly IRepositoryHostConnectionStore _connectionStore = connectionStore;
    private readonly IRepositoryHostResolver _hostResolver = hostResolver;

    public async Task<IReadOnlyList<HostRepository>> ExecuteAsync(
        ListHostRepositoriesQuery query,
        CancellationToken cancellationToken
    )
    {
        var key = RepositoryHostConnectionProfileValidation.ValidateAndNormalizeKey(query.ConnectionKey);
        if (!key.IsValid)
        {
            return [];
        }

        var profile = await _connectionStore.GetByKeyAsync(key.Key, cancellationToken);
        if (profile is null)
        {
            return [];
        }

        // Dispatch through the provider-keyed resolver.
        // The resolver handles unsupported-provider fallback (empty list, no throw).
        // Individual hosts handle config/PAT errors (empty list, no throw).
        return await _hostResolver.ListRepositoriesAsync(profile, cancellationToken);
    }
}
