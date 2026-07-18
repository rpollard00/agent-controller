namespace AgentController.Domain.Secrets;

/// <summary>
/// Stable discriminator constants for secret types.
/// Used in API payloads, persistence, and runtime type checks.
/// </summary>
public static class SecretType
{
    /// <summary>
    /// Personal Access Token — a single string value used for HTTP-based authentication.
    /// </summary>
    public const string PersonalAccessToken = "personal-access-token";

    /// <summary>
    /// SSH key pair — an atomic payload containing a private key, a public key,
    /// and an optional passphrase for the private key.
    /// </summary>
    public const string SshKey = "ssh-key";
}
