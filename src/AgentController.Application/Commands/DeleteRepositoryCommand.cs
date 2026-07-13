namespace AgentController.Application.Commands;

/// <summary>Deletes a managed repository profile by its immutable key.</summary>
public sealed record DeleteRepositoryCommand(string Key);
