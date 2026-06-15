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
public sealed class PollingWorker : BackgroundService
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
            _logger.LogInformation(
                "Polling worker is disabled (WorkerEnabled=false). No Azure DevOps, source control, "
                    + "environment, or pi-materia work will be performed."
            );

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

        _logger.LogInformation(
            "Polling worker started. WorkerId={WorkerId}, PollInterval={PollInterval}s, " +
            "MaxConcurrency={MaxConcurrency}, StaleTimeout={StaleTimeout}s",
            options.WorkerId,
            options.PollIntervalSeconds,
            options.MaxConcurrentRuns,
            options.StaleTimeoutSeconds
        );

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
                _logger.LogError(
                    ex,
                    "Unhandled exception in polling cycle. Worker will retry after delay."
                );
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

        _logger.LogInformation("Polling worker stopped.");
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
        var options = _options.CurrentValue;

        // ── 1. Check concurrency ─────────────────────────────────────
        var activeCount = await runStore.CountActiveAsync(ct);
        var availableSlots = options.MaxConcurrentRuns - activeCount;

        if (availableSlots <= 0)
        {
            _logger.LogDebug(
                "Concurrency limit reached (active={ActiveCount}, max={MaxConcurrency}). " +
                "Skipping work discovery.",
                activeCount,
                options.MaxConcurrentRuns);
        }
        else
        {
            // ── 2. Discover eligible work ────────────────────────────
            var query = new WorkQuery { MaxResults = availableSlots };
            var candidates = await workSource.FindEligibleAsync(query, ct);

            if (candidates.Count > 0)
            {
                _logger.LogInformation(
                    "Discovered {CandidateCount} eligible work candidate(s).",
                    candidates.Count);
            }

            foreach (var candidate in candidates)
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    await ProcessCandidateAsync(
                        candidate, workSource, lifecycle, options, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to process work candidate {CandidateId} ({Title}).",
                        candidate.Id,
                        candidate.Title);
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
        AgentControllerOptions options,
        CancellationToken ct)
    {
        // Claim the work item for exclusive execution
        var claim = new ClaimRequest
        {
            WorkerId = options.WorkerId,
            ClaimedAt = DateTimeOffset.UtcNow,
        };

        var claimResult = await workSource.TryClaimAsync(candidate, claim, ct);

        if (!claimResult.Success)
        {
            _logger.LogDebug(
                "Could not claim candidate {CandidateId}: {Reason}",
                candidate.Id,
                claimResult.FailureReason ?? "unknown");
            return;
        }

        _logger.LogInformation(
            "Claimed work candidate {CandidateId} ({Title}).",
            candidate.Id,
            candidate.Title);

        // Create the agent run (starts in Claimed state, records controller.claimed event)
        var run = await lifecycle.CreateRunForWorkItemAsync(
            candidate.Id,
            options.WorkerId,
            ct);

        _logger.LogInformation(
            "Created agent run {RunId} for work item {WorkItemId}.",
            run.RunId,
            candidate.Id);

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

        _logger.LogInformation(
            "Run {RunId} advanced to {State} for work item {WorkItemId}.",
            run.RunId,
            RunLifecycleState.AwaitingResult,
            candidate.Id);
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
                _logger.LogWarning(
                    "Recovered stale run {RunId} (last heartbeat: {LastHeartbeat}).",
                    staleRun.RunId,
                    staleRun.LastHeartbeatAt?.ToString("O") ?? "never");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to recover stale run {RunId}.",
                    staleRun.RunId);
            }
        }

        if (staleRuns.Count > 0)
        {
            _logger.LogInformation(
                "Recovered {StaleCount} stale run(s).",
                staleRuns.Count);
        }
    }
}
