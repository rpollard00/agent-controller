using AgentController.Domain;

namespace AgentController.Application.Commands;

/// <summary>Creates a managed repository host connection profile.</summary>
public sealed record CreateRepositoryHostConnectionCommand(RepositoryHostConnectionProfile Profile);
