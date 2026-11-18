# Dev Interview Trainer: Static Edition on GitHub Pages

Status: draft for owner approval, revised after the Codex and Fable reviews · Date: 2026-11-17 · Repository path when
committed: `docs/superpowers/specs/2026-11-17-static-edition-design.md`

This document extends `docs/superpowers/specs/2026-11-11-dev-interview-trainer-design.md` (called "the main spec"
below; section numbers in the form "main 8.2" refer to it). Everything the main spec says still holds for the server
edition. Where the static edition deviates, this document says so and ADR 0022 records it.

## 1. Intent

Ship the site now without a server. A second, fully static edition of the same application is built in CI from the
same repository and published on GitHub Pages at the project URL. Visitors get the same public pages (home, catalog,
topics, questions, about) and the same learning modes (Practice, Mock, Review, profile), with progress kept in the
browser. The server edition (Plan 3: Docker, VPS, PostgreSQL, Telegram sign-in, feedback, Umami) stays in the
repository, builds and tests as before, and can be switched on later.

Why a static edition:

- The VPS, its domain, backups and operations are a cost and a commitment the owner does not want to take yet.
- Almost everything a visitor does already works without an account: the public pages are pre-rendered HTML, the
  islands grade answers in the browser, and anonymous answers are kept in `localStorage` (main 8.8).
- GitHub Pages is free for a public repository, needs no operations, and the repository is going public anyway.

## 2. Goals, non-goals, anti-goals

### Goals

1. One build setting (the public base URL) produces a complete static site that works under
   `https://platonenkov.github.io/dev-interview-trainer/`: every link, asset, Open Graph URL, sitemap entry and the
   Blazor WebAssembly base path carry the `/dev-interview-trainer/` prefix. Everything else the build needs (the
   crawler's prefix, the preview and smoke inputs) is derived from that setting, so a custom domain changes one line
   (with the consequence for visitors' progress stated in 5.6).
2. Static HTML for every public URL, rendered by the same Razor components as the server edition, so SEO metadata,
   localisation and markup are identical apart from the documented static-edition differences.
3. Practice, Mock, Review and profile work for every visitor from browser storage, with the same grading, the same
   SM-2 scheduling and the same session selection rules as the server edition.
4. Content is published as small, content-hashed JSON files per language: an index per stack and one prebuilt file per
   question, produced by the same validation and rendering pipeline as the server import. A session downloads only
   the questions it shows.
5. Main 8.2's phone targets hold on the deployed static site: Practice usable (5.9) within 2 s warm and 5 s cold on a
   mid-range phone over 4G, measured with `?perf=1`.
6. The repository becomes public safely: the existing hardening checklist plus an audit of everything that becomes
   public (all refs, Actions logs and artifacts, issues and pull requests, packages), run as owner-confirmed steps.
7. CI builds, verifies and deploys the static site after a successful `ci` run of `main`, never deploying an older
   commit over a newer one, with pinned actions and minimal permissions.
8. A deployment from build A to build B never strands a visitor: pages of A keep working for a defined window, and
   anything older recovers with one reload (5.13).

### Non-goals

- Removing, rewriting or redeploying the server edition. Its code, tests, Dockerfile, `deploy/` and workflows stay;
  its SQL session queries stay as they are; shared code it uses changes only where this document says so.
- Accounts, cross-device sync, server-side feedback, like and dislike, the admin page and server-side review history
  in the static edition.
- Progress export and import, and therefore keeping progress across an origin change (5.6, owner decision).
- Offline-first operation. The service worker of 5.9 caches only immutable files.
- A JavaScript search index or client-side full-text search beyond filtering the catalog list.
- Scheduled or weekly CI jobs of any weight for the static edition.

### Anti-goals (would make the static edition a failure even if it works)

- **A broken link or asset under the prefix.** One URL that escapes the prefix is a 404 on Pages. Structural checks of
  every exported file and the browser's own request log must fail the build instead.
- **A sign-in-only feature that looks available and then fails.** Anything that needs the server is hidden or
  explained, never a dead button.
- **A mixed or stranded state after a deployment.** A visitor never sees an index from one build and question files
  from another; a page older than the retention window says "the site was updated" and recovers with a reload.
- **Silent loss of progress.** When browser storage is unavailable or full, the islands say so (the existing
  `StorageNotice`). Loss through clearing site data, Safari eviction or an origin change is documented, not silent.
- **A fat or slow first load.** One language's whole bank is about 500 KB gzip; downloading it to start a session would
  break the cold target. Start-up cost must also not grow with a visitor's history.

## 3. Owner decisions (binding, quoted)

1. Hosting: "GitHub Pages at the project URL https://platonenkov.github.io/dev-interview-trainer/ - every link,
   asset, OG URL, sitemap and the Blazor WASM base path must work under the /dev-interview-trainer/ prefix. Keep the
   base path a single build setting so a custom domain later is a one-line change."
2. "The repository will be made public (code MIT, content CC BY-SA 4.0) - the plan must include a task that runs the
   existing "Hardening checklist before the repository goes public" in docs/deployment.md (and anything else needed:
   secret scan of history, settings) as owner-confirmed steps. Making it public is the owner's own click."
3. "Content is NOT shipped as one big JSON. Per language: a small index file (ids, stack, topic, level, title, whatever
   session selection needs) plus one prebuilt JSON per question (question, answer, already-rendered and sanitized
   HTML), produced by the same validation/render pipeline the server import uses today, published on Pages itself
   (never fetched from raw.githubusercontent.com). File names or URLs carry a content hash so they can be cached long
   and a content update never serves a mix. Sessions fetch only the selected questions and prefetch the next one.
   Measured: content is 392 questions, 3.7 MB YAML raw, ~1 MB gzip for both languages; so one language's whole set is
   ~500 KB gzip, which is why it must be split."
4. "Feedback: no server. "Report a problem" opens a prefilled GitHub issue (new-issue URL with title/body/labels
   containing question id, language, and the page URL). No like/dislike in the static edition."
