using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Provider-neutral reference to a work item linked to a pull request.
/// </summary>
public sealed record PullRequestWorkItemReference
{
    /// <summary>Provider-assigned work-item identifier.</summary>
    public string WorkItemId { get; init; } = string.Empty;

    /// <summary>Provider URL for retrieving the linked work item.</summary>
    public string WorkItemUrl { get; init; } = string.Empty;
}

/// <summary>
/// Point-in-time view of a pull request in a controller-managed repository.
/// </summary>
public sealed record ManagedPullRequestSnapshot
{
    /// <summary>Canonical identity and source metadata for the pull request.</summary>
    public PullRequestReference PullRequest { get; init; } = new();

    /// <summary>Pull-request title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Current provider status (for example, active, completed, or abandoned).</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Labels currently applied to the pull request.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Work items linked to the pull request by the source provider.</summary>
    public IReadOnlyList<PullRequestWorkItemReference> LinkedWorkItems { get; init; } = [];

    /// <summary>Stable, case-insensitive identity for this pull request.</summary>
    public string CanonicalKey => PullRequest.CanonicalKey;

    /// <summary>Managed source environment key.</summary>
    public string EnvironmentKey => PullRequest.EnvironmentKey;

    /// <summary>Managed repository key.</summary>
    public string RepositoryKey => PullRequest.RepositoryKey;

    /// <summary>Provider-assigned pull-request identifier.</summary>
    public string PullRequestId => PullRequest.PullRequestId;

    /// <summary>Browser URL for the pull request.</summary>
    public string PullRequestUrl => PullRequest.PullRequestUrl;

    /// <summary>Source branch containing the proposed changes.</summary>
    public string SourceBranch => PullRequest.SourceBranch;

    /// <summary>Target branch into which the changes will merge.</summary>
    public string TargetBranch => PullRequest.TargetBranch;

    /// <summary>Source-branch commit observed in this snapshot.</summary>
    public string SourceCommitSha => PullRequest.SourceCommitSha;
}

/// <summary>
/// Application port for enumerating active pull requests across all repositories
/// managed by the controller.
/// </summary>
public interface IManagedPullRequestDiscovery
{
    /// <summary>
    /// Lists active pull requests from managed repositories. Implementations return
    /// an empty collection when their provider is disabled or has no active pull requests.
    /// </summary>
    Task<IReadOnlyList<ManagedPullRequestSnapshot>> ListActiveAsync(
        CancellationToken cancellationToken
    );
}
