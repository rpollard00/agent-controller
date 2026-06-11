using System.ComponentModel.DataAnnotations;

namespace AgentController.Infrastructure.Options;

/// <summary>
/// Environment provider configuration.
/// Section: "environmentProvider"
/// </summary>
public sealed class EnvironmentProviderOptions
{
    public const string SectionName = "environmentProvider";

    /// <summary>
    /// Provider identifier (e.g. "LocalWorkspace", "Docker").
    /// </summary>
    [Required]
    public string Provider { get; init; } = string.Empty;
}
