/**
 * Colocated coverage for the monitoring transport layer
 * (`src/monitor/transport.ts`). Run with `bun test` from client/.
 */

import { describe, expect, it } from 'bun:test';

import type { MonitorEventsResponse } from '../types.js';
import {
  buildSnapshotUrl,
  buildStreamUrl,
  fetchSnapshot,
  openMonitorStream,
  parseSsePayload,
  type EventSourceConstructor,
  type EventSourceLike,
} from './transport.js';

const PAYLOAD = {
  runId: 'run_1',
  status: 'AgentRunning',
  runtimeEvents: {
    events: [
      { index: 0, status: 'Valid', eventId: 'a', eventType: 'runtime.heartbeat' },
    ],
    cap: 200,
    truncated: false,
    totalAvailable: 1,
  },
};

// ── URL builders ─────────────────────────────────────────────────────────────

describe('buildSnapshotUrl / buildStreamUrl', () => {
  it('builds a same-origin snapshot URL with runId', () => {
    expect(buildSnapshotUrl('', 'run_1')).toBe('/api/monitor/events?runId=run_1');
  });

  it('includes cap when provided', () => {
    const url = buildSnapshotUrl(undefined, 'run_1', 50);
    expect(url).toContain('runId=run_1');
    expect(url).toContain('cap=50');
  });

  it('normalizes an api base with a trailing slash', () => {
    expect(buildSnapshotUrl('http://localhost:5000/', 'run_1')).toBe(
      'http://localhost:5000/api/monitor/events?runId=run_1',
    );
  });

  it('builds a stream URL with stream=true', () => {
    const url = buildStreamUrl('', 'run_1');
    expect(url).toContain('/api/monitor/events?');
    expect(url).toContain('runId=run_1');
    expect(url).toContain('stream=true');
  });
});

// ── SSE parsing ──────────────────────────────────────────────────────────────

describe('parseSsePayload', () => {
  it('parses a valid monitoring payload', () => {
    const parsed = parseSsePayload(JSON.stringify(PAYLOAD));
    expect(parsed?.runId).toBe('run_1');
    expect(parsed?.runtimeEvents.events).toHaveLength(1);
  });

  it('returns null for empty or invalid frames without throwing', () => {
    expect(parseSsePayload('')).toBeNull();
    expect(parseSsePayload('   ')).toBeNull();
    expect(parseSsePayload('{not json')).toBeNull();
    expect(parseSsePayload('123')).not.toBeNull(); // valid JSON, normalized to empty stream
    const normalized = parseSsePayload('123') as MonitorEventsResponse;
    expect(normalized.runtimeEvents.events).toEqual([]);
  });
});

// ── fetchSnapshot ────────────────────────────────────────────────────────────

describe('fetchSnapshot', () => {
  it('normalizes a successful JSON response', async () => {
    const fetchImpl = (() =>
      Promise.resolve(
        new Response(JSON.stringify(PAYLOAD), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        }),
      ));

    const result = await fetchSnapshot({ runId: 'run_1', fetchImpl });
    expect(result.runId).toBe('run_1');
    expect(result.runtimeEvents.events).toHaveLength(1);
  });

  it('throws on a non-ok response', async () => {
    const fetchImpl = (() =>
      Promise.resolve(new Response('nope', { status: 500 })));
    await expect(fetchSnapshot({ runId: 'run_1', fetchImpl })).rejects.toThrow(
      /failed \(500\)/,
    );
  });

  it('is resilient to a malformed body (normalizes to empty stream)', async () => {
    const fetchImpl = (() =>
      Promise.resolve(
        new Response('{not-json', { status: 200 }),
      ));
    const result = await fetchSnapshot({ runId: 'run_1', fetchImpl });
    expect(result.runtimeEvents.events).toEqual([]);
  });

  it('requests the expected URL', async () => {
    let requested = '';
    const fetchImpl = ((url: string | URL | Request) => {
      requested = String(url);
      return Promise.resolve(new Response(JSON.stringify(PAYLOAD), { status: 200 }));
    });
    await fetchSnapshot({ apiBase: 'http://api', runId: 'run_9', cap: 10, fetchImpl });
    expect(requested).toContain('http://api/api/monitor/events');
    expect(requested).toContain('runId=run_9');
    expect(requested).toContain('cap=10');
  });
});

// ── openMonitorStream ────────────────────────────────────────────────────────

/** Minimal fake EventSource for testing stream wiring. */
class FakeEventSource implements EventSourceLike {
  readonly url: string;
  private listeners = new Map<string, Set<(event: MessageEvent) => void>>();

  constructor(url: string) {
    this.url = url;
    FakeEventSource.lastOpened = this;
  }

  static lastOpened: FakeEventSource | null = null;

  addEventListener(type: string, listener: (event: MessageEvent) => void): void {
    const set = this.listeners.get(type) ?? new Set();
    set.add(listener);
    this.listeners.set(type, set);
  }

  removeEventListener(type: string, listener: (event: MessageEvent) => void): void {
    this.listeners.get(type)?.delete(listener);
  }

  close(): void {
    this.closed = true;
  }

  closed = false;

  emit(type: string, data: string): void {
    const listeners = this.listeners.get(type);
    if (!listeners) return;
    const event = { data } as MessageEvent;
    for (const listener of listeners) listener(event);
  }
}

describe('openMonitorStream', () => {
  it('dispatches parsed payloads from named monitor events', () => {
    const received: MonitorEventsResponse[] = [];
    const stream = openMonitorStream(
      '/api/monitor/events?runId=run_1&stream=true',
      { onPayload: (payload) => received.push(payload) },
      FakeEventSource as unknown as EventSourceConstructor,
    );

    const source = FakeEventSource.lastOpened!;
    source.emit('monitor', JSON.stringify(PAYLOAD));
    expect(received).toHaveLength(1);
    expect(received[0]!.runId).toBe('run_1');

    stream.close();
    expect(source.closed).toBe(true);
  });

  it('also tolerates default message events', () => {
    const received: MonitorEventsResponse[] = [];
    openMonitorStream(
      '/api/monitor/events?runId=run_1&stream=true',
      { onPayload: (payload) => received.push(payload) },
      FakeEventSource as unknown as EventSourceConstructor,
    );
    FakeEventSource.lastOpened!.emit('message', JSON.stringify(PAYLOAD));
    expect(received).toHaveLength(1);
  });

  it('skips invalid frames without invoking the payload handler', () => {
    const received: MonitorEventsResponse[] = [];
    openMonitorStream(
      '/api/monitor/events?runId=run_1&stream=true',
      { onPayload: (payload) => received.push(payload) },
      FakeEventSource as unknown as EventSourceConstructor,
    );
    FakeEventSource.lastOpened!.emit('monitor', '{not-json');
    FakeEventSource.lastOpened!.emit('monitor', JSON.stringify(PAYLOAD));
    expect(received).toHaveLength(1);
  });

  it('invokes onError when the source errors', () => {
    let errored = 0;
    openMonitorStream(
      '/api/monitor/events?runId=run_1&stream=true',
      { onPayload: () => {}, onError: () => (errored += 1) },
      FakeEventSource as unknown as EventSourceConstructor,
    );
    FakeEventSource.lastOpened!.emit('error', '');
    expect(errored).toBe(1);
  });
});
