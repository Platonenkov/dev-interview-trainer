# ADR 0015: The internal endpoints live on a separate port; the content directory is mounted read-only from its parent

## Status

Accepted. A design decision of Plan 3 (deployment), taken by the designer inside the
scope of spec 7.3 and 8.7.2; no spec text changes.

## Context

Content deploys without an application release: a workflow copies the content
checkout to the server and the running application imports it. The application
therefore needs an endpoint that starts an import and an endpoint that reports its
version and the active content, and the deploy script must be able to call them
without exposing them to the internet. Three questions needed an answer.

- Where the endpoints listen. A path on the public port, hidden by an nginx `deny`,
  stays reachable if the proxy rule is wrong or removed. A `Host` header check is
  worthless: the client sets it.
- How the content reaches the container. Mounting a release directory directly pins
  the mount to one inode; when a deploy switches a `current` symlink to a new
  release, the container keeps seeing the old directory until it is recreated.
- Where the import token comes from. Passing it from the GitHub workflow to the
  server would make the `deploy-content` job hold an application secret.

## Decision

- The application listens on a second Kestrel endpoint, `http://+:8081`
  (`Kestrel:Endpoints:Internal`), published only on the server's loopback. The
  routes `POST /internal/content/import` and `GET /internal/version` are mapped only
  when `App:Internal:Port` is positive, and `InternalPortFilter` answers an empty 404
  unless `HttpContext.Connection.LocalPort` equals that port. The check is on the
  socket, so `Host: x:8081` sent through nginx to the public port cannot reach them.
  The filter also switches the status-code pages feature off for the request, so the
  404 and the 401 stay empty instead of becoming the HTML not-found page.
- The import requires `Authorization: Bearer <token>`, compared in constant time with
  `ContentImport:Token` (environment variable `ContentImport__Token`, at least 32
  characters). Production refuses to start without an internal port and a token. The
  version endpoint carries no token: the port is the boundary and its values are not
  secret. It reports the active content hash so a script can tell whether an import
  call that failed on the wire had committed.
- The import runs under the host's stopping token, not the request's, so a client that
  disconnects or times out never cancels an import half-way; a retry waits on the
  importer's advisory lock and gets a definitive answer (200 or 422). An import cut
  short by the host shutting down answers 503, which is retryable: the retry goes to
  the next process.
- Every other `/internal/*` request, whatever the method and including unknown paths,
  answers an empty 404 on every port, so the prefix never falls through to the HTML pages.
- The container mounts the parent directory of the content releases read-only, and
  `App:ContentRoot` points at the `current` symlink inside it. A switch of the symlink
  is then visible to the running application without recreating the container.
- `receive-content.sh` on the server reads the token from the server's `.env`, which
  is rendered from the GitHub secret during the application deploy, instead of
  receiving it from the workflow. The `deploy-content` job needs no application secret.

## Consequences

- The deployment must publish port 8081 to loopback only and must not proxy it.
  Misconfiguring that exposes a 404 and a token check, not an unauthenticated import.
- Tests cannot open sockets in the in-memory server, so they inject the local port from
  a request header (`LocalPortFromHeaderStartupFilter`, registered only by the test
  factory); the published-build suite runs with a real second URL.
- Rotating the import token is a change of the GitHub secret and a redeploy of the
  application, which rewrites `.env`; there is no second credential to keep in step.
- A host whose configuration has no internal port runs normally outside Production,
  and the routes do not exist there (they are not mapped, so the paths are ordinary 404s).
