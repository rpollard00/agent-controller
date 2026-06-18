/**
 * Colocated coverage for the monitoring runtime event formatting helpers.
 *
 * Run with: `bun test` (from client/). These cover timestamp display, severity
 * styling, event title/message formatting, payload summarization, fallback
 * labels for unknown shapes, ordering, and normalization of untyped JSON.
 */

import { describe, expect, it } from 'bun:test';

import type { MonitoringRuntimeEvent } from './types.js';

import {
  DEFAULT_RAW_LINE_MAX,
  DEFAULT_RAW_PAYLOAD_MAX,
  formatCompletionOutcome,
  formatEventMessage,
  formatEventTitle,
  formatPayloadJson,
  formatRawDetails,
  formatRelativeTime,
  formatTimestamp,
  getEventTypeLabel,
  getParseStatusInfo,
  getSeverityInfo,
  normalizeEvent,
  normalizeMonitorEventsResponse,
  normalizeParseStatusKey,
  normalizeSeverityKey,
  orderEvents,
  parseTimestamp,
  summarizePayload,
  UNKNOWN_EVENT_TITLE,
} from './formatters.js';

const NOW = Date.UTC(2026, 5, 17, 12, 0, 0); // 2026-06-17T12:00:00Z
const ISO = (offsetMs: number) => new Date(NOW + offsetMs).toISOString();

function validEvent(overrides: Partial<MonitoringRuntimeEvent> = {}): MonitoringRuntimeEvent {
  return {
    index: 0,
    status: 'Valid',
    eventId: 'evt_1',
    occurredAt: ISO(-60_000),
    eventType: 'runtime.status',
    severity: 'Info',
    message: 'Running tests',
    ...overrides,
  };
}

// ── Timestamps ───────────────────────────────────────────────────────────────

describe('parseTimestamp', () => {
  it('parses valid ISO strings', () => {
    expect(parseTimestamp('2026-06-17T12:00:00Z')?.getTime()).toBe(NOW);
  });

  it('returns null for missing or invalid values', () => {
    expect(parseTimestamp(undefined)).toBeNull();
    expect(parseTimestamp(null)).toBeNull();
    expect(parseTimestamp('')).toBeNull();
    expect(parseTimestamp('   ')).toBeNull();
    expect(parseTimestamp('not-a-date')).toBeNull();
  });
});

describe('formatRelativeTime', () => {
  it('formats recent and distant past values', () => {
    expect(formatRelativeTime(ISO(-10_000), NOW)).toBe('just now');
    expect(formatRelativeTime(ISO(-5 * 60_000), NOW)).toBe('5m ago');
    expect(formatRelativeTime(ISO(-3 * 3_600_000), NOW)).toBe('3h ago');
    expect(formatRelativeTime(ISO(-2 * 86_400_000), NOW)).toBe('2d ago');
  });

  it('clamps allowed future skew to "just now"', () => {
    // Server permits up to 5 minutes of future skew.
    expect(formatRelativeTime(ISO(2 * 60_000), NOW)).toBe('just now');
  });

  it('returns null beyond a week and for invalid input', () => {
    expect(formatRelativeTime(ISO(-10 * 86_400_000), NOW)).toBeNull();
    expect(formatRelativeTime(null, NOW)).toBeNull();
    expect(formatRelativeTime('garbage', NOW)).toBeNull();
  });
});

describe('formatTimestamp', () => {
  it('returns absolute + relative for valid values', () => {
    const display = formatTimestamp(ISO(-60_000), { now: NOW });
    expect(display.iso).toBe(ISO(-60_000));
    expect(display.date).not.toBeNull();
    expect(display.absolute.length).toBeGreaterThan(0);
    expect(display.relative).toBe('1m ago');
  });

  it('falls back gracefully for invalid input', () => {
    const display = formatTimestamp(undefined, { now: NOW });
    expect(display.iso).toBeNull();
    expect(display.date).toBeNull();
    expect(display.absolute).toBe('—');
    expect(display.relative).toBeNull();
  });
});

// ── Severity ─────────────────────────────────────────────────────────────────

