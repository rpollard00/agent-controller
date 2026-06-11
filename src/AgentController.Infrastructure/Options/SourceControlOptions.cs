using System.ComponentModel.DataAnnotations;

namespace AgentController.Infrastructure.Options;

/// <summary>
/// Source-control provider configuration.
/// Section: "sourceControl"
/// </summary>
public sealed class SourceControlOptions
{
    public const string SectionName = "sourceControl";

    /// <summary>
    /// Provider identifier (e.g. "AzureDevOpsRepos", "LocalFake").
    /// </summary>
    [Required]
    public string Provider { get; init; } = string.Empty;
}
