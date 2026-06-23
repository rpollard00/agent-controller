#!/usr/bin/env bash
# Scaffold a throwaway "widget" project for the integration spike.
#
# Creates a tiny, real, autonomous-codable project (a python module + test) that
# Wedge can actually plan/build/eval/maintain against, and writes the project
# `.pi/pi-materia.json` that enables the `agent-controller` eventing preset.
#
# Re-runnable: pass --force to wipe and recreate an existing directory.
set -euo pipefail

DIR="${1:?usage: scaffold_project.sh <project-dir> [--force]}"
FORCE=0
[[ "${2:-}" == "--force" ]] && FORCE=1

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
"""Widget: a tiny calculator module used by the integration-spike cast."""
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
pi-materia integration spike.
MD

cat > .gitignore <<'GI'
__pycache__/
*.pyc
.pi/pi-materia/
GI

# Project-level pi-materia config: enable the agent-controller webhook preset
# and select the Wedge loadout (defined in the user profile).
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

git init -q
git config user.email "spike@example.com"
git config user.name "spike"
git add -A
git commit -q -m "Initial widget project"

echo "[scaffold] ready at $DIR"
echo "[scaffold] loadout: Wedge  | eventing: agent-controller preset"
