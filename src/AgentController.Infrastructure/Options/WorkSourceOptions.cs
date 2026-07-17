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
    /// Key of the unified ConnectionProfile to use for this work source.
    /// The connection carries OrganizationUrl and PAT; the consumer profile
    /// carries only the consumer-level Project.
    /// </summary>
    public string? ConnectionKey { get; init; }

    /// <summary>
    /// Azure DevOps project name (consumer-level, not connection-level).
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

    // ── Prefix-aware lifecycle tag helpers ──

    /// <summary>Tag added when a work item is prepared for agent pickup.</summary>
    public static string TagReady(string prefix = DefaultTagPrefix) => $"{prefix}-ready";

    /// <summary>Tag added when the controller claims a work item.</summary>
    public static string TagActive(string prefix = DefaultTagPrefix) => $"{prefix}-active";

    /// <summary>Tag added when a run fails.</summary>
    public static string TagFailed(string prefix = DefaultTagPrefix) => $"{prefix}-failed";

    /// <summary>Tag added when a run requires human input.</summary>
    public static string TagNeedsHuman(string prefix = DefaultTagPrefix) => $"{prefix}-needs-human";

    /// <summary>
    /// All controller-owned lifecycle tags for the given prefix
    /// (active, failed, needs-human).
    /// </summary>
    public static IReadOnlyList<string> LifecycleTags(string prefix = DefaultTagPrefix) =>
    [
        TagActive(prefix),
        TagFailed(prefix),
        TagNeedsHuman(prefix),
    ];

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
