using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Application;

/// <summary>
/// Provider-keyed resolver that maps a <see cref="WorkSourceEnvironmentProfile.Provider"/>
/// string to the registered <see cref="IWorkSourceConnectivityVerifier"/> for that provider.
/// Returns a non-success result with a clear error for unsupported providers.
/// </summary>
internal sealed class WorkSourceConnectivityVerifierResolver(
    IServiceProvider serviceProvider,
    IReadOnlyDictionary<string, Type> verifierTypes
) : IWorkSourceConnectivityVerifierResolver
{
    /// <inheritdoc />
    public Task<WorkSourceConnectivityResult> VerifyAsync(
        WorkSourceEnvironmentProfile profile,
        CancellationToken cancellationToken
    )
    {
        if (!verifierTypes.TryGetValue(profile.Provider, out var verifierType))
        {
            return Task.FromResult(
                WorkSourceConnectivityResult.FailureResult(
                    [
                        $"Connectivity verification is not supported for provider '{profile.Provider}'."
                    ]
                )
            );
        }

        var verifier = (IWorkSourceConnectivityVerifier)serviceProvider
            .GetRequiredService(verifierType);

        return verifier.VerifyAsync(profile, cancellationToken);
    }
}
