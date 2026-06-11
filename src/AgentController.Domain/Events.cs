namespace AgentController.Domain;

/// <summary>
/// An event emitted by an agent runtime.
/// Uses a common envelope with a typed payload carried as a dictionary.
/// </summary>
public sealed record RuntimeEvent
{
    /// <summary>Stable unique identifier for this event (idempotency key).</summary>
    public string EventId { get; init; } = string.Empty;

    /// <summary>Controller-assigned run identifier.</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>Runtime-assigned run identifier, if available.</summary>
    public string? RuntimeRunId { get; init; }

    /// <summary>Monotonically increasing sequence number for this run, if available.</summary>
    public int? Sequence { get; init; }

    /// <summary>When the event occurred.</summary>
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Event type (e.g. "runtime.accepted", "runtime.completed").
    /// See <see cref="RuntimeEventTypes"/> for well-known values.
    /// </summary>
    public string EventType { get; init; } = string.Empty;

    /// <summary>Severity of the event.</summary>
    public EventSeverity Severity { get; init; } = EventSeverity.Info;

    /// <summary>Human-readable message.</summary>
    public string? Message { get; init; }

    /// <summary>
    /// Type-specific payload. Keys and values are provider-defined.
    /// For well-known event types, see the architecture doc for expected keys.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Payload { get; init; }
}

/// <summary>
/// A lifecycle event recorded by the controller in its authoritative event log.
/// May originate from a <see cref="RuntimeEvent"/> or from internal controller actions.
/// </summary>
public sealed record LifecycleEvent
{
    /// <summary>Controller-assigned unique identifier for this event.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Run identifier this event belongs to.</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>
    /// Event identifier from the runtime, if this event originated from a <see cref="RuntimeEvent"/>.
    /// Used for idempotency.
    /// </summary>
    public string? EventId { get; init; }

    /// <summary>
    /// Event type (e.g. "runtime.completed", "controller.claimed", "controller.failed").
    /// </summary>
    public string EventType { get; init; } = string.Empty;

    /// <summary>Severity of the event.</summary>
    public EventSeverity Severity { get; init; } = EventSeverity.Info;

    /// <summary>Human-readable message.</summary>
    public string? Message { get; init; }

    /// <summary>Optional structured payload.</summary>
    public IReadOnlyDictionary<string, object?>? Payload { get; init; }

    /// <summary>When the controller recorded this event.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
