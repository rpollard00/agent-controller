namespace AgentController.Application.Commands;

/// <summary>Deletes a managed work source environment by its immutable key.</summary>
public sealed record DeleteWorkSourceEnvironmentCommand(string Key);
