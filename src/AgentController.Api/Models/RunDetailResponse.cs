using AgentController.Domain;

namespace AgentController.Api.Models;

/// <summary>
/// Full run detail returned by the GET /runs/{runId} endpoint.
/// Includes the associated work item, current run status, runtime fields,
/// environment record when present, and ordered lifecycle events so the
/// full local controller lifecycle is inspectable end to end.
/// </summary>
public sealed record RunDetailResponse
{
    /// <summary>Controller-assigned run identifier.</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>Identifier of the work item this run is for.</summary>
    public string? WorkItemId { get; init; }

    /// <summary>The associated work item, if it exists in the store.</summary>
    public WorkCandidate? WorkItem { get; init; }

    /// <summary>Identifier of the environment provisioned for this run.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>Type of agent runtime used for this run (e.g. "PiMateria").</summary>
    public string? RuntimeType { get; init; }

    /// <summary>Runtime-assigned run identifier, if the runtime provides one.</summary>
    public string? RuntimeRunId { get; init; }

    /// <summary>Current lifecycle state of the run.</summary>
    public string Status { get; init; } = nameof(RunLifecycleState.Queued);

    /// <summary>Branch name created or used by the runtime.</summary>
    public string? BranchName { get; init; }

    /// <summary>Pull request URL if the runtime opened one.</summary>
    public string? PullRequestUrl { get; init; }

    /// <summary>Human-readable summary of the run result.</summary>
    public string? ResultSummary { get; init; }

    /// <summary>When the run was started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the run finished (success or failure).</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Last time a heartbeat was received from the runtime.</summary>
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    /// <summary>Error message if the run is in a failed state.</summary>
    public string? Error { get; init; }

    /// <summary>Environment record when present.</summary>
    public EnvironmentHandle? Environment { get; init; }

    /// <summary>Ordered lifecycle events (oldest first).</summary>
    public IReadOnlyList<LifecycleEvent> LifecycleEvents { get; init; } = [];

    /// <summary>When the run record was first persisted.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the run record was last mutated.</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
