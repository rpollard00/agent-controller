using AgentController.Application.Abstractions;
using AgentController.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Application.Services;

/// <summary>
/// Application service that owns <see cref="AgentRunHandle"/> lifecycle transitions.
///
/// Coordinates <see cref="IAgentRunStore"/>, <see cref="ILifecycleEventStore"/>, and
/// <see cref="IWorkItemStore"/> to ensure consistent state transitions. Every transition
/// appends a controller lifecycle event. The service validates that transitions follow
/// the legal state graph.
///
/// Registered as scoped so it shares the same unit-of-work (DbContext) as the
/// stores it coordinates.
/// </summary>
internal sealed partial class RunLifecycleService : IRunLifecycleService
{
    private readonly ILogger<RunLifecycleService> _logger;
    private readonly IAgentRunStore _runStore;
    private readonly ILifecycleEventStore _eventStore;
    private readonly IWorkItemStore _workItemStore;
    private readonly IWorkSource _workSource;
    private readonly IOptionsMonitor<WorkSourceOptionsView> _workSourceOptions;
    private readonly IManagedProfileResolver? _profileResolver;

    /// <summary>
    /// Legal state transitions.
    /// The key is the current state, the values are allowed target states.
    /// Any state not listed as a key can only transition to states defined by
    /// runtime events (handled separately in <see cref="IngestRuntimeEventAsync"/>).
    /// </summary>
    private static readonly Dictionary<
        RunLifecycleState,
        HashSet<RunLifecycleState>
    > AllowedTransitions = new()
    {
        [RunLifecycleState.Queued] = [RunLifecycleState.Claimed],
        [RunLifecycleState.Claimed] = [RunLifecycleState.EnvironmentProvisioning],
        [RunLifecycleState.EnvironmentProvisioning] = [RunLifecycleState.EnvironmentReady],
        [RunLifecycleState.EnvironmentReady] = [RunLifecycleState.RepositoryCloning],
        [RunLifecycleState.RepositoryCloning] = [RunLifecycleState.RepositoryReady],
        [RunLifecycleState.RepositoryReady] = [RunLifecycleState.ContextInjected],
        [RunLifecycleState.ContextInjected] = [RunLifecycleState.AgentStarting],
        [RunLifecycleState.AgentStarting] = [RunLifecycleState.AgentRunning],
        [RunLifecycleState.AgentRunning] = [RunLifecycleState.AwaitingResult],
        // AwaitingResult transitions are driven by runtime events, not TransitionAsync.
        // ResultReceived transitions to these terminal/resolution states:
        [RunLifecycleState.ResultReceived] =
        [
            RunLifecycleState.PrOpened,
            RunLifecycleState.BranchPushed,
            RunLifecycleState.Completed,
            RunLifecycleState.NeedsHuman,
            RunLifecycleState.Failed,
        ],
        [RunLifecycleState.PrOpened] = [RunLifecycleState.Completed],
        [RunLifecycleState.BranchPushed] = [RunLifecycleState.Completed],
        // Terminal and near-terminal:
        [RunLifecycleState.Completed] = [RunLifecycleState.CleanupPending],
        [RunLifecycleState.Failed] = [RunLifecycleState.CleanupPending],
        [RunLifecycleState.Cancelled] = [RunLifecycleState.CleanupPending],
        [RunLifecycleState.NeedsHuman] = [RunLifecycleState.CleanupPending],
        [RunLifecycleState.CleanupPending] = [RunLifecycleState.CleanedUp],
    };

    public RunLifecycleService(
        ILogger<RunLifecycleService> logger,
        IAgentRunStore runStore,
        ILifecycleEventStore eventStore,
        IWorkItemStore workItemStore,
        IWorkSource workSource,
        IOptionsMonitor<WorkSourceOptionsView> workSourceOptions,
        IManagedProfileResolver? profileResolver = null
    )
    {
        _logger = logger;
        _runStore = runStore;
        _eventStore = eventStore;
        _workItemStore = workItemStore;
        _workSource = workSource;
        _workSourceOptions = workSourceOptions;
        _profileResolver = profileResolver;
    }

    /// <inheritdoc />
    public async Task<AgentRunHandle> CreateRunForWorkItemAsync(
        string workItemId,
        string workerId,
        CancellationToken ct
    )
    {
        var workItem = await _workItemStore.GetByIdAsync(workItemId, ct);
        if (workItem is null)
        {
            throw new InvalidOperationException(
                $"Cannot create run: work item '{workItemId}' not found."
            );
        }

        var run = await _runStore.CreateAsync(
            new CreateRunRequest
            {
                WorkItemId = workItemId,
                WorkerId = workerId,
                InitialStatus = RunLifecycleState.Claimed,
            },
            ct
        );

        await AppendControllerEventAsync(
            run.RunId,
            ControllerEventTypes.Claimed,
            $"Run created for work item '{workItemId}' (title: '{workItem.Title}').",
            new Dictionary<string, object?>
            {
                ["workItemId"] = workItemId,
                ["workItemTitle"] = workItem.Title,
                ["repoKey"] = workItem.RepoKey,
            },
            ct
        );

        // Project claim tags (agent-active) to the external work source.
        // Board state (ActiveState) is NOT set here — it is gated on
        // runtime.accepted in HandleAcceptedAsync so the board only goes
        // Active once pi-materia confirms a successful agent start.
        await MaybeProjectToWorkSource(run, RunLifecycleState.Claimed, ct);

        return run;
    }

