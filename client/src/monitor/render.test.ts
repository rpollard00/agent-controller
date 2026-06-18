/**
 * Colocated coverage for the monitoring render layer (`src/monitor/render.ts`).
 * Run with `bun test` from client/.
 */

import { describe, expect, it } from 'bun:test';

import type { FeedItemView, FeedViewModel } from './feed.js';
import {
  renderCountsLabel,
  renderEmptyHtml,
  renderErrorHtml,
  renderFeedHtml,
  renderItemHtml,
  renderLoadingHtml,
} from './render.js';

function item(overrides: Partial<FeedItemView> = {}): FeedItemView {
  return {
    key: 'evt:1',
    index: 0,
    statusLabel: 'Valid',
    statusClassName: 'parse-status-valid',
    severityLabel: 'Info',
    severityClassName: 'severity-info',
    severityRank: 0,
    title: 'Status',
    message: 'Running tests',
    summary: 'phase=validation',
    occurredIso: '2026-06-17T12:00:00Z',
    occurredAbsolute: '6/17/2026, 12:00 PM',
    occurredRelative: '1m ago',
    malformed: false,
    raw: {
      payloadJson: '{\n  "phase": "validation"\n}',
      payloadTruncated: false,
      payloadOriginalLength: 26,
      rawLine: '{"eventType":"runtime.status","phase":"validation"}',
      rawLineTruncated: false,
      rawLineOriginalLength: 48,
      parseError: '',
    },
    ...overrides,
  };
}

function vm(overrides: Partial<FeedViewModel> = {}): FeedViewModel {
  return {
    runId: 'run_1',
    status: 'AgentRunning',
    order: 'newest-first',
    items: [item()],
    totalAvailable: 1,
    returnedCount: 1,
    truncated: false,
    cap: 200,
    generatedAt: '2026-06-17T12:00:00Z',
    empty: false,
    ...overrides,
  };
}

describe('render states', () => {
  it('renders a loading marker', () => {
    const html = renderLoadingHtml();
    expect(html).toContain('monitoring-state--loading');
    expect(html).toContain('Loading runtime events');
  });

  it('renders an error state with the provided message', () => {
    const html = renderErrorHtml('Monitoring request failed (500).');
    expect(html).toContain('monitoring-state--error');
    expect(html).toContain('Monitoring request failed (500).');
  });

  it('falls back to a default error message', () => {
    expect(renderErrorHtml('')).toContain('Failed to load runtime events.');
  });

  it('renders an empty state', () => {
    const html = renderEmptyHtml('No runtime events yet.');
    expect(html).toContain('monitoring-state--empty');
    expect(html).toContain('No runtime events yet.');
  });
});

describe('renderItemHtml', () => {
  it('renders severity + type badges, time, message, and summary', () => {
    const html = renderItemHtml(item());
    expect(html).toContain('severity-info');
    expect(html).toContain('>Info<');
    expect(html).toContain('>Status<');
    expect(html).toContain('datetime="2026-06-17T12:00:00Z"');
    expect(html).toContain('1m ago');
    expect(html).toContain('Running tests');
    expect(html).toContain('phase=validation');
    expect(html).toContain('data-event-key="evt:1"');
    expect(html).toContain('data-severity-rank="0"');
  });

  it('escapes untrusted message and summary content', () => {
    const html = renderItemHtml(
      item({ message: '<script>alert(1)</script>', summary: 'a="&"' }),
    );
    expect(html).toContain('&lt;script&gt;');
    expect(html).not.toContain('<script>alert(1)</script>');
    expect(html).toContain('a=&quot;&amp;&quot;');
  });

  it('omits message/summary nodes when empty', () => {
    const html = renderItemHtml(item({ message: '', summary: '' }));
    expect(html).not.toContain('monitoring-event-message');
    expect(html).not.toContain('monitoring-event-summary');
  });

  it('adds a parse-status badge for malformed entries', () => {
    const html = renderItemHtml(
      item({
        malformed: true,
        statusLabel: 'Malformed',
        statusClassName: 'parse-status-malformed',
        title: 'Malformed entry',
        message: 'Unexpected token',
      }),
    );
    expect(html).toContain('parse-status-malformed');
    expect(html).toContain('>Malformed<');
    expect(html).toContain('Malformed entry');
  });

  it('uses the absolute time when no relative time is available', () => {
    const html = renderItemHtml(
      item({ occurredRelative: null, occurredAbsolute: '6/17/2026, 12:00 PM' }),
    );
    expect(html).toContain('>6/17/2026, 12:00 PM<');
  });
});

