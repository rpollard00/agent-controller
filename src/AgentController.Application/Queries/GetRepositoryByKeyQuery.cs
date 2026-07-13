namespace AgentController.Application.Queries;

/// <summary>Reads a managed repository profile by its immutable key.</summary>
public sealed record GetRepositoryByKeyQuery(string Key);
