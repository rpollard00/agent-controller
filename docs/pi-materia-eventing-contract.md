# Pi-Materia Stdout Eventing Contract

This document describes the stdout event contract between the **pi-materia emitter** (TypeScript, running inside `pi --mode rpc`) and the **agent-controller parser** (`PiMateriaRuntime`, C#).

This is the contract for the **stdout JSONL stream** — not the HTTP webhook channel (see [Runtime Event Contract](./runtime-events.md) for the `runtime.*` webhook events). The stdout stream carries diagnostic and lifecycle events from pi; the webhook carries the authoritative runtime lifecycle state.

---

## 1. Single-Source Schema

The authoritative definition of every recognized stdout event type, its fields, and its terminal/intermediate classification lives in:

- **`src/AgentController.Domain/PiMateriaStdoutEventTypes.cs`** — constant strings, `AllRecognizedTypes`, `TerminalTypes`, `IntermediateTypes` sets.
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
| `cast_end` | cast | No | Cast completion event. Emitted when all sockets in the pipeline have completed. |
| `agent_end` | agent | **Yes** | Agent socket completed its turn. Under agent-controller eventing this signals the cast is done and the runtime should initiate graceful shutdown. |
| `materia_start` | materia | No | Materia socket started execution. Contains the materia name and socket identifier. |
| `materia_end` | materia | No | Materia socket completed execution. Contains the materia name and socket identifier. |

### 2.1 Terminal vs Intermediate

- **Terminal** events signal the agent's work is complete. The runtime uses these to initiate graceful shutdown. Currently, only `agent_end` is terminal.
- **Intermediate** events are recognized and processed (logged, metadata extracted) but do not signal completion.

The partition is exhaustive: every recognized type is in exactly one of `TerminalTypes` or `IntermediateTypes`. The `Validate()` method enforces this at runtime.

### 2.2 Field Contracts

Each event type has a defined set of fields. The schema in `PiMateriaStdoutEventContract.Schema` specifies for each field: its name, JSON type, whether it is required, and a human-readable description. For example:

**`cast_start`** (required fields):
- `type` — always `"cast_start"`
- `castId` — unique identifier for this cast
- `eventing` — object with `preset` field (e.g. `"agent-controller"`, `"interactive"`)
- `sockets` — array of resolved socket objects, each with `socketName`, `type`, `materiaName`, `multiTurn`

**`agent_end`** (required fields):
- `type` — always `"agent_end"`
- `messages` — array of message objects produced during the agent turn

See `PiMateriaStdoutEventContract.Schema` for the complete field definitions for all types.

---

## 3. MultiTurn Agent Socket Fail-Fast Rule

### 3.1 The Rule

Under the **agent-controller** eventing preset, if the `cast_start` event contains any agent-type socket with `multiTurn: true`, the cast is **aborted immediately** with a `runtime.failed` event.

### 3.2 Why This Exists

The agent-controller sends exactly **one** `/materia cast <task>` prompt and **never** sends `/materia continue`. A multiTurn agent socket requires the client to send `continue` commands to advance through its turns. Under agent-controller eventing, those commands never arrive, so the agent stalls forever — a guaranteed token sink.

This rule is a **fail-fast guard**: it detects the misconfiguration at cast start (before any agent work begins) and fails the run with a clear error message naming the offending socket(s).

### 3.3 Interactive/CLI Eventing Is Unaffected

Under interactive or CLI eventing presets, a human can provide `/materia continue` commands. MultiTurn agent sockets are valid and allowed in those contexts. The fail-fast check is **only** active when `eventing.preset` equals `"agent-controller"`.

### 3.4 Implementation

- **Detection**: `PiMateriaRuntime.InterpretStdoutLine()` parses the `cast_start` event and extracts multiTurn agent socket names via `ExtractMultiTurnAgentSockets()`.
- **Enforcement**: `PiMateriaRuntime.WaitForTerminalOrExitAsync()` checks `active.HasMultiTurnAgentSockets && active.EventingPreset == "agent-controller"` and calls `SynthesizeFailureAsync()` if both are true.
- **Artifact enrichment**: The `cast_start` event is persisted as a `runtime.status` lifecycle artifact with full socket metadata (names, types, materia names, multiTurn flags) so misconfigurations are diagnosable from the run log.
- **Tests**: `PiMateriaRuntimeTests.cs` covers all four scenarios: single-turn allowed, multiTurn under agent-controller aborts, multiTurn under interactive allowed, and failure artifact contains socket metadata.

---

## 4. Fail-Closed on Unrecognized Event Types

### 4.1 The Behavior

When `PiMateriaRuntime` encounters a stdout event with a `type` not in `AllRecognizedTypes`:

| Eventing Preset | Behavior |
|-----------------|----------|
| `agent-controller` | **Fail the cast** with a `runtime.failed` event. The error message includes the unrecognized type string, `castId`, and resolved materia name. |
| `interactive` / `cli` / other | **Warn and continue**. A warning is logged via `Log.PiStdoutUnknownType()` with diagnostic context. Execution continues so a human can intervene. |

### 4.2 Why This Exists

This is a **defense-in-depth guard** against contract drift between the pi-materia emitter and the agent-controller parser. Even if a new event type slips past the conformance tests, an autonomous run will **fail closed** instead of stalling silently and burning tokens.

The original incident that motivated this rule: pi emitted `type="agent_end"` which the runtime did not recognize at the time. The run stalled with only a warn-level log. The fix added `agent_end` to the recognized contract (§2) AND added this fail-closed guard so future drift is caught immediately.

### 4.3 Defense-in-Depth

The unrecognized-type check runs in two places:

1. **Monitor loop** (`WaitForTerminalOrExitAsync`): checked every polling interval while the process is alive.
2. **Process exit handler** (`HandleProcessExitAsync`): if the process exits and an unrecognized type was seen during the run, the exit handler fails the run instead of synthesizing a completion from the exit code.

This ensures contract drift is caught even if the monitor loop is slow to detect it.

### 4.4 Implementation

- **Tracking**: `ActiveProcess.SetUnrecognizedType()` records the first unrecognized type seen.
- **Diagnostic enrichment**: `castId` is extracted from `cast_start`; `materiaName` is tracked from `materia_start` events. Both are included in the failure payload.
- **Tests**: `PiMateriaRuntimeTests.cs` covers both the autonomous fail-closed path and the interactive warn-continue path.

---

## 5. Relationship to the HTTP Webhook Channel

The stdout event stream and the HTTP webhook channel serve different purposes:

| Channel | Purpose | Authoritative? |
|---------|---------|----------------|
| **stdout JSONL** | Diagnostic events, prompt acceptance, stall prevention (`agent_end`, multiTurn detection, contract drift) | No — used for runtime internals only |
| **HTTP webhook** (`POST /runs/{runId}/events`) | Runtime lifecycle state transitions (`runtime.accepted`, `runtime.completed`, etc.) | **Yes** — the controller's state machine is driven by webhook events |

The stdout `agent_end` event triggers graceful shutdown of the pi process, but the authoritative terminal state comes from the webhook's `runtime.completed` or `runtime.failed` event. If the webhook is delayed, the process exit handler synthesizes a final event from the exit code.

See [Runtime Event Contract](./runtime-events.md) for the full webhook event specification.

---

## 6. Related Source Files

| File | Role |
|------|------|
| `src/AgentController.Domain/PiMateriaStdoutEventTypes.cs` | Single-source event type constants and schema |
| `src/AgentController.Infrastructure/PiMateriaRuntime.cs` | Parser and runtime enforcement (multiTurn fail-fast, unrecognized-type fail-closed, `agent_end` shutdown) |
| `tests/AgentController.Infrastructure.Tests/PiMateriaRuntimeTests.cs` | Integration tests for stdout event handling |
| `tests/AgentController.Domain.Tests/PiMateriaStdoutEventContractTests.cs` | Conformance tests for the schema |
| [Runtime Event Contract](./runtime-events.md) | HTTP webhook event specification |
| `docs/investigations/socket-3-materia-divergence.md` | Root cause analysis of the Biggs loadout multiTurn misconfiguration |

---

## 7. Adding a New Event Type

To add a new stdout event type:

1. Add a `public const string` to `PiMateriaStdoutEventTypes` with XML docs describing the type and its terminal/intermediate classification.
2. Add the type to the appropriate set (`TerminalTypes` or `IntermediateTypes`).
3. Add a `StdoutEventSchemaEntry` to `PiMateriaStdoutEventContract.Schema` with full field descriptors.
4. Handle the type in `PiMateriaRuntime.InterpretStdoutLine()` with appropriate logic.
5. Add tests: conformance (schema consistency) and integration (runtime behavior).
6. If semantics change or types are added, increment `ContractVersion`.

Do not add types to the emitter without updating the parser — the conformance tests will catch the drift.
