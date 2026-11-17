#!/usr/bin/env python3
"""Fault-injecting proxy for POST /internal/content/import, used only by deploy/tests/smoke.sh.

It forwards the request to the real application, and for a call it is told to drop it closes the client connection
without an answer once the import is known to be in flight: a watcher session in PostgreSQL (through dit_psql from
remote/lib.sh) polls pg_locks until the importer's advisory lock is held. The real import therefore has started when the
client sees the transport failure, which is the ambiguity receive-content.sh must resolve.

The watcher is started and confirmed running before the request is forwarded, so the lock cannot be taken and released
unobserved because of the watcher's own startup. A very short import can still finish between two polls; the call is
then dropped as "completed" (the import has finished, which is also a state the client cannot tell from the outside).
Every call appends one line to the log: "<n> dropped held", "<n> dropped completed", "<n> forwarded" or an error.

    import-proxy.py --port 8082 --upstream 127.0.0.1:8081 --lib <remote/lib.sh> --log <file> --drop first|all
"""

import argparse
import http.client
import socket
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

# ContentImportService.AdvisoryLockKey (0x4449_5443_4F4E_5431) as pg_locks reports a bigint advisory key:
# classid is the high 32 bits, objid the low 32 bits and objsubid is 1.
LOCK_CLASSID = 0x44495443
LOCK_OBJID = 0x4F4E5431

WATCH_SQL = f"""
DO $$
DECLARE i int := 0;
BEGIN
  RAISE NOTICE 'watching';
  LOOP
    IF EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND classid = {LOCK_CLASSID} AND objid = {LOCK_OBJID} AND objsubid = 1 AND granted) THEN
      RAISE NOTICE 'held';
      RETURN;
    END IF;
    i := i + 1;
    EXIT WHEN i > 60000;
    PERFORM pg_sleep(0.001);
  END LOOP;
  RAISE EXCEPTION 'the importer lock was never taken';
END $$;
"""

FORWARDED_HEADERS = ("Authorization", "Content-Type", "Content-Length")


class Watcher:
    """One psql session that returns when the importer's advisory lock is held."""

    def __init__(self, lib: str) -> None:
        self.ready = threading.Event()
        self.held = threading.Event()
        self.process = subprocess.Popen(
            ["bash", "-c", '. "$1"; dit_psql postgres -f -', "_", lib],
            stdin=subprocess.PIPE,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            text=True,
        )
        self.process.stdin.write(WATCH_SQL)
        self.process.stdin.close()
        threading.Thread(target=self._read_notices, daemon=True).start()

    def _read_notices(self) -> None:
        for line in self.process.stderr:
            if "watching" in line:
                self.ready.set()
            if "held" in line:
                self.held.set()

    def failed(self) -> bool:
        return self.process.poll() not in (None, 0) and not self.held.is_set()

    def stop(self) -> None:
        if self.process.poll() is None:
            self.process.kill()
        self.process.wait()


class Proxy:
    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.calls = 0
        self.lock = threading.Lock()

    def next_call(self) -> int:
        with self.lock:
            self.calls += 1
            return self.calls

    def should_drop(self, call: int) -> bool:
        return self.args.drop == "all" or (self.args.drop == "first" and call == 1)

    def log(self, line: str) -> None:
        with self.lock, open(self.args.log, "a", encoding="utf-8") as handle:
            handle.write(line + "\n")


def make_handler(proxy: Proxy):
    host, _, port = proxy.args.upstream.partition(":")

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, format: str, *args) -> None:  # noqa: A002
            pass

        def do_POST(self) -> None:
            call = proxy.next_call()
            length = int(self.headers.get("Content-Length") or 0)
            body = self.rfile.read(length) if length else b""
            drop = proxy.should_drop(call)
            watcher = None
            if drop:
                watcher = Watcher(proxy.args.lib)
                if not watcher.ready.wait(60):
                    proxy.log(f"{call} error watcher-not-started")
                    watcher.stop()
                    self.send_error(502)
                    return

            result: dict = {}
            done = threading.Event()

            def forward() -> None:
                try:
                    connection = http.client.HTTPConnection(host, int(port), timeout=None)
                    headers = {k: self.headers[k] for k in FORWARDED_HEADERS if self.headers.get(k)}
                    connection.request("POST", self.path, body=body, headers=headers)
                    response = connection.getresponse()
                    result["status"] = response.status
                    result["type"] = response.getheader("Content-Type", "application/json")
                    result["body"] = response.read()
                    connection.close()
                except Exception as error:  # noqa: BLE001
                    result["error"] = repr(error)
                finally:
                    done.set()

            worker = threading.Thread(target=forward, daemon=True)
            worker.start()

            if not drop:
                done.wait()
                proxy.log(f"{call} forwarded")
                self.relay(result)
                return

            while not (watcher.held.is_set() or done.is_set() or watcher.failed()):
                time.sleep(0.001)
            if watcher.held.is_set():
                state = "held"
            elif done.is_set():
                state = "completed"
            else:
                state = "error watcher-failed"
            proxy.log(f"{call} dropped {state}")
            watcher.stop()
            try:
                self.connection.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            self.close_connection = True
            # The import goes on in the application; waiting keeps this thread's accounting honest.
            done.wait()

        def relay(self, result: dict) -> None:
            if "error" in result:
                self.send_error(502)
                return
            self.send_response(result["status"])
            self.send_header("Content-Type", result["type"])
            self.send_header("Content-Length", str(len(result["body"])))
            self.end_headers()
            self.wfile.write(result["body"])

    return Handler


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--upstream", required=True)
    parser.add_argument("--lib", required=True)
    parser.add_argument("--log", required=True)
    parser.add_argument("--drop", choices=("first", "all"), required=True)
    args = parser.parse_args()

    server = ThreadingHTTPServer(("127.0.0.1", args.port), make_handler(Proxy(args)))
    server.daemon_threads = True
    server.serve_forever()


if __name__ == "__main__":
    main()
