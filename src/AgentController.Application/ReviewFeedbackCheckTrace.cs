namespace AgentController.Application;

/// <summary>Outcome of the request-marker check performed for a pull request.</summary>
public enum FeedbackMarkerCheckStatus
{
    /// <summary>The marker was established by the caller (the Assistance discovery path).</summary>
    AlreadyValidated,

    /// <summary>The configured marker was found.</summary>
    Present,

    /// <summary>Labels were fetched, but the configured marker was not found.</summary>
    Missing,

    /// <summary>Labels could not be fetched, so the check failed closed.</summary>
    FetchFailed,

    /// <summary>The feedback signal did not have a corresponding pull request to inspect.</summary>
    PullRequestNotFound,

    /// <summary>The reviewer allowlist failed closed before marker lookup.</summary>
    NotAttempted,
}

/// <summary>
/// Body-free trace of the feedback checks applied to one pull request. Counts describe
/// each successive stage of the same policy used by the feedback polling worker.
/// </summary>
public sealed record ReviewFeedbackCheckTrace
{
    /// <summary>Provider pull-request identifier.</summary>
    public string PullRequestId { get; init; } = string.Empty;

    /// <summary>Result of looking for the configured request marker.</summary>
    public FeedbackMarkerCheckStatus MarkerStatus { get; init; }

    /// <summary>Whether at least one reviewer is configured.</summary>
    public bool ReviewerAllowlistConfigured { get; init; }

    /// <summary>Number of threads before filtering.</summary>
    public int TotalThreadCount { get; init; }

    /// <summary>Number of active threads.</summary>
    public int ActiveThreadCount { get; init; }

    /// <summary>Active threads whose reply chain contains an allowlisted reviewer.</summary>
    public int AllowlistedReviewerThreadCount { get; init; }

    /// <summary>Allowlisted-reviewer threads whose reply chain contains non-whitespace content.</summary>
    public int NonEmptyContentThreadCount { get; init; }

    /// <summary>Number of threads retained by the complete policy.</summary>
    public int QualifyingThreadCount { get; init; }

    /// <summary>
    /// Whether the request is retained. Assistance may be retained with zero qualifying
    /// threads; Revival requires at least one.
    /// </summary>
    public bool IsAccepted { get; init; }
}
