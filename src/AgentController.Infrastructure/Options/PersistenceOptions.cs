using System.ComponentModel.DataAnnotations;

namespace AgentController.Infrastructure.Options;

/// <summary>
/// Persistence layer configuration.
/// Section: "persistence"
/// </summary>
public sealed class PersistenceOptions
{
    public const string SectionName = "persistence";

    /// <summary>
    /// Provider identifier (e.g. "Sqlite", "Postgres").
    /// </summary>
    [Required]
    public string Provider { get; init; } = "Sqlite";

    /// <summary>
    /// Connection string or data source path.
    /// Must be explicitly configured; there is no fallback default.
    /// </summary>
    [Required]
    public string ConnectionString { get; init; } = string.Empty;
}
