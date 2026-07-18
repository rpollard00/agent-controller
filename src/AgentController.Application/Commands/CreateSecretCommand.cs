using AgentController.Domain.Secrets;

namespace AgentController.Application.Commands;

/// <summary>Creates a new named secret with an initial typed payload.</summary>
/// <remarks>
/// The <see cref="Payload"/> type discriminator determines the secret's immutable type.
/// Use <see cref="PersonalAccessTokenPayload"/> for PAT secrets or <see cref="SshKeyPayload"/>
/// for SSH-key secrets.
/// </remarks>
public sealed record CreateSecretCommand(
    /// <summary>The unique secret name.</summary>
    string Name,

    /// <summary>The initial typed payload (write-only, never returned).</summary>
    SecretPayload Payload
);
