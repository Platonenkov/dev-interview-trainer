# ADR 0026: A content-addressed service worker restores immutable caching on GitHub Pages

## Status

Accepted by the owner on 2026-11-17 as part of the static edition design (section 5.9).

## Context

GitHub Pages sends `Cache-Control: max-age=600` for every file and cannot be configured. The fingerprinted runtime,
stylesheets, scripts, fonts and content shards are immutable by name, but the browser revalidates each of them after ten
minutes, one round trip per file, which is the opposite of the warm target of the island pages (docs/performance.md).

A service worker can answer those requests from its own cache. The usual way (a precache list per build, one cache per
generation, a migration step when the list changes) ties the worker to the build and makes every deployment a worker
update with state to reconcile. This site has no use for that: the files are already content-addressed.

## Decision

1. **Eligibility is a path pattern, and the build makes it sound.** A request is cached when its path under the scope
   matches `StaticAssetRules`: a fingerprinted asset (`name.<fingerprint>.ext`, a fingerprint being exactly 10 characters
   of `[a-z0-9]` between dots, as recorded by the spike) or a content shard (`data/.+\.[0-9a-f]{16}\.json`). The rule lives
   in `Trainer.Web` only; the worker receives it as a regex literal prepended to `sw-template.js` by `GET /sw.js`
   (`const ELIGIBLE = /.../;`), and the exporter receives the same text through the export manifest. The export verifier
   (Task 16) enforces the other direction: every published file that matches is in the build's immutable set, nothing
   mutable (HTML, `build.json`, `immutable.json`, `sw.js`, unfingerprinted aliases) matches, and a name present in both the
   retained previous build and the new one has identical bytes. A name therefore means the same bytes in every
   generation, so any active worker may cache any build's file and no per-build allowlist or version exists. A query
   string makes a request ineligible.
2. **One cache, `dit-immutable-v1:<scope path>`, cache first for eligible `GET` requests without a `Range` header.** Anything
   else is not intercepted, except navigations (7). Only a good response is stored: `ok`, status 200, not redirected,
   type `basic` or `default`, and `response.url` equal to the requested URL. A 404, a 503, an opaque or a redirected
   response goes to the page and is never stored, so the next request reaches the network and a transient failure heals
   without clearing storage. Writes are best effort: a quota or write error is swallowed after the response is already on
   its way to the page.
3. **Least recently used pruning with two budgets**: runtime and assets by `Content-Length` (40 MB), content shards by
   count (600 entries). Recency is held in memory and persisted at most once a minute as the JSON entry `__recency` in the
   same cache (never eligible, so never served); a missing or damaged record makes every entry count as the oldest. One scan
   of the cache per worker lifetime makes the record mirror the cache; after that a store only consults the record, and a
   prune runs when a budget is exceeded and evicts that budget down to 90%, so a full cache does not scan on every store.
   Content shards over their limit never evict runtime files, and the reverse. The measure is the brief's: `Content-Length`
   counts compressed bytes, so on-disk use may exceed 40 MB. Recency newer than the last persist is lost when the worker
   stops, so those entries count as oldest after a restart.
4. **`skipWaiting()` on install, `clients.claim()` on every activate.** Both are safe because names are
   content-addressed: an old page served by a new worker still gets exactly the bytes it names, whichever generation the
   worker or the page belongs to. The worker keeps no install-time state to decide whether to claim.
5. **No precache list; the page tells the worker what it already loaded.** `sw-register.js` registers after `load`. When a
   worker controls the page (now, or on `controllerchange` after the first claim) it posts the same-origin URLs under the
   scope from Resource Timing, and posts again on `dit:island-usable`, which `IslandRoot` raises once when the island first
   shows something to act on. The worker decides which of them are eligible, fetches those it does not have inside
   `waitUntil` and stores the good responses. Those fetches are normally answered by the HTTP cache (the files were loaded
   seconds earlier and are fresh for ten minutes), so the extra network traffic of a first visit is close to zero and at
   most the files the page itself loaded. The first return visit is warm. A page request and a fill for the same URL share
   one network fetch (the later one clones the earlier one's response). Resource Timing's default buffer of 250 entries is
   ample: the spike measured 63 requests for an island page. A URL whose entries all carry an error `responseStatus` (400 or
   more: a 404 after a redeployment, a 503) is not posted, because the fill would ask the network for it again at once, a request
   the page did not make; it is posted when the page is later answered well (final review).
6. **The worker owns the rule.** The page pre-filters only by origin, scope and the absence of a query, so the rule is
   never duplicated in a second script that could drift.
7. **Navigations use navigation preload with a correct fallback.** `activate` enables it where the browser has it; the
   navigation handler awaits `event.preloadResponse` and answers with it, or with `fetch(event.request)` when it resolves
   to nothing or rejects. Worker start-up stays off the critical path. HTML is never cached: there are no offline pages.
8. The edition sets no cookie and the meta policy already allows the worker (`worker-src 'self'`, `script-src 'self'`).
   The registration script is on every island and public page; the root chooser, a redirect hop, does not register.

## Consequences

- A warm visit loads the runtime, styles, scripts, fonts and content from the worker's cache instead of revalidating them
  every ten minutes. The measured effect belongs to the live checks (Task 17).
- Deployment needs no worker versioning. Changing the rule or the template changes `sw.js` and the browser installs the
  new worker; the text does not change between builds otherwise.
- Soundness rests on the export verifier, not on the worker: a published mutable file that matches the pattern would be
  cached forever. The verifier fails the build for it.
- Storage the site occupies is bounded (about 40 MB plus 600 small files) and shrinks by recency, not by deployment.
- Browsers without service workers, private windows that refuse them, and a worker that fails to register behave as the
  site did before: every failure of the worker path is logged and ignored.
- **Reset.** A visitor clears the site's data, or unregisters the worker under the browser's application tools. The
  maintainers bump the `v1` in the cache name to abandon every cache at once (the old cache is not deleted automatically;
  the browser's storage pressure reclaims it).
