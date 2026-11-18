# ADR 0025: GoatCounter counts page views in the static edition

## Status

Accepted by the owner on 2026-11-17 as part of the static edition design (decision 5 of its decisions list). The
self-hosted, reduced script below replaces the first implementation (the vendor's script from its CDN, pinned by SRI),
which review found to send the page's query string; the change is an implementation detail of Plan 5.

## Context

The server edition counts page views with self-hosted Umami served from the site's own origin (ADR 0018). GitHub Pages
has no server to run it on. The owner accepted a third-party counter that sets no cookie and keeps no personal data, and
the design promises that search terms never leave the browser.

The vendor's `count.v4.js` cannot keep that promise. Its `get_data` always sends `q: location.search`, whatever the
`path` setting returns, and `r` defaults to `document.referrer`, which for a same-origin navigation is the full previous
URL. The static catalog filters through a `GET` form, so `?q=<term>` is in the URL of every search.

## Decision

1. The static edition uses GoatCounter and the server edition keeps Umami; each ignores the other's setting
   (`App:Analytics:GoatCounterCode` against `App:Analytics:WebsiteId`). The code is the site's subdomain, matching
   `^[a-z0-9-]{2,63}$`; a malformed value fails startup. An empty code renders no script and puts no GoatCounter origin
   into the CSP. The code is a public value, supplied by the export's configuration (a repository variable).
2. The counter is self-hosted: `wwwroot/js/goatcounter.js`, a fingerprinted asset like every other script, a reduced
   copy of the vendor's `count.v4.js`. The licence is ISC (stated in the file header of the vendor's script), which
   allows modification and redistribution; the header is kept in the copy together with a list of the changes. The
   base is v4 because it is the version whose bytes GoatCounter documents as never changing, and the one reviewed;
   `count.v5.js` exists and is not used. A newer version is a deliberate update of this record and of the copy.

   | Upstream file | Value |
   |---|---|
   | URL | `https://gc.zgo.at/count.v4.js` |
   | Size | 9119 bytes, downloaded 2026-11-18 |
   | SHA-384 | `sha384-nRw6qfbWyJha9LhsOtSb2YJDyZdKvvCFh0fJYlkquSFjUxp9FVNugbfy8q1jdxI+` (equals the value GoatCounter publishes) |
   | SHA-256 | `b9f5c4c04edb8802d7b73f479a2f532f52367b633b56a09a224fc659fde86c03` |

   The hashes were computed on the public file with `openssl`; nothing about the site, the owner or a visitor was sent
   anywhere to obtain it.
3. Changes against the upstream file:
   - The `q` field is not sent.
   - The path is the page path only. Whatever the settings callback or the page produces is cut at the first `?` or `#`,
     and the canonical-link lookup (which carried the query) is removed.
   - The referrer is sent only when it comes from another origin and then as that origin alone; a same-origin,
     malformed or non-http(s) referrer is dropped.
   - `navigator.doNotTrack === '1'` suppresses the hit, as `data-do-not-track` does for the server edition's Umami tag.
   - Events, the visitor-counter widget, the `data-goatcounter-settings` attribute and the "toggle" hash are removed;
     the title, screen size and bot score are kept as in the upstream script.
4. Markup at the end of `<body>`: `js/goatcounter-settings.js` (reports `location.pathname`), then
   `js/goatcounter.js` with `data-goatcounter="https://<code>.goatcounter.com/count"`. Both are same-origin, so no
   third-party script origin is in `script-src`; `connect-src` names only `https://<code>.goatcounter.com`, where the
   beacon goes. The sign-in, admin and profile pages are excluded as in the server edition, and the root chooser (a
   redirect hop) is not counted.
5. The recovery script does not watch the counter (`data-recover` is absent from it): content blockers refuse it, and a
   blocked counter must not show the visitor an error banner.
6. `GoatCounterScriptTests` run the script under Node against a fake browser and assert that the outgoing beacon has no
   `q`, no same-origin referrer and no query or hash in the path, and that Do Not Track suppresses it.

## Consequences

- A visitor with a content blocker is not counted; the numbers are a lower bound.
- The copy does not follow GoatCounter's changes. That costs a manual update when the vendor ships something worth
  having, and removes both the SRI-rot failure (a changed file on the CDN silently blocked) and a third-party script
  that runs with the page's rights.
- The beacon still reveals the visitor's IP address to goatcounter.com, as any request does, and the page title, which
  is the title of a public page.
- The About page names GoatCounter (cookieless, goatcounter.com) as the static edition's privacy text; it stays
  accurate: no cookie is set and no search term, query or same-origin address is sent.
