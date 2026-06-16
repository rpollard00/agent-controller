using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Port for calling Azure DevOps Boards REST APIs.
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
