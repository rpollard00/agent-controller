using AgentController.Domain;

namespace AgentController.Application.Commands;

/// <summary>Creates a managed runtime environment profile.</summary>
public sealed record CreateRuntimeEnvironmentCommand(RuntimeEnvironmentProfile Profile);
