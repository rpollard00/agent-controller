using AgentController.Domain;

namespace AgentController.Api.Models;

/// <summary>
/// Monitoring/local sync payload for the <c>/api/monitor/events</c> channel.
///
/// Combines a lightweight run summary (the existing monitoring fields: run id,
/// lifecycle status, and key timestamps) with the additive
/// <see cref="RuntimeEvents"/> snapshot. The schema is additive and backward
/// compatible: older clients that only understand the summary fields ignore
/// <see cref="RuntimeEvents"/>, while newer clients render the formatted event
/// feed. The same shape is used for both the JSON snapshot and the
/// server-sent-event payload so clients reuse one type.
/// </summary>
public sealed class MonitorEventsResponse
{
    /// <summary>Controller-assigned run identifier this monitoring payload describes.</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>Current run lifecycle status name (e.g. "AgentRunning", "Completed").</summary>
    public string? Status { get; init; }

    /// <summary>When the run was started, when known.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the run finished, when known.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Last heartbeat time received from the runtime, when known.</summary>
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    /// <summary>When this monitoring payload was generated (snapshot time).</summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Additive runtime event stream snapshot. Always present (possibly empty) so
    /// clients can render empty/loading states uniformly. Older clients ignore it.
    /// </summary>
    public MonitoringRuntimeEventSnapshot RuntimeEvents { get; init; } = new();
}
