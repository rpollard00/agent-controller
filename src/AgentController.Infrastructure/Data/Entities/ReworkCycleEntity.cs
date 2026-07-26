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

    /// <summary>How work on the existing pull request was requested.</summary>
    public int RequestMode { get; set; }

    /// <summary>Canonical environment/repository/PR identity, when available.</summary>
    public string? CanonicalPullRequestKey { get; set; }

    /// <summary>Managed source environment key.</summary>
    public string? PullRequestEnvironmentKey { get; set; }

    /// <summary>Managed repository key.</summary>
    public string? PullRequestRepositoryKey { get; set; }

    /// <summary>Provider pull-request identifier.</summary>
    public string? PullRequestId { get; set; }

    /// <summary>Target branch into which the pull request will merge.</summary>
    public string? PullRequestTargetBranch { get; set; }

    /// <summary>Identifier of the work item this cycle is for.</summary>
    public string WorkItemId { get; set; } = string.Empty;

    /// <summary>
    /// Which rework cycle this is (1-based), scoped to the original work item
    /// for Revival and to the canonical pull request for Assistance.
    /// </summary>
    public int CycleNumber { get; set; }

    /// <summary>Controller-assigned run identifier of the prior run.</summary>
    public string? PriorRunId { get; set; }

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
    /// Combined with request mode and canonical PR identity for idempotency.
    /// </summary>
    public string FeedbackBundleId { get; set; } = string.Empty;

    /// <summary>Stable correlation used to reconcile assistance materialization.</summary>
    public string? CorrelationId { get; set; }

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
