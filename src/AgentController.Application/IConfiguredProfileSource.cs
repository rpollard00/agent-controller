using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Supplies backward-compatible controller profiles from static configuration.
/// Implementations map configuration into domain records without resolving secret values.
/// </summary>
public interface IConfiguredProfileSource
{
    /// <summary>Gets an appsettings repository profile, or <see langword="null"/> when absent.</summary>
    RepositoryProfile? GetRepository(string key);

    /// <summary>Builds the configured runtime fallback for a repository.</summary>
    RuntimeEnvironmentProfile GetRuntimeEnvironment(RepositoryProfile repository);

    /// <summary>Gets the configured work source fallback, or <see langword="null"/> when absent.</summary>
    WorkSourceEnvironmentProfile? GetAzureDevOpsEnvironment();
}
