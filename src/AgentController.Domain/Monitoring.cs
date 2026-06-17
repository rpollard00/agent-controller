namespace AgentController.Domain;

/// <summary>
/// Classification of a runtime event entry as parsed for the monitoring/local sync feed.
/// Used by <see cref="MonitoringRuntimeEvent.Status"/> so the UI can render valid,
/// incomplete, and unparseable entries differently while never dropping data.
/// </summary>
public enum RuntimeEventParseStatus
{
    /// <summary>
    /// The entry parsed successfully and carries the required identifying field
    /// (a non-empty <see cref="MonitoringRuntimeEvent.EventType"/>). All parsed
    /// fields are populated where the source provided them.
    /// </summary>
    Valid = 0,

    /// <summary>
    /// The entry parsed as a JSON object but is missing required identifying
    /// fields (for example an empty <see cref="MonitoringRuntimeEvent.EventType"/>).
    /// Parsed fields and the raw line are preserved for debugging.
    /// </summary>
    MissingFields = 1,

    /// <summary>
    /// The entry could not be parsed (invalid JSON, wrong root shape, or a
    /// deserialization failure). Only the raw line and a parse error are available.
    /// </summary>
    Malformed = 2,
}

/// <summary>
/// A single runtime event projected for the monitoring/local sync feed.
///
/// Carries the parsed fields from a <see cref="RuntimeEvent"/> (timestamp, type,
/// severity, message, payload, source/run metadata) alongside raw/debug metadata
/// (the original raw line and any parse error) so the Materia UI can show a
/// readable progress feed while still letting developers inspect malformed entries.
///
/// One <see cref="MonitoringRuntimeEvent"/> can represent any of the three parse
/// outcomes described by <see cref="RuntimeEventParseStatus"/>. All parsed fields
/// are optional so that missing or malformed entries remain representable without
/// throwing.
/// </summary>
public sealed record MonitoringRuntimeEvent
{
    /// <summary>
    /// Zero-based position of this entry within its source stream. Provides stable
    /// ordering independent of timestamps, which may be missing on malformed entries.
    /// </summary>
    public int Index { get; init; }

    /// <summary>Outcome of parsing this entry. See <see cref="RuntimeEventParseStatus"/>.</summary>
    public RuntimeEventParseStatus Status { get; init; } = RuntimeEventParseStatus.Valid;

    // ── Parsed fields (null when absent or unparseable) ───────────────

    /// <summary>Stable unique identifier for this event (idempotency key), when available.</summary>
    public string? EventId { get; init; }

    /// <summary>Controller-assigned run identifier this event belongs to, when available.</summary>
    public string? RunId { get; init; }

    /// <summary>Runtime-assigned run identifier (e.g. pi-materia process id), when available.</summary>
    public string? RuntimeRunId { get; init; }

    /// <summary>Monotonically increasing sequence number for this run, when available.</summary>
    public int? Sequence { get; init; }

    /// <summary>When the event occurred (timestamp). Null when the source did not provide one.</summary>
    public DateTimeOffset? OccurredAt { get; init; }

    /// <summary>
    /// Event type (e.g. "runtime.accepted", "runtime.completed"). The primary
    /// classifying field; an empty value here is what marks an entry
    /// <see cref="RuntimeEventParseStatus.MissingFields"/>.
    /// </summary>
    public string? EventType { get; init; }

    /// <summary>Event severity, when available. Null when the source did not provide one.</summary>
    public EventSeverity? Severity { get; init; }

    /// <summary>Human-readable message, when available.</summary>
    public string? Message { get; init; }

    /// <summary>Type-specific payload, when available. Keys and values are provider-defined.</summary>
    public IReadOnlyDictionary<string, object?>? Payload { get; init; }

    // ── Raw / debug metadata ──────────────────────────────────────────

    /// <summary>
    /// The original raw line (or raw JSON) this entry was read from, when available.
    /// Always preserved for malformed and missing-field entries so developers can
    /// inspect the source in the UI.
    /// </summary>
    public string? RawLine { get; init; }

