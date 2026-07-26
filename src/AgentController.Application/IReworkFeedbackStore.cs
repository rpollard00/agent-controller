using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Complete persistence payload for a feedback observation.
/// </summary>
public sealed record ReworkFeedbackUpsertRequest
{
    public ReworkRequestMode RequestMode { get; init; } = ReworkRequestMode.Revival;
    public PullRequestReference PullRequest { get; init; } = new();
    public string? OriginatingRunId { get; init; }
    public string FeedbackBundleId { get; init; } = string.Empty;
    public string FeedbackBundleJson { get; init; } = string.Empty;
    public int ThreadCount { get; init; }
    public DateTimeOffset FirstQualifyingCommentAt { get; init; }
    public DateTimeOffset LastQualifyingCommentAt { get; init; }
    public ReworkFeedbackStatus Status { get; init; } = ReworkFeedbackStatus.Watching;
    public string? CorrelationId { get; init; }
}

/// <summary>
/// Restart-safe receipt for an assistance story created from soaked feedback.
/// The external fields may be recorded before the local work-item upsert has completed.
/// </summary>
public sealed record AssistanceStoryReceipt
{
    public string CorrelationId { get; init; } = string.Empty;
    public string? WorkItemId { get; init; }
    public string ExternalId { get; init; } = string.Empty;
    public string? Url { get; init; }
}

/// <summary>
/// Persistence contract for rework feedback soak-state records.
/// Used by the feedback worker to track debounce state across polls
/// so soak correctness survives restarts (stored in SQLite, not worker memory).
/// </summary>
public interface IReworkFeedbackStore
{
    /// <summary>
    /// Upsert a feedback row with its request mode, canonical pull-request
    /// snapshot, optional run lineage, and materialization correlation.
    /// </summary>
    Task<ReworkFeedback> UpsertAsync(
        ReworkFeedbackUpsertRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Legacy Revival overload retained for callers that do not yet carry a
    /// canonical pull-request reference. If a row with the same
    /// (PullRequestId, FeedbackBundleId) exists it is updated.
    /// </summary>
    Task<ReworkFeedback> UpsertAsync(
        string? originatingRunId,
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
    /// List all feedback rows that still contribute to lifecycle tracking.
    /// Superseded observations are omitted; Watching, Soaked, and Materialized
    /// rows are returned so controller APIs can trace the request through dispatch.
    /// </summary>
    Task<IReadOnlyList<ReworkFeedback>> GetTrackedAsync(
        CancellationToken cancellationToken);

    /// <summary>Find a feedback row by its stable materialization correlation.</summary>
    Task<ReworkFeedback?> GetByCorrelationIdAsync(
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persist or enrich the assistance-story receipt for a feedback row.
    /// Repeating the same receipt is idempotent; conflicting IDs are rejected.
    /// </summary>
    Task<ReworkFeedback> RecordAssistanceStoryAsync(
        string id,
        AssistanceStoryReceipt receipt,
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

    /// <summary>
    /// Mark a Soaked row as Materialized after its feedback has been
    /// used to create a Pending ReworkCycle. Idempotent: no-op if
    /// not in Soaked status.
    /// </summary>
    Task MarkMaterializedAsync(
        string id,
        CancellationToken cancellationToken);
}
