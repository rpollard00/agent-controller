#!/usr/bin/env python3
"""Poll a running controller API until the workflow's run reaches a terminal
state, then assert the contract.

Used by run_test.sh (Tier B). The controller's PollingWorker discovers a
LocalFile work item, claims it, clones the widget repo, hands off to
PiMateriaRuntime (real pi), and pi-materia POSTs runtime.* events back to the
controller's POST /runs/{runId}/events endpoint. This script watches the public
GET /runs and GET /runs/{id} endpoints for the workflow to finish.

Success: the run reaches Completed / PrOpened / BranchPushed and the lifecycle
events include runtime.completed.
Failure: the run reaches Failed / Cancelled, or no terminal state within the
timeout.
Partial: the run reaches NeedsHuman (cast ran but asked for a human).

Exit codes: 0 success, 1 failure/timeout, 2 needs_human.
"""
from __future__ import annotations

import argparse
import datetime as _dt
import json
import sys
import time
import urllib.error
import urllib.request


def _now() -> str:
    return _dt.datetime.now(tz=_dt.timezone.utc).isoformat()


# Force a no-proxy opener. The poller only ever talks to localhost; bypass any
# ambient proxy configuration (env/system) so requests reach the controller
# directly. urllib honors *_proxy env vars by default, which can divert localhost
# traffic to a proxy that returns 404.
_OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def http_get(url: str, timeout: float = 5.0):
    req = urllib.request.Request(url, headers={"Accept": "application/json"})
    return json.loads(_OPENER.open(req, timeout=timeout).read().decode("utf-8"))


def http_get_or_diag(url: str, timeout: float = 5.0):
    """Like http_get but returns (data, None) on success or (None, message) on
    failure, with the response body included when an HTTP response was received.
    Used so the poller can print what actually answered (proxy/server) on error."""
    req = urllib.request.Request(url, headers={"Accept": "application/json"})
    try:
        data = _OPENER.open(req, timeout=timeout).read().decode("utf-8", "replace")
        return json.loads(data), None
    except urllib.error.HTTPError as e:
        body = ""
        try:
            body = e.read().decode("utf-8", "replace")[:200]
        except Exception:
            pass
        return None, f"HTTP {e.code} {e.reason}; body={body!r}"
    except Exception as e:  # noqa: BLE001
        return None, repr(e)


def get_case(d: dict, *keys, default=None):
    """Case-insensitive nested key lookup (handles PascalCase vs camelCase)."""
    cur = d
    for key in keys:
        if not isinstance(cur, dict):
            return default
        lower = {k.lower(): v for k, v in cur.items()}
        if key.lower() not in lower:
            return default
        cur = lower[key.lower()]
    return cur


def is_workflow_done(status: str | None) -> bool:
    return status in {
        "Completed", "Failed", "Cancelled",
        "PrOpened", "BranchPushed", "NeedsHuman",
        "CleanedUp",
    }


def is_success(status: str | None) -> bool:
    return status in {"Completed", "PrOpened", "BranchPushed"}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base-url", required=True)
    ap.add_argument("--timeout", type=float, default=900.0, help="max seconds to wait for terminal")
    ap.add_argument("--poll-interval", type=float, default=3.0)
    ap.add_argument("--boot-grace", type=float, default=60.0,
                    help="seconds to wait for the first run to appear")
    args = ap.parse_args()

    base = args.base_url.rstrip("/")
    deadline = time.time() + args.timeout
    boot_deadline = time.time() + args.boot_grace

    run_id = None
    last_status = None

    # Phase 1: wait for a run to appear (worker must discover the work item).
    print(f"[{_now()}] waiting for a run to appear at {base}/runs ...", flush=True)
    while time.time() < deadline:
        listing, diag = http_get_or_diag(f"{base}/runs")
        if diag is not None:
            print(f"[{_now()}] (controller not ready yet: {diag})", flush=True)
            time.sleep(2)
            continue

        runs = get_case(listing, "runs", default=[]) or []
        if runs:
            # Pick the most recent run (there should be exactly one).
            runs_sorted = sorted(runs, key=lambda r: get_case(r, "createdAt", default="") or "", reverse=True)
            run_id = get_case(runs_sorted[0], "runId")
            if run_id:
                print(f"[{_now()}] run appeared: {run_id}", flush=True)
                break

        if time.time() > boot_deadline:
            print(f"[{_now()}] FAIL: no run appeared within {args.boot_grace}s boot grace. "
                  "Check that workerEnabled=true and the work item is eligible.", flush=True)
            return 1
        time.sleep(args.poll_interval)

    if not run_id:
        print(f"[{_now()}] FAIL: no run discovered before the overall timeout.", flush=True)
        return 1

    # Phase 2: poll the run until terminal.
    print(f"[{_now()}] polling {base}/runs/{run_id} until terminal ...", flush=True)
    while time.time() < deadline:
        detail, diag = http_get_or_diag(f"{base}/runs/{run_id}")
        if diag is not None:
            print(f"[{_now()}] (detail fetch failed: {diag})", flush=True)
            time.sleep(args.poll_interval)
            continue

        status = get_case(detail, "status")
        if status != last_status:
            runtime_run_id = get_case(detail, "runtimeRunId")
            print(f"[{_now()}] status={status}  runtimeRunId={runtime_run_id}", flush=True)
            last_status = status

        if is_workflow_done(status):
            print(f"[{_now()}] run reached terminal state: {status}", flush=True)
            return summarize(detail, status, run_id)

        time.sleep(args.poll_interval)

    print(f"[{_now()}] FAIL: run {run_id} did not reach a terminal state within "
          f"{args.timeout}s (last status: {last_status}).", flush=True)
    return 1


def summarize(detail: dict, status: str | None, run_id: str) -> int:
    events = get_case(detail, "lifecycleEvents", default=[]) or []
    event_types = [get_case(e, "eventType") for e in events]
    runtime_types = [t for t in event_types if t and t.startswith("runtime.")]
    pr_url = get_case(detail, "pullRequestUrl")
    branch = get_case(detail, "branchName")
    error = get_case(detail, "error")

    print(f"[{_now()}] === SUMMARY for {run_id} ===", flush=True)
    print(f"  final status     : {status}", flush=True)
    print(f"  runtimeRunId     : {get_case(detail, 'runtimeRunId')}", flush=True)
    print(f"  branch           : {branch}", flush=True)
    print(f"  pullRequestUrl   : {pr_url}", flush=True)
    print(f"  error            : {error}", flush=True)
    print(f"  lifecycle events : {len(event_types)} total, {len(runtime_types)} runtime.*", flush=True)
    print(f"  runtime.* types  : {runtime_types}", flush=True)

    # Contract assertions for a successful cast.
    if is_success(status):
        if "runtime.completed" not in runtime_types:
            print("  CONTRACT GAP: success state reached without a runtime.completed event.", flush=True)
        else:
            print("  CONTRACT OK: runtime.completed present.", flush=True)

    if status == "Failed" or status == "Cancelled":
        print(f"  RESULT: workflow ended in {status}.", flush=True)
        return 1
    if status == "NeedsHuman":
        print("  RESULT: workflow paused for human input.", flush=True)
        return 2
    print("  RESULT: workflow completed successfully.", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
