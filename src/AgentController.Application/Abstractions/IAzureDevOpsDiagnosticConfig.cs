namespace AgentController.Application.Abstractions;

/// <summary>
/// Provides the configuration values required for the Azure DevOps diagnostic query.
/// This abstraction allows the Application layer to read work-source and boards
/// configuration without depending on Infrastructure options types directly.
/// </summary>
public interface IAzureDevOpsDiagnosticConfig
{
    /// <summary>Azure DevOps organization URL, or <c>null</c> if not configured.</summary>
    string? OrganizationUrl { get; }

    /// <summary>Azure DevOps project name, or <c>null</c> if not configured.</summary>
    string? Project { get; }

    /// <summary>
    /// Resolves the effective PAT value.
    /// Returns <c>null</c> when not configured.
    /// Throws <see cref="InvalidOperationException"/> when an ENV: reference is missing.
    /// </summary>
    string? ResolvePersonalAccessToken();
}
