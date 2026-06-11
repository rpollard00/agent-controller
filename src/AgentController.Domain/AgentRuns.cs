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
/// Handle returned when an agent run is started.
/// </summary>
public sealed record AgentRunHandle
{
    /// <summary>Controller-assigned run identifier.</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>Runtime-assigned run identifier, if the runtime provides one.</summary>
    public string? RuntimeRunId { get; init; }

    /// <summary>Current lifecycle state of the run.</summary>
    public RunLifecycleState Status { get; init; } = RunLifecycleState.Queued;

    /// <summary>When the run was started.</summary>
    public DateTimeOffset? StartedAt { get; init; }
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
