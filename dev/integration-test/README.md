# agent_router ↔ pi-materia controller-driven integration test (Tier B)

Drives a **full workflow through the real controller**: boots the real
`AgentController.Api` (PollingWorker + `PiMateriaRuntime`), scaffolds a clean
widget repo, seeds one `LocalFile` work item, and lets the controller discover →
claim → provision → clone → cast (real `pi` + pi-materia) → ingest `runtime.*`
events over the real `POST /runs/{runId}/events` endpoint. Then polls
`GET /runs/{id}` until terminal and asserts the contract.

This is the only harness that exercises the **real poll loop AND the real
runtime AND real pi against the real controller endpoint**:

| Harness | poll loop | runtime | pi | event endpoint |
|---|---|---|---|---|
| `dotnet test` `PiMateriaRuntimeTests` (Tier A) | bypassed | real | fake | real (HttpListener) |
| `dev/integration-spike` | bypassed | bypassed (standalone driver) | real | stand-in listener |
| **`dev/integration-test` (this)** | **real** | **real** | **real** | **real controller** |

## Prerequisites (per machine, NOT automated)

- .NET 10 SDK
- `pi` on PATH with pi-materia installed (`pi install npm:@rpollard00/pi-materia`)
- A configured model + API key in `~/.pi/agent` (the test uses the **Wedge**
  loadout from your pi-materia user profile)
- `git` and `jj` (Wedge's Blackbelt utilities require jj)

## Run

```bash
./run_test.sh
```

Optional environment overrides:

| Var | Default | Purpose |
|---|---|---|
| `PORT` | `5103` | controller port |
| `TASK` | *add a multiply() function…* | the work item title → `/materia cast` prompt |
| `BODY` | brief description | work item body |
| `TIMEOUT` | `900` | seconds to wait for terminal |
| `LOADOUT` | `Wedge` | pi-materia loadout (from the user profile) |
| `POLL` | `2` | PollingWorker interval (seconds) |
| `KEEP` | `0` | `1` = keep the widget repo + run output after success |

## What it does

1. `dotnet build` the solution.
2. Scaffolds a **clean** widget repo (no `.pi/pi-materia.json` — the controller
   injects eventing itself via `MATERIA_CONFIG`, so this proves the repo is not
   mutated).
3. Writes controller config as environment variables (layered on the committed
   `appsettings.json` defaults) — no committed test config file.
4. Runs migrations (`AgentController.Migrations`).
5. Boots the API host (`AgentController.Api`) with `workerEnabled=true` and
   `runtime:provider=PiMateria`.
6. Waits for `GET /health`.
7. `wait_for_terminal.py` polls `GET /runs` until a run appears, then
   `GET /runs/{id}` until terminal, and asserts `runtime.completed` is present.

## Output

Everything lands in `runs/<timestamp>/` (gitignored):

- `controller.db` — the controller's SQLite store (inspect with `sqlite3`)
- `controller-runs/<runId>/` — per-run workspace (cloned repo, context files,
  logs) retained for debugging
- `env.sh` — the generated controller config
- `api.log` / `migrations.log` — host logs

## Expected first-run notes

- A real Wedge cast takes ~2–3 minutes.
- The benign `accepted`-on-`AwaitingResult` race (poll loop advances faster than
  pi boots) used to return 422; it is now tolerated (200) — see the
  `accepted`-tolerance fix in `RunLifecycleService.HandleAcceptedAsync`.
- On success, `PiMateriaRuntime` detects the terminal webhook event and shuts pi
  down before the harness kills the API.

## Files

- `run_test.sh` — orchestrator
- `wait_for_terminal.py` — polls the controller API + asserts the contract
- `../integration-spike/scaffold_project.sh` — reused widget scaffold (called
  with `--no-materia-config`)
