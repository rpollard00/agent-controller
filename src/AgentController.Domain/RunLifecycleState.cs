namespace AgentController.Domain;

/// <summary>
/// Controller-authoritative lifecycle states for an agent run.
/// These are internal states; Azure DevOps Board state is a separate projection.
/// </summary>
public enum RunLifecycleState
{
    /// <summary>Run record created, not yet claimed.</summary>
    Queued = 0,

    /// <summary>Work item claimed by this controller instance.</summary>
    Claimed,

    /// <summary>Environment provisioning in progress.</summary>
    EnvironmentProvisioning,

    /// <summary>Environment is ready for use.</summary>
    EnvironmentReady,

    /// <summary>Repository cloning in progress.</summary>
    RepositoryCloning,

    /// <summary>Repository cloned and ready.</summary>
    RepositoryReady,

    /// <summary>Context files written, ready for agent start.</summary>
    ContextInjected,

    /// <summary>Agent runtime start requested.</summary>
    AgentStarting,

    /// <summary>Agent runtime is executing.</summary>
    AgentRunning,

    /// <summary>Waiting for final result from the runtime.</summary>
    AwaitingResult,

    /// <summary>Result received but not yet fully processed.</summary>
    ResultReceived,

    /// <summary>Pull request opened by the runtime.</summary>
    PrOpened,

    /// <summary>Branch pushed without a PR.</summary>
    BranchPushed,

    /// <summary>Runtime requested human input or review.</summary>
    NeedsHuman,

    /// <summary>Run completed successfully.</summary>
    Completed,

    /// <summary>Run failed with an error.</summary>
    Failed,

    /// <summary>Run was cancelled by the controller.</summary>
    Cancelled,

    /// <summary>Cleanup is pending.</summary>
    CleanupPending,

    /// <summary>Cleanup completed, run is terminal.</summary>
    CleanedUp,
}
