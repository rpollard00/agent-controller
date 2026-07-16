using AgentController.Domain;

namespace AgentController.Application.Results;

/// <summary>
/// Result of materializing a repository into a local workspace.
/// Carries the checkout metadata and any resolved environment variables
/// that downstream consumers (e.g. agent runtimes) should inject.
/// </summary>
public sealed record RepositoryMaterializationResult
{
    /// <summary>Whether materialization succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Repository key.</summary>
    public string RepoKey { get; init; } = string.Empty;

    /// <summary>Absolute local filesystem path to the cloned repository.</summary>
    public string LocalPath { get; init; } = string.Empty;

    /// <summary>Branch currently checked out.</summary>
    public string Branch { get; init; } = string.Empty;

    /// <summary>HEAD commit SHA at clone time, if available.</summary>
    public string? CommitSha { get; init; }

    /// <summary>Transport type used for the clone.</summary>
    public CloneTransport Transport { get; init; } = CloneTransport.Unspecified;

    /// <summary>When the materialization was completed.</summary>
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Environment variables resolved from the secrets manifest that should
    /// be forwarded to the agent process (e.g. PAT values for downstream tools).
    /// Empty when no secrets require forwarding.
    /// </summary>
    public IReadOnlyDictionary<string, string> ResolvedEnvVars { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Error messages if materialization failed.
    /// Empty when successful.
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>Create a success result.</summary>
    public static RepositoryMaterializationResult SuccessResult(
        string repoKey,
        string localPath,
        string branch,
        string? commitSha,
        CloneTransport transport,
        IReadOnlyDictionary<string, string> resolvedEnvVars
    ) => new()
    {
        Success = true,
        RepoKey = repoKey,
        LocalPath = localPath,
        Branch = branch,
        CommitSha = commitSha,
        Transport = transport,
        ResolvedEnvVars = resolvedEnvVars,
    };

    /// <summary>Create a failure result.</summary>
    public static RepositoryMaterializationResult FailureResult(
        string repoKey,
        params string[] errors
    ) => new()
    {
        Success = false,
        RepoKey = repoKey,
        Errors = errors,
    };
}
