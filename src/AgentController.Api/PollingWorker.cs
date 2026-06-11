using AgentController.Application;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AgentController.Api;

/// <summary>
/// Background polling worker that discovers, claims, and orchestrates agent runs.
/// Disabled by default until real providers are wired. When disabled, the worker
/// performs no Azure DevOps, source control, environment, database, or pi-materia work.
///
/// Seam: kept in the same host as the API for the prototype; a future split can
/// move this into a separate deployable without changing the domain or application contracts.
/// </summary>
public sealed class PollingWorker : BackgroundService
{
    private readonly IWorkSource _workSource;
    private readonly ISourceControlProvider _sourceControlProvider;
    private readonly IEnvironmentProvider _environmentProvider;
    private readonly IAgentRuntime _agentRuntime;
    private readonly IOptionsMonitor<AgentControllerOptions> _options;
    private readonly ILogger<PollingWorker> _logger;

    public PollingWorker(
        IWorkSource workSource,
        ISourceControlProvider sourceControlProvider,
        IEnvironmentProvider environmentProvider,
        IAgentRuntime agentRuntime,
        IOptionsMonitor<AgentControllerOptions> options,
        ILogger<PollingWorker> logger
    )
    {
        _workSource = workSource;
        _sourceControlProvider = sourceControlProvider;
        _environmentProvider = environmentProvider;
        _agentRuntime = agentRuntime;
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
            "Polling worker started. WorkerId={WorkerId}, PollInterval={PollInterval}s, MaxConcurrency={MaxConcurrency}",
            options.WorkerId,
            options.PollIntervalSeconds,
            options.MaxConcurrentRuns
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Placeholder: future polling loop will:
                // 1. Query IWorkSource.FindEligibleAsync for candidate work items
                // 2. Claim eligible items via IWorkSource.TryClaimAsync
                // 3. Provision environments via IEnvironmentProvider.CreateAsync
                // 4. Clone repos via ISourceControlProvider.CloneAsync
                // 5. Start agent runs via IAgentRuntime.StartAsync
                // 6. Track lifecycle, ingest events, reconcile status
                // 7. Report results back via IWorkSource.UpdateStatusAsync / AddCommentAsync
                // For now, this block is intentionally empty — no external work is performed.

                await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
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
                    "Unhandled exception in polling loop. Worker will retry after delay."
                );
                await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
            }
        }

        _logger.LogInformation("Polling worker stopped.");
    }
}
