namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// EF Core entity for the AgentRuns table.
/// Maps to the prototype data model defined in the architecture (§7.5).
/// </summary>
internal sealed class AgentRunEntity
{
    /// <summary>Controller-assigned run identifier (PK).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Identifier of the work item this run is for.</summary>
    public string? WorkItemId { get; set; }

    /// <summary>Identifier of the worker/controller instance that owns this run.</summary>
    public string? WorkerId { get; set; }

    /// <summary>Type of agent runtime used (e.g. "PiMateria").</summary>
    public string? RuntimeType { get; set; }

    /// <summary>Runtime-assigned run identifier, if the runtime provides one.</summary>
    public string? RuntimeRunId { get; set; }

    /// <summary>Identifier of the environment provisioned for this run.</summary>
    public string? EnvironmentId { get; set; }

    /// <summary>Current lifecycle state of the run (stored as int).</summary>
    public int Status { get; set; }

    /// <summary>Branch name created or used by the runtime.</summary>
    public string? BranchName { get; set; }

    /// <summary>Pull request URL if the runtime opened one.</summary>
    public string? PullRequestUrl { get; set; }

    /// <summary>Human-readable summary of the run result.</summary>
    public string? ResultSummary { get; set; }

    /// <summary>When the run was started.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>When the run finished (success or failure).</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Last time a heartbeat was received from the runtime.</summary>
    public DateTimeOffset? LastHeartbeatAt { get; set; }

    /// <summary>Error message if the run is in a failed state.</summary>
    public string? Error { get; set; }

    /// <summary>When the run record was first persisted.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the run record was last mutated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
