using System.ComponentModel.DataAnnotations;

namespace AgentController.Infrastructure.Options;

/// <summary>
/// Top-level controller configuration.
/// Section: "agentController"
/// </summary>
public sealed class AgentControllerOptions
{
    public const string SectionName = "agentController";

    /// <summary>
    /// Human- or machine-readable identifier for this controller instance.
    /// Used in claim leases and log correlation.
    /// </summary>
    [Required]
    public string WorkerId { get; init; } = string.Empty;

    /// <summary>
    /// How often the worker polls for new eligible work items, in seconds.
    /// Must be positive.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int PollIntervalSeconds { get; init; } = 30;

    /// <summary>
    /// Maximum number of concurrent agent runs this controller will allow.
    /// Must be positive.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxConcurrentRuns { get; init; } = 3;

    /// <summary>
    /// Root directory for per-run workspaces.
    /// Supports tilde (~) expansion for home directory.
    /// </summary>
    [Required]
    public string RunRoot { get; init; } = "~/.agent-work-controller/runs";

    /// <summary>
    /// Whether to retain workspace directories for successful runs.
    /// Debugging-friendly default: true.
    /// </summary>
    public bool RetainSuccessfulRuns { get; init; } = true;

    /// <summary>
    /// Whether to retain workspace directories for failed runs.
    /// Debugging-friendly default: true.
    /// </summary>
    public bool RetainFailedRuns { get; init; } = true;

    /// <summary>
    /// Whether to enable the background polling worker.
    /// Disabled by default for early scaffolding; enable once real providers are wired.
    /// </summary>
    public bool WorkerEnabled { get; init; }

    /// <summary>
    /// Maximum time a run can be in <see cref="Domain.RunLifecycleState.AwaitingResult"/>
    /// or <see cref="Domain.RunLifecycleState.AgentRunning"/>
    /// without a heartbeat or final event before being considered stale and recovered.
    /// Must be positive. Default: 30 minutes.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int StaleTimeoutSeconds { get; init; } = 1800;

    /// <summary>
    /// Maximum number of run attempts for a single work item before escalating
    /// to NeedsHuman. When a run fails with a retryable error (keepalive-stall,
    /// process-exit-without-terminal), the controller kicks off a fresh run for
    /// the same ADO board story from scratch, up to this threshold.
    /// Must be positive. Default: 3.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxRunAttempts { get; init; } = 3;
}
