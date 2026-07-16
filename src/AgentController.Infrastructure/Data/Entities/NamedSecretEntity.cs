namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// EF Core entity for a named, versioned secret.
/// Each row represents a secret identified by a unique name with an ordered set of versions.
/// </summary>
internal sealed class NamedSecretEntity
{
    /// <summary>Primary key.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Unique human-readable name for the secret.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When the secret was first created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Ordered collection of versions for this secret.</summary>
    public ICollection<SecretVersionEntity> Versions { get; set; } = new List<SecretVersionEntity>();
}