describe('renderItemHtml raw details', () => {
  it('renders a collapsed raw-details toggle by default', () => {
    const html = renderItemHtml(item());
    expect(html).toContain('data-raw-toggle');
    expect(html).toContain('aria-expanded="false"');
    expect(html).toContain('>Raw details<');
    // The panel is always present (copy-friendly); the row just isn't expanded.
    expect(html).toContain('monitoring-event-raw');
    expect(html).not.toContain('monitoring-event--expanded');
  });

  it('marks the row expanded and flips aria-expanded when expanded', () => {
    const html = renderItemHtml(item(), true);
    expect(html).toContain('monitoring-event--expanded');
    expect(html).toContain('aria-expanded="true"');
  });

  it('links the toggle to the panel via aria-controls/id', () => {
    const html = renderItemHtml(item());
    expect(html).toContain('aria-controls="monitoring-raw-evt_1"');
    expect(html).toContain('id="monitoring-raw-evt_1"');
  });

  it('renders the raw line and pretty payload in selectable <pre> blocks', () => {
    const html = renderItemHtml(item());
    expect(html).toContain('Raw line');
    expect(html).toContain('Parsed payload');
    expect(html).toContain('monitoring-raw-pre--json');
    expect(html).toContain('<pre class="monitoring-raw-pre">');
    // Pretty payload content is escaped but present.
    expect(html).toContain('phase&quot;: &quot;validation');
  });

  it('escapes untrusted raw line and payload content', () => {
    const html = renderItemHtml(
      item({
        raw: {
          payloadJson: '{ "x": "</script>" }',
          payloadTruncated: false,
          payloadOriginalLength: 20,
          rawLine: '<script>alert(1)</script>',
          rawLineTruncated: false,
          rawLineOriginalLength: 27,
          parseError: '',
        },
      }),
    );
    expect(html).toContain('&lt;script&gt;');
    expect(html).not.toContain('<script>alert(1)</script>');
  });

  it('shows muted fallbacks when raw line and payload are missing', () => {
    const html = renderItemHtml(
      item({
        raw: {
          payloadJson: '',
          payloadTruncated: false,
          payloadOriginalLength: 0,
          rawLine: '',
          rawLineTruncated: false,
          rawLineOriginalLength: 0,
          parseError: '',
        },
      }),
    );
    expect(html).toContain('No raw line captured.');
    expect(html).toContain('No parsed payload.');
  });

  it('renders parse-error context for malformed entries', () => {
    const html = renderItemHtml(
      item({
        malformed: true,
        statusLabel: 'Malformed',
        statusClassName: 'parse-status-malformed',
        title: 'Malformed entry',
        message: 'Unexpected token }',
        raw: {
          payloadJson: '',
          payloadTruncated: false,
          payloadOriginalLength: 0,
          rawLine: '{boom',
          rawLineTruncated: false,
          rawLineOriginalLength: 5,
          parseError: 'Unexpected token }',
        },
      }),
    );
    expect(html).toContain('Parse error');
    expect(html).toContain('Unexpected token }');
  });

  it('renders a truncation note only when a field was truncated', () => {
    const collapsed = renderItemHtml(item());
    expect(collapsed).not.toContain('Truncated for display');

    const html = renderItemHtml(
      item({
        raw: {
          payloadJson: '{ "a": 1 …',
          payloadTruncated: true,
          payloadOriginalLength: 9_999,
          rawLine: 'x'.repeat(10),
          rawLineTruncated: true,
          rawLineOriginalLength: 9_999,
          parseError: '',
        },
      }),
    );
    expect(html).toContain('Truncated for display');
    expect(html).toContain('characters');
  });
});

describe('renderCountsLabel', () => {
  it('pluralizes and reports truncation/cap', () => {
    expect(renderCountsLabel(vm({ returnedCount: 1, cap: null }))).toBe('1 event');
    expect(renderCountsLabel(vm({ returnedCount: 3, cap: null }))).toBe('3 events');
    expect(
      renderCountsLabel(vm({ returnedCount: 200, truncated: true, totalAvailable: 42 })),
    ).toContain('showing newest 200 of 42');
    expect(renderCountsLabel(vm({ cap: 200 }))).toContain('cap 200');
  });

  it('uses ? when totalAvailable is unknown but truncated', () => {
    expect(renderCountsLabel(vm({ truncated: true, totalAvailable: null }))).toContain(
      'of ?',
    );
  });
});

describe('renderFeedHtml', () => {
  it('renders the header with counts and an order toggle, plus the list', () => {
    const html = renderFeedHtml(vm());
    expect(html).toContain('monitoring-feed-header');
    expect(html).toContain('monitoring-feed-counts');
    // The orchestrator binds clicks via `[data-order-toggle]`; the rendered
    // button must expose that hook alongside the target `data-order` value.
    expect(html).toContain('data-order-toggle');
    expect(html).toContain('data-order="oldest-first"'); // newest-first -> toggles to oldest
    expect(html).toContain('Order: Newest first');
    expect(html).toContain('<ul class="monitoring-feed-list">');
    // The single item is rendered inside the list.
    expect(html).toContain('monitoring-event-message');
  });

  it('flips the order toggle target when ordered oldest-first', () => {
    const html = renderFeedHtml(vm({ order: 'oldest-first' }));
    expect(html).toContain('data-order="newest-first"');
    expect(html).toContain('Order: Oldest first');
  });
});

describe('renderFeedHtml expandedKeys', () => {
  it('marks matching event keys as expanded', () => {
    const html = renderFeedHtml(vm(), new Set(['evt:1']));
    expect(html).toContain('monitoring-event--expanded');
    expect(html).toContain('aria-expanded="true"');
  });

  it('leaves events collapsed when their key is not in the set', () => {
    const html = renderFeedHtml(vm(), new Set(['evt:other']));
    expect(html).not.toContain('monitoring-event--expanded');
    expect(html).toContain('aria-expanded="false"');
  });

  it('defaults to all collapsed when no set is provided', () => {
    const html = renderFeedHtml(vm());
    expect(html).not.toContain('monitoring-event--expanded');
    expect(html).toContain('aria-expanded="false"');
  });
});
