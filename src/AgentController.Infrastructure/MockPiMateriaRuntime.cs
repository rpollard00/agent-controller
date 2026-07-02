using AgentController.Application;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentController.Infrastructure;

/// <summary>
/// A mock <see cref="IAgentRuntime"/> implementation that simulates a pi-materia
/// agent runtime by emitting a deterministic sequence of runtime events in-process.
///
/// When <see cref="StartAsync"/> is called, a background task fires that emits:
/// <list type="number">
///   <item><c>runtime.accepted</c> — runtime accepts the run</item>
///   <item><c>runtime.heartbeat</c> — runtime signals it is alive</item>
///   <item><c>runtime.status</c> — human-readable status update</item>
///   <item><c>runtime.completed</c> — runtime completes with configurable outcome</item>
/// </list>
///
/// Events are ingested through <see cref="IRunLifecycleService.IngestRuntimeEventAsync"/>
/// (in-process, not HTTP), so this works without the API being reachable.
///
/// The mock always completes with outcome <c>pull_request_opened</c>.
///
/// Registered as a singleton via
/// <see cref="AgentControllerServiceCollectionExtensions.AddAgentControllerMockPiMateriaRuntime"/>.
/// Uses <see cref="IServiceScopeFactory"/> internally to resolve the scoped
/// <see cref="IRunLifecycleService"/> per background emission.
///
/// This enables fully local end-to-end controller runs without requiring
/// Azure DevOps, a real pi installation, or manual HTTP calls to the mock
/// event endpoint.
/// </summary>
public sealed partial class MockPiMateriaRuntime : IAgentRuntime
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MockPiMateriaRuntime> _logger;

    /// <summary>
    /// Default delay between event emissions to simulate a realistic
    /// runtime execution timeline.
    /// </summary>
    private static readonly TimeSpan DefaultEventDelay = TimeSpan.FromMilliseconds(50);

    public MockPiMateriaRuntime(
        IServiceScopeFactory scopeFactory,
        ILogger<MockPiMateriaRuntime> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<AgentRunHandle> StartAsync(AgentRunSpec spec, CancellationToken cancellationToken)
    {
        var handle = new AgentRunHandle
        {
            RunId = spec.RunId,
            RuntimeRunId = $"mock-pi-{spec.RunId}",
            Status = RunLifecycleState.Queued,
            StartedAt = DateTimeOffset.UtcNow,
        };

        Log.RuntimeStarting(_logger, spec.RunId, handle.RuntimeRunId);

        // Fire-and-forget background emission. The task captures its own scope
        // and cancellation token for the lifetime of the emission sequence.
        _ = SimulateExecutionAsync(spec.RunId, handle.RuntimeRunId, CancellationToken.None);

        return Task.FromResult(handle);
    }

    /// <inheritdoc />
    public Task<AgentRuntimeStatus> GetStatusAsync(
        AgentRunHandle handle,
        CancellationToken cancellationToken)
    {
        var status = new AgentRuntimeStatus
        {
            Status = handle.Status,
            RuntimeRunId = handle.RuntimeRunId,
            StartedAt = handle.StartedAt,
            LastHeartbeatAt = handle.LastHeartbeatAt,
            Events = null,
            Error = handle.Error,
        };

        return Task.FromResult(status);
    }

    /// <inheritdoc />
    public Task CancelAsync(AgentRunHandle handle, CancellationToken cancellationToken)
    {
        Log.RuntimeCancelled(_logger, handle.RunId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Simulate the runtime execution lifecycle by emitting a sequence of
    /// runtime events through <see cref="IRunLifecycleService"/>.
    ///
    /// Uses <see cref="IServiceScopeFactory"/> to create a fresh scope per emission
    /// batch, ensuring the lifecycle service and its backing <see cref="DbContext"/>
    /// are properly scoped.
    /// </summary>
    private async Task SimulateExecutionAsync(
        string runId,
        string runtimeRunId,
        CancellationToken ct)
    {
        try
        {
            // ── 1. Emit runtime.accepted ──────────────────────────
            await EmitEventAsync(runId, runtimeRunId, new RuntimeEvent
            {
                EventId = $"mock-accepted-{runId}",
                RunId = runId,
                RuntimeRunId = runtimeRunId,
                OccurredAt = DateTimeOffset.UtcNow,
                EventType = RuntimeEventTypes.Accepted,
                Severity = EventSeverity.Info,
                Message = "Mock runtime accepted the run.",
                Payload = new Dictionary<string, object?>
                {
                    ["runtime"] = "MockPiMateria",
                },
            }, ct);

            await DelaySafe(DefaultEventDelay, ct);

            // ── 2. Emit runtime.heartbeat ─────────────────────────
            await EmitEventAsync(runId, runtimeRunId, new RuntimeEvent
            {
                EventId = $"mock-heartbeat-{runId}",
                RunId = runId,
                RuntimeRunId = runtimeRunId,
                OccurredAt = DateTimeOffset.UtcNow,
                EventType = RuntimeEventTypes.Heartbeat,
                Severity = EventSeverity.Info,
                Payload = new Dictionary<string, object?>
                {
                    ["phase"] = "implementation",
                },
            }, ct);

            await DelaySafe(DefaultEventDelay, ct);

            // ── 3. Emit runtime.status ────────────────────────────
            await EmitEventAsync(runId, runtimeRunId, new RuntimeEvent
            {
                EventId = $"mock-status-{runId}",
                RunId = runId,
                RuntimeRunId = runtimeRunId,
                OccurredAt = DateTimeOffset.UtcNow,
                EventType = RuntimeEventTypes.Status,
                Severity = EventSeverity.Info,
                Message = "Mock runtime is working on the task.",
                Payload = new Dictionary<string, object?>
                {
                    ["phase"] = "validation",
                },
            }, ct);

            await DelaySafe(DefaultEventDelay, ct);

            // ── 3.5 Emit runtime.pr_created ───────────────────────
            await EmitEventAsync(runId, runtimeRunId, new RuntimeEvent
            {
                EventId = $"mock-pr-created-{runId}",
                RunId = runId,
                RuntimeRunId = runtimeRunId,
                OccurredAt = DateTimeOffset.UtcNow,
                EventType = RuntimeEventTypes.PrCreated,
                Severity = EventSeverity.Info,
                Message = "Mock runtime created a pull request.",
                Payload = new Dictionary<string, object?>
                {
                    ["prUrl"] = $"https://dev.azure.com/mock/project/_git/repo/pullrequest/mock-{runId}",
                    ["prNumber"] = "42",
                    ["branchName"] = $"agent/mock-{runId}",
                },
            }, ct);

            await DelaySafe(DefaultEventDelay, ct);

            // ── 4. Emit runtime.completed ─────────────────────────
            var (outcome, message, payload) = ResolveCompletionOutcome(runId);

            await EmitEventAsync(runId, runtimeRunId, new RuntimeEvent
            {
                EventId = $"mock-completed-{runId}",
                RunId = runId,
                RuntimeRunId = runtimeRunId,
                OccurredAt = DateTimeOffset.UtcNow,
                EventType = RuntimeEventTypes.Completed,
                Severity = EventSeverity.Info,
                Message = message,
                Payload = payload,
            }, ct);

            Log.RuntimeCompleted(_logger, runId, outcome);
        }
        catch (OperationCanceledException)
        {
            Log.RuntimeEmissionCancelled(_logger, runId);
        }
        catch (Exception ex)
        {
            Log.RuntimeEmissionFailed(_logger, runId, ex);

            // Best-effort: emit a runtime.failed event so the controller
            // doesn't leave the run stuck in AwaitingResult forever.
            try
            {
                await EmitEventAsync(runId, runtimeRunId, new RuntimeEvent
                {
                    EventId = $"mock-failed-{runId}",
                    RunId = runId,
                    RuntimeRunId = runtimeRunId,
                    OccurredAt = DateTimeOffset.UtcNow,
                    EventType = RuntimeEventTypes.Failed,
                    Severity = EventSeverity.Error,
                    Message = "Mock runtime emission failed.",
                    Payload = new Dictionary<string, object?>
                    {
                        ["error"] = ex.Message,
                    },
                }, CancellationToken.None);
            }
            catch
            {
                // Nothing more we can do.
            }
        }
    }

    /// <summary>
    /// Emit a single runtime event through <see cref="IRunLifecycleService"/>.
    /// Creates a fresh DI scope for each emission to ensure the scoped services
    /// (DbContext, stores, lifecycle service) are properly unit-of-work isolated.
    /// </summary>
    private async Task EmitEventAsync(
        string runId,
        string runtimeRunId,
        RuntimeEvent evt,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IRunLifecycleService>();

        try
        {
            await lifecycle.IngestRuntimeEventAsync(evt, ct);
        }
        catch (InvalidOperationException ex)
        {
            // The run may have already been transitioned by another path
            // (e.g., duplicate event ID, terminal state). Log and continue.
            Log.EventIngestRejected(_logger, runId, evt.EventId, ex.Message);
        }
    }

    /// <summary>
    /// Resolve the completion outcome and related payload.
    /// The mock always completes with a successful pull request outcome.
    /// </summary>
    private static (string Outcome, string Message, Dictionary<string, object?> Payload)
        ResolveCompletionOutcome(string runId)
    {
        return (
            CompletionOutcomes.PullRequestOpened,
            "Mock runtime completed and opened a pull request.",
            new Dictionary<string, object?>
            {
                ["outcome"] = CompletionOutcomes.PullRequestOpened,
                ["summary"] = "Implemented change in mock runtime.",
                ["branchName"] = $"agent/mock-{runId}",
                ["prUrl"] = $"https://dev.azure.com/mock/project/_git/repo/pullrequest/mock-{runId}",
            }
        );
    }

    /// <summary>
    /// Delay with cancellation support, swallowing cancellation.
    /// </summary>
    private static async Task DelaySafe(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // Swallowed — the caller will check ct and exit.
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Mock runtime starting for run '{RunId}' with runtime ID '{RuntimeRunId}'.")]
        public static partial void RuntimeStarting(
            ILogger logger, string runId, string runtimeRunId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Mock runtime completed run '{RunId}' with outcome '{Outcome}'.")]
        public static partial void RuntimeCompleted(
            ILogger logger, string runId, string outcome);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Mock runtime cancelled for run '{RunId}'.")]
        public static partial void RuntimeCancelled(
            ILogger logger, string runId);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Mock runtime event emission cancelled for run '{RunId}'.")]
        public static partial void RuntimeEmissionCancelled(
            ILogger logger, string runId);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Mock runtime event emission failed for run '{RunId}'.")]
        public static partial void RuntimeEmissionFailed(
            ILogger logger, string runId, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Mock runtime event '{EventId}' for run '{RunId}' was rejected: {Reason}")]
        public static partial void EventIngestRejected(
            ILogger logger, string runId, string eventId, string reason);
    }
}
