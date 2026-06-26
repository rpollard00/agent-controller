#!/usr/bin/env bash
# Scaffold a throwaway "widget" project for the integration harnesses.
#
# Creates a tiny, real, autonomous-codable project (a python module + test) that
# Wedge can actually plan/build/eval/maintain against.
#
# Re-runnable: pass --force to wipe and recreate an existing directory.
set -euo pipefail

usage() {
  echo "usage: scaffold_project.sh <project-dir> [--force]" >&2
  exit 2
}

DIR=""
FORCE=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --force) FORCE=1; shift ;;
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

git init -q -b main
git config user.email "spike@example.com"
git config user.name "spike"
git add -A
git commit -q -m "Initial widget project"

echo "[scaffold] ready at $DIR"
