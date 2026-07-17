using AgentController.Application.Abstractions;
using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>
/// Resolves a unified connection by key and dispatches repository
/// enumeration through the provider-keyed connection resolver.
/// </summary>
public sealed class ListHostRepositoriesQueryHandler(
    IConnectionStore connectionStore,
    IConnectionResolver connectionResolver
) : IQueryHandler<ListHostRepositoriesQuery, IReadOnlyList<HostRepository>>
{
    private readonly IConnectionStore _connectionStore = connectionStore;
    private readonly IConnectionResolver _connectionResolver = connectionResolver;

    public async Task<IReadOnlyList<HostRepository>> ExecuteAsync(
        ListHostRepositoriesQuery query,
        CancellationToken cancellationToken
    )
    {
        var key = ConnectionProfileValidation.ValidateAndNormalizeKey(query.ConnectionKey);
        if (!key.IsValid)
        {
            return [];
        }

        var profile = await _connectionStore.GetByKeyAsync(key.Key, cancellationToken);
        if (profile is null)
        {
            return [];
        }

        // Dispatch through the unified connection resolver.
        // The resolver handles unsupported-provider fallback (empty list, no throw).
        // Individual connections handle config/PAT errors (empty list, no throw).
        return await _connectionResolver.ListRepositoriesAsync(
            profile,
            query.Project,
            cancellationToken
        );
    }
}
