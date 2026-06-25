using System.ComponentModel.DataAnnotations;

namespace AgentController.Infrastructure.Options;

/// <summary>
/// Per-repository profile configuration.
/// Maps a repository key to clone details and associated profiles.
/// Section element within "repositories".
/// </summary>
public sealed class RepositoryProfileOptions
{
    /// <summary>
    /// Remote URL from which to clone the repository.
    /// Must be a non-empty URL (e.g. HTTPS, SSH git@, or local path).
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string CloneUrl { get; init; } = string.Empty;

    /// <summary>
    /// Default branch to check out after cloning.
    /// </summary>
    public string DefaultBranch { get; init; } = "main";

    /// <summary>
    /// Name of the environment profile to use for runs targeting this repository.
    /// </summary>
    public string EnvironmentProfile { get; init; } = string.Empty;

    /// <summary>
    /// Name of the runtime profile to use for runs targeting this repository.
    /// </summary>
    public string RuntimeProfile { get; init; } = string.Empty;

    /// <summary>
    /// Allowed paths within the repository that the agent may modify.
    /// An empty list means no path restrictions.
    /// </summary>
    public IReadOnlyList<string> AllowedPaths { get; init; } = [];
}
