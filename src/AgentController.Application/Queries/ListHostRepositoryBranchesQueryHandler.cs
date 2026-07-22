using AgentController.Application.Abstractions;
using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>
/// Resolves a unified connection by key and dispatches branch
/// enumeration through the provider-keyed connection resolver.
/// Returns bare branch names with provider-specific prefixes (e.g. "refs/heads/") stripped.
/// </summary>
public sealed class ListHostRepositoryBranchesQueryHandler(
    IConnectionStore connectionStore,
    IConnectionResolver connectionResolver
) : IQueryHandler<ListHostRepositoryBranchesQuery, IReadOnlyList<string>>
{
    private readonly IConnectionStore _connectionStore = connectionStore;
    private readonly IConnectionResolver _connectionResolver = connectionResolver;

    public async Task<IReadOnlyList<string>> ExecuteAsync(
        ListHostRepositoryBranchesQuery query,
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
        return await _connectionResolver.ListBranchesAsync(
            profile,
            query.Project,
            query.RepositoryId,
            cancellationToken
        );
    }
}
