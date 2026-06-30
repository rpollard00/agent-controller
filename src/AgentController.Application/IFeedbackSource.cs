using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Metadata about a single pull request the feedback worker should poll.
/// Populated by the worker from run data; the source implementation stays
/// dumb and only uses the fields it needs to fetch threads.
/// </summary>
public sealed record PrUnderTest
{
    /// <summary>Controller-assigned run identifier of the run that produced this PR.</summary>
    public string OriginatingRunId { get; init; } = string.Empty;

    /// <summary>Identifier of the work item this run was for.</summary>
    public string WorkItemId { get; init; } = string.Empty;

    /// <summary>Repository key (e.g. "org/project") for the source system.</summary>
    public string RepoKey { get; init; } = string.Empty;

    /// <summary>URL of the pull request.</summary>
    public string PullRequestUrl { get; init; } = string.Empty;

    /// <summary>
    /// Pull request identifier in the source system (e.g. ADO PR integer ID).
    /// Used by the feedback source to query threads.
    /// </summary>
    public string PullRequestId { get; init; } = string.Empty;

    /// <summary>Branch name the prior run pushed to.</summary>
    public string BranchName { get; init; } = string.Empty;
}

/// <summary>
/// Query parameters passed by the feedback worker to a feedback source.
/// The source uses <see cref="OpenPrs"/> to know which PRs to fetch threads for.
/// <see cref="AllowedReviewers"/> and <see cref="ReworkMarkerTag"/> are carried
/// for potential use by the source (e.g. marker label checks) but all filtering
/// is the responsibility of the upstream worker pipeline, not the source.
/// </summary>
public sealed record FeedbackQuery
{
    /// <summary>List of open PRs to poll for review threads.</summary>
    public IReadOnlyList<PrUnderTest> OpenPrs { get; init; } = [];

    /// <summary>
    /// Set of canonical reviewer identifiers (uniqueName / email).
    /// Used by the filter pipeline; the source may surface them for
    /// marker-gate checks but does not filter on them.
    /// </summary>
    public IReadOnlySet<string> AllowedReviewers { get; init; } = new HashSet<string>();

    /// <summary>
    /// Tag/label name that marks a PR as requesting rework
    /// (e.g. "agent-rework-requested").
    /// </summary>
    public string ReworkMarkerTag { get; init; } = "agent-rework-requested";
}

/// <summary>
/// Signal returned by a feedback source for a single PR.
/// Contains the raw review threads fetched from the source system
/// along with timestamps for soak-window tracking.
/// All filtering (marker gate, allowlist, status, author, content)
/// is performed upstream by the worker pipeline, not the source.
/// </summary>
public sealed record ReworkSignal
{
    /// <summary>Controller-assigned run identifier of the run that produced this PR.</summary>
    public string OriginatingRunId { get; init; } = string.Empty;

    /// <summary>Pull request identifier in the source system.</summary>
    public string PullRequestId { get; init; } = string.Empty;

    /// <summary>Review threads fetched from the source system (unfiltered).</summary>
    public IReadOnlyList<ReviewThread> Threads { get; init; } = [];

    /// <summary>Timestamp of the first qualifying comment across all threads.</summary>
    public DateTimeOffset FirstQualifyingCommentAt { get; init; }

    /// <summary>Timestamp of the last qualifying comment across all threads.</summary>
    public DateTimeOffset LastQualifyingCommentAt { get; init; }
}

/// <summary>
/// Port for fetching review feedback from pull requests.
/// 
/// Designed to be provider-agnostic: the same contract serves both
/// polling-based sources (Azure DevOps REST API) and future
/// webhook-based sources (which would drain an internal queue on
/// <see cref="PollAsync"/>). The source is a pure fetcher — it returns
/// raw threads and all filtering is the responsibility of the
/// upstream worker filter pipeline.
/// </summary>
public interface IFeedbackSource
{
    /// <summary>
    /// Poll the feedback provider for review threads on the given PRs.
    /// Returns one <see cref="ReworkSignal"/> per PR that has threads,
    /// or an empty list if no threads were found.
    /// 
    /// Implementations should return raw threads only. All filtering
    /// (marker gate, allowlist, thread status, author, content) is
    /// performed by the worker pipeline after this call returns.
    /// </summary>
    /// <param name="query">PRs to poll and query configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of rework signals, one per PR with threads.</returns>
    Task<IReadOnlyList<ReworkSignal>> PollAsync(
        FeedbackQuery query,
        CancellationToken cancellationToken);
}
