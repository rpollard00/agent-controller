namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// EF Core entity for the ReworkCycles table.
/// Maps to the domain ReworkCycle record used by the feedback pipeline
/// to track materialized rework cycles from Pending to Consumed.
/// </summary>
internal sealed class ReworkCycleEntity
{
    /// <summary>Controller-assigned identifier (PK).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Identifier of the work item this cycle is for.</summary>
    public string WorkItemId { get; set; } = string.Empty;

    /// <summary>Which rework cycle this is for the work item (1-based).</summary>
    public int CycleNumber { get; set; }

    /// <summary>Controller-assigned run identifier of the prior run.</summary>
    public string PriorRunId { get; set; } = string.Empty;

    /// <summary>Branch name the prior run pushed to.</summary>
    public string BranchName { get; set; } = string.Empty;

    /// <summary>URL of the pull request from the prior run.</summary>
    public string PullRequestUrl { get; set; } = string.Empty;

    /// <summary>Commit SHA the prior run based its work on.</summary>
    public string BaseCommitSha { get; set; } = string.Empty;

    /// <summary>Bundled review threads serialized as JSON.</summary>
    public string FeedbackBundleJson { get; set; } = string.Empty;

    /// <summary>
    /// Stable hash of the feedback bundle contents.
    /// Unique index on this column is the hard idempotency guard.
    /// </summary>
    public string FeedbackBundleId { get; set; } = string.Empty;

    /// <summary>Current lifecycle status (stored as int).</summary>
    public int Status { get; set; }

    /// <summary>When the cycle record was first persisted.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the work item was reactivated for this cycle.</summary>
    public DateTimeOffset? ReactivatedAt { get; set; }

    /// <summary>When the cycle was consumed by the happy path.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>Controller-assigned run identifier of the new run that consumed this cycle.</summary>
    public string? NewRunId { get; set; }
}
