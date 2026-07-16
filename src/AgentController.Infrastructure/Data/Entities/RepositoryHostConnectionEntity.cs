namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// Persisted managed repository host connection profile. Secret values are
/// deliberately absent; only the secret reference (kind + id) is stored.
/// </summary>
internal sealed class RepositoryHostConnectionEntity
{
    public string Key { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string Provider { get; set; } = "AzureDevOpsRepos";

    public string OrganizationUrl { get; set; } = string.Empty;

    public string Project { get; set; } = string.Empty;

    /// <summary>Secret reference kind (e.g. "EnvVar", "Db").</summary>
    public string PersonalAccessTokenReferenceKind { get; set; } = string.Empty;

    /// <summary>Secret reference identifier (e.g. environment variable name or database id).</summary>
    public string PersonalAccessTokenReferenceId { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
