#!/usr/bin/env bash
#
# test.sh — Canonical quiet test runner for AgentController.
#
# Runs `dotnet test` against the full solution with `-v minimal` to suppress
# xUnit adapter chatter (Discovering/Discovered/Starting/Finished/version lines)
# while keeping per-project pass/fail summaries and full failure detail.
#
# This is the guaranteed-quiet fallback. The Directory.Build.props default
# already sets minimal verbosity for test projects, but this script ensures
# quiet output regardless of environment or MSBuild state.
#
# Usage:
#   ./dev/test.sh              Run all tests (quiet)
#   ./dev/test.sh -v normal    Override verbosity (passthrough)
#   ./dev/test.sh --filter "FullyQualifiedName~Api"   Pass any dotnet-test args
#
# All arguments are passed through to `dotnet test`. Verbosity defaults to
# `minimal` unless `-v` or `--verbosity` is explicitly provided.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SOLUTION="$ROOT/AgentController.slnx"

cd "$ROOT"

# Default verbosity: minimal (suppress xUnit adapter chatter).
# Users can override with -v normal, -v detailed, etc.
if [[ "$#" -eq 0 ]]; then
    ARGS=(-v minimal)
else
    # Check if user already passed a verbosity flag
    HAS_VERBOSITY=false
    for arg in "$@"; do
        if [[ "$arg" == "-v" ]] || [[ "$arg" == -v=* ]] || [[ "$arg" == "--verbosity" ]] || [[ "$arg" == --verbosity=* ]]; then
            HAS_VERBOSITY=true
            break
        fi
    done

    if [[ "$HAS_VERBOSITY" == true ]]; then
        ARGS=("$@")
    else
        ARGS=(-v minimal "$@")
    fi
fi

dotnet test "$SOLUTION" "${ARGS[@]}"
