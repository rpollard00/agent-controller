using AgentController.Domain;

namespace AgentController.Api.Models;

/// <summary>
/// Request body for the POST /runs/{runId}/events endpoint.
/// Maps to a <see cref="RuntimeEvent"/>; the <c>runId</c> is taken from the route
/// but may also be set in the body. When both are provided, they must match.
/// </summary>
public sealed record RuntimeEventRequest
{
    /// <summary>
    /// Controller-assigned run identifier. Required per the runtime event contract.
    /// When provided, must match the <c>runId</c> route parameter.
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>Stable unique identifier for this event (idempotency key). Required.</summary>
    public string? EventId { get; init; }

    /// <summary>
    /// Event type (e.g. "runtime.accepted", "runtime.completed").
    /// See <see cref="RuntimeEventTypes"/> for well-known values. Required.
    /// </summary>
    public string? EventType { get; init; }

    /// <summary>Runtime-assigned run identifier, if available.</summary>
    public string? RuntimeRunId { get; init; }

    /// <summary>Monotonically increasing sequence number for this run, if available.</summary>
    public int? Sequence { get; init; }

    /// <summary>When the event occurred. Defaults to now.</summary>
    public DateTimeOffset? OccurredAt { get; init; }

    /// <summary>Severity of the event. Defaults to Info.</summary>
    public EventSeverity? Severity { get; init; }

    /// <summary>Human-readable message.</summary>
    public string? Message { get; init; }

    /// <summary>
    /// Type-specific payload. Keys and values are provider-defined.
    /// For well-known event types, see the architecture doc for expected keys.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Payload { get; init; }
}
