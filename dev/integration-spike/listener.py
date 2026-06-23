#!/usr/bin/env python3
"""
Minimal stand-in for the agent_router controller's
`POST /runs/{runId}/events` endpoint.

Purpose (Gap 4 spike): prove a real pi-materia `/materia cast` with the
`agent-controller` eventing preset actually POSTs `runtime.*` events over HTTP
to the controller contract endpoint.

Behavior:
  - Listens on PORT (default 5099).
  - GET  /health               -> 200 {"ok": true}    (readiness probe)
  - POST /runs/{runId}/events  -> 200 {"accepted": true}
    (also accepts POST to any path; logs method + path + body)
  - Appends every received event body as one JSONL line to OUTFILE and prints
    a one-line summary to stdout.
  - Returns the same idempotent 200 the real controller returns so pi-materia's
    WebhookSink treats delivery as successful (no retries).

This is deliberately NOT the real controller — it does no validation, state
transitioning, or persistence beyond the JSONL log. It only proves the wire
contract is met end-to-end.
"""
from __future__ import annotations

import argparse
import datetime as _dt
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def _now() -> str:
    return _dt.datetime.now(tz=_dt.timezone.utc).isoformat()


class Handler(BaseHTTPRequestHandler):
    server_version = "gap4-spike/0.1"

    # Quiet logging — we print our own summary lines.
    def log_message(self, fmt, *args):  # noqa: A003 - signature dictated by base
        return

    def _send(self, code: int, payload: dict) -> None:
        body = json.dumps(payload).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):  # noqa: N802 - BaseHTTPRequestHandler API
        if self.path.rstrip("/") == "/health" or self.path == "/":
            self._send(200, {"ok": True})
        else:
            self._send(404, {"ok": False, "error": "not found"})

    def do_POST(self):  # noqa: N802 - BaseHTTPRequestHandler API
        length = int(self.headers.get("Content-Length", "0") or "0")
        raw = self.rfile.read(length) if length else b""
        path = self.path

        parsed = None
        parse_err = None
        if raw:
            try:
                parsed = json.loads(raw.decode("utf-8"))
            except Exception as exc:  # noqa: BLE001
                parse_err = str(exc)

        event_type = parsed.get("eventType") if isinstance(parsed, dict) else None
        run_id = parsed.get("runId") if isinstance(parsed, dict) else None

        record = {
            "ts": _now(),
            "method": "POST",
            "path": path,
            "eventType": event_type,
            "runId": run_id,
            "body": parsed,
        }
        if parse_err:
            record["parseError"] = parse_err

        outfile: str = self.server.outfile  # type: ignore[attr-defined]
        with open(outfile, "a", encoding="utf-8") as fh:
            fh.write(json.dumps(record, ensure_ascii=False) + "\n")
            fh.flush()

        flag = "  <<<TERMINAL" if (event_type or "").startswith(("runtime.completed", "runtime.failed", "runtime.cancelled")) else ""
        print(f"[{_now()}] POST {path}  eventType={event_type!r} runId={run_id!r}{flag}", flush=True)

        # The real controller returns 200 on accepted events (409 on dup, 422 on
        # bad shape). For the spike we always accept so the sink stops retrying.
        self._send(200, {"accepted": True})


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", type=int, default=5099)
    ap.add_argument("--outfile", required=True, help="JSONL file to append received events to")
    args = ap.parse_args()

    # Truncate / create the outfile up front.
    open(args.outfile, "w", encoding="utf-8").close()

    srv = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    srv.outfile = args.outfile  # type: ignore[attr-defined]
    print(f"[{_now()}] event listener ready on http://127.0.0.1:{args.port} -> {args.outfile}", flush=True)
    print(f"[{_now()}] GET /health | POST /runs/<runId>/events", flush=True)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        srv.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
