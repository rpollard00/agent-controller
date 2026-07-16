namespace AgentController.Domain;

/// <summary>
/// Opaque reference to a secret value stored outside the profile itself.
/// Resolved at runtime by an <c>ISecretStore</c> implementation.
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

/// <summary>
/// Managed configuration for a repository host connection (e.g. Azure DevOps Repos, GitHub, GitLab).
/// This is the distinct 'connected API as a source of repos' abstraction, decoupled from any work source.
/// Azure DevOps Repos is the first supported provider.
/// </summary>
public sealed record RepositoryHostConnectionProfile
{
    /// <summary>Stable key used by repository profiles to reference this connection.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Human-readable name shown to operators.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Whether the connection may be used for repository discovery and cloning.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Provider discriminator (e.g. "AzureDevOpsRepos").
    /// Determines which provider-specific configuration is applicable.
    /// Reserved values: "GitHub", "Forgejo", "GitLab".
    /// </summary>
    public string Provider { get; init; } = "AzureDevOpsRepos";

    // --- Azure DevOps Repos connection fields ---

    /// <summary>Azure DevOps organization URL (provider: AzureDevOpsRepos).</summary>
    public string OrganizationUrl { get; init; } = string.Empty;

    /// <summary>Azure DevOps project name (provider: AzureDevOpsRepos).</summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>
    /// Reference to the secret holding the personal access token.
    /// The credential value itself must never be stored on the profile.
    /// </summary>
    public SecretReference PersonalAccessTokenReference { get; init; } = new();

    /// <summary>When the managed profile was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the managed profile was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
