# Investigation: Orphaned pi-materia Run (run_88bfd100311a48c9accdb88626b9b3e1)

## Summary

The pi-materia loadout died after completing two full work-item iterations (Socket-4 visits 1-3) and partway through a third, leaving the agent-controller run orphaned in `AgentRunning` state. The run never emitted `runtime.accepted` or any webhook events because eventing was disabled in the agent_router config. Even if events had fired, stale-run recovery was structurally blind to runs stuck in `AgentRunning` — it only covers `AwaitingResult`.

**Cast:** `2026-06-26T17-17-28-071Z`
**Run:** `run_88bfd100311a48c9accdb88626b9b3e1`
**Loadout:** Biggs (`user:rude-copy:15d29129-5e29-4bb2-8562-0356fc3ebc2f`)
**Agent:** Elena / Auto-Plana (via `Interactive-Plani` on Socket-3)

## Execution Timeline

The cast executed the following socket sequence (from `events.jsonl`):

| Order | Socket | Materia | Work Item | Outcome |
|-------|--------|---------|-----------|---------|
| 1 | Socket-1 | Ignore-Artifacts | — | Completed |
| 2 | Socket-2 | Blackbelt-Bootstrap | — | Completed |
| 3 | Socket-3 | Interactive-Plani | — | Completed (planning phase) |
| 4 | Socket-8 | Commit-Sigil | — | Completed (sigil check) |
| 5 | Socket-4 (v1) | Builda | WI-1: feat(api): add request-level observability | Completed |
| 6 | Socket-5 (v1) | Auto-Evala | WI-1 | Completed (satisfied) |
| 7 | Socket-6 (v1) | Blackbelt-Maintain | WI-1 | Completed (jj checkpoint) |
| 8 | Socket-4 (v2) | Builda | WI-2: fix(api): log field-validation rejections | Completed |
| 9 | Socket-5 (v2) | Auto-Evala | WI-2 | Completed (satisfied) |
| 10 | Socket-6 (v2) | Blackbelt-Maintain | WI-2 | Completed (jj checkpoint) |
| 11 | Socket-4 (v3) | Builda | WI-3: fix(runtime): extend stale-run recovery | Completed |
| 12 | Socket-5 (v3) | Auto-Evala | WI-3 | Completed (satisfied) |
| 13 | Socket-6 (v3) | Blackbelt-Maintain | WI-3 | Completed (jj checkpoint) |
| 14 | Socket-4 (v4) | Builda | WI-4: chore(docs): correct stale socket-3 investigation | **DIED — awaiting_agent_response** |

The last event in `events.jsonl` is an `async_prompt_dispatch_attempt` for Socket-4 visit 4. There is no `socket_end`, no `cast_end`, and no `runtime.*` events anywhere in the log.

## Root Causes

### 1. Eventing disabled in agent_router config (`eventing.enabled=false`)

The agent_router configuration has `eventing.enabled=false` with empty presets. This means pi-materia never fires webhook callbacks to the agent-controller at `/runs/{runId}/events`. Without these events:

- No `runtime.accepted` is emitted, so the run never transitions from `AgentRunning` to `AwaitingResult`
- No `runtime.completed` or `runtime.failed` is emitted, so the controller has no signal that the loadout finished (or died)
- The run stays in `AgentRunning` indefinitely — there is no code path to detect the orphan

**Evidence:** `events.jsonl` contains 152 events, all `advancement_lifecycle` or `socket_start` types. Zero `runtime.*` events. Zero webhook-related events.

### 2. Stale-run recovery excludes `AgentRunning` state

`FindStaleAsync` (EfAgentRunStore.cs:183) and `RecoverStaleRunWithRetryAsync` (RunLifecycleService.cs:1021) only handle runs in `AwaitingResult`. A fire-and-forget pi-materia run that dies before emitting `runtime.accepted` never advances past `AgentRunning`, so stale recovery is structurally blind to it.

This is the structural blind spot: even if the polling worker runs on schedule, it finds zero stale runs because the orphaned run is in `AgentRunning`, not `AwaitingResult`.

### 3. Socket-3 LLM hang / process termination

The pi-materia process died while Socket-4 (Builda) was awaiting an agent response for WI-4. The exact cause of the process death is not fully determined from the event log — it could be an LLM timeout, OOM, or signal termination. What is clear is that no graceful shutdown occurred (no `cast_end`, no error event).

## Why the Old Investigation Was Wrong

The previous version of this document described a "Biggs/Interactive-Plani multiTurn stall" where the controller never sent `/materia continue`. That was based on an earlier cast (`2026-06-25T15-44-35-220Z`) and an incomplete analysis. The actual issue for this run is different:

- The `Interactive-Plani` on Socket-3 **did** complete successfully (it produced the plan and advanced to Socket-8)
- The run executed 3 full work-item loops and was partway through a 4th before dying
- The orphan was caused by eventing being off + stale recovery not covering `AgentRunning`, not by a multiTurn stall

## Remediation (Implemented)

The following code changes address the root causes:

1. **feat(api): add request-level observability for event ingestion** — Information-level logging for every POST to `/runs/{runId}/events` so webhook activity (or lack thereof) is visible at default log levels. Also logs the resolved `ControllerBaseUrl` and constructed event URL at run start.

2. **fix(api): log field-validation rejections at Information** — Promotes field-validation rejections in `IngestRuntimeEventCommandHandler` from Debug to Information, so malformed webhook events are observable without Debug logging.

3. **fix(runtime): extend stale-run recovery to `AgentRunning` state** — `FindStaleAsync` now queries both `AwaitingResult` and `AgentRunning` runs. `RecoverStaleRunWithRetryAsync` accepts `AgentRunning` runs with a configurable cutoff threshold (based on `StartedAt`), synthesizing a failure → `NeedsHuman` transition instead of orphaning.

4. **Config fix (user-owned)**: Enable eventing in agent_router config by setting `eventing.enabled=true` and configuring the `agent-controller` preset. The runId wiring is already correct (resolved via `CONTROLLER_RUN_ID` env / `CONTROLLER_CONTEXT_DIR/controller-run.json` / explicit param).

## Artifacts Examined

- `.pi/pi-materia/2026-06-26T17-17-28-071Z/events.jsonl` — 152 runtime events, zero `runtime.*` events, last entry is `async_prompt_dispatch_attempt` for Socket-4 visit 4
- `.pi/pi-materia/2026-06-26T17-17-28-071Z/manifest.json` — Cast manifest showing Biggs loadout, Interactive-Plani on Socket-3, Elena/Auto-Plana agent
- `.pi/pi-materia/2026-06-26T17-17-28-071Z/config.resolved.json` — Full resolved config with all loadouts and materia definitions
- `src/AgentController.Infrastructure/Data/Repositories/EfAgentRunStore.cs` — `FindStaleAsync` (line 183)
- `src/AgentController.Application/Services/RunLifecycleService.cs` — `RecoverStaleRunWithRetryAsync` (line 1021)
- `src/AgentController.Api/PollingWorker.cs` — `RecoverStaleRunsAsync` polling worker (line 1232)
