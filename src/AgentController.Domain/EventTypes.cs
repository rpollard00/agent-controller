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

    /// <summary>
    /// Runtime failed with a retryable error (e.g. keepalive-stall, process crash).
    /// The controller should evaluate the run-level retry threshold before
    /// escalating to NeedsHuman.
    /// </summary>
    public const string FailedRetryable = "runtime.failed_retryable";
}

/// <summary>
/// Well-known failure reason strings used to classify run failures as
/// retryable or non-retryable for the run-level retry mechanism.
/// </summary>
public static class RetryableFailureReasons
{
    /// <summary>
    /// Keepalive-stall: no runtime event observed within the stall deadline.
    /// The run is considered orphaned and may succeed on retry.
    /// </summary>
    public const string KeepaliveStall = "keepalive_stall";

    /// <summary>
    /// Process exited with non-zero code without emitting a terminal event.
    /// The runtime process crashed or was killed; retry may succeed.
    /// </summary>
    public const string ProcessExitNonZero = "process_exit_nonzero";

    /// <summary>
    /// Runtime process failed to start (e.g. executable not found).
    /// Retry may succeed if the environment is transiently unavailable.
    /// </summary>
    public const string ProcessStartFailed = "process_start_failed";

    /// <summary>
    /// Environment unreachable during runtime execution.
    /// Retry may succeed if the environment recovers.
    /// </summary>
    public const string EnvironmentUnreachable = "environment_unreachable";

    /// <summary>
    /// Pre-accept setup failure: environment provisioning failed.
    /// The workspace or runtime environment could not be created; retry may succeed.
    /// </summary>
    public const string EnvironmentProvisioningFailed = "environment_provisioning_failed";

    /// <summary>
    /// Pre-accept setup failure: repository clone failed.
    /// The source repository could not be cloned; retry may succeed.
    /// </summary>
    public const string RepositoryCloneFailed = "repository_clone_failed";

    /// <summary>
    /// Returns the complete set of retryable failure reason strings.
    /// Failures with a reason in this set are eligible for run-level retry.
    /// </summary>
    public static IReadOnlySet<string> AllRetryableReasons { get; } = new HashSet<string>
    {
        KeepaliveStall,
        ProcessExitNonZero,
        ProcessStartFailed,
        EnvironmentUnreachable,
        EnvironmentProvisioningFailed,
        RepositoryCloneFailed,
    };

    /// <summary>
    /// Returns <c>true</c> if the failure is classified as retryable based on
    /// the <paramref name="reason"/> string or the presence of <c>"retryable": true</c>
    /// in the <paramref name="payload"/>.
    /// </summary>
    public static bool IsRetryable(string? reason, IReadOnlyDictionary<string, object?>? payload)
    {
        // Explicit retryable flag in payload takes precedence
        if (payload?.TryGetValue("retryable", out var retryableValue) == true &&
            retryableValue is bool isRetryable)
        {
            return isRetryable;
        }

        // Check against known retryable reasons
        if (!string.IsNullOrWhiteSpace(reason))
        {
            return AllRetryableReasons.Contains(reason);
        }

        return false;
    }
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

    /// <summary>Claim was released due to a pre-agent setup failure (e.g. clone failure).
    /// Agent tags stripped, work item reverted to eligible state.</summary>
    public const string ClaimReleased = "controller.claim_released";

    /// <summary>Run failed with a retryable error. The controller will evaluate
    /// the retry threshold before escalating to NeedsHuman.</summary>
    public const string RetryableFailure = "controller.retryable_failure";

    /// <summary>Retry run created for a work item after a previous run failed
    /// with a retryable error.</summary>
    public const string RetryRunCreated = "controller.retry_run_created";

    /// <summary>Run escalated to NeedsHuman after exhausting all retry attempts.</summary>
    public const string RetryExhausted = "controller.retry_exhausted";

    /// <summary>Work-source projection failed (best-effort). The controller internal
    /// state transition succeeded but the external work source could not be updated.</summary>
    public const string WorkSourceProjectionFailed = "controller.work_source_projection_failed";
}
