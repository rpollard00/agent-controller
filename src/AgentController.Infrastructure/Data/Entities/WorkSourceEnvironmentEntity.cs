namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// Persisted managed work-source environment profile. Credential values are
/// deliberately absent; only the name of the environment variable containing
/// the PAT is stored.
/// </summary>
internal sealed class WorkSourceEnvironmentEntity : BaseConnectionEntity
{
    /// <inheritdoc />
    public new string Provider { get; set; } = "AzureDevOpsBoards";

    public string TagPrefix { get; set; } = "agent";

    public string? ActiveState { get; set; }

    public string? CompletedState { get; set; }

    public string PatEnvironmentVariable { get; set; } = string.Empty;
}
