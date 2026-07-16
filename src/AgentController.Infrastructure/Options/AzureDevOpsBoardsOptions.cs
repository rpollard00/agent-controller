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
    /// Resolves the effective PAT value.
    /// Returns null when the configured value is empty or whitespace.
    /// Returns the configured PAT value directly for non-empty values.
    ///
    /// For new managed profiles, prefer
    /// <see cref="ResolvePersonalAccessTokenAsync"/> which routes through
    /// <see cref="IManagedSecretStore"/>.
    /// </summary>
    public string? ResolvePersonalAccessToken()
    {
        var configured = PersonalAccessToken;
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        return configured;
    }

    /// <summary>
    /// Resolves the effective PAT value asynchronously.
    /// Returns null when the configured value is empty or whitespace.
    /// Returns the configured PAT value directly for non-empty values.
    /// </summary>
    /// <param name="secretStore">
    /// Reserved for future secret store integration.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token.
    /// </param>
    public Task<string?> ResolvePersonalAccessTokenAsync(
        IManagedSecretStore secretStore,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var configured = PersonalAccessToken;
        if (string.IsNullOrWhiteSpace(configured))
            return Task.FromResult<string?>(null);

        // Direct PAT value — return as-is.
        return Task.FromResult<string?>(configured);
    }
}
