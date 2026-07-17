using AgentController.Domain;

namespace AgentController.Application.Commands;

/// <summary>
/// Request to onboard a repository by selecting it from a connected repository host.
/// </summary>
public sealed record OnboardRepositoryFromHostCommand(
    /// <summary>Key of the unified connection to use.</summary>
    string ConnectionKey,

    /// <summary>Provider-specific project name to scope the repository enumeration.</summary>
    string Project,

    /// <summary>
    /// Provider-specific repository identifier (e.g. ADO repo GUID) to select.
    /// </summary>
    string RepositoryId,

    /// <summary>
    /// Optional stable key for the new repository profile. If not provided,
    /// a key is derived from the repository name.
    /// </summary>
    string? RepositoryKey
);
