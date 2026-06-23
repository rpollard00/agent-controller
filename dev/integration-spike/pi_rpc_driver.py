#!/usr/bin/env python3
"""
Drive a real pi-materia cast via `pi --mode rpc` for the Gap 4 spike.

This mimics what agent_router's future `PiMateriaRuntime` will do: spawn `pi`
as a subprocess, hand it the work as an extension command, and let pi-materia
report progress over the webhook channel (validated separately by listener.py).

Flow:
  1. spawn `pi --mode rpc --no-session` with CONTROLLER_* env, cwd = project dir
  2. wait for boot, send `{"type":"prompt","message":"/materia cast <task>"}`
  3. poll EVENTS_FILE for a terminal runtime.* event (completed/failed/cancelled)
  4. on terminal (or timeout): short grace, abort, terminate

Completion signal: the terminal webhook event written to EVENTS_FILE by
listener.py. That is exactly the contract under test, so it is the right
signal to key off of.

stdout/stderr from pi are tee'd to PI_STDOUT_LOG / PI_STDERR_LOG.
RPC JSONL events are summarized at the end for diagnostics.
"""
from __future__ import annotations

import argparse
import datetime as _dt
import json
import os
import signal
import subprocess
import sys
import threading
import time
from pathlib import Path

TERMINAL_PREFIXES = ("runtime.completed", "runtime.failed", "runtime.cancelled")


def _now() -> str:
    return _dt.datetime.now(tz=_dt.timezone.utc).isoformat()


