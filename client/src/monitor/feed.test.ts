/**
 * Colocated coverage for the monitoring feed view-model derivation
 * (`src/monitor/feed.ts`). Run with `bun test` from client/.
 */

import { describe, expect, it } from 'bun:test';

import {
  DEFAULT_FEED_ORDER,
  deriveFeed,
  feedSignature,
} from './feed.js';

const NOW = Date.UTC(2026, 5, 17, 12, 0, 0); // 2026-06-17T12:00:00Z
const iso = (offsetMs: number) => new Date(NOW + offsetMs).toISOString();

function snapshot(events: unknown[], extra: Record<string, unknown> = {}) {
  return {
    runId: 'run_1',
    status: 'AgentRunning',
    runtimeEvents: {
      events,
      cap: 200,
      truncated: false,
      totalAvailable: events.length,
      generatedAt: iso(0),
      ...extra,
    },
  };
}

describe('deriveFeed', () => {
  it('defaults to newest-first ordering', () => {
    const vm = deriveFeed(
      snapshot([
        { index: 0, status: 'Valid', eventId: 'a', occurredAt: iso(-60_000), eventType: 'runtime.status' },
        { index: 1, status: 'Valid', eventId: 'b', occurredAt: iso(-10_000), eventType: 'runtime.status' },
      ]),
      { now: NOW },
    );
    expect(vm.order).toBe(DEFAULT_FEED_ORDER);
    expect(vm.items.map((i) => i.key)).toEqual(['evt:b', 'evt:a']);
  });

  it('respects oldest-first ordering', () => {
    const vm = deriveFeed(
      snapshot([
        { index: 0, status: 'Valid', eventId: 'a', occurredAt: iso(-60_000) },
        { index: 1, status: 'Valid', eventId: 'b', occurredAt: iso(-10_000) },
      ]),
      { order: 'oldest-first', now: NOW },
    );
    expect(vm.items.map((i) => i.key)).toEqual(['evt:a', 'evt:b']);
  });

  it('formats title, message, summary, severity, and timestamps', () => {
    const vm = deriveFeed(
      snapshot([
        {
          index: 3,
          status: 'Valid',
          eventId: 'evt_9',
          occurredAt: iso(-5 * 60_000),
          eventType: 'runtime.pr_created',
          severity: 'Critical',
          message: 'Opened PR #42',
          payload: { files: 3, additions: 120 },
        },
      ]),
      { now: NOW },
    );
    const item = vm.items[0]!;
    expect(item.title).toBe('PR created');
    expect(item.message).toBe('Opened PR #42');
    expect(item.summary).toBe('files=3, additions=120');
    expect(item.severityLabel).toBe('Critical');
    expect(item.severityClassName).toBe('severity-critical');
    expect(item.occurredRelative).toBe('5m ago');
    expect(item.occurredIso).toBe(iso(-5 * 60_000));
    expect(item.key).toBe('evt:evt_9');
  });

  it('falls back to index keys when eventId is missing', () => {
    const vm = deriveFeed(
      snapshot([{ index: 7, status: 'Valid', eventType: 'runtime.heartbeat' }]),
      { now: NOW },
    );
    expect(vm.items[0]!.key).toBe('idx:7');
  });

  it('keeps malformed entries visible with a malformed badge', () => {
    const vm = deriveFeed(
      snapshot([
        { index: 0, status: 'Malformed', rawLine: '{boom', parseError: 'Unexpected token' },
      ]),
      { now: NOW },
    );
    const item = vm.items[0]!;
    expect(item.malformed).toBe(true);
    expect(item.title).toBe('Malformed entry');
    expect(item.message).toBe('Unexpected token');
    expect(item.statusLabel).toBe('Malformed');
    expect(item.statusClassName).toBe('parse-status-malformed');
  });

  it('populates the raw inspection view per event', () => {
    const vm = deriveFeed(
      snapshot([
        {
          index: 0,
          status: 'Valid',
          eventId: 'a',
          eventType: 'runtime.status',
          rawLine: '{"a":1}',
          payload: { a: 1, nested: { x: 2 } },
        },
      ]),
      { now: NOW },
    );
    const raw = vm.items[0]!.raw;
    expect(raw.rawLine).toBe('{"a":1}');
    expect(raw.rawLineTruncated).toBe(false);
    expect(raw.payloadJson).toContain('"a": 1');
    expect(raw.payloadJson).toContain('"nested"');
    expect(raw.parseError).toBe('');
  });

  it('reports an empty view when there are no events', () => {
    const vm = deriveFeed(snapshot([]), { now: NOW });
    expect(vm.empty).toBe(true);
    expect(vm.items).toEqual([]);
    expect(vm.returnedCount).toBe(0);
  });

  it('surfaces truncation, cap, and total counts', () => {
    const vm = deriveFeed(
      snapshot(
        [{ index: 0, status: 'Valid', eventType: 'runtime.heartbeat' }],
        { truncated: true, totalAvailable: 42, cap: 200 },
      ),
      { now: NOW },
    );
    expect(vm.truncated).toBe(true);
    expect(vm.totalAvailable).toBe(42);
    expect(vm.cap).toBe(200);
    expect(vm.returnedCount).toBe(1);
  });

  it('is resilient to entirely invalid payloads', () => {
    const vm = deriveFeed('garbage', { now: NOW });
    expect(vm.empty).toBe(true);
    expect(vm.runId).toBe('');
    expect(vm.items).toEqual([]);
  });
});

describe('feedSignature', () => {
  it('changes when content changes and is stable otherwise', () => {
    const payload = snapshot([
      { index: 0, status: 'Valid', eventId: 'a', occurredAt: iso(-60_000), message: 'hi' },
    ]);
    const a = feedSignature(deriveFeed(payload, { now: NOW }));
    const a2 = feedSignature(deriveFeed(payload, { now: NOW + 999 }));
    expect(a).toBe(a2); // relative time drift must NOT change the signature

    const changed = feedSignature(
      deriveFeed(
        snapshot([
          { index: 0, status: 'Valid', eventId: 'a', occurredAt: iso(-60_000), message: 'bye' },
        ]),
        { now: NOW },
      ),
    );
    expect(changed).not.toBe(a);
  });

  it('changes when the display order changes', () => {
    const payload = snapshot([
      { index: 0, status: 'Valid', eventId: 'a', occurredAt: iso(-60_000) },
      { index: 1, status: 'Valid', eventId: 'b', occurredAt: iso(-10_000) },
    ]);
    const newest = feedSignature(deriveFeed(payload, { order: 'newest-first', now: NOW }));
    const oldest = feedSignature(deriveFeed(payload, { order: 'oldest-first', now: NOW }));
    expect(newest).not.toBe(oldest);
  });

  it('changes when an event raw line or parse error changes', () => {
    const before = feedSignature(
      deriveFeed(
        snapshot([{ index: 0, status: 'Malformed', rawLine: 'abc', parseError: 'e1' }]),
        { now: NOW },
      ),
    );
    const afterLine = feedSignature(
      deriveFeed(
        snapshot([{ index: 0, status: 'Malformed', rawLine: 'xyz', parseError: 'e1' }]),
        { now: NOW },
      ),
    );
    const afterError = feedSignature(
      deriveFeed(
        snapshot([{ index: 0, status: 'Malformed', rawLine: 'abc', parseError: 'e2' }]),
        { now: NOW },
      ),
    );
    expect(before).not.toBe(afterLine);
    expect(before).not.toBe(afterError);
  });
});
