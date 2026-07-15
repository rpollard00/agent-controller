namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// Persisted managed work-source environment profile. Credential values are
/// deliberately absent; only the name of the environment variable containing
/// the PAT is stored.
/// </summary>
internal sealed class WorkSourceEnvironmentEntity
{
    public string Key { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string Provider { get; set; } = "AzureDevOpsBoards";

    public string TagPrefix { get; set; } = "agent";

    public string OrganizationUrl { get; set; } = string.Empty;

    public string Project { get; set; } = string.Empty;

    public string? ActiveState { get; set; }

    public string? CompletedState { get; set; }

    public string PatEnvironmentVariable { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
