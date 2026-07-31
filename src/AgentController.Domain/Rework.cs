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
/// Carries a displayable author value, typed identity aliases, body, timestamp,
/// and whether it is a reply to another comment.
/// </summary>
public sealed record ReviewThreadComment
{
    /// <summary>Displayable author value supplied by the source provider.</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>Typed aliases that identify the comment author.</summary>
    public IReadOnlyList<ReviewerIdentity> AuthorIdentities { get; init; } = [];

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

    /// <summary>Displayable author value of the thread.</summary>
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
/// Identifies how work on an existing pull request was requested.
/// Numeric values are persisted; do not reorder or reuse them.
/// </summary>
public enum ReworkRequestMode
{
    /// <summary>
    /// Reopen the controller-produced work item and continue the pull request
    /// created by its prior run.
    /// </summary>
    Revival = 0,

    /// <summary>
    /// Create a new assistance work item and continue an existing pull request,
    /// whether or not the controller produced it.
    /// </summary>
    Assistance = 1,
}

/// <summary>
/// Provider-neutral, stable reference to an existing pull request.
/// The environment, repository, and provider pull-request identifier form its
/// canonical identity; URL, branches, and commit describe the referenced snapshot.
/// </summary>
public sealed record PullRequestReference
{
    /// <summary>Managed source environment key.</summary>
    public string EnvironmentKey { get; init; } = string.Empty;

    /// <summary>Managed repository key.</summary>
    public string RepositoryKey { get; init; } = string.Empty;

    /// <summary>Pull request identifier assigned by the source provider.</summary>
    public string PullRequestId { get; init; } = string.Empty;

    /// <summary>Browser URL for the pull request.</summary>
    public string PullRequestUrl { get; init; } = string.Empty;

    /// <summary>Source branch containing the pull request changes.</summary>
    public string SourceBranch { get; init; } = string.Empty;

    /// <summary>Target branch into which the pull request will merge.</summary>
    public string TargetBranch { get; init; } = string.Empty;

    /// <summary>Source-branch commit observed for this pull request snapshot.</summary>
    public string SourceCommitSha { get; init; } = string.Empty;

    /// <summary>
    /// Case-insensitive canonical identity suitable for correlation and persistence.
    /// Empty when an identity component is unavailable.
    /// </summary>
    public string CanonicalKey => CreateCanonicalKey(
        EnvironmentKey,
        RepositoryKey,
        PullRequestId
    );

    /// <summary>Whether all canonical identity components are available.</summary>
    public bool HasCanonicalIdentity => CanonicalKey.Length > 0;

    private static string CreateCanonicalKey(
        string environmentKey,
        string repositoryKey,
        string pullRequestId
    )
    {
        if (string.IsNullOrWhiteSpace(environmentKey)
            || string.IsNullOrWhiteSpace(repositoryKey)
            || string.IsNullOrWhiteSpace(pullRequestId))
        {
            return string.Empty;
        }

        return string.Join(
            '|',
            EncodeIdentitySegment(environmentKey),
            EncodeIdentitySegment(repositoryKey),
            EncodeIdentitySegment(pullRequestId)
        );
    }

    private static string EncodeIdentitySegment(string value) =>
        Uri.EscapeDataString(value.Trim().ToUpperInvariant());
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
    /// <summary>How work on the existing pull request was requested.</summary>
    public ReworkRequestMode RequestMode { get; init; } = ReworkRequestMode.Revival;

    /// <summary>Canonical reference to the pull request being continued.</summary>
    public PullRequestReference PullRequest { get; init; } = new();

    /// <summary>Which rework cycle this is (1-based).</summary>
    public int CycleNumber { get; init; }

    /// <summary>
    /// Controller-assigned run identifier of the prior run.
    /// Null for assistance requests not originating from a controller run.
    /// </summary>
    public string? PriorRunId { get; init; }

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

    /// <summary>
    /// This feedback has been materialized into a Pending ReworkCycle.
    /// Terminal state — will not be re-processed.
    /// </summary>
    Materialized,
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

    /// <summary>How work on the existing pull request was requested.</summary>
    public ReworkRequestMode RequestMode { get; init; } = ReworkRequestMode.Revival;

    /// <summary>Canonical reference to the pull request being observed.</summary>
    public PullRequestReference PullRequest { get; init; } = new();

