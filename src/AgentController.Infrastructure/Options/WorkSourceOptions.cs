using System.ComponentModel.DataAnnotations;
using AgentController.Application.Abstractions;

namespace AgentController.Infrastructure.Options;

/// <summary>
/// Work source provider configuration.
/// Section: "workSource"
/// </summary>
public sealed class WorkSourceOptions : IWorkSourceOptions
{
    public const string SectionName = "workSource";

    /// <summary>
    /// Provider identifier (e.g. "AzureDevOpsBoards", "LocalFake").
    /// </summary>
    [Required]
    public string Provider { get; init; } = string.Empty;

    /// <summary>
    /// Azure DevOps organization URL (required for AzureDevOpsBoards).
    /// </summary>
    public string? OrganizationUrl { get; init; }

    /// <summary>
    /// Azure DevOps project name.
    /// </summary>
    public string? Project { get; init; }

    /// <summary>
    /// Prefix used for controller-owned lifecycle tags on the board.
    /// Defaults to "agent" producing tags like agent-ready, agent-active,
    /// agent-failed, agent-needs-human.
    /// </summary>
    public string TagPrefix { get; init; } = DefaultTagPrefix;

    /// <summary>Default tag prefix when not explicitly configured.</summary>
    public const string DefaultTagPrefix = "agent";

    /// <summary>
    /// Work item states that are considered finished and not picked up.
    /// Items in these states are excluded from discovery queries.
    /// </summary>
    public IReadOnlyList<string> CompletedStates { get; init; } = [];

    // --- Legacy fields (removed by downstream work item #10) ---

    /// <summary>
    /// Tags that mark a work item as eligible for autonomous execution.
    /// </summary>
    public IReadOnlyList<string> EligibleTags { get; init; } = [];

    /// <summary>
    /// Tags that exclude a work item from autonomous execution.
    /// Defaults to agent-controlled lifecycle tags so that items already
    /// claimed, failed, or marked needs-human are not re-picked up on the
    /// next discovery cycle. Items can be retried explicitly by removing
    /// the exclusion tag from the work item in ADO.
    /// </summary>
    public IReadOnlyList<string> ExcludedTags { get; init; } =
    [
        DefaultExcludedTagAgentActive,
        DefaultExcludedTagAgentFailed,
        DefaultExcludedTagAgentNeedsHuman,
    ];

    /// <summary>Tag added when the controller claims a work item.</summary>
    public const string DefaultExcludedTagAgentActive = "agent-active";

    /// <summary>Tag added when a run fails.</summary>
    public const string DefaultExcludedTagAgentFailed = "agent-failed";

    /// <summary>Tag added when a run requires human input.</summary>
    public const string DefaultExcludedTagAgentNeedsHuman = "agent-needs-human";

    /// <summary>Tag added when a work item is prepared for agent pickup via rework.</summary>
    public const string DefaultTagAgentReady = "agent-ready";

    /// <summary>
    /// Work item states that are eligible for autonomous pickup.
    /// </summary>
    public IReadOnlyList<string> EligibleStates { get; init; } = [];

    /// <summary>
    /// Azure DevOps work item type used for state validation and queries
    /// (e.g. "User Story", "Task", "Bug").
    /// Defaults to "User Story" if not configured.
    /// Used by startup validation to enumerate valid System.State values
    /// for the configured project/WIT.
    /// </summary>
    public string WorkItemType { get; init; } = DefaultWorkItemType;

    /// <summary>Default work item type when not explicitly configured.</summary>
    public const string DefaultWorkItemType = "User Story";

    /// <summary>
    /// State to set on a work item when the controller starts working on it.
    /// </summary>
    public string? ActiveState { get; init; }

    /// <summary>
    /// State to set on a work item when the controller completes it.
    /// </summary>
    public string? CompletedState { get; init; }

    /// <summary>
    /// Maximum number of discussion comments to fetch from the work source
    /// and include in the agent runtime context. This bounds comment depth
    /// to keep context manageable. Default: 50.
    /// </summary>
    public int MaxComments { get; init; } = 50;
}
