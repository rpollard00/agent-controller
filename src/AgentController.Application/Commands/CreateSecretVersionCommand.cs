namespace AgentController.Application.Commands;

/// <summary>Creates a new version of an existing named secret.</summary>
public sealed record CreateSecretVersionCommand(
    /// <summary>The secret name.</summary>
    string Name,

    /// <summary>The new plaintext value (write-only).</summary>
    string Value
);
