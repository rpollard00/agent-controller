namespace AgentController.Domain;

/// <summary>
/// Managed configuration for an Azure DevOps organization and project.
/// This is an onboarding profile, not a run-scoped execution environment.
/// </summary>
public sealed record AzureDevOpsEnvironmentProfile
{
    /// <summary>Stable key used by repository profiles to reference this profile.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Human-readable name shown to operators.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Whether the profile may be used for new work.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Azure DevOps organization URL.</summary>
    public string OrganizationUrl { get; init; } = string.Empty;

    /// <summary>Azure DevOps project name.</summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>Work item type used for board-state discovery and validation.</summary>
    public string WorkItemType { get; init; } = "User Story";

    /// <summary>Tags that make a work item eligible for autonomous execution.</summary>
    public IReadOnlyList<string> EligibleTags { get; init; } = [];

    /// <summary>Tags that exclude a work item from autonomous execution.</summary>
    public IReadOnlyList<string> ExcludedTags { get; init; } = [];

    /// <summary>Board states that are eligible for autonomous execution.</summary>
    public IReadOnlyList<string> EligibleStates { get; init; } = [];

    /// <summary>Board states that exclude a work item from autonomous execution.</summary>
    public IReadOnlyList<string> ExcludedStates { get; init; } = [];

    /// <summary>State applied when work begins, if configured.</summary>
    public string? ActiveState { get; init; }

    /// <summary>State applied when work completes, if configured.</summary>
    public string? CompletedState { get; init; }

    /// <summary>
    /// Name of the environment variable containing the Azure DevOps PAT.
    /// The credential value itself must never be stored on the profile.
    /// </summary>
    public string PatEnvironmentVariable { get; init; } = string.Empty;

    /// <summary>When the managed profile was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the managed profile was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