5. "Analytics: GoatCounter (cookieless, script allowed in CSP). Self-hosted Umami is not used by the static edition."
6. "Lost in the static edition (by design): Telegram sign-in, server sync, server profile, admin, server-side review
   history. Practice, Mock, Review and Profile work from browser storage only (the code already has client storage:
   IBrowserStore, OutboxStore, MockPersistence). Anything that requires sign-in must be hidden or explained, not
   broken."
7. "No scheduled/weekly heavy CI jobs."

## 4. Constraints

- Everything in the main spec section 3 and in `AGENTS.md` applies: .NET 10, xUnit v3 with the built-in `Assert`, no
  FluentAssertions, Conventional Commits, English code and docs, no AI attribution, no long dashes in docs, RU and EN
  strings kept in sync, WCAG 2.2 AA.
- Dependency rules stay: Domain and Contracts reference nothing; Web.Client references Domain and Contracts only; Web
  never references Snippets. `Trainer.Contracts` ships to the browser, so host-only types stay out of it.
- GitHub Pages serves files only. Observed on 2026-11-17 on `*.github.io`: `Cache-Control: max-age=600` on every
  response, `Vary: Accept-Encoding`, gzip on HTML and JavaScript, small `.wasm` files (under 100 bytes) uncompressed,
  no `X-Content-Type-Options`, `Referrer-Policy` or `X-Frame-Options`, a directory path without a trailing slash
  answered with a 301 to the slashed path, `<site>/404.html` for a missing path. Pages never serves precompressed
  `.br`/`.gz` files. Whether it gzips large `.wasm` files is not yet proven (Task 1 of the plan settles it before any
  deployment).
- `platonenkov.github.io` is shared by every Pages project site of the account: `localStorage`, Cache Storage and
  service worker registrations are per origin, not per project.
- A project site's `robots.txt` (`/dev-interview-trainer/robots.txt`) is not read by crawlers; only the origin root's
  is.
- GitHub Pages for this repository requires it to be public (or a paid plan); the first deployment follows the going
  public task; everything before the deploy job runs in `ci.yml` on every pull request, so it is proven while the
  repository is private.
- Publishing is deterministic: `-p:IncludeSourceRevisionInInformationalVersion=false` and a constant
  `-p:InformationalVersion=static` for every static build, so the assemblies carry no commit and a content-only change
  keeps every runtime and asset name and byte. The deployment's identity lives outside the assemblies: a build id
  (`<commit>.<run id>.<attempt>`) written into `build.json` (build id, commit, build time), into `immutable.json` (build
  id and the complete list of immutable files) and into a `<meta name="dit-build">` element of every exported page.
- The public site URL is one repository variable, `SITE_URL`, read by every workflow that builds or deploys the site;
  no workflow file contains the URL.

## 5. Design

### 5.1 One application, two editions

`App:Edition` selects `Server` (default, everything as today) or `Static`. Static is allowed only in the
`StaticExport`, `Development` and `Testing` environments; startup fails anywhere else, so a production server can
never expose the export manifest or run without sign-in by mistake.

`App:PublicBaseUrl` may now carry a path. Its path, without a trailing slash, is the request path base
(`UsePathBase`), and every URL the server writes goes through one service, `SitePaths`. The static build sets
`App:PublicBaseUrl=https://platonenkov.github.io/dev-interview-trainer/`; the server edition's value has no path, so
its URLs do not change. Inside the server, logical paths (culture-rooted, unprefixed, unslashed) stay the currency;
only markup and absolute URLs are converted.

In the static edition page URLs end with a slash (`/dev-interview-trainer/ru/dotnet/questions/`) and the export writes
each page as `<path>/index.html`: Pages answers `/x` with a 301 to `/x/` when `x/` is a directory, and `/ru`,
`/ru/dotnet` and `/ru/dotnet/questions` are both pages and directories. Canonical, `hreflang`, sitemap `loc` and Open
Graph page URLs use the slashed form; file URLs (sitemaps, images, assets, data) keep their exact names.

Only island pages carry `<base href="<prefix>/">` (as today with `/`); public pages have none, so their CSP keeps
`base-uri 'none'`. Fragment links on island pages are written with the current page's prefixed path (the existing
skip-link rule), so they never resolve against the base.

### 5.2 Rendering and export

