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
}
