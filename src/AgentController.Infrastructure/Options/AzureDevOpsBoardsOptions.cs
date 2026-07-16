using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Domain;

namespace AgentController.Infrastructure.Options;

/// <summary>
/// Azure DevOps Boards connection and authentication configuration.
/// Section: "azureDevOps"
///
/// The organization URL and project are carried on
/// <see cref="WorkSourceOptions"/> (section "workSource").
/// This section holds only the auth/connection settings needed
/// to authenticate REST API calls.
/// </summary>
public sealed class AzureDevOpsBoardsOptions : IAzureDevOpsBoardsOptions
{
    public const string SectionName = "azureDevOps";

    /// <summary>
    /// Azure DevOps Personal Access Token (PAT) for REST API authentication.
    /// Supports the "ENV:VARIABLE_NAME" prefix to read from an environment variable.
    /// If the value starts with "ENV:", the remainder is treated as an environment
    /// variable name whose value is the actual PAT.
    ///
    /// Validation is deferred to <see cref="AzureDevOpsBoardsValidator.Validate"/>,
    /// which runs only when the work source provider is "AzureDevOpsBoards".
    /// This avoids spurious validation-on-start failures when a different provider
    /// is configured.
    /// </summary>
    public string PersonalAccessToken { get; init; } = string.Empty;

    /// <summary>
    /// Base URL for the Azure DevOps REST API.
    /// Derived from <see cref="WorkSourceOptions.OrganizationUrl"/>
    /// (e.g. "https://dev.azure.com/myorg").
    /// Required when the work source provider is "AzureDevOpsBoards".
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Project name for the Azure DevOps Boards project.
    /// Derived from <see cref="WorkSourceOptions.Project"/>.
    /// Required when the work source provider is "AzureDevOpsBoards".
    /// </summary>
    public string? Project { get; set; }

    /// <summary>
    /// Resolves the effective PAT value by expanding "ENV:VARIABLE_NAME" references.
    /// Returns null when the configured value is empty or whitespace.
    /// Throws <see cref="InvalidOperationException"/> when an ENV: reference points
    /// to a missing or empty environment variable.
    ///
    /// This synchronous method uses <c>Environment.GetEnvironmentVariable</c> directly
    /// for backward compatibility. For new managed profiles, prefer
    /// <see cref="ResolvePersonalAccessTokenAsync"/> which routes through
    /// <see cref="IManagedSecretStore"/>.
    /// </summary>
    public string? ResolvePersonalAccessToken()
    {
        var configured = PersonalAccessToken;
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        const string envPrefix = "ENV:";
        if (configured.StartsWith(envPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var envName = configured[envPrefix.Length..];
            if (string.IsNullOrWhiteSpace(envName))
                throw new InvalidOperationException(
                    $"Azure DevOps PAT is configured as 'ENV:' but no environment variable name was provided.");

            var envValue = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrWhiteSpace(envValue))
                throw new InvalidOperationException(
                    $"Azure DevOps PAT references environment variable '{envName}' " +
                    $"which is missing or empty.");

            return envValue;
        }

        return configured;
    }

    /// <summary>
    /// Resolves the effective PAT value asynchronously, routing through
    /// <see cref="IManagedSecretStore"/> for "ENV:VARIABLE_NAME" references.
    ///
    /// For legacy "ENV:NAME" values, converts to a <see cref="SecretReference"/>
    /// of kind "EnvVar" and resolves through the secret store. For direct PAT
    /// values, returns them as-is. This provides backward compatibility for
    /// existing deployments while allowing new managed profiles to store
    /// a <see cref="SecretReference"/> instead.
    /// </summary>
    /// <param name="secretStore">
    /// The secret store to use for resolving "ENV:" references.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token.
    /// </param>
    public async Task<string?> ResolvePersonalAccessTokenAsync(
        IManagedSecretStore secretStore,
        CancellationToken cancellationToken
    )
    {
        var configured = PersonalAccessToken;
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        const string envPrefix = "ENV:";
        if (configured.StartsWith(envPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var envName = configured[envPrefix.Length..];
            if (string.IsNullOrWhiteSpace(envName))
                throw new InvalidOperationException(
                    $"Azure DevOps PAT is configured as 'ENV:' but no environment variable name was provided.");

            // Route through IManagedSecretStore for backward-compatible EnvVar resolution.
            var reference = SecretReference.EnvironmentVariable(envName.Trim());
            var resolved = await secretStore.ResolveAsync(reference, cancellationToken);
            if (string.IsNullOrWhiteSpace(resolved))
                throw new InvalidOperationException(
                    $"Azure DevOps PAT references environment variable '{envName}' " +
                    $"which is missing or empty.");

            return resolved;
        }

        // Direct PAT value — return as-is.
        return configured;
    }
}
