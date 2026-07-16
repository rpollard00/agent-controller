namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// EF Core entity for persisted secrets.
/// Each row maps to a SecretReference with Kind "Db" where Id is the row's Id.
/// The secret value is stored encrypted-at-rest if a protector is configured,
/// plaintext otherwise.
/// </summary>
internal sealed class SecretEntity
{
    /// <summary>Unique identifier matching SecretReference.Id for Kind "Db".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The secret value. Encrypted-at-rest if a protector is configured,
    /// plaintext otherwise.
    /// TODO: key rotation support for encrypted values.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Optional label for operational visibility (not a security boundary).</summary>
    public string? Label { get; set; }

    /// <summary>When the secret was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the secret was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
