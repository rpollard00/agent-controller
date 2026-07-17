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

    /// <summary>
    /// Key of the unified <see cref="ConnectionProfile"/> this work source uses for
    /// organization-level connectivity (org URL, PAT, etc.).
    /// </summary>
    public string ConnectionKey { get; init; } = string.Empty;

    /// <summary>Consumer-level project name (e.g. Azure DevOps project).</summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>State applied when work begins, if configured.</summary>
    public string? ActiveState { get; init; }

    /// <summary>State applied when work completes, if configured.</summary>
    public string? CompletedState { get; init; }

    /// <summary>When the managed profile was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the managed profile was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
