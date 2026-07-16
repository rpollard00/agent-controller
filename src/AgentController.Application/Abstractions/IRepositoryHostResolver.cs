using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Application.Abstractions;

/// <summary>
/// Resolves a provider-specific repository host by provider string.
/// Maps <see cref="RepositoryHostConnectionProfile.Provider"/> values (e.g. "AzureDevOpsRepos")
/// to registered <see cref="IRepositoryHostConnection"/> instances.
/// </summary>
public interface IRepositoryHostResolver
{
    /// <summary>
    /// Verify connectivity using the host registered for <paramref name="profile"/>.Provider.
    /// </summary>
    /// <param name="profile">The repository host connection profile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A provider-neutral result. When the provider string has no registered host,
    /// returns a non-success result with a clear "unsupported provider" error rather than throwing.
    /// </returns>
    Task<RepositoryHostConnectivityResult> VerifyConnectivityAsync(
        RepositoryHostConnectionProfile profile,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// List repositories using the host registered for <paramref name="profile"/>.Provider.
    /// </summary>
    /// <param name="profile">The repository host connection profile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A list of provider-neutral repository records. When the provider string has no
    /// registered host, returns an empty list with a logged warning rather than throwing.
    /// </returns>
    Task<IReadOnlyList<HostRepository>> ListRepositoriesAsync(
        RepositoryHostConnectionProfile profile,
        CancellationToken cancellationToken
    );
}
