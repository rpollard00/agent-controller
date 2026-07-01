namespace AgentController.Domain;

/// <summary>
/// Extension helpers for <see cref="RunLifecycleState"/> classification.
/// Provides a single source of truth for "active for concurrency" and
/// "terminal" so that infrastructure and API layers cannot drift apart.
/// </summary>
public static class RunLifecycleStateExtensions
{
    /// <summary>
    /// Returns true when the run's agent runtime is still actively executing
    /// or being staged (Claimed through AwaitingResult).
    ///
    /// Post-execution states (ResultReceived, PrOpened, BranchPushed, NeedsHuman)
    /// and terminal states do NOT count as active for concurrency purposes.
    /// </summary>
    public static bool IsActiveForConcurrency(this RunLifecycleState state)
    {
        return state is
            RunLifecycleState.Claimed
            or RunLifecycleState.EnvironmentProvisioning
            or RunLifecycleState.EnvironmentReady
            or RunLifecycleState.RepositoryCloning
            or RunLifecycleState.RepositoryReady
            or RunLifecycleState.ContextInjected
            or RunLifecycleState.AgentStarting
            or RunLifecycleState.AgentRunning
            or RunLifecycleState.AwaitingResult;
    }

    /// <summary>
    /// Returns true when the run has reached an irrecoverable terminal state.
    /// Terminal states: Completed, Failed, Cancelled, CleanedUp.
    ///
    /// Note: this is distinct from concurrency classification.
    /// For example, NeedsHuman is non-terminal (the graph can still advance)
    /// but is also non-active for concurrency (the runtime has stopped).
    /// </summary>
    public static bool IsTerminal(this RunLifecycleState state)
    {
        return state is
            RunLifecycleState.Completed
            or RunLifecycleState.Failed
            or RunLifecycleState.Cancelled
            or RunLifecycleState.CleanedUp;
    }
}
