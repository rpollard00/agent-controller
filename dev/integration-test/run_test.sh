#!/usr/bin/env bash
# Tier B integration test: drive a FULL workflow through the REAL controller.
#
# Boots the real AgentController.Api (PollingWorker + PiMateriaRuntime) on a
# fixed port, scaffolds a clean widget repo, seeds one LocalFile work item, and
# lets the controller discover → claim → provision → clone → cast (real pi +
# pi-materia) → ingest runtime.* events over the real HTTP endpoint. Then polls
# GET /runs/{id} until terminal and asserts the contract.
#
# This is the only harness that exercises the real poll loop AND the real
# runtime AND real pi against the real controller endpoint. Tier A
# (PiMateriaRuntimeTests, `dotnet test`) covers the runtime with a fake pi; the
# standalone spike (dev/integration-spike) covers real pi against a stand-in
# listener.
#
# Usage:
#   ./run_test.sh                 # defaults
#   PORT=5200 TASK="..." ./run_test.sh
#
# Env (all optional):
#   PORT      controller port             (default 5103)
#   TASK      the cast task / work item   (default: add multiply())
#   BODY      work item body text         (default: brief description)
#   TIMEOUT   seconds to wait for terminal (default 900)
#   LOADOUT   materia loadout             (default Wedge, from the user profile)
#   POLL      worker poll interval (s)    (default 2)
#   KEEP      1 = keep run output + widget after success (default 0)
#
# Per-machine prereqs (NOT automated by this script): .NET 10 SDK, `pi` on PATH
# with pi-materia installed, a configured model + API key in ~/.pi/agent, git,
# and jj (Wedge's Blackbelt utilities require jj).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(git -C "$HERE" rev-parse --show-toplevel)"
SPIKE="$REPO/dev/integration-spike"

PORT="${PORT:-5103}"
BASE_URL="http://localhost:${PORT}"
LOADOUT="${LOADOUT:-Wedge}"
POLL="${POLL:-2}"
TIMEOUT="${TIMEOUT:-900}"
KEEP="${KEEP:-0}"
TASK="${TASK:-Add a multiply(a, b) function to widget/calc.py that returns the product of a and b. Add a test for it in test_calc.py.}"
BODY="${BODY:-A small, well-scoped task that a Wedge cast can plan, implement, evaluate, and checkpoint autonomously.}"

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
RUN_DIR="$HERE/runs/${STAMP}"
DB_PATH="${RUN_DIR}/controller.db"
RUN_ROOT="${RUN_DIR}/controller-runs"
WIDGET_REPO="${RUN_DIR}/widget"
ENV_FILE="${RUN_DIR}/env.sh"
API_LOG="${RUN_DIR}/api.log"
MIGRATIONS_LOG="${RUN_DIR}/migrations.log"
API_PID=""

echo "============================================================"
echo " agent_router controller-driven integration test (Tier B)"
echo "   run dir    : $RUN_DIR"
echo "   base url   : $BASE_URL"
echo "   loadout    : $LOADOUT"
echo "   task       : $TASK"
echo "   timeout    : ${TIMEOUT}s"
echo "============================================================"

cleanup() {
  local rc=$?
  echo "[test] tearing down (rc so far=$rc) ..."
  if [[ -n "$API_PID" ]] && kill -0 "$API_PID" 2>/dev/null; then
    echo "[test] SIGTERM api (pid=$API_PID) -> host disposes PiMateriaRuntime -> kills pi"
    kill -TERM "$API_PID" 2>/dev/null || true
    for _ in $(seq 1 20); do
      kill -0 "$API_PID" 2>/dev/null || break
      sleep 0.5
    done
    if kill -0 "$API_PID" 2>/dev/null; then
      echo "[test] api still alive after grace; SIGKILL"
      kill -KILL "$API_PID" 2>/dev/null || true
    fi
    wait "$API_PID" 2>/dev/null || true
  fi
  # Safety net for any orphaned `pi --mode rpc` child (rare: only if the host
  # disposal did not run). This harness is the only thing spawning that process.
  if pgrep -f "pi --mode rpc" >/dev/null 2>&1; then
    echo "[test] killing orphaned pi --mode rpc processes"
    pkill -TERM -f "pi --mode rpc" 2>/dev/null || true
    sleep 1
    pkill -KILL -f "pi --mode rpc" 2>/dev/null || true
  fi

  if [[ "$KEEP" != "1" ]]; then
    rm -rf "$WIDGET_REPO"
  fi
  echo "[test] done. logs: $API_LOG , $MIGRATIONS_LOG ; events via: GET $BASE_URL/runs"
  exit "$rc"
}
trap cleanup EXIT

# ── 1. Build the solution once (so migrations + API run fast) ─────────
echo "[test] building solution ..."
( cd "$REPO" && dotnet build --nologo -clp:ErrorsOnly ) >/dev/null

# ── 2. Scaffold a clean widget repo (controller injects materia config) ──
echo "[test] scaffolding widget repo at $WIDGET_REPO"
bash "$SPIKE/scaffold_project.sh" "$WIDGET_REPO" --force --no-materia-config

