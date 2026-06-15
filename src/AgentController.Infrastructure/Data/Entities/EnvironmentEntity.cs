namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// EF Core entity for the Environments table.
/// Maps to the prototype data model defined in the architecture (§7.5).
/// </summary>
internal sealed class EnvironmentEntity
{
    /// <summary>Store-assigned environment identifier (PK).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Provider type that created this environment.</summary>
    public string ProviderType { get; set; } = string.Empty;

    /// <summary>Run identifier this environment is associated with.</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>Absolute root path of the environment on the host.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Current status of the environment.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Provider-specific metadata serialized as JSON.</summary>
    public string? MetadataJson { get; set; }

    /// <summary>When the environment was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the environment was destroyed, if applicable.</summary>
    public DateTimeOffset? DestroyedAt { get; set; }

    /// <summary>When the record was last mutated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
