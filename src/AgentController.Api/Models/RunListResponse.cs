using AgentController.Domain;

namespace AgentController.Api.Models;

/// <summary>
/// Summary item returned in the GET /runs list endpoint.
/// Carries enough fields for operators to scan active and recent runs.
/// </summary>
public sealed record RunListResponse
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
    public string Status { get; init; } = nameof(RunLifecycleState.Queued);

    /// <summary>When the run was started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the run finished (success or failure).</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Last time a heartbeat was received from the runtime.</summary>
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    /// <summary>When the run record was first persisted.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Response for the GET /runs endpoint.
/// </summary>
public sealed record RunListEnvelope
{
    /// <summary>List of runs matching the query.</summary>
    public IReadOnlyList<RunListResponse> Runs { get; init; } = [];

    /// <summary>Total number of runs matching the query (before pagination).</summary>
    public int TotalCount { get; init; }
}
