namespace AgentController.Domain;

/// <summary>
/// Status of a review thread on a pull request.
/// Mirrors the Azure DevOps thread status values so the feedback source
/// can map directly without lossy conversions.
/// </summary>
public enum ReviewThreadStatus
{
    /// <summary>Thread is open and awaiting resolution.</summary>
    Active = 0,

    /// <summary>Thread was resolved by the author or reviewer.</summary>
    Resolved,

    /// <summary>Commenter indicated the issue was fixed.</summary>
    Fixed,

    /// <summary>Commenter indicated the change will not be made.</summary>
    WontFix,

    /// <summary>Thread was closed without a resolution reason.</summary>
    Closed,

    /// <summary>Commenter indicated the behavior is intentional.</summary>
    ByDesign,
}

/// <summary>
/// A single comment within a review thread.
/// Carries author, body, timestamp, and whether it is a reply to another comment.
/// </summary>
public sealed record ReviewThreadComment
{
    /// <summary>Canonical author identifier (uniqueName / email).</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>Comment body text.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>When the comment was posted.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>True if this comment is a reply to another comment in the thread.</summary>
    public bool IsReply { get; init; }
}

/// <summary>
/// A review thread from a pull request, carrying the full reply chain.
/// Sourced from the feedback source and consumed by the happy path as
/// structured rework context.
/// </summary>
public sealed record ReviewThread
{
    /// <summary>Stable thread identifier from the source system.</summary>
    public string ThreadId { get; init; } = string.Empty;

    /// <summary>Canonical author of the thread (uniqueName / email).</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>When the thread was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Current status of the thread.</summary>
    public ReviewThreadStatus Status { get; init; } = ReviewThreadStatus.Active;

    /// <summary>File path the thread is scoped to (null for PR-level threads).</summary>
    public string? FilePath { get; init; }

    /// <summary>Start line of the thread range (1-based, inclusive).</summary>
    public int? StartLine { get; init; }

    /// <summary>End line of the thread range (1-based, inclusive).</summary>
    public int? EndLine { get; init; }

    /// <summary>True if this is a PR-level (file-level) thread rather than line-specific.</summary>
    public bool IsFileLevel { get; init; }

    /// <summary>Ordered comments in the reply chain.</summary>
    public IReadOnlyList<ReviewThreadComment> Comments { get; init; } = [];
}

/// <summary>
/// Status of a persisted rework cycle row in the store.
/// Tracks the lifecycle from materialization to consumption by the happy path.
/// </summary>
public enum ReworkCycleStatus
{
    /// <summary>
    /// Cycle has been materialized from soaked feedback and is awaiting
    /// consumption by the polling worker.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Cycle was consumed by the happy path (context injected into a new run).
    /// </summary>
    Consumed,
}

/// <summary>
/// Consumable payload threaded through the happy path when the agent
/// is asked to address review feedback on an existing PR.
/// Carries the prior run context and the bundled review threads
/// that triggered this rework cycle.
/// </summary>
public sealed record ReworkContext
{
    /// <summary>Which rework cycle this is (1-based).</summary>
    public int CycleNumber { get; init; }

    /// <summary>Controller-assigned run identifier of the prior run.</summary>
    public string PriorRunId { get; init; } = string.Empty;

    /// <summary>Branch name the prior run pushed to.</summary>
    public string BranchName { get; init; } = string.Empty;

    /// <summary>URL of the pull request from the prior run.</summary>
    public string PullRequestUrl { get; init; } = string.Empty;

    /// <summary>Commit SHA the prior run based its work on.</summary>
    public string BaseCommitSha { get; init; } = string.Empty;

    /// <summary>Bundled review threads that triggered this rework cycle.</summary>
    public IReadOnlyList<ReviewThread> FeedbackBundle { get; init; } = [];
}

