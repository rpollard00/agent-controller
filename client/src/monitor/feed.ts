/**
 * Pure view-model derivation for the monitoring tab runtime event feed.
 *
 * Turns an untyped local-sync payload (the body of `GET /api/monitor/events` or
 * an SSE `data:` payload) into a ready-to-render {@link FeedViewModel}, reusing
 * the pure helpers in `../formatters.js` for normalization, ordering, timestamp
 * display, severity/parse styling, titles, messages, and payload summaries.
 *
 * Everything here is pure and deterministic (timestamps take an optional `now`),
 * so it is safe to unit-test and to call on both the snapshot and SSE paths. It
 * never throws on unknown/malformed input — malformed entries stay visible with
 * dedicated badges/fallbacks so the feed always renders something.
 */

import type {
  MonitoringRuntimeEvent,
} from '../types.js';
import {
  formatEventMessage,
  formatEventTitle,
  formatRawDetails,
  formatTimestamp,
  getParseStatusInfo,
  getSeverityInfo,
  normalizeMonitorEventsResponse,
  orderEvents,
  summarizePayload,
  type EventOrder,
  type RawDetailsView,
} from '../formatters.js';

/** Display order for the rendered feed; mirrors {@link EventOrder}. */
export type FeedOrder = EventOrder;

/** A single rendered event row, ready for the render layer. */
export interface FeedItemView {
  /** Stable key for dedupe/keys (event id when present, else its source index). */
  key: string;
  /** Source stream index (stable ordering tiebreak, also useful for debugging). */
  index: number;
  /** Human parse-status label (e.g. "Malformed"). */
  statusLabel: string;
  /** Parse-status CSS token (e.g. "parse-status-malformed"). */
  statusClassName: string;
  /** Human severity label (e.g. "Critical"). */
  severityLabel: string;
  /** Severity CSS token (e.g. "severity-critical"). */
  severityClassName: string;
  /** Severity sort rank (higher = more severe); useful for styling/sorting. */
  severityRank: number;
  /** Rendered title (event-type label or dedicated malformed fallback). */
  title: string;
  /** Rendered message (may be empty when nothing applies). */
  message: string;
  /** One-line payload summary (may be empty). */
  summary: string;
  /** ISO 8601 occurrence time, or null when missing/invalid. */
  occurredIso: string | null;
  /** Absolute locale timestamp; never empty ("—" when invalid). */
  occurredAbsolute: string;
  /** Relative phrase ("5m ago"), or null when not meaningful. */
  occurredRelative: string | null;
  /** True for malformed entries (drives an extra badge). */
  malformed: boolean;
  /** Render-ready raw inspection view (raw line, pretty payload, parse error). */
  raw: RawDetailsView;
}

/** A complete, render-ready view of the monitoring feed. */
export interface FeedViewModel {
  /** Run identifier this feed describes. */
  runId: string;
  /** Run lifecycle status name, when known. */
  status: string | null;
  /** Display order used to derive `items`. */
  order: FeedOrder;
  /** Ordered, render-ready event rows. */
  items: FeedItemView[];
  /** Total events available in the source before any cap, when known. */
  totalAvailable: number | null;
  /** Number of events returned in this snapshot (pre-ordering count). */
  returnedCount: number;
  /** True when the source had more events than returned (cap dropped some). */
  truncated: boolean;
  /** Newest-event cap applied by the producer, when known. */
  cap: number | null;
  /** When this payload was generated, when known. */
  generatedAt: string | null;
  /** Convenience: true when there are no events to render. */
  empty: boolean;
}

export interface DeriveFeedOptions {
  /** Display order (default: newest-first, the documented UI preference). */
  order?: FeedOrder;
  /** Epoch ms representing "now" for relative timestamps (default: Date.now()). */
  now?: number;
}

/** Canonical newest-first default, matching the documented UI preference. */
export const DEFAULT_FEED_ORDER: FeedOrder = 'newest-first';

/**
 * Derive a render-ready feed view model from an untyped local-sync payload.
 *
 * Normalizes the payload (tolerant of older/missing/malformed shapes), orders
 * the events, and formats each row. Never throws.
 */
export function deriveFeed(raw: unknown, opts: DeriveFeedOptions = {}): FeedViewModel {
  const order: FeedOrder = opts.order ?? DEFAULT_FEED_ORDER;
  const now = opts.now ?? Date.now();

  const response = normalizeMonitorEventsResponse(raw);
  const snapshot = response.runtimeEvents;
  const ordered = orderEvents(snapshot.events, order);

  const items = ordered.map((event) => deriveItem(event, now));
  const returnedCount = snapshot.events.length;

  return {
    runId: response.runId,
    status: response.status ?? null,
    order,
    items,
    totalAvailable: snapshot.totalAvailable ?? null,
    returnedCount,
    truncated: snapshot.truncated,
    cap: snapshot.cap ?? null,
    generatedAt: response.generatedAt ?? snapshot.generatedAt ?? null,
    empty: items.length === 0,
  };
}

/** Format a single event into a render-ready row. */
function deriveItem(event: MonitoringRuntimeEvent, now: number): FeedItemView {
  const status = getParseStatusInfo(event.status);
  const severity = getSeverityInfo(event.severity);
  const timestamp = formatTimestamp(event.occurredAt, { now });

  return {
    key: event.eventId ? `evt:${event.eventId}` : `idx:${event.index}`,
    index: event.index,
    statusLabel: status.label,
    statusClassName: status.className,
    severityLabel: severity.label,
    severityClassName: severity.className,
    severityRank: severity.rank,
    title: formatEventTitle(event),
    message: formatEventMessage(event),
    summary: summarizePayload(event.payload),
    occurredIso: timestamp.iso,
    occurredAbsolute: timestamp.absolute,
    occurredRelative: timestamp.relative,
    malformed: status.key === 'malformed',
    raw: formatRawDetails(event),
  };
}

/**
 * A stable signature that changes whenever the rendered feed content changes.
 * Used by the orchestrator to skip no-op re-renders on duplicate SSE updates.
 * Intentionally excludes relative timestamps (which drift with wall-clock time)
 * so the orchestrator can refresh relative labels on its own cadence.
 */
export function feedSignature(vm: FeedViewModel): string {
  const header = [
    vm.runId,
    vm.status ?? '',
    vm.order,
    vm.returnedCount,
    vm.truncated,
    vm.totalAvailable ?? '',
    vm.cap ?? '',
  ].join('|');

  const body = vm.items
    .map(
      (item) =>
        `${item.key}\u{241F}${item.severityRank}\u{241F}${item.title}\u{241F}${item.message}\u{241F}${item.summary}\u{241F}${item.occurredIso ?? ''}\u{241F}${item.raw.rawLine}\u{241F}${item.raw.payloadJson}\u{241F}${item.raw.parseError}`,
    )
    .join('\n');

  return `${header}\n#${vm.items.length}\n${body}`;
}
