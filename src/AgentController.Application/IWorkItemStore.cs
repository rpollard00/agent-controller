using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Persistence contract for work items (local fake and externally sourced).
/// Used by API endpoints and the worker polling loop.
/// Implementations are storage-agnostic; API and worker code must not
/// reference EF Core or any specific persistence technology directly.
/// </summary>
public interface IWorkItemStore
{
    /// <summary>
    /// Create a new local fake work item and return it with its controller-assigned identifier.
    /// </summary>
    Task<WorkCandidate> CreateAsync(
        CreateWorkItemRequest request,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// List work items matching the optional filters in <paramref name="query"/>.
    /// Supports pagination via <see cref="ListWorkItemsQuery.Offset"/> and
    /// <see cref="ListWorkItemsQuery.MaxResults"/>.
    /// </summary>
    Task<IReadOnlyList<WorkCandidate>> ListAsync(
        ListWorkItemsQuery query,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Get a single work item by its controller-assigned identifier.
    /// Returns null if no item matches.
    /// </summary>
    Task<WorkCandidate?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Find work items eligible for autonomous agent execution according to
    /// the provided query. This is the persistence-level query used by
    /// <see cref="IWorkSource"/> implementations to discover candidates
    /// from the local store.
    /// </summary>
    Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(
        WorkQuery query,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Attempt to claim a work item for exclusive execution.
    /// The claim is atomic: the store verifies the item is unclaimed
    /// (no active lease), sets <c>LeaseOwner</c> and <c>LeaseExpiresAt</c>,
    /// and returns success. Returns failure if the item was already claimed
    /// by another worker with an active lease.
    /// </summary>
    Task<ClaimResult> TryClaimAsync(
        string workItemId,
        ClaimRequest claim,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Update the status string of a work item (controller-internal projection).
    /// </summary>
    Task UpdateStatusAsync(
        string workItemId,
        string status,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Upsert a work candidate into the local persistence store.
    /// Keyed by the combination of <see cref="WorkCandidate.Source"/> and
    /// <see cref="WorkCandidate.ExternalId"/>. If a matching record already
    /// exists, its mutable fields (title, description, status, priority,
    /// tags, assigned-to, repo key, source metadata) are updated; otherwise
    /// a new record is inserted. Returns the persisted candidate with its
    /// controller-assigned <see cref="WorkCandidate.Id"/>.
    ///
    /// This enables externally discovered work items (e.g. from Azure DevOps
    /// Boards) to be persisted before claiming and run creation, so the
    /// lifecycle service can find them by ID.
    /// </summary>
    Task<WorkCandidate> UpsertAsync(
        WorkCandidate candidate,
        CancellationToken cancellationToken
    );
}
