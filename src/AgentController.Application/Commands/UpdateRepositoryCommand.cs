using AgentController.Domain;

namespace AgentController.Application.Commands;

/// <summary>
/// Updates the mutable fields of a managed repository profile. <see cref="Key"/> identifies the
/// existing profile and must match the immutable key in <see cref="Profile"/>.
/// </summary>
public sealed record UpdateRepositoryCommand(string Key, RepositoryProfile Profile);
