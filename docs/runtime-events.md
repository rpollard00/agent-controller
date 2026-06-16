# Runtime Event Contract

**Status**: Implemented (Phase 1)
**Endpoint**: `POST /runs/{runId}/events`

This document describes the runtime event contract as implemented in Phase 1. Agent
runtimes (e.g., `pi-materia`) emit events to this endpoint to report progress and
outcomes back to the controller. The controller ingests events, enforces state
transitions, records lifecycle entries, and projects status to work items.

---

## 1. Endpoint

```
POST /runs/{runId}/events
Content-Type: application/json
```

- `{runId}` is the controller-assigned run identifier (returned when the controller
  creates a run for a claimed work item).
- The request body uses a common envelope (see §2).

---

## 2. Request Fields

### 2.1 Required Fields

| Field | Type | Description |
|-------|------|-------------|
| `eventId` | `string` | Stable unique identifier for this event. Serves as the **idempotency key**. Must be non-empty. |
| `eventType` | `string` | The event type. Must be a supported `runtime.*` value (see §4). |

The route parameter `{runId}` is authoritative. The body may also include a `runId`
field; when provided, it **must match** the route parameter exactly.

### 2.2 Defaulted Fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `occurredAt` | `string` (ISO 8601) | `now` (UTC) | When the event occurred. Maximum allowed future skew: 5 minutes. |
| `severity` | `string` | `"info"` | Event severity. One of: `info`, `warning`, `error`, `critical`. |
| `message` | `string` | `null` | Human-readable status or summary message. |
| `payload` | `object` | `null` | Type-specific key/value map. Content depends on `eventType` (see §5). |

### 2.3 Optional Fields

| Field | Type | Description |
|-------|------|-------------|
| `runId` | `string` | Controller run identifier. If provided, must match the route `{runId}` exactly. |
| `runtimeRunId` | `string` | Runtime-assigned run identifier (e.g., `pi-materia` process ID). |
| `sequence` | `int` | Monotonically increasing sequence number for this run. |

---

## 3. Field Validation (API Layer)

The API performs these validation checks before delegating to the service layer:

1. **`eventType`** — must be non-empty. Returns `400 Bad Request`.
2. **`eventId`** — must be non-empty. Returns `400 Bad Request`.
3. **`runId`** (when provided in body) — must match the route `{runId}`. Returns `400 Bad Request` with route vs. body details.
4. **`severity`** — if provided, must be a defined `EventSeverity` enum value. Out-of-range values (e.g., `99`) return `400 Bad Request`.
5. **`occurredAt`** — if provided, must not be more than 5 minutes in the future. Returns `400 Bad Request`.

The service layer additionally validates severity before idempotency/persistence (see §7.5).

---

## 4. Supported Event Types

All event types use the `runtime.` prefix.

### 4.1 `runtime.accepted`

The runtime accepted the run and has started work.

- **State effect**: Transitions to `AgentRunning` only if the run is prior to
  `AgentRunning`. Rejected if the run has already progressed beyond `AgentRunning`
  (regression prevention).
- **Runtime fields updated**: `RuntimeRunId`, `LastHeartbeatAt`, `StartedAt` (if not already set).
- **Work item status**: `Running`.

### 4.2 `runtime.heartbeat`

The runtime is still alive.

- **State effect**: None (no state transition).
- **Runtime fields updated**: `LastHeartbeatAt`.
- **Work item status**: Unchanged.

### 4.3 `runtime.status`

Human-readable status update.

- **State effect**: None (no state transition).
- **Runtime fields updated**: `RuntimeRunId` (if provided in the event).
- **Work item status**: Unchanged.

### 4.4 `runtime.branch_created`

The runtime created or selected a branch.

- **State effect**: None (informational only).
- **Runtime fields updated**: `RuntimeRunId`, `BranchName`, `LastHeartbeatAt`.

### 4.5 `runtime.pr_created`

The runtime opened a pull request.

- **State effect**: None (informational only).
- **Runtime fields updated**: `RuntimeRunId`, `BranchName`, `PullRequestUrl`, `LastHeartbeatAt`.