describe('severity', () => {
  it('maps wire PascalCase values to styling tokens', () => {
    expect(getSeverityInfo('Critical')).toMatchObject({
      key: 'critical',
      label: 'Critical',
      rank: 3,
      className: 'severity-critical',
    });
    expect(getSeverityInfo('Warning').rank).toBeGreaterThan(getSeverityInfo('Info').rank);
    expect(getSeverityInfo('Error').rank).toBeGreaterThan(getSeverityInfo('Warning').rank);
  });

  it('tolerates lowercase and aliases', () => {
    expect(normalizeSeverityKey('warning')).toBe('warning');
    expect(normalizeSeverityKey('WARN')).toBe('warning');
    expect(normalizeSeverityKey('fatal')).toBe('critical');
    expect(normalizeSeverityKey('err')).toBe('error');
  });

  it('falls back to unknown for missing/unknown values', () => {
    expect(normalizeSeverityKey(undefined)).toBe('unknown');
    expect(normalizeSeverityKey('bogus')).toBe('unknown');
    expect(getSeverityInfo(null).className).toBe('severity-unknown');
    expect(getSeverityInfo('bogus').label).toBe('Unknown');
  });
});

// ── Parse status ─────────────────────────────────────────────────────────────

describe('parse status', () => {
  it('maps wire PascalCase statuses to labels', () => {
    expect(getParseStatusInfo('Malformed')).toMatchObject({
      key: 'malformed',
      label: 'Malformed',
      className: 'parse-status-malformed',
    });
    expect(getParseStatusInfo('MissingFields').label).toBe('Missing fields');
    expect(getParseStatusInfo('Valid').key).toBe('valid');
  });

  it('normalizes unknown values', () => {
    expect(normalizeParseStatusKey('Something')).toBe('unknown');
    expect(normalizeParseStatusKey(undefined)).toBe('unknown');
    expect(getParseStatusInfo('garbage').className).toBe('parse-status-unknown');
  });
});

// ── Event type / title / message ─────────────────────────────────────────────

describe('event type labels', () => {
  it('maps known runtime types to friendly labels', () => {
    expect(getEventTypeLabel('runtime.accepted')).toBe('Accepted');
    expect(getEventTypeLabel('runtime.pr_created')).toBe('PR created');
    expect(getEventTypeLabel('runtime.needs_human')).toBe('Needs human');
  });

  it('humanizes unknown types and falls back when missing', () => {
    expect(getEventTypeLabel('runtime.deploy_started')).toBe('Deploy started');
    expect(getEventTypeLabel(undefined)).toBe(UNKNOWN_EVENT_TITLE);
    expect(getEventTypeLabel('')).toBe(UNKNOWN_EVENT_TITLE);
  });
});

describe('formatEventTitle', () => {
  it('uses the event type label for valid events', () => {
    expect(formatEventTitle(validEvent({ eventType: 'runtime.failed' }))).toBe('Failed');
  });

  it('uses dedicated fallback titles for unparsable entries', () => {
    expect(formatEventTitle({ index: 0, status: 'Malformed' })).toBe('Malformed entry');
    expect(formatEventTitle({ index: 0, status: 'MissingFields' })).toBe('Unparsed event');
  });
});

describe('formatEventMessage', () => {
  it('uses the parsed message when present', () => {
    expect(formatEventMessage(validEvent({ message: 'Compiling project' }))).toBe(
      'Compiling project',
    );
  });

  it('derives a default from type when the message is missing', () => {
    expect(formatEventMessage(validEvent({ eventType: 'runtime.heartbeat', message: null }))).toBe(
      'Heartbeat received.',
    );
  });

  it('derives a default from completion outcome', () => {
    const event = validEvent({
      eventType: 'runtime.completed',
      message: null,
      payload: { outcome: 'pull_request_opened' },
    });
    expect(formatEventMessage(event)).toBe('Pull request opened');
  });

  it('returns empty for unknown types without a message', () => {
    expect(formatEventMessage(validEvent({ eventType: 'runtime.mystery', message: null }))).toBe('');
  });

  it('surfaces parse errors and fallbacks for malformed/missing entries', () => {
    expect(
      formatEventMessage({ index: 0, status: 'Malformed', parseError: 'Unexpected token' }),
    ).toBe('Unexpected token');
    expect(formatEventMessage({ index: 0, status: 'Malformed' })).toBe(
      'Could not parse this event.',
    );
    expect(formatEventMessage({ index: 0, status: 'MissingFields' })).toBe(
      'Event is missing required identifying fields.',
    );
  });
});

describe('formatCompletionOutcome', () => {
  it('maps known outcomes', () => {
    expect(formatCompletionOutcome('no_changes_needed')).toBe('No changes needed');
    expect(formatCompletionOutcome('branch_pushed')).toBe('Branch pushed');
  });

  it('returns empty for missing/unknown outcomes', () => {
    expect(formatCompletionOutcome(undefined)).toBe('');
    expect(formatCompletionOutcome('magical_solution')).toBe('');
  });
});

// ── Payload summarization ────────────────────────────────────────────────────

