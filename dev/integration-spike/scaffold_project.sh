#!/usr/bin/env bash
# Scaffold a throwaway "widget" project for the integration harnesses.
#
# Creates a tiny, real, autonomous-codable project (a python module + test) that
# Wedge can actually plan/build/eval/maintain against.
#
# By default it also writes a project `.pi/pi-materia.json` enabling the
# `agent-controller` eventing preset (used by the standalone spike). For the
# controller-driven Tier B test, pass --no-materia-config: the controller's
# PiMateriaRuntime injects eventing itself via the MATERIA_CONFIG env var, so the
# cloned repo must stay clean.
#
# Re-runnable: pass --force to wipe and recreate an existing directory.
set -euo pipefail

usage() {
  echo "usage: scaffold_project.sh <project-dir> [--force] [--no-materia-config]" >&2
  exit 2
}

DIR=""
FORCE=0
WRITE_MATERIA=1

while [[ $# -gt 0 ]]; do
  case "$1" in
    --force) FORCE=1; shift ;;
    --no-materia-config) WRITE_MATERIA=0; shift ;;
    -h|--help) usage ;;
    *)
      if [[ -z "$DIR" ]]; then
        DIR="$1"
      else
        echo "unexpected argument: $1" >&2
        usage
      fi
      shift
      ;;
  esac
done

[[ -n "$DIR" ]] || usage

if [[ -d "$DIR/.git" ]]; then
  if [[ "$FORCE" == "1" ]]; then
    rm -rf "$DIR"
  else
    echo "[scaffold] $DIR already initialized (pass --force to recreate)" >&2
    exit 0
  fi
fi

mkdir -p "$DIR/widget" "$DIR/.pi"
cd "$DIR"

cat > widget/__init__.py <<'PY'
"""Widget: a tiny calculator module used by the integration harnesses."""
from .calc import add

__all__ = ["add"]
PY

cat > widget/calc.py <<'PY'
def add(a, b):
    """Return the sum of a and b."""
    return a + b
PY

cat > test_calc.py <<'PY'
from widget.calc import add


def test_add():
    assert add(2, 3) == 5
PY

cat > README.md <<'MD'
# widget

Tiny calculator package. Used as the target project for the agent_router /
pi-materia integration harnesses.
MD

cat > .gitignore <<'GI'
__pycache__/
*.pyc
.pi/pi-materia/
GI

# Optional project-level pi-materia config (the standalone spike uses this; the
# controller-driven Tier B test omits it via --no-materia-config because the
# controller's PiMateriaRuntime injects eventing via MATERIA_CONFIG).
if [[ "$WRITE_MATERIA" == "1" ]]; then
  cat > .pi/pi-materia.json <<'JSON'
{
  "activeLoadout": "Wedge",
  "eventing": {
    "enabled": true,
    "presets": ["agent-controller"],
    "heartbeatIntervalMs": 30000
  }
}
JSON
fi

git init -q -b main
git config user.email "spike@example.com"
git config user.name "spike"
git add -A
git commit -q -m "Initial widget project"

if [[ "$WRITE_MATERIA" == "1" ]]; then
  echo "[scaffold] ready at $DIR (loadout: Wedge, eventing: agent-controller preset in-repo)"
else
  echo "[scaffold] ready at $DIR (clean repo; controller will inject materia config via MATERIA_CONFIG)"
fi
