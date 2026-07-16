using System.Security.Cryptography;

namespace AgentController.Infrastructure.Secrets;

/// <summary>
/// Envelope encryption service using AES-256-GCM.
/// 
/// For each secret value:
/// 1. A random 256-bit DEK (Data Encryption Key) is generated.
/// 2. The plaintext value is encrypted with the DEK using AES-GCM, producing
///    a ciphertext blob and a 12-byte nonce.
/// 3. The DEK itself is encrypted (wrapped) by the KEK using AES-GCM,
///    producing a wrapped DEK blob and its own 12-byte nonce.
/// 
/// The stored artifacts are:
/// - EncryptedValue (ciphertext + tag from AES-GCM)
/// - Nonce (12 bytes, used to decrypt EncryptedValue with the unwrapped DEK)
/// - WrappedDek (encrypted DEK + tag from AES-GCM)
/// 
/// The Nonce stored on the entity is the one for the data encryption (step 2).
/// The nonce for the DEK wrapping (step 3) is embedded in the WrappedDek storage
/// by prepending it to the wrapped DEK bytes: [12-byte nonce | wrapped DEK ciphertext+tag].
/// </summary>
internal sealed class AesGcmEnvelopeEncryption
{
    private static readonly int Aes256KeySizeBytes = 32;
    private static readonly int NonceSizeBytes = 12;
    private static readonly int TagSizeBytes = 16; // AES-GCM default tag size

    private readonly IKeyEncryptionKeySource _kekSource;

    /// <summary>
    /// Create a new envelope encryption instance.
    /// </summary>
    /// <param name="kekSource">
    /// The source for the master KEK. Fails fast at construction if the KEK is missing.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the KEK is missing, unreadable, or not 32 bytes.
    /// </exception>
    public AesGcmEnvelopeEncryption(IKeyEncryptionKeySource kekSource)
    {
        // Fail fast: validate KEK at construction time.
        var kek = kekSource.GetKey();
        if (kek == null || kek.Length != Aes256KeySizeBytes)
        {
            throw new InvalidOperationException(
                $"KEK must be {Aes256KeySizeBytes} bytes for AES-256-GCM. " +
                $"Got {(kek == null ? "null" : kek.Length + " bytes")}."
            );
        }

        _kekSource = kekSource;
    }

    /// <summary>
    /// Encrypt a plaintext value using envelope encryption.
    /// </summary>
    /// <param name="plaintext">The secret value to encrypt.</param>
    /// <returns>
    /// A tuple of (encryptedValue, nonce, wrappedDek) suitable for storage.
    /// </returns>
    public (byte[] EncryptedValue, byte[] Nonce, byte[] WrappedDek) Encrypt(string plaintext)
    {
        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);

        // Step 1: Generate a random DEK.
        var dek = new byte[Aes256KeySizeBytes];
        RandomNumberGenerator.Fill(dek);

        // Step 2: Encrypt the plaintext with the DEK using AES-GCM.
        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSizeBytes];
        using var aesGcm = new AesGcm(dek, TagSizeBytes);
        aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Combine ciphertext + tag for storage.
        var encryptedValue = new byte[ciphertext.Length + tag.Length];
        System.Array.Copy(ciphertext, 0, encryptedValue, 0, ciphertext.Length);
        System.Array.Copy(tag, 0, encryptedValue, ciphertext.Length, tag.Length);

        // Step 3: Wrap (encrypt) the DEK with the KEK using AES-GCM.
        var kek = _kekSource.GetKey();
        var dekNonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(dekNonce);

        var wrappedCiphertext = new byte[Aes256KeySizeBytes];
        var wrappedTag = new byte[TagSizeBytes];
        using var kekAesGcm = new AesGcm(kek, TagSizeBytes);
        kekAesGcm.Encrypt(dekNonce, dek, wrappedCiphertext, wrappedTag);

        // Combine wrapped DEK ciphertext + tag.
        var wrappedDekContent = new byte[wrappedCiphertext.Length + wrappedTag.Length];
        System.Array.Copy(wrappedCiphertext, 0, wrappedDekContent, 0, wrappedCiphertext.Length);
        System.Array.Copy(wrappedTag, 0, wrappedDekContent, wrappedCiphertext.Length, wrappedTag.Length);

        // Prepend the DEK nonce to the wrapped DEK for storage.
        var wrappedDek = new byte[NonceSizeBytes + wrappedDekContent.Length];
        System.Array.Copy(dekNonce, 0, wrappedDek, 0, NonceSizeBytes);
        System.Array.Copy(wrappedDekContent, 0, wrappedDek, NonceSizeBytes, wrappedDekContent.Length);

        return (encryptedValue, nonce, wrappedDek);
    }

    /// <summary>
    /// Decrypt an encrypted value using envelope decryption.
    /// </summary>
    /// <param name="encryptedValue">The encrypted value blob (ciphertext + tag).</param>
    /// <param name="nonce">The nonce used for data encryption.</param>
    /// <param name="wrappedDek">The wrapped DEK ([nonce | ciphertext + tag]).</param>
    /// <returns>The decrypted plaintext value.</returns>
    /// <exception cref="CryptographicException">
    /// Thrown when decryption fails (e.g., tampered data or wrong key).
    /// </exception>
    public string Decrypt(byte[] encryptedValue, byte[] nonce, byte[] wrappedDek)
    {
        // Step 1: Unwrap the DEK using the KEK.
        var kek = _kekSource.GetKey();

        // Extract the DEK nonce from the front of wrappedDek.
        var dekNonce = new byte[NonceSizeBytes];
        System.Array.Copy(wrappedDek, 0, dekNonce, 0, NonceSizeBytes);

        var wrappedContent = new byte[wrappedDek.Length - NonceSizeBytes];
        System.Array.Copy(wrappedDek, NonceSizeBytes, wrappedContent, 0, wrappedContent.Length);

        // Split wrapped content into ciphertext and tag.
        var wrappedCiphertext = new byte[wrappedContent.Length - TagSizeBytes];
        var wrappedTag = new byte[TagSizeBytes];
        System.Array.Copy(wrappedContent, 0, wrappedCiphertext, 0, wrappedCiphertext.Length);
        System.Array.Copy(wrappedContent, wrappedCiphertext.Length, wrappedTag, 0, TagSizeBytes);

        var dek = new byte[Aes256KeySizeBytes];
        using var kekAesGcm = new AesGcm(kek, TagSizeBytes);
        kekAesGcm.Decrypt(dekNonce, wrappedCiphertext, wrappedTag, dek);

        // Step 2: Decrypt the plaintext with the unwrapped DEK.
        var ciphertext = new byte[encryptedValue.Length - TagSizeBytes];
        var tag = new byte[TagSizeBytes];
        System.Array.Copy(encryptedValue, 0, ciphertext, 0, ciphertext.Length);
        System.Array.Copy(encryptedValue, ciphertext.Length, tag, 0, TagSizeBytes);

        var plaintextBytes = new byte[ciphertext.Length];
        using var aesGcm = new AesGcm(dek, TagSizeBytes);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintextBytes);

        return System.Text.Encoding.UTF8.GetString(plaintextBytes);
    }
}
