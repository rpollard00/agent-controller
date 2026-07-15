using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Queries;

/// <summary>
/// Resolves a managed work-source environment by key and dispatches connectivity
/// verification through the provider-keyed verifier resolver.
/// </summary>
public sealed class VerifyWorkSourceConnectivityQueryHandler(
    IManagedProfileResolver profileResolver,
    IWorkSourceConnectivityVerifierResolver verifierResolver
) : IQueryHandler<VerifyWorkSourceConnectivityQuery, WorkSourceConnectivityResult>
{
    private readonly IManagedProfileResolver _profileResolver = profileResolver;
    private readonly IWorkSourceConnectivityVerifierResolver _verifierResolver = verifierResolver;

    public async Task<WorkSourceConnectivityResult> ExecuteAsync(
        VerifyWorkSourceConnectivityQuery query,
        CancellationToken cancellationToken
    )
    {
        var key = WorkSourceEnvironmentProfileValidation.ValidateAndNormalizeKey(query.Key);
        if (!key.IsValid)
        {
            var validationErrors = key.Errors
                .SelectMany(pair => pair.Value.Select(msg => $"{pair.Key}: {msg}"))
                .ToList();
            return WorkSourceConnectivityResult.FailureResult(
                validationErrors,
                authMechanism: string.Empty
            );
        }

        var resolvedEnvironment = await _profileResolver.ResolveWorkSourceEnvironmentAsync(
            key.Key,
            cancellationToken
        );

        if (resolvedEnvironment is null)
        {
            return WorkSourceConnectivityResult.FailureResult(
                new[] { $"Work source environment '{key.Key}' was not found." },
                authMechanism: string.Empty
            );
        }

        // Dispatch through the provider-keyed resolver.
        // The resolver handles unsupported-provider fallback (non-success, no throw).
        // Individual verifiers handle config/PAT errors (non-success, no throw).
        return await _verifierResolver.VerifyAsync(resolvedEnvironment.Profile, cancellationToken);
    }
}
