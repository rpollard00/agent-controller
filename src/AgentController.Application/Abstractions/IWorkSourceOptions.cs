namespace AgentController.Application.Abstractions;

/// <summary>
/// Minimal abstraction over work-source configuration options.
/// Allows the Application layer to read organization/project settings
/// without depending on the Infrastructure options types directly.
/// </summary>
public interface IWorkSourceOptions
{
    /// <summary>Azure DevOps organization URL, or <c>null</c> if not configured.</summary>
    string? OrganizationUrl { get; }

    /// <summary>Azure DevOps project name, or <c>null</c> if not configured.</summary>
    string? Project { get; }
}
