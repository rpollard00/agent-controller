# pi-materia <-> controller integration spike

Validates (Gap 4) that a real `/materia cast` with the `agent-controller`
eventing preset POSTs `runtime.*` events to the controller HTTP endpoint,
before building agent_router's `PiMateriaRuntime`.

## Run

    ./run_spike.sh

Optional env: `RUN_ID`, `PORT` (default 5099), `TASK`, `TIMEOUT` (default 900s),
`KEEP=1` (keep scratch project).

## Pieces

- `listener.py`        stand-in for `POST /runs/{runId}/events`
- `scaffold_project.sh` builds a throwaway widget project + project pi-materia.json
- `pi_rpc_driver.py`   spawns `pi --mode rpc`, sends `/materia cast`, waits for terminal event
- `run_spike.sh`       orchestrator
- `analyze.py`         asserts the runtime.* event contract

Output lands in `runs/<timestamp>/` (gitignored).
