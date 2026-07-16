namespace AgentController.Domain;

/// <summary>
/// Managed configuration for a work-source environment (e.g. Azure DevOps Boards, GitHub Issues).
/// This is an onboarding profile, not a run-scoped execution environment.
/// Azure DevOps Boards is the first supported provider.
/// </summary>
public sealed record WorkSourceEnvironmentProfile
{
    /// <summary>Stable key used by repository profiles to reference this profile.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Human-readable name shown to operators.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Whether the profile may be used for new work.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Provider discriminator (e.g. "AzureDevOpsBoards").
    /// Determines which provider-specific configuration is applicable.
    /// </summary>
    public string Provider { get; init; } = "AzureDevOpsBoards";

    /// <summary>
    /// Prefix used for controller-owned lifecycle tags on the board
    /// (e.g. "{prefix}-ready", "{prefix}-active", "{prefix}-failed", "{prefix}-needs-human").
    /// Defaults to "agent" when blank.
    /// </summary>
    public string TagPrefix { get; init; } = "agent";

    // --- Azure DevOps Boards connection fields ---

    /// <summary>Azure DevOps organization URL (provider: AzureDevOpsBoards).</summary>
    public string OrganizationUrl { get; init; } = string.Empty;

    /// <summary>Azure DevOps project name (provider: AzureDevOpsBoards).</summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>State applied when work begins, if configured.</summary>
    public string? ActiveState { get; init; }

    /// <summary>State applied when work completes, if configured.</summary>
    public string? CompletedState { get; init; }

    /// <summary>
    /// Reference to a named, versioned secret holding the Azure DevOps PAT.
    /// Resolved at runtime via <see cref="Secrets.ISecretStore"/>.
    /// </summary>
    public Secrets.SecretReference PersonalAccessTokenReference { get; init; } =
        Secrets.SecretReference.Empty;

    /// <summary>When the managed profile was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the managed profile was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
