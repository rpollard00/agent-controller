namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// Persisted managed work-source environment profile.
/// The PAT is referenced by a named, versioned secret resolved at runtime
/// via <see cref="Domain.Secrets.ISecretStore"/>.
/// </summary>
internal sealed class WorkSourceEnvironmentEntity : BaseConnectionEntity
{
    /// <inheritdoc />
    public new string Provider { get; set; } = "AzureDevOpsBoards";

    public string TagPrefix { get; set; } = "agent";

    public string? ActiveState { get; set; }

    public string? CompletedState { get; set; }

    /// <summary>Secret name for PAT resolution via ISecretStore.</summary>
    public string PersonalAccessTokenSecretName { get; set; } = string.Empty;

    /// <summary>Optional version pin for the PAT secret.</summary>
    public int? PersonalAccessTokenSecretVersion { get; set; }
}
