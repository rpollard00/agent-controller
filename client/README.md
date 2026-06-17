# @agentcontroller/monitoring-client

Client-side types, formatting helpers, and the monitoring tab UI for the
AgentController monitoring/local sync runtime event feed. The Materia UI uses
this to render the runtime event stream as a readable progress feed.

This package mirrors the server payload produced by `GET /api/monitor/events`
(see `src/AgentController.Api/Models/MonitorEventsResponse.cs` and
`src/AgentController.Domain/Monitoring.cs`) and provides both the pure
formatting helpers and the rendered monitoring tab that turns that payload into
a live, readable event feed.

## Contents

### Library (reusable helpers)

- `src/types.ts` — client-side types for the monitoring response, snapshot, and
  runtime events (`MonitorEventsResponse`, `MonitoringRuntimeEvent`, etc.).
- `src/formatters.ts` — pure helpers:
  - **Timestamps**: `parseTimestamp`, `formatRelativeTime`, `formatTimestamp`.
  - **Severity styling**: `getSeverityInfo`, `normalizeSeverityKey` (badges,
    sort rank, CSS/theme tokens).
  - **Parse status**: `getParseStatusInfo`, `normalizeParseStatusKey`.
  - **Title/message**: `getEventTypeLabel`, `formatEventTitle`, `formatEventMessage`,
    `formatCompletionOutcome`.
  - **Payload**: `summarizePayload`.
  - **Ordering**: `orderEvents` (oldest-first / newest-first).
  - **Normalization**: `normalizeEvent`, `normalizeMonitorEventsResponse` for
    untyped `fetch`/SSE JSON.
- `src/formatters.test.ts` — colocated coverage (run with `bun test`).

### Monitoring tab UI (the rendering layer)

- `src/monitor/dom.ts` — HTML-escaping helpers (all runtime event text is
  untrusted, so every interpolation is escaped).
- `src/monitor/feed.ts` — pure view-model derivation: turns an untyped
  local-sync payload into a render-ready `FeedViewModel` (ordering, timestamps,
  badges, titles/messages, payload summaries). Includes `feedSignature` for
  skip-if-unchanged re-renders.
- `src/monitor/render.ts` — pure HTML renderers for feed rows, badges, counts,
  and the loading/empty/error states.
- `src/monitor/transport.ts` — local-sync transport: `fetchSnapshot` (one-shot
  JSON) and `openMonitorStream` (server-sent events for live updates), both with
  injectable `fetch`/`EventSource` for testing.
- `src/monitor/main.ts` — orchestrator that mounts `[data-monitoring-tab]`, drives
  loading/empty/error states, live SSE updates with a polling fallback, an
  oldest/newest order toggle, and relative-timestamp refresh.
- `src/monitor/styles.css` — feed styles (severity/parse badges, states, layout).
- `src/monitor/*.test.ts` — colocated coverage for the feed, render, and
  transport layers.
- `index.html` — host page containing the monitoring tab (auto-mounts).
- `scripts/build.mjs` — bundles the UI into `dist/` for static hosting.
- `scripts/serve.mjs` — dev server that serves `dist/` and proxies `/api/*`
  (including SSE) to the controller API so the page runs same-origin.

## Usage

```ts
import {
  normalizeMonitorEventsResponse,
  orderEvents,
  formatEventTitle,
  formatEventMessage,
  formatTimestamp,
  getSeverityInfo,
  summarizePayload,
} from '@agentcontroller/monitoring-client';

const response = normalizeMonitorEventsResponse(await res.json());
const newestFirst = orderEvents(response.runtimeEvents.events, 'newest-first');

for (const event of newestFirst) {
  const severity = getSeverityInfo(event.severity); // { label, className, rank }
  const ts = formatTimestamp(event.occurredAt);     // { absolute, relative }
  const summary = summarizePayload(event.payload);
  console.log(formatEventTitle(event), formatEventMessage(event), ts.relative, summary);
}
```

All helpers are pure and deterministic (timestamps take an optional `now`) and
never throw on unknown or malformed input — they fall back to stable labels so
the UI can always render something.

## Monitoring tab UI

The monitoring tab renders the runtime event stream from local sync as a
readable progress feed:

- **Ordering**: newest-first by default (toggle to oldest-first in the header).
- **Badges**: severity (`Info`/`Warning`/`Error`/`Critical`) and event-type
  label, plus a parse-status badge for malformed entries so they stay visible.
- **Timestamps**: relative label ("5m ago") with an absolute tooltip.
- **Message + payload summary** per event.
- **States**: loading, empty (no event stream yet), and error — the feed never
  goes blank and keeps the last good view on transient failures.
- **Live updates**: subscribes to the SSE channel
  (`/api/monitor/events?runId=…&stream=true`) and falls back to polling when
  `EventSource` is unavailable. Re-renders only when content actually changes.

### Run it

```sh
bun run dev   # build the UI, then serve dist/ at http://localhost:5173
              # (proxies /api/* to $AC_API_BASE, default http://localhost:5000)
```

Open `http://localhost:5173/?runId=<run-id>` to follow a run. The tab reads
`runId` from `?runId=` or a `data-run-id` attribute, and optionally
`data-api-base` (default: same-origin) and `data-cap`.

### Host page snippet

```html
<section data-monitoring-tab data-run-id="run_1" data-cap="200"></section>
<script type="module" src="./main.js"></script>
```

`main.ts` auto-mounts every `[data-monitoring-tab]` on the page. Build the
bundle (`bun run build`) to produce a self-contained `dist/` that can be served
from the controller API origin (recommended, so SSE/local-sync works
same-origin without CORS).

## Scripts

```sh
bun test            # run colocated tests
bun run typecheck   # tsc --noEmit
bun run build       # bundle the monitoring UI into dist/
bun run serve       # serve dist/ + proxy /api/* to $AC_API_BASE
bun run dev         # build, then serve
```
