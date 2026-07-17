using AgentController.Application;
using AgentController.Infrastructure.Options;

namespace AgentController.Infrastructure;

/// <summary>
/// Validates Azure DevOps Boards configuration at startup.
/// Checks that all required settings are present and well-formed
/// before the application accepts requests.
/// </summary>
internal static class AzureDevOpsBoardsValidator
{
    /// <summary>
    /// Validates that all required Azure DevOps Boards settings are present
    /// and well-formed. Throws <see cref="InvalidOperationException"/> with a
    /// clear message on the first validation failure.
    ///
    /// OrganizationUrl and PAT are validated on the ConnectionProfile,
    /// not on WorkSourceOptions. This method validates consumer-level settings.
    /// </summary>
    public static void Validate(
        WorkSourceOptions workSource,
        AzureDevOpsBoardsOptions boards)
    {
        var failures = new List<string>();

        // Connection key (required for connection-based configuration)
        if (string.IsNullOrWhiteSpace(workSource.ConnectionKey))
        {
            failures.Add(
                "Azure DevOps connection key is required. " +
                "Configure 'workSource:connectionKey' with the key of an AzureDevOps connection.");
        }

        // Project
        if (string.IsNullOrWhiteSpace(workSource.Project))
        {
            failures.Add(
                "Azure DevOps project name is required. " +
                "Configure 'workSource:project'.");
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Azure DevOps Boards configuration is invalid:\n" +
                string.Join("\n", failures.Select((f, i) => $"  {i + 1}. {f}")));
        }
    }

    /// <summary>
    /// Validates Azure DevOps Boards configuration asynchronously, routing PAT
    /// resolution through <see cref="Domain.Secrets.ISecretStore"/>.
    /// Throws <see cref="InvalidOperationException"/> with a clear message on
    /// the first validation failure.
    ///
    /// OrganizationUrl and PAT are validated on the ConnectionProfile,
    /// not on WorkSourceOptions. This method validates consumer-level settings.
    /// </summary>
    public static async Task ValidateAsync(
        WorkSourceOptions workSource,
        AzureDevOpsBoardsOptions boards,
        Domain.Secrets.ISecretStore secretStore,
        CancellationToken cancellationToken
    )
    {
        var failures = new List<string>();

        // Connection key (required for connection-based configuration)
        if (string.IsNullOrWhiteSpace(workSource.ConnectionKey))
        {
            failures.Add(
                "Azure DevOps connection key is required. " +
                "Configure 'workSource:connectionKey' with the key of an AzureDevOps connection.");
        }

        // Project
        if (string.IsNullOrWhiteSpace(workSource.Project))
        {
            failures.Add(
                "Azure DevOps project name is required. " +
                "Configure 'workSource:project'.");
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Azure DevOps Boards configuration is invalid:\n" +
                string.Join("\n", failures.Select((f, i) => $"  {i + 1}. {f}")));
        }

        // Ensure async context is awaited.
        await Task.Yield();
    }
}
