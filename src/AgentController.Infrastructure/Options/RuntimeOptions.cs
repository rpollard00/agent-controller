using System.ComponentModel.DataAnnotations;

namespace AgentController.Infrastructure.Options;

/// <summary>
/// Agent runtime provider configuration.
/// Section: "runtime"
/// </summary>
public sealed class RuntimeOptions
{
    public const string SectionName = "runtime";

    /// <summary>
    /// Provider identifier (e.g. "PiMateria", "NoOp").
    /// </summary>
    [Required]
    public string Provider { get; init; } = string.Empty;

    /// <summary>
    /// Path to the pi executable (when using PiMateria runtime).
    /// </summary>
    public string? PiExecutablePath { get; init; }

    /// <summary>
    /// Default materia loadout name to pass to pi.
    /// </summary>
    public string? DefaultMateriaLoadout { get; init; }
}
