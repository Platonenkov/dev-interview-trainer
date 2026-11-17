#!/usr/bin/env python3
"""S3 front for the smoke test's bucket stand-in that behaves like a key which may list and write but not read or delete.

The stand-in (rclone serve s3) has a single full-rights key and no policy language, so the restriction of the server's
backup key (docs/deployment.md: "list and write, no delete, no bucket creation", ADR 0021: "it cannot read anything
back") is enforced here, in front of it, the way an object store answers such a key. Refused with 403 AccessDenied:

  GET or HEAD of an object, path-style (/<bucket>/<key>) or virtual-hosted-style (Host <bucket>.<domain>, /<key>)
  GET or HEAD of the bucket with any query other than a listing (acl, versions, uploads, object-lock ...)
  DELETE, and POST with a "delete" query (multi-object delete)
  PUT or POST carrying x-amz-copy-source (a server-side copy reads an object)

Forwarded unchanged, Host header included, because the signature covers it: listings, PUT, and the multipart upload
calls (POST ?uploads, POST ?uploadId=...). Every request appends "<METHOD> <path>[?query] <status>" to the log, so a test
can prove which calls the backup script made. The smoke test reaches the stand-in directly (with its own full-rights
helper) on the upstream port.

    s3-no-read-proxy.py --port 9000 --upstream 127.0.0.1:9001 --log <file> [--bucket dit-smoke]
    s3-no-read-proxy.py --self-test
"""

import argparse
import http.client
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit

DENIED = (
    b'<?xml version="1.0" encoding="UTF-8"?><Error><Code>AccessDenied</Code>'
    b"<Message>The key may list and write only</Message></Error>"
)
HOP_BY_HOP = {"connection", "keep-alive", "transfer-encoding", "proxy-connection", "te", "trailer", "upgrade"}


LISTING_QUERY_KEYS = {
    "list-type", "prefix", "delimiter", "max-keys", "continuation-token", "start-after", "encoding-type", "marker", "fetch-owner",
}
WRITE_POST_KEYS = {"uploads", "uploadid"}


def is_object_path(host: str, path: str, bucket: str) -> bool:
    """True when the request addresses an object: /<bucket>/<key>, or /<key> under a virtual-hosted Host <bucket>.<domain>."""
    key_path = urlsplit(path).path.strip("/")
    first_label = host.split(":")[0].split(".")[0]
    if "." in host.split(":")[0] and first_label == bucket:
        return key_path != ""
    return "/" in key_path


def classify(method: str, host: str, target: str, headers: dict, bucket: str) -> str:
    """"deny" or "forward" for one request; headers has lower-case names."""
    parts = urlsplit(target)
    query = {k.lower(): v for k, v in parse_qs(parts.query, keep_blank_values=True).items()}
    if method == "DELETE":
        return "deny"
    if "x-amz-copy-source" in headers:
        return "deny"
    if method in ("GET", "HEAD"):
        if is_object_path(host, target, bucket) or not set(query) <= LISTING_QUERY_KEYS:
            return "deny"
        return "forward"
    if method == "POST":
        if "delete" in query or not (set(query) & WRITE_POST_KEYS):
            return "deny"
    return "forward"


def self_test() -> None:
    bucket = "dit-smoke"
    cases = [
        ("GET", "127.0.0.1:9000", "/dit-smoke/k.dump.age", {}, "deny"),
        ("HEAD", "127.0.0.1:9000", "/dit-smoke/k.dump.age", {}, "deny"),
        ("GET", "dit-smoke.s3.example.test", "/k.dump.age", {}, "deny"),
        ("HEAD", "dit-smoke.s3.example.test", "/k.dump.age", {}, "deny"),
        ("DELETE", "127.0.0.1:9000", "/dit-smoke/k.dump.age", {}, "deny"),
        ("POST", "127.0.0.1:9000", "/dit-smoke?delete", {}, "deny"),
        ("POST", "127.0.0.1:9000", "/dit-smoke/?delete=", {}, "deny"),
        ("POST", "dit-smoke.s3.example.test", "/?delete", {}, "deny"),
        ("GET", "127.0.0.1:9000", "/dit-smoke?acl", {}, "deny"),
        ("GET", "127.0.0.1:9000", "/dit-smoke?versions", {}, "deny"),
        ("PUT", "127.0.0.1:9000", "/dit-smoke/k", {"x-amz-copy-source": "/dit-smoke/other"}, "deny"),
        ("GET", "127.0.0.1:9000", "/dit-smoke?list-type=2&prefix=trainer-", {}, "forward"),
        ("GET", "127.0.0.1:9000", "/dit-smoke/", {}, "forward"),
        ("GET", "dit-smoke.s3.example.test", "/?list-type=2", {}, "forward"),
        ("PUT", "127.0.0.1:9000", "/dit-smoke/k.dump.age", {}, "forward"),
        ("POST", "127.0.0.1:9000", "/dit-smoke/k?uploads", {}, "forward"),
        ("POST", "127.0.0.1:9000", "/dit-smoke/k?uploadId=abc", {}, "forward"),
    ]
    failures = [c for c in cases if classify(c[0], c[1], c[2], c[3], bucket) != c[4]]
    if failures:
        for method, host, target, _, expected in failures:
            print(f"self-test FAILED: {method} {host}{target} should be {expected}", file=sys.stderr)
        sys.exit(1)
    print(f"self-test passed ({len(cases)} cases)")


