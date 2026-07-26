using AgentController.Domain;

namespace AgentController.Application;

/// <summary>Complete persistence payload for a materialized rework cycle.</summary>
public sealed record ReworkCycleCreateRequest
{
    public ReworkRequestMode RequestMode { get; init; } = ReworkRequestMode.Revival;
    public PullRequestReference PullRequest { get; init; } = new();
    public string WorkItemId { get; init; } = string.Empty;

    /// <summary>
    /// Explicit cycle number for Revival. Assistance cycle numbers are allocated
    /// by the store from the canonical pull-request identity, so this value is ignored.
    /// </summary>
    public int CycleNumber { get; init; }

    public string? PriorRunId { get; init; }
    public string BranchName { get; init; } = string.Empty;
    public string PullRequestUrl { get; init; } = string.Empty;
    public string BaseCommitSha { get; init; } = string.Empty;
    public string FeedbackBundleJson { get; init; } = string.Empty;
    public string FeedbackBundleId { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
}

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
    /// Get the rework cycle consumed by a specific run. This preserves the
    /// rework dispatch contract when a later run in that run's retry lineage
    /// must continue the same pull request.
    /// </summary>
    Task<ReworkCycle?> GetConsumedByRunIdAsync(
        string runId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Check whether a rework cycle for the given feedback bundle already exists
    /// (in any status). Returns true if the bundle has already been materialized.
    /// </summary>
    Task<bool> ExistsByFeedbackBundleIdAsync(
        string feedbackBundleId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Check for a materialized bundle within one request mode and canonical PR.
    /// Falls back to the legacy bundle guard when the canonical identity is unavailable.
    /// </summary>
    Task<bool> ExistsAsync(
        ReworkRequestMode requestMode,
        PullRequestReference pullRequest,
        string feedbackBundleId,
        CancellationToken cancellationToken);

    /// <summary>Find a cycle by its stable materialization correlation.</summary>
    Task<ReworkCycle?> GetByCorrelationIdAsync(
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Idempotently create a Pending cycle from a complete persistence payload.
    /// Assistance cycles are assigned the next number for their canonical pull request;
    /// concurrent allocation conflicts are retried. Revival uses the caller-supplied
    /// cycle number. A retry with the same correlation or per-PR bundle returns the
    /// existing cycle.
    /// </summary>
    Task<ReworkCycle> CreateAsync(
        ReworkCycleCreateRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Create a new Pending Revival rework cycle.
    /// Fails if a cycle with the same <paramref name="feedbackBundleId"/>
    /// already exists (unique index guard against double-materialization).
    /// </summary>
    Task<ReworkCycle> CreateAsync(
        string workItemId,
        int cycleNumber,
        string? priorRunId,
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
    /// This work-item-scoped operation retains Revival's original numbering behavior.
    /// </summary>
    Task<int> GetMaxCycleNumberAsync(
        string workItemId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Get the maximum Assistance cycle number for a canonical pull request across
    /// all generated work items. Returns 0 when the pull request has no cycles.
    /// </summary>
    Task<int> GetMaxAssistanceCycleNumberAsync(
        PullRequestReference pullRequest,
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
