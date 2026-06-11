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
    /// </summary>
    [Required]
    public string ConnectionString { get; init; } = "Data Source=agent-controller.db";
}
