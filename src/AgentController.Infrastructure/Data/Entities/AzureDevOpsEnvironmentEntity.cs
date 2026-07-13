namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// Persisted managed Azure DevOps environment profile. Credential values are
/// deliberately absent; only the name of the environment variable containing
/// the PAT is stored.
/// </summary>
internal sealed class AzureDevOpsEnvironmentEntity
{
    public string Key { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string OrganizationUrl { get; set; } = string.Empty;

    public string Project { get; set; } = string.Empty;

    public string WorkItemType { get; set; } = string.Empty;

    public string EligibleTagsJson { get; set; } = "[]";

    public string ExcludedTagsJson { get; set; } = "[]";

    public string EligibleStatesJson { get; set; } = "[]";

    public string ExcludedStatesJson { get; set; } = "[]";

    public string? ActiveState { get; set; }

    public string? CompletedState { get; set; }

    public string PatEnvironmentVariable { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
