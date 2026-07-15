using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Application.Abstractions;

/// <summary>
/// Port for verifying connectivity to a work-source environment.
/// Implementations are provider-specific (e.g. Azure DevOps, GitHub).
/// </summary>
public interface IWorkSourceConnectivityVerifier
{
    /// <summary>
    /// Verify that the configured work-source environment can be reached and authenticated.
    /// </summary>
    /// <param name="profile">
    /// The resolved work-source environment profile to verify.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A provider-neutral result indicating success or failure.
    /// Implementations must return a non-success result with descriptive errors
    /// rather than throwing on connectivity or configuration failures.
    /// </returns>
    Task<WorkSourceConnectivityResult> VerifyAsync(
        WorkSourceEnvironmentProfile profile,
        CancellationToken cancellationToken
    );
}
