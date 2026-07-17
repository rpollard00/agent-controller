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
    /// Name of the named, envelope-encrypted secret holding the Azure DevOps
    /// Personal Access Token (PAT) for REST API authentication.
    ///
    /// The secret value is resolved at runtime via <see cref="Domain.Secrets.ISecretStore"/>
    /// using this property as the secret name.
    ///
    /// Validation is deferred to <see cref="AzureDevOpsBoardsValidator.Validate"/>,
    /// which runs only when the work source provider is "AzureDevOpsBoards".
    /// This avoids spurious validation-on-start failures when a different provider
    /// is configured.
    /// </summary>
    public string PersonalAccessToken { get; init; } = string.Empty;

    /// <summary>
    /// Base URL for the Azure DevOps REST API.
    /// Derived from the resolved ConnectionProfile's AzureDevOps settings
    /// (e.g. "https://dev.azure.com/myorg").
    /// Required when the work source provider is "AzureDevOpsBoards".
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Project name for the Azure DevOps Boards project.
    /// Derived from <see cref="WorkSourceOptions.Project"/> (consumer-level).
    /// Required when the work source provider is "AzureDevOpsBoards".
    /// </summary>
    public string? Project { get; set; }

    /// <summary>
    /// Returns the configured secret name for the PAT.
    /// Returns null when the configured value is empty or whitespace.
    ///
    /// This does NOT resolve the actual PAT value — use
    /// <see cref="ResolvePersonalAccessTokenAsync"/> with an
    /// <see cref="Domain.Secrets.ISecretStore"/> to obtain the plaintext PAT.
    /// This sync method is suitable for presence checks (e.g., "is a secret
    /// name configured?") but not for obtaining the credential itself.
    /// </summary>
    public string? ResolvePersonalAccessToken()
    {
        var configured = PersonalAccessToken;
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        return configured;
    }

    /// <summary>
    /// Resolves the PAT value asynchronously by looking up the named secret
    /// through <see cref="Domain.Secrets.ISecretStore"/>.
    /// Returns null when the configured secret name is empty or whitespace,
    /// or when the secret does not exist in the store.
    /// </summary>
    /// <param name="secretStore">
    /// The provider-neutral secret store used to resolve the named secret.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token.
    /// </param>
    public Task<string?> ResolvePersonalAccessTokenAsync(
        Domain.Secrets.ISecretStore secretStore,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var configured = PersonalAccessToken;
        if (string.IsNullOrWhiteSpace(configured))
            return Task.FromResult<string?>(null);

        // Resolve the named secret through ISecretStore.
        return secretStore.ResolveAsync(configured.Trim(), cancellationToken: cancellationToken);
    }
}
