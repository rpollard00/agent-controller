using AgentController.Domain;

namespace AgentController.Application.Commands;

/// <summary>Updates a managed connection profile by its immutable key.</summary>
public sealed record UpdateConnectionCommand(string Key, ConnectionProfile Profile);
