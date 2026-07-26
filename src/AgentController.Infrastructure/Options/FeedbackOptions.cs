using System.ComponentModel.DataAnnotations;

namespace AgentController.Infrastructure.Options;

/// <summary>
/// Configuration for the feedback polling pipeline that drives PR-comment rework.
/// Section: "feedback"
/// </summary>
public sealed class FeedbackOptions : IValidatableObject
{
    public const string SectionName = "feedback";

    /// <summary>
    /// Whether the feedback polling pipeline is enabled.
    /// When disabled, the <see cref="FeedbackPollingWorker"/> sleeps indefinitely.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Feedback source provider to use.
    /// Supported values: "AzureDevOpsRepos", "Local", "None".
    /// Defaults to "None" (no feedback source registered).
    /// </summary>
    public string Provider { get; init; } = "None";

    /// <summary>
    /// How often the feedback worker polls for review comments, in seconds.
    /// Must be positive. Default: 60.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int PollIntervalSeconds { get; init; } = 60;

    /// <summary>
    /// Maximum number of concurrent feedback poll operations.
    /// Must be positive. Default: 2.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxConcurrentPolls { get; init; } = 2;

    /// <summary>
    /// Minimum time (in minutes) since the last qualifying comment before feedback
    /// is considered "soaked" and eligible for materialization. Default: 5.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int SoakMinutes { get; init; } = 5;

    /// <summary>
    /// PR label/tag that marks a PR as eligible for rework.
    /// Default: "agent-rework-requested".
    /// </summary>
    public string ReworkMarkerTag { get; init; } = "agent-rework-requested";

    /// <summary>
    /// PR label/tag that requests a new assistance story for an existing PR.
    /// Default: "agent-assistance-requested".
    /// </summary>
    [Required]
    public string AssistanceMarkerTag { get; init; } = "agent-assistance-requested";

    /// <summary>
    /// PR label/tag that marks an assistance request as being worked.
    /// Default: "agent-assistance-in-progress".
    /// </summary>
    [Required]
    public string AssistanceInProgressTag { get; init; } = "agent-assistance-in-progress";

    /// <summary>
    /// Canonical reviewer identifiers (uniqueName / email) whose comments qualify
    /// as rework feedback. Empty set means no feedback is accepted (fail-closed).
    /// </summary>
    public IReadOnlyList<string> AllowedReviewers { get; init; } = [];

    /// <summary>
    /// Maximum number of review threads to bundle per rework cycle.
    /// Must be positive. Default: 50.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxReviewThreadsPerBundle { get; init; } = 50;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (TagsMatch(AssistanceMarkerTag, ReworkMarkerTag))
        {
            yield return new ValidationResult(
                $"{nameof(AssistanceMarkerTag)} must be distinct from {nameof(ReworkMarkerTag)}.",
                [nameof(AssistanceMarkerTag), nameof(ReworkMarkerTag)]
            );
        }

        if (TagsMatch(AssistanceInProgressTag, ReworkMarkerTag))
        {
            yield return new ValidationResult(
                $"{nameof(AssistanceInProgressTag)} must be distinct from {nameof(ReworkMarkerTag)}.",
                [nameof(AssistanceInProgressTag), nameof(ReworkMarkerTag)]
            );
        }

        if (TagsMatch(AssistanceMarkerTag, AssistanceInProgressTag))
        {
            yield return new ValidationResult(
                $"{nameof(AssistanceMarkerTag)} must be distinct from {nameof(AssistanceInProgressTag)}.",
                [nameof(AssistanceMarkerTag), nameof(AssistanceInProgressTag)]
            );
        }
    }

    private static bool TagsMatch(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
