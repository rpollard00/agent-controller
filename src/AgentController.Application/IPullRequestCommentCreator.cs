using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Lifecycle milestones that can be reported on an assistance pull request.
/// </summary>
public enum AssistanceLifecycleCommentKind
{
    /// <summary>The generated assistance story is ready to be claimed.</summary>
    Queued = 0,

    /// <summary>The assistance run updated the pull request successfully.</summary>
    Completed,

    /// <summary>The assistance run failed and can be retried.</summary>
    Failed,

    /// <summary>The assistance run requires human attention.</summary>
    NeedsHuman,
}

/// <summary>
/// Provider-neutral request to report one assistance lifecycle milestone on a pull request.
/// The correlation identifier and lifecycle kind form the idempotency key, allowing each
/// distinct milestone to be safely retried while still permitting later milestones.
/// </summary>
public sealed record AssistanceLifecycleCommentRequest
{
    /// <summary>Stable identifier for the assistance materialization.</summary>
    public string CorrelationId { get; init; } = string.Empty;

    /// <summary>Lifecycle milestone being reported.</summary>
    public AssistanceLifecycleCommentKind Kind { get; init; }

    /// <summary>Provider-assigned identifier of the generated assistance story.</summary>
    public string AssistanceStoryId { get; init; } = string.Empty;

    /// <summary>Browser URL of the generated assistance story.</summary>
    public string AssistanceStoryUrl { get; init; } = string.Empty;
}

/// <summary>
/// Provider-neutral port for creating idempotent assistance lifecycle comments on an
/// existing pull request.
/// </summary>
public interface IPullRequestCommentCreator
{
    /// <summary>
    /// Creates the requested lifecycle comment unless the same correlation and milestone
    /// have already been posted.
    /// </summary>
    Task CreateAsync(
        PullRequestReference pullRequest,
        AssistanceLifecycleCommentRequest request,
        CancellationToken cancellationToken
    );
}
