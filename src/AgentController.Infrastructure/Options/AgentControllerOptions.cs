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
}
