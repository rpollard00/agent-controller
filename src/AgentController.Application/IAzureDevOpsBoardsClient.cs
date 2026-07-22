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
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Attempt to claim a work item for exclusive execution.
    /// </summary>
    Task<ClaimResult> TryClaimWorkItemAsync(
        ExternalWorkRef workRef,
        ClaimRequest request,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Update the external status, tags, or state of a work item.
    /// </summary>
    /// <returns>True if the update succeeded, false on failure (e.g. invalid state transition).</returns>
    Task<bool> UpdateWorkItemStatusAsync(
        ExternalWorkRef workRef,
        ExternalWorkStatus status,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Append a comment to a work item.
    /// </summary>
    Task AddCommentAsync(
        ExternalWorkRef workRef,
        string comment,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Enumerate Git repositories in an Azure DevOps project.
    /// Calls GET {project}/_apis/git/repositories?api-version=7.1
    /// and returns parsed repository metadata.
    /// </summary>
    Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(
        string project,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Verify connectivity to an Azure DevOps organization/project using a PAT.
    /// Performs a lightweight GET on the project endpoint, and on success
    /// enumerates Git repositories in the project. Org-level checks (empty project)
    /// skip repository enumeration.
    /// Returns a result capturing success/failure, HTTP status, and any errors
    /// rather than throwing on failure.
    /// </summary>
    Task<AzureDevOpsConnectivityResult> VerifyConnectivityAsync(
        string organizationUrl,
        string project,
        string personalAccessToken,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Fetch discussion comments (thread history) for a work item.
    /// Returns comments in chronological order, bounded by <paramref name="maxComments"/>.
    /// Used to surface ADO work item comments into runtime context for the agent.
    /// </summary>
    Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
        ExternalWorkRef workRef,
        int maxComments,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Release a previously claimed work item back to the work source.
    /// Strips agent-controlled tags (agent-active, agent-worker:*) and
    /// optionally reverts the work item state so it becomes eligible for re-discovery.
    /// </summary>
    Task ReleaseClaimWorkItemAsync(
        ReleaseClaimRequest request,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Enumerate the valid <c>System.State</c> values for all work item types
    /// in the process associated with a project, using the Azure DevOps Process API.
    ///
    /// Flow: GET project → read capabilities.processTemplate.templateTypeId →
    /// GET process WITs → for each WIT, GET its states (in parallel).
    ///
    /// Returns results grouped by work item type: a map from work item type name
    /// to a sorted list of bare state names. Work item types are sorted alphabetically;
    /// states within each type are sorted alphabetically.
    ///
    /// This is used by startup validation to ensure the configured
    /// <c>ActiveState</c>, <c>CompletedState</c>, and <c>EligibleStates</c>
    /// are valid states for at least one WIT in the process.
    /// </summary>
    /// <param name="project">The Azure DevOps project name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A dictionary mapping work item type name → sorted list of bare state names.
    /// Returns an empty dictionary on failure rather than throwing.
    /// </returns>
    /// <summary>
    /// Enumerate branches (Git refs) for a repository.
    /// Calls GET {project}/_apis/git/repositories/{repositoryId}/refs?filter=heads/&amp;api-version=7.1
    /// and returns bare branch names with the "refs/heads/" prefix stripped.
    /// Returns an empty list on failure rather than throwing.
    /// </summary>
    Task<IReadOnlyList<string>> ListBranchesAsync(
        string project,
        string repositoryId,
        CancellationToken cancellationToken
    );

    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetValidStatesAsync(
        string project,
        CancellationToken cancellationToken
    );
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

    /// <summary>SSH URL for cloning, or <c>null</c> if unavailable.</summary>
    public string? SshUrl { get; init; }
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

    /// <summary>Work item states to exclude.</summary>
    public IReadOnlyList<string>? ExcludedStates { get; init; }

    /// <summary>Tags that must be present on a work item.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Tags that must not be present on a work item.</summary>
    public IReadOnlyList<string>? ExcludedTags { get; init; }

    /// <summary>Maximum number of items to return.</summary>
    public int MaxResults { get; init; } = 50;
}
