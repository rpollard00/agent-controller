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

/// <summary>Structured settings for an agent runtime provider.</summary>
public sealed record RuntimeProviderSettings
{
    /// <summary>Path to the pi executable, or a command resolvable through PATH.</summary>
    public string? PiExecutablePath { get; init; } = "pi";

    /// <summary>Controller base URL used to construct runtime callback URLs.</summary>
    public string? ControllerBaseUrl { get; init; }

    /// <summary>Optional executable used to allocate a pseudo-terminal.</summary>
    public string? PtyWrapperPath { get; init; } = "script";

    /// <summary>Arguments passed to the pseudo-terminal wrapper.</summary>
    public string? PtyWrapperArgs { get; init; } = "-qfc";

    /// <summary>Pi-materia loadout selected for each execution kind.</summary>
    public IReadOnlyDictionary<ExecutionKind, string> Loadouts { get; init; } =
        new Dictionary<ExecutionKind, string>
        {
            [ExecutionKind.NewWork] = "ADO-Build-NewWork",
            [ExecutionKind.Rework] = "ADO-Build-Rework",
        };

    /// <summary>
    /// Target-to-source environment-variable names forwarded to the runtime.
    /// Values are source variable names, never resolved values or credentials.
    /// </summary>
    public IReadOnlyDictionary<string, string> ForwardEnvironmentVariables { get; init; } =
        new Dictionary<string, string>
        {
            ["AZURE_DEVOPS_EXT_PAT"] = "AZURE_DEVOPS_PAT",
            ["AZURE_DEVOPS_PAT"] = "AZURE_DEVOPS_PAT",
        };
}
