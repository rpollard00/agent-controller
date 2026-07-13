namespace AgentController.Domain;

/// <summary>
/// Specification for creating an execution environment.
/// </summary>
public sealed record EnvironmentSpec
{
    /// <summary>Run identifier this environment is for.</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>Environment profile name from configuration.</summary>
    public string Profile { get; init; } = string.Empty;

    /// <summary>Root path under which the run environment is created, if specified.</summary>
    public string? RootPath { get; init; }

    /// <summary>The resolved managed or configured runtime-environment profile.</summary>
    public RuntimeEnvironmentProfile? RuntimeEnvironmentProfile { get; init; }

    /// <summary>Additional provider-specific metadata.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Handle to a provisioned execution environment.
/// </summary>
public sealed record EnvironmentHandle
{
    /// <summary>Unique environment identifier assigned by the provider.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Provider type that created this environment.</summary>
    public string ProviderType { get; init; } = string.Empty;

    /// <summary>Absolute root path of the environment on the host.</summary>
    public string RootPath { get; init; } = string.Empty;

    /// <summary>Current status of the environment.</summary>
    public string Status { get; init; } = string.Empty;
}

/// <summary>
/// Specification for executing a command within an environment.
/// </summary>
public sealed record CommandSpec
{
    /// <summary>Executable or command to run.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>Arguments to pass to the command.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Working directory for the command, relative to environment root.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Environment variables to set for the command.</summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }

    /// <summary>Maximum duration before the command is terminated.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>
/// Result of executing a command within an environment.
/// </summary>
public sealed record CommandResult
{
    /// <summary>Process exit code.</summary>
    public int ExitCode { get; init; }

    /// <summary>Captured standard output.</summary>
    public string? StdOut { get; init; }

    /// <summary>Captured standard error.</summary>
    public string? StdErr { get; init; }

    /// <summary>Wall-clock duration of the command execution.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Whether the command was terminated due to a timeout.</summary>
    public bool TimedOut { get; init; }
}

/// <summary>
/// Request to create a new environment record in the persistence store.
/// </summary>
public sealed record CreateEnvironmentRequest
{
    /// <summary>Run identifier this environment is associated with.</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>Provider type that created this environment.</summary>
    public string ProviderType { get; init; } = string.Empty;

    /// <summary>Absolute root path of the environment on the host.</summary>
    public string RootPath { get; init; } = string.Empty;

    /// <summary>Initial status of the environment.</summary>
    public string Status { get; init; } = "created";

    /// <summary>Provider-specific metadata.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
