/**
 * Orchestrator for the Materia UI monitoring tab.
 *
 * Mounts the runtime event feed onto a `[data-monitoring-tab]` container and
 * drives it from the local-sync channel:
 *   1. Render a loading state, then fetch a one-shot snapshot.
 *   2. Open a server-sent-event subscription for live updates, with a polling
 *      fallback when EventSource is unavailable or the stream ends.
 *   3. Re-derive the view model (and re-render) only when the feed content
 *      actually changes, and refresh relative timestamps on a wall-clock tick.
 *
 * The pure logic lives in `feed.ts` / `render.ts` / `transport.ts`; this module
 * only wires them to the DOM and the network, and is deliberately resilient: a
 * failure never leaves the tab blank (it shows an error or keeps the last good
 * view) and no exception escapes to the page.
 */

import type { MonitorEventsResponse } from '../types.js';
import type { EventOrder } from '../formatters.js';
import {
  DEFAULT_FEED_ORDER,
  deriveFeed,
  feedSignature,
  type FeedOrder,
  type FeedViewModel,
} from './feed.js';
import {
  renderEmptyHtml,
  renderErrorHtml,
  renderFeedHtml,
  renderLoadingHtml,
} from './render.js';
import {
  buildStreamUrl,
  fetchSnapshot,
  openMonitorStream,
  type FetchImpl,
  type MonitorStream,
} from './transport.js';

/** Polling fallback cadence when SSE is unavailable or ends. */
const POLL_INTERVAL_MS = 8_000;
/** How often relative timestamps ("5m ago") are refreshed. */
const RELATIVE_REFRESH_MS = 30_000;

export interface MountMonitoringTabOptions {
  /** Run identifier to monitor (required). */
  runId: string;
  /** API base URL (empty/undefined = same-origin). */
  apiBase?: string;
  /** Newest-event cap to request, when set. */
  cap?: number;
  /** Element to mount the feed into. */
  container: HTMLElement;
  /** Injectable window/location for tests (defaults to the globals). */
  eventSourceCtor?: import('./transport.js').EventSourceConstructor;
  fetchImpl?: FetchImpl;
}

interface TabState {
  status: 'loading' | 'ready' | 'empty' | 'error';
  vm: FeedViewModel | null;
  lastResponse: MonitorEventsResponse | null;
  errorMessage: string;
  order: FeedOrder;
  /** Event keys whose raw-details panel is expanded (survives re-renders). */
  expandedKeys: Set<string>;
}

/**
 * Mount the monitoring feed onto the given container. Returns a cleanup function
 * that closes the stream and clears timers (useful for tests/unmounting).
 */
