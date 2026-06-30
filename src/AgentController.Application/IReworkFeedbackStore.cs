using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Persistence contract for rework feedback soak-state records.
/// Used by the feedback worker to track debounce state across polls
/// so soak correctness survives restarts (stored in SQLite, not worker memory).
/// </summary>
public interface IReworkFeedbackStore
{
    /// <summary>
    /// Upsert a rework feedback soak-state row.
    /// If a row with the same (PullRequestId, FeedbackBundleId) exists it is
    /// updated; otherwise a new row is inserted.
    /// </summary>
    Task<ReworkFeedback> UpsertAsync(
        string originatingRunId,
        string pullRequestId,
        string feedbackBundleId,
        string feedbackBundleJson,
        int threadCount,
        DateTimeOffset firstQualifyingCommentAt,
        DateTimeOffset lastQualifyingCommentAt,
        ReworkFeedbackStatus status,
        CancellationToken cancellationToken);

    /// <summary>
    /// List all rework feedback rows currently in Watching status.
    /// Used by the feedback worker to check which bundles are still
    /// within their soak window.
    /// </summary>
    Task<IReadOnlyList<ReworkFeedback>> GetWatchingAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Mark a Watching row as Soaked when the soak window has elapsed.
    /// Returns the updated row, or null if the row was not found or
    /// is not in Watching status (idempotent guard).
    /// </summary>
    Task<ReworkFeedback?> MarkSoakedAsync(
        string id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Mark a row as Superseded when a newer bundle hash replaces it.
    /// Idempotent: no-op if already Superseded or Soaked.
    /// </summary>
    Task MarkSupersededAsync(
        string id,
        CancellationToken cancellationToken);

    /// <summary>
    /// List all rework feedback rows currently in Soaked status.
    /// Used by the feedback worker to find rows eligible for
    /// materialization into Pending ReworkCycle records.
    /// </summary>
    Task<IReadOnlyList<ReworkFeedback>> GetSoakedAsync(
        CancellationToken cancellationToken);
}
