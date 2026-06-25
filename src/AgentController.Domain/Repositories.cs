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

    /// <summary>Name of the environment profile to use for this repository.</summary>
    public string EnvironmentProfile { get; init; } = string.Empty;

    /// <summary>Name of the runtime profile to use for this repository.</summary>
    public string RuntimeProfile { get; init; } = string.Empty;

    /// <summary>
    /// Allowed paths within the repository that the agent may modify.
    /// An empty list means no path restrictions.
    /// </summary>
    public IReadOnlyList<string> AllowedPaths { get; init; } = [];
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
