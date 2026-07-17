using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Queries;

/// <summary>
/// Resolves a unified connection by key and dispatches connectivity
/// verification through the provider-keyed connection resolver.
/// </summary>
public sealed class VerifyConnectionQueryHandler(
    IConnectionStore connectionStore,
    IConnectionResolver connectionResolver
) : IQueryHandler<VerifyConnectionQuery, ConnectionConnectivityResult>
{
    private readonly IConnectionStore _connectionStore = connectionStore;
    private readonly IConnectionResolver _connectionResolver = connectionResolver;

    public async Task<ConnectionConnectivityResult> ExecuteAsync(
        VerifyConnectionQuery query,
        CancellationToken cancellationToken
    )
    {
        var key = ConnectionProfileValidation.ValidateAndNormalizeKey(query.Key);
        if (!key.IsValid)
        {
            var validationErrors = key.Errors
                .SelectMany(pair => pair.Value.Select(msg => $"{pair.Key}: {msg}"))
                .ToList();
            return ConnectionConnectivityResult.FailureResult(
                validationErrors,
                authMechanism: string.Empty
            );
        }

        var profile = await _connectionStore.GetByKeyAsync(key.Key, cancellationToken);
        if (profile is null)
        {
            return ConnectionConnectivityResult.FailureResult(
                new[] { $"Connection '{key.Key}' was not found." },
                authMechanism: string.Empty
            );
        }

        // Dispatch through the unified connection resolver.
        // The resolver handles unsupported-provider fallback (non-success, no throw).
        // Individual connections handle config/PAT errors (non-success, no throw).
        return await _connectionResolver.VerifyConnectivityAsync(profile, cancellationToken);
    }
}
