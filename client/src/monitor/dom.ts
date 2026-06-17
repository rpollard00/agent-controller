/**
 * Minimal HTML-escaping helpers for the monitoring tab.
 *
 * All runtime event text is treated as untrusted (it originates from agent run
 * artifacts and could contain markup), so every interpolation into HTML goes
 * through these helpers to prevent markup injection. The monitoring feed is a
 * read-only view, so escaped text interpolation is sufficient and safe.
 */

const HTML_ESCAPES: Readonly<Record<string, string>> = {
  '&': '&amp;',
  '<': '&lt;',
  '>': '&gt;',
  '"': '&quot;',
  "'": '&#39;',
};

function escape(value: unknown): string {
  const text = value == null ? '' : String(value);
  return text.replace(/[&<>"']/g, (ch) => HTML_ESCAPES[ch] ?? ch);
}

/** Escape a value for safe interpolation as HTML text content. */
export function escapeHtml(value: unknown): string {
  return escape(value);
}

/** Escape a value for safe use inside a double-quoted HTML attribute. */
export function escapeAttr(value: unknown): string {
  return escape(value);
}
