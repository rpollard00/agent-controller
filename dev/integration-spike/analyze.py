#!/usr/bin/env python3
"""Analyze the events.jsonl captured by listener.py and assert the contract.

Spike success criteria:
  - at least one `runtime.accepted`
  - a terminal event present: `runtime.completed` | `runtime.failed` | `runtime.cancelled`
  - if completed: payload.outcome is one of the controller's known outcomes
  - every event body is shape-compatible (eventId, eventType, occurredAt)
"""
from __future__ import annotations

import argparse
import json
import sys

KNOWN_OUTCOMES = {
    "pull_request_opened",
    "branch_pushed",
    "patch_created",
    "no_changes_needed",
    "needs_human",
    "failed",
}

REQUIRED_BODY_FIELDS = ("eventId", "eventType", "occurredAt")


def load(path: str) -> list[dict]:
    out = []
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                out.append(json.loads(line))
            except Exception:
                pass
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--events-file", required=True)
    ap.add_argument("--run-id", required=True)
    args = ap.parse_args()

    events = load(args.events_file)
    bodies = [e.get("body") or {} for e in events]
    runtime = [b for b in bodies if str(b.get("eventType", "")).startswith("runtime.")]
    types = [b.get("eventType") for b in runtime]

    print("=== EVENT ANALYSIS ===")
    print(f"total POSTs received     : {len(events)}")
    print(f"runtime.* events         : {len(runtime)}")
    print(f"runtime.* event types    : {types}")

    run_ids = {b.get("runId") for b in bodies if b.get("runId")}
    print(f"runId values seen        : {run_ids}")

    # Shape check
    bad_shape = []
    for b in runtime:
        missing = [f for f in REQUIRED_BODY_FIELDS if not b.get(f)]
        if missing:
            bad_shape.append((b.get("eventId"), missing))
    if bad_shape:
        print(f"events missing fields    : {bad_shape}")
    else:
        print(f"all runtime.* bodies have required fields {list(REQUIRED_BODY_FIELDS)}")

    accepted = [b for b in runtime if b.get("eventType") == "runtime.accepted"]
    terminal = [b for b in runtime if str(b.get("eventType", "")).split(".", 1)[-1] in {"completed", "failed", "cancelled"}]
    outcome = None
    if terminal:
        outcome = (terminal[-1].get("payload") or {}).get("outcome")
        print(f"terminal event           : {terminal[-1].get('eventType')}  outcome={outcome}")
        print(f"terminal summary         : {(terminal[-1].get('payload') or {}).get('summary')!r}")

    print()
    print("=== RESULT ===")
    ok = True
    reasons = []
    if not accepted:
        ok = False
        reasons.append("no runtime.accepted event received")
    if not terminal:
        ok = False
        reasons.append("no terminal runtime.* event received")
    if terminal and outcome and outcome not in KNOWN_OUTCOMES:
        ok = False
        reasons.append(f"outcome {outcome!r} not in known outcomes {sorted(KNOWN_OUTCOMES)}")
    if bad_shape:
        ok = False
        reasons.append(f"{len(bad_shape)} event(s) failed shape check")
    if args.run_id not in run_ids and run_ids:
        ok = False
        reasons.append(f"runId mismatch: expected {args.run_id!r}, saw {run_ids}")

    if ok:
        print("PASS — agent-controller webhook contract validated end-to-end")
        return 0
    print("FAIL:")
    for r in reasons:
        print(f"  - {r}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
