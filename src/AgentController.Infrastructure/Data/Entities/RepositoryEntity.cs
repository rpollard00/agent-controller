namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// EF Core entity for the Repositories table.
/// Maps to the prototype data model defined in the architecture (§7.5).
/// AllowedPaths is stored as a JSON string column.
/// </summary>
internal sealed class RepositoryEntity
{
    /// <summary>Unique key for this repository profile (PK).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Remote URL to clone.</summary>
    public string CloneUrl { get; set; } = string.Empty;

    /// <summary>Default branch to check out after cloning.</summary>
    public string DefaultBranch { get; set; } = "main";

    /// <summary>Name of the environment profile to use for this repository.</summary>
    public string EnvironmentProfile { get; set; } = string.Empty;

    /// <summary>Name of the runtime profile to use for this repository.</summary>
    public string RuntimeProfile { get; set; } = string.Empty;

    /// <summary>Allowed paths serialized as a JSON array.</summary>
    public string? AllowedPathsJson { get; set; }

    /// <summary>When the record was first persisted.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the record was last mutated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
