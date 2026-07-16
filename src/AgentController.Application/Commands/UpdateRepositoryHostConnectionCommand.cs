using AgentController.Domain;

namespace AgentController.Application.Commands;

/// <summary>
/// Updates a managed repository host connection. <see cref="Key"/> identifies the existing profile
/// and must match the immutable key in <see cref="Profile"/>.
/// </summary>
public sealed record UpdateRepositoryHostConnectionCommand(
    string Key,
    RepositoryHostConnectionProfile Profile
);
