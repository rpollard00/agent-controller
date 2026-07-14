using AgentController.Domain;

namespace AgentController.Application.Commands;

/// <summary>
/// Updates a managed work source environment. <see cref="Key"/> identifies the existing profile
/// and must match the immutable key in <see cref="Profile"/>.
/// </summary>
public sealed record UpdateWorkSourceEnvironmentCommand(
    string Key,
    WorkSourceEnvironmentProfile Profile
);
