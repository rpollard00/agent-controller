using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Port for discovering, claiming, and updating work items from a work source.
/// First real implementation: AzureDevOpsBoardsWorkSource.
/// Development/testing implementation: LocalFakeWorkSource.
/// </summary>
public interface IWorkSource
{
    /// <summary>
    /// Discover work items eligible for autonomous agent execution
    /// according to the provided query.
    /// </summary>
    Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(
        WorkQuery query,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Attempt to claim a work candidate for exclusive execution.
    /// Returns a <see cref="ClaimResult"/> indicating success or failure.
    /// </summary>
    Task<ClaimResult> TryClaimAsync(
        WorkCandidate candidate,
        ClaimRequest claim,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Project controller status back to the work source
    /// (update tags, state, or other external status fields).
    /// </summary>
    Task UpdateStatusAsync(
        ExternalWorkRef workRef,
        ExternalWorkStatus status,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Append a comment to the work item in the work source.
    /// </summary>
    Task AddCommentAsync(
        ExternalWorkRef workRef,
        string comment,
        CancellationToken cancellationToken
    );
}
