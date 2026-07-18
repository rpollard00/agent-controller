namespace AgentController.Application.Commands;

/// <summary>Deletes a named secret and all of its versions.</summary>
public sealed record DeleteSecretCommand(string Name);
