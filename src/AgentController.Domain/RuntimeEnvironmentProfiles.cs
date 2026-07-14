namespace AgentController.Domain;

/// <summary>
/// Managed configuration selecting an execution-environment provider and an agent runtime.
/// This reusable profile is distinct from a run-scoped <see cref="EnvironmentHandle"/>.
/// </summary>
public sealed record RuntimeEnvironmentProfile
{
    /// <summary>Stable key used by repository profiles to reference this profile.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Human-readable name shown to operators.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Whether the profile may be used for new runs.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Environment provider identifier, such as <c>LocalWorkspace</c>.</summary>
    public string EnvironmentProvider { get; init; } = string.Empty;

    /// <summary>Settings interpreted by the selected environment provider.</summary>
    public EnvironmentProviderSettings EnvironmentSettings { get; init; } = new();

    /// <summary>Agent runtime identifier, such as <c>PiMateria</c>.</summary>
    public string RuntimeProvider { get; init; } = string.Empty;

    /// <summary>Settings interpreted by the selected agent runtime.</summary>
    public RuntimeProviderSettings RuntimeSettings { get; init; } = new();

    /// <summary>When the managed profile was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the managed profile was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Structured settings shared by execution-environment providers.</summary>
public sealed record EnvironmentProviderSettings
{
    /// <summary>Root directory under which run workspaces are provisioned.</summary>
    public string? WorkspaceRoot { get; init; }
}

/// <summary>
/// Legacy per-profile runtime settings retained so profiles written by earlier controller
/// versions can still be read. Pi Materia execution settings are controller-owned and these
/// values are not used when launching a run.
/// </summary>
public sealed record RuntimeProviderSettings
{
    public string? PiExecutablePath { get; init; }

    public string? ControllerBaseUrl { get; init; }

    public string? PtyWrapperPath { get; init; }

    public string? PtyWrapperArgs { get; init; }

    public IReadOnlyDictionary<ExecutionKind, string> Loadouts { get; init; } =
        new Dictionary<ExecutionKind, string>();

    public IReadOnlyDictionary<string, string> ForwardEnvironmentVariables { get; init; } =
        new Dictionary<string, string>();
}
