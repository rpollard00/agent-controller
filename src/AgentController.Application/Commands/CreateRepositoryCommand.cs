using AgentController.Domain;

namespace AgentController.Application.Commands;

/// <summary>Creates a managed repository onboarding profile.</summary>
public sealed record CreateRepositoryCommand(RepositoryProfile Profile);
