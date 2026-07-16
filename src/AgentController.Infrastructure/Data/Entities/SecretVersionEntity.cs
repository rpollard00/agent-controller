namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// EF Core entity for a single version of a named secret.
/// Stores the encrypted value blob, nonce/tag, wrapped per-version DEK,
/// a monotonically increasing version number, and a created timestamp.
/// Never stores plaintext values at rest.
/// </summary>
internal sealed class SecretVersionEntity
{
    /// <summary>Primary key.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Foreign key to the parent <see cref="NamedSecretEntity"/>.</summary>
    public string NamedSecretId { get; set; } = string.Empty;

    /// <summary>Monotonically increasing version number (1-based).</summary>
    public int VersionNumber { get; set; }

    /// <summary>Encrypted secret value (AES-GCM ciphertext).</summary>
    public byte[] EncryptedValue { get; set; } = Array.Empty<byte>();

    /// <summary>Nonce / authentication tag for the AES-GCM encryption.</summary>
    public byte[] Nonce { get; set; } = Array.Empty<byte>();

    /// <summary>The data-encryption key (DEK) wrapped by the KEK for this version.</summary>
    public byte[] WrappedDek { get; set; } = Array.Empty<byte>();

    /// <summary>When this version was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Navigational reference to the parent <see cref="NamedSecretEntity"/>.</summary>
    public NamedSecretEntity? NamedSecret { get; set; }
}