    /// <inheritdoc />
    public async Task TransitionAsync(
        string runId,
        RunLifecycleState targetState,
        CancellationToken ct
    )
    {
        var run = await _runStore.GetByIdAsync(runId, ct);
        if (run is null)
        {
            throw new InvalidOperationException($"Cannot transition run '{runId}': run not found.");
        }

        if (IsTerminal(run.Status))
        {
            throw new InvalidOperationException(
                $"Cannot transition run '{runId}': run is in terminal state '{run.Status}'."
            );
        }

        if (!IsTransitionAllowed(run.Status, targetState))
        {
            throw new InvalidOperationException(
                $"Cannot transition run '{runId}' from '{run.Status}' to '{targetState}': "
                    + "transition is not allowed. Use runtime event ingestion for "
                    + "AwaitingResult → terminal transitions."
            );
        }

        var previousState = run.Status;

        await _runStore.UpdateStatusAsync(runId, targetState, ct);

        await AppendControllerEventAsync(
            runId,
            ControllerEventTypes.StateTransition,
            $"State transition: {previousState} → {targetState}.",
            new Dictionary<string, object?>
            {
                ["previousState"] = previousState.ToString(),
                ["targetState"] = targetState.ToString(),
            },
            ct
        );

        // Update work item status for key controller-owned transitions
        await MaybeUpdateWorkItemStatus(run, targetState, ct);
    }

    /// <inheritdoc />
    public async Task AppendControllerEventAsync(
        string runId,
        string eventType,
        string message,
        IReadOnlyDictionary<string, object?>? payload,
        CancellationToken ct
    )
    {
        await AppendControllerEventAsync(
            runId,
            eventType,
            message,
            payload,
            EventSeverity.Info,
            ct
        );
    }

    /// <inheritdoc />
    public async Task AppendControllerEventAsync(
        string runId,
        string eventType,
        string message,
        IReadOnlyDictionary<string, object?>? payload,
        EventSeverity severity,
        CancellationToken ct
    )
    {
        var normalizedType = eventType.StartsWith("controller.", StringComparison.Ordinal)
            ? eventType
            : $"controller.{eventType}";

        await _eventStore.AppendAsync(
            new LifecycleEvent
            {
                RunId = runId,
                EventType = normalizedType,
                Severity = severity,
                Message = message,
                Payload = payload,
            },
            ct
        );
    }

