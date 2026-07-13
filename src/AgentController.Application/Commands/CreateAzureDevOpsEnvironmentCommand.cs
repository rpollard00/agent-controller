using AgentController.Domain;

namespace AgentController.Application.Commands;

/// <summary>Creates a managed Azure DevOps environment profile.</summary>
public sealed record CreateAzureDevOpsEnvironmentCommand(AzureDevOpsEnvironmentProfile Profile);
