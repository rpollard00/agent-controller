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

    /// <summary>Controller-assigned run identifier of the run that produced the PR.</summary>
    public string OriginatingRunId { get; set; } = string.Empty;

    /// <summary>Pull request identifier (from the source system).</summary>
    public string PullRequestId { get; set; } = string.Empty;

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

    /// <summary>Current soak-state lifecycle status (stored as int).</summary>
    public int Status { get; set; }

    /// <summary>When the soak row was first persisted.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the soak row was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