### 4.6 `runtime.needs_human`

The runtime cannot proceed without human input or review.

- **State effect**: Transitions to `NeedsHuman`.
- **Runtime fields updated**: `RuntimeRunId`, `ResultSummary` (from `message`), `LastHeartbeatAt`, `FinishedAt`.
- **Work item status**: `NeedsHuman`.

### 4.7 `runtime.completed`

The runtime completed its work.

- **State effect**: Transitions based on `payload.outcome`:
  - `pull_request_opened` → `PrOpened`
  - `branch_pushed` → `BranchPushed`
  - `patch_created` → `Completed`
  - `no_changes_needed` → `Completed`
  - `needs_human` → `NeedsHuman`
  - `failed` → `Failed`
- **Runtime fields updated**: `RuntimeRunId`, `BranchName`, `PullRequestUrl`, `ResultSummary`, `LastHeartbeatAt`, `FinishedAt`.
- **Work item status**: Matches the resolved state.
- **Unsupported outcomes**: Return `422 Unprocessable Entity`.

### 4.8 `runtime.failed`

The runtime failed.

- **State effect**: Transitions to `Failed`.
- **Runtime fields updated**: `RuntimeRunId`, `Error`, `ResultSummary`, `LastHeartbeatAt`, `FinishedAt`.
- **Work item status**: `Failed`.

### 4.9 `runtime.cancelled`

The runtime acknowledged cancellation.

- **State effect**: Transitions to `Cancelled`.
- **Runtime fields updated**: `RuntimeRunId`, `LastHeartbeatAt`, `FinishedAt`.
- **Work item status**: `Cancelled`.

---

## 5. Completion Outcomes

Reported in `payload.outcome` of a `runtime.completed` event.

| Outcome | Controller State |
|---------|-----------------|
| `pull_request_opened` | `PrOpened` |
| `branch_pushed` | `BranchPushed` |
| `patch_created` | `Completed` |
| `no_changes_needed` | `Completed` |
| `needs_human` | `NeedsHuman` |
| `failed` | `Failed` |

Any other value results in a `422 Unprocessable Entity` error listing the supported
outcomes.

---

## 6. Idempotency

Every runtime event must include a unique `eventId`. The controller enforces:

1. **Before processing**: The service checks `ILifecycleEventStore.ExistsByEventIdAsync`
   for the given `(runId, eventId)` pair.
2. **Duplicate event**: If the `eventId` has already been processed for that run, the
   controller returns `409 Conflict` with:
   ```json
   {
     "error": "Runtime event '<eventId>' has already been processed for run '<runId>'.",
     "runId": "run_123",
     "eventId": "evt_001"
   }
   ```
3. **Idempotent replay is safe**: Resending the same `eventId` produces a deterministic
   `409` without side effects.

The `LifecycleEvents` table enforces uniqueness at the database level with a unique
index on `(RunId, EventId) WHERE EventId IS NOT NULL`.

---

## 7. State Transitions and Enforcement

### 7.1 Legal Transitions

The controller validates that transitions follow the legal state graph. Controller-owned
states advance forward-only through:

```
Queued → Claimed → EnvironmentProvisioning → EnvironmentReady →
RepositoryCloning → RepositoryReady → ContextInjected → AgentStarting →
AgentRunning → AwaitingResult
```

Runtime events drive transitions from `AwaitingResult` to terminal/resolution states:

```
AwaitingResult → result_received → PrOpened | BranchPushed | Completed | NeedsHuman | Failed
PrOpened → Completed
BranchPushed → Completed
Completed → CleanupPending → CleanedUp
Failed → CleanupPending → CleanedUp
Cancelled → CleanupPending → CleanedUp
NeedsHuman → CleanupPending → CleanedUp
```

### 7.2 Terminal State Rejection

Events targeting a run in a terminal state (`Completed`, `Failed`, `Cancelled`,
`CleanedUp`) are rejected with `422 Unprocessable Entity`:

```json
{
  "error": "Cannot ingest runtime event for run '<runId>': run is in terminal state '<state>'.",
  "runId": "run_123",
  "eventId": "evt_001"
}
```

### 7.3 `runtime.accepted` Regression Prevention

`runtime.accepted` must never regress a run that has already progressed beyond
`AgentRunning`. If a run is already in `AwaitingResult` or any later state, the event
is rejected with `422 Unprocessable Entity`:

```json
{
  "error": "Cannot accept run '<runId>': run has already progressed to '<state>'. 'runtime.accepted' is only valid for runs prior to AgentRunning. Use 'runtime.heartbeat' or 'runtime.status' to report activity on active runs."
}
```

### 7.4 Unsupported Event Type

Unrecognized event types (anything not listed in §4) return `422 Unprocessable Entity`:

```json
{
  "error": "Unsupported runtime event type '<type>'."
}
```

### 7.5 Unsupported Severity

The service layer rejects undefined `EventSeverity` values before idempotency or
persistence checks (throwing `InvalidOperationException` for direct callers).
The API layer catches this earlier and returns `400 Bad Request`. An out-of-range
severity (e.g., `(EventSeverity)99`) produces:

```json
{
  "error": "Unsupported severity value 99. Valid values: Info, Warning, Error, Critical."
}
```

---

## 8. Example Payloads

### 8.1 Minimal Heartbeat

```json
POST /runs/run_abc123/events
{
  "eventId": "evt_hb_01",
  "eventType": "runtime.heartbeat"
}
```

Omits `runId` (route is authoritative), `occurredAt` (defaults to now), `severity`
(defaults to info).

### 8.2 Status Update with Phase Info

```json
POST /runs/run_abc123/events
{
  "eventId": "evt_status_01",
  "eventType": "runtime.status",
  "severity": "info",
  "message": "Running integration tests",
  "payload": {
    "phase": "validation",
    "testsPassed": 42,
    "testsFailed": 1
  }
}
```

### 8.3 Completion with PR Opened

```json
POST /runs/run_abc123/events
{
  "eventId": "evt_complete_01",
  "runId": "run_abc123",
  "eventType": "runtime.completed",
  "occurredAt": "2026-06-16T14:30:00Z",
  "severity": "info",
  "message": "All changes applied and PR opened",
  "payload": {
    "outcome": "pull_request_opened",
    "summary": "Implemented retry handling for transient 5xx responses.",
    "branchName": "agent/42-add-retry",
    "pullRequestUrl": "https://dev.azure.com/org/project/_git/repo/pullrequest/99"
  }
}
```

### 8.4 Completion with No Changes Needed

```json
POST /runs/run_abc123/events
{
  "eventId": "evt_complete_02",
  "eventType": "runtime.completed",
  "message": "No code changes required",
  "payload": {
    "outcome": "no_changes_needed",
    "summary": "Acceptance criteria already satisfied by current code."
  }
}
```

### 8.5 Runtime Failure

```json
POST /runs/run_abc123/events
{
  "eventId": "evt_fail_01",
  "eventType": "runtime.failed",
  "severity": "error",
  "message": "Tests failed after implementation",
  "payload": {
    "reason": "tests_failed",
    "summary": "Three retry tests failed due to timeout behavior.",
    "logPath": "logs/runtime.stdout.log"
  }
}
```

### 8.6 Needs Human Input

```json
POST /runs/run_abc123/events
{
  "eventId": "evt_human_01",
  "eventType": "runtime.needs_human",
  "severity": "warning",
  "message": "Ambiguous acceptance criteria",
  "payload": {
    "reason": "ambiguous_acceptance_criteria",
    "questions": [
      "Should 429 responses be retried?",
      "What timeout should be applied to retries?"
    ]
  }
}
```

---

## 9. Response Status Codes

| Status | When |
|--------|------|
| `200 OK` | Event ingested successfully. Body includes updated run state. |
| `400 Bad Request` | Missing required field (`eventId`, `eventType`), `runId` mismatch, out-of-range `severity`, or far-future `occurredAt`. |
| `409 Conflict` | Duplicate `eventId` — event was already processed. |
| `422 Unprocessable Entity` | Unsupported event type, unsupported completion outcome, run not found, run in terminal state, or `runtime.accepted` on a progressed run. |

