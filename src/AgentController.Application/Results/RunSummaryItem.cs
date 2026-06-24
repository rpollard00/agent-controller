namespace AgentController.Application.Results;

/// <summary>
/// Summary of a single agent run returned from the list query.
/// Enriched with work item title and repo key when available.
/// </summary>
public sealed record RunSummaryItem
{
    /// <summary>Controller-assigned run identifier.</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>Identifier of the work item this run is for.</summary>
    public string? WorkItemId { get; init; }

    /// <summary>Work item title, if the associated work item was fetched.</summary>
    public string? WorkItemTitle { get; init; }

    /// <summary>Repository key the work item maps to.</summary>
    public string? RepoKey { get; init; }

    /// <summary>Current lifecycle state of the run.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>When the run was started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the run finished (success or failure).</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Last time a heartbeat was received from the runtime.</summary>
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    /// <summary>When the run record was first persisted.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}
