using AgentController.Domain;

namespace AgentController.Application.Commands;

/// <summary>Creates a managed connection profile.</summary>
public sealed record CreateConnectionCommand(ConnectionProfile Profile);
