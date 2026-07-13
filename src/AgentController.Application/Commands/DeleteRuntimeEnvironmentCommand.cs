namespace AgentController.Application.Commands;

/// <summary>Deletes a managed runtime environment by its immutable key.</summary>
public sealed record DeleteRuntimeEnvironmentCommand(string Key);
