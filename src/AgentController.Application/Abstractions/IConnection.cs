using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Application.Abstractions;

/// <summary>
/// Minimal project descriptor returned by <see cref="IConnection.ListProjectsAsync"/>
/// to populate UI dropdowns.
/// </summary>
public sealed record ConnectionProject(
    /// <summary>Provider-specific project identifier.</summary>
    string Id,

    /// <summary>Human-readable project name.</summary>
    string Name
);

/// <summary>
/// Unified, capability-aware port for interacting with a repository host,
/// work-source, or runtime-environment provider.
///
/// Keyed by <see cref="ConnectionProfile.Provider"/>.
///
/// Supported provider discriminator values:
/// <list type="table">
///   <item><term>AzureDevOps</term><description>Azure DevOps (Repositories + WorkTracking)</description></item>
/// </list>
/// 
/// Reserved provider strings (no implementations yet):
/// <c>GitHub</c>, <c>Forgejo</c>, <c>GitLab</c>, <c>Jira</c>.
/// </summary>
public interface IConnection
{
    /// <summary>
    /// Verify that the configured connection can be reached and authenticated.
    /// </summary>
    /// <param name="profile">
    /// The connection profile to verify.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A provider-neutral result indicating success or failure.
    /// Implementations must return a non-success result with descriptive errors
    /// rather than throwing on connectivity or configuration failures.
    /// </returns>
    Task<ConnectionConnectivityResult> VerifyConnectivityAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Enumerate projects available from the provider for the configured connection.
    /// </summary>
    /// <param name="profile">
    /// The connection profile to enumerate projects from.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A list of <see cref="ConnectionProject"/> records to feed UI dropdowns.
    /// Implementations must return an empty list rather than throwing on enumeration failures.
    /// </returns>
    Task<IReadOnlyList<ConnectionProject>> ListProjectsAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Enumerate repositories available within a specific project.
    /// </summary>
    /// <param name="profile">
    /// The connection profile to enumerate repositories from.
    /// </param>
    /// <param name="project">
    /// Provider-specific project identifier to scope the enumeration.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A list of provider-neutral <see cref="HostRepository"/> records.
    /// Implementations must return an empty list rather than throwing on enumeration failures.
    /// </returns>
    Task<IReadOnlyList<HostRepository>> ListRepositoriesAsync(
        ConnectionProfile profile,
        string project,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Enumerate branches available within a specific repository.
    /// </summary>
    /// <param name="profile">
    /// The connection profile to enumerate branches from.
    /// </param>
    /// <param name="project">
    /// Provider-specific project identifier to scope the enumeration.
    /// </param>
    /// <param name="repositoryId">
    /// Provider-specific repository identifier for which to list branches.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A list of bare branch names (e.g. "main", "develop") with provider-specific
    /// prefix (e.g. "refs/heads/") stripped.
    /// Implementations must return an empty list rather than throwing on enumeration failures.
    /// </returns>
    Task<IReadOnlyList<string>> ListBranchesAsync(
        ConnectionProfile profile,
        string project,
        string repositoryId,
        CancellationToken cancellationToken
    );
}