def _read_seen_events(events_file: Path) -> list[dict]:
    if not events_file.exists():
        return []
    out: list[dict] = []
    for line in events_file.read_text(encoding="utf-8", errors="replace").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            out.append(json.loads(line))
        except Exception:
            pass
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--cwd", required=True, help="project working directory for pi")
    ap.add_argument("--task", required=True, help="the cast task prompt text")
    ap.add_argument("--events-file", required=True, help="JSONL file listener.py is writing events to")
    ap.add_argument("--run-id", default="run-spike-001")
    ap.add_argument("--port", type=int, default=5099)
    ap.add_argument("--timeout", type=float, default=900.0, help="max seconds to wait for a terminal event")
    ap.add_argument("--boot-wait", type=float, default=6.0)
    ap.add_argument("--grace", type=float, default=8.0, help="seconds to wait after terminal event before tearing down")
    ap.add_argument("--loadout", default=None, help="optional override: use '/materia autocast loadout:X -- task' instead")
    args = ap.parse_args()

    cwd = str(Path(args.cwd).resolve())
    events_file = Path(args.events_file).resolve()
    run_dir = events_file.parent
    stdout_log = open(run_dir / "pi_stdout.log", "w", encoding="utf-8")
    stderr_log = open(run_dir / "pi_stderr.log", "w", encoding="utf-8")

    env = dict(os.environ)
    event_url = f"http://127.0.0.1:{args.port}/runs/{args.run_id}/events"
    context_dir = run_dir / "context"
    context_dir.mkdir(parents=True, exist_ok=True)
    (context_dir / "controller-run.json").write_text(
        json.dumps(
            {
                "runId": args.run_id,
                "workItemId": "spike-1",
                "externalId": "spike-1",
                "source": "LocalFile",
                "repoKey": "widget",
                "repoPath": cwd,
                "startedAt": _now(),
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    env["CONTROLLER_RUN_ID"] = args.run_id
    env["CONTROLLER_EVENT_URL"] = event_url
    env["CONTROLLER_CONTEXT_DIR"] = str(context_dir)

    print(f"[{_now()}] launching pi --mode rpc  (cwd={cwd})", flush=True)
    print(f"[{_now()}] CONTROLLER_RUN_ID    = {args.run_id}", flush=True)
    print(f"[{_now()}] CONTROLLER_EVENT_URL = {event_url}", flush=True)
    print(f"[{_now()}] CONTROLLER_CONTEXT_DIR = {context_dir}", flush=True)

    proc = subprocess.Popen(
        ["pi", "--mode", "rpc", "--no-session"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        cwd=cwd,
        env=env,
        text=True,
        bufsize=1,
    )

    rpc_events: list[dict] = []
    rpc_responses: list[dict] = []

    def stdout_reader():
        assert proc.stdout is not None
        for line in proc.stdout:
            stdout_log.write(line)
            stdout_log.flush()
            line = line.strip()
            if not line:
                continue
            try:
                o = json.loads(line)
            except Exception:
                continue
            if o.get("type") == "response":
                rpc_responses.append(o)
                print(f"[{_now()}] rpc response: command={o.get('command')} success={o.get('success')} err={o.get('error')}", flush=True)
            elif o.get("type") == "extension_error":
                rpc_events.append(o)
                print(f"[{_now()}] rpc EXTENSION_ERROR: {json.dumps(o)[:300]}", flush=True)
            else:
                rpc_events.append(o)

    def stderr_reader():
        assert proc.stderr is not None
        for line in proc.stderr:
            stderr_log.write(line)
            stderr_log.flush()
            stripped = line.strip()
            if stripped:
                print(f"[{_now()}] pi stderr: {stripped}", flush=True)

    t1 = threading.Thread(target=stdout_reader, daemon=True)
    t2 = threading.Thread(target=stderr_reader, daemon=True)
    t1.start()
    t2.start()

    def send(obj: dict) -> None:
        assert proc.stdin is not None
        proc.stdin.write(json.dumps(obj) + "\n")
        proc.stdin.flush()

    exit_code = 0
    try:
        # Boot
        deadline = time.time() + args.boot_wait
        ready = False
        while time.time() < deadline:
            # Readiness via the listener, not pi. pi just needs to be up.
            time.sleep(0.5)
            if proc.poll() is not None:
                raise RuntimeError(f"pi exited during boot with code {proc.returncode}")
            ready = True
        if not ready:
            raise RuntimeError("pi did not boot in time")

        # Send the cast
        if args.loadout:
            message = f"/materia autocast loadout:{args.loadout} -- {args.task}"
        else:
            message = f"/materia cast {args.task}"
        print(f"[{_now()}] >>> prompt: {message}", flush=True)
        send({"id": "cast", "type": "prompt", "message": message})

        # Poll for terminal webhook event
        start = time.time()
        terminal = None
        last_seen_count = 0
        while time.time() - start < args.timeout:
            if proc.poll() is not None:
                print(f"[{_now()}] ! pi process exited early with code {proc.returncode}", flush=True)
                break
            seen = _read_seen_events(events_file)
            if len(seen) != last_seen_count:
                last_seen_count = len(seen)
                types = [s.get("eventType") for s in seen]
                print(f"[{_now()}] events so far ({len(seen)}): {types}", flush=True)
            for s in seen:
                et = (s.get("eventType") or "")
                if et.startswith(TERMINAL_PREFIXES):
                    terminal = s
                    break
            if terminal:
                break
            time.sleep(2.0)

        if terminal:
            print(f"[{_now()}] ✓ terminal webhook event received: {terminal.get('eventType')} "
                  f"outcome={(terminal.get('body') or {}).get('payload', {}).get('outcome')}", flush=True)
            print(f"[{_now()}]   grace {args.grace}s for trailing status events...", flush=True)
            time.sleep(args.grace)
        else:
            print(f"[{_now()}] ✗ NO terminal webhook event within {args.timeout}s timeout", flush=True)
            exit_code = 2

    except Exception as exc:  # noqa: BLE001
        print(f"[{_now()}] driver error: {exc}", flush=True)
        exit_code = 3
    finally:
        print(f"[{_now()}] shutting pi down (abort + terminate)...", flush=True)
        try:
            send({"type": "abort"})
        except Exception:
            pass
        try:
            proc.terminate()
            proc.wait(timeout=5)
        except Exception:
            try:
                proc.kill()
            except Exception:
                pass
        t1.join(timeout=2)
        t2.join(timeout=2)
        stdout_log.close()
        stderr_log.close()

    # Summary
    seen = _read_seen_events(events_file)
    runtime_types = [s.get("eventType") for s in seen if (s.get("eventType") or "").startswith("runtime.")]
    print(f"[{_now()}] === SUMMARY ===", flush=True)
    print(f"  rpc events captured : {len(rpc_events)}", flush=True)
    print(f"  rpc responses       : {len(rpc_responses)}", flush=True)
    print(f"  webhook POSTs seen  : {len(seen)}", flush=True)
    print(f"  runtime.* events    : {runtime_types}", flush=True)
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
