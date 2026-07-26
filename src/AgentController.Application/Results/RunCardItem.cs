using AgentController.Domain;

namespace AgentController.Application.Results;

/// <summary>
/// Aggregate dashboard projection for either an agent run or a rework-feedback soak row.
/// </summary>
public sealed record RunCardItem
{
    /// <summary>Run identifier, or rework-feedback row identifier for a soak card.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Card discriminator: <c>run</c> or <c>rework-soak</c>.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Current run state or synthesized soak state.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Dashboard category: executing, pending, attention, or completed.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Associated work item title, when available.</summary>
    public string? WorkItemTitle { get; init; }

    /// <summary>External work item URL, when available.</summary>
    public string? WorkItemUrl { get; init; }

    /// <summary>Work source that supplied the associated work item.</summary>
    public string? WorkItemSource { get; init; }

    /// <summary>Managed repository key associated with the work item.</summary>
    public string? RepoKey { get; init; }

    /// <summary>Browser-friendly repository URL, when one can be resolved.</summary>
    public string? RepositoryUrl { get; init; }

    /// <summary>Agent runtime type used by the run.</summary>
    public string? RuntimeType { get; init; }

    /// <summary>Display name of the runtime environment profile captured for the run.</summary>
    public string? RuntimeProfileName { get; init; }

    /// <summary>Environment provider type captured for the run.</summary>
    public string? EnvironmentProviderType { get; init; }

    /// <summary>One-based attempt number for the associated run.</summary>
    public int RunAttempt { get; init; } = 1;

    /// <summary>How work on an existing pull request was requested, when applicable.</summary>
    public ReworkRequestMode? RequestMode { get; init; }

    /// <summary>Canonical pull-request reference associated with the rework request.</summary>
    public PullRequestReference? PullRequest { get; init; }

    /// <summary>One-based rework cycle number after the request is materialized.</summary>
    public int? CycleNumber { get; init; }

    /// <summary>Controller-local work-item ID of a generated assistance story.</summary>
    public string? AssistanceStoryWorkItemId { get; init; }

    /// <summary>Provider-assigned ID of a generated assistance story.</summary>
    public string? AssistanceStoryExternalId { get; init; }

    /// <summary>Browser URL of a generated assistance story.</summary>
    public string? AssistanceStoryUrl { get; init; }

    /// <summary>Current feedback soak/materialization state.</summary>
    public ReworkFeedbackStatus? FeedbackStatus { get; init; }

    /// <summary>Current state of the materialized rework cycle.</summary>
    public ReworkCycleStatus? CycleStatus { get; init; }

    /// <summary>Run that originally consumed the materialized cycle.</summary>
    public string? ConsumingRunId { get; init; }

    /// <summary>Type of the latest lifecycle event or synthesized soak event.</summary>
    public string? LastEventType { get; init; }

    /// <summary>Message from the latest lifecycle event or synthesized soak event.</summary>
    public string? LastEventMessage { get; init; }

    /// <summary>Occurrence time of the latest lifecycle event or qualifying comment.</summary>
    public DateTimeOffset? LastEventAt { get; init; }

    /// <summary>When the underlying run or soak row was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the underlying run or soak row was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}
