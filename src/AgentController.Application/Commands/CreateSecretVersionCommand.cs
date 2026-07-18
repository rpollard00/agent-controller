using AgentController.Domain.Secrets;

namespace AgentController.Application.Commands;

/// <summary>Creates a new version of an existing named secret with a typed payload.</summary>
/// <remarks>
/// The <see cref="Payload"/> type must match the secret's immutable type.
/// For SSH-key secrets this is a wholesale replacement — all fields
/// (private key, public key, passphrase) must be supplied.
/// </remarks>
public sealed record CreateSecretVersionCommand(
    /// <summary>The secret name.</summary>
    string Name,

    /// <summary>The typed payload for the new version (write-only, never returned).</summary>
    SecretPayload Payload
);