    /// <summary>
    /// Controller-assigned run identifier of the run that produced the PR.
    /// Null for assistance requests not originating from a controller run.
    /// </summary>
    public string? OriginatingRunId { get; init; }

    /// <summary>Pull request identifier (from the source system).</summary>
    public string PullRequestId { get; init; } = string.Empty;

    /// <summary>
    /// Stable hash of the feedback bundle contents.
    /// Combined with request mode and canonical PR identity to prevent
    /// duplicate soak rows for the same bundle.
    /// </summary>
    public string FeedbackBundleId { get; init; } = string.Empty;

    /// <summary>
    /// Bundled review threads serialized as JSON.
    /// Persisted alongside the soak row so the bundle is available
    /// at materialization time without re-fetching from the feedback source.
    /// </summary>
    public string FeedbackBundleJson { get; init; } = string.Empty;

    /// <summary>Timestamp of the first qualifying comment for this bundle.</summary>
    public DateTimeOffset FirstQualifyingCommentAt { get; init; }

    /// <summary>Timestamp of the last qualifying comment for this bundle.</summary>
    public DateTimeOffset LastQualifyingCommentAt { get; init; }

    /// <summary>Number of review threads in the current bundle.</summary>
    public int ThreadCount { get; init; }

    /// <summary>
    /// Stable materialization correlation identifier. Assistance processing uses
    /// this value to reconcile external story creation after retries or restarts.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>Controller-local ID of the assistance story created for this feedback.</summary>
    public string? AssistanceStoryWorkItemId { get; init; }

    /// <summary>Provider-assigned ID of the assistance story created for this feedback.</summary>
    public string? AssistanceStoryExternalId { get; init; }

    /// <summary>Browser URL of the assistance story created for this feedback.</summary>
    public string? AssistanceStoryUrl { get; init; }

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

    /// <summary>How work on the existing pull request was requested.</summary>
    public ReworkRequestMode RequestMode { get; init; } = ReworkRequestMode.Revival;

    /// <summary>Canonical reference to the pull request being continued.</summary>
    public PullRequestReference PullRequest { get; init; } = new();

    /// <summary>Identifier of the work item this cycle is for.</summary>
    public string WorkItemId { get; init; } = string.Empty;

    /// <summary>
    /// Which rework cycle this is (1-based). Revival is numbered per original
    /// work item; Assistance is numbered per canonical pull request.
    /// </summary>
    public int CycleNumber { get; init; }

    /// <summary>
    /// Controller-assigned run identifier of the prior run.
    /// Null for assistance cycles not originating from a controller run.
    /// </summary>
    public string? PriorRunId { get; init; }

    /// <summary>Branch name the prior run pushed to.</summary>
    public string BranchName { get; init; } = string.Empty;

    /// <summary>URL of the pull request from the prior run.</summary>
    public string PullRequestUrl { get; init; } = string.Empty;

    /// <summary>Commit SHA the prior run based its work on.</summary>
    public string BaseCommitSha { get; init; } = string.Empty;

    /// <summary>Bundled review threads serialized as JSON.</summary>
    public string FeedbackBundleJson { get; init; } = string.Empty;

    /// <summary>
    /// Stable hash of the feedback bundle contents. Combined with request mode
    /// and canonical PR identity as the hard materialization idempotency guard.
    /// </summary>
    public string FeedbackBundleId { get; init; } = string.Empty;

    /// <summary>
    /// Stable materialization correlation identifier. Populated for assistance
    /// cycles so cycle creation can be safely retried after a restart.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>Current lifecycle status of the cycle.</summary>
    public ReworkCycleStatus Status { get; init; } = ReworkCycleStatus.Pending;

    /// <summary>When the cycle record was first persisted.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the work item was reactivated for this cycle (null if not yet
    /// reactivated). Used to ensure reactivation runs at most once per cycle.
    /// </summary>
    public DateTimeOffset? ReactivatedAt { get; init; }

    /// <summary>When the cycle was consumed by the happy path (null if still pending).</summary>
    public DateTimeOffset? ConsumedAt { get; init; }

    /// <summary>
    /// Controller-assigned run identifier of the new run that consumed this cycle
    /// (null if still pending).
    /// </summary>
    public string? NewRunId { get; init; }
}
