namespace AgentController.Domain.Secrets;

/// <summary>
/// Value object referencing a named, versioned secret stored in the secret store.
/// 
/// Used by work-source and other domain profiles to reference a secret by name
/// without carrying the credential value. When <see cref="Version"/> is omitted,
/// the latest version is resolved at runtime via <see cref="ISecretStore"/>.
/// </summary>
public sealed record SecretReference
{
    /// <summary>The unique name of the secret.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Optional version number. When omitted, resolves to the latest version.
    /// </summary>
    public int? Version { get; init; }

    /// <summary>Creates a reference to the latest version of a named secret.</summary>
    public static SecretReference ByName(string name) =>
        new() { Name = name };

    /// <summary>Creates a reference to a specific version of a named secret.</summary>
    public static SecretReference ByNameAndVersion(string name, int version) =>
        new() { Name = name, Version = version };

    /// <summary>
    /// Creates an empty reference. Useful as a default value on profiles
    /// that have not yet been configured with a secret.
    /// </summary>
    public static SecretReference Empty => new();

    /// <summary>Returns <c>true</c> if the reference has a non-empty name.</summary>
    public bool IsSpecified => !string.IsNullOrWhiteSpace(Name);
}
