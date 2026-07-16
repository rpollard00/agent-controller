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
    /// </summary>
    public static void Validate(
        WorkSourceOptions workSource,
        AzureDevOpsBoardsOptions boards)
    {
        var failures = new List<string>();

        // Organization URL
        if (string.IsNullOrWhiteSpace(workSource.OrganizationUrl))
        {
            failures.Add(
                "Azure DevOps organization URL is required. " +
                "Configure 'workSource:organizationUrl' (e.g. 'https://dev.azure.com/myorg').");
        }
        else if (!Uri.TryCreate(workSource.OrganizationUrl, UriKind.Absolute, out var uri)
                 || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            failures.Add(
                $"Azure DevOps organization URL '{workSource.OrganizationUrl}' is not a valid " +
                "absolute URL with an http or https scheme.");
        }

        // Project
        if (string.IsNullOrWhiteSpace(workSource.Project))
        {
            failures.Add(
                "Azure DevOps project name is required. " +
                "Configure 'workSource:project'.");
        }

        // Personal Access Token
        string? resolvedPat = null;
        try
        {
            resolvedPat = boards.ResolvePersonalAccessToken();
        }
        catch (InvalidOperationException ex)
        {
            failures.Add(ex.Message);
        }

        if (resolvedPat is null)
        {
            failures.Add(
                "Azure DevOps PAT secret name is required. " +
                "Configure 'azureDevOps:personalAccessToken' with the name of a named secret " +
                "holding the PAT (resolved at runtime via ISecretStore).");
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
    /// </summary>
    public static async Task ValidateAsync(
        WorkSourceOptions workSource,
        AzureDevOpsBoardsOptions boards,
        Domain.Secrets.ISecretStore secretStore,
        CancellationToken cancellationToken
    )
    {
        var failures = new List<string>();

        // Organization URL
        if (string.IsNullOrWhiteSpace(workSource.OrganizationUrl))
        {
            failures.Add(
                "Azure DevOps organization URL is required. " +
                "Configure 'workSource:organizationUrl' (e.g. 'https://dev.azure.com/myorg').");
        }
        else if (!Uri.TryCreate(workSource.OrganizationUrl, UriKind.Absolute, out var uri)
                 || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            failures.Add(
                $"Azure DevOps organization URL '{workSource.OrganizationUrl}' is not a valid " +
                "absolute URL with an http or https scheme.");
        }

        // Project
        if (string.IsNullOrWhiteSpace(workSource.Project))
        {
            failures.Add(
                "Azure DevOps project name is required. " +
                "Configure 'workSource:project'.");
        }

        // Personal Access Token — resolved through ISecretStore.
        string? resolvedPat = null;
        try
        {
            resolvedPat = await boards.ResolvePersonalAccessTokenAsync(
                secretStore,
                cancellationToken
            );
        }
        catch (InvalidOperationException ex)
        {
            failures.Add(ex.Message);
        }

        if (resolvedPat is null)
        {
            failures.Add(
                "Azure DevOps PAT could not be resolved. " +
                "Ensure 'azureDevOps:personalAccessToken' contains the name of a named secret " +
                "that exists in the secret store and holds a valid PAT.");
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Azure DevOps Boards configuration is invalid:\n" +
                string.Join("\n", failures.Select((f, i) => $"  {i + 1}. {f}")));
        }
    }
}
