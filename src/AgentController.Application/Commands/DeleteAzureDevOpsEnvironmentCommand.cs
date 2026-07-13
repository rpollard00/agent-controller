namespace AgentController.Application.Commands;

/// <summary>Deletes a managed Azure DevOps environment by its immutable key.</summary>
public sealed record DeleteAzureDevOpsEnvironmentCommand(string Key);
