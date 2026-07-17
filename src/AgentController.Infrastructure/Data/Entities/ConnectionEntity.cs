namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// EF Core entity for unified, provider-discriminated connection profiles.
/// 
/// Maps to the "Connections" table. A single org-level connection carries
/// one or more capability flags and provider-specific settings as JSON.
/// Consumer profiles (work sources, repositories) reference this via Key.
/// </summary>
internal sealed class ConnectionEntity
{
    /// <summary>Stable key used by consumer profiles to reference this connection.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Human-readable name shown to operators.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Whether the connection may be used by consumers.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Provider discriminator (e.g. "AzureDevOps").</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Bitmask of <see cref="AgentController.Domain.ConnectionCapability"/> values.
    /// Repositories=1, WorkTracking=2, ExecutionHost=4.
    /// </summary>
    public int Capabilities { get; set; }

    /// <summary>
    /// Provider-specific settings stored as JSON.
    /// Deserialized to the concrete <see cref="AgentController.Domain.ConnectionSettings"/>
    /// subtype matching <see cref="Provider"/>.
    /// </summary>
    public string? ProviderSettingsJson { get; set; }

    /// <summary>When the connection profile was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the connection profile was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
