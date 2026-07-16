namespace AgentController.Application.Commands;

/// <summary>Deletes a managed repository host connection by its immutable key.</summary>
public sealed record DeleteRepositoryHostConnectionCommand(string Key);
