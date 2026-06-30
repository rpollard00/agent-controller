using AgentController.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentController.Api;

/// <summary>
/// Background service that polls for PR review comments and drives the feedback
/// materialization pipeline into <see cref="Application.ReworkCycle"/> rows.
///
/// This is a separate <see cref="BackgroundService"/> from <see cref="PollingWorker"/>
/// with its own poll interval (<see cref="FeedbackOptions.PollIntervalSeconds"/>) and
/// its own concurrency limit (<see cref="FeedbackOptions.MaxConcurrentPolls"/>).
/// The two workers are fully independent: the discovery worker claims and executes
/// work items, while the feedback worker watches open PRs for reviewer comments.
///
/// When <see cref="FeedbackOptions.Enabled"/> is false the worker sleeps indefinitely
/// until shutdown, keeping the host alive without performing any work.
/// </summary>
public sealed partial class FeedbackPollingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<FeedbackOptions> _options;
    private readonly ILogger<FeedbackPollingWorker> _logger;

    public FeedbackPollingWorker(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<FeedbackOptions> options,
        ILogger<FeedbackPollingWorker> logger
    )
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;

        if (!options.Enabled)
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
            options.PollIntervalSeconds,
            options.MaxConcurrentPolls,
            options.SoakMinutes,
            options.Provider);

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
    /// Execute a single feedback poll cycle.
    ///
    /// Each cycle gets its own DI scope so scoped services (EF Core DbContext,
    /// stores, feedback source) share a single unit-of-work.
    ///
    /// Future work items will fill in the body: PR scan query, filter pipeline,
    /// soak-window logic, and ReworkCycle materialization.
    /// </summary>
    private async Task PollCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var options = _options.CurrentValue;

        Log.PollCycleStarted(_logger, options.PollIntervalSeconds);

        // ── Concurrency gate ─────────────────────────────────────
        // The feedback worker has its own concurrency budget independent of
        // the discovery worker's MaxConcurrentRuns. This prevents feedback
        // polling from competing with run execution for concurrency slots.
        //
        // TODO: implement PR scan query, filter pipeline, soak-window logic,
        // and ReworkCycle materialization in subsequent work items.

        Log.PollCycleCompleted(_logger);
    }

    /// <summary>
    /// Run a single poll cycle synchronously from test code.
    /// Exposed as internal for integration-style tests.
    /// </summary>
    internal async Task RunPollCycleForTestingAsync(CancellationToken ct = default)
    {
        await PollCycleAsync(ct);
    }

    /// <summary>
    /// Source-generated high-performance logger methods.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Feedback polling worker is disabled (feedback.enabled=false). "
                + "No PR review comments will be polled.")]
        public static partial void WorkerDisabled(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Feedback polling worker started. PollInterval={PollInterval}s, "
                + "MaxConcurrency={MaxConcurrency}, SoakMinutes={SoakMinutes}, Provider='{Provider}'")]
        public static partial void WorkerStarted(
            ILogger logger, int pollInterval, int maxConcurrency, int soakMinutes, string provider);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Unhandled exception in feedback polling cycle. Worker will retry after delay.")]
        public static partial void PollCycleError(ILogger logger, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Feedback polling worker stopped.")]
        public static partial void WorkerStopped(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Feedback poll cycle started (interval={Interval}s).")]
        public static partial void PollCycleStarted(ILogger logger, int interval);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Feedback poll cycle completed.")]
        public static partial void PollCycleCompleted(ILogger logger);
    }
}
