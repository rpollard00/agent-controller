using AgentController.Domain;

namespace AgentController.Application.Commands;

/// <summary>
/// Command to ingest a runtime event for an agent run.
/// Carries the route-level <c>RunId</c>, the optional body-level <c>RunId</c>,
/// and all other body fields from the runtime event request.
/// </summary>
public sealed record IngestRuntimeEventCommand(
    /// <summary>
    /// Run identifier from the route parameter (authoritative run identifier).
    /// </summary>
    string RouteRunId,

    /// <summary>
    /// Run identifier from the request body, if provided.
    /// Used for route-vs-body consistency validation in the handler.
    /// </summary>
    string? BodyRunId,

    /// <summary>Stable unique identifier for this event (idempotency key). Required.</summary>
    string? EventId,

    /// <summary>Event type (e.g. "runtime.accepted", "runtime.completed"). Required.</summary>
    string? EventType,

    /// <summary>Runtime-assigned run identifier, if available.</summary>
    string? RuntimeRunId,

    /// <summary>Monotonically increasing sequence number for this run, if available.</summary>
    int? Sequence,

    /// <summary>When the event occurred. Defaults to UtcNow.</summary>
    DateTimeOffset? OccurredAt,

    /// <summary>Severity of the event. Defaults to Info.</summary>
    EventSeverity? Severity,

    /// <summary>Human-readable message.</summary>
    string? Message,

    /// <summary>Type-specific payload.</summary>
    IReadOnlyDictionary<string, object?>? Payload
);
