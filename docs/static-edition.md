# Static edition

The static edition is the same application exported as plain files and published on GitHub Pages. This page is the
operator's guide: how the site is built and deployed, how to preview it, which settings it reads, and what to do when
something goes wrong. The design is in `docs/superpowers/specs/2026-11-17-static-edition-design.md` (the spec, with section
numbers such as "spec 5.13"), the decisions are ADR 0022 to 0028, and the code is described in the "Static edition" section of
`docs/architecture.md`. When this page and a workflow or a script disagree, the workflow or the script is right and this page
has a bug to fix.

## Overview

One codebase makes two editions (ADR 0022). `App:Edition=Server` is the application of `docs/deployment.md`: PostgreSQL,
sign-in, feedback, Umami. `App:Edition=Static` renders the same pages without any of that, and the CI exports them to files.
Visitors get the public pages (home, catalog, topics, questions, about) and the learning modes (Practice, Mock, Review and the
profile). Their progress lives in their browser and is never sent anywhere. What does leave the browser is a GoatCounter
page-view beacon (only when a GoatCounter code is configured) and, when a visitor follows a "Report a problem" link, the
address of that page inside the GitHub issue form.

What the static edition does not have, and what the visitor sees instead, is the table in section 6 of the spec: no
sign-in, accounts or sync; no like and dislike or admin page; the catalog is filtered in the browser; the security headers
are meta elements; the language is kept in `localStorage`. The server edition is not deployed while the site runs on Pages;
its deployment workflows stay in the repository, and the owner disables them in the repository settings while no server
exists (spec 5.11, `docs/superpowers/specs/2026-11-17-static-edition-design.md`).

The site is served from a project URL such as `https://<owner>.github.io/<repository>/`. Everything the application writes
(links, assets, canonical URLs, the sitemap) goes through `SitePaths`, so the path `/<repository>/` comes from one value, the
address, which the `pages` gate derives from the repository (`https://<owner>.github.io/<repository>/`, lowercase owner) unless
the optional repository variable `SITE_URL` names a custom domain.

## How the site is built

Three workflows do it. Their step-by-step description, with permissions and inputs, is in "Tests and CI" of `docs/architecture.md`;
this is the shape.

1. `ci.yml` calls `static-site.yml` on every pull request and push, without retention and without an upload, so the whole
   build is exercised before anything is published.
2. `static-site.yml` (reusable, `workflow_call`) publishes `src/Trainer.Web` in Release with a constant informational version
   (so identical source gives identical fingerprints), starts a throw-away PostgreSQL, applies the migrations, runs the
   published app in the `StaticExport` environment, and runs `tools/Trainer.StaticSite`: `export` crawls the running app into a
   folder, `verify` checks the folder structurally, and the `StaticSmoke` tests then run it in Chromium behind the Pages
   emulator. The `_framework` transfer budget of 2 500 000 bytes always gates the job; the timings are written to the job
   summary and are advisory (`DIT_PERF_ENFORCE=0`, `docs/performance.md`).
3. `pages.yml` publishes to GitHub Pages. It runs after a green `ci` push run on `main`, or by hand from `main`. There is no
   schedule. Its jobs run in this order:
   - `gate` checks where the run comes from and decides from the live `build.json` whether to publish (a commit that is
     older than the live one is always skipped, and the live commit itself is skipped on an automatic run and republished on
     a manual one);
   - `build` calls `static-site.yml` with retention from the live build and uploads the Pages artifact;
   - `deploy` (the only job with the `github-pages` environment and the concurrency group `pages`) decides once more inside
     the lock, right before `actions/deploy-pages`, so a run that waited for another deployment never publishes backwards:
     two close pushes leave the newest commit live. A run that declines ends green and says so in its summary;
   - `live-check` runs only after a real deploy and checks the live site: routing, the not-found page, this run's build id,
     and what the host does with the runtime files.

   The decisions are shell logic in `deploy/pages/pages-gate.sh`, tested by `deploy/tests/pages-gate.test.sh`; the workflow
   only calls it. The manual `workflow_dispatch` input `allow_unknown_live` is the recovery after `main` was rewritten, see
   "Publishing the static site" in `docs/deployment.md`.

