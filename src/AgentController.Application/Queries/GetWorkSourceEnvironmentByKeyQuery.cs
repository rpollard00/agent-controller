namespace AgentController.Application.Queries;

/// <summary>Reads a managed work source environment profile by its immutable key.</summary>
public sealed record GetWorkSourceEnvironmentByKeyQuery(string Key);
