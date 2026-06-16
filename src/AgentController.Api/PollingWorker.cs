using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentController.Api;

/// <summary>
/// Background polling worker that discovers, claims, and orchestrates agent runs
/// through the controller-owned portion of the lifecycle.
///
/// Phase 1 behavior: discovers eligible local fake work items, claims them,
/// creates an AgentRun, records lifecycle events for each controller-owned step,
/// and transitions the run to <see cref="RunLifecycleState.AwaitingResult"/>.
/// No real source control, environment provisioning, Azure DevOps, or pi-materia
/// integration is invoked.
///
/// Also detects and recovers stale runs stuck in AwaitingResult past the
/// configured <see cref="AgentControllerOptions.StaleTimeoutSeconds"/>.
///
/// Seam: kept in the same host as the API for the prototype; a future split can
/// move this into a separate deployable without changing the domain or application contracts.
/// </summary>
public sealed partial class PollingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AgentControllerOptions> _options;
    private readonly ILogger<PollingWorker> _logger;

    /// <summary>
    /// Controller-owned lifecycle states the worker advances through in Phase 1,
    /// in order. Each transition records a lifecycle event.
    /// The run is created in <see cref="RunLifecycleState.Claimed"/> by
    /// <see cref="IRunLifecycleService.CreateRunForWorkItemAsync"/>, so we
    /// start from the next state.
    /// </summary>
    private static readonly RunLifecycleState[] ControllerProgression =
    [
        RunLifecycleState.EnvironmentProvisioning,
        RunLifecycleState.EnvironmentReady,
        RunLifecycleState.RepositoryCloning,
        RunLifecycleState.RepositoryReady,
        RunLifecycleState.ContextInjected,
        RunLifecycleState.AgentStarting,
        RunLifecycleState.AgentRunning,
        RunLifecycleState.AwaitingResult,
    ];

    public PollingWorker(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AgentControllerOptions> options,
        ILogger<PollingWorker> logger
    )
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;

        if (!options.WorkerEnabled)
        {
            Log.WorkerDisabled(_logger);

            // Sleep indefinitely until requested to stop, keeping the host alive.
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when the host shuts down.
            }

            return;
        }

        Log.WorkerStarted(
            _logger,
            options.WorkerId,
            options.PollIntervalSeconds,
            options.MaxConcurrentRuns,
            options.StaleTimeoutSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected shutdown.
                break;
            }
            catch (Exception ex)
            {
                Log.PollCycleError(_logger, ex);
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Log.WorkerStopped(_logger);
    }

    /// <summary>
    /// Execute a single poll cycle: check concurrency, discover and claim work,
    /// advance new runs through controller states, and recover stale runs.
    /// </summary>
    private async Task PollCycleAsync(CancellationToken ct)
    {
        // Each poll cycle gets its own DI scope so scoped services (EF Core DbContext,
        // stores, lifecycle service) share a single unit-of-work.
        await using var scope = _scopeFactory.CreateAsyncScope();

        var workSource = scope.ServiceProvider.GetRequiredService<IWorkSource>();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IRunLifecycleService>();
        var runStore = scope.ServiceProvider.GetRequiredService<IAgentRunStore>();
        var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();
        var options = _options.CurrentValue;

        // ── 1. Check concurrency ─────────────────────────────────────
        var activeCount = await runStore.CountActiveAsync(ct);
        var availableSlots = options.MaxConcurrentRuns - activeCount;

        if (availableSlots <= 0)
        {
            Log.ConcurrencyLimitReached(_logger, activeCount, options.MaxConcurrentRuns);
        }
        else
        {
            // ── 2. Discover eligible work ────────────────────────────
            var query = new WorkQuery { MaxResults = availableSlots };
            var candidates = await workSource.FindEligibleAsync(query, ct);

            if (candidates.Count > 0)
            {
                Log.CandidatesDiscovered(_logger, candidates.Count);
            }

            foreach (var candidate in candidates)
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    await ProcessCandidateAsync(
                        candidate, workSource, lifecycle, workItemStore, options, ct);
                }
                catch (Exception ex)
                {
                    Log.CandidateProcessingFailed(
                        _logger, ex, candidate.Id, candidate.Title);
                }
            }
        }

        // ── 3. Stale run recovery ────────────────────────────────────
        await RecoverStaleRunsAsync(lifecycle, options, ct);
    }

    /// <summary>
    /// Claim a work candidate, create an AgentRun, and advance it through
    /// all controller-owned lifecycle states up to <see cref="RunLifecycleState.AwaitingResult"/>.
    /// </summary>
    private async Task ProcessCandidateAsync(
        WorkCandidate candidate,
        IWorkSource workSource,
        IRunLifecycleService lifecycle,
        IWorkItemStore workItemStore,
        AgentControllerOptions options,
        CancellationToken ct)
    {
        // ── Upsert remote candidates into local persistence ────────
        // Azure DevOps Boards (and future remote sources) return
        // WorkCandidate objects that must exist in IWorkItemStore
        // before CreateRunForWorkItemAsync can find them by ID.
        if (candidate.Source != "LocalFake")
        {
            candidate = await workItemStore.UpsertAsync(candidate, ct);
            Log.CandidateUpserted(_logger, candidate.Id, candidate.Source);
        }

        // Claim the work item for exclusive execution
        var claim = new ClaimRequest
        {
            WorkerId = options.WorkerId,
            ClaimedAt = DateTimeOffset.UtcNow,
        };

        var claimResult = await workSource.TryClaimAsync(candidate, claim, ct);

        if (!claimResult.Success)
        {
            Log.ClaimFailed(
                _logger,
                candidate.Id,
                claimResult.FailureReason ?? "unknown");
            return;
        }

        Log.CandidateClaimed(_logger, candidate.Id, candidate.Title);

        // Create the agent run (starts in Claimed state, records controller.claimed event)
        var run = await lifecycle.CreateRunForWorkItemAsync(
            candidate.Id,
            options.WorkerId,
            ct);

        Log.RunCreated(_logger, run.RunId, candidate.Id);

        // Advance through all controller-owned lifecycle states.
        // Phase 1: skip real environment provisioning, repository cloning,
        // context injection, and agent runtime start. Record lifecycle
        // events for each step without performing the actual work.
        foreach (var targetState in ControllerProgression)
        {
            if (ct.IsCancellationRequested)
                break;

            await lifecycle.TransitionAsync(run.RunId, targetState, ct);

            // Append a phase-specific event in addition to the generic
            // state_transition recorded by TransitionAsync, so the event
            // log clearly shows each Phase 1 milestone.
            var (eventType, message) = targetState switch
            {
                RunLifecycleState.EnvironmentProvisioning => (
                    ControllerEventTypes.EnvironmentProvisioning,
                    "Environment provisioning (Phase 1 no-op — skipped)."),
                RunLifecycleState.EnvironmentReady => (
                    ControllerEventTypes.EnvironmentReady,
                    "Environment ready (Phase 1 no-op — skipped)."),
                RunLifecycleState.RepositoryCloning => (
                    ControllerEventTypes.RepositoryCloning,
                    "Repository cloning (Phase 1 no-op — skipped)."),
                RunLifecycleState.RepositoryReady => (
                    ControllerEventTypes.RepositoryReady,
                    "Repository ready (Phase 1 no-op — skipped)."),
                RunLifecycleState.ContextInjected => (
                    ControllerEventTypes.ContextInjected,
                    "Context injected (Phase 1 no-op — skipped)."),
                RunLifecycleState.AgentStarting => (
                    ControllerEventTypes.AgentStarting,
                    "Agent runtime start requested (Phase 1 no-op — skipped)."),
                RunLifecycleState.AgentRunning => (
                    ControllerEventTypes.AgentRunning,
                    "Agent runtime is executing (Phase 1 no-op — skipped)."),
                RunLifecycleState.AwaitingResult => (
                    ControllerEventTypes.AwaitingResult,
                    "Run handed off to runtime, awaiting result (Phase 1 — " +
                    "stopped at handoff; no real runtime invoked)."),
                _ => (
                    ControllerEventTypes.StateTransition,
                    $"Transitioned to {targetState}.")
            };

            await lifecycle.AppendControllerEventAsync(
                run.RunId,
                eventType,
                message,
                new Dictionary<string, object?>
                {
                    ["state"] = targetState.ToString(),
                    ["phase"] = "Phase1",
                },
                ct);
        }

        Log.RunAdvanced(_logger, run.RunId, candidate.Id);
    }

    /// <summary>
    /// Find and recover runs that are stuck in <see cref="RunLifecycleState.AwaitingResult"/>
    /// past the configured stale timeout.
    /// </summary>
    private async Task RecoverStaleRunsAsync(
        IRunLifecycleService lifecycle,
        AgentControllerOptions options,
        CancellationToken ct)
    {
        var staleTimeout = TimeSpan.FromSeconds(options.StaleTimeoutSeconds);
        var staleRuns = await lifecycle.FindStaleRunsAsync(staleTimeout, ct);

        foreach (var staleRun in staleRuns)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                await lifecycle.RecoverStaleRunAsync(staleRun.RunId, ct);
                Log.StaleRunRecovered(
                    _logger,
                    staleRun.RunId,
                    staleRun.LastHeartbeatAt);
            }
            catch (Exception ex)
            {
                Log.StaleRecoveryFailed(_logger, ex, staleRun.RunId);
            }
        }

        if (staleRuns.Count > 0)
        {
            Log.StaleRecoverySummary(_logger, staleRuns.Count);
        }
    }

    /// <summary>
    /// Source-generated high-performance logger methods.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Polling worker is disabled (WorkerEnabled=false). No Azure DevOps, source control, "
                + "environment, or pi-materia work will be performed.")]
        public static partial void WorkerDisabled(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Polling worker started. WorkerId={WorkerId}, PollInterval={PollInterval}s, "
                + "MaxConcurrency={MaxConcurrency}, StaleTimeout={StaleTimeout}s")]
        public static partial void WorkerStarted(
            ILogger logger, string workerId, int pollInterval, int maxConcurrency, int staleTimeout);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Unhandled exception in polling cycle. Worker will retry after delay.")]
        public static partial void PollCycleError(ILogger logger, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Polling worker stopped.")]
        public static partial void WorkerStopped(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Concurrency limit reached (active={ActiveCount}, max={MaxConcurrency}). "
                + "Skipping work discovery.")]
        public static partial void ConcurrencyLimitReached(
            ILogger logger, int activeCount, int maxConcurrency);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Discovered {CandidateCount} eligible work candidate(s).")]
        public static partial void CandidatesDiscovered(ILogger logger, int candidateCount);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Failed to process work candidate {CandidateId} ({Title}).")]
        public static partial void CandidateProcessingFailed(
            ILogger logger, Exception ex, string candidateId, string title);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Could not claim candidate {CandidateId}: {Reason}")]
        public static partial void ClaimFailed(ILogger logger, string candidateId, string reason);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Claimed work candidate {CandidateId} ({Title}).")]
        public static partial void CandidateClaimed(ILogger logger, string candidateId, string title);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Upserted work candidate {CandidateId} from source {Source} into local persistence.")]
        public static partial void CandidateUpserted(ILogger logger, string candidateId, string source);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Created agent run {RunId} for work item {WorkItemId}.")]
        public static partial void RunCreated(ILogger logger, string runId, string workItemId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Run {RunId} advanced to AwaitingResult for work item {WorkItemId}.")]
        public static partial void RunAdvanced(ILogger logger, string runId, string workItemId);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Recovered stale run {RunId} (last heartbeat: {LastHeartbeat}).")]
        public static partial void StaleRunRecovered(
            ILogger logger, string runId, DateTimeOffset? lastHeartbeat);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Failed to recover stale run {RunId}.")]
        public static partial void StaleRecoveryFailed(ILogger logger, Exception ex, string runId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Recovered {StaleCount} stale run(s).")]
        public static partial void StaleRecoverySummary(ILogger logger, int staleCount);
    }
}
