using System.ComponentModel.DataAnnotations.Schema;

namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// Persisted managed work-source environment profile.
/// Organization URL and PAT are carried on the referenced <see cref="ConnectionEntity"/>;
/// this entity holds only the consumer-level <see cref="Project"/> and board-usage settings.
/// </summary>
internal sealed class WorkSourceEnvironmentEntity : BaseConnectionEntity
{
    /// <inheritdoc />
    public new string Provider { get; set; } = "AzureDevOpsBoards";

    /// <summary>
    /// Key of the unified <see cref="ConnectionEntity"/> this work source references.
    /// </summary>
    public string ConnectionKey { get; set; } = string.Empty;

    /// <summary>
    /// Overrides the base <see cref="BaseConnectionEntity.OrganizationUrl"/> so it is
    /// NOT mapped to the WorkSourceEnvironments table (org URL lives on the connection).
    /// </summary>
    [NotMapped]
    public override string OrganizationUrl => base.OrganizationUrl;

    /// <summary>Consumer-level project name (e.g. Azure DevOps project).</summary>
    public string Project { get; set; } = string.Empty;

    public string TagPrefix { get; set; } = "agent";

    public string? ActiveState { get; set; }

    public string? CompletedState { get; set; }
}
