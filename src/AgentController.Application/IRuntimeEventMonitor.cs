using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Port for reading the runtime event stream of an agent run for the
/// monitoring/local sync feed.
///
/// Implementations resolve the run's artifact directory and read the bounded
/// runtime event stream without blocking the caller: absent streams, partial
/// writes, and malformed lines are surfaced as empty/malformed snapshots rather
/// than thrown. Used by the monitoring API (<c>/api/monitor/events</c>) to
/// publish runtime events to the Materia UI local sync.
/// </summary>
public interface IRuntimeEventMonitor
{
    /// <summary>
    /// Read the bounded runtime event stream for <paramref name="runId"/>.
    ///
    /// Never throws for absent or malformed streams. <paramref name="cap"/>
    /// overrides the default newest-event cap when supplied; <c>null</c> applies
    /// the reader default.
    /// </summary>
    /// <param name="runId">Controller-assigned run identifier.</param>
    /// <param name="cap">
    /// Optional newest-event cap. <c>null</c> applies the reader default.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    Task<MonitoringRuntimeEventSnapshot> GetRuntimeEventsAsync(
        string runId,
        int? cap,
        CancellationToken cancellationToken);
}