The static HTML is produced by the real application, not by a second renderer: CI publishes `Trainer.Web` in Release
(deterministic, with the pinned `wasm-tools` workload), applies the migrations to a throw-away PostgreSQL started
from the Compose pin, runs the published app from its publish directory with `App:Edition=Static` in the
`StaticExport` environment, waits for `/health/ready` (the startup import validates and renders the content and
enforces the content schema range exactly as in production), and runs a crawler, `tools/Trainer.StaticSite`, that
saves every response under the site prefix.

Why not a dedicated `HtmlRenderer`: the pages depend on the HTTP pipeline (culture middleware, status code pages,
request localisation, `HttpContext`, router and `NavigationManager`, the static asset endpoints behind `@Assets`) and
on the EF Core query services; a second renderer would need in-memory copies of all of them and could drift from what
the server edition renders and tests. Running the app costs one PostgreSQL container and about a minute in CI.

The mechanism is proven before anything is built on it: the first plan task takes a published Release island page and
its assets, serves them from a plain static file server under the prefix with the ASP.NET process stopped, and
records the network log, the CSP result and the real compressed sizes. The island boot does not import
`_framework/resource-collection.*.js`, because `IslandEndpointConventions.KeepResourceCollectionOnServer` already
keeps the resource collection URL out of the persisted state; the spike confirms that no other runtime-generated
endpoint is requested.

What the crawler fetches is listed by the app itself (`GET /static-export/manifest.json`, mapped only in the static
edition, written from a host-side record): every page path in both cultures (sitemap entries, retired question pages,
island pages, about, root), the redirect sources of renamed slugs, the sitemaps and `robots.txt`, the not-found page,
the content indexes, the service worker, and every static asset route of the app's own endpoints manifest except the
precompressed `.gz`/`.br` variants. Open Graph images are discovered from each saved page's `og:image`, question files
from each index. Redirects are captured with automatic redirects off and become a small HTML page (meta refresh to the
validated, escaped same-site target, canonical link, `noindex`, the static CSP and referrer meta); the not-found page
is fetched expecting 404 and saved as `404.html`.

Two origins are kept apart. The public site URL (`App:PublicBaseUrl`) is what every saved file says; the transport
URL (`http://127.0.0.1:5080/<prefix>`) is where the crawler fetches. Any absolute URL the crawler meets (an
`og:image`, a redirect `Location`) is accepted when it is under the public site URL or under the transport URL, and is
fetched by mapping the public prefix onto the transport prefix; the crawler never contacts the live Pages site, except
the retention step of a real deployment (5.13), which uses its own client.

After the crawl the exporter verifies the site structurally: HTML and XML are parsed (not regex-scanned), entities
decoded and URLs normalised against the document's base before the prefix check; every same-site URL in `href`,
`src`, `srcset`, `action`, `og:*`, canonical and alternate links, import maps, CSS `url()`, sitemap `loc` and
`xhtml:link` must resolve under the Pages rules; every HTML file, including redirect pages and `404.html`, carries
the CSP meta as the first `<head>` element; the persisted component state names no URL outside the prefix. The
browser tests add the runtime view: every request the browser makes is under the prefix or is the allowed GoatCounter
script and endpoint. One problem fails the build.

The root (`/dev-interview-trainer/`) is a small language chooser: an external script picks the stored choice or the
browser's preferred language and replaces the location; without JavaScript it shows the two links.

### 5.3 What changes on the pages

| Page or element | Server edition | Static edition |
|---|---|---|
| Header account area | Sign-in link or account name and sign-out | A "Progress" link to the profile page; no sign-in |
| Language switcher | `/lang/{target}?return=...` (sets the `lang` cookie) | Direct link to the page in the other language; the choice is remembered in `localStorage` for the root chooser |
| Catalog | Server-side filters, search and 25-row pages | Every row of the stack on one page; the filter panel is a `GET` form of checkboxes and the search box; `catalog-filter.js` applies `level`, `topic`, `type` and `q` from the query string with the server's rules (title substring or exact tag) and updates the count; without JavaScript the full list shows |
| Question page feedback | Like, dislike, report forms for signed-in users | "Report a problem" link to a prefilled GitHub issue, for every visitor |
| Home Review card | "Sign in to review" | "Questions you answered come back here when they are due" |
| Review, profile pages | Require sign-in | Open to everyone, read browser storage |
| About | As today | One more paragraph: progress lives in this browser, how to report a problem |
| Practice and Review feedback bar | Like, dislike, report | "Report a problem" link only |
| Profile import panel, account notices, failed summaries | Signed-in only | Never rendered |
| Analytics | Umami (same-origin, ADR 0018) | GoatCounter |
| Security headers | Response headers | `<meta http-equiv="Content-Security-Policy">` and `<meta name="referrer">` (5.8) |
| Theme preference key | `theme` | `dit.theme` in both editions (the bare key collides with neighbouring sites; the server edition has no deployed users) |

Sign-in pages, the admin page, `/api`, `/lang`, `/signin-telegram` and `/internal` are not exported.

### 5.4 Content shards

For each culture and stack the app builds, from the imported database, an index and one file per live question:

