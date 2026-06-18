/**
 * Pure HTML renderers for the monitoring tab runtime event feed.
 *
 * Each function returns an HTML *string* built entirely from escaped text, so
 * the feed is safe against markup injection from untrusted runtime event
 * content. The orchestrator (`main.ts`) mounts these strings via `innerHTML`
 * and wires up interactivity (e.g. the order toggle). Returning strings keeps
 * the render layer pure and unit-testable without a DOM.
 */

import type { FeedItemView, FeedViewModel } from './feed.js';
import { escapeAttr, escapeHtml } from './dom.js';

/** Loading state shown before the first snapshot resolves. */
export function renderLoadingHtml(): string {
  return (
    '<div class="monitoring-state monitoring-state--loading" role="status">' +
    '<span class="monitoring-spinner" aria-hidden="true"></span>' +
    '<span>Loading runtime events…</span>' +
    '</div>'
  );
}

/** Error state shown when the feed could not be loaded (and nothing is cached). */
export function renderErrorHtml(message: string): string {
  const text = message && message.trim() !== '' ? message : 'Failed to load runtime events.';
  return (
    '<div class="monitoring-state monitoring-state--error" role="alert">' +
    `<p>${escapeHtml(text)}</p>` +
    '<p class="monitoring-state-hint">The event stream will retry automatically.</p>' +
    '</div>'
  );
}

/** Empty state shown when the run has no runtime events (yet, or at all). */
export function renderEmptyHtml(message: string): string {
  const text = message && message.trim() !== '' ? message : 'No runtime events yet.';
  return (
    '<div class="monitoring-state monitoring-state--empty">' +
    `<p>${escapeHtml(text)}</p>` +
    '<p class="monitoring-state-hint">New events will appear here as the run progresses.</p>' +
    '</div>'
  );
}

/** Default label for the expand/collapse raw-details toggle. */
const RAW_TOGGLE_LABEL = 'Raw details';

/** Build a DOM-safe, stable id for an event's raw details panel. */
function rawPanelId(key: string): string {
  return `monitoring-raw-${key.replace(/[^A-Za-z0-9_-]/g, '_')}`;
}

/** Render the expand/collapse toggle for an event's raw details. */
function renderRawToggleHtml(item: FeedItemView, expanded: boolean): string {
  const panelId = rawPanelId(item.key);
  return (
    '<div class="monitoring-event-actions">' +
    `<button type="button" class="monitoring-event-raw-toggle" data-raw-toggle ` +
    `aria-expanded="${expanded ? 'true' : 'false'}" aria-controls="${escapeAttr(panelId)}" ` +
    'title="Show the original raw line and parsed payload for this event">' +
    '<span class="monitoring-event-raw-toggle-icon" aria-hidden="true">\u{25B8}</span>' +
    `<span>${escapeHtml(RAW_TOGGLE_LABEL)}</span>` +
    '</button>' +
    '</div>'
  );
}

/** Render the note shown when a raw field was truncated for display. */
function renderTruncationNote(originalLength: number): string {
  return (
    '<span class="monitoring-raw-truncated" role="note">' +
    `Truncated for display; original is ${originalLength.toLocaleString()} characters. ` +
    'Select the text above to copy what is shown.</span>'
  );
}

/** Render the raw details panel (parse error, raw line, parsed payload). */
function renderRawPanelHtml(item: FeedItemView): string {
  const panelId = rawPanelId(item.key);
  const raw = item.raw;
  const parts: string[] = [];

  parts.push(
    `<div class="monitoring-event-raw" id="${escapeAttr(panelId)}" ` +
      'role="region" aria-label="Raw event details">',
  );

  if (raw.parseError) {
    parts.push(
      '<p class="monitoring-raw-parse-error">' +
        '<span class="monitoring-raw-label">Parse error</span>' +
        `<span class="monitoring-raw-value">${escapeHtml(raw.parseError)}</span>` +
        '</p>',
    );
  }

  parts.push('<div class="monitoring-raw-block">');
  parts.push('<span class="monitoring-raw-label">Raw line</span>');
  if (raw.rawLine) {
    parts.push(`<pre class="monitoring-raw-pre">${escapeHtml(raw.rawLine)}</pre>`);
    if (raw.rawLineTruncated) {
      parts.push(renderTruncationNote(raw.rawLineOriginalLength));
    }
  } else {
    parts.push('<span class="monitoring-raw-muted">No raw line captured.</span>');
  }
  parts.push('</div>');

  parts.push('<div class="monitoring-raw-block">');
  parts.push('<span class="monitoring-raw-label">Parsed payload</span>');
  if (raw.payloadJson) {
    parts.push(
      `<pre class="monitoring-raw-pre monitoring-raw-pre--json">${escapeHtml(raw.payloadJson)}</pre>`,
    );
    if (raw.payloadTruncated) {
      parts.push(renderTruncationNote(raw.payloadOriginalLength));
    }
  } else {
    parts.push('<span class="monitoring-raw-muted">No parsed payload.</span>');
  }
  parts.push('</div>');

  parts.push('</div>');
  return parts.join('');
}

