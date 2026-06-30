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
