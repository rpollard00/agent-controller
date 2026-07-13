namespace AgentController.Application.Queries;

/// <summary>Reads a managed Azure DevOps environment profile by its immutable key.</summary>
public sealed record GetAzureDevOpsEnvironmentByKeyQuery(string Key);
