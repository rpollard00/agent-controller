namespace AgentController.Application.Commands;

/// <summary>Deletes a managed connection profile by its immutable key.</summary>
public sealed record DeleteConnectionCommand(string Key);
