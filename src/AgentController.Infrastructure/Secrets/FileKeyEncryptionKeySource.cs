using System.ComponentModel.DataAnnotations;

namespace AgentController.Infrastructure.Secrets;

/// <summary>
/// Configuration for the file-based KEK source.
/// </summary>
public sealed class FileKeyEncryptionKeySourceOptions
{
    /// <summary>
    /// Path to the file containing the raw 32-byte KEK.
    /// The file must contain exactly 32 bytes of binary data.
    /// </summary>
    [Required]
    public string? FilePath { get; init; }
}

/// <summary>
/// Reads the master KEK from a file on disk.
/// 
/// The file must contain exactly 32 bytes of binary data (AES-256 key).
/// Throws <see cref="InvalidOperationException"/> at construction if the
/// file is missing, unreadable, or not exactly 32 bytes.
/// </summary>
internal sealed class FileKeyEncryptionKeySource : IKeyEncryptionKeySource
{
    private readonly byte[] _key;

    /// <summary>
    /// Create a new file-based KEK source.
    /// </summary>
    /// <param name="filePath">Path to the 32-byte binary KEK file.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the file is missing, unreadable, or not exactly 32 bytes.
    /// </exception>
    public FileKeyEncryptionKeySource(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new InvalidOperationException(
                $"KEK file not found at '{filePath}'. " +
                "The secret provider cannot start without a valid KEK. " +
                "See the KEK setup guide for provisioning instructions.");
        }

        var bytes = File.ReadAllBytes(filePath);

        if (bytes.Length != 32)
        {
            throw new InvalidOperationException(
                $"KEK file at '{filePath}' must contain exactly 32 bytes. " +
                $"Got {bytes.Length} bytes.");
        }

        _key = bytes;
    }

    /// <inheritdoc />
    public byte[] GetKey() => _key;
}
