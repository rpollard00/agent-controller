namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// Persisted managed runtime environment profile. Environment-variable forwarding
/// is represented only by target-to-source variable names; resolved values and
/// credentials are deliberately absent.
/// </summary>
internal sealed class RuntimeEnvironmentEntity
{
    public string Key { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string EnvironmentProvider { get; set; } = string.Empty;

    public string? WorkspaceRoot { get; set; }

    public string RuntimeProvider { get; set; } = string.Empty;

    public string? PiExecutablePath { get; set; }

    public string? ControllerBaseUrl { get; set; }

    public string? PtyWrapperPath { get; set; }

    public string? PtyWrapperArgs { get; set; }

    public string LoadoutsJson { get; set; } = "{}";

    public string ForwardEnvironmentVariablesJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
