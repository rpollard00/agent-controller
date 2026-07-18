using System.Text.Json.Serialization;

namespace AgentController.Domain.Secrets;

/// <summary>
/// Base type for typed secret payloads.
/// Each subtype corresponds to a <see cref="SecretType"/> discriminator value.
/// Payloads are never serialized to API responses; they are write-only at version creation
/// and read-only at resolution time.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PersonalAccessTokenPayload), typeDiscriminator: "personal-access-token")]
[JsonDerivedType(typeof(SshKeyPayload), typeDiscriminator: "ssh-key")]
public abstract record SecretPayload
{
    /// <summary>
    /// Returns the stable type discriminator for this payload.
    /// </summary>
    public abstract string Type { get; }
}

/// <summary>
/// Payload for a Personal Access Token secret.
/// Contains a single plaintext token value.
/// </summary>
public sealed record PersonalAccessTokenPayload : SecretPayload
{
    /// <summary>
    /// The plaintext token value. Never returned in API responses or logs.
    /// </summary>
    public required string Value { get; init; }

    /// <inheritdoc />
    public override string Type => SecretType.PersonalAccessToken;

    /// <summary>Converts a plain string value into a PAT payload.</summary>
    public static implicit operator PersonalAccessTokenPayload(string value) =>
        new() { Value = value };
}

/// <summary>
/// Payload for an SSH key secret.
/// An atomic payload containing the private key, public key, and an optional passphrase.
/// Every version is a wholesale replacement — no field is preserved across versions.
/// </summary>
public sealed record SshKeyPayload : SecretPayload
{
    /// <summary>
    /// The SSH private key (PEM-encoded). Never returned in API responses or logs.
    /// </summary>
    public required string PrivateKey { get; init; }

    /// <summary>
    /// The SSH public key (typically <c>ssh-rsa, ssh-ed25519, ...</c> format).
    /// Safe to display in metadata and version listings.
    /// </summary>
    public required string PublicKey { get; init; }

    /// <summary>
    /// Optional passphrase for the encrypted private key.
    /// When <c>null</c>, the private key is expected to have no passphrase.
    /// Never returned in API responses or logs.
    /// </summary>
    public string? Passphrase { get; init; }

    /// <inheritdoc />
    public override string Type => SecretType.SshKey;
}
