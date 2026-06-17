/**
 * Client-side types for the AgentController monitoring/local sync channel
 * (`GET /api/monitor/events`).
 *
 * These mirror the server payload produced by the monitoring endpoint
 * (src/AgentController.Api/Models/MonitorEventsResponse.cs and
 * src/AgentController.Domain/Monitoring.cs) so the Materia UI can consume the
 * runtime event feed with full typing. The same shape is used for both the JSON
 * snapshot and the server-sent-event `data:` payload, so the UI reuses one type.
 *
 * The server schema is additive/backward compatible: every parsed field is
 * optional because missing-field and malformed entries carry no parsed value,
 * and older clients ignore the `runtimeEvents` snapshot entirely.
 */

/**
 * Runtime event severity, as serialized by the monitoring endpoint. The server
 * emits PascalCase enum names (`Info`, `Warning`, `Error`, `Critical`); helpers
 * in `formatters.ts` also tolerate case-insensitive / alias input.
 */
export type EventSeverity = 'Info' | 'Warning' | 'Error' | 'Critical';

/**
 * Parse status for a monitoring runtime event entry, as serialized by the
 * monitoring endpoint (PascalCase).
 *
 * - `Valid`         - parsed successfully and carries an identifying event type.
 * - `MissingFields` - parsed as JSON but missing required identifying fields.
 * - `Malformed`     - could not be parsed; only the raw line + parse error exist.
 */
export type RuntimeEventParseStatus = 'Valid' | 'MissingFields' | 'Malformed';

/** Type-specific payload: a provider-defined key/value map of JSON values. */
export type RuntimeEventPayload = Record<string, unknown> | null;

/** A single runtime event projected for the monitoring/local sync feed. */
export interface MonitoringRuntimeEvent {
  /** Zero-based position of this entry within its source stream (stable order). */
  index: number;
  /** Outcome of parsing this entry. See {@link RuntimeEventParseStatus}. */
  status: RuntimeEventParseStatus;
  /** Stable unique identifier for this event (idempotency key), when available. */
  eventId?: string | null;
  /** Controller-assigned run identifier this event belongs to, when available. */
  runId?: string | null;
  /** Runtime-assigned run identifier (e.g. pi-materia process id), when available. */
  runtimeRunId?: string | null;
  /** Monotonically increasing sequence number for this run, when available. */
  sequence?: number | null;
  /** When the event occurred (ISO 8601). Null when the source omitted it. */
  occurredAt?: string | null;
  /** Event type (e.g. "runtime.accepted"). The primary classifying field. */
  eventType?: string | null;
  /** Event severity, when available. */
  severity?: EventSeverity | null;
  /** Human-readable message, when available. */
  message?: string | null;
  /** Type-specific payload, when available. */
  payload?: RuntimeEventPayload;
  /** Original raw line/JSON this entry was read from (debug metadata). */
  rawLine?: string | null;
  /** Parse-error metadata for malformed entries. */
  parseError?: string | null;
}

/** Snapshot of the runtime events for a run, shaped for the monitoring channel. */
export interface MonitoringRuntimeEventSnapshot {
  /** Controller-assigned run identifier this stream belongs to, when known. */
  runId?: string | null;
  /** Ordered runtime events. Producers document ordering; never null. */
  events: MonitoringRuntimeEvent[];
  /** Newest-event cap applied by the producer, when one was applied. */
  cap?: number | null;
  /** True when the source had more events than returned (cap dropped some). */
  truncated: boolean;
  /** Total events available in the source before any cap, when known. */
  totalAvailable?: number | null;
  /** When this snapshot was generated (ISO 8601). */
  generatedAt?: string | null;
}

/**
 * Monitoring/local sync payload for `/api/monitor/events`. Combines the run
 * summary with the additive `runtimeEvents` snapshot.
 */
export interface MonitorEventsResponse {
  /** Controller-assigned run identifier this payload describes. */
  runId: string;
  /** Current run lifecycle status name (e.g. "AgentRunning", "Completed"). */
  status?: string | null;
  /** When the run was started, when known. */
  startedAt?: string | null;
  /** When the run finished, when known. */
  finishedAt?: string | null;
  /** Last heartbeat time received from the runtime, when known. */
  lastHeartbeatAt?: string | null;
  /** When this monitoring payload was generated (snapshot time). */
  generatedAt?: string | null;
  /** Additive runtime event stream snapshot. Always present (possibly empty). */
  runtimeEvents: MonitoringRuntimeEventSnapshot;
}
