namespace AgentController.Application.Queries;

/// <summary>Reads a managed runtime environment profile by its immutable key.</summary>
public sealed record GetRuntimeEnvironmentByKeyQuery(string Key);
