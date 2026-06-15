namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// EF Core entity for the LifecycleEvents table.
/// Maps to the prototype data model defined in the architecture (§7.5).
/// Payload is stored as a JSON string column.
/// </summary>
internal sealed class LifecycleEventEntity
{
    /// <summary>Controller-assigned unique identifier for this event (PK).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Run identifier this event belongs to.</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>
    /// External event identifier from the runtime, if this event originated from a runtime event.
    /// Used for idempotency checking. NULL for controller-internal events.
    /// </summary>
    public string? EventId { get; set; }

    /// <summary>Event type (e.g. "runtime.completed", "controller.claimed").</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Severity of the event (stored as int).</summary>
    public int Severity { get; set; }

    /// <summary>Human-readable message.</summary>
    public string? Message { get; set; }

    /// <summary>Optional structured payload serialized as JSON.</summary>
    public string? PayloadJson { get; set; }

    /// <summary>When the controller recorded this event.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the record was last mutated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
