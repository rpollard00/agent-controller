using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Application service that owns <see cref="AgentRunHandle"/> lifecycle transitions.
///
/// Coordinates <see cref="IAgentRunStore"/>, <see cref="ILifecycleEventStore"/>, and
/// <see cref="IWorkItemStore"/> to ensure consistent state transitions. Every transition
/// appends a controller lifecycle event. The service validates that transitions follow
/// the legal state graph.
///
/// Shared by the worker polling loop (controller-owned state advancement) and the
/// runtime event ingestion endpoint (runtime-driven transitions after AwaitingResult).
/// </summary>
public interface IRunLifecycleService
{
    /// <summary>
    /// Create a new <see cref="AgentRunHandle"/> for a claimed work item.
    /// The run is created in <see cref="RunLifecycleState.Claimed"/> state and a
    /// "controller.claimed" lifecycle event is appended.
    /// </summary>
    /// <param name="workItemId">Identifier of the claimed work item.</param>
    /// <param name="workerId">Identifier of the worker/controller instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created run handle.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the work item is not found.
    /// </exception>
    Task<AgentRunHandle> CreateRunForWorkItemAsync(
        string workItemId,
        string workerId,
        CancellationToken ct);

    /// <summary>
    /// Transition a run to the specified <paramref name="targetState"/>.
    /// Validates that the transition is legal (forward-only progression,
    /// no transitions from terminal states). Appends a "controller.state_transition"
    /// lifecycle event recording the previous and new state.
    /// </summary>
    /// <param name="runId">Identifier of the run to transition.</param>
    /// <param name="targetState">Target lifecycle state.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the run is not found, is in a terminal state,
    /// or the transition is not allowed.
    /// </exception>
    Task TransitionAsync(
        string runId,
        RunLifecycleState targetState,
        CancellationToken ct);

    /// <summary>
    /// Append a controller-authored lifecycle event for a run.
    /// The <paramref name="eventType"/> is automatically prefixed with "controller."
    /// if it does not already start with that prefix.
    /// </summary>
    /// <param name="runId">Identifier of the run.</param>
    /// <param name="eventType">
    /// Event type (e.g. "claimed", "environment_ready").
    /// See <see cref="ControllerEventTypes"/> for well-known values.
    /// </param>
    /// <param name="message">Human-readable message.</param>
    /// <param name="payload">Optional structured payload.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AppendControllerEventAsync(
        string runId,
        string eventType,
        string message,
        IReadOnlyDictionary<string, object?>? payload,
        CancellationToken ct);

    /// <summary>
    /// Ingest and process a runtime event targeting a run.
    ///
    /// Checks idempotency via <see cref="RuntimeEvent.EventId"/>.
    /// Dispatches based on <see cref="RuntimeEvent.EventType"/>:
    /// <list type="bullet">
    ///   <item><c>runtime.accepted</c> — transitions to AgentRunning, records runtime run id</item>
    ///   <item><c>runtime.heartbeat</c> — updates LastHeartbeatAt</item>
    ///   <item><c>runtime.status</c> — appends lifecycle event without state change</item>
    ///   <item><c>runtime.completed</c> — transitions to Completed/PrOpened/BranchPushed based on payload.outcome</item>
    ///   <item><c>runtime.failed</c> — transitions to Failed, records error</item>
    ///   <item><c>runtime.needs_human</c> — transitions to NeedsHuman</item>
    ///   <item><c>runtime.cancelled</c> — transitions to Cancelled</item>
    /// </list>
    /// </summary>
    /// <param name="evt">The runtime event to ingest.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when:
    /// <list type="bullet">
    ///   <item>The event was already processed (duplicate eventId).</item>
    ///   <item>The run is not found.</item>
    ///   <item>The run is in a terminal state.</item>
    ///   <item>The event type is unsupported.</item>
    ///   <item>Required fields are missing.</item>
    /// </list>
    /// </exception>
    Task IngestRuntimeEventAsync(
        RuntimeEvent evt,
        CancellationToken ct);

    /// <summary>
    /// Find runs stuck in <see cref="RunLifecycleState.AwaitingResult"/> past the
    /// specified <paramref name="staleTimeout"/>. These runs have not received a
    /// heartbeat or final event within the timeout window.
    /// </summary>
    /// <param name="staleTimeout">
    /// Maximum allowed time since the last heartbeat (or run start if no heartbeat)
    /// before a run is considered stale.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of stale runs, ordered by least-recently-active first.</returns>
    Task<IReadOnlyList<AgentRunHandle>> FindStaleRunsAsync(
        TimeSpan staleTimeout,
        CancellationToken ct);

    /// <summary>
    /// Recover a stale run by transitioning it to <see cref="RunLifecycleState.NeedsHuman"/>.
    /// Appends a "controller.stale_recovered" lifecycle event with severity Warning
    /// documenting the timeout.
    /// </summary>
    /// <param name="runId">Identifier of the stale run to recover.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the run is not found or is in a terminal state.
    /// </exception>
    Task RecoverStaleRunAsync(
        string runId,
        CancellationToken ct);

    /// <summary>
    /// Returns <c>true</c> when the given <paramref name="state"/> is terminal
    /// and no further lifecycle transitions are allowed.
    /// Terminal states: <see cref="RunLifecycleState.Completed"/>,
    /// <see cref="RunLifecycleState.Failed"/>, <see cref="RunLifecycleState.Cancelled"/>,
    /// <see cref="RunLifecycleState.CleanedUp"/>.
    /// </summary>
    bool IsTerminal(RunLifecycleState state);
}
