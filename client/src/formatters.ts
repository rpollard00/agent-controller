/**
 * Pure formatting/normalization helpers for the monitoring/local sync runtime
 * event feed. Used by the Materia UI monitoring tab to render a readable event
 * progress feed.
 *
 * Everything here is pure and deterministic (timestamps take an optional `now`
 * so callers/tests control "now") so it is safe to unit-test and to reuse across
 * the snapshot and SSE rendering paths. These helpers never throw on unknown or
 * malformed input; they fall back to stable labels.
 */

import type {
  EventSeverity,
  MonitoringRuntimeEvent,
  MonitoringRuntimeEventSnapshot,
  MonitorEventsResponse,
  RuntimeEventParseStatus,
  RuntimeEventPayload,
} from './types.js';

// ─────────────────────────────────────────────────────────────────────────────
// Timestamps
// ─────────────────────────────────────────────────────────────────────────────

/** Parse an ISO 8601 timestamp into a Date, or null when missing/invalid. */
export function parseTimestamp(value: string | null | undefined): Date | null {
  if (value == null) return null;
  const text = typeof value === 'string' ? value.trim() : '';
  if (text === '') return null;
  const date = new Date(text);
  return Number.isNaN(date.getTime()) ? null : date;
}

/**
 * Format a timestamp as a relative phrase (e.g. "5m ago", "just now").
 *
 * Returns null when the value is missing/invalid, or older than a week (callers
 * should fall back to an absolute timestamp in that case). The server allows up
 * to 5 minutes of future skew on event timestamps, so small future values are
 * reported as "just now".
 *
 * @param value ISO 8601 timestamp string.
 * @param now   Epoch ms representing "now" (defaults to Date.now()).
 */
export function formatRelativeTime(
  value: string | null | undefined,
  now: number = Date.now(),
): string | null {
  const date = parseTimestamp(value);
  if (!date) return null;

  // Clamp small future skew to zero so we never render "-3m ago".
  const pastMs = Math.max(0, now - date.getTime());
  const seconds = Math.floor(pastMs / 1000);
  if (seconds < 60) return 'just now';

  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;

  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;

  const days = Math.floor(hours / 24);
  if (days < 7) return `${days}d ago`;

  return null;
}

/** Rich, UI-ready breakdown of a timestamp for the monitoring feed. */
export interface TimestampDisplay {
  /** Original ISO 8601 string, or null when missing/invalid. */
  iso: string | null;
  /** Parsed Date, or null when missing/invalid. */
  date: Date | null;
  /** Human absolute timestamp (locale aware). Never empty; "—" when invalid. */
  absolute: string;
  /** Human relative phrase, or null when not meaningful (see formatRelativeTime). */
  relative: string | null;
}

/**
 * Format a timestamp for display, returning both an absolute and a relative
 * representation so the UI can show e.g. a relative label with an absolute
 * tooltip. Always returns a value (with a "—" fallback) so it is safe to render
 * directly.
 */
