namespace AgentController.Application.Queries;

/// <summary>Reads a managed repository host connection profile by its immutable key.</summary>
public sealed record GetRepositoryHostConnectionByKeyQuery(string Key);
