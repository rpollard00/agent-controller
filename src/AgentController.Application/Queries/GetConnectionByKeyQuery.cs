namespace AgentController.Application.Queries;

/// <summary>Reads a managed connection profile by its immutable key.</summary>
public sealed record GetConnectionByKeyQuery(string Key);
