namespace AgentController.Domain.Secrets;

/// <summary>
/// Provider-neutral read port for resolving typed secret payloads by name.
/// 
/// This is the runtime path used by consumers (e.g. work-source PAT resolution,
/// SSH-key-based cloning) to obtain a typed, plaintext secret payload.
/// It does not expose DB, encryption, or storage concerns.
/// 
/// The returned <see cref="SecretPayload"/> retains its concrete subtype so callers
/// can pattern-match on it (e.g. <c>is PersonalAccessTokenPayload</c> or
/// <c>is SshKeyPayload</c>) without needing a separate type lookup.
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Resolve the typed payload of a secret by name.
    /// </summary>
    /// <param name="name">
    /// The secret name to resolve.
    /// </param>
    /// <param name="version">
    /// Optional version number. When omitted, resolves to the latest version.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The typed secret payload, or <c>null</c> if the secret (or version) does not exist.
    /// </returns>
    Task<SecretPayload?> ResolveAsync(
        string name,
        int? version = null,
        CancellationToken cancellationToken = default
    );
}
