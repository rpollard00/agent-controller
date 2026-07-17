namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// Persisted managed repository host connection profile. Secret values are
/// deliberately absent; only the named-secret reference is stored.
/// </summary>
internal sealed class RepositoryHostConnectionEntity : BaseConnectionEntity
{
    /// <inheritdoc />
    public new string Provider { get; set; } = "AzureDevOpsRepos";

    /// <summary>Azure DevOps project name (provider: AzureDevOpsRepos).</summary>
    public string Project { get; set; } = string.Empty;

    /// <summary>
    /// Named secret reference for the PAT. Resolved at runtime via
    /// <see cref="AgentController.Domain.Secrets.ISecretStore"/>.
    /// </summary>
    public string PersonalAccessTokenSecretName { get; set; } = string.Empty;
}