export function formatTimestamp(
  value: string | null | undefined,
  opts: { now?: number; locale?: string; timeZone?: string } = {},
): TimestampDisplay {
  const date = parseTimestamp(value);
  if (!date) {
  return { iso: null, date: null, absolute: '—', relative: null };
  }

  const dateTimeOptions = opts.timeZone ? { timeZone: opts.timeZone } : undefined;
  const absolute = date.toLocaleString(opts.locale, dateTimeOptions);
  return {
    iso: date.toISOString(),
    date,
    absolute,
    relative: formatRelativeTime(value, opts.now ?? Date.now()),
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// Severity styling
// ─────────────────────────────────────────────────────────────────────────────

/** Canonical severity keys (lowercase), including an `unknown` fallback. */
export type SeverityKey = 'info' | 'warning' | 'error' | 'critical' | 'unknown';

/** Stable styling/token info for a severity, for badges/labels/colors. */
export interface SeverityInfo {
  /** Canonical lowercase key. */
  key: SeverityKey;
  /** Human label (e.g. "Critical"). */
  label: string;
  /** Sort rank (higher = more severe). Unknown ranks with info (0). */
  rank: number;
  /** Kebab-case CSS/theme token the UI maps to a color (e.g. "severity-critical"). */
  className: string;
}

const SEVERITY_BY_KEY: Record<SeverityKey, SeverityInfo> = {
  info: { key: 'info', label: 'Info', rank: 0, className: 'severity-info' },
  warning: { key: 'warning', label: 'Warning', rank: 1, className: 'severity-warning' },
  error: { key: 'error', label: 'Error', rank: 2, className: 'severity-error' },
  critical: { key: 'critical', label: 'Critical', rank: 3, className: 'severity-critical' },
  unknown: { key: 'unknown', label: 'Unknown', rank: 0, className: 'severity-unknown' },
};

/**
 * Resolve a server severity value into stable styling info. Tolerates the
 * PascalCase wire form, lowercase/aliases, and missing/unknown values, always
 * returning a usable {@link SeverityInfo}.
 */
export function getSeverityInfo(severity?: string | null): SeverityInfo {
  return SEVERITY_BY_KEY[normalizeSeverityKey(severity)];
}

/** Normalize a severity value (wire PascalCase, lowercase, alias, or missing). */
export function normalizeSeverityKey(severity?: string | null): SeverityKey {
  if (!severity) return 'unknown';
  switch (String(severity).trim().toLowerCase()) {
    case 'info':
      return 'info';
    case 'warning':
    case 'warn':
      return 'warning';
    case 'error':
    case 'err':
      return 'error';
    case 'critical':
    case 'fatal':
      return 'critical';
    default:
      return 'unknown';
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Parse status
// ─────────────────────────────────────────────────────────────────────────────

/** Canonical parse-status keys (kebab-case), including an `unknown` fallback. */
export type ParseStatusKey = 'valid' | 'missing-fields' | 'malformed' | 'unknown';

/** Stable styling/token info for a parse status. */
export interface ParseStatusInfo {
  key: ParseStatusKey;
  label: string;
  className: string;
}

const PARSE_STATUS_BY_KEY: Record<ParseStatusKey, ParseStatusInfo> = {
  'valid': { key: 'valid', label: 'Valid', className: 'parse-status-valid' },
  'missing-fields': {
    key: 'missing-fields',
    label: 'Missing fields',
    className: 'parse-status-missing-fields',
  },
  'malformed': {
    key: 'malformed',
    label: 'Malformed',
    className: 'parse-status-malformed',
  },
  'unknown': { key: 'unknown', label: 'Unknown', className: 'parse-status-unknown' },
};

/** Resolve a server parse-status value into stable info; tolerates unknowns. */
export function getParseStatusInfo(status?: string | null): ParseStatusInfo {
  return PARSE_STATUS_BY_KEY[normalizeParseStatusKey(status)];
}

/** Normalize a parse-status value (wire PascalCase or missing) to a canonical key. */
export function normalizeParseStatusKey(status?: string | null): ParseStatusKey {
  switch (typeof status === 'string' ? status.trim() : '') {
    case 'Valid':
      return 'valid';
    case 'MissingFields':
      return 'missing-fields';
    case 'Malformed':
      return 'malformed';
    default:
      return 'unknown';
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Event type / title / message
// ─────────────────────────────────────────────────────────────────────────────

const EVENT_TYPE_LABELS: Readonly<Record<string, string>> = {
  'runtime.accepted': 'Accepted',
  'runtime.heartbeat': 'Heartbeat',
  'runtime.status': 'Status',
  'runtime.branch_created': 'Branch created',
  'runtime.pr_created': 'PR created',
  'runtime.needs_human': 'Needs human',
  'runtime.completed': 'Completed',
  'runtime.failed': 'Failed',
  'runtime.cancelled': 'Cancelled',
};

/** Fallback title when no recognizable event type is available. */
export const UNKNOWN_EVENT_TITLE = 'Event';

/**
 * Map an event type to a human label. Known `runtime.*` types map to friendly
 * labels; unknown types are humanized from their trailing segment (e.g.
 * "runtime.deploy_started" -> "Deploy started"); missing types fall back to
 * {@link UNKNOWN_EVENT_TITLE}.
 */
export function getEventTypeLabel(eventType?: string | null): string {
  if (!eventType) return UNKNOWN_EVENT_TITLE;
  const known = EVENT_TYPE_LABELS[eventType];
  if (known) return known;

  const segments = eventType.split('.');
  const segment = (segments[segments.length - 1] ?? eventType).trim();
  if (segment === '') return UNKNOWN_EVENT_TITLE;
  return humanize(segment);
}

/**
 * Convert a snake/kebab segment into a sentence-case phrase, matching the
 * style of the curated known labels (e.g. "Branch created").
 */
function humanize(segment: string): string {
  const phrase = segment.replace(/[_-]+/g, ' ').trim().toLowerCase();
  if (phrase === '') return UNKNOWN_EVENT_TITLE;
  return phrase.charAt(0).toUpperCase() + phrase.slice(1);
}

/**
 * Format a stable, human-readable title for a monitoring event. Malformed and
 * missing-field entries get dedicated fallback titles so they stay visible and
 * distinct in the feed.
 */
export function formatEventTitle(event: MonitoringRuntimeEvent): string {
  switch (normalizeParseStatusKey(event.status)) {
    case 'malformed':
      return 'Malformed entry';
    case 'missing-fields':
      return 'Unparsed event';
    default:
      return getEventTypeLabel(event.eventType);
  }
}

/** Default messages for common event types when the event carries no message. */
const EVENT_TYPE_DEFAULT_MESSAGES: Readonly<Record<string, string>> = {
  'runtime.accepted': 'Run accepted by runtime.',
  'runtime.heartbeat': 'Heartbeat received.',
  'runtime.branch_created': 'Branch created.',
  'runtime.pr_created': 'Pull request opened.',
  'runtime.needs_human': 'Needs human input.',
  'runtime.failed': 'Runtime failed.',
  'runtime.cancelled': 'Run cancelled.',
};

const COMPLETION_OUTCOME_LABELS: Readonly<Record<string, string>> = {
  'pull_request_opened': 'Pull request opened',
  'branch_pushed': 'Branch pushed',
  'patch_created': 'Patch created',
  'no_changes_needed': 'No changes needed',
  'needs_human': 'Needs human input',
  'failed': 'Run failed',
};

/**
 * Map a `runtime.completed` payload `outcome` to a friendly label. Returns an
 * empty string for missing/unknown outcomes so callers can fall back.
 */
export function formatCompletionOutcome(outcome?: string | null): string {
  if (!outcome) return '';
  return COMPLETION_OUTCOME_LABELS[outcome] ?? '';
}

function readOutcome(payload: RuntimeEventPayload | undefined): string | null {
  if (!payload || typeof payload !== 'object') return null;
  const outcome = payload['outcome'];
  return typeof outcome === 'string' && outcome.trim() !== '' ? outcome : null;
}

/**
 * Format a human-readable message for a monitoring event.
 *
 * Uses the parsed message when present; otherwise derives a sensible default
 * from the event type / completion outcome so the feed is never confusingly
 * empty. Malformed entries surface their parse error; missing-field entries get
 * an explanatory fallback. Returns an empty string only when nothing applies.
 */
export function formatEventMessage(event: MonitoringRuntimeEvent): string {
  switch (normalizeParseStatusKey(event.status)) {
    case 'malformed': {
      const err = event.parseError?.trim();
      return err !== '' && err != null ? err : 'Could not parse this event.';
    }
    case 'missing-fields':
      return 'Event is missing required identifying fields.';
  }

  const message = event.message?.trim();
  if (message) return message;

  if (event.eventType === 'runtime.completed') {
    const outcome = formatCompletionOutcome(readOutcome(event.payload));
    if (outcome) return outcome;
  }

  return EVENT_TYPE_DEFAULT_MESSAGES[event.eventType ?? ''] ?? '';
}

// ─────────────────────────────────────────────────────────────────────────────
// Payload summarization
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Summarize a runtime event payload into a short, single-line, debug-friendly
 * string (e.g. `phase=validation, testsPassed=42, config={2 keys}`). Returns an
 * empty string for missing/empty payloads. Long values and the overall result
 * are truncated with an ellipsis.
 *
 * @param payload The event payload to summarize.
 * @param maxLen  Maximum total length (default 120).
 */
export function summarizePayload(
  payload: RuntimeEventPayload | undefined,
  maxLen = 120,
): string {
  if (!payload || typeof payload !== 'object') return '';
  const entries = Object.entries(payload);
  if (entries.length === 0) return '';

  const joined = entries
    .map(([key, value]) => `${key}=${formatPayloadValue(value)}`)
    .join(', ');

  return truncate(joined, maxLen);
}

/** Format a single payload value for summarization. */
function formatPayloadValue(value: unknown): string {
  if (value === null) return 'null';
  if (typeof value === 'string') return truncate(value, 40);
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  if (Array.isArray(value)) {
    const count = value.length;
    return `[${count} item${count === 1 ? '' : 's'}]`;
  }
  if (typeof value === 'object') {
    const count = Object.keys(value as Record<string, unknown>).length;
    return `{${count} key${count === 1 ? '' : 's'}}`;
  }
  return String(value);
}

/** Truncate a string to `max` chars, appending an ellipsis when truncated. */
function truncate(value: string, max: number): string {
  if (value.length <= max) return value;
  const limit = Math.max(0, max - 1);
  return `${value.slice(0, limit).trimEnd()}…`;
}

// ─────────────────────────────────────────────────────────────────────────────
// Ordering
// ─────────────────────────────────────────────────────────────────────────────

/** Event ordering for the monitoring feed. */
export type EventOrder = 'oldest-first' | 'newest-first';

/**
 * Return a new array of events ordered for display. The server emits events
 * oldest-first within the kept (capped) window; the UI typically prefers
 * newest-first. Ordering is by `occurredAt` (entries without a timestamp sort
 * as oldest), with `index` as a stable tiebreak.
 */
export function orderEvents(
  events: readonly MonitoringRuntimeEvent[],
  order: EventOrder = 'oldest-first',
): MonitoringRuntimeEvent[] {
  const direction = order === 'newest-first' ? -1 : 1;
  const keyed = events.map((event) => ({
    event,
    time: parseTimestamp(event.occurredAt)?.getTime() ?? null,
  }));

  keyed.sort((a, b) => {
    const primary = compareNullable(a.time, b.time);
    if (primary !== 0) return primary * direction;
    return (a.event.index - b.event.index) * direction;
  });

  return keyed.map((entry) => entry.event);
}

/** Compare two nullable numbers; null sorts before any value (treated oldest). */
function compareNullable(a: number | null, b: number | null): number {
  if (a === null && b === null) return 0;
  if (a === null) return -1;
  if (b === null) return 1;
  return a - b;
}

// ─────────────────────────────────────────────────────────────────────────────
// Normalization (unknown JSON -> typed shape)
// ─────────────────────────────────────────────────────────────────────────────

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Return a non-empty trimmed string, or null. */
function asString(value: unknown): string | null {
  if (typeof value !== 'string') return null;
  const trimmed = value.trim();
  return trimmed === '' ? null : trimmed;
}

/** Return a finite number, or null. */
function asNullableNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

/** Coerce a value to a typed payload (object map) or null. */
function asPayload(value: unknown): RuntimeEventPayload {
  return isRecord(value) ? value : null;
}

const KNOWN_SEVERITIES: ReadonlySet<string> = new Set([
  'Info',
  'Warning',
  'Error',
  'Critical',
]);

/** Coerce a value to a known {@link EventSeverity}, or null. */
function asEventSeverity(value: unknown): EventSeverity | null {
  if (typeof value === 'string' && KNOWN_SEVERITIES.has(value)) {
    return value as EventSeverity;
  }
  return null;
}

/**
 * Normalize an untyped runtime event entry (e.g. parsed from a `fetch`/SSE JSON
 * payload) into a typed {@link MonitoringRuntimeEvent}, filling stable fallbacks
 * for missing/invalid fields. Never throws.
 *
 * @param raw   The raw event object.
 * @param index Fallback index when the raw entry lacks one.
 */
export function normalizeEvent(
  raw: unknown,
  index = 0,
): MonitoringRuntimeEvent {
  const obj = isRecord(raw) ? raw : {};
  const rawIndex = asNullableNumber(obj['index']);
  return {
    index: rawIndex ?? index,
    status: normalizeParseStatusKeyWire(obj['status']),
    eventId: asString(obj['eventId']),
    runId: asString(obj['runId']),
    runtimeRunId: asString(obj['runtimeRunId']),
    sequence: asNullableNumber(obj['sequence']),
    occurredAt: asString(obj['occurredAt']),
    eventType: asString(obj['eventType']),
    severity: asEventSeverity(obj['severity']),
    message: asString(obj['message']),
    payload: asPayload(obj['payload']),
    rawLine: asString(obj['rawLine']),
    parseError: asString(obj['parseError']),
  };
}

function normalizeParseStatusKeyWire(value: unknown): RuntimeEventParseStatus {
  const key = normalizeParseStatusKey(typeof value === 'string' ? value : '');
  switch (key) {
    case 'valid':
      return 'Valid';
    case 'missing-fields':
      return 'MissingFields';
    case 'malformed':
      return 'Malformed';
    default:
      return 'Valid';
  }
}

/**
 * Normalize an untyped monitoring response (e.g. the parsed body of
 * `GET /api/monitor/events` or an SSE `data:` payload) into a fully typed
 * {@link MonitorEventsResponse}, tolerant of older/missing fields. Never throws;
 * a missing `runtimeEvents` snapshot yields an empty stream.
 */
export function normalizeMonitorEventsResponse(raw: unknown): MonitorEventsResponse {
  const obj = isRecord(raw) ? raw : {};
  const snapshot = isRecord(obj['runtimeEvents']) ? obj['runtimeEvents'] : {};
  const rawEvents = Array.isArray(snapshot['events']) ? snapshot['events'] : [];
  const events: MonitoringRuntimeEvent[] = rawEvents.map((entry, index) =>
    normalizeEvent(entry, index),
  );

  const normalizedSnapshot: MonitoringRuntimeEventSnapshot = {
    runId: asString(snapshot['runId']),
    events,
    cap: asNullableNumber(snapshot['cap']),
    truncated: Boolean(snapshot['truncated']),
    totalAvailable: asNullableNumber(snapshot['totalAvailable']),
    generatedAt: asString(snapshot['generatedAt']),
  };

  return {
    runId: asString(obj['runId']) ?? '',
    status: asString(obj['status']),
    startedAt: asString(obj['startedAt']),
    finishedAt: asString(obj['finishedAt']),
    lastHeartbeatAt: asString(obj['lastHeartbeatAt']),
    generatedAt: asString(obj['generatedAt']),
    runtimeEvents: normalizedSnapshot,
  };
}