export function mountMonitoringTab(options: MountMonitoringTabOptions): () => void {
  const { runId, apiBase, cap, container } = options;
  const state: TabState = {
    status: 'loading',
    vm: null,
    lastResponse: null,
    errorMessage: '',
    order: DEFAULT_FEED_ORDER,
    expandedKeys: new Set<string>(),
  };

  let lastSignature = '';
  let stream: MonitorStream | null = null;
  let pollTimer: ReturnType<typeof setInterval> | null = null;
  let refreshTimer: ReturnType<typeof setInterval> | null = null;
  let disposed = false;

  container.addEventListener('click', onContainerClick);

  function render(): void {
    if (state.status === 'loading') {
      container.innerHTML = renderLoadingHtml();
      return;
    }
    if (state.status === 'error') {
      container.innerHTML = renderErrorHtml(state.errorMessage);
      return;
    }
    if (!state.vm || state.vm.empty) {
      container.innerHTML = renderEmptyHtml('No runtime events yet for this run.');
      return;
    }
    container.innerHTML = renderFeedHtml(state.vm, state.expandedKeys);
  }

  /** Re-derive the view model from the last response and re-render if needed. */
  function rederive(force: boolean): void {
    if (!state.lastResponse) return;
    const vm = deriveFeed(state.lastResponse, { order: state.order, now: Date.now() });
    state.vm = vm;
    state.status = vm.empty ? 'empty' : 'ready';
    const signature = feedSignature(vm);
    if (force || signature !== lastSignature) {
      lastSignature = signature;
      render();
    }
  }

  function applyResponse(response: MonitorEventsResponse): void {
    state.lastResponse = response;
    rederive(false);
  }

  async function refresh(): Promise<void> {
    try {
      const response = await fetchSnapshot({
        apiBase,
        runId,
        cap,
        fetchImpl: options.fetchImpl,
      });
      applyResponse(response);
    } catch (error) {
      // Keep the last good view when we have one; only surface an error initially.
      if (!state.vm) {
        state.status = 'error';
        state.errorMessage = describeError(error);
        render();
      }
    }
  }

  function startStream(): void {
    const ctor = options.eventSourceCtor ?? globalThis.EventSource;
    if (!ctor) {
      startPolling();
      return;
    }

    const url = buildStreamUrl(apiBase, runId, cap);
    try {
      stream = openMonitorStream(
        url,
        { onPayload: applyResponse, onError: startPolling },
        ctor,
      );
    } catch {
      stream = null;
      startPolling();
    }
  }

  function startPolling(): void {
    if (pollTimer !== null) return;
    pollTimer = setInterval(() => {
      void refresh();
    }, POLL_INTERVAL_MS);
  }

  function startRelativeRefresh(): void {
    if (refreshTimer !== null) return;
    refreshTimer = setInterval(() => {
      // Re-derive (not refetch) so "5m ago" labels stay current; force a render.
      rederive(true);
    }, RELATIVE_REFRESH_MS);
  }

  function onContainerClick(event: MouseEvent): void {
    const target = event.target as HTMLElement | null;

    // Expand/collapse an event's raw-details panel. Mutate the tracked set
    // *and* the DOM directly so the toggle is snappy and survives the next
    // full re-render (signature change, live update, or relative-time tick),
    // which re-applies `expandedKeys` via `render()`.
    const rawToggle = target?.closest?.('[data-raw-toggle]') as HTMLElement | null;
    if (rawToggle) {
      const row = rawToggle.closest('.monitoring-event') as HTMLElement | null;
      const key = row?.dataset.eventKey;
      if (key) {
        toggleExpanded(key, row);
      }
      return;
    }

    // Feed-wide order toggle.
    const button = target?.closest?.('[data-order-toggle]') as HTMLElement | null;
    if (!button) return;
    const next = button.dataset.order as EventOrder | undefined;
    if (next !== 'oldest-first' && next !== 'newest-first') return;
    if (next === state.order) return;
    state.order = next;
    rederive(true);
  }

  /** Toggle one event's raw-details expansion in state and the DOM. */
  function toggleExpanded(key: string, row: HTMLElement): void {
    const willExpand = !state.expandedKeys.has(key);
    if (willExpand) {
      state.expandedKeys.add(key);
    } else {
      state.expandedKeys.delete(key);
    }
    row.classList.toggle('monitoring-event--expanded', willExpand);
    const toggle = row.querySelector('[data-raw-toggle]');
    toggle?.setAttribute('aria-expanded', willExpand ? 'true' : 'false');
  }

  // Boot sequence.
  void refresh().then(() => {
    if (disposed) return;
    startStream();
    startRelativeRefresh();
  });

  return function dispose(): void {
    disposed = true;
    container.removeEventListener('click', onContainerClick);
    stream?.close();
    stream = null;
    if (pollTimer !== null) {
      clearInterval(pollTimer);
      pollTimer = null;
    }
    if (refreshTimer !== null) {
      clearInterval(refreshTimer);
      refreshTimer = null;
    }
  };
}

/** Render a safe, single-line message for an unknown error value. */
function describeError(error: unknown): string {
  if (error instanceof Error) return error.message;
  if (typeof error === 'string') return error;
  return 'Failed to load runtime events.';
}

/** Resolve mount options from a container's data attributes and the page URL. */
export function resolveOptionsFromContainer(
  container: HTMLElement,
): MountMonitoringTabOptions | null {
  const datasetRunId = container.dataset.runId?.trim();
  const search = typeof location !== 'undefined' ? location.search : '';
  const urlRunId = new URLSearchParams(search).get('runId')?.trim();
  const runId = datasetRunId || urlRunId;
  if (!runId) return null;

  const apiBase = container.dataset.apiBase?.trim() || undefined;
  const capAttr = container.dataset.cap?.trim();
  const parsedCap = capAttr ? Number.parseInt(capAttr, 10) : Number.NaN;

  return {
    runId,
    apiBase,
    cap: Number.isFinite(parsedCap) ? parsedCap : undefined,
    container,
  };
}

/** Auto-mount every `[data-monitoring-tab]` on the page. */
function autoMount(): void {
  const containers = document.querySelectorAll<HTMLElement>('[data-monitoring-tab]');
  containers.forEach((container) => {
    const resolved = resolveOptionsFromContainer(container);
    if (!resolved) {
      container.innerHTML = renderErrorHtml(
        'No run id provided. Add ?runId=<id> to the URL or a data-run-id attribute.',
      );
      return;
    }
    mountMonitoringTab(resolved);
  });
}

if (typeof document !== 'undefined') {
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', autoMount, { once: true });
  } else {
    autoMount();
  }
}
