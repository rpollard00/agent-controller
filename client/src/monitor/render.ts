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

/** Render a single event row. */
export function renderItemHtml(item: FeedItemView): string {
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
    `<li class="monitoring-event ${escapeAttr(item.severityClassName)}" ` +
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
export function renderFeedHtml(vm: FeedViewModel): string {
  const items = vm.items.map(renderItemHtml).join('');
  return (
    '<section class="monitoring-feed" aria-live="polite" aria-atomic="false">' +
    renderHeaderHtml(vm) +
    `<ul class="monitoring-feed-list">${items}</ul>` +
    '</section>'
  );
}
