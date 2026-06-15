namespace AgentController.Domain;

/// <summary>
/// Specification for starting an agent run.
/// Contains all context needed by the runtime to perform autonomous work.
/// </summary>
public sealed record AgentRunSpec
{
    /// <summary>Controller-assigned run identifier.</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>Reference to the external work item being worked on.</summary>
    public ExternalWorkRef WorkRef { get; init; } = new();

    /// <summary>Repository checkout the agent should work in.</summary>
    public RepositoryCheckout RepoCheckout { get; init; } = new();

    /// <summary>Environment handle for the run.</summary>
    public EnvironmentHandle EnvironmentHandle { get; init; } = new();

    /// <summary>Runtime profile name from configuration.</summary>
    public string RuntimeProfile { get; init; } = string.Empty;

    /// <summary>Additional context files to write into the run workspace (path → content).</summary>
    public IReadOnlyDictionary<string, string>? ContextFiles { get; init; }

    /// <summary>Suggested branch naming prefix (e.g. "agent/123").</summary>
    public string? BranchNamingPrefix { get; init; }

    /// <summary>Callback URL where the runtime can POST events.</summary>
    public string? CallbackUrl { get; init; }
}

/// <summary>
/// Handle returned when an agent run is created, started, or queried.
/// Carries the full persisted state of an agent run.
/// </summary>
public sealed record AgentRunHandle
{
    /// <summary>Controller-assigned run identifier.</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>Identifier of the work item this run is for.</summary>
    public string? WorkItemId { get; init; }

    /// <summary>Identifier of the environment provisioned for this run.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>Type of agent runtime used for this run (e.g. "PiMateria").</summary>
    public string? RuntimeType { get; init; }

    /// <summary>Runtime-assigned run identifier, if the runtime provides one.</summary>
    public string? RuntimeRunId { get; init; }

    /// <summary>Current lifecycle state of the run.</summary>
    public RunLifecycleState Status { get; init; } = RunLifecycleState.Queued;

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

    /// <summary>When the run record was first persisted.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the run record was last mutated.</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Status snapshot of an agent runtime.
/// </summary>
public sealed record AgentRuntimeStatus
{
    /// <summary>Current lifecycle state.</summary>
    public RunLifecycleState Status { get; init; }

    /// <summary>Runtime-assigned identifier.</summary>
    public string? RuntimeRunId { get; init; }

    /// <summary>When the run started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Last time a heartbeat was received from the runtime.</summary>
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    /// <summary>Recent events emitted by the runtime.</summary>
    public IReadOnlyList<RuntimeEvent>? Events { get; init; }

    /// <summary>Error message if the run is in a failed state.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Request to create a new agent run record in the persistence store.
/// </summary>
public sealed record CreateRunRequest
{
    /// <summary>Identifier of the work item this run is for.</summary>
    public string WorkItemId { get; init; } = string.Empty;

    /// <summary>Identifier of the worker/controller instance creating the run.</summary>
    public string WorkerId { get; init; } = string.Empty;

    /// <summary>Type of agent runtime to use (e.g. "PiMateria").</summary>
    public string? RuntimeType { get; init; }

    /// <summary>Initial lifecycle state for the run. Defaults to <see cref="RunLifecycleState.Claimed"/>.</summary>
    public RunLifecycleState InitialStatus { get; init; } = RunLifecycleState.Claimed;
}

/// <summary>
/// Partial update of runtime-related fields on an agent run.
/// All properties are optional; only non-null values are applied.
/// </summary>
public sealed record RuntimeFieldUpdate
{
    /// <summary>Runtime-assigned run identifier.</summary>
    public string? RuntimeRunId { get; init; }

    /// <summary>Type of agent runtime.</summary>
    public string? RuntimeType { get; init; }

    /// <summary>Identifier of the associated environment.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>Branch name created or used.</summary>
    public string? BranchName { get; init; }

    /// <summary>Pull request URL.</summary>
    public string? PullRequestUrl { get; init; }

    /// <summary>Human-readable result summary.</summary>
    public string? ResultSummary { get; init; }

    /// <summary>When the run started executing.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the run finished.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Last heartbeat timestamp from the runtime.</summary>
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    /// <summary>Error message.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Query parameters for listing agent runs.
/// All fields are optional; implementations apply the provided filters.
/// </summary>
public sealed record RunListQuery
{
    /// <summary>Filter by lifecycle state.</summary>
    public RunLifecycleState? Status { get; init; }

    /// <summary>Filter by associated work item identifier.</summary>
    public string? WorkItemId { get; init; }

    /// <summary>Maximum number of runs to return.</summary>
    public int MaxResults { get; init; } = 100;

    /// <summary>Number of runs to skip for pagination.</summary>
    public int Offset { get; init; } = 0;
}
