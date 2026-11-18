# Performance

Spec 8.2 sets the targets: "on a mid-range phone over 4G, Practice is interactive
within 2 s with a warm cache and within 5 s cold". This page fixes what that
means, how it is measured, and every measurement so far.

**Decision (owner, 2026-11-13, ADR 0014).** Two different checks:

- **Real-phone targets (spec 8.2): 2 s warm, 5 s cold.** An acceptance criterion of
  Plan 3, measured on a real mid-range phone over 4G against the deployed site with
  `?perf=1` ([Measure on the deployed site](#measure-on-the-deployed-site)).
- **Lab regression budgets: 8 500 ms cold, 4 750 ms warm, 2.5 MB transfer.** The
  emulated phone in CI (the `Release` suite under the throttling profile below)
  guards against regressions; it is not a stand-in for the phone targets.

## What is measured

- **Interactive** is the moment the Practice island sets
  `data-island-ready="true"` on `.island-root`, timed with `performance.now()` from
  the document's navigation start. The island sets it after the first render in
  which it has left its loading phase and shows something to act on (Practice: the
  filter form with its handlers attached, or the first question of a `?first=`
  link), never earlier (`IslandRoot.Usable`). It includes the HTML response, the
  runtime download and start, the island's own start-up (account watcher, stored
  filters) and that render. Rows before the Stage 2 fix round set the mark after
  the first render of the island shell, a little earlier; they are not directly
  comparable with later rows.
- **Cold** is a new browser context (empty HTTP cache) opening
  `/ru/dotnet/practice` or `/en/dotnet/practice` directly; both languages are
  timed (rows before Stage 2 timed `/en` only).
- **Warm** is the same context coming back to the same page from a public page of
  its language after one earlier visit; every `_framework/` file then comes from
  the HTTP cache (fingerprinted, `max-age=31536000, immutable`).
- **Transfer** is the sum of the bytes received for every `_framework/` request of
  one cold load, headers included, as Chromium's DevTools Protocol reports them
  (`Network.loadingFinished.encodedDataLength`).
- Each timing is the median of 5 runs; the range (fastest to slowest) is given next
  to it, because a shared developer machine varies by several hundred
  milliseconds between runs of the same build. The timing runs are anonymous (no
  session cookie); the signed-in path is checked for caching, not timed.

## Throttling profile

The Planner's fixed stand-in for "a mid-range phone over 4G", applied per tab
through the DevTools Protocol (`MobileProfile` in `tests/Trainer.Web.Release`):

| Setting | Value |
|---|---|
| Latency | 150 ms (`Network.emulateNetworkConditions`) |
| Download | 9 Mbit/s |
| Upload | 1.5 Mbit/s |
| CPU | 4× slowdown (`Emulation.setCPUThrottlingRate`) |
| Viewport | 375 × 812, touch (`HasTouch`) |
| Browser | Playwright's headless Chromium |

## Targets and budgets

Lab regression budgets (the `Release` suite, `IslandPerformanceTests`):

| Check | Limit | Fails the run |
|---|---|---|
| `_framework/` transfer on a cold load, ru and en | ≤ 2.5 MB (2 500 000 bytes) | always |
| Practice interactive, cold (median of 5), ru and en | < 8 500 ms | only with `DIT_PERF_ENFORCE=1` |
| Practice interactive, warm (median of 5), ru and en | < 4 750 ms | only with `DIT_PERF_ENFORCE=1` |

The timing budgets sit about 10% above the lab medians measured after Stage 2 (about
7.7 s cold and 4.3 s warm), so an enforced run fails when start-up regresses. The
transfer budget is the Planner's call: 2.5 MB is about 2.2 s on the wire at 9 Mbit/s.
Timings on shared runners are noisy, so pull request and push runs only record them
(advisory); the CI `release` job dispatched with `enforce = true` holds them to the
budgets. A budget is never raised without the owner's decision; a miss is fixed by
reducing the payload or the start-up work.

Real-phone targets (spec 8.2, Plan 3 acceptance): Practice interactive within 2 s
warm and 5 s cold on a mid-range phone over 4G, medians of 5 runs, Russian and
English, measured on the deployed site.

## Documented runner

The enforced run is the `release` job of `.github/workflows/ci.yml` on
`ubuntu-latest` (a GitHub-hosted runner with 4 vCPU and the standard image),
started by hand: Actions, `ci`, "Run workflow", `enforce` = true. That sets
`DIT_PERF_ENFORCE=1` for the job. Every run, enforced or not, writes
`TestResults/perf/perf.json`, adds the numbers to the job summary and uploads the
`release-perf` artifact.

## Running it locally

The suite runs the published build, never the Debug build: trimming,
fingerprinting and pre-compressed assets exist only there.

1. Start PostgreSQL 17 and migrate a database:

   ```bash
   docker run -d --name dit-release-pg -e POSTGRES_PASSWORD=dev -p 55432:5432 postgres:17-alpine
   export DIT_RELEASE_CONNECTION="Host=localhost;Port=55432;Database=dit_release;Username=postgres;Password=dev"
   dotnet tool restore
   TRAINER_DB="$DIT_RELEASE_CONNECTION" dotnet ef database update --project src/Trainer.Infrastructure
   ```

2. Publish and build the tests:

   ```bash
   dotnet publish src/Trainer.Web -c Release -o artifacts/publish
   export DIT_RELEASE_PUBLISH_DIR=artifacts/publish
   dotnet build
   pwsh tests/Trainer.Web.Release/bin/Debug/net10.0/playwright.ps1 install chromium
   ```

3. Run the suite (port 5080 must be free). Add `DIT_PERF_ENFORCE=1` to make the
   timings blocking:

   ```bash
   dotnet test --project tests/Trainer.Web.Release --filter-trait "Category=Release" --ignore-exit-code 8
   ```

The app log of the run is `TestResults/release/app.log`; the measurements are in
`TestResults/perf/perf.json`. The database keeps the rows of earlier runs; every
run signs in with new Telegram ids, so it can be reused.

## Results

Local runs are advisory: a developer machine is neither throttled like the
runner nor quiet. Only the CI rows marked enforced count against the targets.

| Date | Commit | Runner | Mode | Cold median | Warm median | Transfer `/ru` | Transfer `/en` |
|---|---|---|---|---|---|---|---|
| 2026-11-13 | Task 21 (on 86cdef5) | local: Windows 11, 12 logical CPUs, `wasm-tools` workload installed (native runtime relinked) | advisory | 9520 ms | 5222 ms | 2 652 423 B | 2 573 326 B |
| 2026-11-13 | same, published with `WasmBuildNative=false` (the prebuilt runtime, as on a runner without the workload) | local, as above | advisory, transfer only | not measured | not measured | 2 685 615 B | 2 606 518 B |
| 2026-11-13 | Task 21 fix round 1 (asset collection kept on the server) | local, as in the first row | advisory | 8434 ms | 4370 ms | 2 639 559 B | 2 560 447 B |
| 2026-11-13 | Stage 2, build `4b1d981` (invariant globalization; reads through `[JSImport]`, deferred start-up calls, both reverted later) | local, as in the first row; machine shared with other work | advisory | ru 7367 ms, en 7333 ms | ru 3938 ms, en 3952 ms | 1 935 743 B | 1 934 947 B |
| 2026-11-13 | Stage 2 fix round, build `8376585` (ready mark at the first usable view, see "What is measured") | local, as in the first row; quiet machine | advisory | ru 7 689 (7 660-7 880), en 7 717 (7 689-7 801) | ru 4 260 (4 123-4 488), en 4 250 (4 224-4 378) | 1 936 445 B | 1 935 658 B |

Cold and warm runs of the first row, in ms: cold 9601, 9603, 9520, 9370, 9396;
warm 5255, 5107, 5222, 5340, 5028. The runtime files had all arrived after a
median of 3472 ms cold and 420 ms warm, so about 4.8 s of every load is the
runtime and the island starting on the throttled CPU, not the network.

Stage 2 fix round runs in ms: ru cold 7727, 7880, 7689, 7682, 7660, warm 4488, 4260,
4348, 4123, 4253; en cold 7801, 7717, 7743, 7689, 7689, warm 4378, 4250, 4250, 4237,
4224 (50 runtime requests). Transfer sizes differ by about 1 KB between builds of the
same configuration (fingerprints and the import map change with every build).

No enforced CI run has been made yet. Two local runs with `DIT_PERF_ENFORCE=1`
against the regression budgets (build `a26262c`, 2026-11-13) were taken while other
work loaded the machine (about 37% CPU before the run) and are not conclusive: the
transfer passed (1 937 440 B ru, 1 936 542 B en); the first run failed both cold
medians (ru 10 041 ms, en 9 914 ms; single runs up to 12 007 ms), the second only the
English cold median (ru 7 875 ms, en 8 931 ms; single runs from 7 692 to 12 480 ms).
Alternating runs of the previous and the current build at the same time showed no
difference between them (medians 7 441-9 315 ms cold either way), so this is host load,
not a regression. The quiet-machine medians after Stage 2 (7.7 s cold, 4.3 s warm) are
within the budgets; the enforced CI run on the documented runner decides.

Numbers taken at different hours are not comparable: the same Stage 1 build measured
8 434 / 4 370 ms on a quiet machine and 11 614 / 7 055 ms while other work loaded it.
Per-change effects in "Changes made" are therefore given only from alternating pairs
of runs taken together, with the range of each set of 5.

## Changes made to meet the checks

- **The published islands did not start at all.** `TrimMode=full` trims the
  client assembly too, and nothing in it references the island components (the
  server names them in its render markers), so the trimmed runtime failed with
  "Root component type 'Trainer.Web.Client.Islands.PracticeIsland' could not be
  found" and no island ever became interactive. `Islands/IslandRoots.cs` now keeps
  the four island components with `[DynamicDependency]`, called from
  `Program.cs`. Effect on the Brotli sizes: `Trainer.Web.Client.wasm` 10.9 KB →
  106.6 KB, `Trainer.Contracts.wasm` 1.5 KB → 47.4 KB, `System.Net.Http.wasm`
  3.2 KB → 53.1 KB. Rooting the whole client assembly
  (`<TrimmerRootAssembly>`) instead was tried first and cost 22 KB more.
- **`dotnet.js` was downloaded twice.** The preload named the fingerprinted
  `dotnet.{fingerprint}.js`, but `blazor.web.js` imports `./_framework/dotnet.js`,
  and without an import map that plain name was fetched as a second, `no-cache`
  copy (revalidated on every later visit). Island pages now render
  `<ImportMap />` under a per-request CSP nonce (ADR 0012). Effect: one request per
  runtime file (52 on a cold load), all fingerprinted, all from the HTTP cache on a
  warm visit.
- **The runtime loaded the site's whole asset collection before the first
  render.** An island page handed the client the URL of
  `_framework/resource-collection.*.js`, and the WebAssembly host imports and
  deserialises it (every static asset of the site, through reflection-based JSON,
  into frozen dictionaries) before it adds the first root component. The islands
  never call `Assets[...]`. `IslandEndpointConventions.KeepResourceCollectionOnServer`
  removes the URL metadata from the page endpoints; the server still fingerprints
  its own URLs and the import map. Effect (local, medians of 5): cold 9520 → 8434 ms,
  warm 5222 → 4370 ms, one request and 13 KB less.
- **Stage 2 (owner decision 2026-11-13): no ICU data.** The client runs with
  `InvariantGlobalization` (ADR 0013); the page culture is set from `<html lang>`
  so the satellite resources still load, and the profile's dates are formatted by
  the browser's `Intl`. With the relinked runtime the ICU code goes too:
  `dotnet.native.wasm` 931 KB → 468 KB Brotli, no `icudt_*.dat` (0.14-0.22 MB).
  Transfer (build `544856f`): 2 639 559 → 1 937 052 B (ru), 2 560 447 → 1 936 257 B
  (en), 50 requests. Timings, three alternating pairs of runs on a quiet machine
  (median, then range of the 5 runs; ms):

  | | Stage 1 build | Invariant build |
  |---|---|---|
  | ru cold | 8 384 (8 295-8 604), 8 451 (8 379-8 496), 8 495 (8 339-8 532) | 7 503 (7 417-7 509), 7 572 (7 533-7 677), 7 770 (7 674-7 780) |
  | en cold | 8 427 (8 243-8 553), 8 374 (8 261-8 492), 8 421 (8 317-8 560) | 7 459 (7 330-7 589), 7 613 (7 590-7 692), 7 708 (7 698-7 742) |
  | ru warm | 4 234 (4 197-4 358), 4 270 (4 230-4 522), 4 432 (4 226-4 978) | 4 043 (4 002-4 138), 4 262 (4 084-4 585), 4 238 (4 228-4 404) |
  | en warm | 4 151 (4 103-4 411), 4 229 (4 067-4 342), 4 222 (4 177-4 614) | 3 929 (3 905-4 160), 4 144 (4 088-4 318), 4 314 (4 094-4 376) |

  Cold is faster in every pair, by 0.7-1.0 s (less to download and no ICU data to
  load). Warm differs by -0.2 to +0.1 s: five of the six pairs are slightly faster
  (ru pair 1, 4 197-4 358 against 4 002-4 138, does not even overlap), so a warm
  gain of at most about 0.2 s is possible but the repeats do not establish it. An earlier experiment on a quieter run of the same change gave warm
  -0.3 s and cold -1.1 s (before the asset-collection fix); a single pair on a
  loaded machine gave about -1 s for both, which these repeats do not support.
- **Stage 2, tried and reverted: no reflection JSON before the first render.**
  Reading browser storage through `[JSImport]` and yielding once in the islands
  before their account and storage calls moved the first `IJSRuntime` call (and
  its reflection-based JSON metadata) behind the ready mark. Paired runs gave
  between -0.3 s and nothing, within noise, and the yield only moved real start-up
  work behind the mark, so both were reverted (fix round). Only the runtime-start
  counter stays a `[JSImport]`. A `[JSImport]` write was rejected earlier: a write
  that never completes left the Mock island stuck on "Saving the results"
  (`MockFlowTests.Mock_OutboxWriteNeverCompletes_SummaryStillShows`).
- Tried without effect (under 1 KB): `MetricsSupport=false`,
  `StackTraceSupport=false`, `_AggressiveAttributeTrimming=true`. The other usual
  switches (`UseSystemResourceKeys`, `EventSourceSupport`,
  `HttpActivityPropagationSupport`, `UseSizeOptimizedLinq`, `DebuggerSupport`) are
  already the Blazor WebAssembly defaults.

## Open decisions

None for Plan 2. The transfer budget is met (1.94 MB for both languages with the
relinked runtime; without the `wasm-tools` workload the prebuilt runtime would make it
about 2.44 MB, so CI pins the workload and the Plan 3 image must too). The timing
question was decided by the owner (ADR 0014): the lab emulation keeps regression
budgets, and the spec's phone targets move to the Plan 3 acceptance. If the phone
measurement misses them, the options from the start-up profile come back as owner
decisions in Plan 3: AOT compilation, a server-rendered first view, or fewer methods
before the first render.

## The `wasm-tools` workload (CI and Plan 3)

The published client must be built with the `wasm-tools` workload, which relinks
the native runtime to what the app uses. With `InvariantGlobalization` that drops
the ICU code: `dotnet.native.wasm` is 468 KB Brotli relinked against 977 KB
prebuilt (ADR 0013). The workload is pinned to a workload set, which must belong to
the SDK's feature band, so the two move together: SDK 10.0.303 and workload set
10.0.303. `global.json` requests 10.0.203 and rolls forward to the newest installed
10.0 feature band, so on its own it would pick the runner's preinstalled 10.0.4xx.
The CI `release` job therefore overwrites the `sdk` block of its checkout's
`global.json` with `10.0.303` and `rollForward: disable` (never committed), installs
that SDK, fails when `dotnet --version` is not in the 10.0.3xx band, and runs:

```bash
dotnet workload install wasm-tools --version 10.0.303
```

The Plan 3 production image must do the same in its build stage, before
`dotnet publish`:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:<digest> AS build
RUN dotnet workload install wasm-tools --version 10.0.303
```

When the SDK moves to another feature band, the workload set version moves with it;
`dotnet workload search version` lists the sets available for the installed SDK.
The relink makes a publish noticeably slower (it compiles and links the native
runtime; about a minute more locally). With the workload installed,
`WasmBuildNative=false` is refused while `InvariantGlobalization` is on, so a build
without relinking can only come from a machine without the workload.

## Measure on the deployed site

The Plan 3 acceptance measurement (ADR 0014), on a real mid-range Android phone over
4G (mobile data, Wi-Fi off), Chrome, against the deployed HTTPS site:

1. Cold, 5 runs: open a new Incognito tab (empty cache; close every Incognito tab
   between runs) and open `https://<site>/ru/dotnet/practice?perf=1`. When the island
   is ready, the readout in the bottom right corner shows
   `ready <ms> ms · _framework <bytes> B in <n> files`; write down the milliseconds.
2. Warm, 5 runs: in a normal tab, open the same URL once; then for each run open
   `https://<site>/ru/about` and type the practice URL again in the address bar (the
   bytes should be close to 0).
3. Repeat with `/en/`. Record the medians, the phone model, Android and Chrome
   versions and the network in the Results table, marked "phone, deployed".
4. Accepted when every median is under 5 000 ms cold and 2 000 ms warm.

## Measure on your phone (optional, before deployment)

The detailed method for a phone before anything is deployed. The published build runs on the computer, and an Android
phone opens it over USB as `http://localhost:5080`. Chrome treats `localhost` as a
secure context, so Web Locks, the outbox and the whole island path work as in
production (only HTTP/1.1 instead of HTTP/2, see Known gaps).

1. On the computer, publish and start the app (PowerShell; the database is the one
   from "Running it locally", sign-in is not needed for Practice). The workload
   install writes into the SDK folder, so on Windows run it in an elevated shell,
   and `--version 10.0.303` exists only for an SDK in the 10.0.3xx band: check that
   `dotnet --version` prints `10.0.3xx` first (for example `10.0.303`); with another
   band, `dotnet workload search version` lists the sets that fit.

   ```powershell
   dotnet --version
   dotnet workload install wasm-tools --version 10.0.303
   dotnet publish src/Trainer.Web -c Release -o artifacts/publish
   $env:ASPNETCORE_ENVIRONMENT = "Testing"
   $env:ASPNETCORE_URLS = "http://localhost:5080"
   $env:ConnectionStrings__Trainer = "Host=localhost;Port=55432;Database=dit_release;Username=postgres;Password=dev"
   $env:App__ContentRoot = "$PWD/content"
   $env:App__PublicBaseUrl = "http://localhost:5080"
   $env:TelegramOidc__ClientId = "1000"
   $env:TelegramOidc__ClientSecret = "phone-measurement"
   Push-Location artifacts/publish; dotnet Trainer.Web.dll; Pop-Location
   ```

2. On the phone: Settings, About phone, tap "Build number" seven times; then
   Developer options, turn on "USB debugging". Connect the cable and allow the
   computer.
3. On the computer, open `chrome://inspect/#devices` in desktop Chrome, click
   "Port forwarding...", add port `5080` to `localhost:5080`, tick "Enable port
   forwarding" and click "Done". Keep that tab open.
4. On the phone, in Chrome, open
   `http://localhost:5080/ru/dotnet/practice?perf=1`. When the island is ready, a
   small readout appears in the bottom right corner:
   `ready <ms> ms · _framework <bytes> B in <n> files`. The milliseconds are from
   navigation start to `data-island-ready`; the bytes are what the `_framework/`
   files cost over the network (0 for files from the cache). `?perf=1` is the only
   way to show it.
5. **Cold**, 5 times: open a new Incognito tab (it starts with an empty cache;
   close all Incognito tabs between runs) and load the URL from step 4. Write down
   the milliseconds and the bytes.
6. **Warm**, 5 times: in a normal tab, load the URL from step 4 once, then for each
   run open `http://localhost:5080/ru/about` and type the URL from step 4 again in
   the address bar. The bytes should be close to 0.
7. Repeat steps 5 and 6 with `/en/` instead of `/ru/`, and note the phone model,
   Android and Chrome versions and the network (USB forwarding has no mobile-network
   latency; for a 4G reading, open the same URL through a tunnel later, Plan 3).
8. Report the medians next to the emulated rows in the table above, marked
   "phone".

## Static edition: warm visits on GitHub Pages

Pages sends `Cache-Control: max-age=600`, so without help a warm visit revalidates every file after ten minutes. The
content-addressed service worker (ADR 0026) answers fingerprinted files and content shards from its own cache, which
takes the warm path back to the nearly zero bytes of the server edition. The page fills the cache from Resource Timing
after the island is usable, so a first visit adds close to no traffic (the files are answered by the HTTP cache). The
worker is not part of the Playwright lab runs of this document; its effect is measured by the live checks after each
deployment.

## Static edition: Brotli runtime on GitHub Pages

Pages serves `.wasm` gzip at best, and the gzip cold set of an island page (2 784 251 bytes, measured in
`docs/static-edition-spike.md`) is over the 2.5 MB budget. With `App:StaticSite:BrotliRuntime` on, the page fetches the
`.br` files of the runtime and decodes them itself (ADR 0027): 2 350 695 bytes of `.br` plus about 67 KB of decoder
(`gzip -9`), 2 417 576 in all. Decoding adds main-thread time per file, so the live checks record the transfer and the
usable time of a cold load; the `_framework` transfer is held to the 2 500 000-byte budget by the static smoke suite in CI
(`docs/static-edition.md`). The `.br` and fallback URLs are fetched in the default cache mode because they are content-addressed. The runtime passes no integrity to the loader, so
the decoded bytes are verified against a table the export publishes (`js/boot-integrity.{hash}.mjs`, ADR 0027, decision 7).

## Static edition: browser checks of the exported site

Two groups of tests run the exported site in Chromium, served by the Pages emulator (`tools/Trainer.StaticSite`) under
its path prefix the way GitHub Pages serves a project site (`Cache-Control: max-age=600`, gzip, no other headers).

- **`Category=E2E`, `tests/Trainer.Web.E2E`** (`StaticSiteTests`, `StaticServiceWorkerTests`, `StaticCatalogFilterTests`,
  `StaticDeploymentTests`, `StaticBrotliBootTests`): functional checks of a fixture export of the Debug build. They cover the
  language chooser, the islands running from browser storage, the catalog filter against the server catalog, the service
  worker, a second deployment while pages of the first are open or cached, the recovery banner, and the page's request and
  cookie hygiene. They record no timings. A Debug build ships no pre-compressed files, so the fixture writes the `.br` files
  itself; the integrity table (below) comes from the app's endpoint manifest like a Release build's, so the island really boots
  from the decoded files (`Brotli_BootsOnANestedRoute` asserts no plain `.wasm` is requested), and the failure paths of the
  Brotli loader are driven through the `loadBootResource` function the page hands to Blazor.
- **`Category=StaticSmoke`, `tests/Trainer.Web.Release`** (`StaticSmokeTests`, `StaticIslandPerformanceTests`): the Release
  export of a deployment. They need `DIT_STATIC_SITE_DIR` (the export, absolute) and `DIT_STATIC_SITE_URL` (the public URL it was
  made for); `DIT_PREVIOUS_PAGE` is the live Practice page of the previous build (served so that page runs against this
  artifact's retained files), and `DIT_STATIC_SMOKE_REQUIRED=1` turns a missing input into a failure (CI sets it). The host
  compresses nothing here: the runtime is the `.br` set, which is also the largest transfer. The worker is active in these
  runs, so the warm numbers include its cache. `DIT_PERF_RUNS` lowers the number of runs of a timing (default 5) on a loaded
  machine.

The performance tests apply the same emulated phone as above and hold the transfer to the same budget: `_framework/` at most
2 500 000 bytes (always blocking), usable at 8 500 ms cold and 4 750 ms warm (blocking only with `DIT_PERF_ENFORCE=1`).
"Usable" is `data-island-ready`, which in the static edition waits for the content index and the replay of the browser's
answer log; "shell" is the first render (`data-island-shell-ms`). Transfer is what the host sent, so it includes the files
the service worker's fill asked for after the load. Besides the cold and warm runs the suite checks that a late index or a
late first question delays `data-island-ready` (a shell alone cannot pass), runs with 10 000 answers seeded into
`dit.outbox.v1.anon` (usable within the same budgets; a Start pressed at once begins a session in under 500 ms for `all`,
`new` and `due`; a `?first=` link shows its question at least a second before the progress, with the log read held back so
that the replay is not already running on the page's one thread) and times one answer appended to that log. The ten-thousand
answer timings start from the progress checkpoint a returning visitor has (ADR 0028); the one load that has none and replays
the whole log is timed on its own and recorded as `firstLoadWithoutCheckpointMs`. Every row of `TestResults/perf/perf.json` carries `edition: static`, and the practice rows the size of the index.

```bash
export DIT_STATIC_SITE_DIR=/abs/path/to/export DIT_STATIC_SITE_URL=https://owner.github.io/dit/ DIT_STATIC_SMOKE_REQUIRED=1
dotnet test --project tests/Trainer.Web.Release --filter-trait "Category=StaticSmoke"
```

Local measurement of the fix round (2026-11-18, a Release publish exported with the Pages configuration, three runs per
timing, the emulated phone above; advisory, on a 12-CPU desktop that was shared with other work, so the figures are not a CI
figure):

| | cold usable (median) | warm usable (median) | shell (cold median) |
|---|---|---|---|
| Practice, empty log | 12 492 ms | 8 903 ms | 10 096 ms |
| Practice, 10 000 answers, with the stored checkpoint | 12 721 ms | 9 378 ms | 9 689 ms |
| Practice, 10 000 answers, the one load that has no checkpoint | 20 003 ms | | |

- **Transfer.** `_framework/` is 2 486 240 bytes in 50 files, under the 2 500 000-byte budget (the table `js/boot-integrity`
  is part of it). Before the fix every decoded file was refused and the plain file was downloaded as well: 8 045 938 bytes.
- **Why the runtime's integrity was empty.** The published boot configuration inside `dotnet.js` lists a sha256 per runtime
  file, but `dotnet.js` of the pinned SDK (10.0.203) copies the field `hash` of each resource to the `integrity` argument of
  `loadBootResource`, and the configuration names it `integrity`. No setting in this repository's projects changes that. The app therefore exports
  the table itself (ADR 0027, decision 7), built from the sha256 the build already computed for each published file.
- **10 000 answers.** The replay was 1.0 to 1.3 s of a load; the rest was reading the log into .NET (a document parse of 1.4 to
  2.3 s, one DTO per entry 3.6 to 5.4 s, the ids 0.6 to 0.8 s). The checkpoint (74 KB) replaces all of it, so a log of ten
  thousand answers now costs about the same as an empty one (cold plus about 0.2 s, warm within the noise of the runs). An
  appended answer takes 672 ms from the click to the write being reported, 135 ms of it in `outbox.js`, where it was 811 ms.
  The one load without a checkpoint (every visitor's first load after this change) still replays everything and takes
  20 s on the emulated phone, about half of it a page that cannot draw because the replay holds its one thread.
- **The budgets on this machine.** The 8 500 ms cold and 4 750 ms warm budgets are not met with `DIT_PERF_ENFORCE=1`, and the
  ten thousand answers are not the reason: the same machine measures the server edition's Practice page at 9 248 ms cold and
  5 393 ms warm (en, five runs, same publish), already over the budgets that were set at about 7.7 s and 4.3 s. Against the
  server edition the static island is about 3 s slower cold and 3.5 s warm. Measured pieces of that: decoding the `.br`
  files costs about 0.95 s per load (46 files, the decoder runs on the main thread, 4x slowed) and the response built from
  decoded bytes has no compiled-code cache, so the runtime compiles again on every load; from the shell to the usable mark
  the page spends 2.2 to 3.1 s that is not the log (the index and the first renders). These are unmeasured beyond that and
  are the next place to look; they are not caused by the answer log.

## Known gaps

- **iOS Safari ignores `interactive-widget=resizes-content`.** Island pages ask
  for it so the on-screen keyboard resizes the layout instead of covering the
  field being typed in. Chromium-based browsers honour it; Safari on iOS does not,
  so there the keyboard overlays the page. `islands.css` pads the sticky action
  bar with `env(safe-area-inset-bottom)` and `islands-start.js` scrolls the
  focused field into view on resize, which keeps the field visible but can leave
  the action bar under the keyboard until it is closed. The measurements above
  are Chromium only; no WebKit run is part of the suite.
- **The measured server speaks HTTP/1.1.** Kestrel on `http://localhost` has no
  HTTP/2 (that needs TLS), so Chromium opens at most 6 connections and the
  emulated 150 ms latency is paid per request: the 52 runtime requests of a cold
  load queue in about 9 waves. Production sits behind an HTTP/2 proxy (Plan 3),
  which multiplexes them, so the cold numbers here are pessimistic by roughly a
  second. Warm loads come from the cache and are not affected.
- The profile throttles one Chromium tab; it does not reproduce a phone's
  slower JavaScript engine start-up or memory pressure beyond the 4× CPU factor.
