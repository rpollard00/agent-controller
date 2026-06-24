using System.Net;
using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Port for calling Azure DevOps REST APIs (Boards + Git).
/// Provides the minimal surface needed by <see cref="IWorkSource"/>
/// implementations that target Azure DevOps Boards.
///
/// All methods accept a <c>CancellationToken</c> and map Azure DevOps
/// domain types into the controller's domain types
/// (<see cref="WorkCandidate"/>, <see cref="ClaimResult"/>, etc.).
/// </summary>
public interface IAzureDevOpsBoardsClient
{
    /// <summary>
    /// Discover work items eligible for autonomous agent execution.
    /// </summary>
    Task<IReadOnlyList<WorkCandidate>> QueryWorkItemsAsync(
        BoardsQueryParameters parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Attempt to claim a work item for exclusive execution.
    /// </summary>
    Task<ClaimResult> TryClaimWorkItemAsync(
        ExternalWorkRef workRef,
        ClaimRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Update the external status, tags, or state of a work item.
    /// </summary>
    Task UpdateWorkItemStatusAsync(
        ExternalWorkRef workRef,
        ExternalWorkStatus status,
        CancellationToken cancellationToken);

    /// <summary>
    /// Append a comment to a work item.
    /// </summary>
    Task AddCommentAsync(
        ExternalWorkRef workRef,
        string comment,
        CancellationToken cancellationToken);

    /// <summary>
    /// Enumerate Git repositories in an Azure DevOps project.
    /// Calls GET {project}/_apis/git/repositories?api-version=7.1
    /// and returns parsed repository metadata.
    /// </summary>
    Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(
        string project,
        CancellationToken cancellationToken);

    /// <summary>
    /// Verify connectivity to an Azure DevOps organization/project using a PAT.
    /// Performs a lightweight GET on the project endpoint, and on success
    /// enumerates Git repositories in the project.
    /// Returns a result capturing success/failure, HTTP status, and any errors
    /// rather than throwing on failure.
    /// </summary>
    Task<AzureDevOpsConnectivityResult> VerifyConnectivityAsync(
        string organizationUrl,
        string project,
        string personalAccessToken,
        CancellationToken cancellationToken);
}

/// <summary>
/// Metadata for a Git repository in an Azure DevOps project.
/// </summary>
public sealed record RepositoryInfo
{
    /// <summary>Repository identifier (GUID string).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Repository name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Default branch name (e.g. <c>refs/heads/main</c>), or <c>null</c> if not set.</summary>
    public string? DefaultBranch { get; init; }

    /// <summary>Remote URL for cloning, or <c>null</c> if unavailable.</summary>
    public string? RemoteUrl { get; init; }
}

/// <summary>
/// Query parameters for Azure DevOps Boards work item queries.
/// Field/project-level filters are supplied; the client translates them
/// into the appropriate Azure DevOps REST API query (WIQL or OData).
/// </summary>
public sealed record BoardsQueryParameters
{
    /// <summary>Project name (required for Azure DevOps queries).</summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>Work item states to include.</summary>
    public IReadOnlyList<string>? States { get; init; }

    /// <summary>Tags that must be present on a work item.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Tags that must not be present on a work item.</summary>
    public IReadOnlyList<string>? ExcludedTags { get; init; }

    /// <summary>Maximum number of items to return.</summary>
    public int MaxResults { get; init; } = 50;
}
