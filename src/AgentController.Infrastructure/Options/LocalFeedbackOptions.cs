namespace AgentController.Infrastructure.Options;

/// <summary>
/// Configuration for deterministic feedback signals loaded from configuration
/// when the feedback source provider is <c>"Local"</c>.
/// Section: "localFeedback"
/// </summary>
public sealed class LocalFeedbackOptions
{
    public const string SectionName = "localFeedback";

    /// <summary>
    /// Ordered set of feedback signal definitions.
    /// Each definition is matched against <see cref="Application.PrUnderTest.PullRequestId"/>
    /// in the poll query. Only PRs present in the query that have a matching
    /// definition will produce a <see cref="Application.ReworkSignal"/>.
    /// </summary>
    public IReadOnlyList<LocalFeedbackSignalDefinition> Signals { get; init; } = [];
}

/// <summary>
/// A single feedback signal definition for the <c>Local</c> feedback source.
/// </summary>
public sealed class LocalFeedbackSignalDefinition
{
    /// <summary>
    /// Pull request identifier to match against the poll query.
    /// Required — definitions with an empty value are skipped at startup.
    /// </summary>
    public string PullRequestId { get; init; } = string.Empty;

    /// <summary>
    /// Controller-assigned run identifier of the run that produced this PR.
    /// Defaults to "local-run-{PullRequestId}" if not supplied.
    /// </summary>
    public string? OriginatingRunId { get; init; }

    /// <summary>
    /// Review threads to return for this PR.
    /// </summary>
    public IReadOnlyList<LocalFeedbackThreadDefinition> Threads { get; init; } = [];

    /// <summary>
    /// PR labels to return for this PR (used by the marker gate in the filter pipeline).
    /// When absent, the PR is treated as having no labels (marker gate fails-closed).
    /// </summary>
    public IReadOnlyList<LocalFeedbackLabelDefinition> Labels { get; init; } = [];
}

/// <summary>
/// A review thread definition for the <c>Local</c> feedback source.
/// </summary>
public sealed class LocalFeedbackThreadDefinition
{
    /// <summary>Stable thread identifier. Required.</summary>
    public string ThreadId { get; init; } = string.Empty;

    /// <summary>Canonical author identifier (uniqueName / email).</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>ISO 8601 date-time string for thread creation.</summary>
    public string? CreatedAt { get; init; }

    /// <summary>Thread status string (e.g. "Active", "Resolved"). Defaults to "Active".</summary>
    public string Status { get; init; } = "Active";

    /// <summary>File path the thread is scoped to (empty for PR-level threads).</summary>
    public string? FilePath { get; init; }

    /// <summary>Start line of the thread range (1-based, inclusive).</summary>
    public int? StartLine { get; init; }

    /// <summary>End line of the thread range (1-based, inclusive).</summary>
    public int? EndLine { get; init; }

    /// <summary>True if this is a PR-level thread rather than line-specific.</summary>
    public bool IsFileLevel { get; init; }

    /// <summary>Ordered comments in the reply chain.</summary>
    public IReadOnlyList<LocalFeedbackCommentDefinition> Comments { get; init; } = [];
}

/// <summary>
/// A comment definition within a local feedback thread.
/// </summary>
public sealed class LocalFeedbackCommentDefinition
{
    /// <summary>Canonical author identifier (uniqueName / email).</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>Comment body text.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>ISO 8601 date-time string for comment creation.</summary>
    public string? CreatedAt { get; init; }

    /// <summary>True if this comment is a reply to another comment in the thread.</summary>
    public bool IsReply { get; init; }
}

/// <summary>
/// A PR label definition for the <c>Local</c> feedback source.
/// Used by the marker gate in the filter pipeline.
/// </summary>
public sealed class LocalFeedbackLabelDefinition
{
    /// <summary>Label name (e.g. "agent-rework-requested").</summary>
    public string Name { get; init; } = string.Empty;
}
