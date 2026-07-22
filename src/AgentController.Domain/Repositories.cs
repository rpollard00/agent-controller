using AgentController.Domain.Secrets;

namespace AgentController.Domain;

/// <summary>
/// Specification for cloning a repository.
/// </summary>
public sealed record RepositorySpec
{
    /// <summary>Repository key matching a configured profile.</summary>
    public string RepoKey { get; init; } = string.Empty;

    /// <summary>Remote URL to clone from.</summary>
    public string CloneUrl { get; init; } = string.Empty;

    /// <summary>Default branch to check out after cloning.</summary>
    public string DefaultBranch { get; init; } = "main";

    /// <summary>Clone transport type (SSH, HTTPS+PAT, Local, or inferred).</summary>
    public CloneTransport Transport { get; init; } = CloneTransport.Unspecified;

    /// <summary>Optional resolved repository profile from configuration.</summary>
    public RepositoryProfile? Profile { get; init; }

    /// <summary>
    /// Optional resolved host connection associated with <see cref="Profile"/>.
    /// Runtime clone providers use this profile to locate the connection's typed,
    /// optionally version-pinned credential reference without querying persistence.
    /// </summary>
    public ConnectionProfile? RepositoryConnection { get; init; }
}

/// <summary>
/// Result of a repository clone operation.
/// </summary>
public sealed record RepositoryCheckout
{
    /// <summary>Repository key.</summary>
    public string RepoKey { get; init; } = string.Empty;

    /// <summary>Absolute local filesystem path to the cloned repository.</summary>
    public string LocalPath { get; init; } = string.Empty;

    /// <summary>Branch currently checked out.</summary>
    public string Branch { get; init; } = string.Empty;

    /// <summary>HEAD commit SHA at clone time, if available.</summary>
    public string? CommitSha { get; init; }

    /// <summary>Transport type used for the clone (e.g. SSH, HTTPS+PAT, Local).</summary>
    public CloneTransport Transport { get; init; } = CloneTransport.Unspecified;

    /// <summary>When the clone was completed.</summary>
    public DateTimeOffset ClonedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Repository profile loaded from configuration.
/// Maps a repository key to clone details and associated profiles.
/// </summary>
public sealed record RepositoryProfile
{
    /// <summary>Unique key for this repository profile.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Remote URL to clone.</summary>
    public string CloneUrl { get; init; } = string.Empty;

    /// <summary>Default branch to check out after cloning.</summary>
    public string DefaultBranch { get; init; } = "main";

    /// <summary>Clone transport type (SSH, HTTPS+PAT, Local, or inferred).</summary>
    public CloneTransport Transport { get; init; } = CloneTransport.Unspecified;

    /// <summary>
    /// Legacy environment profile name loaded from appsettings.
    /// Retained for backward compatibility with existing repository configuration.
    /// </summary>
    public string EnvironmentProfile { get; init; } = string.Empty;

    /// <summary>
    /// Legacy runtime profile name loaded from appsettings.
    /// Retained for backward compatibility with existing repository configuration.
    /// </summary>
    public string RuntimeProfile { get; init; } = string.Empty;

    /// <summary>
    /// Optional key of the managed repository host connection profile.
    /// References a unified <see cref="ConnectionProfile"/> with the
    /// <see cref="ConnectionCapability.Repositories"/> capability.
    /// </summary>
    public string? RepositoryHostConnectionKey { get; init; }

    /// <summary>
    /// Provider-specific project name scoped to this repository.
    /// Persisted explicitly (not derived from <see cref="CloneUrl"/>).
    /// </summary>
    public string? Project { get; init; }

    /// <summary>
    /// Optional provider-specific remote identity for this repository
    /// (e.g. ADO repo GUID or name). Used to correlate the local profile
    /// with the remote repository on the connected host.
    /// </summary>
    public string? RemoteIdentity { get; init; }

    /// <summary>Optional key of the managed runtime environment profile.</summary>
    public string? RuntimeEnvironmentKey { get; init; }

    /// <summary>
    /// Optional named secret reference for the Personal Access Token used for
    /// HTTPS+PAT clone authentication. Resolved at materialization time via
    /// <see cref="ISecretStore"/> by name.
    /// </summary>
    public string? PersonalAccessTokenSecretName { get; init; }

    /// <summary>
    /// Optional reference to the SSH-key secret used by this repository.
    /// A version may be pinned; when omitted, consumers resolve the latest version.
    /// </summary>
    public SecretReference? SshKeyReference { get; init; }


}

/// <summary>
/// Reference to a source control resource for status inspection.
/// </summary>
public sealed record SourceControlRef
{
    /// <summary>Provider identifier (e.g. "AzureDevOpsRepos").</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>Repository key.</summary>
    public string RepoKey { get; init; } = string.Empty;

    /// <summary>Branch name, if known.</summary>
    public string? Branch { get; init; }

    /// <summary>Commit SHA, if known.</summary>
    public string? CommitSha { get; init; }
}

/// <summary>
/// Status of a source control resource (branch, PR, etc.).
/// </summary>
public sealed record SourceControlStatus
{
    /// <summary>Whether the referenced resource still exists.</summary>
    public bool Exists { get; init; }

    /// <summary>Current branch name.</summary>
    public string? Branch { get; init; }

    /// <summary>Current HEAD commit SHA.</summary>
    public string? CommitSha { get; init; }

    /// <summary>Pull request URL, if one was created.</summary>
    public string? PullRequestUrl { get; init; }

    /// <summary>Pull request status (e.g. "active", "completed", "abandoned").</summary>
    public string? PullRequestStatus { get; init; }
}
