# @agentcontroller/monitoring-client

Client-side types and formatting helpers for the AgentController
monitoring/local sync runtime event feed. The Materia UI consumes this to render
the runtime event stream on the monitoring tab.

This package mirrors the server payload produced by `GET /api/monitor/events`
(see `src/AgentController.Api/Models/MonitorEventsResponse.cs` and
`src/AgentController.Domain/Monitoring.cs`) and adds the pure formatting helpers
the UI needs to turn that payload into a readable progress feed.

## Contents

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

## Scripts

```sh
bun test        # run colocated tests
bun run typecheck   # tsc --noEmit
```
