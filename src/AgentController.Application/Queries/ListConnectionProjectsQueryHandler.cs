using AgentController.Application.Abstractions;

namespace AgentController.Application.Queries;

/// <summary>
/// Resolves a unified connection by key and dispatches project
/// enumeration through the provider-keyed connection resolver.
/// </summary>
public sealed class ListConnectionProjectsQueryHandler(
    IConnectionStore connectionStore,
    IConnectionResolver connectionResolver
) : IQueryHandler<ListConnectionProjectsQuery, IReadOnlyList<ConnectionProject>>
{
    private readonly IConnectionStore _connectionStore = connectionStore;
    private readonly IConnectionResolver _connectionResolver = connectionResolver;

    public async Task<IReadOnlyList<ConnectionProject>> ExecuteAsync(
        ListConnectionProjectsQuery query,
        CancellationToken cancellationToken
    )
    {
        var key = ConnectionProfileValidation.ValidateAndNormalizeKey(query.Key);
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
        return await _connectionResolver.ListProjectsAsync(profile, cancellationToken);
    }
}
