namespace AgentController.Domain.Secrets;

/// <summary>
/// Metadata for a single secret version (no plaintext passwords, private keys, or passphrases).
/// For SSH-key secrets, <see cref="PublicKey"/> is populated so operators can inspect
/// the active public key; for PAT secrets it is always <c>null</c>.
/// </summary>
public sealed record SecretVersionInfo(
    /// <summary>Monotonically increasing version number (1-based).</summary>
    int Version,

    /// <summary>When this version was created.</summary>
    DateTimeOffset CreatedAt,

    /// <summary>
    /// The stable secret-type discriminator (e.g. <c>"personal-access-token"</c> or <c>"ssh-key"</c>).
    /// </summary>
    string SecretType,

    /// <summary>
    /// For SSH-key secrets, the public key material safe for display.
    /// <c>null</c> for PAT secrets.
    /// </summary>
    string? PublicKey = null
);

/// <summary>
/// Metadata for a named secret (no plaintext passwords, private keys, or passphrases).
/// </summary>
public sealed record SecretInfo(
    /// <summary>The unique secret name.</summary>
    string Name,

    /// <summary>Current (latest) version number.</summary>
    int LatestVersion,

    /// <summary>When the secret was first created.</summary>
    DateTimeOffset CreatedAt,

    /// <summary>When the latest version was created.</summary>
    DateTimeOffset UpdatedAt,

    /// <summary>
    /// The stable secret-type discriminator (e.g. <c>"personal-access-token"</c> or <c>"ssh-key"</c>).
    /// </summary>
    string SecretType
);

/// <summary>
/// Provider-neutral admin port for managing named, versioned secrets.
/// 
/// This is the management path: create secrets, create new versions,
/// and list secrets/versions. Stored values are never decrypted for display.
/// </summary>
public interface ISecretManager
{
    /// <summary>
    /// Create a new secret with an initial typed payload (version 1).
    /// </summary>
    /// <param name="name">The unique secret name.</param>
    /// <param name="payload">
    /// The typed secret payload (write-only, never returned). Type is inferred from the
    /// concrete <see cref="SecretPayload"/> subtype; the subtype must match the secret's
    /// immutable type after creation.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> if the secret was created; <c>false</c> if a secret with that name already exists.
    /// </returns>
    Task<bool> CreateAsync(
        string name,
        SecretPayload payload,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create a new version of an existing secret.
    /// </summary>
    /// <param name="name">The secret name.</param>
    /// <param name="payload">
    /// The typed secret payload (write-only, never returned). The payload type must match
    /// the secret's immutable type.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The version number of the newly created version, or <c>null</c> if the secret does not exist.
    /// </returns>
    Task<int?> CreateVersionAsync(
        string name,
        SecretPayload payload,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a secret and all of its versions.
    /// </summary>
    /// <param name="name">The secret name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> if the secret (and all of its versions) was removed;
    /// <c>false</c> if no secret with that name exists.
    /// Stored plaintext values are never exposed or returned.
    /// </returns>
    Task<bool> DeleteAsync(
        string name,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// List all secrets by name (metadata only, no values).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ordered list of secret metadata.</returns>
    Task<IReadOnlyList<SecretInfo>> ListAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// List all versions of a secret (metadata only, no values).
    /// </summary>
    /// <param name="name">The secret name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Ordered list of version metadata (oldest first), or <c>null</c> if the secret does not exist.
    /// </returns>
    Task<IReadOnlyList<SecretVersionInfo>?> ListVersionsAsync(
        string name,
        CancellationToken cancellationToken = default
    );
}