The browser downloads the runtime as Brotli files and decodes them itself, because GitHub Pages serves `.wasm` gzip at best
and the gzip set is over the budget (ADR 0027). Each decoded file is verified against an integrity table the export
publishes (`js/boot-integrity.{hash}.mjs`): the .NET 10 runtime reads the boot configuration field `hash`, which is empty,
while the configuration carries `integrity`, so the runtime passes no usable value to the loader. Decoded bytes are never
used without a verified entry; a resource the table does not list loads uncompressed at the runtime's default trust.

## Deployments and cached pages

GitHub Pages caches every file for ten minutes, and a visitor can keep a page open for much longer, so a page of build A
runs for a while after build B is live. The rules (spec 5.13):

| What | Name stability | A page of build A after B is live |
|---|---|---|
| HTML pages | Mutable URLs | Cached up to 10 minutes by the browser and the CDN, then B |
| Runtime, CSS, scripts, fonts | Fingerprinted, content-addressed; unchanged files keep their names | Served: B's deployment also carries A's files (retention), and the service worker may have cached them |
| Content files | Named by a hash of their bytes | Served while retained; afterwards a 404 that the island reports as "site updated" |
| Open Graph images | Fingerprinted | Retained like the runtime |
| Service worker `sw.js` | Mutable | B's worker takes over at once; A's pages keep getting A's bytes because names are content-addressed |

- **Retention.** Every deployment publishes `build.json` (build id, commit, build time) and `immutable.json` (the build id and
  every immutable file). The next build fetches both from the live site, requires the same build id in both (a CDN serving
  them from two deployments is retried for three minutes, then the build fails), downloads every listed file that its own
  export lacks, and deploys them beside its own files. The files are not linked and not in the sitemap. The first deployment
  retains nothing.
- **The window is one deployment.** A page of build A works until the deployment after B. Two deployments inside the ten
  minutes of the HTML lifetime can exceed it; that is accepted. When the `deploy` job sees that another deployment went live
  while its build ran, it writes a notice: the build copied its files from the older build, so a client still on the replaced
  one may miss some hashed files until the recovery banner reloads it.
- **The reload recovery.** Every exported page has a small inline script (`RecoveryScript`) and a hidden banner "Part of the
  page did not load. Reload the page." (in the page's language). A failed stylesheet or script marked `data-recover`, or any
  failed island start, shows it at most once per page and browser session. Its link reloads the page with `_r=<time>`, which
  makes the browser and the CDN fetch the current HTML. A content file that is gone inside a running island shows the
  island's own "site updated" state instead.
- **Service worker** (ADR 0026). `sw.js` caches only content-addressed files, so a worker of any generation may serve any
  build's files and no cache generation is ever deleted.

## Preview locally

This reproduces the CI build on one machine, with Docker for PostgreSQL. The database password below is the throw-away one
of the README, for a database bound to `127.0.0.1`.

CI builds with its own pin, not the SDK of `global.json`: SDK 10.0.303 with `rollForward` disabled, and the `wasm-tools`
workload of the same version (`static-site.yml`, "Pin the SDK to 10.0.303" and "Install wasm-tools"). The pair relinks the
runtime, and the fingerprints and the `_framework` transfer budget are reproducible only on it. To get CI's numbers, install
SDK 10.0.303 side by side, make `global.json` say `"sdk": {"version": "10.0.303", "rollForward": "disable"}` in your working
copy (do not commit it), and run `dotnet workload install wasm-tools --version 10.0.303`. Without that, the preview still
works, but it publishes a runtime that was not relinked: its files, fingerprints and sizes differ from CI's, and the
transfer budget is not meaningful.

1. Publish with the deterministic flags (the same as `static-site.yml`):

   ```bash
   dotnet publish src/Trainer.Web -c Release -o artifacts/web -p:IncludeSourceRevisionInInformationalVersion=false -p:InformationalVersion=static
   ```

2. Start PostgreSQL and migrate it:

   ```bash
   docker run --name dit-postgres -e POSTGRES_PASSWORD=dev -e POSTGRES_DB=trainer -p 127.0.0.1:5432:5432 -d postgres:17-alpine
   export TRAINER_DB="Host=localhost;Port=5432;Database=trainer;Username=postgres;Password=dev"
   dotnet tool restore
   dotnet ef database update --project src/Trainer.Infrastructure
   ```