describe('summarizePayload', () => {
  it('summarizes primitive values', () => {
    expect(summarizePayload({ phase: 'validation', testsPassed: 42, ok: true })).toBe(
      'phase=validation, testsPassed=42, ok=true',
    );
  });

  it('summarizes collections by count', () => {
    expect(summarizePayload({ tags: ['a', 'b'], config: { x: 1, y: 2 } })).toBe(
      'tags=[2 items], config={2 keys}',
    );
  });

  it('handles null and empty payloads', () => {
    expect(summarizePayload(null)).toBe('');
    expect(summarizePayload({})).toBe('');
    expect(summarizePayload(undefined)).toBe('');
  });

  it('truncates long summaries', () => {
    const long = summarizePayload({ text: 'x'.repeat(500) }, 20);
    expect(long.length).toBeLessThanOrEqual(20);
    expect(long.endsWith('…')).toBe(true);
  });
});

// ── Raw event details (expandable inspection view) ─────────────────────────

describe('formatPayloadJson', () => {
  it('pretty-prints objects with a 2-space indent by default', () => {
    const out = formatPayloadJson({ a: 1, b: 'x' });
    expect(out.text).toBe('{\n  "a": 1,\n  "b": "x"\n}');
    expect(out.truncated).toBe(false);
    expect(out.originalLength).toBe(out.text.length);
  });

  it('returns empty for missing/empty payloads', () => {
    expect(formatPayloadJson(undefined).text).toBe('');
    expect(formatPayloadJson(null).text).toBe('');
    expect(formatPayloadJson({}).text).toBe('');
    expect(formatPayloadJson({}).originalLength).toBe(0);
  });

  it('truncates large payloads and reports the original length', () => {
    const out = formatPayloadJson({ blob: 'x'.repeat(10_000) }, { maxLen: 50 });
    expect(out.truncated).toBe(true);
    expect(out.originalLength).toBeGreaterThan(50);
    expect(out.text.length).toBeLessThanOrEqual(50);
    expect(out.text.endsWith('…')).toBe(true);
  });

  it('never throws on non-serializable payloads', () => {
    const circular: Record<string, unknown> = {};
    circular.self = circular;
    const out = formatPayloadJson(circular);
    expect(out.text).toContain('[Circular]');
    expect(out.truncated).toBe(false);
  });

  it('honours a custom indent width', () => {
    const out = formatPayloadJson({ a: 1 }, { indent: 4 });
    expect(out.text).toBe('{\n    "a": 1\n}');
  });
});

describe('formatRawDetails', () => {
  it('combines parse error, raw line, and pretty payload', () => {
    const details = formatRawDetails({
      index: 0,
      status: 'Valid',
      rawLine: '{"a":1}',
      payload: { a: 1, nested: { x: 2 } },
      parseError: null,
    });
    expect(details.rawLine).toBe('{"a":1}');
    expect(details.rawLineTruncated).toBe(false);
    expect(details.payloadJson).toContain('"a": 1');
    expect(details.payloadJson).toContain('"nested"');
    expect(details.payloadTruncated).toBe(false);
    expect(details.parseError).toBe('');
  });

  it('surfaces parse errors for malformed entries and omits a payload', () => {
    const details = formatRawDetails({
      index: 0,
      status: 'Malformed',
      rawLine: '{boom',
      parseError: 'Unexpected token }',
    });
    expect(details.parseError).toBe('Unexpected token }');
    expect(details.payloadJson).toBe('');
    expect(details.payloadOriginalLength).toBe(0);
  });

  it('trims and tolerates a missing parse error', () => {
    const details = formatRawDetails({
      index: 0,
      status: 'Malformed',
      parseError: '   ',
    });
    expect(details.parseError).toBe('');
  });

  it('handles missing raw line and payload gracefully', () => {
    const details = formatRawDetails({ index: 0, status: 'Valid' });
    expect(details.rawLine).toBe('');
    expect(details.rawLineTruncated).toBe(false);
    expect(details.rawLineOriginalLength).toBe(0);
    expect(details.payloadJson).toBe('');
    expect(details.parseError).toBe('');
  });

  it('truncates large raw lines and payloads via options', () => {
    const longLine = 'x'.repeat(5_000);
    const details = formatRawDetails(
      { index: 0, status: 'Valid', rawLine: longLine, payload: { blob: 'y'.repeat(5_000) } },
      { payloadMaxLen: 100, rawLineMaxLen: 100 },
    );
    expect(details.rawLineTruncated).toBe(true);
    expect(details.rawLineOriginalLength).toBe(5_000);
    expect(details.rawLine.length).toBeLessThanOrEqual(100);
    expect(details.payloadTruncated).toBe(true);
    expect(details.payloadOriginalLength).toBeGreaterThan(100);
    expect(details.payloadJson.length).toBeLessThanOrEqual(100);
  });

  it('applies the documented default caps', () => {
    expect(DEFAULT_RAW_PAYLOAD_MAX).toBeGreaterThan(0);
    expect(DEFAULT_RAW_LINE_MAX).toBeGreaterThan(0);
    const longLine = 'x'.repeat(DEFAULT_RAW_LINE_MAX + 100);
    const details = formatRawDetails({ index: 0, status: 'Valid', rawLine: longLine });
    expect(details.rawLineTruncated).toBe(true);
    expect(details.rawLineOriginalLength).toBe(DEFAULT_RAW_LINE_MAX + 100);
  });
});

