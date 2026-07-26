namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// EF Core entity for the ReworkFeedback table.
/// Maps to the domain ReworkFeedback record used by the feedback pipeline
/// to track soak-state debounce in SQLite so correctness survives restarts.
/// </summary>
internal sealed class ReworkFeedbackEntity
{
    /// <summary>Controller-assigned identifier (PK).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>How work on the existing pull request was requested.</summary>
    public int RequestMode { get; set; }

    /// <summary>Canonical environment/repository/PR identity, when available.</summary>
    public string? CanonicalPullRequestKey { get; set; }

    /// <summary>Managed source environment key.</summary>
    public string? PullRequestEnvironmentKey { get; set; }

    /// <summary>Managed repository key.</summary>
    public string? PullRequestRepositoryKey { get; set; }

    /// <summary>Controller-assigned run identifier of the run that produced the PR.</summary>
    public string? OriginatingRunId { get; set; }

    /// <summary>Pull request identifier (from the source system).</summary>
    public string PullRequestId { get; set; } = string.Empty;

    /// <summary>Browser URL for the pull request.</summary>
    public string? PullRequestUrl { get; set; }

    /// <summary>Source branch containing the pull request changes.</summary>
    public string? PullRequestSourceBranch { get; set; }

    /// <summary>Target branch into which the pull request will merge.</summary>
    public string? PullRequestTargetBranch { get; set; }

    /// <summary>Source commit observed for the pull request.</summary>
    public string? PullRequestSourceCommitSha { get; set; }

    /// <summary>Stable hash of the feedback bundle contents.</summary>
    public string FeedbackBundleId { get; set; } = string.Empty;

    /// <summary>Bundled review threads serialized as JSON.</summary>
    public string FeedbackBundleJson { get; set; } = string.Empty;

    /// <summary>Timestamp of the first qualifying comment for this bundle.</summary>
    public DateTimeOffset FirstQualifyingCommentAt { get; set; }

    /// <summary>Timestamp of the last qualifying comment for this bundle.</summary>
    public DateTimeOffset LastQualifyingCommentAt { get; set; }

    /// <summary>Number of review threads in the current bundle.</summary>
    public int ThreadCount { get; set; }

    /// <summary>Stable correlation used to reconcile assistance materialization.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Controller-local assistance story work-item ID.</summary>
    public string? AssistanceStoryWorkItemId { get; set; }

    /// <summary>Provider-assigned assistance story ID.</summary>
    public string? AssistanceStoryExternalId { get; set; }

    /// <summary>Browser URL for the created assistance story.</summary>
    public string? AssistanceStoryUrl { get; set; }

    /// <summary>Current soak-state lifecycle status (stored as int).</summary>
    public int Status { get; set; }

    /// <summary>When the soak row was first persisted.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the soak row was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
