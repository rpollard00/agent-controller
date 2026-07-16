using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Queries;

/// <summary>
/// Resolves a managed repository host connection by key and dispatches connectivity
/// verification through the provider-keyed host resolver.
/// </summary>
public sealed class VerifyRepositoryHostConnectivityQueryHandler(
    IRepositoryHostConnectionStore connectionStore,
    IRepositoryHostResolver hostResolver
) : IQueryHandler<VerifyRepositoryHostConnectivityQuery, RepositoryHostConnectivityResult>
{
    private readonly IRepositoryHostConnectionStore _connectionStore = connectionStore;
    private readonly IRepositoryHostResolver _hostResolver = hostResolver;

    public async Task<RepositoryHostConnectivityResult> ExecuteAsync(
        VerifyRepositoryHostConnectivityQuery query,
        CancellationToken cancellationToken
    )
    {
        var key = RepositoryHostConnectionProfileValidation.ValidateAndNormalizeKey(query.Key);
        if (!key.IsValid)
        {
            var validationErrors = key.Errors
                .SelectMany(pair => pair.Value.Select(msg => $"{pair.Key}: {msg}"))
                .ToList();
            return RepositoryHostConnectivityResult.FailureResult(
                validationErrors,
                authMechanism: string.Empty
            );
        }

        var profile = await _connectionStore.GetByKeyAsync(key.Key, cancellationToken);
        if (profile is null)
        {
            return RepositoryHostConnectivityResult.FailureResult(
                new[] { $"Repository host connection '{key.Key}' was not found." },
                authMechanism: string.Empty
            );
        }

        // Dispatch through the provider-keyed resolver.
        // The resolver handles unsupported-provider fallback (non-success, no throw).
        // Individual hosts handle config/PAT errors (non-success, no throw).
        return await _hostResolver.VerifyConnectivityAsync(profile, cancellationToken);
    }
}
