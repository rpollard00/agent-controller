using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Persistence contract for rework cycle records.
/// Used by the feedback worker to materialize Pending cycles from soaked
/// feedback, and by the polling worker to look up and consume cycles at
/// claim time.
/// </summary>
public interface IReworkCycleStore
{
    /// <summary>
    /// Get the first Pending rework cycle for a given work item.
    /// Returns null if no pending cycle exists.
    /// </summary>
    Task<ReworkCycle?> GetPendingForWorkItemAsync(
        string workItemId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Check whether a rework cycle for the given feedback bundle already exists
    /// (in any status). Returns true if the bundle has already been materialized.
    /// </summary>
    Task<bool> ExistsByFeedbackBundleIdAsync(
        string feedbackBundleId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Create a new Pending rework cycle.
    /// Fails if a cycle with the same <paramref name="feedbackBundleId"/>
    /// already exists (unique index guard against double-materialization).
    /// </summary>
    Task<ReworkCycle> CreateAsync(
        string workItemId,
        int cycleNumber,
        string priorRunId,
        string branchName,
        string pullRequestUrl,
        string baseCommitSha,
        string feedbackBundleJson,
        string feedbackBundleId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Mark a Pending rework cycle as Consumed, recording the new run ID
    /// and consumption timestamp. Idempotent: no-op if already consumed.
    /// </summary>
    Task MarkConsumedAsync(
        string id,
        string newRunId,
        CancellationToken cancellationToken);

    /// <summary>
    /// List all Pending rework cycles.
    /// Used by the feedback worker to find cycles that need work item reactivation.
    /// </summary>
    Task<IReadOnlyList<ReworkCycle>> ListPendingAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// List all Consumed rework cycles.
    /// Used by the feedback worker to determine which work items already
    /// have an active rework in progress (non-terminal NewRunId).
    /// </summary>
    Task<IReadOnlyList<ReworkCycle>> ListConsumedAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Get the maximum cycle number for a given work item across all cycles
    /// (Pending and Consumed). Returns 0 if no cycles exist for the work item.
    /// Used by the feedback worker to compute the next cycle number.
    /// </summary>
    Task<int> GetMaxCycleNumberAsync(
        string workItemId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Mark a Pending rework cycle as reactivated (work item state transitioned
    /// and tags cleaned). Idempotent: no-op if already reactivated.
    /// Used to ensure reactivation runs at most once per cycle.
    /// </summary>
    Task MarkReactivatedAsync(
        string id,
        CancellationToken cancellationToken);
}