### 9.1 Success Response (200)

```json
{
  "runId": "run_abc123",
  "status": "PrOpened",
  "runtimeRunId": "pi_456",
  "lastHeartbeatAt": "2026-06-16T14:30:00Z",
  "finishedAt": "2026-06-16T14:30:00Z",
  "resultSummary": "Implemented retry handling for transient 5xx responses.",
  "error": null,
  "eventId": "evt_complete_01",
  "message": "Runtime event 'runtime.completed' ingested successfully."
}
```

### 9.2 Validation Error (400)

```json
{
  "error": "Request body is missing required field 'eventType'."
}
```

### 9.3 RunId Mismatch (400)

```json
{
  "error": "RunId mismatch: route parameter 'run_abc123' does not match request body 'runId' value 'run_xyz'.",
  "routeRunId": "run_abc123",
  "bodyRunId": "run_xyz"
}
```

### 9.4 Duplicate Event (409)

```json
{
  "error": "Runtime event 'evt_001' has already been processed for run 'run_abc123'.",
  "runId": "run_abc123",
  "eventId": "evt_001"
}
```

### 9.5 Terminal State (422)

```json
{
  "error": "Cannot ingest runtime event for run 'run_abc123': run is in terminal state 'Failed'.",
  "runId": "run_abc123",
  "eventId": "evt_late"
}
```

### 9.6 Unsupported Event Type (422)

```json
{
  "error": "Unsupported runtime event type 'runtime.bogus_event'.",
  "runId": "run_abc123",
  "eventId": "evt_bad_type"
}
```

### 9.7 Unsupported Completion Outcome (422)

```json
{
  "error": "Unsupported completion outcome 'magical_solution'. Supported outcomes: pull_request_opened, branch_pushed, patch_created, no_changes_needed, needs_human, failed.",
  "runId": "run_abc123",
  "eventId": "evt_bad_outcome"
}
```

---

## 10. Minimal Required Contract

For the first usable prototype, a runtime only needs to support:

1. `runtime.accepted` — indicate that the run has started.
2. `runtime.heartbeat` — periodic liveness signals.
3. `runtime.status` — progress updates.
4. `runtime.completed` — final result with a supported `payload.outcome`.
5. `runtime.failed` — final result indicating failure.

The `runtime.completed` event should include enough context in its payload so that
separate `runtime.pr_created` and `runtime.branch_created` events are optional.
The `pullRequestUrl`, `branchName`, and `summary` fields in the `payload` of
`runtime.completed` carry the same information.

---

## 11. Design Principles

1. **Controller owns lifecycle state** — The controller database is authoritative.
   The runtime reports observations and outcomes.
2. **Idempotent events** — Every event has a stable `eventId`. Duplicates are
   rejected deterministically.
3. **No assumption of PR creation** — Completion outcomes include `no_changes_needed`,
   `needs_human`, and `failed` as valid alternatives.
4. **Explicit validation layers** — The API validates envelope/format; the service
   validates business rules and state transitions.
5. **Agent-friendly error messages** — All error responses include a human-readable
   `error` field and relevant diagnostic fields.
6. **Long-lived agent support** — The `runtime.heartbeat` event keeps runs from
   going stale while long-running agents make progress.

---

## 12. Related Documentation

- [Architecture Document](./arch.md) — §10 for contract design background, §13 (Phase 1) for implementation plan.
- [Development Guide](./development.md) — local setup, running tests, and migration workflow.
- `src/AgentController.Api/Models/RuntimeEventRequest.cs` — API request model.
- `src/AgentController.Domain/Events.cs` — `RuntimeEvent` domain record.
- `src/AgentController.Domain/EventTypes.cs` — `RuntimeEventTypes`, `CompletionOutcomes`, `ControllerEventTypes`.
- `src/AgentController.Application/Services/RunLifecycleService.cs` — ingestion and state machine logic.