# ── 3. Write controller config as environment variables ──────────────
# ASP.NET Core reads env vars with '__' as the hierarchy separator, layered on
# top of the committed appsettings.json defaults. This keeps the harness fully
# self-contained (no committed test config file) and works identically for the
# migrations console app and the API host. Values are shell-quoted via %q so
# free-text tasks (apostrophes, parens, etc.) round-trip safely through source.
q() { printf '%q' "$1"; }
{
  printf 'export ASPNETCORE_ENVIRONMENT=%s\n' "$(q Development)"
  printf 'export ASPNETCORE_URLS=%s\n'             "$(q "$BASE_URL")"
  printf 'export DOTNET_ENVIRONMENT=%s\n'           "$(q Development)"

  printf 'export AgentController__WorkerId=%s\n'             "$(q intgtest)"
  printf 'export AgentController__WorkerEnabled=%s\n'         "$(q true)"
  printf 'export AgentController__PollIntervalSeconds=%s\n'   "$(q "$POLL")"
  printf 'export AgentController__MaxConcurrentRuns=%s\n'     "$(q 1)"
  printf 'export AgentController__StaleTimeoutSeconds=%s\n'   "$(q 1200)"
  printf 'export AgentController__RunRoot=%s\n'               "$(q "$RUN_ROOT")"
  printf 'export AgentController__RetainSuccessfulRuns=%s\n'  "$(q true)"
  printf 'export AgentController__RetainFailedRuns=%s\n'      "$(q true)"

  printf 'export Persistence__Provider=%s\n'          "$(q Sqlite)"
  printf 'export Persistence__ConnectionString=%s\n'  "$(q "Data Source=$DB_PATH")"

  printf 'export WorkSource__Provider=%s\n'            "$(q LocalFile)"
  printf 'export SourceControl__Provider=%s\n'         "$(q LocalGit)"
  printf 'export EnvironmentProvider__Provider=%s\n'   "$(q LocalWorkspace)"

  printf 'export Runtime__Provider=%s\n'                       "$(q PiMateria)"
  printf 'export Runtime__PiExecutablePath=%s\n'               "$(q pi)"
  printf 'export Runtime__DefaultMateriaLoadout=%s\n'          "$(q "$LOADOUT")"
  printf 'export Runtime__ControllerBaseUrl=%s\n'              "$(q "$BASE_URL")"
  printf 'export Runtime__HeartbeatIntervalSeconds=%s\n'       "$(q 30)"
  printf 'export Runtime__PromptAcceptanceTimeoutSeconds=%s\n' "$(q 120)"
  printf 'export Runtime__CancelGracePeriodSeconds=%s\n'       "$(q 15)"

  printf 'export LocalWork__Definitions__0__RepoKey=%s\n'  "$(q widget)"
  printf 'export LocalWork__Definitions__0__Title=%s\n'    "$(q "$TASK")"
  printf 'export LocalWork__Definitions__0__Body=%s\n'     "$(q "$BODY")"
  printf 'export LocalWork__Definitions__0__Tags__0=%s\n'  "$(q agent-ready)"
  printf 'export LocalWork__Definitions__0__Priority=%s\n' "$(q 1)"
  printf 'export LocalWork__Definitions__0__Status=%s\n'   "$(q New)"

  printf 'export Repositories__widget__CloneUrl=%s\n'          "$(q "$WIDGET_REPO")"
  printf 'export Repositories__widget__DefaultBranch=%s\n'    "$(q main)"
  printf 'export Repositories__widget__EnvironmentProfile=%s\n' "$(q local-default)"
  printf 'export Repositories__widget__RuntimeProfile=%s\n'    "$(q pi-materia-default)"
} > "$ENV_FILE"
echo "[test] controller config written to $ENV_FILE"
# shellcheck disable=SC1090
source "$ENV_FILE"

# ── 4. Run database migrations (blocking) ────────────────────────────
echo "[test] running migrations ..."
if ! ( cd "$REPO" && dotnet run --no-build --project src/AgentController.Migrations ) > "$MIGRATIONS_LOG" 2>&1; then
  echo "[test] FAIL: migrations failed. See $MIGRATIONS_LOG" >&2
  tail -20 "$MIGRATIONS_LOG" >&2
  exit 1
fi
echo "[test] migrations complete."

# ── 5. Boot the API host in the background ───────────────────────────
echo "[test] starting controller API on $BASE_URL ..."
# Env vars are already in this shell (sourced in step 3); the API child inherits them.
( cd "$REPO" && dotnet run --no-build --no-launch-profile --project src/AgentController.Api ) \
  > "$API_LOG" 2>&1 &
API_PID=$!
echo "[test] api pid=$API_PID  (log: $API_LOG)"

# ── 6. Wait for the API health endpoint ──────────────────────────────
echo "[test] waiting for controller health ..."
healthy=0
for _ in $(seq 1 60); do
  if curl -sf -m 2 "$BASE_URL/health" >/dev/null 2>&1; then
    healthy=1
    break
  fi
  if ! kill -0 "$API_PID" 2>/dev/null; then
    echo "[test] FAIL: api process exited during startup. See $API_LOG" >&2
    tail -30 "$API_LOG" >&2
    exit 1
  fi
  sleep 1
done
if [[ "$healthy" != "1" ]]; then
  echo "[test] FAIL: controller never became healthy. See $API_LOG" >&2
  tail -30 "$API_LOG" >&2
  exit 1
fi
echo "[test] controller healthy. PollingWorker will discover the work item shortly."

# ── 7. Poll the run to terminal + assert ─────────────────────────────
python3 "$HERE/wait_for_terminal.py" \
  --base-url "$BASE_URL" \
  --timeout "$TIMEOUT" \
  --poll-interval 3 \
  --boot-grace 60
POLL_RC=$?

# Give the runtime a moment to finish shutting pi down on the success path
# before the cleanup trap kills the API.
if [[ "$POLL_RC" == "0" ]]; then
  echo "[test] success — letting the runtime finish shutting pi down ..."
  sleep 4
fi

exit "$POLL_RC"
