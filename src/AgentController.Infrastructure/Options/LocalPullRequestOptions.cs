namespace AgentController.Infrastructure.Options;

/// <summary>
/// Deterministic active pull requests used by local pull-request discovery.
/// Section: "localPullRequests".
/// </summary>
public sealed class LocalPullRequestOptions
{
    public const string SectionName = "localPullRequests";

    /// <summary>Active pull requests exposed to offline workflows.</summary>
    public IReadOnlyList<LocalPullRequestDefinition> Definitions { get; init; } = [];
}

/// <summary>A pull request exposed by local discovery.</summary>
public sealed class LocalPullRequestDefinition
{
    /// <summary>Managed source environment key.</summary>
    public string EnvironmentKey { get; init; } = string.Empty;

    /// <summary>Managed repository key.</summary>
    public string RepositoryKey { get; init; } = string.Empty;

    /// <summary>Provider-assigned pull-request identifier.</summary>
    public string PullRequestId { get; init; } = string.Empty;

    /// <summary>Pull-request title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Browser URL for the pull request.</summary>
    public string PullRequestUrl { get; init; } = string.Empty;

    /// <summary>Source branch containing the proposed changes.</summary>
    public string SourceBranch { get; init; } = string.Empty;

    /// <summary>Target branch into which the changes will merge.</summary>
    public string TargetBranch { get; init; } = string.Empty;

    /// <summary>Source-branch commit observed in this snapshot.</summary>
    public string SourceCommitSha { get; init; } = string.Empty;

    /// <summary>Labels currently applied to the pull request.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Work items linked to the pull request.</summary>
    public IReadOnlyList<LocalPullRequestWorkItemDefinition> LinkedWorkItems { get; init; } = [];
}

/// <summary>A work-item link exposed by local pull-request discovery.</summary>
public sealed class LocalPullRequestWorkItemDefinition
{
    /// <summary>Provider-assigned work-item identifier.</summary>
    public string WorkItemId { get; init; } = string.Empty;

    /// <summary>Provider URL for retrieving the linked work item.</summary>
    public string WorkItemUrl { get; init; } = string.Empty;
}
