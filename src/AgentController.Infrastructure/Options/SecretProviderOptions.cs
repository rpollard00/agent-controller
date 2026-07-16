using System.ComponentModel.DataAnnotations;

namespace AgentController.Infrastructure.Options;

/// <summary>
/// Secret provider selection configuration.
/// Section: "secrets"
/// </summary>
public sealed class SecretProviderOptions
{
    public const string SectionName = "secrets";

    /// <summary>
    /// Provider identifier for the active secret store.
    /// Supported values: "Db" (default).
    /// </summary>
    [Required]
    public string Provider { get; init; } = "Db";
}