3. Run the published app in the `StaticExport` environment from the publish folder. The public address decides the path
   prefix; the app listens on `127.0.0.1` so that the crawler's address passes the host filter:

   ```bash
   cd artifacts/web
   export ASPNETCORE_ENVIRONMENT=StaticExport ASPNETCORE_URLS=http://127.0.0.1:5080
   export App__Edition=Static App__PublicBaseUrl=https://example.github.io/dev-interview-trainer/ App__StaticSite__BuildId=local.1.1
   export App__ContentRoot="$(pwd)/../../content" ConnectionStrings__Trainer="$TRAINER_DB"
   dotnet Trainer.Web.dll
   ```

4. In another shell, wait for `http://127.0.0.1:5080/dev-interview-trainer/health/ready`, then export, verify and serve
   (`--build-id` must equal the app's `App__StaticSite__BuildId`):

   ```bash
   dotnet publish tools/Trainer.StaticSite -c Release -o artifacts/tool
   dotnet artifacts/tool/Trainer.StaticSite.dll export --app http://127.0.0.1:5080/dev-interview-trainer/ --site-url https://example.github.io/dev-interview-trainer/ --out artifacts/site --build-id local.1.1 --commit 0000000000000000000000000000000000000000
   dotnet artifacts/tool/Trainer.StaticSite.dll verify --dir artifacts/site --site-url https://example.github.io/dev-interview-trainer/
   dotnet artifacts/tool/Trainer.StaticSite.dll serve --dir artifacts/site --site-url https://example.github.io/dev-interview-trainer/ --port 8099 --gzip
   ```

   Stop the app after the export; only `serve` is needed afterwards. `serve` behaves like Pages (`max-age=600`, no other
   header, gzip for text and WebAssembly with `--gzip`) and prints its address; open it at the path prefix, for example
   `http://127.0.0.1:8099/dev-interview-trainer/`. Exit codes of the tool: 0 success, 1 a crawl, retention or verification
   problem, 2 a usage error. Add `--retain-from <live url> --live-build-id <id>` to `export` to rehearse retention against a
   live site.

5. Remove the database when you are done: `docker rm -f dit-postgres`.

## Configuration

The static edition reads these keys (the full table is in "Configuration" of `docs/architecture.md`). Set them as
environment variables with `__` for `:`, as `static-site.yml` does.

| Key | Meaning |
|---|---|
| `App:Edition` | `Static` turns the static edition on. Accepted only in the `StaticExport`, `Development` and `Testing` environments. |
| `App:PublicBaseUrl` | The public address of the site with its path (`https://<owner>.github.io/<repository>/`). Its path is the path base of every URL. In `pages` it is the address derived from the repository, or `SITE_URL` when that is set. |
| `App:StaticSite:RepositoryUrl` | The GitHub repository the "Report a problem" link opens an issue in. Default: this project's repository. |
| `App:StaticSite:GoogleSiteVerification` | Value of the `google-site-verification` meta tag on the root chooser and each culture home page; empty renders nothing. |
| `App:StaticSite:YandexVerification` | The same for `yandex-verification`. |
| `App:StaticSite:BuildId` | The identity of a deployment, `<commit>.<run id>.<attempt>`, rendered as `<meta name="dit-build">` on every page and written to `build.json`. Empty (a local run) renders nothing. |
| `App:StaticSite:BrotliRuntime` | `true` exports the Brotli runtime files and makes the island pages decode them (ADR 0027). Off by default; `appsettings.StaticExport.json` turns it on, which is the configuration of the Pages export. |
| `App:Analytics:GoatCounterCode` | The GoatCounter site code. Empty: no counter script and no GoatCounter origin in the policy. The counter script itself is self-hosted (`js/goatcounter.js`, ADR 0025); only the site code comes from configuration. |

Repository variables (Settings, Secrets and variables, Actions, Variables; none is a secret, and the workflows use no
secret):

| Variable | Feeds | Notes |
|---|---|---|
| `SITE_URL` | `App:PublicBaseUrl`, the crawler and smoke addresses | Optional, only for a custom domain: `https`, with a trailing slash, no query or fragment. Unset, the gate derives `https://<owner>.github.io/<repository>/` (a repository named `<owner>.github.io` is served at the root). No file of the repository names the site address. |
| `GOATCOUNTER_CODE` | `App:Analytics:GoatCounterCode` | Optional. |
| `GOOGLE_SITE_VERIFICATION` | `App:StaticSite:GoogleSiteVerification` | Optional. |
| `YANDEX_VERIFICATION` | `App:StaticSite:YandexVerification` | Optional. |

Changing a variable publishes nothing by itself. Run `pages` by hand from `main` (the same commit is republished with a new
build id) to make it live.

Two files in the export describe the deployment:

- `build.json`: `buildId`, `commit`, the build time and the verification values. The gate reads it from the live site to
  decide what to do, and `live-check` waits for the new build id in it. The service worker never caches it.
- `immutable.json`: the build id and every immutable file (the fingerprinted runtime, styles, scripts and fonts, the Open
  Graph images, the content files) plus the rule that decides which names the service worker may cache. Retention reads it
  from the live site.

## Moving to a custom domain

1. Set the repository variable `SITE_URL` to the new address, with a trailing slash (for a domain at the root it is
   `https://example.org/`), and set the domain in Settings, Pages. Without the variable the address stays the one derived
   from the repository. To return to it, delete the variable.
2. Run `pages` by hand from `main`. The new build uses the new address for every URL, canonical link and sitemap entry.
3. Check the live site, then update the search engine consoles (a new property, a new sitemap) and the verification variables
   if the new site has its own codes.

Visitors' progress does not move. Progress is stored per origin, and a custom domain is a new origin: the browser does not
share `localStorage` between `https://<owner>.github.io` and `https://example.org`, and Pages redirects the old address at
once, so no script can carry anything across. Visitors start from zero. The owner accepted this on 2026-11-17 (ADR 0022), and
there is no export or import of progress. Announce the move beforehand, so that visitors know their history will not follow.

## Progress in the browser

The limits, which the profile page also shows: progress is per browser and device; clearing the site's data deletes it; Safari
deletes script-written storage of a site that was not visited for seven days; the 5 MB `localStorage` quota of the origin
is shared with every other site on the same `github.io` account (about 20 000 answers fill it, and the storage notice appears
before that).

### Progress storage

In the static edition the anonymous outbox (`dit.outbox.v1.anon`) is the visitor's whole progress: every answer is
kept there and replayed on read. A corrupt value of that key is quarantined to `dit.outbox.v1.corrupt.{unix ms}` like
any outbox, which here means the visible progress resets to empty; the copy stays in `localStorage` and nothing
reads it back automatically.

Next to the log the page keeps a derived checkpoint, `dit.progress.v1.anon` (ADR 0028), so that a load does not parse and
replay thousands of answers on a phone. The log is the source of truth. The checkpoint is trusted only for the exact log
state it was written for, and it is safe to delete: the next load replays the log once and writes a new one. When an append
to the log hits the storage quota, the checkpoint is evicted first and the append is tried again, so derived data never costs
an answer its place.

### Mock history storage

Finished mocks are kept in `dit.mock.v1.anon.history` (`StoredMockHistory`), the one thing stored beside the answer log.
It follows the outbox rules: every write is one `historyAdd` op under the key's Web Lock that merges by mock id and cuts
each stack's list to its newest 20 (`StoredMockHistory.MaxItems`, a few kilobytes per stack), so two tabs finishing mocks
together keep both and older mocks fall off the end. Every entry carries its stack; the profile of a stack lists the newest
five of that stack, and an entry without a valid stack is never shown. A mock is recorded only once its answers are in the
outbox, and before the finished mock's keys are removed, so a failure of the history can never lose the only copy. When
storage cannot take it (private mode, no Web Locks, a full quota) it stays in that tab's memory until the tab closes,
retried by the next add, and the storage notice says so. A corrupt history is quarantined like the outbox, which
here means the listed mocks reset to empty while the copy stays in `localStorage`; a document written by a newer app is
never touched.

## Content updates

The question bank is `content/`. The `StaticExport` run imports it into the throw-away database at start, so a content
change reaches the site the way a code change does: merge to `main`, let `ci` pass, and `pages` publishes it. There is nothing
to import by hand and no content deployment. A question's file is named after a hash of its bytes, so a changed question gets
a new name; the previous deployment's files are retained for one deployment (see "Deployments and cached pages").

## Reports from visitors

"Report a problem" on a question opens a new issue in `App:StaticSite:RepositoryUrl` with the form `question-report.yml`:
the question id, the language and the page address are prefilled, the visitor picks a reason from a fixed list and may add
details. The form puts the label `content-report` on the issue. It is a plain link, so it needs no script and no policy
change. Triage these issues in the repository; the weekly `link-check` workflow keeps its own issue of dead source links.

## Troubleshooting

**The site answers 404 under the right address, or every asset is missing.** The path prefix does not match the address Pages serves.
If `SITE_URL` is set, check that it equals that address (`https://<owner>.github.io/<repository>/`, with the slash), that
Settings, Pages publishes from GitHub Actions, and that the last `pages` run deployed (a skipped run publishes nothing).

**"Part of the page did not load. Reload the page." keeps appearing.** The banner is the recovery of a page that is older than
the deployment: it appears at most once per page and browser session, and its link loads the current page. If it comes back
on a fresh page, a file the page needs is really missing:
1. Open the browser's network panel and find the failing request.
2. Compare it with `immutable.json` of the live site. A name that is in the file but returns 404 means the deployment lost
   it; run `pages` again by hand.
3. A name that is not in the file means the HTML is older than the file list, which is the one-deployment window; wait ten
   minutes or reload.

**Resetting the service worker.** In the browser's developer tools, Application, Service workers, unregister the worker of the
site; then, under Cache storage, delete `dit-immutable-v1:<path>` if you want every file fetched again. Do not clear
`localStorage`: it holds the visitor's progress. A worker never serves a wrong file for a name, so this is needed only to test.

**A failed `static-site` CI job.** Open the job summary first, then the step that failed:
1. `Derive the site paths` fails: the site address passed to `static-site.yml` is empty or not an `https` URL with a trailing slash
   (in `ci` it is `SITE_URL` or the `ci.invalid` sentinel; a malformed `SITE_URL` is also rejected by the `pages` gate).
2. `Wait for the app` fails: read the app log it prints; usually a database or a content problem.
3. `Export the site` or `Verify the export` fails: the message names the file and the rule (an unresolved URL, a missing
   immutable file, a hash that differs from its name). Reproduce it with "Preview locally".
4. `Static smoke tests` fails: the `_framework` budget (2 500 000 bytes) is blocking, timing rows are advisory. Download the
   `static-site-perf` artifact for `perf.json`.

**A retention failure.** `Export the site` fails in one of two ways:
- "The live manifests ... did not settle on build ..." means `build.json` and `immutable.json` of the live site carried
  different build ids for three minutes, which is a CDN serving two deployments. Re-run the job.
- "... has different bytes in the live site and in this export" means a content-addressed name now holds other bytes, which
  breaks the rule that a name always means the same bytes. Find the file named in the message and the change that altered it
  without renaming it. A `.br` file is compared by its decoded bytes: a different compressed stream of the same file is
  accepted and the new build's copy is kept, so the message for a `.br` file means that the decoded file itself changed.
- "... is not a valid Brotli stream" means the live copy of a `.br` file is damaged; check what the live site serves for it.

**A run was skipped ("already live").** The gate found the candidate commit equal to the live commit (or older) and stopped,
which is correct for an automatic run; the reason is in the run's annotations. To publish the same commit again, for example
after changing a repository variable, run `pages` by hand from `main`. If the gate fails because the live commit is not in
the history of `main` (after a force-push), run `pages` by hand with `allow_unknown_live`.

**The site is one commit behind.** GitHub keeps one pending `deploy` job per concurrency group and replaces it with a newer
one, so a pending job of a newer commit can be replaced by a later-queued job of an older commit. The older job then
publishes nothing, because the in-lock decision refuses to go backwards, and the site stays behind until the next push.
Dispatch `pages` by hand from `main` to publish the tip.

**A deployment record without a deployment.** The `deploy` job has an environment, so GitHub records a deployment when it
starts, even when the in-lock decision then declines. A run that the gate skips creates no record.

## Spike results

The first task of the plan measured whether a published Release island boots from a plain static host and fixed the
compression decision: `docs/static-edition-spike.md`. In short, the island boots under a path prefix with no request to a
server, the host serves gzip but not Brotli (the gzip set was 2 784 251 bytes, over the budget), and so the runtime ships as
Brotli files decoded in the page (ADR 0027).

## Going public

The repository is private until the owner makes it public. Task 20 of the plan audits what becomes public (history, Actions
logs and artifacts, issues, branches) and walks the hardening checklist of `docs/deployment.md`; this section records the
result when that task is done.

## Acceptance log

The live acceptance of the first deployment (Task 21 of the plan: the live checks, the phone measurements and the owner's
sign-off) is recorded here when it is done.
