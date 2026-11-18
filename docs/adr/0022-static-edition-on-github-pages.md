# ADR 0022: The static edition on GitHub Pages

## Status

Accepted by the owner on 2026-11-17. A design decision of Plan 5 that adds a second way to publish the site and
drops features of the first, so it needed the owner's approval.

## Context

The server edition needs a VPS, PostgreSQL, a Telegram bot and an operator. The owner wants the content and the
practice tools to be reachable without any of that, at the project URL
`https://platonenkov.github.io/dev-interview-trainer/`, hosted for free by GitHub Pages. Pages serves files only: it
sets no custom response headers, ignores query strings when choosing a file, answers `/x` with a redirect to `/x/`
when `x/` is a directory, and serves a project site under a path prefix.

Keeping two applications in step would double every change, so the static site has to come from the code that already
renders the pages.

## Decision

1. One codebase, two editions. `App:Edition` is `Server` (default, everything as before) or `Static`. The static
   site is produced by exporting the pages of a running instance in the `StaticExport` environment; `Static` is
   refused in every other environment except `Development` and `Testing`, so a production server can never run
   without sign-in by mistake (`EditionStartupCheck`).
2. The base path is a single setting. `App:PublicBaseUrl` may carry a path (`https://h.io/dev-interview-trainer/`);
   that path, without a trailing slash, is the request path base (`UsePathBase`, registered first) and is applied by
   one service, `SitePaths`. A custom domain later is a change of this one value. The path may not contain a query,
   a fragment or `.` and `..` segments (`PublicBaseUrlValidation`). `SiteUrls.Origin` is scheme, host and port only.
3. Page URLs of the static edition end with a slash and are written as `<path>/index.html`, because Pages redirects a
   directory URL without the slash and `/ru`, `/ru/dotnet` and `/ru/dotnet/questions` are both pages and directories.
   Canonical, `hreflang`, sitemap and Open Graph page URLs use the slashed form; file URLs (assets, sitemaps, images,
   data) keep their exact names (`SitePaths.Page` and `SitePaths.Asset`).
4. Lost in the static edition by design: Telegram sign-in, accounts and cross-device sync; the server profile, the
   server review history and the import of anonymous answers; like and dislike, in-app reports and the admin page
   (a report opens a prefilled GitHub issue); server-side catalog search and indexable `?level=` pages; response
   security headers (replaced by a meta CSP and a meta referrer policy); long-lived HTTP caching (replaced by a
   service worker cache); an effective `robots.txt`; the language cookie and the server root redirect; self-hosted Umami
   (replaced by GoatCounter). Practice, Mock, Review and the profile work from browser storage only.
5. Progress is stored for the origin `platonenkov.github.io`. Moving to a custom domain is a new origin and Pages
   redirects at once, so no script can carry progress across and visitors start fresh. The owner accepted this
   consequence; progress export and import is out of scope.

## Consequences

- Every URL the server writes must go through `SitePaths`; a hard-coded `/ru/...` in markup breaks under the prefix.
  Later tasks of Plan 5 route the existing call sites through it, and the export check fails on a link that leaves
  the prefix.
- The server edition is unchanged as long as its `App:PublicBaseUrl` has no path: `Page` returns the logical path
  untouched and `PathBase` is empty.
- The static edition is a degraded copy of the site on purpose. It never gets a sign-in button that cannot work: an
  anonymous-only page must hide or explain what needs an account.
- A custom domain costs every visitor their stored progress, so the move has to be announced in advance.