    /// <inheritdoc />
    public async Task IngestRuntimeEventAsync(RuntimeEvent evt, CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var previousState = default(RunLifecycleState?);

        try
        {
            if (string.IsNullOrWhiteSpace(evt.EventId))
            {
                throw new InvalidOperationException(
                    "Runtime event is missing required field 'eventId'."
                );
            }

            if (string.IsNullOrWhiteSpace(evt.RunId))
            {
                throw new InvalidOperationException(
                    "Runtime event is missing required field 'runId'."
                );
            }

            if (string.IsNullOrWhiteSpace(evt.EventType))
            {
                throw new InvalidOperationException(
                    "Runtime event is missing required field 'eventType'."
                );
            }

            if (!Enum.IsDefined(evt.Severity))
            {
                throw new InvalidOperationException(
                    $"Unsupported severity value {(int)evt.Severity}. "
                        + $"Valid values: {string.Join(", ", Enum.GetNames<EventSeverity>())}."
                );
            }

            // Idempotency check
            var alreadyExists = await _eventStore.ExistsByEventIdAsync(evt.RunId, evt.EventId, ct);
            if (alreadyExists)
            {
                throw new InvalidOperationException(
                    $"Runtime event '{evt.EventId}' has already been processed for run '{evt.RunId}'."
                );
            }

            var run = await _runStore.GetByIdAsync(evt.RunId, ct);
            if (run is null)
            {
                throw new InvalidOperationException(
                    $"Cannot ingest runtime event for run '{evt.RunId}': run not found."
                );
            }

            if (IsTerminal(run.Status))
            {
                throw new InvalidOperationException(
                    $"Cannot ingest runtime event for run '{evt.RunId}': "
                        + $"run is in terminal state '{run.Status}'."
                );
            }

            previousState = run.Status;

            // Dispatch by event type
            switch (evt.EventType)
            {
                case RuntimeEventTypes.Accepted:
                    await HandleAcceptedAsync(run, evt, ct);
                    break;

                case RuntimeEventTypes.Heartbeat:
                    await HandleHeartbeatAsync(run, evt, ct);
                    break;

                case RuntimeEventTypes.Status:
                    await HandleStatusAsync(run, evt, ct);
                    break;

                case RuntimeEventTypes.Completed:
                    await HandleCompletedAsync(run, evt, ct);
                    break;

                case RuntimeEventTypes.Failed:
                    await HandleFailedAsync(run, evt, ct);
                    break;

                case RuntimeEventTypes.FailedRetryable:
                    await HandleFailedRetryableAsync(run, evt, ct);
                    break;

                case RuntimeEventTypes.NeedsHuman:
                    await HandleNeedsHumanAsync(run, evt, ct);
                    break;

                case RuntimeEventTypes.Cancelled:
                    await HandleCancelledAsync(run, evt, ct);
                    break;

                // Well-known but not yet implemented transitions
                case RuntimeEventTypes.BranchCreated:
                case RuntimeEventTypes.PrCreated:
                    await HandleInformationalEventAsync(run, evt, ct);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported runtime event type '{evt.EventType}'."
                    );
            }

            // Reload the run to observe the new state after dispatch
            var updatedRun = await _runStore.GetByIdAsync(evt.RunId, ct);
            var newState = updatedRun?.Status;

            // Classify the dispatch outcome and log accordingly
            if (IsNoOpEventType(evt.EventType))
            {
                Log.RuntimeEventNoOp(
                    _logger,
                    evt.EventType,
                    evt.RunId,
                    evt.EventId,
                    previousState.Value,
                    stopwatch.Elapsed.TotalMilliseconds
                );
            }
            else if (newState is not null && newState != previousState)
            {
                Log.RuntimeEventTransition(
                    _logger,
                    evt.EventType,
                    evt.RunId,
                    evt.EventId,
                    previousState.Value,
                    newState.Value,
                    stopwatch.Elapsed.TotalMilliseconds
                );
            }
            else
            {
                // State didn't change but it wasn't a known no-op (e.g. accepted on AwaitingResult)
                Log.RuntimeEventNoOp(
                    _logger,
                    evt.EventType,
                    evt.RunId,
                    evt.EventId,
                    previousState.Value,
                    stopwatch.Elapsed.TotalMilliseconds
                );
            }

            // Record the ingested event in the controller event log
            await _eventStore.AppendAsync(
                new LifecycleEvent
                {
                    RunId = evt.RunId,
                    EventId = evt.EventId,
                    EventType = evt.EventType,
                    Severity = evt.Severity,
                    Message = evt.Message,
                    Payload = evt.Payload,
                    CreatedAt = evt.OccurredAt,
                },
                ct
            );

            await AppendControllerEventAsync(
                evt.RunId,
                ControllerEventTypes.RuntimeEventIngested,
                $"Ingested runtime event '{evt.EventId}' of type '{evt.EventType}'.",
                new Dictionary<string, object?>
                {
                    ["eventId"] = evt.EventId,
                    ["eventType"] = evt.EventType,
                    ["severity"] = evt.Severity.ToString(),
                },
                ct
            );
        }
        catch (Exception ex)
        {
            Log.RuntimeEventDispatchError(
                _logger,
                ex,
                evt.EventType,
                evt.RunId ?? "(unknown)",
                evt.EventId ?? "(unknown)",
                stopwatch.Elapsed.TotalMilliseconds
            );
            throw;
        }
    }

    /// <summary>
    /// Returns <c>true</c> for event types that never change the run state
    /// (heartbeat, status, and informational events like branch_created/pr_created).
    /// </summary>
    private static bool IsNoOpEventType(string eventType)
    {
        return eventType
            is RuntimeEventTypes.Heartbeat
                or RuntimeEventTypes.Status
                or RuntimeEventTypes.BranchCreated
                or RuntimeEventTypes.PrCreated;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentRunHandle>> FindStaleRunsAsync(
        TimeSpan staleTimeout,
        CancellationToken ct
    )
    {
        return await _runStore.FindStaleAsync(staleTimeout, ct);
    }

    /// <inheritdoc />
    public async Task RecoverStaleRunAsync(string runId, CancellationToken ct)
    {
        var run = await _runStore.GetByIdAsync(runId, ct);
        if (run is null)
        {
            throw new InvalidOperationException(
                $"Cannot recover stale run '{runId}': run not found."
            );
        }

        if (IsTerminal(run.Status))
        {
            throw new InvalidOperationException(
                $"Cannot recover stale run '{runId}': run is already in terminal state '{run.Status}'."
            );
        }

        if (
            run.Status != RunLifecycleState.AwaitingResult
            && run.Status != RunLifecycleState.AgentRunning
        )
        {
            throw new InvalidOperationException(
                $"Cannot recover stale run '{runId}': run is in state '{run.Status}', "
                    + "only runs in AwaitingResult or AgentRunning can be recovered as stale."
            );
        }

        await _runStore.UpdateStatusAsync(runId, RunLifecycleState.NeedsHuman, ct);
        await MaybeUpdateWorkItemStatus(run, RunLifecycleState.NeedsHuman, ct);

        await AppendControllerEventAsync(
            runId,
            ControllerEventTypes.StaleRecovered,
            $"Stale run recovered: no heartbeat or final event received within the timeout. "
                + $"Transitioned from {run.Status} to {RunLifecycleState.NeedsHuman}.",
            new Dictionary<string, object?>
            {
                ["previousState"] = run.Status.ToString(),
                ["targetState"] = RunLifecycleState.NeedsHuman.ToString(),
                ["lastHeartbeatAt"] = run.LastHeartbeatAt?.ToString("O"),
                ["startedAt"] = run.StartedAt?.ToString("O"),
            },
            EventSeverity.Warning,
            ct
        );
    }

    /// <inheritdoc />
    public bool IsTerminal(RunLifecycleState state)
    {
        return state switch
        {
            RunLifecycleState.Completed => true,
            RunLifecycleState.Failed => true,
            RunLifecycleState.Cancelled => true,
            RunLifecycleState.CleanedUp => true,
            _ => false,
        };
    }

    // ── Private helpers ──────────────────────────────────────────────

    private static bool IsTransitionAllowed(RunLifecycleState current, RunLifecycleState target)
    {
        if (current == target)
            return true;

        // Cancellation and failure are always allowed from any non-terminal state.
        // Failure transitions may come from environment provisioning, repository
        // cloning, or runtime handoff errors.
        if (target == RunLifecycleState.Cancelled && current != RunLifecycleState.CleanedUp)
            return true;

        if (target == RunLifecycleState.Failed && !IsTerminalStatic(current))
            return true;

        if (AllowedTransitions.TryGetValue(current, out var allowed))
            return allowed.Contains(target);

        return false;
    }

    private static bool IsTerminalStatic(RunLifecycleState state)
    {
        return state switch
        {
            RunLifecycleState.Completed => true,
            RunLifecycleState.Failed => true,
            RunLifecycleState.Cancelled => true,
            RunLifecycleState.CleanedUp => true,
            _ => false,
        };
    }

    private async Task MaybeUpdateWorkItemStatus(
        AgentRunHandle run,
        RunLifecycleState targetState,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(run.WorkItemId))
            return;

        var workItem = await _workItemStore.GetByIdAsync(run.WorkItemId, ct);
        if (workItem is null)
            return;

        // Managed board-state settings follow the environment that produced the
        // work item. Appsettings remain the fallback for legacy or missing keys.
        var states = await ResolveWorkSourceStatesAsync(workItem, ct);
        var status = targetState switch
        {
            // Note: Claimed does NOT project board state here — that is gated
            // on runtime.accepted in HandleAcceptedAsync.
            RunLifecycleState.EnvironmentProvisioning => states.ActiveState,
            RunLifecycleState.AgentRunning => states.ActiveState,
            RunLifecycleState.AwaitingResult => states.ActiveState,
            RunLifecycleState.Completed => states.CompletedState,
            RunLifecycleState.PrOpened => states.CompletedState,
            RunLifecycleState.BranchPushed => states.CompletedState,
            // Failed/needs_human/cancelled remain in active state (or unchanged)
            // so they stay visible on the active board columns.
            _ => null,
        };

        if (status is not null)
        {
            await _workItemStore.UpdateStatusAsync(run.WorkItemId, status, ct);
        }

        // Project status to the external work source (Phase 2 feedback loop).
        // Best-effort: failures in external projection are not fatal to the
        // controller's internal state transition.
        await MaybeProjectToWorkSource(run, workItem, targetState, states, ct);
    }

    /// <summary>
    /// Project controller state to the external work source when the work
    /// item originated from one (e.g. Azure DevOps Boards).
    ///
    /// Builds an <see cref="ExternalWorkStatus"/> from the current lifecycle
    /// state and calls <see cref="IWorkSource.UpdateStatusAsync"/> and
    /// <see cref="IWorkSource.AddCommentAsync"/> for key lifecycle milestones.
    ///
    /// This is best-effort; failures are caught and do not prevent the
    /// controller's internal state transition from completing.
    /// </summary>
    private async Task MaybeProjectToWorkSource(
        AgentRunHandle run,
        RunLifecycleState targetState,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(run.WorkItemId))
            return;

        var workItem = await _workItemStore.GetByIdAsync(run.WorkItemId, ct);
        if (workItem is null)
            return;

        var states = await ResolveWorkSourceStatesAsync(workItem, ct);
        await MaybeProjectToWorkSource(run, workItem, targetState, states, ct);
    }

    private async Task MaybeProjectToWorkSource(
        AgentRunHandle run,
        WorkCandidate workItem,
        RunLifecycleState targetState,
        WorkSourceStates states,
        CancellationToken ct
    )
    {
        // Only project for work items that have an external source.
        if (
            string.IsNullOrWhiteSpace(workItem.ExternalId)
            || string.IsNullOrWhiteSpace(workItem.Source)
            || workItem.Source == "LocalFake"
        )
            return;

        var revision =
            workItem.SourceMetadata?.TryGetValue("revision", out var rev) == true ? rev : null;

        var workRef = new ExternalWorkRef
        {
            Source = workItem.Source,
            ExternalId = workItem.ExternalId,
            Url = workItem.ExternalUrl,
            Revision = revision,
            EnvironmentKey = GetAzureDevOpsEnvironmentKey(workItem.SourceMetadata),
        };

        // Determine status update (tags, state) and comment for this lifecycle milestone.
        var (extStatus, comment) = BuildExternalProjection(targetState, run, states);

        try
        {
            if (extStatus is not null)
            {
                await _workSource.UpdateStatusAsync(workRef, extStatus, ct);
            }

            if (!string.IsNullOrWhiteSpace(comment))
            {
                await _workSource.AddCommentAsync(workRef, comment, ct);
            }
        }
        catch (Exception ex)
        {
            // Best-effort projection: external work source failures are not
            // fatal to the controller's internal state transition. The next
            // poll cycle may retry through QueryWorkItemsAsync.
            // However, we log the failure and record a lifecycle event so it is observable.
            Log.WorkSourceProjectionFailed(
                _logger,
                ex,
                run.RunId,
                run.WorkItemId ?? string.Empty,
                workItem.ExternalId,
                workItem.Source,
                targetState
            );

            await _eventStore.AppendAsync(
                new LifecycleEvent
                {
                    RunId = run.RunId,
                    EventType = ControllerEventTypes.WorkSourceProjectionFailed,
                    Severity = EventSeverity.Warning,
                    Message =
                        $"Work-source projection failed for run {run.RunId} "
                        + $"(workItemId={run.WorkItemId}, externalId={workItem.ExternalId}, "
                        + $"source={workItem.Source}, targetState={targetState}): {ex.Message}",
                    Payload = new Dictionary<string, object?>
                    {
                        ["runId"] = run.RunId,
                        ["workItemId"] = run.WorkItemId,
                        ["externalId"] = workItem.ExternalId,
                        ["source"] = workItem.Source,
                        ["targetState"] = targetState.ToString(),
                        ["error"] = ex.Message,
                    },
                },
                ct
            );
        }
    }

    /// <summary>
    /// Builds the external status update and comment for a lifecycle state
    /// transition. Returns <c>null</c> for states that do not require
    /// external projection.
    ///
    /// Uses the selected managed environment's <c>ActiveState</c> and
    /// <c>CompletedState</c>, with appsettings as the legacy fallback.
    /// This makes the projection idempotent: re-projecting the same state
    /// is a no-op at the ADO API level (PATCH with the same value is harmless).
    /// </summary>
    private static (ExternalWorkStatus? Status, string? Comment) BuildExternalProjection(
        RunLifecycleState targetState,
        AgentRunHandle run,
        WorkSourceStates states
    )
    {
        return targetState switch
        {
            // ── Claim: add claim tag only, NO board state change ────
            // Board state (ActiveState) is gated on runtime.accepted in
            // HandleAcceptedAsync so the board only goes Active once
            // pi-materia confirms a successful agent start.
            RunLifecycleState.Claimed => (
                new ExternalWorkStatus { Tags = ["agent-active"] },
                "Agent controller claimed this work item and started processing."
            ),

            // ── Agent running: ensure active state ──────────────────
            RunLifecycleState.AgentRunning => (
                new ExternalWorkStatus { Status = states.ActiveState },
                "Agent runtime is now executing."
            ),

            // ── Awaiting result: ensure active state (idempotent) ───
            RunLifecycleState.AwaitingResult => (
                new ExternalWorkStatus { Status = states.ActiveState },
                "Agent runtime is working; awaiting result."
            ),

            // ── PR opened: move to completed state ──────────────────
            RunLifecycleState.PrOpened => (
                new ExternalWorkStatus { Status = states.CompletedState },
                !string.IsNullOrWhiteSpace(run.PullRequestUrl)
                    ? $"Pull request opened: {run.PullRequestUrl}"
                    : "Pull request opened."
            ),

            // ── Branch pushed: move to completed state ──────────────
            RunLifecycleState.BranchPushed => (
                new ExternalWorkStatus { Status = states.CompletedState },
                !string.IsNullOrWhiteSpace(run.BranchName)
                    ? $"Branch pushed: {run.BranchName}"
                    : "Branch pushed to remote."
            ),

            // ── Completed: move to completed state ──────────────────
            RunLifecycleState.Completed => (
                new ExternalWorkStatus { Status = states.CompletedState },
                !string.IsNullOrWhiteSpace(run.ResultSummary)
                    ? $"Run completed: {run.ResultSummary}"
                    : "Run completed successfully."
            ),

            // ── Failed: comment only, no agent-failed tag ──
            // A bad runtime environment should not dirty the external record.
            // For pre-agent setup failures (clone, environment), the claim is
            // released via ReleaseClaimAsync which strips agent-active/agent-worker
            // tags and reverts the item to an eligible state.
            // For runtime failures, the work item stays in active state for visibility.
            RunLifecycleState.Failed => (
                null,
                !string.IsNullOrWhiteSpace(run.Error) ? $"Run failed: {run.Error}" : "Run failed."
            ),

            // ── Needs human: add needs-human tag (keep in active state) ──
            RunLifecycleState.NeedsHuman => (
                new ExternalWorkStatus { Tags = ["agent-needs-human"] },
                !string.IsNullOrWhiteSpace(run.ResultSummary)
                    ? $"Run requires human input: {run.ResultSummary}"
                    : "Run requires human input."
            ),

            // ── Cancelled: comment only ─────────────────────────────
            RunLifecycleState.Cancelled => (null, "Run was cancelled."),

            _ => (null, null),
        };
    }

    private async Task HandleAcceptedAsync(
        AgentRunHandle run,
        RuntimeEvent evt,
        CancellationToken ct
    )
    {
        // runtime.accepted normally arrives while the run is still before
        // AgentRunning. With a real pi runtime, the PollingWorker advances the
        // run through AgentRunning → AwaitingResult synchronously in a few
        // milliseconds, while pi takes seconds to boot before it POSTs its
        // accepted event. So accepted frequently lands on an AwaitingResult
        // run. Treat accepted as forward-progress before AgentRunning, and as
        // an informational heartbeat-equivalent otherwise — never throw, so
        // the ingestion endpoint does not return 422 for this benign ordering
        // race. (Terminal-state runs are still rejected upstream by
        // IngestRuntimeEventAsync before reaching this handler.)
        if ((int)run.Status < (int)RunLifecycleState.AgentRunning)
        {
            await _runStore.UpdateStatusAsync(run.RunId, RunLifecycleState.AgentRunning, ct);
        }

        // Record runtime fields and refresh the heartbeat in every non-terminal
        // case. This keeps LastHeartbeatAt fresh even when accepted arrives late,
        // so a slow-booting runtime never trips stale-run recovery.
        var update = new RuntimeFieldUpdate
        {
            RuntimeRunId = evt.RuntimeRunId,
            LastHeartbeatAt = evt.OccurredAt,
            StartedAt = run.StartedAt ?? evt.OccurredAt,
        };
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, update, ct);

        // runtime.accepted is the gate for System.State=Active.
        // Now that pi-materia confirmed a successful agent start, project
        // the board to ActiveState. This is idempotent — if TransitionAsync
        // already reached AgentRunning it will re-project the same value.
        await MaybeUpdateWorkItemStatus(run, RunLifecycleState.AgentRunning, ct);
    }

    private async Task HandleHeartbeatAsync(
        AgentRunHandle run,
        RuntimeEvent evt,
        CancellationToken ct
    )
    {
        var update = new RuntimeFieldUpdate { LastHeartbeatAt = evt.OccurredAt };
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, update, ct);
    }

    private async Task HandleStatusAsync(AgentRunHandle run, RuntimeEvent evt, CancellationToken ct)
    {
        // Status events are informational only — no state change.
        // Update runtime fields if the runtime provided its run id.
        if (!string.IsNullOrWhiteSpace(evt.RuntimeRunId))
        {
            var update = new RuntimeFieldUpdate { RuntimeRunId = evt.RuntimeRunId };
            await _runStore.UpdateRuntimeFieldsAsync(run.RunId, update, ct);
        }
    }

    private async Task HandleCompletedAsync(
        AgentRunHandle run,
        RuntimeEvent evt,
        CancellationToken ct
    )
    {
        var outcome = GetPayloadString(evt.Payload, "outcome");
        var targetState = ResolveCompletionState(outcome);

        await _runStore.UpdateStatusAsync(run.RunId, targetState, ct);
        await MaybeUpdateWorkItemStatus(run, targetState, ct);

        // Collect runtime fields from the completed event payload
        var update = new RuntimeFieldUpdate
        {
            RuntimeRunId = evt.RuntimeRunId ?? run.RuntimeRunId,
            BranchName = GetPayloadString(evt.Payload, "branchName") ?? run.BranchName,
            PullRequestUrl = GetPayloadString(evt.Payload, "prUrl") ?? run.PullRequestUrl,
            CommitSha = GetPayloadString(evt.Payload, "commitSha") ?? run.CommitSha,
            ResultSummary =
                GetPayloadString(evt.Payload, "summary") ?? evt.Message ?? run.ResultSummary,
            LastHeartbeatAt = evt.OccurredAt,
            FinishedAt = evt.OccurredAt,
            // When the completion outcome is "failed", carry the error message
            // and reason so the run record is diagnosable.
            Error =
                targetState == RunLifecycleState.Failed
                    ? (evt.Message ?? GetPayloadString(evt.Payload, "summary"))
                    : run.Error,
        };
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, update, ct);
    }

    private async Task HandleFailedAsync(AgentRunHandle run, RuntimeEvent evt, CancellationToken ct)
    {
        await _runStore.UpdateStatusAsync(run.RunId, RunLifecycleState.Failed, ct);
        await MaybeUpdateWorkItemStatus(run, RunLifecycleState.Failed, ct);

        var update = new RuntimeFieldUpdate
        {
            RuntimeRunId = evt.RuntimeRunId ?? run.RuntimeRunId,
            Error = evt.Message ?? GetPayloadString(evt.Payload, "reason"),
            ResultSummary = GetPayloadString(evt.Payload, "summary"),
            LastHeartbeatAt = evt.OccurredAt,
            FinishedAt = evt.OccurredAt,
        };
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, update, ct);
    }

    /// <summary>
    /// Handle a <c>runtime.failed_retryable</c> event. This transitions the run
    /// to Failed (same as runtime.failed) but also records the failure reason
    /// so the controller's retry mechanism can classify it as retryable.
    /// The actual retry decision is made by the PollingWorker or the caller
    /// via <see cref="EvaluateRetryAsync"/>.
    /// </summary>
    private async Task HandleFailedRetryableAsync(
        AgentRunHandle run,
        RuntimeEvent evt,
        CancellationToken ct
    )
    {
        var reason = GetPayloadString(evt.Payload, "reason") ?? evt.Message;

        await _runStore.UpdateStatusAsync(run.RunId, RunLifecycleState.Failed, ct);
        await MaybeUpdateWorkItemStatus(run, RunLifecycleState.Failed, ct);

        var update = new RuntimeFieldUpdate
        {
            RuntimeRunId = evt.RuntimeRunId ?? run.RuntimeRunId,
            Error = evt.Message ?? reason,
            ResultSummary = GetPayloadString(evt.Payload, "summary"),
            LastHeartbeatAt = evt.OccurredAt,
            FinishedAt = evt.OccurredAt,
        };
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, update, ct);

        // Record the failure reason as a controller event for retry classification.
        await AppendControllerEventAsync(
            run.RunId,
            ControllerEventTypes.RetryableFailure,
            $"Run failed with retryable error (reason: {reason ?? "unknown"}). "
                + "The controller will evaluate the retry threshold.",
            new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["runAttempt"] = run.RunAttempt,
                ["retryable"] = true,
            },
            EventSeverity.Warning,
            ct
        );
    }

    private async Task HandleNeedsHumanAsync(
        AgentRunHandle run,
        RuntimeEvent evt,
        CancellationToken ct
    )
    {
        await _runStore.UpdateStatusAsync(run.RunId, RunLifecycleState.NeedsHuman, ct);
        await MaybeUpdateWorkItemStatus(run, RunLifecycleState.NeedsHuman, ct);

        var update = new RuntimeFieldUpdate
        {
            RuntimeRunId = evt.RuntimeRunId ?? run.RuntimeRunId,
            ResultSummary = evt.Message,
            LastHeartbeatAt = evt.OccurredAt,
            FinishedAt = evt.OccurredAt,
        };
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, update, ct);
    }

    private async Task HandleCancelledAsync(
        AgentRunHandle run,
        RuntimeEvent evt,
        CancellationToken ct
    )
    {
        await _runStore.UpdateStatusAsync(run.RunId, RunLifecycleState.Cancelled, ct);
        await MaybeUpdateWorkItemStatus(run, RunLifecycleState.Cancelled, ct);

        var update = new RuntimeFieldUpdate
        {
            RuntimeRunId = evt.RuntimeRunId ?? run.RuntimeRunId,
            LastHeartbeatAt = evt.OccurredAt,
            FinishedAt = evt.OccurredAt,
        };
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, update, ct);
    }

    private async Task HandleInformationalEventAsync(
        AgentRunHandle run,
        RuntimeEvent evt,
        CancellationToken ct
    )
    {
        // branch_created and pr_created are informational — record fields
        // but do not change run status.
        var update = new RuntimeFieldUpdate
        {
            RuntimeRunId = evt.RuntimeRunId ?? run.RuntimeRunId,
            BranchName = GetPayloadString(evt.Payload, "branchName") ?? run.BranchName,
            PullRequestUrl = GetPayloadString(evt.Payload, "prUrl") ?? run.PullRequestUrl,
            CommitSha = GetPayloadString(evt.Payload, "commitSha") ?? run.CommitSha,
            LastHeartbeatAt = evt.OccurredAt,
        };
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, update, ct);
    }

    private async Task<WorkSourceStates> ResolveWorkSourceStatesAsync(
        WorkCandidate workItem,
        CancellationToken ct
    )
    {
        var configured = _workSourceOptions.CurrentValue;
        var fallback = new WorkSourceStates(configured.ActiveState, configured.CompletedState);
        var environmentKey = GetAzureDevOpsEnvironmentKey(workItem.SourceMetadata);

        if (_profileResolver is null || string.IsNullOrWhiteSpace(environmentKey))
        {
            return fallback;
        }

        var environment = await _profileResolver.ResolveWorkSourceEnvironmentAsync(
            environmentKey,
            ct
        );
        return environment?.IsManaged == true
            ? new WorkSourceStates(
                environment.Profile.ActiveState,
                environment.Profile.CompletedState
            )
            : fallback;
    }

    private static string? GetAzureDevOpsEnvironmentKey(
        IReadOnlyDictionary<string, string>? metadata
    )
    {
        return metadata?.TryGetValue("azureDevOpsEnvironmentKey", out var key) == true ? key : null;
    }

    private sealed record WorkSourceStates(string? ActiveState, string? CompletedState);

    private static RunLifecycleState ResolveCompletionState(string? outcome)
    {
        return outcome switch
        {
            CompletionOutcomes.PullRequestOpened => RunLifecycleState.PrOpened,
            CompletionOutcomes.BranchPushed => RunLifecycleState.BranchPushed,
            CompletionOutcomes.PatchCreated => RunLifecycleState.Completed,
            CompletionOutcomes.NoChangesNeeded => RunLifecycleState.Completed,
            CompletionOutcomes.NeedsHuman => RunLifecycleState.NeedsHuman,
            CompletionOutcomes.Failed => RunLifecycleState.Failed,
            _ => throw new InvalidOperationException(
                $"Unsupported completion outcome '{outcome ?? "(null)"}'. "
                    + $"Supported outcomes: {CompletionOutcomes.PullRequestOpened}, "
                    + $"{CompletionOutcomes.BranchPushed}, {CompletionOutcomes.PatchCreated}, "
                    + $"{CompletionOutcomes.NoChangesNeeded}, {CompletionOutcomes.NeedsHuman}, "
                    + $"{CompletionOutcomes.Failed}."
            ),
        };
    }

    private static string? GetPayloadString(
        IReadOnlyDictionary<string, object?>? payload,
        string key
    )
    {
        if (payload is null || !payload.TryGetValue(key, out var value) || value is null)
            return null;

        return value.ToString();
    }

    // ── Run-level retry logic ──────────────────────────────────────

    /// <inheritdoc />
    public async Task<AgentRunHandle?> EvaluateRetryAsync(
        string failedRunId,
        string workerId,
        int maxRunAttempts,
        CancellationToken ct
    )
    {
        var failedRun = await _runStore.GetByIdAsync(failedRunId, ct);
        if (failedRun is null)
        {
            throw new InvalidOperationException(
                $"Cannot evaluate retry: run '{failedRunId}' not found."
            );
        }

        if (failedRun.Status != RunLifecycleState.Failed)
        {
            // Only Failed runs are eligible for retry evaluation.
            // Other terminal states (Completed, Cancelled, CleanedUp) are not retried.
            return null;
        }

        // Check if the failure is retryable by examining the error message
        // and any retryable failure events in the event log.
        var isRetryable = IsFailureRetryable(failedRun);
        if (!isRetryable)
        {
            Log.NonRetryableFailure(_logger, failedRunId, failedRun.Error ?? "unknown");
            return null;
        }

        // Find the latest run for this work item to determine the attempt count.
        var latestRun = await _runStore.FindLatestRunByWorkItemAsync(failedRun.WorkItemId!, ct);

        var currentAttempt = latestRun?.RunAttempt ?? 1;

        if (currentAttempt >= maxRunAttempts)
        {
            // Exhausted all retry attempts — escalate to NeedsHuman.
            await EscalateToNeedsHumanAsync(failedRun, currentAttempt, maxRunAttempts, ct);
            return null;
        }

        // Create a retry run with incremented attempt counter.
        var retryAttempt = currentAttempt + 1;
        var retryRun = await _runStore.CreateAsync(
            new CreateRunRequest
            {
                WorkItemId = failedRun.WorkItemId!,
                WorkerId = workerId,
                InitialStatus = RunLifecycleState.Claimed,
                RunAttempt = retryAttempt,
                PreviousRunId = failedRunId,
            },
            ct
        );

        // Record the retry creation as a controller event.
        await AppendControllerEventAsync(
            retryRun.RunId,
            ControllerEventTypes.RetryRunCreated,
            $"Retry run created (attempt {retryAttempt}/{maxRunAttempts}) "
                + $"for work item '{failedRun.WorkItemId}' after previous run '{failedRunId}' failed.",
            new Dictionary<string, object?>
            {
                ["previousRunId"] = failedRunId,
                ["previousRunStatus"] = failedRun.Status.ToString(),
                ["previousRunError"] = failedRun.Error,
                ["previousRunAttempt"] = currentAttempt,
                ["retryAttempt"] = retryAttempt,
                ["maxRunAttempts"] = maxRunAttempts,
            },
            ct
        );

        // Also record on the failed run for traceability.
        await AppendControllerEventAsync(
            failedRunId,
            ControllerEventTypes.RetryRunCreated,
            $"Retry run '{retryRun.RunId}' created (attempt {retryAttempt}/{maxRunAttempts}).",
            new Dictionary<string, object?>
            {
                ["retryRunId"] = retryRun.RunId,
                ["retryAttempt"] = retryAttempt,
            },
            ct
        );

        Log.RetryRunCreated(_logger, retryRun.RunId, failedRunId, retryAttempt, maxRunAttempts);

        return retryRun;
    }

    /// <inheritdoc />
    public async Task<AgentRunHandle?> RecoverStaleRunWithRetryAsync(
        string staleRunId,
        string workerId,
        int maxRunAttempts,
        CancellationToken ct
    )
    {
        var staleRun = await _runStore.GetByIdAsync(staleRunId, ct);
        if (staleRun is null)
        {
            throw new InvalidOperationException(
                $"Cannot recover stale run '{staleRunId}': run not found."
            );
        }

        if (IsTerminal(staleRun.Status))
        {
            throw new InvalidOperationException(
                $"Cannot recover stale run '{staleRunId}': run is already in terminal state '{staleRun.Status}'."
            );
        }

        if (
            staleRun.Status != RunLifecycleState.AwaitingResult
            && staleRun.Status != RunLifecycleState.AgentRunning
        )
        {
            throw new InvalidOperationException(
                $"Cannot recover stale run '{staleRunId}': run is in state '{staleRun.Status}', "
                    + "only runs in AwaitingResult or AgentRunning can be recovered as stale."
            );
        }

        // StaleTimeout is a non-retryable failure — it goes straight to NeedsHuman.
        // The existing StaleTimeout path continues to escalate directly to NeedsHuman (not retried).
        await _runStore.UpdateStatusAsync(staleRunId, RunLifecycleState.NeedsHuman, ct);
        await MaybeUpdateWorkItemStatus(staleRun, RunLifecycleState.NeedsHuman, ct);

        await AppendControllerEventAsync(
            staleRunId,
            ControllerEventTypes.StaleRecovered,
            $"Stale run recovered: no heartbeat or final event received within the timeout. "
                + $"Transitioned from {staleRun.Status} to {RunLifecycleState.NeedsHuman}. "
                + $"Note: StaleTimeout is a non-retryable failure and does NOT trigger run-level retry.",
            new Dictionary<string, object?>
            {
                ["previousState"] = staleRun.Status.ToString(),
                ["targetState"] = RunLifecycleState.NeedsHuman.ToString(),
                ["lastHeartbeatAt"] = staleRun.LastHeartbeatAt?.ToString("O"),
                ["startedAt"] = staleRun.StartedAt?.ToString("O"),
                ["staleTimeoutIsNonRetryable"] = true,
            },
            EventSeverity.Warning,
            ct
        );

        return null;
    }

    /// <summary>
    /// Escalate a work item to NeedsHuman after exhausting all retry attempts.
    /// Records a summary of all failure reasons on the final failed run.
    /// </summary>
    private async Task EscalateToNeedsHumanAsync(
        AgentRunHandle failedRun,
        int currentAttempt,
        int maxRunAttempts,
        CancellationToken ct
    )
    {
        // Collect failure reasons from the retry chain.
        var failureReasons = new List<string>();
        failureReasons.Add($"Attempt {currentAttempt}: {failedRun.Error ?? "unknown"}");

        // Walk the retry chain to collect all failure reasons.
        var previousRunId = failedRun.PreviousRunId;
        while (previousRunId is not null)
        {
            var previousRun = await _runStore.GetByIdAsync(previousRunId, ct);
            if (previousRun is null)
                break;

            failureReasons.Insert(
                0,
                $"Attempt {previousRun.RunAttempt}: {previousRun.Error ?? "unknown"}"
            );
            previousRunId = previousRun.PreviousRunId;
        }

        var failureSummary = string.Join("; ", failureReasons);

        // Transition the failed run to NeedsHuman (it's already Failed, but we need
        // to escalate it). Since Failed is terminal, we record the escalation as an event
        // rather than a state transition.
        await AppendControllerEventAsync(
            failedRun.RunId,
            ControllerEventTypes.RetryExhausted,
            $"Run escalated to NeedsHuman after {maxRunAttempts} failed attempts. "
                + $"Failure summary: {failureSummary}",
            new Dictionary<string, object?>
            {
                ["maxRunAttempts"] = maxRunAttempts,
                ["failureReasons"] = failureReasons,
                ["failureSummary"] = failureSummary,
            },
            EventSeverity.Error,
            ct
        );

        // Update the local work item to NeedsHuman state.
        if (!string.IsNullOrWhiteSpace(failedRun.WorkItemId))
        {
            await _workItemStore.UpdateStatusAsync(failedRun.WorkItemId, "NeedsHuman", ct);
        }

        // Project NeedsHuman to the external work source (adds agent-needs-human tag).
        // This is best-effort; projection failures are not fatal.
        await MaybeProjectToWorkSource(failedRun, RunLifecycleState.NeedsHuman, ct);

        Log.RetryExhausted(
            _logger,
            failedRun.RunId,
            failedRun.WorkItemId,
            maxRunAttempts,
            failureReasons.Count
        );
    }

    /// <summary>
    /// Determine if a failed run's error is classified as retryable.
    /// Checks the Error field for known retryable failure reason strings.
    /// </summary>
    private static bool IsFailureRetryable(AgentRunHandle run)
    {
        var error = run.Error;
        if (string.IsNullOrWhiteSpace(error))
            return false;

        // Check if the error message contains a known retryable reason.
        foreach (var reason in RetryableFailureReasons.AllRetryableReasons)
        {
            if (error.Contains(reason, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // ── Source-generated LoggerMessage partials ─────────────────────

    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Runtime event drove state transition — eventType={EventType}, "
                + "runId={RunId}, eventId={EventId}, from={FromState}, to={ToState}, "
                + "elapsed={ElapsedMilliseconds:F1}ms"
        )]
        public static partial void RuntimeEventTransition(
            ILogger logger,
            string eventType,
            string runId,
            string eventId,
            RunLifecycleState fromState,
            RunLifecycleState toState,
            double elapsedMilliseconds
        );

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Runtime event no-op — eventType={EventType}, "
                + "runId={RunId}, eventId={EventId}, state={State}, "
                + "elapsed={ElapsedMilliseconds:F1}ms"
        )]
        public static partial void RuntimeEventNoOp(
            ILogger logger,
            string eventType,
            string runId,
            string eventId,
            RunLifecycleState state,
            double elapsedMilliseconds
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Runtime event dispatch failed — eventType={EventType}, "
                + "runId={RunId}, eventId={EventId}, elapsed={ElapsedMilliseconds:F1}ms"
        )]
        public static partial void RuntimeEventDispatchError(
            ILogger logger,
            Exception ex,
            string eventType,
            string runId,
            string eventId,
            double elapsedMilliseconds
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Retry run created — retryRunId={RetryRunId}, "
                + "previousRunId={PreviousRunId}, attempt={Attempt}/{MaxAttempts}"
        )]
        public static partial void RetryRunCreated(
            ILogger logger,
            string retryRunId,
            string previousRunId,
            int attempt,
            int maxAttempts
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Non-retryable failure — runId={RunId}, error={Error}"
        )]
        public static partial void NonRetryableFailure(ILogger logger, string runId, string error);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Retry exhausted — runId={RunId}, workItemId={WorkItemId}, "
                + "maxAttempts={MaxAttempts}, failureCount={FailureCount}"
        )]
        public static partial void RetryExhausted(
            ILogger logger,
            string runId,
            string? workItemId,
            int maxAttempts,
            int failureCount
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Work-source projection failed — runId={RunId}, workItemId={WorkItemId}, "
                + "externalId={ExternalId}, source={Source}, targetState={TargetState}"
        )]
        public static partial void WorkSourceProjectionFailed(
            ILogger logger,
            Exception ex,
            string runId,
            string workItemId,
            string? externalId,
            string? source,
            RunLifecycleState targetState
        );
    }
}
