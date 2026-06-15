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

/// <summary>
/// Well-known controller lifecycle event type constants.
/// These are emitted by <c>IRunLifecycleService</c> to record controller-owned
/// state transitions and internal actions in the authoritative event log.
/// </summary>
public static class ControllerEventTypes
{
    /// <summary>Work item claimed by this controller instance.</summary>
    public const string Claimed = "controller.claimed";

    /// <summary>Environment provisioning started.</summary>
    public const string EnvironmentProvisioning = "controller.environment_provisioning";

    /// <summary>Environment provisioned and ready.</summary>
    public const string EnvironmentReady = "controller.environment_ready";

    /// <summary>Repository cloning started.</summary>
    public const string RepositoryCloning = "controller.repository_cloning";

    /// <summary>Repository cloned and ready.</summary>
    public const string RepositoryReady = "controller.repository_ready";

    /// <summary>Context files injected into the run workspace.</summary>
    public const string ContextInjected = "controller.context_injected";

    /// <summary>Agent runtime start requested.</summary>
    public const string AgentStarting = "controller.agent_starting";

    /// <summary>Agent runtime is executing.</summary>
    public const string AgentRunning = "controller.agent_running";

    /// <summary>Run handed off to the runtime, awaiting result.</summary>
    public const string AwaitingResult = "controller.awaiting_result";

    /// <summary>A runtime event was ingested and processed.</summary>
    public const string RuntimeEventIngested = "controller.runtime_event_ingested";

    /// <summary>A manual or external state transition was performed.</summary>
    public const string StateTransition = "controller.state_transition";

    /// <summary>Stale run recovered by the controller (transitioned to NeedsHuman).</summary>
    public const string StaleRecovered = "controller.stale_recovered";

    /// <summary>Run was cancelled by the controller.</summary>
    public const string Cancelled = "controller.cancelled";

    /// <summary>Run failed due to a controller-side error.</summary>
    public const string Failed = "controller.failed";
}
