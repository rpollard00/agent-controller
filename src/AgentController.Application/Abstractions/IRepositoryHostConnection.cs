using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Application.Abstractions;

/// <summary>
/// Clone transport hint returned by a repository host to guide the materializer.
/// Mirrors the domain <see cref="CloneTransport"/> values as a hint from the host API.
/// </summary>
public enum CloneTransportHint
{
    /// <summary>Host did not specify a preferred transport.</summary>
    Unspecified = 0,

    /// <summary>SSH transport is preferred or available.</summary>
    Ssh = 1,

    /// <summary>HTTPS with Personal Access Token is preferred or available.</summary>
    HttpsPat = 2,
}

/// <summary>
/// Provider-neutral description of a remote repository discovered from a repository host.
/// </summary>
public sealed record HostRepository(
    /// <summary>
    /// Provider-specific repository identifier (e.g. ADO repo GUID).
    /// </summary>
    string Id,

    /// <summary>Human-readable repository name.</summary>
    string Name,

    /// <summary>Default branch name (e.g. "main", "master").</summary>
    string DefaultBranch,

    /// <summary>Remote URL suitable for cloning (provider format, e.g. HTTPS or SSH).</summary>
    string RemoteUrl,

    /// <summary>
    /// Suggested clone transport. The materializer may override based on local configuration.
    /// </summary>
    CloneTransportHint CloneTransportHint
);

/// <summary>
/// Port for interacting with a repository host provider (e.g. Azure DevOps Repos, GitHub, GitLab).
/// Implementations are provider-specific and handle connectivity verification and repository enumeration.
/// 
/// Supported provider discriminator values:
/// <list type="table">
///   <item><term>AzureDevOpsRepos</term><description>Azure DevOps Repos (implemented)</description></item>
///   <item><term>GitHub</term><description>GitHub (reserved)</description></item>
///   <item><term>Forgejo</term><description>Forgejo (reserved)</description></item>
///   <item><term>GitLab</term><description>GitLab (reserved)</description></item>
/// </list>
/// </summary>
public interface IRepositoryHostConnection
{
    /// <summary>
    /// Verify that the configured repository host connection can be reached and authenticated.
    /// </summary>
    /// <param name="profile">
    /// The repository host connection profile to verify.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A provider-neutral result indicating success or failure.
    /// Implementations must return a non-success result with descriptive errors
    /// rather than throwing on connectivity or configuration failures.
    /// </returns>
    Task<RepositoryHostConnectivityResult> VerifyConnectivityAsync(
        RepositoryHostConnectionProfile profile,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Enumerate repositories available from the host for the configured connection.
    /// </summary>
    /// <param name="profile">
    /// The repository host connection profile to enumerate from.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A list of provider-neutral <see cref="HostRepository"/> records.
    /// Implementations must return an empty list rather than throwing on enumeration failures.
    /// </returns>
    Task<IReadOnlyList<HostRepository>> ListRepositoriesAsync(
        RepositoryHostConnectionProfile profile,
        CancellationToken cancellationToken
    );
}
