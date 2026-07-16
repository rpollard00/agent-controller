using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Application;

/// <summary>
/// Provider-keyed resolver that maps a <see cref="WorkSourceEnvironmentProfile.Provider"/>
/// string to the registered <see cref="IWorkSourceConnectivityVerifier"/> for that provider.
/// Returns a non-success result with a clear error for unsupported providers.
///
/// Uses the generic <see cref="ProviderKeyedResolver{TService,TResult}"/> to avoid
/// duplicating the scoped resolution pattern.
/// </summary>
internal sealed class WorkSourceConnectivityVerifierResolver(
    IServiceScopeFactory scopeFactory,
    IReadOnlyDictionary<string, Type> verifierTypes
) : IWorkSourceConnectivityVerifierResolver
{
    private readonly ProviderKeyedResolver<IWorkSourceConnectivityVerifier, WorkSourceConnectivityResult>
        _resolver = new(scopeFactory, verifierTypes);

    public Task<WorkSourceConnectivityResult> VerifyAsync(
        WorkSourceEnvironmentProfile profile,
        CancellationToken cancellationToken
    )
    {
        return _resolver.ResolveAndExecuteAsync(
            profile.Provider,
            WorkSourceConnectivityResult.FailureResult(
                [
                    $"Connectivity verification is not supported for provider '{profile.Provider}'."
                ]
            ),
            (verifier, ct) => verifier.VerifyAsync(profile, ct),
            cancellationToken
        );
    }
}
