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
/// Per-profile runtime settings. <see cref="Loadouts"/> are a user-level, profile-specific
/// control. The remaining properties are legacy fields retained so profiles written by
/// earlier controller versions can still be read; Pi Materia process behavior (executable,
/// controller callback URL, PTY, environment-variable forwarding) is controller-owned and
/// these legacy values are not used when launching a run.
/// </summary>
public sealed record RuntimeProviderSettings
{
    /// <summary>Legacy. The pi executable path is controller-owned and not read at launch.</summary>
    public string? PiExecutablePath { get; init; }

    /// <summary>Legacy. The controller callback URL is controller-owned and not read at launch.</summary>
    public string? ControllerBaseUrl { get; init; }

    /// <summary>Legacy. The PTY wrapper path is controller-owned and not read at launch.</summary>
    public string? PtyWrapperPath { get; init; }

    /// <summary>Legacy. The PTY wrapper arguments are controller-owned and not read at launch.</summary>
    public string? PtyWrapperArgs { get; init; }

    /// <summary>Pi-materia loadout selected for each execution kind. User-level, profile-specific.</summary>
    public IReadOnlyDictionary<ExecutionKind, string> Loadouts { get; init; } =
        new Dictionary<ExecutionKind, string>
        {
            [ExecutionKind.NewWork] = "ADO-Build-NewWork",
            [ExecutionKind.Rework] = "ADO-Build-Rework",
        };

    /// <summary>Legacy. Environment-variable forwarding is controller-owned and not read at launch.</summary>
    public IReadOnlyDictionary<string, string> ForwardEnvironmentVariables { get; init; } =
        new Dictionary<string, string>();
}