/// <summary>
/// Status of a persisted rework feedback soak-state row.
/// Tracks the debounce lifecycle from first qualifying comment
/// through soak window to readiness for materialization.
/// </summary>
public enum ReworkFeedbackStatus
{
    /// <summary>
    /// Feedback bundle is being watched for additional comments.
    /// Soak timer is active; row becomes Soaked after soakMinutes
    /// of inactivity.
    /// </summary>
    Watching = 0,

    /// <summary>
    /// Soak window has elapsed with no new qualifying comments.
    /// Row is eligible for materialization into a Pending ReworkCycle.
    /// </summary>
    Soaked,

    /// <summary>
    /// A newer feedback bundle superseded this one (bundle hash changed).
    /// This row is stale and will not be materialized.
    /// </summary>
    Superseded,
}

/// <summary>
/// Persisted soak-state record for debounce logic.
/// Lives in SQLite so soak correctness survives worker restarts
/// rather than relying on in-memory state.
/// </summary>
public sealed record ReworkFeedback
{
    /// <summary>Controller-assigned identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Controller-assigned run identifier of the run that produced the PR.</summary>
    public string OriginatingRunId { get; init; } = string.Empty;

    /// <summary>Pull request identifier (from the source system).</summary>
    public string PullRequestId { get; init; } = string.Empty;

    /// <summary>
    /// Stable hash of the feedback bundle contents.
    /// Unique index on (PullRequestId, FeedbackBundleId) prevents
    /// duplicate soak rows for the same bundle.
    /// </summary>
    public string FeedbackBundleId { get; init; } = string.Empty;

    /// <summary>Timestamp of the first qualifying comment for this bundle.</summary>
    public DateTimeOffset FirstQualifyingCommentAt { get; init; }

    /// <summary>Timestamp of the last qualifying comment for this bundle.</summary>
    public DateTimeOffset LastQualifyingCommentAt { get; init; }

    /// <summary>Number of review threads in the current bundle.</summary>
    public int ThreadCount { get; init; }

    /// <summary>Current soak-state lifecycle status.</summary>
    public ReworkFeedbackStatus Status { get; init; } = ReworkFeedbackStatus.Watching;

    /// <summary>When the soak row was first persisted.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the soak row was last updated (bumped on new comments or status change).</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Persisted rework cycle linking soaked feedback to a new agent run.
/// Materialized by the feedback worker from a Soaked ReworkFeedback row
/// and consumed by the polling worker at claim time.
/// </summary>
public sealed record ReworkCycle
{
    /// <summary>Controller-assigned identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Identifier of the work item this cycle is for.</summary>
    public string WorkItemId { get; init; } = string.Empty;

    /// <summary>Which rework cycle this is for the work item (1-based).</summary>
    public int CycleNumber { get; init; }

    /// <summary>Controller-assigned run identifier of the prior run.</summary>
    public string PriorRunId { get; init; } = string.Empty;

    /// <summary>Branch name the prior run pushed to.</summary>
    public string BranchName { get; init; } = string.Empty;

    /// <summary>URL of the pull request from the prior run.</summary>
    public string PullRequestUrl { get; init; } = string.Empty;

    /// <summary>Commit SHA the prior run based its work on.</summary>
    public string BaseCommitSha { get; init; } = string.Empty;

    /// <summary>Bundled review threads serialized as JSON.</summary>
    public string FeedbackBundleJson { get; init; } = string.Empty;

    /// <summary>
    /// Stable hash of the feedback bundle contents. Unique index on this
    /// column is the hard idempotency guard against double-materialization.
    /// </summary>
    public string FeedbackBundleId { get; init; } = string.Empty;

    /// <summary>Current lifecycle status of the cycle.</summary>
    public ReworkCycleStatus Status { get; init; } = ReworkCycleStatus.Pending;

    /// <summary>When the cycle record was first persisted.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the cycle was consumed by the happy path (null if still pending).</summary>
    public DateTimeOffset? ConsumedAt { get; init; }

    /// <summary>
    /// Controller-assigned run identifier of the new run that consumed this cycle
    /// (null if still pending).
    /// </summary>
    public string? NewRunId { get; init; }
}