- `data/{culture}/{stack}/index.{hash}.json`: format version, culture, stack, the content hash of the import, topic
  titles, and one entry per live question with a locale for the culture: `id`, `slug`, `topic`, `level`, `type`,
  `title` and the 16-character hash of its question file (the client derives the path
  `data/{culture}/q/{id}.{hash}.json`).
- `data/{culture}/q/{id}.{hash}.json`: exactly the `QuestionDto` a server session returns today, built by the same
  `SessionRows.ToDto` from the same imported rows, so its HTML is the importer's rendered and sanitised HTML.

`{hash}` is the first 16 lowercase hex characters of the SHA-256 of the file's bytes; serialisation uses the
source-generated `TrainerJsonContext` and a fixed order, so the same content always yields the same names. The island
page bootstrap names the index path, so a page and its index come from the same build.

Sizes, estimated from the entry shape: about 200 bytes per index entry, about 80 KB raw and 25-35 KB gzip for 392
entries (ids and hashes do not compress), still far below the 500 KB the owner ruled out; a question file is 2-10 KB
before gzip. The static smoke run reports the real index size.

### 5.5 Islands without a server

The WebAssembly client gets one seam, `IIslandBackend`, with two implementations: `ServerIslandBackend` (today's
`TrainerApiClient` calls and today's outbox and pending-answer rules) and `StaticIslandBackend`. The edition is read
before the WebAssembly host is built, through a `[JSImport]` call like `PageCulture`'s, from `<html data-edition>`,
which only the static edition renders (a missing attribute means the server edition, so server HTML is unchanged).
The backend also owns what differs between the editions inside the islands: which outbox key answers go to, whether
answers still waiting in the outbox are filtered out of Review and excluded from Practice `new`, and whether progress
is available without a sign-in.

Session selection rules shared with the server live in `Trainer.Domain/Sessions/`: `MockComposer` (moved from
Application, used by both editions) and new in-memory equivalents of the server's practice pool and review order
(`PracticeSelector`, `ReviewOrder`) used by the static edition. The server keeps its SQL queries unchanged; a parity
test runs both over the same fixture for every status and filter combination, so the rules cannot drift.

Practice and Review load the session plan (the chosen ids) from the index, fetch the first question, show it, and
prefetch the next while the visitor answers. Between questions on a slow link the island shows a fixed-size skeleton
with an `aria-live` "Loading the next question" message; a failed load shows the offline state with a retry button
that keeps the results so far. Mock fetches all its chosen questions (at most 50) before the timer starts, because its
jump strip, timer and restore need them all. Practice and Mock read their filters from the page's query string in the
static edition, with the same normalisation as `CatalogQueryParser`.

### 5.6 Progress in the browser

In the static edition the anonymous outbox (`dit.outbox.v1.anon`) receives every answer of Practice, Mock and Review,
is never sent anywhere and is never trimmed: it is the full answer log, and the only progress record. Nothing derived
is stored. Progress is recomputed from the whole log on read: every answer, of every stack and language, is replayed
through the domain `Sm2ReviewScheduler` ordered by each answer's effective time, then client answer id. The effective
time is fixed once, when the answer enters the log: it is the answer's `AnsweredAt`, taken from the device clock at that
moment (the static edition's "server clock"), so it is never in the future of the clock that accepted it; replay never
re-clamps or moves it. A device clock corrected later therefore cannot shift any anchor on reload: the same log gives
the same states on every day and every reload, as stored answers do on the server after `AnswerRecorder` clamped them
once at ingestion. Because the full log is replayed
every time, a late or backdated answer simply takes its place in the order; there is no irreversible state to
reconcile. Review states are kept for every question id in the log; the current stack's and language's index filters
them only for selection and display, so switching stacks or languages, or a question missing in one language, never
loses or overwrites anything. The result is memoised per page and recomputed when this tab writes an answer or
another tab changes the log; the latter is a new `storage` event listener on the anonymous outbox key in `outbox.js`
(today only `dit.account` is watched), surfaced as an `OutboxStore` event.

**Amended in the fix round of Task 17 (ADR 0028).** Measured on the emulated phone, reading a log of ten thousand answers
into .NET (parse, one DTO per entry, ids) cost 6 to 9 seconds of a load and the replay itself only about one, so "nothing
derived is stored" gave way to a checkpoint of the replayed result, written next to the log and trusted only for the log
state it was written for (a native fingerprint of the entry count and the tail). The log stays the only source of truth, and
the reasons below for not folding answers out of it still hold: the checkpoint never removes anything.

Why no snapshot (designer's call): a snapshot that removes answers from the log needs a read-compute-commit under one
lock with revision checks, and an irreversible fold breaks chronological replay for late answers; the full log is
obviously correct and small enough. Estimate: an answer entry is about 200 bytes of JSON; a committed visitor answering
30 questions a day for 300 days writes 9 000 answers, about 1.8 MB; the cost is one JSON parse and 9 000 scheduler
steps. It runs before the island is declared usable, because session selection (`new`, `due`) and the sidebar need it,
and it is inside the measured time (5.9) with a 10 000-answer budget test. The hard limit is the origin's 5 MB `localStorage` quota (shared with the owner's other Pages sites):
around 20 000 answers the existing "storage full" notice appears; a compaction design is left to a later plan if real
visitors ever approach it.

From the replayed states and the index the client computes the status filters (`new`, `due`), the Review session (no
"pending" filtering: in the static edition every answer is already applied locally), the topic progress, the streak
(`StreakCalculator`, in the device's local time) and the profile. Finished mock summaries are kept locally
(`dit.mock.v1.anon.history`, newest 20).

Limits, shown on the profile page and in `docs/static-edition.md`: progress is per browser and device; clearing site
data deletes it; Safari deletes script-written storage of a site not visited for 7 days; the origin's 5 MB storage is
shared with the owner's other Pages sites.

**Origin change (owner decision 2026-11-17, accepted consequence):** progress is stored for the origin
`platonenkov.github.io`. Moving to a custom domain is a new origin and Pages redirects at once, so no script can carry
progress across: visitors start fresh. The owner accepted this; there is no progress export or import. ADR 0022 and the
custom-domain procedure in `docs/static-edition.md` record it, and the procedure tells the owner to announce the move.

Storage keys stay `dit.*` (no per-prefix namespace): only one deployment of this application lives on the origin, and
a namespace would need a migration of existing progress for no current benefit. Cache Storage names do carry the
service worker's scope (5.9), because they are created fresh.

### 5.7 Feedback

"Report a problem" on the question page and in Practice and Review opens
`https://github.com/Platonenkov/dev-interview-trainer/issues/new` with the issue form template
`question-report.yml`, a title naming the question, and the form fields `question-id`, `language` and `page-url`
prefilled; the visitor picks a reason from the same fixed list as the server edition (wrong answer, unclear wording,
outdated, broken link, other) and may add details. The label `content-report` comes from the template, because a
`labels=` URL parameter is applied only for users with triage rights. A plain link: it works without JavaScript and
needs no CSP change.

### 5.8 Security headers and CSP

Pages cannot set headers, so every exported HTML file starts its `<head>` with a CSP meta element, before the inline
theme script:

- `default-src 'self'; script-src 'self' <theme and recovery script hashes> https://gc.zgo.at; style-src 'self';
  img-src 'self' https: data:; font-src 'self'; connect-src 'self' https://<code>.goatcounter.com; worker-src 'self';
  base-uri 'none'; form-action 'self'` on public pages, redirect pages, `404.html` and the root chooser;
- island pages add `'wasm-unsafe-eval'` and the SHA-256 of their import map to `script-src`, and `base-uri 'self'`.
  The import map's nonce (ADR 0012) is replaced by its hash, because a static file cannot carry a per-request nonce.

`<meta name="referrer" content="strict-origin-when-cross-origin">` replaces the `Referrer-Policy` header. In the
static edition the app sends neither the CSP nor the other security headers itself, so a local static run behaves
like Pages.

Lost on Pages, by design and recorded in ADR 0024: `frame-ancestors` (meta CSP cannot carry it, and Pages sends no
`X-Frame-Options`, so the site can be framed), `X-Content-Type-Options: nosniff`, CSP reporting, and HSTS
configuration (github.io is on the browsers' HSTS preload list, so HTTPS is still enforced). There is no cookie, no
form that posts, no token and no account, so framing gives an attacker nothing to click on.

### 5.9 Caching, compression and performance

**Measured state.** "Practice usable" in the static edition is the moment the Practice island can start a session
with any filter: the filter form is interactive, the index is loaded and the progress replay (5.6) is complete, so a
click on Start with `all`, `new` or `due` selects at once. For a `?first=` link, usable is the first question on
screen: it is fetched in parallel with the index and does not depend on progress, and the rest of that session is
selected when the replay completes, before the visitor can press Next. `data-island-ready` is set at the usable moment;
the shell-ready moment is recorded separately in the `?perf=1` readout. The phone targets apply to the usable moment.

**Service worker.** Pages caches every file for 10 minutes, then revalidates. A service worker (`sw.js`, scope
`/dev-interview-trainer/`) restores immutable caching with a content-addressed model that has no generations:

- Eligibility is a path pattern, and the build makes the pattern sound: a request is eligible when its path, under the
  scope, is a fingerprinted asset (`name.<fingerprint>.ext`, with the fingerprint format recorded by the spike) or a
  content file (`data/...<16 hex>.json`). The export verifier enforces that every published file matching the pattern
  is in the build's immutable set, that nothing mutable (HTML, `build.json`, `immutable.json`, `sw.js`, unfingerprinted
  asset aliases) matches it, and that a name present in both the retained previous build and the new build has
  identical bytes. A name therefore always means the same bytes, in every generation, so any active worker may cache
  any build's files.
- Only good responses are stored: `response.ok`, not redirected, type `basic` or `default`, for exactly the requested
  eligible URL. A 404, a 503, an opaque or a redirected response is passed to the page and never cached, so the next
  request goes to the network again and a transient failure heals without clearing storage.
- One cache (`dit-immutable-v1:<scope>`), cache-first for eligible GET requests, network otherwise. Writes are
  best-effort (a quota or write error never fails the response). Pruning is least-recently-used with two budgets, about
  40 MB for runtime and assets and 600 entries for content files; recency is kept in a small record the worker updates
  in memory and persists at most once a minute, best-effort.
- Lifecycle: `install` calls `skipWaiting()`, `activate` calls `clients.claim()`. Both are safe because URLs are
  content-addressed: an old page served by a new worker still gets exactly the bytes it names. There is no precache
  list. The page registers the worker after `load`; when the worker controls the page, the page posts the eligible URLs
  it has already loaded (Resource Timing), on `load` and again when an island becomes usable, and the worker adds those
  it does not have inside `event.waitUntil`. Those fetches are normally answered by the HTTP cache (the files were
  loaded seconds earlier and are fresh for 10 minutes), so the first visit's extra network traffic is close to zero;
  at most it re-downloads files the page itself loaded. The first return visit is warm.
- Navigations: navigation preload is enabled and the navigation handler answers with the awaited `preloadResponse`, or
  with `fetch(request)` when that resolves to nothing or rejects; this keeps worker start-up off the critical path.
  Nothing else mutable is intercepted.

**Compression.** Pages serves gzip at best, never the precompressed Brotli files; the lab transfer is 1.94 MB with
Brotli (`docs/performance.md`) against a 2.5 MB budget. The decision is made before any deployment, from the spike's
measurement of the published files (gzip at the level a CDN typically uses, Brotli, and the decoder's cost) and from
host evidence: if the host gzips large `.wasm` files and the gzip cold transfer is within the budget, the site relies
on host gzip; otherwise the runtime fetches the `.br` files through `loadBootResource`, decodes them in the browser
with Google's decoder imported as a module, and verifies each decoded file against its integrity value; on any failure
that same promise falls back to fetching the original file with integrity enforcement, because a promise resolving to
nothing is not a request for default loading (the `dotnet.d.ts` contract). The live check
after each deployment confirms the decision holds (it records `Content-Encoding` and size of every runtime file).

`?perf=1` keeps working: the readout counts files under the page's base `_framework/`, not the origin root's.

### 5.10 SEO

Canonical, `hreflang`, `x-default`, JSON-LD and Open Graph tags are rendered as today with the prefixed URLs. Sitemaps
(`sitemap.xml`, `sitemap-ru.xml`, `sitemap-en.xml`) are exported with absolute prefixed URLs; the sitemap index and
`robots.txt` name the sitemap files without a trailing slash. Open Graph images are rendered by SkiaSharp at build
time and saved as PNG files at their fingerprinted URLs. The catalog's indexable single-level pages (`?level=junior`)
are lost: Pages ignores the query string, so the catalog has one canonical URL; topic pages stay indexable.
`robots.txt` is exported but has no effect on a project site; island pages already carry `noindex`. Sitemap discovery
therefore goes through a search console submission (owner decision 2026-11-17): the owner submits `sitemap.xml` to
Google Search Console and Yandex Webmaster after the first deployment. Ownership is verified by meta tags,
`<meta name="google-site-verification">` and `<meta name="yandex-verification">`, rendered from configuration on the root
chooser and every culture home page; meta tags are not governed by CSP, so the meta CSP does not affect them, and the
export verifier checks that they are present when configured. If Yandex Webmaster accepts only the origin
`https://platonenkov.github.io/` and not a path, the tag belongs on that origin's own site (the account's user Pages
repository), which is an owner step outside this repository.

### 5.11 CI and deployment

The site build (publish, PostgreSQL, export, verify, static smoke) is one reusable workflow, `static-site.yml`
(`workflow_call`). `ci.yml` calls it on every pull request and push without retention or upload, so the whole build
is exercised while the repository is private and on the very pull request that introduces it (a new
`workflow_dispatch` workflow cannot be dispatched before it exists on the default branch). The static smoke suite fails,
not skips, when its inputs are missing, and the step requires a minimum number of executed tests. Both workflows read
the site URL from the repository variable `SITE_URL`; every URL-derived value (crawler address, prefix, smoke URL) is
computed from it.

`pages.yml` runs after a successful `ci` push run on `main` of this repository (provenance checked) and builds exactly
that run's commit; a manual dispatch builds the tip of `main` after checking that it has a successful `ci` push run.
GitHub deployment records are not used to identify what is live; the live site says it:

1. `gate` (no environment): fetches the live `build.json` with a cache-busting query. A 404 is accepted as the first
   deployment only when the site root also answers 404 (nothing is live yet); any other inconsistency fails. A
   candidate older than the live commit (an ancestor of it) is always refused. A candidate equal to the live commit is
   skipped on automatic runs and accepted on a manual dispatch, which is how a configuration-only change (the
   `SITE_URL`, GoatCounter or verification variables) is published: same commit, new build id. A skipped run stops
   here: no environment job runs, so no empty deployment record is created.
2. `build`: `static-site.yml` with retention from the identified live build (5.13) and the artifact upload.
3. `recheck` (no environment): repeats the gate's decision, with the same policy, against the live site seconds
   before deploying.
4. `deploy` (environment `github-pages`, only when `recheck` says deploy): `actions/deploy-pages`.
5. `live-check` (only after a real deploy): checks the live site, including that `build.json` now carries this build id.

Runs share the concurrency group `pages` without cancelling the one in progress (a Pages deployment is never cut off
mid-way); GitHub keeps at most one pending run and replaces it with a newer one. The gate makes order safe: an older
commit never replaces a newer live one. The residual case, a pending run for a newer commit replaced by a later-queued
run for an older one, leaves the site one commit behind until the next push or a manual dispatch; it is documented.
Actions are pinned by full commit SHA; the workflow declares `contents: read`, and each job declares exactly what its
steps use (`deploy`: `contents: read`, `pages: write`, `id-token: write`). No secret is used. No schedule.

The server deployment workflows (`deploy-app`, `deploy-content`, `rotate-db-password`, `ops-check`) stay in the
repository but are disabled in the repository settings while no server exists.

### 5.12 Going public

Before the owner makes the repository public, everything that becomes public is audited: a fresh mirror clone with
every branch, tag and pull request ref is scanned with gitleaks and searched with `git log --all -p -G` for the
owner's personal identifiers and server addresses; Actions run logs and artifacts are reviewed or deleted (variables
are not masked in logs); issues, pull request bodies and comments, releases and packages are reviewed; stale branches
are deleted. Any real secret found is revoked and rotated, always; rewriting history is a separate owner decision
afterwards, because a rewrite neither invalidates a credential nor removes copies already taken. Then the hardening
checklist of `docs/deployment.md` is walked item by item (items that concern the server environment are recorded as
not applicable while it does not exist), repository settings are applied, and the owner clicks "Change visibility".
Secret scanning and push protection are switched on right after.

### 5.13 Deployment from build A to build B

| What | Name stability | A page of build A after B is live |
|---|---|---|
| HTML pages | Mutable URLs | Cached up to 10 minutes by the browser and the CDN, then B |
| Runtime, CSS, JS, fonts | Fingerprinted, content-addressed; unchanged files keep their names (deterministic publish) | Served: B's deployment also carries A's immutable files (retention), and any worker may have cached them |
| Content files | Hashed | Served while retained; afterwards a 404 that the island reports as "site updated" |
| Open Graph images | Fingerprinted | Retained like the runtime |
| Service worker | `sw.js`, mutable | B's worker takes over at once (5.9); A pages keep getting A's bytes because names are content-addressed |

Retention: each deployment publishes `build.json` (build id, commit, build time) and `immutable.json` (build id and the
complete immutable set: runtime, CSS, JS, fonts, Open Graph images, content files). The next build fetches both from the
live site with cache-busting queries and requires both to carry the same build id (a CDN serving the two files from
different deployments is refused and retried for up to three minutes, then fails); it downloads every listed file
missing from its own export and deploys it alongside (not linked, not in the sitemap, not in its own manifests). The
first deployment, identified by the gate (5.11), retains nothing. The window is one deployment: a page of A works
until the deployment after B. Two deployments within the 10-minute HTML lifetime can exceed it; that is accepted and
documented.

The previous-build smoke test runs a page of the live build against the new artifact: the build fetches the live
Practice page with a cache-busting query and requires its `<meta name="dit-build">` to equal the identified live build
id (retried like the manifests). It is skipped only on the first deployment.

Recovery outside WebAssembly: every exported HTML page carries a small inline recovery script, allowed in the meta CSP
by its SHA-256 like the theme script, and a hidden banner in the page's language: "Part of the page did not load.
Reload the page." The script listens in the capture phase for load errors of the elements marked `data-recover` (the
stylesheets, `islands-start.js`, `blazor.web.js`; not the analytics scripts, which content blockers refuse) and exposes
`window.ditRecover()`, which `islands-start.js` calls on any boot failure (a failed runtime fetch, module import or
start). The banner is shown at most once per page URL per browser session (`sessionStorage`, in try/catch); its link
keeps the page's query and sets `_r=<time>` (ignored by every parser), so the browser and the CDN fetch the current
HTML instead of the cached copy. It works without the site stylesheet (`hidden` attribute, plain link) and without
`islands-start.js`, which may be the missing file. A content 404 inside a running island shows the site-updated state
of the island instead.

## 6. What the static edition loses

| Lost | Why | What the visitor sees |
|---|---|---|
| Telegram sign-in, accounts, cross-device sync | No server to hold a client secret or data | No sign-in controls; profile explains that progress lives in this browser |
| Server profile, server review history, import of anonymous answers | Same | Profile computed from browser storage |
| Like and dislike, in-app reports, admin page | No server | "Report a problem" opens a GitHub issue |
| Server-side catalog search and paging, indexable `?level=` catalog pages | Pages ignores query strings | Full list filtered in the browser |
| Response security headers (`frame-ancestors`, `nosniff`, CSP reports) | Pages sets no custom headers | Meta CSP and meta referrer policy |
| Long-lived HTTP caching (`immutable`) | Pages sends `max-age=600` | Service worker cache for immutable files |
| Brotli transfer from the host | Pages does not negotiate precompressed files | Host gzip, or in-browser Brotli decoding (5.9) |
| Effective `robots.txt` | Project sites cannot own the origin root | `noindex` on non-indexable pages; sitemap submitted |
| Language cookie and server root redirect | No server | Language remembered in `localStorage`; root chooser script |
| Offline continuation of a whole Practice session | Questions are fetched one ahead | Next question is prefetched; a lost connection shows the offline state with retry |
| Progress across an origin change | Storage is per origin | Fresh start after a custom-domain move (5.6) |

## 7. Designer's calls

| Call | Choice | Reason |
|---|---|---|
| How static HTML is produced | Run the published app with a throw-away PostgreSQL in CI and crawl it, after a spike proves the boot on a static host | Identical rendering to the tested server edition, no second implementation of the query services |
| Base path | Derived from the path of `App:PublicBaseUrl`, applied with `UsePathBase`, written by `SitePaths` | One setting; the server edition's URLs do not change |
| Page layout on disk | `<path>/index.html`, slashed canonical URLs | Works on Pages and any other static host without a redirect hop |
| Shared session code | `MockComposer` moved to Domain; `PracticeSelector` and `ReviewOrder` in Domain for the static edition; server SQL unchanged with a parity test | One set of rules without rewriting the server edition's queries |
| Mock loading | All chosen questions before the start | Jump strip, timer and restore need them; still only the chosen ones |
| Local progress | Full answer log, recomputed on read and memoised; a checkpoint of the result is stored beside it and used only for the log state it was written for (ADR 0028) | Obviously correct for late answers, many tabs and many stacks; the checkpoint removes the multi-second read of a long log |
| Storage namespace | `dit.*` keys unchanged; cache names include the scope; `theme` becomes `dit.theme` | One deployment per origin; caches are new, the theme key collides today |
| Issue prefill | Issue form template with prefilled field ids; label from the template | A `labels=` URL parameter is ignored for visitors without triage rights |
| GoatCounter script | `https://gc.zgo.at/count.v4.js` with Subresource Integrity, path reported without query and hash | Owner allows the script in CSP; SRI pins the bytes; search terms never leave the browser |
| Static mode headers | The app sends no security headers in the static edition | A local static run behaves like Pages, so E2E tests the real thing |
| Service worker | Content-addressed eligibility pattern made sound by the export verifier, one LRU cache, `skipWaiting` and `claim`, fill from Resource Timing, correct navigation preload | No generations to coordinate: a name always means the same bytes |
| Deployment window | One-deployment retention of the live build identified by its own manifests, plus an inline CSP-hashed recovery handler | Cached pages keep working; anything older recovers with one click even when the startup script is gone |
| What is live | The live `build.json`, not GitHub deployment records | Deployment records carry the run's SHA and can exist for runs that deployed nothing |
| Answer time | Fixed once when the answer is written; replay never re-clamps | Same log, same states, on every reload and day |
| Build identity | Constant assembly version; build id in `build.json`, `immutable.json` and each page | Identical bytes for identical source; identity still recorded |
| Site build in CI | Reusable `static-site.yml`, called by `ci.yml` and `pages.yml` | Proven on the introducing pull request; one definition |
| Compression | Decided before the first deployment from measured sizes and host evidence | A gzip transfer over budget must not be discovered after going live |
| Server workflows | Disabled in repository settings, files unchanged | No daily failures while no server exists; one command re-enables them |
| Not-found page | The app's not-found page in the default culture, saved as `404.html` | Pages serves one 404 per site |

## 8. Owner answers (2026-11-17)

1. Sitemap: submitted to Google Search Console and Yandex Webmaster as owner steps of the acceptance task, ownership
   verified by meta tags (5.10).
2. Progress loss on a custom-domain move: accepted; no export or import (5.6).
3. If the deployed-phone medians miss the targets: the remedy is the owner's Plan 3 decision 4, unchanged: cut work
   before the first render first, with no payload growth; AOT only if the warm target alone is missed.

No question is open.

## 9. Amendments after the final review (2026-11-18)

- 5.11 steps 3 and 4: the recheck is a step of the `deploy` job (the only job with the environment and the concurrency group
  `pages`), not a separate job without an environment. The decision must be made under the lock, and the lock belongs to one
  job. The cost is that a recheck that declines still leaves a `github-pages` deployment record, which step 1 wanted to avoid for
  skipped runs; a run the gate skips still creates none. Confirmed by the controller (final review, B7).
- 5.11: the `deploy` job does not end when `deploy-pages` returns. It polls the live `build.json` until it carries this run's
  build id (up to five minutes, then it fails), so the lock is held until the next deploy job can see the new build.
- 5.11: the reusable workflow comes from the ref the `pages` run executes (the tip of `main`) while its steps check out the
  candidate; an older candidate is built with the newest workflow, and the worst case is a red run that publishes nothing.
- 5.13: a retained `.br` file may differ in bytes from the live build's file of the same name when both decompress to the same
  bytes (a compressor change leaves the uncompressed file, and so its fingerprint, unchanged); the new build's copy is kept.
  Names are judged by the live build's own `immutable.json`, not by the new build's eligibility rule.
- 5.6: the replay applies an absolute answer-time window (2000-01-01 to 2100-01-01) rather than the clock-relative limits of
  ADR 0005, and a checkpoint append no longer falls back to a replay because of a future-dated answer (ADR 0028, amendment).