// ── Ordering ─────────────────────────────────────────────────────────────────

describe('orderEvents', () => {
  const events: MonitoringRuntimeEvent[] = [
    validEvent({ index: 0, occurredAt: ISO(-3 * 60_000), eventId: 'a' }),
    validEvent({ index: 1, occurredAt: ISO(-1 * 60_000), eventId: 'b' }),
    { index: 2, status: 'Malformed', eventId: null, occurredAt: null },
  ];

  it('orders oldest-first with null timestamps first', () => {
    const ordered = orderEvents(events, 'oldest-first');
    expect(ordered.map((e) => e.eventId)).toEqual([null, 'a', 'b']);
  });

  it('orders newest-first', () => {
    const ordered = orderEvents(events, 'newest-first');
    expect(ordered.map((e) => e.eventId)).toEqual(['b', 'a', null]);
  });

  it('does not mutate the input array', () => {
    const snapshot = [...events];
    orderEvents(events, 'newest-first');
    expect(events.map((e) => e.eventId)).toEqual(snapshot.map((e) => e.eventId));
  });
});

// ── Normalization ────────────────────────────────────────────────────────────

describe('normalizeEvent', () => {
  it('keeps valid fields and coerces optionals', () => {
    const normalized = normalizeEvent({
      index: 4,
      status: 'Valid',
      eventId: 'evt_9',
      eventType: 'runtime.completed',
      severity: 'Critical',
      occurredAt: '2026-06-17T12:00:00Z',
      payload: { outcome: 'failed' },
      extra: 'ignored',
    });
    expect(normalized.eventId).toBe('evt_9');
    expect(normalized.severity).toBe('Critical');
    expect(normalized.payload).toEqual({ outcome: 'failed' });
    // Unknown severities drop to null (formatters then treat as unknown).
    expect(normalizeEvent({ severity: 'bogus' }).severity).toBeNull();
  });

  it('fills stable fallbacks for malformed/empty input', () => {
    expect(normalizeEvent(null).status).toBe('Valid');
    expect(normalizeEvent({}).index).toBe(0);
    expect(normalizeEvent({}, 7).index).toBe(7);
    expect(normalizeEvent({ status: 'Malformed' }).status).toBe('Malformed');
    expect(normalizeEvent({ sequence: 'NaN' }).sequence).toBeNull();
    expect(normalizeEvent({ eventId: '   ' }).eventId).toBeNull();
  });
});

describe('normalizeMonitorEventsResponse', () => {
  it('types a full snapshot payload', () => {
    const normalized = normalizeMonitorEventsResponse({
      runId: 'run_1',
      status: 'AgentRunning',
      runtimeEvents: {
        events: [
          { index: 0, status: 'Valid', eventType: 'runtime.heartbeat' },
          '{not-json',
        ],
        cap: 200,
        truncated: true,
        totalAvailable: 42,
      },
    });
    expect(normalized.runId).toBe('run_1');
    expect(normalized.runtimeEvents.events).toHaveLength(2);
    expect(normalized.runtimeEvents.events[1]?.status).toBe('Valid'); // non-record -> fallback
    expect(normalized.runtimeEvents.truncated).toBe(true);
    expect(normalized.runtimeEvents.totalAvailable).toBe(42);
  });

  it('tolerates missing runtimeEvents with an empty stream', () => {
    const normalized = normalizeMonitorEventsResponse({ runId: 'run_2' });
    expect(normalized.runtimeEvents.events).toEqual([]);
    expect(normalized.runtimeEvents.truncated).toBe(false);
  });

  it('tolerates entirely invalid input', () => {
    const normalized = normalizeMonitorEventsResponse('garbage');
    expect(normalized.runId).toBe('');
    expect(normalized.runtimeEvents.events).toEqual([]);
  });
});
