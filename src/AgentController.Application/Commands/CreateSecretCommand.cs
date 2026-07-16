namespace AgentController.Application.Commands;

/// <summary>Creates a new named secret with an initial value.</summary>
public sealed record CreateSecretCommand(
    /// <summary>The unique secret name.</summary>
    string Name,

    /// <summary>The initial plaintext value (write-only).</summary>
    string Value
);
