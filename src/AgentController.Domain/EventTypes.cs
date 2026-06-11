namespace AgentController.Domain;

/// <summary>
/// Well-known runtime event type constants.
/// New event types can be added by runtimes without changing the domain model.
/// These are data values carried on <see cref="RuntimeEvent.EventType"/>.
/// </summary>
public static class RuntimeEventTypes
{
    /// <summary>Runtime accepted the run and has begun work.</summary>
    public const string Accepted = "runtime.accepted";

    /// <summary>Runtime is still alive.</summary>
    public const string Heartbeat = "runtime.heartbeat";

    /// <summary>Human-readable status update.</summary>
    public const string Status = "runtime.status";

    /// <summary>Runtime created or selected a branch.</summary>
    public const string BranchCreated = "runtime.branch_created";

    /// <summary>Runtime opened a pull request.</summary>
    public const string PrCreated = "runtime.pr_created";

    /// <summary>Runtime needs human input.</summary>
    public const string NeedsHuman = "runtime.needs_human";

    /// <summary>Runtime completed its work.</summary>
    public const string Completed = "runtime.completed";

    /// <summary>Runtime failed.</summary>
    public const string Failed = "runtime.failed";

    /// <summary>Runtime acknowledged cancellation.</summary>
    public const string Cancelled = "runtime.cancelled";
}

/// <summary>
/// Well-known completion outcome constants reported by a runtime on completion.
/// These are data values carried inside the runtime.completed payload.
/// </summary>
public static class CompletionOutcomes
{
    /// <summary>A pull request was opened.</summary>
    public const string PullRequestOpened = "pull_request_opened";

    /// <summary>A branch was pushed without a PR.</summary>
    public const string BranchPushed = "branch_pushed";

    /// <summary>A patch or artifact was produced.</summary>
    public const string PatchCreated = "patch_created";

    /// <summary>No code changes were required.</summary>
    public const string NoChangesNeeded = "no_changes_needed";

    /// <summary>Human input is required to proceed.</summary>
    public const string NeedsHuman = "needs_human";

    /// <summary>The run failed.</summary>
    public const string Failed = "failed";
}
