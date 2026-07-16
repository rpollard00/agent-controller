namespace AgentController.Domain;

/// <summary>
/// Opaque reference to a secret value stored outside the profile itself.
/// Resolved at runtime by an <c>IManagedSecretStore</c> implementation.
///
/// Legacy polymorphic shape (Kind + Id). New code should use
/// <see cref="Secrets.SecretReference"/> (named-secret reference) instead.
/// </summary>
public sealed record SecretReference
{
    /// <summary>Kind of secret store (e.g. "EnvVar", "Db").</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Identifier within the store (e.g. environment variable name or database row id).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Create a reference to an environment variable secret.</summary>
    public static SecretReference EnvironmentVariable(string name) =>
        new() { Kind = "EnvVar", Id = name };

    /// <summary>Create a reference to a database-stored secret.</summary>
    public static SecretReference Database(string id) =>
        new() { Kind = "Db", Id = id };
}