    /// <summary>
    /// Parse-error metadata for malformed entries (e.g. the deserializer error
    /// message). Null for <see cref="RuntimeEventParseStatus.Valid"/> and
    /// <see cref="RuntimeEventParseStatus.MissingFields"/> entries.
    /// </summary>
    public string? ParseError { get; init; }

    /// <summary>
    /// Project a fully-formed <see cref="RuntimeEvent"/> into a monitoring entry.
    /// The entry is <see cref="RuntimeEventParseStatus.Valid"/> when the source
    /// carries a non-empty <see cref="RuntimeEvent.EventType"/>, otherwise it is
    /// <see cref="RuntimeEventParseStatus.MissingFields"/>. Optionally preserves
    /// the <paramref name="rawLine"/> for UI debugging.
    /// </summary>
    public static MonitoringRuntimeEvent FromRuntimeEvent(
        RuntimeEvent source,
        int index,
        string? rawLine = null)
    {
        var hasType = !string.IsNullOrWhiteSpace(source.EventType);
        return new MonitoringRuntimeEvent
        {
            Index = index,
            Status = hasType ? RuntimeEventParseStatus.Valid : RuntimeEventParseStatus.MissingFields,
            EventId = NullIfEmpty(source.EventId),
            RunId = NullIfEmpty(source.RunId),
            RuntimeRunId = NullIfEmpty(source.RuntimeRunId),
            Sequence = source.Sequence,
            OccurredAt = source.OccurredAt,
            EventType = hasType ? source.EventType : null,
            Severity = source.Severity,
            Message = source.Message,
            Payload = source.Payload,
            RawLine = rawLine,
        };
    }

    /// <summary>
    /// Build a <see cref="RuntimeEventParseStatus.Malformed"/> entry from a raw
    /// line that could not be parsed. The raw line and parse error are preserved
    /// verbatim so the malformed entry stays visible in the feed.
    /// </summary>
    public static MonitoringRuntimeEvent FromMalformedLine(
        int index,
        string rawLine,
        string parseError)
    {
        return new MonitoringRuntimeEvent
        {
            Index = index,
            Status = RuntimeEventParseStatus.Malformed,
            RawLine = rawLine,
            ParseError = parseError,
        };
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// A snapshot of the runtime events for a run, shaped for delivery through
/// the monitoring/local sync channel (e.g. an <c>/api/monitor/events</c> snapshot
/// or server-sent-event payload).
///
/// Carries the ordered <see cref="MonitoringRuntimeEvent"/> list plus lightweight
/// summary/cap metadata so clients can render truncation and freshness without
/// re-deriving them. The schema is additive: new optional summary fields can be
/// appended without breaking older clients, and the events list may be empty when
/// no event stream exists for a run.
/// </summary>
public sealed record MonitoringRuntimeEventSnapshot
{
    /// <summary>Controller-assigned run identifier this stream belongs to, when known.</summary>
    public string? RunId { get; init; }

    /// <summary>
    /// Ordered runtime events. Producers document the ordering (oldest-first or
    /// newest-first); consumers should not assume an order without consulting the
    /// producing endpoint. Never null; empty when no event stream exists.
    /// </summary>
    public IReadOnlyList<MonitoringRuntimeEvent> Events { get; init; } = [];

    /// <summary>
    /// The newest-event cap applied by the producer, when one was applied.
    /// Null when no cap was applied. Lets the UI show "showing last N events".
    /// </summary>
    public int? Cap { get; init; }

    /// <summary>
    /// True when the source stream contained more events than were returned
    /// (i.e. <see cref="Cap"/> was applied and events were dropped).
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Total number of events available in the source before any cap was applied,
    /// when known. Equal to <see cref="Events"/>.Count when not truncated.
    /// </summary>
    public int? TotalAvailable { get; init; }

    /// <summary>When this snapshot was generated. Lets clients reason about freshness.</summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}
