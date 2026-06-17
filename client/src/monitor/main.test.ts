/**
 * Colocated coverage for the monitoring orchestrator (`src/monitor/main.ts`).
 *
 * These tests exercise the live DOM wiring — in particular the order-toggle
 * click path that previously broke because the rendered button emitted
 * `data-order` while the click handler bound `[data-order-toggle]`.
 *
 * `happy-dom` provides an *isolated* `Window` per test (constructed explicitly,
 * never registered on the globals), so the rest of the suite keeps running in a
 * DOM-free environment and the module-level `autoMount` guard stays inert.
 *
 * Run with `bun test` from client/.
 */

import { afterEach, describe, expect, it } from 'bun:test';
import { Window } from 'happy-dom';

import { mountMonitoringTab } from './main.js';
import type { FetchImpl } from './transport.js';

/**
 * Two events with distinct timestamps so feed ordering is deterministic:
 * newest-first lists `evt:new` first; oldest-first lists `evt:old` first.
 */
const PAYLOAD = {
  runId: 'run_1',
  status: 'AgentRunning',
  runtimeEvents: {
    events: [
      {
        index: 0,
        status: 'Valid',
        eventId: 'old',
        eventType: 'runtime.phase',
        severity: 'Info',
        occurredAt: '2026-06-17T10:00:00Z',
        message: 'Run started',
      },
      {
        index: 1,
        status: 'Valid',
        eventId: 'new',
        eventType: 'runtime.phase',
        severity: 'Info',
        occurredAt: '2026-06-17T12:00:00Z',
        message: 'Run finished',
      },
    ],
    cap: 200,
    truncated: false,
    totalAvailable: 2,
  },
};

/** Tracks the active mount's cleanup so every test tears down its timers. */
let activeDispose: (() => void) | null = null;

afterEach(() => {
  activeDispose?.();
  activeDispose = null;
});

/** Resolve once `predicate` returns a truthy value (timeout-guarded). */
async function waitFor<T>(
  predicate: () => T | null | undefined,
  timeoutMs = 1000,
): Promise<T> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const value = predicate();
    if (value) return value;
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
  throw new Error('waitFor timed out waiting for a condition');
}

/** First rendered event key, or null when no events are mounted yet. */
function firstEventKey(container: HTMLElement): string | null {
  const node = container.querySelector('.monitoring-event') as HTMLElement | null;
  return node?.dataset.eventKey ?? null;
}

/** All rendered event keys, in DOM order. */
function eventKeys(container: HTMLElement): string[] {
  return Array.from(container.querySelectorAll('.monitoring-event')).map(
    (node) => (node as HTMLElement).dataset.eventKey ?? '',
  );
}

/** Injectable fetch that always resolves the two-event snapshot. */
function fakeFetch(): FetchImpl {
  return (() =>
    Promise.resolve(
      new Response(JSON.stringify(PAYLOAD), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    )) as FetchImpl;
}

/** Mount the tab against an isolated happy-dom container. */
function setup(): HTMLElement {
  // No global registration: globalThis.document stays undefined, so the
  // module's autoMount guard never fires and other tests are unaffected.
  const window = new Window();
  const container = window.document.createElement('section') as unknown as HTMLElement;
  activeDispose = mountMonitoringTab({
    runId: 'run_1',
    container,
    fetchImpl: fakeFetch(),
  });
  return container;
}

describe('mountMonitoringTab order toggle (click path)', () => {
  it('renders the feed newest-first after the snapshot resolves', async () => {
    const container = setup();
    await waitFor(() => container.querySelector('.monitoring-event'));

    expect(firstEventKey(container)).toBe('evt:new');
    expect(eventKeys(container)).toEqual(['evt:new', 'evt:old']);
  });

  it('exposes the data-order-toggle hook the orchestrator binds', async () => {
    const container = setup();
    const toggle = await waitFor(
      () => container.querySelector('[data-order-toggle]') as HTMLElement | null,
    );

    // This is the regression contract: the click handler matches
    // `[data-order-toggle]` and reads `dataset.order`, so both must be present.
    expect(toggle).not.toBeNull();
    expect(toggle.dataset.order).toBe('oldest-first'); // target order from newest-first
  });

  it('flips to oldest-first when the order toggle is clicked', async () => {
    const container = setup();
    const toggle = await waitFor(
      () => container.querySelector('[data-order-toggle]') as HTMLElement | null,
    );

    expect(firstEventKey(container)).toBe('evt:new');

    toggle.click();

    // Clicking must re-render in the toggled order.
    expect(firstEventKey(container)).toBe('evt:old');
    expect(eventKeys(container)).toEqual(['evt:old', 'evt:new']);
    // Header now reflects oldest-first and targets newest-first on the next click.
    const toggleAfter = container.querySelector('[data-order-toggle]') as HTMLElement;
    expect(toggleAfter.dataset.order).toBe('newest-first');
  });

  it('toggles back to newest-first on a second click', async () => {
    const container = setup();
    await waitFor(() => container.querySelector('[data-order-toggle]'));

    (container.querySelector('[data-order-toggle]') as HTMLElement).click();
    expect(firstEventKey(container)).toBe('evt:old');

    (container.querySelector('[data-order-toggle]') as HTMLElement).click();
    expect(firstEventKey(container)).toBe('evt:new');
  });

  it('ignores clicks outside the order toggle', async () => {
    const container = setup();
    await waitFor(() => container.querySelector('.monitoring-event'));

    // Clicking an event row (not the toggle) must not change the order.
    (container.querySelector('.monitoring-event') as HTMLElement).click();
    expect(firstEventKey(container)).toBe('evt:new');
  });
});
