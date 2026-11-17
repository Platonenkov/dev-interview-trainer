# ADR 0018: Page views are counted by self-hosted Umami, served same-origin, without cookies

## Status

Accepted. A design decision of Plan 3, taken by the designer inside the scope of the
deployment section of the spec; no spec text changes.

## Context

The owner needs to know which pages are read. The public pages are static SSR, output
cached and cookieless (ADR 0009, ADR 0012), and the CSP has no third-party origins and no
`unsafe-inline`. A counter must not add a cookie, a consent banner, a third-party origin
or a per-request value to cached HTML.

## Decision

- **Umami**, because it runs on the PostgreSQL server already in the stack, while Plausible
  Community Edition needs ClickHouse, which roughly doubles the VPS memory.
- **Same origin under `/stats`.** nginx proxies exactly two paths to Umami:
  `GET /stats/script.js` and `POST /stats/api/send`. The script is therefore covered by
  `script-src 'self'` and the beacon by `default-src 'self'`; the CSP does not change.
- **`data-host-url`.** Umami's tracker posts to `<host>/api/send`, and the application owns
  `/api`, so the script tag carries `data-host-url="{PublicBaseUrl}/stats"`.
- **No nonce.** A same-origin external script needs none, so the cached HTML of a public
  page is byte-identical for every visitor and the output cache is unaffected.
- **Privacy attributes.** `data-do-not-track`, `data-exclude-search` (query strings can
  carry a search term) and `data-exclude-hash`. Umami's tracker documentation says
  visitors are told apart without cookies; this ADR makes no other claim about browser
  storage, and the pinned release is to be checked in Task 10. `data-do-not-track` is
  documented as deprecated with limited browser support, so it is a courtesy, not the
  privacy mechanism.
- **Opt in by configuration.** `App:Analytics:WebsiteId` (a GUID). Empty means nothing is
  rendered; a value that is not a GUID fails startup. The script is not rendered on
  `/{culture}/admin`, `/{culture}/signin` and the per-user `/{culture}/{stack}/profile`.
- **Dashboard.** It is never proxied publicly. The owner reaches it through
  `ssh -L 3001:127.0.0.1:3001`.

## Consequences

- The About page states that page views are counted without personal identifiers.
- Blocking `/stats` (an ad blocker, a stopped Umami container) only loses counts; no page
  depends on the script.
- Umami adds one container and one database to the deployment (Task 10).