def make_handler(upstream_host: str, upstream_port: int, log_path: str, bucket: str):
    log_lock = threading.Lock()

    def log(method: str, path: str, status: int) -> None:
        with log_lock, open(log_path, "a", encoding="utf-8") as handle:
            handle.write(f"{method} {path} {status}\n")

    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, format: str, *args) -> None:  # noqa: A002
            pass

        def deny(self) -> None:
            log(self.command, self.path, 403)
            # An unread request body would be taken for the next request on a kept-alive connection.
            length = int(self.headers.get("Content-Length") or 0)
            if length:
                self.rfile.read(length)
            self.send_response(403)
            self.send_header("Content-Type", "application/xml")
            body = b"" if self.command == "HEAD" else DENIED
            self.send_header("Content-Length", str(len(DENIED) if self.command != "HEAD" else 0))
            self.end_headers()
            self.wfile.write(body)

        def forward(self) -> None:
            lowered = {k.lower(): v for k, v in self.headers.items()}
            if classify(self.command, self.headers.get("Host", ""), self.path, lowered, bucket) == "deny":
                self.deny()
                return
            if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
                # The payload would have to be re-framed, which changes what the client signed.
                log(self.command, self.path, 501)
                self.send_error(501, "chunked request bodies are not supported by the test proxy")
                return
            length = int(self.headers.get("Content-Length") or 0)
            body = self.rfile.read(length) if length else None
            try:
                connection = http.client.HTTPConnection(upstream_host, upstream_port, timeout=300)
                connection.putrequest(self.command, self.path, skip_host=True, skip_accept_encoding=True)
                for name, value in self.headers.items():
                    if name.lower() not in HOP_BY_HOP and name.lower() != "expect":
                        connection.putheader(name, value)
                connection.endheaders(body)
                response = connection.getresponse()
                payload = response.read() if self.command != "HEAD" else b""
                status = response.status
                # The body was read whole, so the length is known even when the upstream sent it chunked; the connection is
                # kept alive and a client without a length would wait for the end of a body that never ends.
                headers = [
                    (k, v)
                    for k, v in response.getheaders()
                    if k.lower() not in HOP_BY_HOP and (self.command == "HEAD" or k.lower() != "content-length")
                ]
                if self.command != "HEAD":
                    headers.append(("Content-Length", str(len(payload))))
                connection.close()
            except OSError:
                log(self.command, self.path, 502)
                self.send_error(502)
                return
            log(self.command, self.path, status)
            self.send_response_only(status)
            for name, value in headers:
                self.send_header(name, value)
            self.end_headers()
            if payload:
                self.wfile.write(payload)

        do_GET = do_HEAD = do_PUT = do_POST = do_DELETE = forward

    return Handler


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int)
    parser.add_argument("--upstream")
    parser.add_argument("--log")
    parser.add_argument("--bucket", default="dit-smoke")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return
    if args.port is None or not args.upstream or not args.log:
        parser.error("--port, --upstream and --log are required")
    host, _, port = args.upstream.partition(":")

    server = ThreadingHTTPServer(("127.0.0.1", args.port), make_handler(host, int(port), args.log, args.bucket))
    server.daemon_threads = True
    server.serve_forever()


if __name__ == "__main__":
    main()
