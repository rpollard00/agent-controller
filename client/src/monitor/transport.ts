/**
 * Local-sync transport for the monitoring tab.
 *
 * Connects the UI to the controller's monitoring channel:
 *   - {@link fetchSnapshot} pulls a one-shot JSON snapshot from
 *     `GET /api/monitor/events`.
 *   - {@link openMonitorStream} subscribes to live updates via server-sent
 *     events (`?stream=true`), used for "live updates as the local sync data
 *     changes".
 *
 * Both paths normalize untyped JSON into a typed {@link MonitorEventsResponse}
 * (tolerant of older/missing/malformed shapes). `fetch` and `EventSource` are
 * injectable so the parsing/wiring is unit-testable without a network.
 */

import type { MonitorEventsResponse } from '../types.js';
import { normalizeMonitorEventsResponse } from '../formatters.js';

/** Normalize an API base into a prefix with no trailing slash (empty = same-origin). */
function normalizeBase(apiBase: string | undefined): string {
  const base = (apiBase ?? '').trim();
  if (base === '') return '';
  return base.endsWith('/') ? base.slice(0, -1) : base;
}

/** Build the snapshot URL for `GET /api/monitor/events`. */
export function buildSnapshotUrl(apiBase: string | undefined, runId: string, cap?: number): string {
  const params = new URLSearchParams({ runId });
  if (cap != null && Number.isFinite(cap)) {
    params.set('cap', String(cap));
  }
  return `${normalizeBase(apiBase)}/api/monitor/events?${params.toString()}`;
}

/** Build the SSE stream URL (`?stream=true`) for live updates. */
export function buildStreamUrl(apiBase: string | undefined, runId: string, cap?: number): string {
  const params = new URLSearchParams({ runId, stream: 'true' });
  if (cap != null && Number.isFinite(cap)) {
    params.set('cap', String(cap));
  }
  return `${normalizeBase(apiBase)}/api/monitor/events?${params.toString()}`;
}

/**
 * Parse one SSE `data:` payload into a typed response. Returns null for
 * empty/invalid JSON (never throws) so a bad frame never breaks the stream.
 */
export function parseSsePayload(data: string): MonitorEventsResponse | null {
  if (typeof data !== 'string') return null;
  const text = data.trim();
  if (text === '') return null;
  try {
    return normalizeMonitorEventsResponse(JSON.parse(text));
  } catch {
    return null;
  }
}

/** Injectable fetch implementation (the global `fetch` satisfies this). */
export type FetchImpl = (
  input: string | URL | Request,
  init?: RequestInit,
) => Promise<Response>;

export interface FetchSnapshotOptions {
  /** API base URL (empty/undefined = same-origin). */
  apiBase?: string;
  /** Run identifier (required). */
  runId: string;
  /** Newest-event cap to request, when set. */
  cap?: number;
  /** Injectable fetch (defaults to the global fetch). */
  fetchImpl?: FetchImpl;
  /** Optional abort signal. */
  signal?: AbortSignal;
}

/**
 * Fetch and normalize a one-shot monitoring snapshot. Throws on a non-2xx
 * response so the caller can render an error state; never throws on a malformed
 * body (the normalizer produces a safe empty stream instead).
 */
export async function fetchSnapshot(options: FetchSnapshotOptions): Promise<MonitorEventsResponse> {
  const url = buildSnapshotUrl(options.apiBase, options.runId, options.cap);
  const fetchImpl = options.fetchImpl ?? fetch;
  const response = await fetchImpl(url, {
    headers: { Accept: 'application/json' },
    signal: options.signal,
  });

  if (!response.ok) {
    throw new Error(`Monitoring request failed (${response.status}).`);
  }

  // A 2xx with a malformed/empty body should not blow up the feed: fall back to
  // an empty stream so the normalizer (not a JSON parse error) shapes the result.
  let json: unknown;
  try {
    json = await response.json();
  } catch {
    json = null;
  }
  return normalizeMonitorEventsResponse(json);
}

/** The minimal EventSource surface this module depends on. */
export interface EventSourceLike {
  addEventListener(type: string, listener: (event: MessageEvent) => void): void;
  removeEventListener(type: string, listener: (event: MessageEvent) => void): void;
  close(): void;
}

/** Constructor signature for an EventSource-like type (injectable for tests). */
export type EventSourceConstructor = new (url: string) => EventSourceLike;

export interface MonitorStreamHandlers {
  /** Called for each parsed monitoring payload received over the stream. */
  onPayload: (payload: MonitorEventsResponse) => void;
  /** Called when the stream signals an error (the source auto-reconnects). */
  onError?: () => void;
}

export interface MonitorStream {
  /** Stop receiving updates and release the underlying connection. */
  close(): void;
}

/**
 * Open a server-sent-event subscription to the monitoring channel and dispatch
 * parsed payloads to {@link MonitorStreamHandlers.onPayload}. The server emits
 * named `monitor` events; `message` events are also handled defensively. A bad
 * frame is skipped (see {@link parseSsePayload}) so the stream keeps flowing.
 *
 * @param url     Stream URL (see {@link buildStreamUrl}).
 * @param handlers Payload/error callbacks.
 * @param ctor    Injectable EventSource constructor (defaults to the global one).
 */
export function openMonitorStream(
  url: string,
  handlers: MonitorStreamHandlers,
  ctor?: EventSourceConstructor,
): MonitorStream {
  const Constructor: EventSourceConstructor | undefined = ctor ?? globalThis.EventSource;
  if (!Constructor) {
    throw new Error('EventSource is not available in this environment.');
  }

  const source = new Constructor(url);
  const listener = (event: MessageEvent): void => {
    const data = typeof event.data === 'string' ? event.data : '';
    const payload = parseSsePayload(data);
    if (payload) {
      handlers.onPayload(payload);
    }
  };

  // Named events are the canonical channel; `message` is a tolerant fallback.
  source.addEventListener('monitor', listener);
  source.addEventListener('message', listener);
  source.addEventListener('error', () => handlers.onError?.());

  return {
    close(): void {
      source.removeEventListener('monitor', listener);
      source.removeEventListener('message', listener);
      source.close();
    },
  };
}
