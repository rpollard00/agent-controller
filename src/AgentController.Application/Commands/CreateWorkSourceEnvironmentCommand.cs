using AgentController.Domain;

namespace AgentController.Application.Commands;

/// <summary>Creates a managed work source environment profile.</summary>
public sealed record CreateWorkSourceEnvironmentCommand(WorkSourceEnvironmentProfile Profile);
