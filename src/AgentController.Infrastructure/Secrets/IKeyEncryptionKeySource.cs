namespace AgentController.Infrastructure.Secrets;

/// <summary>
/// Provides the master key-encryption key (KEK) used for envelope encryption of secrets.
/// 
/// The KEK is a 256-bit key used to encrypt/decrypt per-secret DEKs.
/// Implementations may source the KEK from systemd-creds, user-secrets file,
/// environment variables, or other secure stores.
/// 
/// Tests can inject a deterministic KEK via a fake implementation.
/// </summary>
internal interface IKeyEncryptionKeySource
{
    /// <summary>
    /// Get the KEK bytes for envelope encryption.
    /// </summary>
    /// <returns>
    /// The KEK bytes (must be 32 bytes for AES-256-GCM).
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the KEK cannot be read or is invalid.
    /// </exception>
    byte[] GetKey();
}
