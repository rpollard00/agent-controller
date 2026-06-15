using AgentController.Domain;

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
internal sealed class RunLifecycleService : IRunLifecycleService
{
    private readonly IAgentRunStore _runStore;
    private readonly ILifecycleEventStore _eventStore;
    private readonly IWorkItemStore _workItemStore;

    /// <summary>
    /// Legal state transitions.
    /// The key is the current state, the values are allowed target states.
    /// Any state not listed as a key can only transition to states defined by
    /// runtime events (handled separately in <see cref="IngestRuntimeEventAsync"/>).
    /// </summary>
    private static readonly Dictionary<RunLifecycleState, HashSet<RunLifecycleState>> AllowedTransitions = new()
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
        IAgentRunStore runStore,
        ILifecycleEventStore eventStore,
        IWorkItemStore workItemStore)
    {
        _runStore = runStore;
        _eventStore = eventStore;
        _workItemStore = workItemStore;
    }

    /// <inheritdoc />
    public async Task<AgentRunHandle> CreateRunForWorkItemAsync(
        string workItemId,
        string workerId,
        CancellationToken ct)
    {
        var workItem = await _workItemStore.GetByIdAsync(workItemId, ct);
        if (workItem is null)
        {
            throw new InvalidOperationException(
                $"Cannot create run: work item '{workItemId}' not found.");
        }

        var run = await _runStore.CreateAsync(
            new CreateRunRequest
            {
                WorkItemId = workItemId,
                WorkerId = workerId,
                InitialStatus = RunLifecycleState.Claimed,
            },
            ct);

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
            ct);

        return run;
    }

    /// <inheritdoc />
    public async Task TransitionAsync(
        string runId,
        RunLifecycleState targetState,
        CancellationToken ct)
    {
        var run = await _runStore.GetByIdAsync(runId, ct);
        if (run is null)
        {
            throw new InvalidOperationException(
                $"Cannot transition run '{runId}': run not found.");
        }

        if (IsTerminal(run.Status))
        {
            throw new InvalidOperationException(
                $"Cannot transition run '{runId}': run is in terminal state '{run.Status}'.");
        }

        if (!IsTransitionAllowed(run.Status, targetState))
        {
            throw new InvalidOperationException(
                $"Cannot transition run '{runId}' from '{run.Status}' to '{targetState}': " +
                "transition is not allowed. Use runtime event ingestion for " +
                "AwaitingResult → terminal transitions.");
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
            ct);

        // Update work item status for key controller-owned transitions
        await MaybeUpdateWorkItemStatus(run, targetState, ct);
    }

    /// <inheritdoc />
    public async Task AppendControllerEventAsync(
        string runId,
        string eventType,
        string message,
        IReadOnlyDictionary<string, object?>? payload,
        CancellationToken ct)
    {
        await AppendControllerEventAsync(
            runId, eventType, message, payload, EventSeverity.Info, ct);
    }

    /// <inheritdoc />
    public async Task AppendControllerEventAsync(
        string runId,
        string eventType,
        string message,
        IReadOnlyDictionary<string, object?>? payload,
        EventSeverity severity,
        CancellationToken ct)
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
            ct);
    }

    /// <inheritdoc />
    public async Task IngestRuntimeEventAsync(
        RuntimeEvent evt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.EventId))
        {
            throw new InvalidOperationException(
                "Runtime event is missing required field 'eventId'.");
        }

        if (string.IsNullOrWhiteSpace(evt.RunId))
        {
            throw new InvalidOperationException(
                "Runtime event is missing required field 'runId'.");
        }

        if (string.IsNullOrWhiteSpace(evt.EventType))
        {
            throw new InvalidOperationException(
                "Runtime event is missing required field 'eventType'.");
        }

        // Idempotency check
        var alreadyExists = await _eventStore.ExistsByEventIdAsync(evt.RunId, evt.EventId, ct);
        if (alreadyExists)
        {
            throw new InvalidOperationException(
                $"Runtime event '{evt.EventId}' has already been processed for run '{evt.RunId}'.");
        }

        var run = await _runStore.GetByIdAsync(evt.RunId, ct);
        if (run is null)
        {
            throw new InvalidOperationException(
                $"Cannot ingest runtime event for run '{evt.RunId}': run not found.");
        }

        if (IsTerminal(run.Status))
        {
            throw new InvalidOperationException(
                $"Cannot ingest runtime event for run '{evt.RunId}': " +
                $"run is in terminal state '{run.Status}'.");
        }

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
                    $"Unsupported runtime event type '{evt.EventType}'.");
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
            ct);

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
            ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentRunHandle>> FindStaleRunsAsync(
        TimeSpan staleTimeout,
        CancellationToken ct)
    {
        return await _runStore.FindStaleAsync(staleTimeout, ct);
    }

    /// <inheritdoc />
    public async Task RecoverStaleRunAsync(
        string runId,
        CancellationToken ct)
    {
        var run = await _runStore.GetByIdAsync(runId, ct);
        if (run is null)
        {
            throw new InvalidOperationException(
                $"Cannot recover stale run '{runId}': run not found.");
        }

        if (IsTerminal(run.Status))
        {
            throw new InvalidOperationException(
                $"Cannot recover stale run '{runId}': run is already in terminal state '{run.Status}'.");
        }

        if (run.Status != RunLifecycleState.AwaitingResult)
        {
            throw new InvalidOperationException(
                $"Cannot recover stale run '{runId}': run is in state '{run.Status}', " +
                "only runs in AwaitingResult can be recovered as stale.");
        }

        await _runStore.UpdateStatusAsync(runId, RunLifecycleState.NeedsHuman, ct);
        await MaybeUpdateWorkItemStatus(run, RunLifecycleState.NeedsHuman, ct);

        await AppendControllerEventAsync(
            runId,
            ControllerEventTypes.StaleRecovered,
            $"Stale run recovered: no heartbeat or final event received within the timeout. " +
            $"Transitioned from {RunLifecycleState.AwaitingResult} to {RunLifecycleState.NeedsHuman}.",
            new Dictionary<string, object?>
            {
                ["previousState"] = RunLifecycleState.AwaitingResult.ToString(),
                ["targetState"] = RunLifecycleState.NeedsHuman.ToString(),
                ["lastHeartbeatAt"] = run.LastHeartbeatAt?.ToString("O"),
                ["startedAt"] = run.StartedAt?.ToString("O"),
            },
            EventSeverity.Warning,
            ct);
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

        // Cancellation is always allowed from any non-terminal state
        if (target == RunLifecycleState.Cancelled && current != RunLifecycleState.CleanedUp)
            return true;

        if (AllowedTransitions.TryGetValue(current, out var allowed))
            return allowed.Contains(target);

        return false;
    }

    private async Task MaybeUpdateWorkItemStatus(
        AgentRunHandle run,
        RunLifecycleState targetState,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(run.WorkItemId))
            return;

        // Update work item status for key lifecycle milestones.
        // The actual status values are configuration-driven; for Phase 1 we
        // use descriptive internal status strings.
        var status = targetState switch
        {
            RunLifecycleState.EnvironmentProvisioning => "Provisioning",
            RunLifecycleState.AgentRunning => "Running",
            RunLifecycleState.Completed => "Completed",
            RunLifecycleState.Failed => "Failed",
            RunLifecycleState.NeedsHuman => "NeedsHuman",
            RunLifecycleState.Cancelled => "Cancelled",
            _ => null,
        };

        if (status is not null)
        {
            await _workItemStore.UpdateStatusAsync(run.WorkItemId, status, ct);
        }
    }

    private async Task HandleAcceptedAsync(
        AgentRunHandle run,
        RuntimeEvent evt,
        CancellationToken ct)
    {
        // Transition to AgentRunning if we're in AwaitingResult or earlier
        if (run.Status != RunLifecycleState.AgentRunning)
        {
            await _runStore.UpdateStatusAsync(run.RunId, RunLifecycleState.AgentRunning, ct);
        }

        // Update runtime fields
        var update = new RuntimeFieldUpdate
        {
            RuntimeRunId = evt.RuntimeRunId,
            LastHeartbeatAt = evt.OccurredAt,
            StartedAt = run.StartedAt ?? evt.OccurredAt,
        };
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, update, ct);
    }

    private async Task HandleHeartbeatAsync(
        AgentRunHandle run,
        RuntimeEvent evt,
        CancellationToken ct)
    {
        var update = new RuntimeFieldUpdate
        {
            LastHeartbeatAt = evt.OccurredAt,
        };
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, update, ct);
    }

    private async Task HandleStatusAsync(
        AgentRunHandle run,
        RuntimeEvent evt,
        CancellationToken ct)
    {
        // Status events are informational only — no state change.
        // Update runtime fields if the runtime provided its run id.
        if (!string.IsNullOrWhiteSpace(evt.RuntimeRunId))
        {
            var update = new RuntimeFieldUpdate
            {
                RuntimeRunId = evt.RuntimeRunId,
            };
            await _runStore.UpdateRuntimeFieldsAsync(run.RunId, update, ct);
        }
    }

    private async Task HandleCompletedAsync(
        AgentRunHandle run,
        RuntimeEvent evt,
        CancellationToken ct)
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
            PullRequestUrl = GetPayloadString(evt.Payload, "pullRequestUrl") ?? run.PullRequestUrl,
            ResultSummary = GetPayloadString(evt.Payload, "summary") ?? evt.Message ?? run.ResultSummary,
            LastHeartbeatAt = evt.OccurredAt,
            FinishedAt = evt.OccurredAt,
        };
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, update, ct);
    }

    private async Task HandleFailedAsync(
        AgentRunHandle run,
        RuntimeEvent evt,
        CancellationToken ct)
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

    private async Task HandleNeedsHumanAsync(
        AgentRunHandle run,
        RuntimeEvent evt,
        CancellationToken ct)
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
        CancellationToken ct)
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
        CancellationToken ct)
    {
        // branch_created and pr_created are informational — record fields
        // but do not change run status.
        var update = new RuntimeFieldUpdate
        {
            RuntimeRunId = evt.RuntimeRunId ?? run.RuntimeRunId,
            BranchName = GetPayloadString(evt.Payload, "branchName") ?? run.BranchName,
            PullRequestUrl = GetPayloadString(evt.Payload, "pullRequestUrl") ?? run.PullRequestUrl,
            LastHeartbeatAt = evt.OccurredAt,
        };
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, update, ct);
    }

    private static RunLifecycleState ResolveCompletionState(string? outcome)
    {
        return outcome switch
        {
            CompletionOutcomes.PullRequestOpened => RunLifecycleState.PrOpened,
            CompletionOutcomes.BranchPushed => RunLifecycleState.BranchPushed,
            CompletionOutcomes.NeedsHuman => RunLifecycleState.NeedsHuman,
            CompletionOutcomes.Failed => RunLifecycleState.Failed,
            // patch_created, no_changes_needed, and unknown → Completed
            _ => RunLifecycleState.Completed,
        };
    }

    private static string? GetPayloadString(
        IReadOnlyDictionary<string, object?>? payload,
        string key)
    {
        if (payload is null || !payload.TryGetValue(key, out var value) || value is null)
            return null;

        return value.ToString();
    }
}