/** Render a single event row. */
export function renderItemHtml(item: FeedItemView, expanded = false): string {
  const time = item.occurredRelative ?? item.occurredAbsolute;
  const timeTitle = item.occurredIso ?? item.occurredAbsolute;

  const message = item.message
    ? `<p class="monitoring-event-message">${escapeHtml(item.message)}</p>`
    : '';

  const summary = item.summary
    ? `<span class="monitoring-event-summary" title="${escapeAttr(item.summary)}">${escapeHtml(item.summary)}</span>`
    : '';

  const malformedBadge = item.malformed
    ? `<span class="monitoring-badge monitoring-parse ${escapeAttr(item.statusClassName)}">${escapeHtml(item.statusLabel)}</span>`
    : '';

  return (
    `<li class="monitoring-event ${expanded ? 'monitoring-event--expanded ' : ''}${escapeAttr(item.severityClassName)}" ` +
    `data-event-key="${escapeAttr(item.key)}" data-severity-rank="${escapeAttr(item.severityRank)}">` +
    '<div class="monitoring-event-main">' +
    '<div class="monitoring-event-badges">' +
    `<span class="monitoring-badge monitoring-severity ${escapeAttr(item.severityClassName)}">${escapeHtml(item.severityLabel)}</span>` +
    `<span class="monitoring-badge monitoring-type">${escapeHtml(item.title)}</span>` +
    malformedBadge +
    '</div>' +
    `<time class="monitoring-event-time" datetime="${escapeAttr(item.occurredIso ?? '')}" title="${escapeAttr(timeTitle)}">${escapeHtml(time)}</time>` +
    '</div>' +
    message +
    summary +
    renderRawToggleHtml(item, expanded) +
    renderRawPanelHtml(item) +
    '</li>'
  );
}

/** Render the feed header: event counts, truncation/cap note, and order toggle. */
export function renderHeaderHtml(vm: FeedViewModel): string {
  const orderLabel = vm.order === 'newest-first' ? 'Newest first' : 'Oldest first';
  const nextOrder: FeedViewModel['order'] = vm.order === 'newest-first' ? 'oldest-first' : 'newest-first';
  const nextLabel = nextOrder === 'newest-first' ? 'Newest first' : 'Oldest first';

  return (
    '<header class="monitoring-feed-header">' +
    `<span class="monitoring-feed-counts">${escapeHtml(renderCountsLabel(vm))}</span>` +
    // `data-order-toggle` is the click hook the orchestrator binds
    // (`[data-order-toggle]`); `data-order` carries the target order to apply.
    `<button type="button" class="monitoring-order-toggle" data-order-toggle ` +
    `data-order="${escapeAttr(nextOrder)}" aria-label="Switch event order to ${escapeAttr(nextLabel)}">` +
    `Order: ${escapeHtml(orderLabel)}` +
    '</button>' +
    '</header>'
  );
}

/** Human-readable counts/truncation label for the feed header. */
export function renderCountsLabel(vm: FeedViewModel): string {
  const noun = vm.returnedCount === 1 ? 'event' : 'events';
  let label = `${vm.returnedCount} ${noun}`;
  if (vm.truncated) {
    const total = vm.totalAvailable == null ? '?' : String(vm.totalAvailable);
    label += ` · showing newest ${vm.returnedCount} of ${total}`;
  }
  if (vm.cap != null) {
    label += ` · cap ${vm.cap}`;
  }
  return label;
}

/** Render the full feed: header + ordered list of event rows. */
export function renderFeedHtml(vm: FeedViewModel, expandedKeys?: ReadonlySet<string>): string {
  const items = vm.items
    .map((item) => renderItemHtml(item, expandedKeys?.has(item.key) ?? false))
    .join('');
  return (
    '<section class="monitoring-feed" aria-live="polite" aria-atomic="false">' +
    renderHeaderHtml(vm) +
    `<ul class="monitoring-feed-list">${items}</ul>` +
    '</section>'
  );
}
