#!/usr/bin/env bash
# Gap 4 spike: validate that a real pi-materia `/materia cast` with the
# `agent-controller` eventing preset POSTs `runtime.*` events to the
# controller's HTTP event endpoint, using the Wedge loadout.
#
# This is the cheapest end-to-end de-risk before building agent_router's
# `PiMateriaRuntime`. It needs no agent_router code at all — just pi + the
# widget project + a stand-in HTTP listener.
#
# Usage:
#   ./run_spike.sh                      # defaults
#   RUN_ID=run-foo PORT=5100 ./run_spike.sh
#
# Env (all optional):
#   RUN_ID      controller run id (default run-spike-001)
#   PORT        listener port          (default 5099)
#   TASK        the cast task          (default: add a multiply() fn)
#   TIMEOUT     seconds to wait for terminal event (default 900)
#   KEEP        1 = keep scratch project after run (default 0)
#   PROJECT_DIR scratch widget project (default ./runs/latest/widget)
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$HERE"

RUN_ID="${RUN_ID:-run-spike-001}"
PORT="${PORT:-5099}"
TASK="${TASK:-Add a multiply(a, b) function to widget/calc.py that returns the product of a and b. Add a test for it in test_calc.py.}"
TIMEOUT="${TIMEOUT:-900}"
KEEP="${KEEP:-0}"

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
RUN_DIR="$HERE/runs/$STAMP"
mkdir -p "$RUN_DIR"
EVENTS="$RUN_DIR/events.jsonl"
PROJECT_DIR="${PROJECT_DIR:-$RUN_DIR/widget}"

echo "============================================================"
echo " pi-materia <-> controller webhook spike"
echo "   run dir : $RUN_DIR"
echo "   run id  : $RUN_ID"
echo "   port    : $PORT"
echo "   project : $PROJECT_DIR"
echo "   task    : $TASK"
echo "============================================================"

# 1. Start the stand-in controller event listener.
python3 "$HERE/listener.py" --port "$PORT" --outfile "$EVENTS" \
  >"$RUN_DIR/listener.log" 2>&1 &
LISTENER_PID=$!
echo "[spike] listener pid=$LISTENER_PID"

# Ensure the listener is killed on exit (success or failure).
cleanup() {
  rc=$?
  if [[ "$KEEP" != "1" && -d "$PROJECT_DIR" ]]; then
    rm -rf "$PROJECT_DIR"
  fi
  kill "$LISTENER_PID" 2>/dev/null || true
  wait "$LISTENER_PID" 2>/dev/null || true
  echo "[spike] done (rc=$rc). events: $EVENTS  logs: $RUN_DIR"
  exit $rc
}
trap cleanup EXIT

# 2. Wait for listener readiness.
for _ in $(seq 1 30); do
  if curl -sf -m 1 "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then
    echo "[spike] listener ready"
    break
  fi
  sleep 0.2
done
curl -sf -m 2 "http://127.0.0.1:$PORT/health" >/dev/null \
  || { echo "[spike] listener never became ready"; exit 1; }

# 3. Scaffold the widget project (idempotent).
bash "$HERE/scaffold_project.sh" "$PROJECT_DIR" --force

# 4. Run the cast via RPC and wait for the terminal webhook event.
python3 "$HERE/pi_rpc_driver.py" \
  --cwd "$PROJECT_DIR" \
  --task "$TASK" \
  --events-file "$EVENTS" \
  --run-id "$RUN_ID" \
  --port "$PORT" \
  --timeout "$TIMEOUT"

# 5. Analyze captured events.
python3 "$HERE/analyze.py" --events-file "$EVENTS" --run-id "$RUN_ID"
