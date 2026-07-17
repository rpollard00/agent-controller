using AgentController.Domain.Secrets;

namespace AgentController.Domain;

/// <summary>
/// Capabilities a connection profile can provide.
/// 
/// A single connection may carry multiple capabilities — for example,
/// Azure DevOps provides both <see cref="Repositories"/> and <see cref="WorkTracking"/>.
/// <see cref="ExecutionHost"/> is reserved for future runtime-environment connections.
/// </summary>
public enum ConnectionCapability
{
    /// <summary>Repository hosting and discovery (e.g. Azure DevOps Repos, GitHub).</summary>
    Repositories = 1,

    /// <summary>Work-item tracking (e.g. Azure DevOps Boards, Jira).</summary>
    WorkTracking = 2,

    /// <summary>
    /// Runtime execution host.
    /// Reserved for future use; no schema fields are emitted for this capability yet.
    /// </summary>
    ExecutionHost = 4
}

/// <summary>
/// Base type for provider-specific connection settings.
/// </summary>
public abstract record ConnectionSettings;

/// <summary>
/// Azure DevOps connection settings.
/// 
/// ADO connections are org-level — <c>Project</c> is NOT stored here;
/// it belongs on the consumer profile (WorkSourceEnvironmentProfile, RepositoryProfile).
/// </summary>
public sealed record AzureDevOpsConnectionSettings : ConnectionSettings
{
    /// <summary>Azure DevOps organization URL (e.g. <c>https://dev.azure.com/myorg</c>).</summary>
    public string OrganizationUrl { get; init; } = string.Empty;

    /// <summary>
    /// Reference to the named secret holding the personal access token.
    /// Resolved at runtime via <see cref="ISecretStore"/>.
    /// </summary>
    public SecretReference PersonalAccessTokenReference { get; init; } =
        SecretReference.Empty;
}

/// <summary>
/// Unified, provider-discriminated connection profile.
///
/// Replaces the connection-shaped halves of legacy repository-host and work-source profiles.
/// A single org-level connection carries one or more <see cref="ConnectionCapability"/>
/// values and provider-specific settings.
/// Consumer profiles (work sources, repositories) reference this via <c>ConnectionKey</c>
/// and add their own consumer-level fields (e.g. <c>Project</c>).
///
/// Provider discriminator values:
/// <list type="table">
///   <item><term>AzureDevOps</term><def>Azure DevOps (org-level, supports Repositories + WorkTracking).</def></item>
/// </list>
/// 
/// Reserved provider strings (no types defined yet):
/// <c>GitHub</c>, <c>Forgejo</c>, <c>GitLab</c>, <c>Jira</c>.
/// </summary>
public sealed record ConnectionProfile
{
    /// <summary>Stable key used by consumer profiles to reference this connection.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Human-readable name shown to operators.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Whether the connection may be used by consumers.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Provider discriminator (e.g. "AzureDevOps").
    /// Determines which <see cref="ConnectionSettings"/> subtype applies.
    /// 
    /// Reserved values: "GitHub", "Forgejo", "GitLab", "Jira".
    /// </summary>
    public string Provider { get; init; } = "AzureDevOps";

    /// <summary>
    /// Capabilities this connection provides.
    /// A connection may carry multiple capabilities (e.g. Repositories + WorkTracking).
    /// </summary>
    public IReadOnlyList<ConnectionCapability> Capabilities { get; init; } =
        Array.Empty<ConnectionCapability>();

    /// <summary>
    /// Provider-specific settings bag.
    /// Cast to the concrete subtype matching <see cref="Provider"/> (e.g. <see cref="AzureDevOpsConnectionSettings"/>).
    /// </summary>
    public ConnectionSettings? ProviderSettings { get; init; }

    /// <summary>When the connection profile was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the connection profile was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
