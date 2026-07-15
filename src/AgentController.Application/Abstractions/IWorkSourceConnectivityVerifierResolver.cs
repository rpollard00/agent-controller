using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Application.Abstractions;

/// <summary>
/// Resolves a provider-specific connectivity verifier by provider string.
/// Maps <see cref="WorkSourceEnvironmentProfile.Provider"/> values (e.g. "AzureDevOpsBoards")
/// to registered <see cref="IWorkSourceConnectivityVerifier"/> instances.
/// </summary>
public interface IWorkSourceConnectivityVerifierResolver
{
    /// <summary>
    /// Verify connectivity using the verifier registered for <paramref name="profile"/>.Provider.
    /// </summary>
    /// <param name="profile">The resolved work-source environment profile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A provider-neutral result. When the provider string has no registered verifier,
    /// returns a non-success result with a clear "unsupported provider" error rather than throwing.
    /// </returns>
    Task<WorkSourceConnectivityResult> VerifyAsync(
        WorkSourceEnvironmentProfile profile,
        CancellationToken cancellationToken
    );
}
