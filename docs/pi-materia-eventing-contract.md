# Pi-Materia Stdout Eventing Contract

This document describes the stdout event contract between the **pi-materia emitter** (TypeScript, running inside `pi --mode rpc`) and the **agent-controller parser** (`PiMateriaRuntime`, C#).

This is the contract for the **stdout JSONL stream** — not the HTTP webhook channel (see [Runtime Event Contract](./runtime-events.md) for the `runtime.*` webhook events). The stdout stream carries diagnostic and lifecycle events from pi; the webhook carries the authoritative runtime lifecycle state.

---

## 1. Single-Source Schema

The authoritative definition of every recognized stdout event type, its fields, and its terminal/intermediate classification lives in:

- **`src/AgentController.Domain/PiMateriaStdoutEventTypes.cs`** — constant strings, `AllRecognizedTypes`, `TerminalTypes`, `IntermediateTypes` sets, and `TelemetryIgnoreList`.
- **`src/AgentController.Domain/PiMateriaStdoutEventContract`** (same file) — full schema with field descriptors, `ContractVersion`, and `Validate()` consistency checks.

Both the pi-materia emitter and the `PiMateriaRuntime` parser reference these definitions. Conformance tests in `PiMateriaStdoutEventContractTests` assert no drift between the schema and the implementation.

**Contract version:** 1 (see `PiMateriaStdoutEventTypes.ContractVersion`). Increment when new types are added, fields change, or semantics are modified.

---

## 2. Canonical Event Types

The following table lists all recognized stdout event types. Every event emitted by pi on the stdout JSONL stream must have a `type` field matching one of these strings.

| Type | Category | Terminal? | Description |
|------|----------|-----------|-------------|
| `response` | rpc | No | RPC command response — signals a command (e.g. `prompt`) was accepted or rejected. |
| `extension_error` | rpc | No | pi-materia extension error — a materialization error from a pi extension. |
| `cast_start` | cast | No | Cast initialization event. Contains resolved socket metadata, `castId`, and the active `eventing` preset. |
| `cast_end` | cast | **Yes** | Cast completion event. Emitted when all sockets in the pipeline have completed. Terminal signal — confirms whole-cast completion. |
| `agent_end` | agent | **No** | Agent socket completed its turn. **Per-socket** completion signal — does NOT terminate the cast. In multi-socket casts this fires once per socket. |
| `materia_start` | materia | No | Materia socket started execution. Contains the materia name and socket identifier. |
| `materia_end` | materia | No | Materia socket completed execution. Contains the materia name and socket identifier. |

### 2.1 Terminal vs Intermediate

- **Terminal** events signal the agent's work is complete. The runtime uses these to initiate graceful shutdown. Currently, only `cast_end` is terminal.
- **Intermediate** events are recognized and processed (logged, metadata extracted, socket-local status updated) but do not signal completion. `agent_end` is intermediate because it fires per-socket in multi-socket casts.

The partition is exhaustive: every recognized type is in exactly one of `TerminalTypes` or `IntermediateTypes`. The `Validate()` method enforces this at runtime.

### 2.2 Field Contracts

Each event type has a defined set of fields. The schema in `PiMateriaStdoutEventContract.Schema` specifies for each field: its name, JSON type, whether it is required, and a human-readable description. For example:

**`cast_start`** (required fields):
- `type` — always `"cast_start"`
- `castId` — unique identifier for this cast
- `eventing` — object with `preset` field (e.g. `"agent-controller"`, `"interactive"`)
- `sockets` — array of resolved socket objects, each with `socketName`, `type`, `materiaName`, `multiTurn`

**`cast_end`** (required fields):
- `type` — always `"cast_end"`
- `castId` — the castId from the corresponding `cast_start` event

**`agent_end`** (required fields):
- `type` — always `"agent_end"`
- `messages` — array of message objects produced during the agent turn

See `PiMateriaStdoutEventContract.Schema` for the complete field definitions for all types.

---

## 3. Multi-Socket Terminal Model

> **Reference:** `fix(pi-materia-runtime): treat agent_end as non-terminal for the cast lifecycle`
> **Reference:** `feat(pi-materia-runtime): use cast_end stdout event as multi-socket completion signal`

### 3.1 The Problem

In multi-socket casts (e.g. Interactive-Plani → Builda → Evala), each socket runs its own materia pipeline. The original runtime treated `agent_end` as a terminal signal, causing mid-flight abort when the first socket's `agent_end` arrived — orphaning downstream sockets in an `AwaitingResult` state.

### 3.2 The Corrected Model

The multi-socket terminal model uses **three distinct signals** at different scopes:

| Signal | Scope | Terminal? | Role |
|--------|-------|-----------|------|
| `agent_end` | Per-socket | **No** | A single socket's materia finished. The runtime updates socket-local status, emits a `runtime.status` "Socket Socket-N completed" event, and **continues** waiting for other sockets. |
| `cast_end` | Whole-cast | **Yes** | All sockets in the cast have completed. The runtime treats this as a strong terminal signal that initiates process shutdown. |
| `runtime.completed` (webhook) | Controller-confirmed | **Yes** | The authoritative terminal signal. The controller's state machine is driven by this webhook event. Combined with `cast_end`, it confirms whole-cast completion. |

### 3.3 Lifecycle Sequence (Multi-Socket Cast)

A typical multi-socket cast lifecycle:

```
cast_start (sockets: [Interactive-Plani, Builda, Auto-Evala])
  → materia_start (Interactive-Plani)
  → materia_end (Interactive-Plani)
  → agent_end (Socket-1: Interactive-Plani completed)    ← non-terminal, runtime stays alive
  → materia_start (Builda)
  → materia_end (Builda)
  → agent_end (Socket-2: Builda completed)               ← non-terminal, runtime stays alive
  → materia_start (Auto-Evala)
  → materia_end (Auto-Evala)
  → agent_end (Socket-3: Auto-Evala completed)           ← non-terminal, runtime stays alive
  → cast_end (all sockets done)                          ← terminal, runtime initiates shutdown
  → runtime.completed (webhook, controller-confirmed)    ← authoritative terminal
```

### 3.4 Socket Completion Tracking

When `agent_end` is received, the runtime:

1. Parses the event and extracts socket identification metadata.
2. Records the socket in `ActiveProcess.CompletedSockets`.
3. Emits a `runtime.status` event with message "Socket {socketName} completed".
4. **Does NOT** transition the run to a terminal state.
5. **Does NOT** stop the runtime.
6. **Does NOT** send `runtime.completed`.

The runtime continues monitoring stdout for subsequent socket completions and the final `cast_end`.

### 3.5 Implementation

- **Detection**: `PiMateriaRuntime.InterpretStdoutLine()` routes `agent_end` to per-socket completion handling and `cast_end` to terminal signal handling.
- **Terminal set**: `PiMateriaStdoutEventTypes.TerminalTypes` contains only `cast_end`. `agent_end` is in `IntermediateTypes`.
- **Tests**: `PiMateriaRuntimeTests.StartAsync_MultiSocketAgentEnd_EmitsSocketCompletionStatusEvents` and `StartAsync_AgentEndNonTerminal_RunCompletesWithoutStall` validate the non-terminal behavior.

---

## 4. Keepalive-Stall Detection

> **Reference:** `fix(pi-materia-runtime): add keepalive-stall detection that fails orphaned AwaitingResult runs`

### 4.1 Purpose

Detect runs that die without reaching a valid terminal state (orphaned `AwaitingResult` after a mid-flight abort, environment unreachable, process crash, etc.) and fail them explicitly rather than hanging indefinitely.

### 4.2 Design

The keepalive-stall detector:

1. **Tracks** the timestamp of the last observed runtime event (any event: stdout line, synthetic heartbeat, socket completion, `agent_end`, etc.).
2. **Computes** a stall deadline as `max(KeepaliveStallSeconds, HeartbeatIntervalSeconds × 3)`.
3. **Checks** on each polling cycle whether the deadline has elapsed without any runtime event.
4. **Fails** the run with a retryable `runtime.failed_retryable` event when the deadline elapses.

### 4.3 Configuration

| Setting | Source | Default | Description |
|---------|--------|---------|-------------|
| `KeepaliveStallSeconds` | `RuntimeOptions` | 90 | Base stall threshold in seconds. |
| `HeartbeatIntervalSeconds` | `RuntimeOptions` | 30 | Synthetic heartbeat interval (mirrors controller config's `HeartbeatIntervalMs`). |
| Effective deadline | Computed | `max(90, 30 × 3) = 90` | Self-scaling: if heartbeat interval is larger, the deadline scales up. |

### 4.4 Failure Classification

When keepalive-stall triggers:

- **Event type**: `runtime.failed_retryable` (not `runtime.failed`).
- **Failure reason**: `keepalive_stall` (from `RetryableFailureReasons.KeepaliveStall`).
- **State**: Transitions to a retryable `Failed` state.
- **Payload**: Includes last-event age, expected heartbeat interval, and castId for diagnosis.
- **Controller action**: The `PollingWorker` evaluates the run-level retry threshold (see §6) before escalating.

### 4.5 Distinction from StaleTimeout

| Feature | Keepalive-Stall | StaleTimeout |
|---------|-----------------|--------------|
| Trigger | No runtime event for `max(KeepaliveStallSeconds, HeartbeatIntervalSeconds × 3)` | Run in `AwaitingResult` for `StaleTimeoutSeconds` (default 30 min) |
| Failure type | Retryable (`runtime.failed_retryable`) | Non-retryable — goes directly to `NeedsHuman` |
| Controller action | Evaluate retry threshold, potentially start fresh run | Escalate to `NeedsHuman` immediately |
| Config | `RuntimeOptions.KeepaliveStallSeconds` | `AgentControllerOptions.StaleTimeoutSeconds` |

The StaleTimeout path continues to escalate directly to `NeedsHuman` and is **not** retried. This invariant is preserved.

### 4.6 Known Gap: Periodic Heartbeats from Pi-Materia

The stall detector keys off **any** runtime event (stdout lines, synthetic heartbeats, socket completions), so it works even if pi-materia is not yet emitting periodic 30s heartbeats. However, the absence of periodic heartbeats from pi-materia itself is a possible pi-materia-side gap that should be addressed in future work. The synthetic heartbeat safety net in `PiMateriaRuntime` compensates for this in the interim.

### 4.7 Implementation

- **Location**: `PiMateriaRuntime.WaitForTerminalOrExitAsync()` — the monitoring loop checks `_activeProcess.LastEventAt` against the computed deadline.
- **Synthetic heartbeat**: When `DisableSyntheticHeartbeat` is false (default), the runtime emits synthetic `runtime.heartbeat` events at `HeartbeatIntervalSeconds` intervals, which reset the stall clock.
- **Tests**: `PiMateriaRuntimeTests.StartAsync_KeepaliveStallDetected_RunFailsWithRetryableError` and `StartAsync_HealthyProgression_SyntheticHeartbeatPreventsStall`.

---

## 5. Telemetry-Only Pi-Core Event Ignore-List

> **Reference:** `fix(pi-materia-stdout-contract): add telemetry-only pi-core event types to an explicit ignore-list`

### 5.1 Purpose

Pi-core emits internal telemetry events on stdout that carry no cast-lifecycle meaning. Previously, the runtime parser treated unknown event types as contract drift and failed the cast (in autonomous mode). The ignore-list prevents these telemetry events from causing false failures.

### 5.2 Current Ignore-List Entries

The following 10 pi-core telemetry event types are silently ignored:

| Type | Description |
|------|-------------|
| `extension_ui_request` | pi-core UI extension request (setWidget, notify, setStatus, etc.) |
| `session_info_changed` | pi-core session info changed (session name update, etc.) |
| `message_start` | pi-core message lifecycle: message start (telemetry for message framing) |
| `message_end` | pi-core message lifecycle: message end (telemetry for message framing) |
| `message_update` | pi-core message update (thinking_delta, content_delta, etc.) |
| `agent_start` | pi-core agent lifecycle: agent start (telemetry for agent turn tracking) |
| `turn_start` | pi-core turn lifecycle: turn start (telemetry for turn tracking) |
| `turn_end` | pi-core turn lifecycle: turn end (telemetry for turn tracking) |
| `tool_execution_start` | pi-core tool execution: tool call start (telemetry for tool invocation tracking) |
| `tool_execution_end` | pi-core tool execution: tool call end (telemetry for tool result tracking) |

### 5.3 Invariants

- The ignore list must **NOT** contain any real lifecycle event types (`cast_start`, `cast_end`, `agent_end`, `materia_start`, `materia_end`, `response`, `extension_error`).
- Telemetry events are logged at **debug severity** only via `Log.PiStdoutIgnoredTelemetry()`.
- Telemetry events are **NOT** part of `AllRecognizedTypes` or the lifecycle schema.

### 5.4 Adding New Telemetry Event Types

To add a new telemetry-only event type to the ignore-list:

1. Add a `public const string` to `PiMateriaStdoutEventTypes` with XML docs describing the type (prefixed with `Telemetry` for clarity).
2. Add the type to `TelemetryIgnoreList`.
3. **Do NOT** add to `AllRecognizedTypes`, `TerminalTypes`, or `IntermediateTypes`.
4. **Do NOT** add a schema entry to `PiMateriaStdoutEventContract.Schema`.
5. Add a conformance test asserting the new type is in the ignore-list and not in `AllRecognizedTypes`.

### 5.5 Implementation

- **Location**: `PiMateriaRuntime.InterpretStdoutLine()` checks `PiMateriaStdoutEventTypes.TelemetryIgnoreList.Contains(type)` before the unrecognized-type check.
- **Logging**: `Log.PiStdoutIgnoredTelemetry()` at `LogLevel.Debug` with the event type and castId.
- **Tests**: `PiMateriaStdoutEventContractTests` asserts no overlap between `TelemetryIgnoreList` and `AllRecognizedTypes`. `PiMateriaRuntimeTests.StartAsync_TelemetryEventsIgnored_LifecycleEventsStillRoute` validates runtime behavior.

---

## 6. Controller Run-Level Retry

> **Reference:** `feat(agent-controller): run-level retry threshold for stalled/crashed runs`

### 6.1 Purpose

When a pi-materia run for a board story dies without reaching a valid terminal state, the controller kicks off a fresh run for the same ADO board story from scratch, up to a configurable attempt threshold.

### 6.2 Configuration

| Setting | Source | Default | Description |
|---------|--------|---------|-------------|
| `MaxRunAttempts` | `AgentControllerOptions` | 3 | Maximum number of run attempts for a single work item before escalating to `NeedsHuman`. |

### 6.3 Attempt Tracking

Each run tracks:

- **`RunAttempt`** (1-based integer, default 1): Which attempt this run is for the work item.
- **`PreviousRunId`**: The ID of the prior failed run (null for attempt 1).

These fields are stored in the `AgentRuns` table and carried through the domain model (`AgentRunHandle`, `CreateRunRequest`).

### 6.4 Retryable vs Non-Retryable Failures

| Failure Type | Retryable? | Controller Action |
|--------------|------------|-------------------|
| `keepalive_stall` | **Yes** | Increment attempt counter, start fresh run if below `MaxRunAttempts` |
| `process_exit_nonzero` | **Yes** | Increment attempt counter, start fresh run if below `MaxRunAttempts` |
| `process_start_failed` | **Yes** | Increment attempt counter, start fresh run if below `MaxRunAttempts` |
| `environment_unreachable` | **Yes** | Increment attempt counter, start fresh run if below `MaxRunAttempts` |
| `StaleTimeout` (stale recovery) | **No** | Escalate directly to `NeedsHuman` (invariant preserved) |
| Other `runtime.failed` | **No** | Escalate directly to `NeedsHuman` |

Retryable failures produce `runtime.failed_retryable` events with a `reason` from `RetryableFailureReasons.AllRetryableReasons`. Non-retryable failures produce `runtime.failed` events.

### 6.5 Retry Flow

```
Run attempt N fails with retryable error
  → PollingWorker detects retryable failure
  → RunLifecycleService.EvaluateRetryAsync() checks:
    → If N < MaxRunAttempts:
      → Create new run with RunAttempt = N+1, PreviousRunId = failed run
      → Emit controller.retry_run_created event
      → New run starts from scratch
    → If N >= MaxRunAttempts:
      → EscalateToNeedsHumanAsync() walks retry chain
      → Produces failure summary: "Attempt 1: ..., Attempt 2: ..., Attempt 3: ..."
      → Transitions work item to NeedsHuman
      → Emit controller.retry_exhausted event
```

### 6.6 Distinction from Pi-Materia Internal Retry

This is **run-level (story-level) retry** — the controller restarts the entire pi-materia cast from scratch. This is explicitly separate from pi-materia's internal `workItem` retry (sub-items it plans and iterates within a single cast), which remains untouched.

### 6.7 Implementation

- **Domain**: `AgentRunHandle.RunAttempt`, `CreateRunRequest.RunAttempt`, `RuntimeFieldUpdate.RunAttempt`.
- **Application**: `RunLifecycleService.EvaluateRetryAsync()`, `RecoverStaleRunWithRetryAsync()`, `EscalateToNeedsHumanAsync()`.
- **API**: `PollingWorker` invokes retry evaluation on retryable failures.
- **Infrastructure**: `PiMateriaRuntime` synthesizes `runtime.failed_retryable` events for keepalive-stall and non-zero process exit without terminal event.
- **Tests**: Cover retry-counter increment, fresh-run kickoff, threshold escalation, and the StaleTimeout-still-goes-to-NeedsHuman invariant.

---

## 7. Config Contract: `materia-controller.json`

> **Reference:** `fix(pi-materia-runtime): derive autonomous-mode from controller-owned config, not cast_start`

### 7.1 Overview

The controller writes `materia-controller.json` with controller-owned configuration fields. The runtime reads this config at startup/initialization rather than inferring behavior from stdout events (which removes a startup-ordering race).

### 7.2 Fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `AutonomousMode` | `boolean` | `false` | Whether the cast runs in autonomous (agent-controller) mode. Drives fail-fast guards for multiTurn agent sockets and unrecognized event types. |
| `HeartbeatIntervalMs` | `integer` | `30000` | Heartbeat interval in milliseconds. Mirrored to `RuntimeOptions.HeartbeatIntervalSeconds` (divided by 1000). Used by keepalive-stall detector. |
| `KeepaliveStallSeconds` | `integer` | `90` | Keepalive-stall threshold in seconds. Used directly by the stall detector. |
| `MaxRunAttempts` | `integer` | `3` | Maximum run attempts before escalation. Used by the controller's retry logic. |

### 7.3 Autonomous-Mode Derivation

The runtime reads `AutonomousMode` from `materia-controller.json` at startup via `ReadControllerConfigAutonomousMode()`. This replaces the previous behavior of inferring autonomous mode from whether `cast_start` was emitted on stdout.

**Config load failures** surface a clear error via `Log.ConfigMissingAutonomousMode()` rather than silently aborting. If the config file is missing or malformed, the runtime defaults to non-autonomous mode and logs a warning.

### 7.4 Fail-Fast Guards (Autonomous Mode Only)

When `IsAutonomous` is true:

1. **MultiTurn agent socket guard**: If `cast_start` contains any agent-type socket with `multiTurn: true`, the cast is aborted immediately with `runtime.failed` (see §8).
2. **Unrecognized event type guard**: If an unknown event type is encountered (not in `AllRecognizedTypes` or `TelemetryIgnoreList`), the cast is failed with `runtime.failed` (see §9).

In non-autonomous mode (interactive/CLI), these guards produce warnings instead of failures.

### 7.5 Implementation

- **Config writing**: `PiMateriaRuntime` writes `materia-controller.json` with `AutonomousMode: true` for agent-controller eventing preset.
- **Config reading**: `ReadControllerConfigAutonomousMode()` parses the JSON and extracts `AutonomousMode`.
- **Tests**: Cover config-driven autonomous-mode, missing-config error paths, and the multiTurn/unrecognized-type guards.

---

## 8. MultiTurn Agent Socket Fail-Fast Rule

### 8.1 The Rule

Under the **agent-controller** eventing preset (i.e., `IsAutonomous` is true), if the `cast_start` event contains any agent-type socket with `multiTurn: true`, the cast is **aborted immediately** with a `runtime.failed` event.

### 8.2 Why This Exists

The agent-controller sends exactly **one** `/materia cast <task>` prompt and **never** sends `/materia continue`. A multiTurn agent socket requires the client to send `continue` commands to advance through its turns. Under agent-controller eventing, those commands never arrive, so the agent stalls forever — a guaranteed token sink.

This rule is a **fail-fast guard**: it detects the misconfiguration at cast start (before any agent work begins) and fails the run with a clear error message naming the offending socket(s).

### 8.3 Interactive/CLI Eventing Is Unaffected

Under interactive or CLI eventing presets, a human can provide `/materia continue` commands. MultiTurn agent sockets are valid and allowed in those contexts. The fail-fast check is active only when `IsAutonomous` is true.

### 8.4 Implementation

- **Detection**: `PiMateriaRuntime.InterpretStdoutLine()` parses the `cast_start` event and extracts multiTurn agent socket names via `ExtractMultiTurnAgentSockets()`.
- **Enforcement**: `PiMateriaRuntime.WaitForTerminalOrExitAsync()` checks `active.IsAutonomous && active.HasMultiTurnAgentSockets` and calls `SynthesizeFailureAsync()` if both are true.
- **Tests**: `PiMateriaRuntimeTests.cs` covers all four scenarios: single-turn allowed, multiTurn under agent-controller aborts, multiTurn under interactive allowed, and failure artifact contains socket metadata.

---

## 9. Fail-Closed on Unrecognized Event Types

### 9.1 The Behavior

When `PiMateriaRuntime` encounters a stdout event with a `type` not in `AllRecognizedTypes` and not in `TelemetryIgnoreList`:

| Mode | Behavior |
|------|----------|
| Autonomous (`IsAutonomous = true`) | **Fail the cast** with a `runtime.failed` event. The error message includes the unrecognized type string, `castId`, and resolved materia name. |
| Interactive / CLI (`IsAutonomous = false`) | **Warn and continue**. A warning is logged via `Log.PiStdoutUnknownType()` with diagnostic context. Execution continues so a human can intervene. |

### 9.2 Why This Exists

This is a **defense-in-depth guard** against contract drift between the pi-materia emitter and the agent-controller parser. Even if a new event type slips past the conformance tests, an autonomous run will **fail closed** instead of stalling silently and burning tokens.

### 9.3 Defense-in-Depth

The unrecognized-type check runs in two places:

1. **Monitor loop** (`WaitForTerminalOrExitAsync`): checked every polling interval while the process is alive.
2. **Process exit handler** (`HandleProcessExitAsync`): if the process exits and an unrecognized type was seen during the run, the exit handler fails the run instead of synthesizing a completion from the exit code.

### 9.4 Implementation

- **Tracking**: `ActiveProcess.SetUnrecognizedType()` records the first unrecognized type seen.
- **Diagnostic enrichment**: `castId` is extracted from `cast_start`; `materiaName` is tracked from `materia_start` events.
- **Tests**: `PiMateriaRuntimeTests.cs` covers both the autonomous fail-closed path and the interactive warn-continue path.

---

## 10. Relationship to the HTTP Webhook Channel

The stdout event stream and the HTTP webhook channel serve different purposes:

| Channel | Purpose | Authoritative? |
|---------|---------|----------------|
| **stdout JSONL** | Diagnostic events, prompt acceptance, stall prevention (`cast_end`, multiTurn detection, contract drift, socket completion) | No — used for runtime internals only |
| **HTTP webhook** (`POST /runs/{runId}/events`) | Runtime lifecycle state transitions (`runtime.accepted`, `runtime.completed`, etc.) | **Yes** — the controller's state machine is driven by webhook events |

The stdout `cast_end` event triggers graceful shutdown of the pi process, but the authoritative terminal state comes from the webhook's `runtime.completed` or `runtime.failed` event. If the webhook is delayed, the process exit handler synthesizes a final event from the exit code.

See [Runtime Event Contract](./runtime-events.md) for the full webhook event specification.

---

## 11. Related Source Files

| File | Role |
|------|------|
| `src/AgentController.Domain/PiMateriaStdoutEventTypes.cs` | Single-source event type constants, schema, telemetry ignore-list |
| `src/AgentController.Domain/EventTypes.cs` | `RuntimeEventTypes.FailedRetryable`, `RetryableFailureReasons` |
| `src/AgentController.Domain/AgentRuns.cs` | `AgentRunHandle.RunAttempt`, `CreateRunRequest.RunAttempt` |
| `src/AgentController.Infrastructure/PiMateriaRuntime.cs` | Parser, runtime enforcement, keepalive-stall detector, config reading |
| `src/AgentController.Infrastructure/Options/RuntimeOptions.cs` | `KeepaliveStallSeconds`, `HeartbeatIntervalSeconds` |
| `src/AgentController.Infrastructure/Options/AgentControllerOptions.cs` | `MaxRunAttempts`, `StaleTimeoutSeconds` |
| `src/AgentController.Application/Services/RunLifecycleService.cs` | Retry evaluation, stale recovery, escalation logic |
| `src/AgentController.Api/PollingWorker.cs` | Retry trigger on retryable failures |
| `tests/AgentController.Infrastructure.Tests/PiMateriaRuntimeTests.cs` | Integration tests for stdout event handling, stall detection, multi-socket pipeline |
| `tests/AgentController.Domain.Tests/PiMateriaStdoutEventContractTests.cs` | Conformance tests for the schema and ignore-list invariants |
| [Runtime Event Contract](./runtime-events.md) | HTTP webhook event specification |
| `docs/investigations/socket-3-materia-divergence.md` | Root cause analysis of the Biggs loadout multiTurn misconfiguration |

---

## 12. Adding a New Event Type

To add a new stdout event type:

1. Add a `public const string` to `PiMateriaStdoutEventTypes` with XML docs describing the type and its terminal/intermediate classification.
2. Add the type to the appropriate set (`TerminalTypes` or `IntermediateTypes`).
3. Add a `StdoutEventSchemaEntry` to `PiMateriaStdoutEventContract.Schema` with full field descriptors.
4. Handle the type in `PiMateriaRuntime.InterpretStdoutLine()` with appropriate logic.
5. Add tests: conformance (schema consistency) and integration (runtime behavior).
6. If semantics change or types are added, increment `ContractVersion`.

To add a new **telemetry-only** event type (see §5):

1. Add a `public const string` to `PiMateriaStdoutEventTypes` (prefixed with `Telemetry` for clarity).
2. Add the type to `TelemetryIgnoreList`.
3. **Do NOT** add to `AllRecognizedTypes`, `TerminalTypes`, `IntermediateTypes`, or the schema.
4. Add a conformance test asserting no overlap with `AllRecognizedTypes`.

Do not add types to the emitter without updating the parser — the conformance tests will catch the drift.

---

## 13. Work Item References

This document was updated as part of the following work items:

1. `fix(pi-materia-runtime): treat agent_end as non-terminal for the cast lifecycle` — Made `agent_end` per-socket and non-terminal; added socket completion tracking and status events.
2. `fix(pi-materia-runtime): derive autonomous-mode from controller-owned config, not cast_start` — Decoupled autonomous-mode from `cast_start`; added config-driven `IsAutonomous` with fail-fast guards.
3. `fix(pi-materia-runtime): add keepalive-stall detection that fails orphaned AwaitingResult runs` — Added stall detector with `max(KeepaliveStallSeconds, HeartbeatIntervalSeconds × 3)` deadline and retryable failure classification.
4. `feat(agent-controller): run-level retry threshold for stalled/crashed runs` — Added run-level retry with `MaxRunAttempts` threshold, attempt counter, and escalation to `NeedsHuman`.
5. `fix(pi-materia-stdout-contract): add telemetry-only pi-core event types to an explicit ignore-list` — Added `TelemetryIgnoreList` with 10 pi-core telemetry types; silent ignore at debug severity.
6. `feat(pi-materia-runtime): use cast_end stdout event as multi-socket completion signal` — Added `cast_end` as the authoritative whole-cast terminal signal; moved it from intermediate to terminal.
7. `test(pi-materia-runtime): multi-socket pipeline reaches runtime.completed without mid-flight abort` — Added end-to-end integration tests for the multi-socket lifecycle and orphaned AwaitingResult regression.
8. `docs(pi-materia-eventing-contract): document multi-socket terminal model, keepalive stall, ignore-list, and retry` — This document.
