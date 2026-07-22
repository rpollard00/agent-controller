using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Application.Abstractions;

/// <summary>
/// Resolves a provider-specific connection by provider string.
/// Maps <see cref="ConnectionProfile.Provider"/> values (e.g. "AzureDevOps")
/// to registered <see cref="IConnection"/> instances.
/// </summary>
public interface IConnectionResolver
{
    /// <summary>
    /// Verify connectivity using the connection registered for <paramref name="profile"/>.Provider.
    /// </summary>
    /// <param name="profile">The connection profile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A provider-neutral result. When the provider string has no registered connection,
    /// returns a non-success result with a clear "unsupported provider" error rather than throwing.
    /// </returns>
    Task<ConnectionConnectivityResult> VerifyConnectivityAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// List projects using the connection registered for <paramref name="profile"/>.Provider.
    /// </summary>
    /// <param name="profile">The connection profile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A list of provider-neutral project records. When the provider string has no
    /// registered connection, returns an empty list with a logged warning rather than throwing.
    /// </returns>
    Task<IReadOnlyList<ConnectionProject>> ListProjectsAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// List repositories within a project using the connection registered for <paramref name="profile"/>.Provider.
    /// </summary>
    /// <param name="profile">The connection profile.</param>
    /// <param name="project">Provider-specific project identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A list of provider-neutral repository records. When the provider string has no
    /// registered connection, returns an empty list with a logged warning rather than throwing.
    /// </returns>
    Task<IReadOnlyList<HostRepository>> ListRepositoriesAsync(
        ConnectionProfile profile,
        string project,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// List branches within a repository using the connection registered for <paramref name="profile"/>.Provider.
    /// </summary>
    /// <param name="profile">The connection profile.</param>
    /// <param name="project">Provider-specific project identifier.</param>
    /// <param name="repositoryId">Provider-specific repository identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A list of bare branch names. When the provider string has no
    /// registered connection, returns an empty list with a logged warning rather than throwing.
    /// </returns>
    Task<IReadOnlyList<string>> ListBranchesAsync(
        ConnectionProfile profile,
        string project,
        string repositoryId,
        CancellationToken cancellationToken
    );
}
