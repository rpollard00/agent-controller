using AgentController.Domain;

namespace AgentController.Application.Results;

/// <summary>
/// Hint for which clone transport mechanism the host prefers.
/// </summary>
public enum CloneTransportHint
{
    /// <summary>No preference indicated.</summary>
    Unspecified = 0,

    /// <summary>SSH-based clone (e.g. git@host:org/repo.git).</summary>
    Ssh = 1,

    /// <summary>HTTPS with personal access token (e.g. https://pat@dev.azure.com/org/repo).</summary>
    HttpsPat = 2
}

/// <summary>
/// Provider-neutral repository descriptor returned by
/// <see cref="Abstractions.IConnection.ListRepositoriesAsync"/> to populate
/// UI dropdowns during repository onboarding.
/// </summary>
public sealed record HostRepository(
    /// <summary>Provider-specific repository identifier (e.g. ADO repo GUID).</summary>
    string Id,

    /// <summary>Human-readable repository name.</summary>
    string Name,

    /// <summary>Default branch name (e.g. "main").</summary>
    string DefaultBranch,

    /// <summary>
    /// Remote URL for cloning.
    /// May be a placeholder if the host does not expose the full URL.
    /// </summary>
    string RemoteUrl,

    /// <summary>
    /// Hint for which clone transport the host prefers.
    /// </summary>
    CloneTransportHint CloneTransportHint
);
