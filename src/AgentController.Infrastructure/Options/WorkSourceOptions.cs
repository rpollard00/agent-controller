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
    /// Tags that mark a work item as eligible for autonomous execution.
    /// </summary>
    public IReadOnlyList<string> EligibleTags { get; init; } = [];

    /// <summary>
    /// Tags that exclude a work item from autonomous execution.
    /// </summary>
    public IReadOnlyList<string> ExcludedTags { get; init; } = [];

    /// <summary>
    /// Work item states that are eligible for autonomous pickup.
    /// </summary>
    public IReadOnlyList<string> EligibleStates { get; init; } = [];

    /// <summary>
    /// State to set on a work item when the controller starts working on it.
    /// </summary>
    public string? ActiveState { get; init; }

    /// <summary>
    /// State to set on a work item when the controller completes it.
    /// </summary>
    public string? CompletedState { get; init; }
}
