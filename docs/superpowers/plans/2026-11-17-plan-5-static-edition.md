# Plan 5: Static Edition on GitHub Pages - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Repository path when committed: `docs/superpowers/plans/2026-11-17-plan-5-static-edition.md`. Revised after the Codex
(C1-C20), Fable (C1, M1-M10, m1-m17), Codex round 2 (R2-1 to R2-13), Codex round 3 (R3-1 to R3-8), Fable round 2
(N1-N5) and Codex round 4 (R4-1, R4-2) reviews, then the pre-flight conflict scan
(`.superpowers/sdd/2026-11-17-plan-5-static-edition/preflight.md`); the disposition of every finding is at the end.

**Goal:** A second, fully static edition of the site, built in CI from this repository and published on GitHub Pages at
`https://platonenkov.github.io/dev-interview-trainer/`, with every public page pre-rendered, Practice, Mock, Review and
profile working from browser storage, content shipped as content-hashed per-question JSON, "Report a problem" as a
prefilled GitHub issue, GoatCounter analytics, safe A-to-B deployments, and spec 8.2's phone targets measured on the
deployed site; the server edition of Plan 3 stays in the repository and keeps passing its suites.

**Architecture:** A spike first proves that a published island boots from a plain static host under the prefix and
fixes the compression decision. Then `App:Edition=Static` switches the existing Blazor Web App into a static-safe mode
(no sign-in, no cookies, no response headers, meta CSP, GoatCounter, data endpoints for content shards, a generated
service worker) and the path of `App:PublicBaseUrl` becomes the request path base, written by one `SitePaths` service.
CI publishes the app deterministically in Release, runs it in the `StaticExport` environment over a throw-away
PostgreSQL that the normal startup import fills, and a crawler (`tools/Trainer.StaticSite`) saves every page, asset,
Open Graph image and content shard, carries the previous deployment's immutable files forward, verifies the result
structurally, and `pages.yml` deploys it with `actions/deploy-pages`. In the WebAssembly client one seam,
`IIslandBackend`, has a server implementation (today's `/api` calls and outbox rules) and a static one (index plus
per-question files, progress recomputed from the full anonymous answer log through the domain scheduler).

**Tech Stack:** .NET 10, ASP.NET Core (static SSR + InteractiveWebAssembly islands), EF Core + Npgsql (export only),
SkiaSharp (Open Graph, unchanged), AngleSharp (export verification only, in the tool; MIT, check the licence before
adding), xUnit v3 on Microsoft.Testing.Platform, Testcontainers.PostgreSql, Playwright (Chromium), GitHub Actions
(`actions/upload-pages-artifact`, `actions/deploy-pages`), GoatCounter, gitleaks (pre-public audit, run once).

**Spec:** `docs/superpowers/specs/2026-11-17-static-edition-design.md` ("static 5.x") and the main spec
`docs/superpowers/specs/2026-11-11-dev-interview-trainer-design.md` ("main x.y"). ADRs 0001-0021 bind this plan,
especially 0009, 0011, 0012 (import map nonce, replaced by a hash in the static edition), 0013 (`wasm-tools`), 0014
(phone targets are an acceptance criterion) and 0018 (cookieless analytics).

## Roadmap (all plans)

| Plan | Scope | Depends on |
|---|---|---|
| 1 (done) | Solution, content pipeline, public SSR site, CI | - |
| 2 (done) | Sign-in, learning modes, islands, admin | 1 |
| 3 (done, not deployed) | Server deployment, feedback, Open Graph, Umami, operations | 1, 2 |
| 4 (running) | Content production | 1 |
| **5 (this)** | Static edition on GitHub Pages, going public, Pages phone acceptance | 1, 2, 3 |

Each plan is reviewed by Codex and Fable before execution.

## Global Constraints

Carried over from Plans 1-3 (unchanged): `net10.0`, SDK per `global.json`, nullable, warnings as errors, central
package management; explicit types instead of `var`; async with `CancellationToken`; `TimeProvider` instead of
`DateTime.Now`; `StringComparison.Ordinal*`; source-generated `System.Text.Json` contexts; comments explain why; xUnit v3
with the built-in `Assert`, no FluentAssertions, no Moq, no bUnit; Conventional Commits; English code, docs and commits;
no AI attribution anywhere; no long dashes in docs; LF line endings, never change a file's encoding; every new UI string
in RU and EN (`SharedResource`, `ClientResource`, key-parity tests) following the `writing-site-copy` rules; WCAG 2.2 AA,
colours only through `tokens.css`, 44 px touch targets, no horizontal scroll at 320 px; Web.Client references Domain and
Contracts only; Web never references Snippets; host-only types stay out of `Trainer.Contracts` (it ships to the
browser); every ADR is written by the task that introduces it, numbered in task order without gaps, linked from
`docs/architecture.md` in the same task; `docs/architecture.md`, `README.md` and `AGENTS.md` change in the task that
changes what they describe; an `AGENTS.md` rule is written by the task that introduces the behaviour it governs. The
test commands in `AGENTS.md` and `ci.yml` do not change. Every test listed in a task is written with real assertions:
the test name, or the one-line comment next to it, states what is asserted; an empty body is not a test, and Step 2's
FAIL means a failing assertion, not only a compile error. A test project that a task changes code for (production or
test helper) is in that task's run list.

Verification rhythm: each task runs the test projects that cover the code it touches (named in its last step). The
complete regression set (`dotnet build -warnaserror`, the unit/integration command, Docker, E2E and Release categories,
`dotnet run --project tools/Trainer.ContentCli -- validate`) runs at three gates: end of Task 12 (client complete), end of
Task 19 (docs), and before Task 20 (going public), plus CI on every pull request.

Plan 5 constraints (owner decisions of 2026-11-17, quoted from static section 3):

- Hosting: "GitHub Pages at the project URL https://platonenkov.github.io/dev-interview-trainer/ - every link, asset, OG URL, sitemap and the Blazor WASM base path must work under the /dev-interview-trainer/ prefix. Keep the base path a single build setting so a custom domain later is a one-line change." The setting is `App:PublicBaseUrl`; in CI it is the repository variable `SITE_URL`, read by `ci.yml` and `pages.yml` and passed to `static-site.yml`; no workflow file contains the URL, and every other URL-derived value is computed from it.
- "The repository will be made public (code MIT, content CC BY-SA 4.0) ... Making it public is the owner's own click."
- Content: "Per language: a small index file ... plus one prebuilt JSON per question ... produced by the same validation/render pipeline the server import uses today, published on Pages itself (never fetched from raw.githubusercontent.com). File names or URLs carry a content hash ... Sessions fetch only the selected questions and prefetch the next one."
- Feedback: "Report a problem" opens a prefilled GitHub issue with question id, language and page URL. "No like/dislike in the static edition."
- Analytics: "GoatCounter (cookieless, script allowed in CSP). Self-hosted Umami is not used by the static edition."
- "Anything that requires sign-in must be hidden or explained, not broken."
- "No scheduled/weekly heavy CI jobs." `pages.yml` has no `schedule`.
- Server edition: with `App:Edition` unset its HTML, headers, URLs and SQL session queries are unchanged (the existing page, header, cache, SEO and `SessionQueriesTests` are the guard), except the theme key `dit.theme` (static 5.3).
- `App:Edition=Static` starts only in the `StaticExport`, `Development` and `Testing` environments.
- In the static edition the app sets no cookie on any response and sends no `Content-Security-Policy`, `X-Content-Type-Options` or `Referrer-Policy` header.
- Static page URLs end with `/` and are exported as `<path>/index.html`; file URLs keep their exact names.
- Content file names: `{name}.{hash}.json`, `hash` = first 16 lowercase hex characters of SHA-256 of the file bytes.
- Every Release publish for the static site uses `-p:IncludeSourceRevisionInInformationalVersion=false -p:InformationalVersion=static` (a constant, never the commit); two publishes of different commits with equal `src/` produce byte-identical `wwwroot` files and identical fingerprint mappings. The deployment's identity is a build id (`<commit>.<run id>.<attempt>`) written to `build.json`, `immutable.json` and a `<meta name="dit-build">` in every exported page, never into an assembly.
- Owner decisions of 2026-11-17 (answers): the sitemap is submitted to Google Search Console and Yandex Webmaster in Task 21 with meta-tag verification; progress loss on a custom-domain move is accepted, no export or import; a missed phone target is remedied per Plan 3 owner decision 4 (cut work before the first render first, no payload growth; AOT only if the warm target alone is missed).
- Phone targets (main 8.2, ADR 0014): Practice usable (static 5.9) within 2 s warm and 5 s cold on a mid-range phone over 4G, medians of 5 runs, RU and EN, `?perf=1`, on the deployed Pages site.
- Workflows: actions pinned by full commit SHA (resolve each tag with `gh api repos/<owner>/<action>/git/ref/tags/<tag>` at implementation time; keep the tag as a comment), `permissions:` minimal on the workflow and each job, inputs only through `env:`, no `pull_request_target`, no secrets in `pages.yml`.

## Review Focus

1. **A deployment while pages of the previous build are open or cached** (cached HTML naming old runtime, CSS and content files; partially cached assets; no service worker; a service worker update). Expect: A's pages keep working for one deployment; older pages show "The site was updated" and recover with one reload that fetches fresh HTML; never a mixed index and question set. Tests: Task 17 `StaticDeploymentTests.AtoB_CachedIslandPageOfAStillBoots`, `AtoB_WithoutServiceWorker`, `CodeUpdate_OldTabOpen_GetsItsOwnBytes`, `Missing_StartupScript_ShowsRecovery`, `Missing_BootModule_ShowsRecovery`, `Missing_Assembly_ShowsRecovery`, `Missing_Stylesheet_ShowsRecovery`, `AtoB_DeletedQuestionShowsSiteUpdated`; StaticSmoke `PreviousLiveIslandPage_BootsFromThisArtifact` (a real previous Release build); Task 16 `RetentionTests.*`.
2. **One URL that escapes the prefix** (Razor literal, CSS `url()`, import map, persisted state, a script string, a client-built link with a query, Open Graph or sitemap URL, `?perf=1`, the worker scope). Expect: the build fails before deploy. Tests: Task 4 `PathBaseTests.EveryRootRelativeUrlCarriesThePathBase`; Task 5 `Css_UsesNoRootRelativeUrls`, `IslandLinksTests.PageWithQuery_IsBaseRelativeAndSlashed`; Task 16 `ExportVerifierTests` (structural) and the verify step of every export; Task 17 `StaticSiteTests.EveryBrowserRequestStaysUnderThePrefixOrIsGoatCounter`; Task 16 export test for the root base URL `/`.
3. **Query strings on static pages** (`?level=JUNIOR&level=junior&topic=unknown`, `?first=<id>`, `?q=` with control characters, `?status=due` with no history, the recovery `_r=` parameter). Expect: the server's normalisation, an empty pool shown as the empty state, never an exception. Tests: Task 11 `PracticeQueryStringParityTests.ClientParserMatchesCatalogQueryParser`, `StaticIslandBackendTests.Practice_DueStatusWithoutHistory_IsEmpty`; Task 17 `StaticCatalogFilterTests.JunkQueryShowsTheNormalisedSelection`, `CatalogSearch_MatchesTitleSubstringOrExactTag`.
4. **Browser storage that is missing, full, written by another tab, or large** (private mode, Safari eviction, two tabs, a visitor with thousands of answers, a device clock moved back). Expect: answering works in memory with the existing `StorageNotice`; two tabs never lose answers; start-up time does not grow with history; a corrected device clock never moves a stored answer's anchor. Tests: Task 11 `LocalProgressTests.Replay_LateBackdatedAnswer_TakesItsChronologicalPlace`, `Replay_SameLogSameStatesAcrossDaysAndReloads`, `Replay_KeepsStatesOfOtherStacksAndLanguages`, `Recompute_AfterAnotherTabWrites`; Task 12 `IslandComponentRenderTests.StaticProfile_MemoryOnlyStorage_ShowsStorageNotice`; Task 17 `StaticSiteTests.TwoTabs_KeepBothAnswers`, StaticSmoke `Practice_UsableWith10000Answers_WithinBudget`, `Practice_StartImmediatelyAfterUsable_AllNewDueAndFirst`.
5. **The service worker on a shared origin and under storage pressure** (neighbour sites on `platonenkov.github.io`, quota errors, hundreds of question files, mutable aliases, a code update while an old tab is open, a return after the HTTP cache expired). Expect: scope exactly the prefix, the cache name carries the scope, only content-addressed files cached, runtime never evicted by content, a failed cache write never fails a request, the first return visit warm, an old tab still gets its own bytes. Tests: Task 14 `ServiceWorkerTests.*`; Task 16 `ExportVerifierTests.EligiblePatternMatchesOnlyImmutableFiles`, `RetainedNameWithDifferentBytes_Fails`; Task 17 `StaticSiteTests.ServiceWorker_SecondVisitServesRuntimeFromCache`, `ServiceWorker_ReturnAfterHttpCacheCleared_IsServedFromCache`, `ServiceWorker_MutableAliasNeverCached`, `ServiceWorker_ContentBrowsingDoesNotEvictRuntime`, `ServiceWorker_QuotaFailureStillServesTheResponse`, `ServiceWorker_NavigationWithoutPreload_FallsBackToFetch`, `StaticDeploymentTests.CodeUpdate_OldTabOpen_GetsItsOwnBytes`.

---

## File Structure

```
docs/static-edition-spike.md (spike record)                                                                    (Task 1)
src/Trainer.Domain/Sessions/  PracticeCandidate, PracticeSelection, PracticeSelector, ReviewOrder, PracticeStatus,
                              MockComposer (moved), SessionLimits (moved)                                     (Task 2)
src/Trainer.Web/Startup/SiteEdition.cs, EditionStartupCheck.cs; Seo/SitePaths.cs                               (Task 3)
src/Trainer.Web/Components/** (URL conversion)                                                                (Task 4)
src/Trainer.Web.Client/Interop/ClientEdition.cs; Islands/IslandLinks.cs; wwwroot/js, css                        (Task 5)
src/Trainer.Web/Routing/StaticRootEndpoint.cs; Components/Pages/LanguageChooser.razor; Auth/IslandPolicies.cs;
    Security/NoAntiforgeryStateProvider.cs                                                                     (Task 6)
src/Trainer.Web/Components/Shared/StaticFilterForm.razor; wwwroot/js/catalog-filter.js                         (Task 7)
src/Trainer.Contracts/StaticSiteContracts.cs; Application/StaticSite/IStaticSiteQueries.cs;
    Infrastructure/StaticSite/StaticSiteQueries.cs; Web/StaticSite/StaticContentCatalog.cs, StaticContentHash.cs,
    StaticContentEndpoints.cs, StaticSiteOptions.cs                                                           (Task 8)
.github/ISSUE_TEMPLATE/question-report.yml; Contracts/ProblemReportLink.cs; ReportProblemLink.razor (Web, Client) (Task 9)
src/Trainer.Web.Client/Backend/ IIslandBackend, ServerIslandBackend, SessionPlan, IQuestionSource,
    PreloadedQuestionSource, IslandProgress                                                                    (Task 10)
src/Trainer.Web.Client/Backend/ StaticIslandBackend, StaticContentClient, StaticQuestionSource, LocalProgress,
    LocalProgressCache, StaticIndex, LocalProfileBuilder;
    Sessions/PracticeQueryString.cs                                                                         (Task 11)
src/Trainer.Web.Client/Storage/LocalMockHistoryStore.cs                                                       (Task 12)
src/Trainer.Web/Startup/StaticCsp.cs, RecoveryScript.cs; Components/Shared/StaticHeadMeta.razor, SiteUpdatedBanner.razor;
    Analytics/GoatCounterScript.cs                                                                             (Task 13)
src/Trainer.Web/StaticSite/StaticAssetList.cs, StaticAssetRules.cs, ServiceWorkerEndpoint.cs;
    wwwroot/js/sw-template.js, sw-register.js                                                                  (Task 14)
src/Trainer.Web/wwwroot/js/vendor/brotli-decode.mjs (only if Task 1 chose Brotli)                               (Task 15)
src/Trainer.Web/StaticSite/StaticExportEndpoints.cs; tools/Trainer.StaticSite/; tests/Trainer.StaticSite.Tests/ (Task 16)
tests/Trainer.Web.E2E/Static*.cs; tests/Trainer.Web.Release/Static*.cs                                          (Task 17)
.github/workflows/static-site.yml, pages.yml; .github/scripts/pages-gate.sh; ci.yml job static-site;
    tests/Trainer.Deploy.Tests/PagesWorkflowTests.cs                                                           (Task 18)
docs/static-edition.md; docs/adr/0022..0027                                                                    (Tasks 3-19)
```

---

### Task 1: Spike: a published island on a plain static host, and the compression decision

No production code. The result decides whether Tasks 3-18 stand as written; a failed gate stops the plan for a
redesign with the owner.

**Files:**
- Create: `docs/static-edition-spike.md` (the record), `tools/spike/serve-static.ps1` (a 30-line PowerShell static server or the `dotnet-serve` global tool command line, whichever needs no new repository dependency; deleted again in Task 16)

**Interfaces:**
- Produces: the decisions later tasks read: (a) boot works / does not; (b) the list of every URL the island requested; (c) `COMPRESSION=host-gzip` or `COMPRESSION=brotli-decode` with the numbers behind it; (d) the determinism result.

- [ ] **Step 1:** Publish twice with the SDK and `wasm-tools` pins of `ci.yml`'s `release` job and the constant version: `dotnet publish src/Trainer.Web -c Release -o artifacts/spike-a -p:IncludeSourceRevisionInInformationalVersion=false -p:InformationalVersion=static`, then the same into `artifacts/spike-b` from a second commit that changes only a file under `content/`. Compare the SHA-256 of every file under `wwwroot/` and the fingerprint mappings in `Trainer.Web.staticwebassets.endpoints.json`. Also record the exact fingerprint format of every fingerprinted name (characters and length, for `_framework/` and for `css/`, `js/`, `fonts/`), which Task 14 turns into `StaticAssetRules`. Gate: identical. If a file differs, find the input that leaks into its bytes (for example `SourceRevisionId` through SourceLink, a build timestamp) and record the extra property that removes it; Task 18 adds it to the publish command.
- [ ] **Step 2:** Run `artifacts/spike-a/Trainer.Web` against a local PostgreSQL (`dotnet ef database update`, Testing environment, `App:PublicBaseUrl=http://localhost:8099/`), save `/en/dotnet/practice` and every file it references by `curl`, stop the process. By hand: put the files under `site/dev-interview-trainer/`, change `<base href>` and every root-relative URL to the prefix, replace the import map nonce with a meta CSP carrying the import map's SHA-256 and `'wasm-unsafe-eval'` as static 5.8 specifies, serve `site/` on port 8099.
- [ ] **Step 3:** Open `http://localhost:8099/dev-interview-trainer/en/dotnet/practice/` in Playwright Chromium (a throw-away script under `artifacts/`, not committed). Gate: `.island-root[data-island-ready='true']` within 30 s; no request outside `/dev-interview-trainer/`; no request to any `resource-collection` URL or other runtime-generated endpoint; no CSP violation; record the full request list.
- [ ] **Step 4:** Compression numbers for the cold-load file set of Step 3: sum of raw sizes, `gzip -6` and `gzip -9` sizes, the `.br` sizes from the publish, and the size of Google's `decode.js` module (MIT) gzipped. Host evidence: find a public `*.github.io` page served directly by GitHub (response headers `Server: GitHub.com`, no third-party CDN) that serves a `.wasm` of at least 100 KB, and record `Content-Encoding` with `Accept-Encoding: gzip, br`. Decision rule, recorded: `host-gzip` when the host evidence shows gzip and the `gzip -6` cold sum is at most 2 300 000 bytes (8% under the 2.5 MB budget, for the difference between our gzip level and the CDN's); otherwise `brotli-decode`. Also record the warm-path facts: the `Cache-Control` the host sends (expected `max-age=600`).
- [ ] **Step 5:** Commit `docs: record the static edition spike` (the record and the serve script only).

### Task 2: Session selection rules in the domain (server SQL unchanged)

**Files:**
- Create: `src/Trainer.Domain/Sessions/PracticeCandidate.cs`, `PracticeSelection.cs`, `PracticeSelector.cs`, `ReviewOrder.cs`
- Move (namespace `Trainer.Domain.Sessions`, bodies unchanged): `src/Trainer.Application/Sessions/MockComposer.cs`; `SessionLimits` and `PracticeStatus` out of `SessionCriteria.cs`; `tests/Trainer.Application.Tests/MockComposerTests.cs` → `tests/Trainer.Domain.Tests/`
- Modify: usings in `src/Trainer.Application/Sessions/*.cs`, `src/Trainer.Infrastructure/Sessions/SessionQueries.cs`, `DependencyInjection.cs`, `src/Trainer.Web/Api/SessionEndpoints.cs`, `ApiRequestParser.cs`, `src/Trainer.Web.Client/Sessions/PracticeFilterSet.cs`. The SQL in `SessionQueries` does not change.
- Test: `tests/Trainer.Domain.Tests/PracticeSelectorTests.cs`, `ReviewOrderTests.cs`, `tests/Trainer.Infrastructure.Tests/SessionRulesParityTests.cs`

**Interfaces:**
- Produces (`Trainer.Domain.Sessions`; named `PracticeCandidate` because `Trainer.Infrastructure.Sessions.SessionCandidate` already exists):
  - `sealed record PracticeCandidate(QuestionId Id, Level Level, TopicKey Topic, QuestionType Type)`; `enum PracticeStatus { All, New, Due }` (moved, same values).
  - `sealed record PracticeSelection(IReadOnlyList<Level> Levels, IReadOnlyList<TopicKey> Topics, IReadOnlyList<QuestionType> Types, PracticeStatus Status, int Count, QuestionId? First, IReadOnlyCollection<QuestionId> Exclude)`; `sealed record PracticePick(IReadOnlyList<QuestionId> Ids, int PoolSize)`.
  - `sealed class PracticeSelector(Random random) { PracticePick Select(IReadOnlyList<PracticeCandidate> live, PracticeSelection selection, IReadOnlyDictionary<QuestionId, DateTimeOffset>? dueAtById, DateTimeOffset now); }`: the rules of `SessionQueries.PracticePool` (empty filters do not restrict; `Exclude` in every status; `New` with null states matches everything not excluded, otherwise questions without a state; `Due` with null states matches nothing, otherwise `dueAt <= now`; `First` when live is in the pool whatever the filters and exclusions, counts toward `PoolSize` and `Count`, comes first); the rest in uniform random order; count and exclusion limits as `SessionLimits`.
  - `static class ReviewOrder { (IReadOnlyList<QuestionId> Ids, int PoolSize) Pick(IEnumerable<(QuestionId Id, DateTimeOffset DueAt)> states, IReadOnlySet<QuestionId> live, DateTimeOffset now, int count); }`: due and live only, `DueAt` ascending, then id ordinal.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Select_EmptyFilters_PoolIsEveryLiveQuestion() { /* 10 live, Count 5: 5 distinct ids, PoolSize 10 */ }
[Fact] public void Select_FirstComesFirstEvenWhenFilteredOutOrExcluded() { /* First is senior, Levels=[Junior], Exclude=[First]: Ids[0]==First, PoolSize = juniors + 1 */ }
[Fact] public void Select_FirstNotLive_IsIgnored() { /* same Ids count and PoolSize as without First; First absent */ }
[Fact] public void Select_NewWithoutStates_UsesOnlyExclude() { /* dueAtById null: pool = live minus Exclude */ }
[Fact] public void Select_NewWithStates_LeavesOutQuestionsWithAState() { /* pool = live minus the ids in dueAtById */ }
[Fact] public void Select_DueWithoutStates_MatchesNothingButFirst() { /* pool is [First], or empty without First */ }
[Fact] public void Select_Due_MatchesStatesDueAtOrBeforeNow() { /* states at now-1s, now, now+1s: the first two are in the pool */ }
[Fact] public void Select_IsUniformAcrossSeeds() { /* 2000 picks of 1 from 4: each 400..600 */ }
[Theory] [InlineData(0)] [InlineData(51)] public void Select_CountOutOfRange_Throws(int count) { /* ArgumentOutOfRangeException */ }
[Fact] public void ReviewOrder_MostOverdueFirstThenId_DueAndLiveOnly() { /* equal DueAt ordered by id ordinal; future and non-live states absent; PoolSize counts due live states */ }
// Infrastructure, the learning fixture database: for each status, filter combination, First and Exclude case,
// SessionQueries (SQL) and PracticeSelector/ReviewOrder over the same candidates and states yield the same pool
// (set of ids) and PoolSize; Review yields the same order.
[Theory, MemberData(nameof(Cases))] public async Task PracticePool_SqlAndDomainAgree(string name) { /* equal id sets and PoolSize for the named case */ }
[Fact] public async Task ReviewOrder_SqlAndDomainAgree() { /* equal ordered ids and PoolSize */ }
```

- [ ] **Step 2:** Run `dotnet test tests/Trainer.Domain.Tests` and `tests/Trainer.Infrastructure.Tests` → FAIL. **Step 3:** Move the types, implement. **Step 4:** Same plus `tests/Trainer.Application.Tests`, `tests/Trainer.Web.Tests`, `tests/Trainer.Web.Client.Tests` (`PracticeFilterSet` usings) → PASS.
- [ ] **Step 5:** Commit `refactor(sessions): share session selection rules through the domain`.

### Task 3: Edition switch, `SitePaths` and a path-aware public base URL

**Files:**
- Create: `src/Trainer.Web/Startup/SiteEdition.cs`, `EditionStartupCheck.cs`, `src/Trainer.Web/Seo/SitePaths.cs`, `docs/adr/0022-static-edition-on-github-pages.md`
- Modify: `Startup/AppOptions.cs` (`Edition`), `PublicBaseUrlValidation.cs`, `Seo/SiteUrls.cs`, `Program.cs` (`UsePathBase` first; register `SitePaths`; call `EditionStartupCheck`), `docs/architecture.md` (configuration table `App:Edition`, section "Static edition" in the Contents list, ADR 0022), `tests/Trainer.Web.Tests/DocsTests.cs` (expected keys)
- Test: `tests/Trainer.Web.Tests/SitePathsTests.cs`, `EditionStartupCheckTests.cs`, `PublicBaseUrlValidationTests.cs` (extend), `SiteUrlsTests.cs`

**Interfaces:**
- Produces:
  - `enum SiteEdition { Server, Static }`; `AppOptions.Edition` (`App:Edition`, default `Server`).
  - `static class EditionStartupCheck { const string StaticExportEnvironment = "StaticExport"; void EnsureValid(AppOptions options, IHostEnvironment env); }`: `Static` outside `StaticExport`, `Development`, `Testing` throws `InvalidOperationException`.
  - `PublicBaseUrlValidation`: a path is allowed; no query, no fragment, no `.`/`..` segment; trailing slash ignored.
  - `sealed class SitePaths(IOptions<AppOptions> options)`: `string PathBase` (`""` or `"/dev-interview-trainer"`); `bool SlashedPages` (`Edition == Static`); `string BaseHref` (`PathBase + "/"`); `string Page(string logicalPathAndQuery)` (prefix; static edition appends `/` to the path part before `?`; `"#x"` unchanged; `"/"` → `BaseHref`); `string Asset(string assetPath)` (prefix, leading slash optional, never slashed); `string CurrentPage(HttpRequest request)` (`PathBase + Path + QueryString`, for fragment links on island pages).
  - `SiteUrls.Origin` (scheme, host, port only); `SiteUrls.Absolute(HttpRequest?, string logicalPathAndQuery)` = origin + `Page(...)`; `SiteUrls.AbsoluteAsset(HttpRequest?, string assetPath)` = origin + `Asset(...)`.
- ADR 0022: the static edition and why (owner decision); one codebase, two editions; `App:Edition`; base path from `App:PublicBaseUrl`; slashed page URLs; the list of what is dropped (static 6); the origin-change consequence and that progress export/import is out of scope (static 5.6).

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("https://h.io/dev-interview-trainer/", "Static", "/ru/dotnet/questions?level=junior", "/dev-interview-trainer/ru/dotnet/questions/?level=junior")]
[InlineData("https://h.io/dev-interview-trainer", "Static", "/", "/dev-interview-trainer/")]
[InlineData("https://h.io/", "Static", "/ru", "/ru/")]
[InlineData("https://h.io", "Server", "/ru/dotnet", "/ru/dotnet")]
[InlineData("https://h.io/x", "Static", "#main", "#main")]
public void Page_PrefixesAndSlashesByEdition(string baseUrl, string edition, string logical, string expected) { /* SitePaths.Page(logical) == expected */ }
[Fact] public void Asset_IsPrefixedAndNeverSlashed() { /* "css/s.x.css" and "/css/s.x.css" both give "/dev-interview-trainer/css/s.x.css" */ }
[Fact] public void AbsoluteAsset_SitemapFileIsNotSlashed() { /* == "https://h.io/dit/sitemap-ru.xml" */ }
[Theory] [InlineData("Production")] [InlineData("Staging")] public void StaticEdition_RefusedOutsideExportDevelopmentAndTesting(string env) { /* InvalidOperationException; StaticExport, Development and Testing do not throw */ }
[Theory] [InlineData("https://h.io/x?y=1")] [InlineData("https://h.io/x#f")] [InlineData("https://h.io/a/../b")] public void PublicBaseUrl_WithQueryFragmentOrDotSegments_IsRejected(string url) { /* InvalidOperationException in StaticExport */ }
```

- [ ] **Step 2:** Run `dotnet test tests/Trainer.Web.Tests` → FAIL. **Step 3:** Implement; ADR 0022. **Step 4:** Same → PASS.
- [ ] **Step 5:** Commit `feat(web): add the static edition switch and path-aware URL building`.

### Task 4: Every server-written URL through `SitePaths`

Driven by the failing `PathBaseTests`, not by a text search (most links are `href="@PathFor(...)"`).

**Files:**
- Modify: `src/Trainer.Web/Components/App.razor` (`<base href="@Paths.BaseHref">` on island pages only; every `/@Assets[...]` → `@Paths.Asset(Assets[...])`; `data-edition="static"` on `<html>` in the static edition only, so server HTML is unchanged; the client treats a missing attribute as `Server`), `Directory.Packages.props` (`AngleSharp`, after checking its licence is MIT on nuget.org and its repository, recorded in the commit body), `tests/Trainer.Web.Tests/Trainer.Web.Tests.csproj` (AngleSharp reference), `AGENTS.md` (rule: every URL the server writes goes through `SitePaths`; logical paths inside, conversion at output), `Components/Layout/*`, every page and shared component that writes a URL, `Components/Shared/IslandLink.razor` (including `data-preload-runtime`), `Components/Layout/MainLayout.razor` (skip link on island pages via `Paths.CurrentPage`), `OpenGraph/OgImageLinks.cs` (`AbsoluteAsset`), `Endpoints/SitemapEndpoints.cs` (sitemap index and `robots.txt` name files with `AbsoluteAsset`; page `loc`s with `Absolute`), `Routing/LanguageEndpoint.cs` (redirect target through `SitePaths.Page`), `Auth/SignInRedirectEndpoint.cs` (same)
- Test: `tests/Trainer.Web.Tests/PathBaseTests.cs`, `SkipLinkTests.cs`

**Interfaces:**
- Consumes: `SitePaths`, `SiteUrls` (Task 3).
- Produces: unprefixed logical paths stay the currency inside the server (`CatalogQueryParser.ToUrl`, `PageLinks`, `CulturePath`, `PathFor`); conversion happens in markup and in `SiteUrls`.

- [ ] **Step 1: Write the failing tests** (factory settings `App:Edition=Static`, `App:PublicBaseUrl=https://example.test/dit/`)

```csharp
[Fact] public async Task EveryRootRelativeUrlCarriesThePathBase()
{
    // GET every public page of the fixture (home, stack home, catalog, a topic, a question, about, not-found, a
    // redirect source) and the Practice and Mock island pages under /dit/... (the root chooser and the Review and
    // profile pages join this list in Task 6, when they become reachable anonymously in the static edition); parse with AngleSharp (test dependency); every
    // href, src, srcset candidate, action, content of og:* and link rel=canonical|alternate, data-preload-runtime and
    // import-map value that is root-relative starts with "/dit/", every absolute https://example.test URL starts with
    // "https://example.test/dit/", and page URLs end with "/".
}
[Fact] public async Task Sitemaps_PageLocsSlashedFileLocsNot() { /* sitemap-ru.xml locs end with '/'; sitemap.xml locs and the robots.txt Sitemap line end with '.xml' */ }
[Fact] public async Task PublicPages_HaveNoBaseElement() { /* home, catalog, question: no <base> */ }
[Fact] public async Task ServerEdition_WithoutPath_KeepsTodaysUrls() { /* snapshot of hrefs from the existing LayoutTests pages */ }
[Fact] public async Task IslandPage_SkipLinkTargetsTheCurrentPrefixedPage() { /* skip link href == "/dit/en/dotnet/practice/#main" */ }
```

- [ ] **Step 2:** Run `dotnet test tests/Trainer.Web.Tests` → FAIL. **Step 3:** Convert until green. **Step 4:** `dotnet test tests/Trainer.Web.Tests` → PASS; E2E category → PASS (server edition unchanged, keyboard skip link included: `LayoutTests`).
- [ ] **Step 5:** Commit `refactor(web): write every URL through SitePaths`.

### Task 5: Client links, edition, scripts, styles and the theme key

**Files:**
- Create: `src/Trainer.Web.Client/Interop/ClientEdition.cs`, `src/Trainer.Web.Client/Islands/IslandLinks.cs`
- Modify: `src/Trainer.Web.Client/Program.cs` (read the edition before the builder, register `ClientEdition` and `IslandLinks` as singletons), `Interop/PageCulture.cs` (shares the `[JSImport]` module), every client link builder (`PracticeIsland.QuestionHref`, `ProfileIsland.ModeHref`, `TopicSidebar`, `IslandRoot`, `ImportNotice`, `MockIsland` "elsewhere" links, `SignInLink`), `src/Trainer.Web/wwwroot/js/islands-start.js` (`window.ditPageEdition`; the `?perf=1` readout counts `new URL('_framework/', document.baseURI).pathname`), `wwwroot/css/site.css` (`url("../fonts/...")`), `Startup/ThemeScript.cs` and `wwwroot/js/theme-toggle.js` (`dit.theme`)
- Test: `tests/Trainer.Web.Client.Tests/IslandLinksTests.cs` (`Page_*`), `tests/Trainer.Web.Tests/StaticAssetsTests.cs` (`Css_*`, `Scripts_*`, `ThemeScript_*`; they read `src/Trainer.Web/wwwroot` and `ThemeScript`)

**Interfaces:**
- Produces:
  - `enum ClientEdition { Server, Static }`; `static partial class ClientEditionReader { [JSImport("globalThis.ditPageEdition")] static partial string Read(); static ClientEdition Current(); }` (`Current` returns `Static` only for the value `static`; empty or missing means `Server`) called in `Program.cs` before `WebAssemblyHostBuilder.CreateDefault`, like `PageCulture.Apply`.
  - `sealed class IslandLinks(ClientEdition edition) { string Page(string culture, string stack, IReadOnlyList<string> segments, string? query = null); }`: base-relative without a leading slash (`ru/dotnet/practice/?topic=async` in the static edition, `ru/dotnet/practice?topic=async` in the server edition), resolved by the island page's `<base href>`.
  - Storage key `dit.theme` (architecture storage-key list).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Page_IsBaseRelativeAndSlashedOnlyInStatic() { /* Static "ru/dotnet/questions/s/", Server "ru/dotnet/questions/s", never a leading slash */ }
[Fact] public void PageWithQuery_IsBaseRelativeAndSlashed() { /* Static with query topic=async: "ru/dotnet/practice/?topic=async" */ }
[Fact] public void Css_UsesNoRootRelativeUrls() { /* no url("/ in wwwroot/css */ }
[Fact] public void Scripts_UseNoRootRelativeUrlLiterals() { /* no '/_framework or "/xx string literal in wwwroot/js except comments; wwwroot/js/vendor/ (third-party, Task 15) is excluded */ }
[Fact] public void ThemeScript_UsesTheNamespacedKey() { /* Source reads and theme-toggle.js writes 'dit.theme'; no bare 'theme' key */ }
```

- [ ] **Step 2:** Run `dotnet test tests/Trainer.Web.Client.Tests` and `tests/Trainer.Web.Tests` → FAIL. **Step 3:** Implement. **Step 4:** Same plus the E2E category (`IslandNavigationTests`, theme tests) → PASS.
- [ ] **Step 5:** Commit `refactor(client): base-relative island links and the client edition`.

### Task 6: Static-edition chrome, routing and access

**Files:**
- Create: `src/Trainer.Web/Routing/StaticRootEndpoint.cs`, `Components/Pages/LanguageChooser.razor`, `Auth/IslandPolicies.cs`, `Security/NoAntiforgeryStateProvider.cs`, `wwwroot/js/lang-choice.js`, `root-chooser.js`
- Modify: `Program.cs` (root and `/lang` by edition; antiforgery state provider in static), `Auth/AuthServiceCollectionExtensions.cs` (policy), `Components/Pages/Review.razor`, `Profile.razor` (`[Authorize(Policy = IslandPolicies.Progress)]`), `Components/Layout/AccountControls.razor`, `LanguageSwitcher.razor`, `Components/Pages/Home.razor` (Review card), `About.razor`, `Localization/Resources/SharedResource.en.resx` and `SharedResource.ru.resx`, `src/Trainer.Web.Client/Storage/StorageKeys.cs` (`LanguageChoice`), `tests/Trainer.Web.Tests/PathBaseTests.cs` (adds the root chooser, Review and profile pages to `EveryRootRelativeUrlCarriesThePathBase`), `docs/architecture.md` (storage key), `AGENTS.md` (rules: a static-edition feature that needs a server is hidden or explained and listed in the spec's section 6; the static edition sets no cookie)
- Test: `tests/Trainer.Web.Tests/StaticEditionPageTests.cs`

**Interfaces:**
- Produces:
  - `IslandPolicies.Progress = "progress"`: server edition requires an authenticated user (today's `[Authorize]`), static `RequireAssertion(_ => true)`.
  - `NoAntiforgeryStateProvider : AntiforgeryStateProvider` returning null, static edition only.
  - `StaticRootEndpoint.Map(IEndpointRouteBuilder, SiteEdition)`: server edition maps `RootRedirectEndpoint` and `LanguageEndpoint` as today; static maps `GET /` to `new RazorComponentResult<LanguageChooser>()` and no `/lang`. `LanguageChooser` is a full document (`<html lang="en">`, title from the existing `Site.Name`, two links labelled with the existing `Language.SwitchToRu` and `Language.SwitchToEn` resources with `lang` and `hreflang`, `noindex`, `<script src="@Paths.Asset(root-chooser.js)" data-base="@Paths.BaseHref">`; `root-chooser.js` reads its prefix from `document.currentScript.dataset.base`, then `localStorage['dit.lang']` in try/catch, else the first `navigator.languages` entry with primary tag `ru` or `en`, else `en`, and calls `location.replace(base + lang + '/')`). No new resource keys for the chooser.
  - Static `LanguageSwitcher`: `href = Paths.Page(Links.PathFor(target))`, `data-lang-choice`; `lang-choice.js` stores `dit.lang` (plain `localStorage` write, a preference: last writer wins).
  - Static `AccountControls`: a `Nav.Progress` link to the profile, no sign-in, no import notice.
  - `SharedResource` keys (RU and EN): `Nav.Progress`, `Home.Mode.Review.Local`, `About.Static.Heading`, `About.Static.Text`. `StorageKeys.LanguageChoice = "dit.lang"`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Header_HasProgressLinkAndNoSignIn() { /* a[href="/dit/en/dotnet/profile/"] with the Nav.Progress text; no sign-in link, no form */ }
[Fact] public async Task LanguageSwitcher_LinksDirectlyToTheSamePageInTheOtherLanguage() { /* on /dit/en/dotnet/questions/: a[href="/dit/ru/dotnet/questions/"][data-lang-choice=ru] */ }
[Fact] public async Task Root_ServesTheLanguageChooserWithBothLinks() { /* GET /dit/ 200, links /dit/ru/ and /dit/en/, robots noindex, root-chooser.js with data-base="/dit/" */ }
[Fact] public async Task LangEndpoint_IsNotMappedInStaticEdition() { /* GET /dit/lang/en?return=/en is 404 */ }
[Theory] [InlineData("review")] [InlineData("profile")] public async Task ProgressPages_OpenForAnonymousVisitors(string page) { /* 200 with the island root, no redirect */ }
[Fact] public async Task EveryExportedKindOfResponse_SetsNoCookie() { /* public pages, island pages, root, not-found: no Set-Cookie (data, sw.js and manifest are covered in Tasks 8, 14, 16) */ }
[Fact] public async Task IslandPage_PersistsNoAntiforgeryToken() { /* persisted state holds no antiforgery token; no .dit.af cookie */ }
[Fact] public async Task HomeReviewCard_ExplainsLocalProgress() { /* card text equals Home.Mode.Review.Local in both cultures */ }
[Fact] public async Task About_HasTheStaticEditionParagraph() { /* About.Static.Heading and About.Static.Text present in both cultures */ }
[Fact] public async Task ServerEdition_ReviewStillRedirectsAnonymousToSignIn() { /* server edition: 302 to the sign-in page */ }
```

- [ ] **Step 2:** `dotnet test tests/Trainer.Web.Tests` → FAIL. **Step 3:** Implement. **Step 4:** `tests/Trainer.Web.Tests`, `tests/Trainer.Web.Client.Tests` (resource parity) → PASS.
- [ ] **Step 5:** Commit `feat(web): static-edition header, language choice and open progress pages`.

### Task 7: Static catalog

**Files:**
- Create: `src/Trainer.Web/Components/Shared/StaticFilterForm.razor`, `wwwroot/js/catalog-filter.js`
- Modify: `src/Trainer.Application/Catalog/ICatalogQueries.cs`, `src/Trainer.Infrastructure/Queries/CatalogQueries.cs`, `Components/Pages/Catalog.razor`, `Components/Shared/QuestionRow.razor`, `Localization/Resources/SharedResource.en.resx` and `SharedResource.ru.resx` (new key `Catalog.Filter.Apply` for the submit button; the group labels, search label, found count and empty state reuse the existing `Catalog.*` keys), `docs/architecture.md` (ADR 0011 note)
- Test: `tests/Trainer.Infrastructure.Tests/CatalogQueriesTests.cs` (extend), `tests/Trainer.Web.Tests/StaticCatalogTests.cs`; the script's behaviour is held to the server's by `StaticCatalogFilterTests.CatalogFilter_MatchesServerCatalogForEveryCase` (Task 17), which compares the visible rows with `ICatalogQueries` for a table of queries

**Interfaces:**
- Produces:
  - `ICatalogQueries.GetAllAsync(CultureKey culture, StackKey stack, CancellationToken ct) → Task<CatalogPage>`: every live row, `Page = 1`, `PageCount = 1`, unfiltered facets.
  - Static `Catalog.razor`: `StaticFilterForm` instead of `FilterPanel`, every row, no `Pager`, slashed canonical.
  - Static `QuestionRow`: `data-level`, `data-topic`, `data-type`, `data-title` (lowercase title), `data-tags` (space-separated tags).
  - `StaticFilterForm`: `<form method="get" role="search">` with `q` (`maxlength` 100) and checkbox groups `level`, `topic`, `type`; count `<p class="catalog__found" data-found-template="...">`.
  - `catalog-filter.js`: reads `location.search` with `CatalogQueryParser`'s normalisation, checks boxes, fills the search box, hides non-matching rows (OR within a group, AND across groups; `q` matches when the lowercase title contains it or a tag equals it with spaces replaced by `-`, the rule of `CatalogQueries`), updates the count and the empty state. Ignores `_r`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task GetAll_ReturnsEveryLiveRowOnOnePageWithUnfilteredFacets() { /* rows == live count, Page 1, PageCount 1, facets equal GetAsync with an empty query */ }
[Fact] public async Task StaticCatalog_ListsEveryLiveQuestionWithoutAPager() { /* row count == live count; no .pager */ }
[Fact] public async Task StaticCatalog_RowsCarryLevelTopicTypeTitleAndTags() { /* each row has data-level, data-topic, data-type, lowercase data-title, space-separated data-tags */ }
[Fact] public async Task StaticCatalog_FilterFormIsGetWithCheckboxesAndNoToken() { /* method=get, checkboxes named level, topic, type, submit labelled Catalog.Filter.Apply, no antiforgery input */ }
[Fact] public async Task ServerCatalog_IsUnchanged() { /* server edition: FilterPanel links and 25-row pager as in CatalogPageTests */ }
```

- [ ] **Step 2:** `dotnet test tests/Trainer.Infrastructure.Tests`, `tests/Trainer.Web.Tests` → FAIL. **Step 3:** Implement. **Step 4:** Same plus `tests/Trainer.Web.Client.Tests` (resource parity) → PASS (browser behaviour in Task 17).
- [ ] **Step 5:** Commit `feat(web): static catalog with a GET filter form filtered in the browser`.

### Task 8: Content shards

**Files:**
- Create: `src/Trainer.Contracts/StaticSiteContracts.cs`, `src/Trainer.Application/StaticSite/IStaticSiteQueries.cs`, `src/Trainer.Infrastructure/StaticSite/StaticSiteQueries.cs`, `src/Trainer.Web/StaticSite/StaticContentCatalog.cs`, `StaticContentHash.cs`, `StaticContentEndpoints.cs`, `StaticSiteOptions.cs`, `docs/adr/0023-content-shards-for-the-static-edition.md`
- Modify: `src/Trainer.Contracts/IslandContracts.cs`, `TrainerJsonContext.cs`, `src/Trainer.Infrastructure/DependencyInjection.cs`, `Sessions/SessionRows.cs` (visibility only), `src/Trainer.Web/Islands/IslandBootstrapFactory.cs`, `Caching/CompositeContentCacheInvalidator.cs`, `Program.cs`, `docs/architecture.md` (configuration `App:StaticSite:RepositoryUrl`, ADR 0023), `DocsTests.cs`
- Test: `tests/Trainer.Infrastructure.Tests/StaticSiteQueriesTests.cs`, `tests/Trainer.Web.Tests/StaticContentTests.cs`, `tests/Trainer.Application.Tests/ContractsJsonTests.cs`

**Interfaces:**
- Produces:
  - Contracts: `StaticIndexDto(int Format, string Culture, string Stack, string ContentHash, IReadOnlyList<TopicDto> Topics, IReadOnlyList<StaticIndexEntryDto> Questions) { const int CurrentFormat = 1; }`; `StaticIndexEntryDto(string Id, string Slug, string Topic, string Level, string Type, string Title, string Hash)`; `static class StaticContentPaths { string Question(string culture, string id, string hash); }` (`data/{culture}/q/{id}.{hash}.json`, used by host and client); `StaticSiteDto(string IndexPath, string IssuesNewUrl)` (`IndexPath` base-relative without a leading slash, `data/{culture}/{stack}/index.{hash}.json`); `IslandBootstrapDto` gains a last parameter `StaticSiteDto? StaticSite = null`; all in `TrainerJsonContext`.
  - Application: `IStaticSiteQueries { Task<IReadOnlyList<QuestionDto>> GetLiveQuestionsAsync(CultureKey, StackKey, CancellationToken); Task<StaticPagePaths> GetPagePathsAsync(CancellationToken); }`; `StaticPagePaths(IReadOnlyList<string> Pages, IReadOnlyList<string> Redirects)` (logical paths: sitemap entries plus retired question pages; redirect sources per culture). Questions by id ordinal, each exactly the session DTO.
  - `StaticSiteOptions` (`App:StaticSite`): `RepositoryUrl` (https, default `https://github.com/Platonenkov/dev-interview-trainer`, the only bound key in this task); `IssuesNewUrl` is a read-only property, `RepositoryUrl + "/issues/new"`, not a configuration key.
  - `StaticContentHash.Of(ReadOnlySpan<byte>) → string`.
  - `StaticContentCatalog` (singleton, static edition): `Task<StaticContentFiles> GetAsync(CultureKey, StackKey, CancellationToken)`, cached per active content hash, cleared by the composite invalidator; `StaticContentFiles(string IndexPath, ReadOnlyMemory<byte> Index, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files)`.
  - `StaticContentEndpoints` (static only): `GET /data/{culture}/{stack}/index.{hash}.json`, `GET /data/{culture}/q/{id}.{hash}.json`, exact bytes, `application/json; charset=utf-8`; anything else 404 with an empty body.
- ADR 0023: shape (owner decision), mechanism (built from imported rows by `SessionRows.ToDto`, SHA-256 names, index path in the bootstrap, retention and "site updated" per static 5.13).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task LiveQuestions_EqualTheSessionDtoForEachQuestion() { /* for each live id: JSON equals GetPracticeAsync(First=id, Count=1).Questions[0] */ }
[Fact] public async Task LiveQuestions_LeaveOutRetiredAndMissingLocale() { /* a retired question and one without an en locale are absent from en */ }
[Fact] public async Task PagePaths_CoverSitemapRetiredQuestionsAndRedirectSources() { /* every sitemap path, the retired question page and each redirect source per culture */ }
[Fact] public async Task Index_ListsEveryLiveQuestionSortedByIdWithItsFileHash() { /* Questions by Id ordinal; each Hash == StaticContentHash.Of(file bytes) */ }
[Fact] public async Task FileNames_CarryTheSha256PrefixOfTheirBytes() { /* every name's hash segment == first 16 hex of SHA-256 of its bytes */ }
[Fact] public async Task SameContent_YieldsTheSameNames() { /* two hosts on the same fixture: equal IndexPath and file key sets */ }
[Fact] public async Task ChangedQuestion_ChangesOnlyItsFileAndTheIndex() { /* after editing one title and reimporting: its file and the index change name, all others keep theirs */ }
[Fact] public async Task WrongHash_Returns404WithEmptyBody() { /* 404 with Content-Length 0 */ }
[Fact] public async Task DataEndpoints_AreNotMappedInServerEdition() { /* a valid data path is 404 in the server edition */ }
[Fact] public async Task IslandBootstrap_NamesTheIndexPathInStaticEditionOnly() { /* static: StaticSite.IndexPath == the catalog's IndexPath, no leading slash; server: StaticSite null */ }
[Fact] public void StaticContracts_RoundTripThroughTrainerJsonContext() { /* each new record serialises and deserialises to an equal value */ }
[Fact] public async Task DataEndpoints_SetNoCookie() { /* index and question responses carry no Set-Cookie */ }
```

- [ ] **Step 2:** Run the three projects → FAIL. **Step 3:** Implement; ADR 0023. **Step 4:** Same → PASS.
- [ ] **Step 5:** Commit `feat(web): serve content-hashed question shards in the static edition`.

### Task 9: "Report a problem" through a GitHub issue

**Files:**
- Create: `.github/ISSUE_TEMPLATE/question-report.yml`, `src/Trainer.Contracts/ProblemReportLink.cs`, `src/Trainer.Web/Components/Shared/ReportProblemLink.razor`, `src/Trainer.Web.Client/Components/ReportProblemLink.razor`
- Modify: `Components/Pages/QuestionPage.razor` (static: `ReportProblemLink` for everyone instead of `QuestionFeedbackForms`), `Localization/Resources/SharedResource.en.resx` and `.ru.resx`, `src/Trainer.Web.Client/Localization/Resources/ClientResource.en.resx` and `.ru.resx` (`Report.Problem`)
- Test: `tests/Trainer.Application.Tests/ProblemReportLinkTests.cs`, `tests/Trainer.Deploy.Tests/IssueTemplateTests.cs`, `tests/Trainer.Web.Tests/StaticEditionPageTests.cs` (extend), `tests/Trainer.Web.Client.Tests/IslandComponentRenderTests.cs` (extend)

**Interfaces:**
- Produces: `static class ProblemReportLink { const string Template = "question-report.yml"; const string Label = "content-report"; string Build(string issuesNewUrl, string questionId, string slug, string culture, string pageUrl); }` → `{issuesNewUrl}?template=question-report.yml&title=<"[{culture}] {slug}">&question-id=..&language=..&page-url=..`, each value `Uri.EscapeDataString`d; page URL = the question page's absolute canonical URL. Issue form: `title: "[question] "`, `labels: [content-report]`, inputs `question-id`, `language`, `page-url` (required), dropdown `reason` with the five `ReportReasonKeys` in English, textarea `details`. The client component takes `[Parameter] QuestionDto Question`, `string Culture`, `string Stack`, `string IssuesNewUrl` and builds the page URL as `new Uri(new Uri(Navigation.BaseUri), IslandLinks.Page(Culture, Stack, ["questions", Question.Slug])).AbsoluteUri` (the island page's base is the public prefix); Task 11 places it in Practice and Review.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Build_NamesTheTemplateAndPrefillsIdLanguageAndPage() { /* query has template=question-report.yml, question-id, language, page-url, title "[en] slug" */ }
[Fact] public void Build_EscapesEveryValue() { /* values with &, #, space and Cyrillic round-trip through Uri parsing */ }
[Fact] public void QuestionReportTemplate_HasThePrefilledIdsTheLabelAndTheFiveReasons() { /* labels == [content-report]; required inputs question-id, language, page-url; reason has the five reasons */ }
[Fact] public async Task StaticQuestionPage_ShowsReportLinkToEveryVisitorAndNoRatingForms() { /* anonymous static page: issues/new link with this question's id; no rating form */ }
[Fact] public async Task ServerQuestionPage_IsUnchanged() { /* anonymous server page: no report link, no feedback forms, as today */ }
[Fact] public void ClientReportLink_CarriesTheQuestionPageUrlUnderTheBase() { /* IslandComponentRenderTests: base https://h.io/dit/, slug s: page-url=https://h.io/dit/en/dotnet/questions/s/ */ }
```

- [ ] **Step 2:** Run `tests/Trainer.Application.Tests`, `tests/Trainer.Deploy.Tests`, `tests/Trainer.Web.Tests`, `tests/Trainer.Web.Client.Tests` → FAIL. **Step 3:** Implement. **Step 4:** Same → PASS.
- [ ] **Step 5:** Commit `feat: report a problem through a prefilled GitHub issue in the static edition`.

### Task 10: Island backend seam and lazily loaded sessions

**Files:**
- Create: `src/Trainer.Web.Client/Backend/IIslandBackend.cs`, `ServerIslandBackend.cs`, `SessionPlan.cs`, `IQuestionSource.cs`, `PreloadedQuestionSource.cs`, `IslandProgress.cs`
- Modify: `Sessions/PracticeSession.cs`, `ReviewSession.cs`, `IslandState.cs` (`SiteUpdated`), `src/Trainer.Contracts/ApiContracts.cs` (`ApiErrorCodes.SiteUpdated = "site-updated"`), `Islands/PracticeIsland.razor`, `ReviewIsland.razor`, `MockIsland.razor`, `ProfileIsland.razor`, `Components/TopicSidebar.razor`, `SessionBar.razor`, `PracticeFilters.razor`, `IslandStateView.razor`, `ClientResource` (`State.SiteUpdated`, `State.LoadingNext`), `Program.cs`, `AGENTS.md` (islands rule)
- Test: `tests/Trainer.Web.Client.Tests/PracticeSessionTests.cs`, `ReviewSessionTests.cs`, `ServerIslandBackendTests.cs`, `IslandStateTests.cs`, `IslandComponentRenderTests.cs`

**Interfaces:**
- Produces:
  - `IQuestionSource { ValueTask<ApiResult<QuestionDto>> GetAsync(string questionId, CancellationToken ct); void Prefetch(string questionId); }`; `PreloadedQuestionSource(IReadOnlyList<QuestionDto>)`.
  - `SessionPlan(IReadOnlyList<string> QuestionIds, int PoolSize, IReadOnlyDictionary<string, string>? MyRatings, IQuestionSource Questions)`.
  - `IslandProgress(int StreakDays, int DueNow, IReadOnlyList<TopicProgressDto> Topics) { static IslandProgress? From(MeDto?); }`; `TopicSidebar`, `SessionBar` take `IslandProgress?`.
  - `IIslandBackend`: `bool KeepsProgress(IslandBootstrapDto)`; `string OutboxKey(IslandBootstrapDto)`; `bool FiltersPendingAnswers { get; }`; `bool SendsAnsweredAsExclude { get; }`; `Task<IslandProgress?> GetProgressAsync(IslandBootstrapDto, CancellationToken)`; `Task<ApiResult<SessionPlan>> PracticeAsync(IslandBootstrapDto, PracticeSessionRequest, CancellationToken)`; `Task<ApiResult<MockSessionDto>> MockAsync(IslandBootstrapDto, MockSessionRequest, CancellationToken)`; `Task<ApiResult<SessionPlan>> ReviewAsync(IslandBootstrapDto, ReviewSessionRequest, CancellationToken)`; `Task<ApiResult<ProfileDto>> ProfileAsync(IslandBootstrapDto, CancellationToken)`; `Task OnSessionFinishedAsync(IslandBootstrapDto, CancellationToken)` (server: nothing).
  - `ServerIslandBackend(TrainerApiClient)`: today's behaviour moved behind the seam: `KeepsProgress` = `Me is not null`; `OutboxKey` = user outbox when signed in, else anonymous (Review keeps `SignedOut` without a user); `FiltersPendingAnswers` = true (Review drops due questions still in the outbox); `SendsAnsweredAsExclude` = true (anonymous `new`); one request per session as today, wrapped in a `PreloadedQuestionSource`.
  - `PracticeSession(SessionPlan, UlidGenerator, Random, ServerClock)`: `Task<ApiResult<QuestionDto>> StartAsync(CancellationToken)`; `Task<ApiResult<QuestionDto>> NextAsync(CancellationToken)`; prefetches the following question after each show; `Current` null while loading; a failed load keeps `Index` and `Results` so retry repeats it. UI: while `Current` is null the island shows a fixed-size skeleton (same size as the question card, no layout shift) with an `aria-live="polite"` `State.LoadingNext` text; a failure shows the offline or site-updated state with a retry button and keeps the summary data; focus moves to the new question when it arrives (the existing focus rule).
  - `IslandStateKind.SiteUpdated` for a 404 with code `site-updated`; `IslandStateView` renders the text and a reload button that navigates to the current path with `_r=<Date.now()>` added (static 5.13). Boot failures before the island exists are handled by the inline recovery of Task 13.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Start_ShowsTheFirstQuestionAndPrefetchesTheSecond() { /* recording source: GetAsync(q1) then Prefetch(q2); Current is q1 */ }
[Fact] public async Task Next_AwaitsAQuestionStillLoading_CurrentIsNullMeanwhile() { /* q2 completes later: Current null until then, then q2 */ }
[Fact] public async Task FailedLoad_KeepsTheIndexAndResultsAndRetries() { /* q2 fails: Index and Results unchanged; NextAsync again returns q2 */ }
[Fact] public async Task ServerBackend_PracticeMakesExactlyOneRequestAndPreloadsEveryQuestion() { /* one POST api/sessions/practice; every GetAsync completes synchronously */ }
[Fact] public async Task ServerBackend_ReviewStillFiltersPendingAnswers() { /* a due question with an answer in the user outbox is dropped */ }
[Fact] public void SiteUpdated404_MapsToSiteUpdated() { /* 404 + site-updated -> SiteUpdated; plain 404 -> ServerError */ }
[Fact] public void LoadingNext_RendersFixedSkeletonWithLiveRegion() { /* while Current is null: skeleton with the card size class and aria-live=polite State.LoadingNext text */ }
```

- [ ] **Step 2:** `dotnet test tests/Trainer.Web.Client.Tests` → FAIL. **Step 3:** Implement; adapt existing tests to the async API without weakening assertions. **Step 4:** `tests/Trainer.Web.Client.Tests`, `tests/Trainer.Web.Tests` (`IslandRootsTests`), E2E category → PASS.
- [ ] **Step 5:** Commit `refactor(client): route island data through a backend seam with lazily loaded sessions`.

### Task 11: Static island backend and local progress

**Files:**
- Create: `src/Trainer.Web.Client/Backend/StaticIslandBackend.cs`, `StaticContentClient.cs`, `StaticQuestionSource.cs`, `LocalProgress.cs`, `LocalProgressCache.cs`, `StaticIndex.cs`, `LocalProfileBuilder.cs`, `src/Trainer.Web.Client/Sessions/PracticeQueryString.cs`
- Modify: `Program.cs` (backend by `ClientEdition`; `Sm2ReviewScheduler`, `PracticeSelector`, `MockComposer`), `Islands/PracticeIsland.razor`, `MockIsland.razor` (initial filters from `PracticeQueryString` when `StaticSite` is set), `ReviewIsland.razor`, `Sessions/PracticeFilterSet.cs` (`Excluded` only when `SendsAnsweredAsExclude`), Practice and Review use the client `ReportProblemLink` instead of `QuestionFeedbackBar` when static, `IslandRoot` (static edition: `Usable` waits for the index and the progress replay, or for the first question on a `?first=` link; shell time recorded as `data-island-shell-ms`), `src/Trainer.Web/wwwroot/js/outbox.js` (`watchKey`: a `storage` listener for one key), `Storage/BrowserStore.cs`, `IBrowserStore.cs`, `OutboxStore.cs` (event `ChangedElsewhere`), `tests/Trainer.Web.Client.Tests/HookStore.cs` (can raise it), `islands-start.js` (`?perf=1` shows shell and usable times), `docs/architecture.md` (storage keys)
- Test: `tests/Trainer.Web.Client.Tests/StaticIslandBackendTests.cs`, `StaticContentClientTests.cs`, `LocalProgressTests.cs`, `LocalProfileBuilderTests.cs`, `PracticeQueryStringTests.cs`, `IslandComponentRenderTests.cs` (extend), `tests/Trainer.Web.Tests/PracticeQueryStringParityTests.cs`, `LocalProfileParityTests.cs`

**Interfaces:**
- Consumes: Tasks 2, 8, 10.
- Produces:
  - `StaticContentClient(HttpClient http)` (scoped): `Task<ApiResult<StaticIndexDto>> GetIndexAsync(string indexPath, CancellationToken ct)`; `ValueTask<ApiResult<QuestionDto>> GetQuestionAsync(string culture, StaticIndexEntryDto entry, CancellationToken ct)`; `void Prefetch(string culture, StaticIndexEntryDto entry)`. Shared fetch semantics: one in-flight fetch per path, owned by the client with its own lifetime token (cancelled only when the page ends); a caller's token cancels only that caller's wait; only successes are memoised; a failed or cancelled fetch is evicted so the next call fetches again. 404 on a hashed path or an index `Format` other than `CurrentFormat` → status 404, code `site-updated`; no response → status 0. The island starts `GetIndexAsync` at initialisation.
  - `LocalProgress` (pure): `static LocalProgress Replay(IReadOnlyList<AnswerDto> log, IReviewScheduler scheduler, DateTimeOffset now)`: every valid answer of the log, whatever its stack or language, ordered by `AnsweredAt` (the effective time, fixed when the answer was written from the device clock, never re-clamped), then `ClientAnswerId` ordinal, applied through the scheduler; `now` is used only for due checks; states are kept for every question id (no index filtering here); `IReadOnlyDictionary<QuestionId, ReviewState> States`; `IReadOnlyDictionary<QuestionId, DateTimeOffset> DueAtById(IReadOnlySet<QuestionId> live)` (states of live ids only, for selection); `IEnumerable<(QuestionId Id, DateTimeOffset DueAt)> DueStates()` (every state, for `ReviewOrder.Pick`); `int StreakDays(DateTimeOffset now, TimeSpan utcOffset)`; `int TotalAnswered`; `IslandProgress Summarize(StaticIndexDto index, DateTimeOffset now, TimeSpan utcOffset)`.
  - `static class StaticIndex { IReadOnlySet<QuestionId> LiveIds(StaticIndexDto index); IReadOnlyList<PracticeCandidate> Candidates(StaticIndexDto index); StaticIndexEntryDto? Find(StaticIndexDto index, string id); }`: the only place index entries become domain types (`QuestionId`, `Level` via `LevelKeys`, `TopicKey`, `QuestionType` via `QuestionTypeKeys`); an entry with an unknown level or type is skipped.
  - `static class LocalProfileBuilder { ProfileDto Build(StaticIndexDto index, LocalProgress progress, IReadOnlyList<MockHistoryDto> mocks, DateTimeOffset now, TimeSpan utcOffset); }` (semantics of `ProgressQueries`: `LiveQuestions` from the index, `Answered` = has a state, `Known` = `LastGrade == Knew`, `Due` = `DueAt <= now`, `NextDueAt` earliest future, mocks newest first). `LocalProfileParityTests` (Web.Tests, Testcontainers) records the same answers through `AnswerRecorder` for a user and replays them locally, and requires `ProgressQueries` and `LocalProfileBuilder` to give equal topic and level rows, streak and due count.
  - `LocalProgressCache(OutboxStore outbox, IReviewScheduler scheduler, TimeProvider time)` (scoped, one per page): `ValueTask<LocalProgress> GetAsync(CancellationToken ct)`: reads the anonymous log once, replays, memoises; the memo is dropped when `OutboxStore.Changed` (this tab's write) or `OutboxStore.ChangedElsewhere` (another tab, through the new `watchKey` listener) fires, so the next call recomputes. The full log is the only progress record; nothing derived is written (static 5.6 states the size estimate and the limit). The island awaits `GetAsync` in parallel with the index before it declares itself usable; on a `?first=` link the first question is fetched and shown without waiting for it, and the remaining selection happens when it completes, before Next is enabled.
  - `StaticIslandBackend(StaticContentClient content, LocalProgressCache progress, PracticeSelector selector, MockComposer composer, TimeProvider time) : IIslandBackend` implements every member of Task 10's interface: `KeepsProgress` true; `OutboxKey` always `StorageKeys.AnonymousOutbox` (Practice, Mock, Review); `FiltersPendingAnswers` false; `SendsAnsweredAsExclude` false; `GetProgressAsync` = `progress.Summarize(index, now, offset)`; Practice: `selector.Select(StaticIndex.Candidates(index), selection, progress.DueAtById(StaticIndex.LiveIds(index)), now)`; Mock: `composer.Compose`, fetch exactly the chosen files (at most 6 at a time), `RepeatsLikely`, `SuggestedCount`; Review: `ReviewOrder.Pick(progress.DueStates(), StaticIndex.LiveIds(index), now, count)`; `ProfileAsync` = `LocalProfileBuilder.Build(index, progress, mocks: [], now, offset)` (Task 12 supplies the mock history); `OnSessionFinishedAsync` does nothing. `offset` is `time.GetLocalNow().Offset`.
  - Practice and Review render the client `ReportProblemLink` (Task 9) with `bootstrap.StaticSite.IssuesNewUrl` instead of `QuestionFeedbackBar` when `StaticSite` is set.
  - `PracticeQueryString.Parse(string query, IReadOnlyList<TopicDto> topics) → PracticeFiltersDto` (`level`, `topic`, `type`, `status`, `first`; `CatalogQueryParser` normalisation; other parameters such as `_r` ignored).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Practice_FetchesTheIndexOnceAndOnlyTheFirstQuestion() { /* handler log: index once, q1, prefetch q2, nothing else */ }
[Fact] public async Task Practice_NewLeavesOutLocallyAnsweredQuestionsWithoutExclude() { /* answered ids absent; ExcludeQuestionIds empty */ }
[Fact] public async Task Practice_DueUsesLocalReviewStates() { /* only ids whose replayed DueAt <= now */ }
[Fact] public async Task Practice_DueStatusWithoutHistory_IsEmpty() { /* empty plan; state Empty */ }
[Fact] public async Task Practice_FirstComesFirstWhateverTheFilters() { /* QuestionIds[0] == first */ }
[Fact] public async Task Mock_FetchesExactlyTheChosenQuestions() { /* handler log: the index and exactly Count question files, the chosen ones */ }
[Fact] public async Task Review_AnswersGoToTheAnonymousOutbox() { /* OutboxKey(bootstrap) == dit.outbox.v1.anon */ }
[Fact] public async Task Review_DueQuestionAnsweredEarlierIsOffered() { /* a question answered 3 days ago and now due is in the plan although its answer is in the outbox */ }
[Fact] public async Task Review_OrdersMostOverdueFirst() { /* plan ordered by DueAt, then id */ }
[Fact] public async Task HashedFile404_IsSiteUpdated() { /* status 404, code site-updated */ }
[Fact] public async Task UnknownIndexFormat_IsSiteUpdated() { /* Format 2: status 404, code site-updated */ }
[Fact] public async Task ConcurrentGetAndPrefetch_FetchTheFileOnce() { /* one request for the path */ }
[Fact] public async Task FailedPrefetch_IsRetriedByTheForegroundLoad() { /* offline then online */ }
[Fact] public async Task CancelledPrefetch_DoesNotCancelTheForegroundLoad() { /* prefetch waiter cancelled; GetQuestionAsync still returns the file; one request */ }
[Fact] public void Replay_AppliesAnswersInServerOrder() { /* AnsweredAt ascending, ties by ClientAnswerId ordinal */ }
[Fact] public void Replay_LateBackdatedAnswer_TakesItsChronologicalPlace() { /* equals replaying the sorted log from scratch */ }
[Fact] public void Replay_KeepsStatesOfOtherStacksAndLanguages() { /* answers of a stack or locale absent from the current index survive and reappear when that index is used */ }
[Fact] public void Replay_SameLogSameStatesAcrossDaysAndReloads() { /* a future-dated answer keeps its anchor on later days */ }
[Fact] public void Replay_NeverReClampsAnsweredAt() { /* an answer 2 days ahead of now: LastAnsweredAt equals that time */ }
[Fact] public async Task Usable_WaitsForIndexAndProgress() { /* ready attribute set only after both controlled tasks complete */ }
[Fact] public async Task FirstLink_ShowsTheQuestionWithoutWaitingForProgress_NextWaitsForSelection() { /* progress task held open: the first question renders; Next stays disabled until the replay completes and the rest of the plan exists */ }
[Fact] public void Replay_EqualsApplyingTheSchedulerDirectly() { /* states equal folding Sm2ReviewScheduler.Apply over the sorted answers */ }
[Fact] public void Streak_CountsDaysInTheDeviceOffset() { /* answers at 23:30 and 00:30 local (+03:00) count as two days */ }
[Fact] public async Task Recompute_AfterAnotherTabWrites() { /* HookStore raises ChangedElsewhere; next GetAsync sees the new answer */ }
[Fact] public async Task Memoised_OneReadPerPageWithoutWrites() { /* two GetAsync calls: one outbox read */ }
[Fact] public void Replay_10000Answers_AppliesTheSchedulerOncePerAnswerAndReadsTheLogOnce() { /* counting IReviewScheduler: 10 000 Apply calls; HookStore: one outbox read; the timing itself is measured in Task 17's perf run */ }
[Fact] public void Build_CountsAnsweredKnownAndDuePerTopicAndLevel() { /* hand-computed Answered, Known, Due for two topics and two levels */ }
[Fact] public void Build_LiveQuestionsComeFromTheIndex() { /* LiveQuestions per topic equals index entries; an answered id missing from the index is not counted */ }
[Fact] public void Build_NextDueIsTheEarliestFutureState() { /* NextDueAt == min DueAt > now; null when none */ }
[Fact] public async Task LocalProfileMatchesProgressQueries() { /* Trainer.Web.Tests, LocalProfileParityTests */ }
[Theory, MemberData(nameof(Queries))] public void ClientParserMatchesCatalogQueryParser(string query) { /* Parse equals CatalogQueryParser.Parse levels, topics, types for each query */ } // Trainer.Web.Tests
```

- [ ] **Step 2:** `dotnet test tests/Trainer.Web.Client.Tests`, `tests/Trainer.Web.Tests` → FAIL. **Step 3:** Implement. **Step 4:** Same → PASS.
- [ ] **Step 5:** Commit `feat(client): run Practice, Mock and Review from content shards and the browser answer log`.

### Task 12: Local profile and mock history

**Files:**
- Create: `src/Trainer.Web.Client/Storage/LocalMockHistoryStore.cs`
- Modify: `src/Trainer.Contracts/Storage/StorageDocuments.cs` (`StoredMockHistory`), `outbox.js` (operation `historyAdd`, family `dit.mock.v1.anon.history`), `StoreTypes.cs`, `BrowserStore.cs`, `StorageKeys.cs` (`AnonymousMockHistory`), `Backend/StaticIslandBackend.cs` (constructor gains `LocalMockHistoryStore history`; `ProfileAsync` passes `await history.ReadAsync()` as `mocks`), `Program.cs` (registration), `tests/Trainer.Web.Client.Tests/HookStore.cs` (the `historyAdd` operation), `MockIsland.razor` (static: add the summary on completion), `ProfileIsland.razor` (static: progress note with the limits of static 5.6, `StorageNotice` from the outbox status, no import panel, no failed-summary list), `ClientResource.en.resx` and `.ru.resx` (`Profile.Local.Note`), `docs/architecture.md`
- Test: `tests/Trainer.Web.Client.Tests/LocalMockHistoryStoreTests.cs`, `StaticIslandBackendTests.cs` (extend), `IslandComponentRenderTests.cs`

**Interfaces:**
- Consumes: `LocalProfileBuilder`, `StaticIslandBackend` (Task 11).
- Produces: `StoredMockHistory(int Version, IReadOnlyList<MockHistoryDto> Items) { CurrentVersion = 1; MaxItems = 20; }`; `LocalMockHistoryStore { ValueTask<StoreResult> AddAsync(MockHistoryDto); ValueTask<IReadOnlyList<MockHistoryDto>> ReadAsync(); }` (merge by `Id` under the Web Lock, newest 20).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Profile_ListsStoredMockHistoryNewestFirst() { /* StaticIslandBackendTests */ }
[Fact] public void StaticProfile_MemoryOnlyStorage_ShowsStorageNotice() { /* IslandComponentRenderTests, FakeBrowserStore unavailable */ }
[Fact] public async Task Add_KeepsTheNewest20AndMergesById() { /* 21 adds: 20 items newest first; re-adding an id replaces it */ }
[Fact] public async Task Add_FromTwoTabs_KeepsBoth() { /* interleaved HookStore adds from two stores: both items present */ }
```

- [ ] **Step 2:** `dotnet test tests/Trainer.Web.Client.Tests` → FAIL. **Step 3:** Implement. **Step 4:** Full regression set (gate 1) → PASS.
- [ ] **Step 5:** Commit `feat(client): local profile and mock history for the static edition`.

### Task 13: Meta CSP, static security headers and GoatCounter

**Files:**
- Create: `src/Trainer.Web/Startup/StaticCsp.cs`, `RecoveryScript.cs`, `Components/Shared/StaticHeadMeta.razor`, `SiteUpdatedBanner.razor`, `Analytics/GoatCounterScript.cs`, `wwwroot/js/goatcounter-settings.js`, `docs/adr/0024-security-headers-on-github-pages.md`, `docs/adr/0025-goatcounter-in-the-static-edition.md`
- Modify: `Components/App.razor` (static: `StaticHeadMeta` first in `<head>`; import map rendered from `ImportMapDefinition` as text so its hash is known), `LanguageChooser.razor`, `Startup/SecurityHeadersMiddleware.cs` (static: no headers), `Analytics/AnalyticsOptions.cs` (`GoatCounterCode`), `Components/Shared/AnalyticsScript.razor`, `Components/Layout/MainLayout.razor` (`SiteUpdatedBanner` first in `<body>` in the static edition), `wwwroot/js/islands-start.js` (static: `showBootFailure` also calls `window.ditRecover()`), `StaticSite/StaticSiteOptions.cs` (`GoogleSiteVerification`, `YandexVerification`, `BuildId`), `Localization/Resources/SharedResource.en.resx` and `.ru.resx` (`Recovery.Text`, `Recovery.Reload`), `docs/architecture.md` (configuration keys, ADRs 0024 and 0025), `DocsTests.cs` (expected keys), `AGENTS.md` (rule: the static edition sends no security header; its CSP is the meta element from `StaticCsp`, and a new inline script needs its hash there)
- Test: `tests/Trainer.Web.Tests/StaticCspTests.cs`, `AnalyticsScriptTests.cs`, `RecoveryTests.cs`

**Interfaces:**
- Produces:
  - `static class RecoveryScript { const string Source = "..."; static readonly string CspSource; }` (like `ThemeScript`): a few lines that add a capture-phase `error` listener on `window` for elements carrying `data-recover` (the stylesheets, `islands-start.js`, `blazor.web.js`; never the analytics scripts), and define `window.ditRecover`; both unhide `#site-updated`, at most once per page URL per browser session (`sessionStorage` key `dit.recover.<path+query>`, in try/catch). `SiteUpdatedBanner` renders `<div id="site-updated" class="site-updated" role="alert" hidden>` with the localised text "Part of the page did not load. Reload the page." and `<a data-reload>`; the script sets that link's `href` to the current path and query with `_r` set to `Date.now()` (other parameters kept, an existing `_r` replaced). Every element listed above gets `data-recover` in `App.razor`. Rendered on every static page rendered by the app, including `404.html` and the root chooser; redirect pages written by the exporter (Task 16) have no scripts and need none. The banner needs no stylesheet.
  - `StaticCsp { string For(bool islandPage, string? importMapHash, string? goatCounterCode); string Sha256Source(string inlineText); }` per static 5.8 (script hashes: theme and recovery) (no `frame-ancestors`, `report-uri`, `sandbox`); `StaticHeadMeta` renders the CSP meta and `<meta name="referrer" content="strict-origin-when-cross-origin">`; `StaticCsp.For(false, null, code)` is also exposed through the export manifest for redirect pages (Task 16). `StaticHeadMeta` also renders `<meta name="google-site-verification">` and `<meta name="yandex-verification">` from `App:StaticSite:GoogleSiteVerification` and `YandexVerification` (repository variables `GOOGLE_SITE_VERIFICATION`, `YANDEX_VERIFICATION`; public values) on the root chooser and each culture home page, when set; CSP does not govern meta tags.
  - `AnalyticsOptions.GoatCounterCode` (`^[a-z0-9-]{2,63}$`): static renders GoatCounter only, server Umami only; excluded pages as today. Markup at the end of `<body>`: `goatcounter-settings.js` (`window.goatcounter = { path: function () { return location.pathname; } }`) then `<script data-goatcounter="https://<code>.goatcounter.com/count" async src="https://gc.zgo.at/count.v4.js" integrity="@GoatCounterScript.Integrity" crossorigin="anonymous">`; the integrity value is the one GoatCounter publishes, checked by downloading the file and computing `sha384` before committing.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task PublicPage_CspMetaIsTheFirstHeadElementBeforeTheThemeScript() { /* head's first element child is the CSP meta; the theme script follows */ }
[Fact] public async Task IslandPage_CspAllowsItsImportMapByHashAndWasmAndBaseSelf() { /* script-src has 'wasm-unsafe-eval' and the sha256 of the rendered import map text; base-uri 'self' */ }
[Fact] public async Task PublicPage_CspHasBaseUriNoneAndNoBaseElement() { /* base-uri 'none'; no <base> */ }
[Fact] public async Task StaticEdition_SendsNoSecurityHeaders() { /* no Content-Security-Policy, X-Content-Type-Options or Referrer-Policy header */ }
[Fact] public async Task CspMeta_HasNoFrameAncestors() { /* no frame-ancestors, report-uri or sandbox in the meta */ }
[Fact] public async Task GoatCounter_RenderedWithIntegrityOnPublicPagesNotOnProfile() { /* public page: count.v4.js with integrity and data-goatcounter for the code; profile: none */ }
[Theory] [InlineData("UPPER")] [InlineData("a")] [InlineData("has space")] public void GoatCounterCode_Malformed_FailsStartup(string code) { /* AnalyticsSetup.Read throws InvalidOperationException */ }
[Fact] public async Task ServerEdition_IgnoresGoatCounterAndKeepsItsHeaders() { /* server edition with a code: no gc.zgo.at script; CSP header present */ }
[Fact] public void GoatCounterIntegrity_IsASha384Source() { /* matches ^sha384-[A-Za-z0-9+/]{64}$ */ }
[Fact] public async Task EveryStaticPage_HasTheRecoveryScriptAllowedByHashAndTheHiddenBanner() { /* inline script equals RecoveryScript.Source, its hash is in script-src, #site-updated is hidden */ }
[Fact] public async Task RecoverMarks_OnBootCriticalElementsOnly_NotOnAnalytics() { /* data-recover on the stylesheets, islands-start.js, blazor.web.js; not on the GoatCounter scripts */ }
[Fact] public async Task EveryStaticPage_CarriesTheBuildIdMeta() { /* App:StaticSite:BuildId */ }
[Fact] public async Task VerificationMeta_OnRootAndHomePagesWhenConfigured() { /* both set: metas on /, /en/, /ru/, not on a question page; unset: absent */ }
```

- [ ] **Step 2:** `dotnet test tests/Trainer.Web.Tests` → FAIL. **Step 3:** Implement; ADRs 0024, 0025. **Step 4:** Same plus `tests/Trainer.Web.Client.Tests` (resource parity) → PASS.
- [ ] **Step 5:** Commit `feat(web): meta CSP and GoatCounter for the static edition`.

### Task 14: Content-addressed service worker

**Files:**
- Create: `src/Trainer.Web/StaticSite/StaticAssetRules.cs`, `src/Trainer.Web/StaticSite/StaticAssetList.cs`, `ServiceWorkerEndpoint.cs`, `wwwroot/js/sw-template.js`, `wwwroot/js/sw-register.js`, `docs/adr/0026-content-addressed-service-worker.md`
- Modify: `Components/App.razor` (static edition, every page: `<script src="@Paths.Asset(sw-register.js)" data-scope="@Paths.BaseHref" data-sw="@Paths.Asset("sw.js")" defer>`), `src/Trainer.Web.Client/Components/IslandRoot.razor` (raises `dit:island-usable` on `document` when `Usable` is first set), `Program.cs`, `docs/architecture.md`, `docs/performance.md`
- Test: `tests/Trainer.Web.Tests/ServiceWorkerTests.cs`, `StaticAssetListTests.cs`, `StaticAssetRulesTests.cs`, `tests/Trainer.Web.Client.Tests/IslandComponentRenderTests.cs` (the `dit:island-usable` event)

**Interfaces:**
- Produces:
  - `static class StaticAssetRules { const string FingerprintPattern; const string ContentPattern; bool IsEligible(string pathUnderScope); string JavaScriptRegex { get; } }`: fingerprinted asset names (`name.<fingerprint>.ext[.br]`, fingerprint format as recorded by Task 1) and content files (`data/.+\.[0-9a-f]{16}\.json`); the same rule as a regex literal for the worker. Host-only (in `Trainer.Web`, not `Trainer.Contracts`, which ships to the browser); the exporter (Task 16) receives the regex through the export manifest's `EligiblePattern` and needs no reference.
  - `sealed class StaticAssetList(IWebHostEnvironment env)` (singleton): reads `<ApplicationName>.staticwebassets.endpoints.json` from `AppContext.BaseDirectory`; `IReadOnlyList<string> ExportRoutes` (every route except `.gz`/`.br` variants and selector variants, unless `App:StaticSite:BrotliRuntime` is true, which Task 15 introduces and which then keeps the `_framework/` `.br` routes); `IReadOnlyList<string> Immutable` (fingerprinted routes; unfingerprinted aliases excluded).
  - `GET /sw.js` (static only): `sw-template.js` with `const ELIGIBLE = /<StaticAssetRules.JavaScriptRegex>/` prepended; `Content-Type: text/javascript`. The text does not change between builds unless the rule changes.
  - Worker: one cache `dit-immutable-v1:<scope>`. `install` → `skipWaiting()`; `activate` → `event.waitUntil(Promise.all([clients.claim(), registration.navigationPreload?.enable()]))`. `fetch` and `fill` store a response only when `response.ok && !response.redirected && (response.type === 'basic' || response.type === 'default')` and `response.url` equals the requested eligible URL; anything else is returned to the page (or dropped by `fill`) uncached. `fetch`: navigations → `event.respondWith((async () => { try { const r = await event.preloadResponse; if (r) return r; } catch {} return fetch(event.request); })())`; a GET under the scope whose path matches `ELIGIBLE` and has no `Range` → cache first, else network, then `event.waitUntil` of a best-effort `put` (errors caught) followed by pruning; anything else not intercepted. `message` `{ type: 'fill', urls }` → `event.waitUntil` of, for each eligible URL not cached, `fetch(url)` (normally answered by the HTTP cache) and a best-effort `put`. Pruning: least recently used, two budgets (runtime and assets 40 MB by `Content-Length`, content files 600 entries); recency held in memory and persisted at most once a minute as a JSON entry `__recency` in the same cache (best-effort; missing entries count as oldest).
  - `sw-register.js`: after `load`, registers with `{ scope }`; when `navigator.serviceWorker.controller` is set (now or on `controllerchange`), posts `fill` with the eligible URLs of `performance.getEntriesByType('resource')`, and posts again on `dit:island-usable`. Errors logged only.
- ADR 0026: why (Pages `max-age=600` against the warm target); content addressing made sound by the export verifier (Task 16) instead of per-build allowlists; one LRU cache; `skipWaiting` and `claim` because names are content-addressed; fill from Resource Timing (first-visit traffic close to zero, at most the files the page already loaded); navigation preload with a correct fallback; no offline pages; how to reset.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory] [InlineData("_framework/dotnet.native.8j2kz4qmvx.wasm", true)] [InlineData("_framework/blazor.web.js", false)] [InlineData("data/ru/q/01J.0123456789abcdef.json", true)] [InlineData("build.json", false)] [InlineData("sw.js", false)] [InlineData("ru/dotnet/", false)]
public void IsEligible_OnlyContentAddressedPaths(string path, bool expected) { } // the first row's fingerprint is replaced by a real file name from Task 1's record if the format differs
[Fact] public void AssetList_ImmutableHasOnlyFingerprintedRoutes() { /* every Immutable route differs from its label; the blazor.web.js alias is absent */ }
[Fact] public void AssetList_ExportRoutesSkipPrecompressedVariants() { /* default options: no route ends .gz or .br */ }
[Fact] public async Task ServiceWorker_ServedInStaticEditionOnly_WithTheSharedPattern() { /* static: 200 text/javascript starting with const ELIGIBLE = /<JavaScriptRegex>/; server: 404 */ }
[Fact] public void Script_CacheNameCarriesTheScope_SkipWaitingAndClaim_PreloadAwaitedWithFallback_WritesBestEffort() { /* over the template text */ }
[Fact] public void Script_StoresOnlyOkNonRedirectedBasicResponsesForTheRequestedUrl() { /* the template guards every put with ok, !redirected, basic/default type and url equality */ }
[Fact] public async Task Registration_RenderedInStaticEditionOnly_WithThePrefixScope() { /* static pages: sw-register.js with data-scope="/dit/"; server pages: none */ }
[Fact] public async Task ServiceWorker_SetsNoCookie() { /* GET /dit/sw.js has no Set-Cookie */ }
[Fact] public void IslandRoot_RaisesIslandUsableOnce() { /* IslandComponentRenderTests: one dit:island-usable dispatch through the JS fake when Usable is first set */ }
```

- [ ] **Step 2:** `dotnet test tests/Trainer.Web.Tests`, `tests/Trainer.Web.Client.Tests` → FAIL. **Step 3:** Implement; ADR 0026. **Step 4:** Same → PASS (browser behaviour in Task 17).
- [ ] **Step 5:** Commit `feat(web): content-addressed service worker for runtime and content files`.

### Task 15: Boot resource compression as decided in Task 1

If Task 1 recorded `host-gzip`: the only change is the live check's assertion (Task 18) that runtime `.wasm` files arrive with `Content-Encoding: gzip`, plus a one-line note in ADR 0026; commit `docs: record the host gzip decision`. If it recorded `brotli-decode`, do the rest of this task.

**Files:**
- Create: `src/Trainer.Web/wwwroot/js/vendor/brotli-decode.mjs` (Google's `decode.js` from github.com/google/brotli, MIT, licence header kept), `docs/adr/0027-brotli-boot-resources-on-pages.md` (records the decoder's version and SHA-256)
- Modify: `islands-start.js` (static edition), `StaticSite/StaticSiteOptions.cs` (`BrotliRuntime`, bool, `App:StaticSite:BrotliRuntime`), `StaticAssetList` (with `BrotliRuntime` true, export the `.br` variants of `_framework/` routes and list them in `Immutable`; they match `StaticAssetRules` as content-addressed names, and the worker caches only the variant the page actually requests, since there is no precache), `docs/architecture.md` (configuration key, ADR 0027 link), `DocsTests.cs` (expected key), `docs/performance.md`
- Test: `tests/Trainer.Web.Tests/StaticAssetListTests.cs`; the browser tests of the Brotli path (`StaticBrotliBootTests`) and the `gzip: false` transfer case are created in Task 17, which runs after the emulator exists, and are listed there

**Interfaces:**
- Produces: `Blazor.start({ webAssembly: { loadBootResource } })` in the static edition: for `dotnetwasm`, `assembly`, `pdb` and `globalization` types returns `fetch(defaultUri + '.br', { cache: 'no-cache' })` → `import('./vendor/brotli-decode.mjs')` (module, resolved relative to the script) → decode → verify `integrity` with `crypto.subtle.digest('SHA-256', ...)` against the given `sha256-` value → `new Response(decoded, { headers: { 'content-type': <original type> } })`; the handled path is one promise chain whose `catch` (missing or corrupt `.br`, integrity mismatch) returns `fetch(defaultUri, { cache: 'no-cache', integrity })`, because a promise resolving to `undefined` is not a request for default loading (`dotnet.d.ts`: only a synchronous `undefined`/`null` return means default); other types return `undefined` synchronously. CSP unchanged (same-origin module).

- [ ] **Step 1:** Test `AssetList_ExportsBrotliVariantsOfTheRuntime` (with `BrotliRuntime` true: every `_framework/*.wasm` route has its `.br` route in `ExportRoutes` and `Immutable`; `.gz` still absent; Task 14's `AssetList_ExportRoutesSkipPrecompressedVariants` keeps running with the default `false`) → FAIL. **Step 2:** Implement; ADR 0027 with its architecture link. **Step 3:** `dotnet test tests/Trainer.Web.Tests` → PASS. **Step 4:** Commit `perf(static): decode Brotli boot resources on GitHub Pages`.

### Task 16: Export tool, retention and structural verification

**Files:**
- Create: `src/Trainer.Web/StaticSite/StaticExportEndpoints.cs` (host-side record `StaticExportManifest`), `tools/Trainer.StaticSite/Trainer.StaticSite.csproj` (`Microsoft.NET.Sdk.Web`; references `Trainer.Contracts` for `StaticIndexDto`; `AngleSharp` from the central version added in Task 4), `Program.cs`, `ExportManifest.cs` (+ own `JsonSerializerContext`), `StaticSiteLayout.cs`, `SiteExporter.cs`, `Retention.cs`, `RedirectPage.cs`, `ExportVerifier.cs`, `PagesEmulator.cs`, `tests/Trainer.StaticSite.Tests/` (project, no trait)
- Modify: `DevInterviewTrainer.slnx`, `src/Trainer.Web/Program.cs`, `tests/Trainer.Web.Tests/Trainer.Web.Tests.csproj` (reference `tools/Trainer.StaticSite`), delete `tools/spike/`, `docs/architecture.md`, `AGENTS.md` (layout)
- Test: `tests/Trainer.StaticSite.Tests/StaticSiteLayoutTests.cs`, `SiteExporterTests.cs`, `RetentionTests.cs`, `ExportVerifierTests.cs`, `RedirectPageTests.cs`, `tests/Trainer.Web.Tests/StaticExportIntegrationTests.cs`

**Interfaces:**
- Consumes: `IStaticSiteQueries`, `StaticContentCatalog` (Task 8), `StaticAssetList`, `StaticAssetRules` (Task 14), `StaticCsp`, `StaticSiteOptions.BuildId` (Task 13), `SitePaths`.
- Produces:
  - Manifest (`GET /static-export/manifest.json`, static only; JSON from the host-side record, read by the tool's own `ExportManifest` record): `Pages`, `Redirects`, `Files` (`sitemap.xml`, `sitemap-{culture}.xml`, `robots.txt`, `sw.js`), `Indexes`, `Assets` (`StaticAssetList.ExportRoutes`, prefixed), `Immutable` (prefixed immutable routes), `NotFoundPage`, `RedirectPageCsp` (`StaticCsp.For(false, null, code)`), `BuildId`, `EligiblePattern` (`StaticAssetRules.JavaScriptRegex`, used by the verifier); every path entry a site path under the prefix.
  - Commands (exit 0 success, 1 failure, 2 usage):
    - `export --app <url> --site-url <url> --out <dir> --build-id <id> --commit <sha> [--retain-from <live site url> --live-build-id <id> --previous-page-out <file>]`: `--app` is the transport address under the prefix, `--site-url` the public one; entries resolve against the transport origin. An absolute URL met while crawling (`og:image`, redirect `Location`) is accepted when it is under the public site URL or the transport URL, and is fetched by mapping the public prefix onto the transport prefix; output keeps the public URL. The crawl client refuses any other host, so the export never contacts the live site. `HttpClientHandler.AllowAutoRedirect = false`. Pages → `FileFor`; `Redirects` expect 301 or 308 with a same-site `Location` (validated, HTML-escaped) and become `RedirectPage.Html(string target, string csp, string buildId)` (meta refresh, canonical, `noindex`, a visible link, CSP, referrer and `dit-build` meta; no script); `NotFoundPage` expects 404 and becomes `404.html`; other entries expect 200; each saved page's `og:image`, each index's question paths (`StaticContentPaths.Question`) are fetched too; at most 4 in flight; 503 and 429 retried up to 5 times after `Retry-After` (default 2 s); anything else fails naming the URL. Writes `build.json` (`{ "buildId", "commit", "builtAt" }`) and `immutable.json` (`{ "buildId", "files": [...] }`: the `Immutable` asset routes, every Open Graph image and every content file path). With `--retain-from` (omitted on the first deployment, which the workflow's gate identified): `Retention.ApplyAsync` (its own HTTP client) fetches `<live>build.json?_r=<now>` and `<live>immutable.json?_r=<now>`, requires both `buildId`s to equal `--live-build-id` (the id the gate read), retrying for up to 3 minutes when they disagree (CDN propagation) and failing after; downloads every listed file missing from the export into the same path (rejecting an HTML 404 body); and, for a listed name the new export also has, requires identical bytes. With `--previous-page-out`, it also fetches `<live>en/dotnet/practice/?_r=<now>`, requires its `<meta name="dit-build">` to equal the live build id (same retry), and writes it to the file. Retained files are not added to the new manifests.
    - `verify --dir <dir> --site-url <url>`: `ExportVerifier.Verify`.
    - `serve --dir <dir> --site-url <url> [--port <n>] [--gzip]`: `PagesEmulator`.
  - `StaticSiteLayout.FileFor(string sitePath, string pathBase)` (`pathBase` `""` for a root site, `"/dit"` for a prefix); `StaticSiteLayout.Resolve(string requestPath, string pathBase, Func<string, bool> exists) → StaticResolution(int Status, string? File, string? Location)` (file → 200; directory with slash → `index.html`; without → 301; else 404 with `404.html`).
  - `ExportVerifier.Verify(string root, Uri siteUrl) → IReadOnlyList<string>`: AngleSharp parsing of HTML and XML; entities decoded; each URL resolved against the document URL and its `<base>`; checks: `href`, `src`, `srcset`, `action`, `og:*`, canonical and alternate links, import map entries, inline and external CSS `url()` (resolved against the stylesheet), sitemap `loc` and `xhtml:link`, and the URLs inside the decoded persisted component state; a same-host URL must be under the site path and resolve through `Resolve`; external URLs are allowed only for `https://gc.zgo.at/` and `https://*.goatcounter.com/`, GitHub issue links and content source links (`<a>` only); every HTML file (pages, redirect pages, `404.html`, root chooser) has the CSP meta as the first `<head>` child; content addressing: every exported or retained file whose path matches `StaticAssetRules.IsEligible` is listed in `immutable.json` (new or previous), and no file outside that list (HTML, manifests, `sw.js`, unfingerprinted aliases) matches it; every HTML file, redirect pages included, carries the `dit-build` meta with the manifest's build id; every page rendered by the app (not redirect pages) carries the recovery script; retained files are otherwise ignored.
  - `sealed record PagesEmulatorOptions(string Root, Uri SiteUrl, int Port = 0, bool Gzip = true)`; `sealed class PagesEmulator : IAsyncDisposable { static Task<PagesEmulator> StartAsync(PagesEmulatorOptions options); Uri BaseAddress { get; } void SwapRoot(string root); void FailOnce(string sitePath, int status); void Delay(string sitePathPrefix, TimeSpan delay); void MapExtra(string sitePath, string file); }`: `Resolve` rules, `Cache-Control: max-age=600`, no other headers, gzip level 6 for text, JavaScript, JSON and `application/wasm` when `Gzip`; `FailOnce` answers the next request for that path with the status, then serves normally; `Delay` holds matching responses; `MapExtra` serves a file outside the root at a test-only path (the previous-build page). Port 0 picks a free port.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory] [InlineData("/dit/", "/dit", "index.html")] [InlineData("/dit/ru/dotnet/", "/dit", "ru/dotnet/index.html")] [InlineData("/dit/_framework/dotnet.abc123defg.js", "/dit", "_framework/dotnet.abc123defg.js")] [InlineData("/", "", "index.html")] [InlineData("/ru/", "", "ru/index.html")]
public void FileFor_MapsSlashedPagesToIndexHtml(string path, string pathBase, string file) { /* FileFor(path, pathBase) == file */ }
[Fact] public void Resolve_DirectoryWithoutSlash_Is301() { /* Status 301, Location "/dit/ru/" */ }
[Fact] public void Resolve_Missing_Is404WithThe404Page() { /* Status 404, File "404.html" */ }
[Fact] public void FindsARootRelativeUrlWithoutThePrefix() { /* href="/ru/" under /dit/: one problem naming the file and the URL */ }
[Fact] public void FindsAnEntityEncodedEscapingUrl() { /* href="&#47;ru/" is reported */ }
[Fact] public void FindsACssUrlAndAnImportMapEntryOutsideThePrefix() { /* url(/fonts/x.woff2) and an import map entry "/x.js": two problems */ }
[Fact] public void FindsAMissingFileAndAMissingCspMetaOn404AndRedirectPages() { /* a link to a missing file, 404.html and a redirect page without CSP: three problems */ }
[Fact] public void AcceptsSlashedPagesAssetsQueryStringsAndGoatCounter() { /* a valid site: no problems */ }
[Fact] public async Task Export_RetriesBusyResponsesThenSucceeds() { /* 503 with Retry-After 0 twice, then 200: file written, exit 0 */ }
[Fact] public async Task Export_FailsOnAnUnexpectedStatusNamingTheUrl() { /* 500 on a page: exit 1, message contains the URL */ }
[Fact] public async Task Export_CapturesRedirectsWithoutFollowingThem() { /* 301: a redirect page is written; the target is not requested through the redirect */ }
[Fact] public async Task Export_RejectsAnOffSiteRedirectTarget() { /* Location https://evil.test/: exit 1 */ }
[Fact] public async Task Export_Accepts404OnlyForTheNotFoundPage() { /* 404 on NotFoundPage becomes 404.html; 404 on another page fails */ }
[Fact] public void RedirectPage_HasRefreshCanonicalNoindexLinkAndCsp() { /* meta refresh and canonical to the target, robots noindex, a visible link, CSP meta first, referrer and dit-build meta, no script */ }
[Fact] public async Task Retain_CopiesPreviousImmutableFilesIncludingOgImagesOnce() { /* listed files missing from the export are copied once, an og/ PNG included; present files are not fetched */ }
[Fact] public async Task Retain_ManifestsWithDifferentBuildIds_RetryThenFail() { /* stale CDN copy of one manifest */ }
[Fact] public async Task Retain_ManifestOfAnotherBuildThanTheGateRead_Fails() { /* both manifests carry id X, the gate read Y: exit 1 */ }
[Fact] public async Task Retain_PreviousPageOfAnotherBuild_RetryThenFail() { /* dit-build meta differs for 3 minutes (fake clock): exit 1 */ }
[Fact] public async Task RetainedNameWithDifferentBytes_Fails() { /* a listed name present in the export with different bytes: exit 1 */ }
[Fact] public void EligiblePatternMatchesOnlyImmutableFiles() { /* a mutable file with a fingerprint-like name fails verification */ }
[Fact] public async Task Export_WithOutboundAccessBlocked_MapsPublicUrlsToTheApp() { /* handler refuses every host but the app */ }
[Fact] public async Task Export_AcceptsRedirectLocationOnPublicOrTransportOrigin() { /* Location on either origin: the redirect page names the public URL */ }
// Trainer.Web.Tests, static-edition factory, exporter in-process through CreateClient()
[Theory] [InlineData("https://example.test/dev-interview-trainer/")] [InlineData("https://example.test/")]
public async Task ExportOfTheFixtureSite_VerifiesClean(string siteUrl) { /* Verify returns no problems for both base URLs */ }
[Fact] public async Task Manifest_IsNotMappedInServerEdition() { /* 404 */ }
[Fact] public async Task Manifest_SetsNoCookie() { /* no Set-Cookie on /static-export/manifest.json */ }
[Fact] public async Task Emulator_FailOnceThenServes() { /* first GET 503, second 200 with the file */ }
[Fact] public async Task Emulator_DelaysMatchingPaths() { /* Delay("/dit/data/", 2 s): an index response takes at least 2 s, a page does not */ }
[Fact] public async Task Emulator_SwapRootServesTheNewDirectory() { /* after SwapRoot the same path returns the new file's bytes */ }
[Fact] public async Task Emulator_MapsAnExtraFile() { /* MapExtra("/dit/__previous/", f): GET returns f's bytes */ }
[Fact] public async Task Emulator_GzipsWasmOnlyWhenOn() { /* Gzip true: Content-Encoding gzip on .wasm; false: none */ }
```

- [ ] **Step 2:** `dotnet test tests/Trainer.StaticSite.Tests`, `tests/Trainer.Web.Tests` → FAIL. **Step 3:** Implement; add both projects to the solution. **Step 4:** Same → PASS; `dotnet build -warnaserror`.
- [ ] **Step 5:** Commit `feat(tools): export, retain, verify and serve the static site`.

### Task 17: Browser checks of the static site under the prefix

**Files:**
- Create: `tests/Trainer.Web.E2E/StaticSiteFixture.cs`, `StaticSiteTests.cs`, `StaticCatalogFilterTests.cs`, `StaticDeploymentTests.cs`, `StaticBrotliBootTests.cs` (only when Task 1 chose `brotli-decode`), `tests/Trainer.Web.Release/StaticSiteFolder.cs`, `StaticSmokeTests.cs`, `StaticIslandPerformanceTests.cs`
- Modify: both test `.csproj` files (reference `tools/Trainer.StaticSite`), `AppHostFixture.cs` (factor the CSP watcher and the request recorder into helpers the static fixture uses), `docs/performance.md`. The emulator hooks the fixture uses (`SwapRoot`, `FailOnce`, `Delay`, `MapExtra`) come from Task 16; this task adds none.
- Test: the files above

**Interfaces:**
- Produces:
  - `StaticSiteFixture` (`Category=E2E`): emulator on a free port, static-edition `TrainerWebFactory` with `App:PublicBaseUrl=http://127.0.0.1:{port}/dev-interview-trainer/`, in-process export and verify into a temp directory; `ExportBuildAsync(ContentWorkspace?)` produces another build with different content and retention from the current one; `ExpireHttpCacheAsync(IPage)` clears the browser HTTP cache through CDP (`Network.clearBrowserCache`; Cache Storage is untouched), standing in for a return after `max-age=600`; `RemoveFromServedAsync(string path)` deletes one file from the served directory (to model a boot dependency that is gone after the retention window: the startup script, the `dotnet.*.js` boot module, one assembly `.wasm`, the stylesheet); `SwapToAsync(dir)` calls `PagesEmulator.SwapRoot`. A genuinely different Release build (new code) is covered in StaticSmoke against the previous live deployment, below. Every test records every browser request and asserts it is under the prefix or is GoatCounter, and calls `AssertNoCspViolations` per tab.
  - `StaticSiteFolder` (`Category=StaticSmoke`, Release project): reads `DIT_STATIC_SITE_DIR` (absolute), `DIT_STATIC_SITE_URL` and optional `DIT_PREVIOUS_PAGE` (the live Practice page of the identified previous build, captured and verified by the exporter, served by the emulator at a test-only path so the previous Release build's page runs against this artifact's retained files); when the directory or the URL is missing it skips, unless `DIT_STATIC_SMOKE_REQUIRED=1`, which turns the absence into a failure. Serves with `PagesEmulator.StartAsync(new(dir, siteUrl, Gzip: true))` (`Gzip: false` when Task 1 chose `brotli-decode`), with `MapExtra` for `DIT_PREVIOUS_PAGE`.
  - `StaticIslandPerformanceTests` (`Category=StaticSmoke`, so `static-site.yml`'s StaticSmoke run includes it): `MobileProfile`; measures usable (static 5.9) and shell times; cold transfer of every `_framework/` file and the content files fetched (≤ 2.5 MB for `_framework/`, fails always), timings advisory unless `DIT_PERF_ENFORCE=1`; delayed index and first-question responses (2 s added by the emulator) must delay `data-island-ready` (an empty shell cannot pass); runs with 10 000 answers seeded into `dit.outbox.v1.anon` by an init script: usable (which now includes the replay) within the budget, and a Start click immediately after usable begins a session at once for `all`, `new` and `due`, and a `?first=` link shows its question without waiting for the replay; the transfer recorded includes the worker's fill requests after load; writes `TestResults/perf/perf.json` rows with `edition: static`, including the index size.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task RootChooser_SendsToTheStoredOrBrowserLanguage() { /* dit.lang=ru stored: lands on /dev-interview-trainer/ru/; none and Accept-Language en: /en/ */ }
[Fact] public async Task EveryBrowserRequestStaysUnderThePrefixOrIsGoatCounter() { /* home → catalog → topic → question → practice → profile */ }
[Fact] public async Task Practice_RunsFromBrowserStorage_FetchingOnlyShownQuestionsAndTheNext() { /* two answers: data requests are the index, q1, q2, q3 only; the outbox holds two answers */ }
[Fact] public async Task Practice_NextQuestionOffline_ShowsRetryAndKeepsResults() { /* q2 route aborted: retry visible, summary counts unchanged; route restored: retry shows q2 */ }
[Fact] public async Task Practice_FirstLinkFromAQuestionPage_StartsWithThatQuestion() { /* the first card is the question of the page */ }
[Fact] public async Task Review_ShowsSeededBackdatedAnswersAsDue() { /* AddInitScriptAsync seeds dit.outbox.v1.anon */ }
[Fact] public async Task Mock_CompletesAndAppearsInTheProfile() { /* after finishing, the profile lists one recent mock with its counts */ }
[Fact] public async Task TwoTabs_KeepBothAnswers() { /* and the first tab's progress reflects the second tab's answer without a reload */ }
[Fact] public async Task ReportProblem_LinksToThePrefilledIssue() { /* href starts with the issues/new URL and carries the shown question's id */ }
[Fact] public async Task NotFound_ServesThe404PageWithWorkingLinks() { /* unknown path: status 404, not-found heading, the home link loads */ }
[Fact] public async Task ServiceWorker_ScopeIsTheSitePrefix() { /* registration.scope ends with /dev-interview-trainer/ */ }
[Fact] public async Task ServiceWorker_SecondVisitServesRuntimeFromCache() { /* response.FromServiceWorker on the second visit */ }
[Fact] public async Task ServiceWorker_ContentBrowsingDoesNotEvictRuntime() { /* 650 content requests, runtime still cached */ }
[Fact] public async Task ServiceWorker_QuotaFailureStillServesTheResponse() { /* cache.put overridden to reject */ }
[Fact] public async Task ServiceWorker_NavigationWithoutPreload_FallsBackToFetch() { /* preload disabled by an init script before registration */ }
[Fact] public async Task ServiceWorker_NavigationDownloadedOnce() { /* one network request for the document per navigation */ }
[Fact] public async Task ServiceWorker_ReturnAfterHttpCacheCleared_IsServedFromCache() { /* after ExpireHttpCacheAsync, _framework responses come from the worker without network requests */ }
[Fact] public async Task ServiceWorker_404Then200_SecondRequestReachesNetworkAndPageRecovers() { /* emulator answers 404 once for an eligible file */ }
[Fact] public async Task ServiceWorker_503Then200_SecondRequestReachesNetworkAndPageRecovers() { /* FailOnce 503 on an eligible file: second request reaches the network, island boots, Cache Storage holds the 200 */ }
[Fact] public async Task ServiceWorker_MutableAliasNeverCached() { /* request _framework/blazor.web.js and build.json; not in Cache Storage */ }
[Fact] public async Task ServiceWorker_FirstVisitFillNeedsNoExtraNetwork() { /* fill requests answered from the HTTP cache */ }
[Fact] public async Task QueryStringFiltersRows() { /* ?level=junior&topic=<t>&q=<word>: visible rows and count as the server catalog for the query */ }
[Fact] public async Task JunkQueryShowsTheNormalisedSelection() { /* ?level=JUNIOR&level=junior&topic=unknown: junior checked once, no topic checked, rows as ?level=junior */ }
[Fact] public async Task CatalogSearch_MatchesTitleSubstringOrExactTag() { /* q=async matches titles containing it and rows tagged exactly async, not rows tagged only async-await */ }
[Theory, MemberData(nameof(CatalogCases))] public async Task CatalogFilter_MatchesServerCatalogForEveryCase(string query) { /* visible row slugs equal the slugs ICatalogQueries.GetAsync returns across all pages for the same query (factory services); the found count equals Total */ }
// StaticBrotliBootTests (brotli-decode only): Brotli_BootsOnANestedRoute, Brotli_MissingCompressedFile_FallsBack (FailOnce 404 on a .br), Brotli_CorruptCompressedFile_FallsBackAfterIntegrityFailure (a truncated .br in a copy of the export); and in StaticIslandPerformanceTests with Gzip false: transfer <= 2 500 000 bytes
// StaticDeploymentTests (A = first build, B = new code and content, C = one more)
[Fact] public async Task AtoB_CachedIslandPageOfAStillBoots() { /* A island page cached, root swapped to B with A retained: reloading the cached A page boots */ }
[Fact] public async Task AtoB_WithoutServiceWorker() { /* serviceWorkers: block */ }
[Fact] public async Task AtoB_PartiallyCachedAssets() { /* half of A's runtime in the HTTP cache, the rest from retained files: boots */ }
[Fact] public async Task CodeUpdate_OldTabOpen_GetsItsOwnBytes() { /* A tab open, B deployed with different fingerprints; B's worker takes over; the A tab navigates within the island and loads A files (cache or retained); repeated B visits after ExpireHttpCacheAsync are served from the cache */ }
[Fact] public async Task Recovery_BlockedAnalyticsScript_ShowsNoBanner() { /* GoatCounter scripts aborted: #site-updated stays hidden */ }
[Fact] public async Task Recovery_LinkKeepsTheQuery() { /* ?topic=async with a missing stylesheet: link keeps topic and sets _r */ }
[Fact] public async Task Recovery_ShownOncePerPageAndSession() { /* a second failure on the same URL in the session shows no banner */ }
[Fact] public async Task Missing_StartupScript_ShowsRecovery() { /* islands-start.js removed: #site-updated visible within 10 s */ }
[Fact] public async Task Missing_BootModule_ShowsRecovery() { /* dotnet.*.js removed: #site-updated visible within 10 s */ }
[Fact] public async Task Missing_Assembly_ShowsRecovery() { /* one assembly .wasm removed: #site-updated visible within 10 s */ }
[Fact] public async Task Missing_Stylesheet_ShowsRecovery() { /* site.*.css removed: #site-updated visible */ }
[Fact] public async Task AtoB_DeletedQuestionShowsSiteUpdated() { /* question removed in B and beyond retention: island shows the SiteUpdated state */ }
[Fact] public async Task Recovery_ReloadFetchesFreshHtml() { /* _r link after SwapToAsync loads the new build */ }
// StaticSmoke
[Fact] public async Task Smoke_EveryPageKindLoadsAndPracticeStarts() { /* root, home, catalog, topic, question, about, 404 and the four island pages load without console errors; Practice starts */ }
[Fact] public async Task Practice_ColdAndWarm_WithinRegressionBudgets() { /* _framework transfer <= 2 500 000 bytes always; medians asserted only with DIT_PERF_ENFORCE=1 */ }
[Fact] public async Task Practice_UsableWaitsForTheIndexAndProgress() { /* Delay 2 s on data/: data-island-ready later than 2 s */ }
[Fact] public async Task Practice_UsableWith10000Answers_WithinBudget() { /* usable median within the cold and warm budgets with 10 000 seeded answers */ }
[Fact] public async Task Practice_StartImmediatelyAfterUsable_AllNewDueAndFirst() { /* Start clicked at ready for all, new, due: first question within 500 ms; ?first= shows its question before the replay ends */ }
[Fact] public async Task PreviousLiveIslandPage_BootsFromThisArtifact() { /* skipped only on the first deployment, when no previous page exists */ }
[Fact] public void MissingInputs_FailWhenRequired() { /* DIT_STATIC_SMOKE_REQUIRED=1 without DIT_STATIC_SITE_DIR: the fixture throws */ }
```

- [ ] **Step 2:** `dotnet test tests/Trainer.Web.E2E --filter-trait "Category=E2E"` → new tests FAIL. **Step 3:** Implement; defects found go into the owning code. **Step 4:** E2E category → PASS; StaticSmoke locally against an exported Release site with `DIT_STATIC_SMOKE_REQUIRED=1` → PASS.
- [ ] **Step 5:** Commit `test(e2e): browser and deployment checks of the static site under its prefix`.

### Task 18: `static-site.yml`, `pages.yml` and the CI job

**Files:**
- Create: `.github/workflows/static-site.yml`, `.github/workflows/pages.yml`, `.github/scripts/pages-gate.sh`, `tests/Trainer.Deploy.Tests/PagesWorkflowTests.cs`, `deploy/tests/pages-gate.test.sh`
- Modify: `.github/workflows/ci.yml` (job `static-site`), `tests/Trainer.Deploy.Tests/WorkflowHardeningTests.cs` (`github-pages` only on the `deploy` job of `pages.yml`; `production` rules unchanged), `tests/Trainer.Web.Tests/DocsTests.cs` (`Architecture_ListsEveryCiJob` sees the new job), `docs/architecture.md` (CI job list), `AGENTS.md` (layout line for the workflows; rule: `pages.yml` has no schedule and the site URL comes only from the `SITE_URL` repository variable)
- Test: `tests/Trainer.Deploy.Tests/PagesWorkflowTests.cs`, `deploy/tests/pages-gate.test.sh` (run by `deploy-smoke.yml` with the other shell tests)

**Interfaces:**
- Produces:
  - `pages-gate.sh <site_url> <candidate_sha> <event>` (bash, `set -euo pipefail`; `event` is `workflow_run` or `workflow_dispatch`, anything else exits 2): fetches `<site_url>build.json?_r=<epoch ns>`; 200 → reads `buildId` and `commit`; prints `decision=deploy|skip`, `first=false`, `live_build_id`, `live_commit`; `skip` when the candidate is a strict ancestor of the live commit (`git merge-base --is-ancestor` and not equal: the live site is newer), and when the candidate equals the live commit on an automatic run (`event` = `workflow_run`); a candidate equal to the live commit on `workflow_dispatch` gives `deploy` (configuration-only redeploy: same commit, new build id); 404 → fetches `<site_url>?_r=...`: 404 too → `decision=deploy first=true`; anything else (root 200 without a manifest, 5xx after 3 retries, an unknown live commit not in the history) → exit 1. Needs `main` history (`fetch-depth: 0`).
  - `static-site.yml` (`on: workflow_call`), inputs `ref`, `site_url`, `build_id` (required strings); `goatcounter_code`, `google_verification`, `yandex_verification`, `live_build_id` (strings, `required: false`, `default: ''`); `retain`, `upload` (booleans, `required: false`, `default: false`); `permissions: contents: read`. One job `build` (30 min): fails at once when `site_url` is empty; derives `prefix` and `app_url` (`http://127.0.0.1:5080` + prefix); checkout of `ref` (`persist-credentials: false`); SDK 10.0.303 pin and `wasm-tools` from `ci.yml`'s `release` job; `dotnet restore`; `dotnet tool restore`; `dotnet publish src/Trainer.Web -c Release -o "$GITHUB_WORKSPACE/artifacts/web" -p:IncludeSourceRevisionInInformationalVersion=false -p:InformationalVersion=static` (plus any property Task 1 recorded); PostgreSQL from the Compose pin; `dotnet ef database update --project src/Trainer.Infrastructure`; the published app in the background from working directory `artifacts/web` with `ASPNETCORE_ENVIRONMENT=StaticExport`, `ASPNETCORE_URLS=http://127.0.0.1:5080`, `App__Edition=Static`, `App__PublicBaseUrl`, `App__StaticSite__BuildId`, the analytics and verification values, `App__ContentRoot=$GITHUB_WORKSPACE/content`, the connection string, no `AllowedHosts` override; wait 120 s for `<app_url>health/ready`; `export --app <app_url> --site-url <site_url> --out "$GITHUB_WORKSPACE/artifacts/site" --build-id <build_id> --commit <ref>`, plus `--retain-from <site_url> --live-build-id <live_build_id> --previous-page-out "$GITHUB_WORKSPACE/artifacts/previous-page.html"` when `retain`; `verify`; Playwright Chromium; `dotnet test tests/Trainer.Web.Release --filter-trait "Category=StaticSmoke" --minimum-expected-tests 4` with `DIT_STATIC_SITE_DIR`, `DIT_STATIC_SITE_URL`, `DIT_STATIC_SMOKE_REQUIRED=1`, and `DIT_PREVIOUS_PAGE` only when `retain` (the previous-page test is skipped only then, which is exactly the first deployment); perf results uploaded; `actions/upload-pages-artifact` when `upload`.
  - `ci.yml` job `static-site` (needs `build-test`; `permissions: contents: read`): calls `static-site.yml` with `ref: ${{ github.sha }}`, `site_url: ${{ vars.SITE_URL || 'https://ci.invalid/fork-build/' }}` (the fallback exists only because repository variables may be unavailable to fork pull requests; it is a non-root prefix and never uploaded), `build_id: <sha>.<run id>.<attempt>`, `retain: false`, `upload: false`. Runs on every pull request and push.
  - `pages.yml`: triggers `workflow_run` (`ci`, `completed`, `branches: [main]`) and `workflow_dispatch`; no `schedule`, `push`, `pull_request`. `permissions: contents: read`; `concurrency: { group: pages, cancel-in-progress: false }` (an in-progress deployment is never cut off; GitHub replaces a pending run with a newer one; the gate prevents going backwards).
    - `gate` (no environment; `permissions: contents: read, actions: read`): requires `vars.SITE_URL` non-empty; for `workflow_run` requires `conclusion == success`, `event == push`, `head_repository.full_name == github.repository`, candidate = `head_sha`; for `workflow_dispatch` requires `github.ref == refs/heads/main`, candidate = `github.sha`, and a successful `ci` push run for it (`gh run list --commit`); checkout of `main` with `fetch-depth: 0`; runs `pages-gate.sh <site_url> <candidate> <github.event_name>`; outputs `candidate`, `decision`, `first`, `live_build_id`, `build_id` (`<candidate>.<run id>.<attempt>`). When `decision=skip`, writes "already live: <live commit>" to the summary.
    - `build` (needs `gate`, `if: decision == 'deploy'`; `permissions: contents: read`): calls `static-site.yml` with `ref: candidate`, `site_url: vars.SITE_URL`, `build_id`, `retain: first != 'true'`, `live_build_id`, `upload: true`, and the variables `GOATCOUNTER_CODE`, `GOOGLE_SITE_VERIFICATION`, `YANDEX_VERIFICATION`.
    - `recheck` (needs `gate`, `build`; no environment; `permissions: contents: read`): checkout of `main` with `fetch-depth: 0`; runs `pages-gate.sh` again with the same event (same policy); outputs `deploy=true` only when the decision is still `deploy` and the live build id is still the one `gate` saw (or the site is still empty for a first deployment).
    - `deploy` (needs `recheck`, `if: recheck.deploy == 'true'`; `permissions: contents: read, pages: write, id-token: write`; `environment: { name: github-pages, url: ... }`): `actions/deploy-pages`. Only a run that publishes ever creates a deployment record.
    - `live-check` (needs `deploy`, runs only when `deploy` succeeded; `permissions: contents: read`): up to 5 minutes of retries: chooser 200; `<site>en/` and `<site>ru/dotnet/questions/` 200; `<site>en` 301 to `<site>en/`; `<site>no-such-page/` 404 with the not-found heading; `build.json?_r=` and `immutable.json?_r=` carry this run's build id; one index 200; for every runtime `.wasm` in `immutable.json`, `Content-Encoding` and size with `Accept-Encoding: gzip, br` in `$GITHUB_STEP_SUMMARY`, asserted against Task 1's decision.
  - Every action pinned by full SHA.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Pages_GateHasNoEnvironmentAndDeployRunsOnlyOnItsDecision() { /* gate has no environment; deploy has if: on recheck.deploy */ }
[Fact] public void Pages_RecheckRunsImmediatelyBeforeDeployWithoutEnvironment() { /* recheck needs build, deploy needs recheck, recheck has no environment */ }
[Fact] public void Pages_LiveCheckRunsOnlyAfterARealDeploy() { /* live-check needs deploy and runs only on its success */ }
[Fact] public void Pages_BuildsTheTriggeringCommitAndChecksProvenance() { /* candidate is workflow_run.head_sha; conclusion, event and head_repository checks present */ }
[Fact] public void Pages_RetainsExceptOnTheGateIdentifiedFirstDeployment() { /* build passes retain: first != 'true' */ }
[Fact] public void Pages_JobsDeclareEveryPermissionTheirStepsNeed() { /* gate: contents and actions read; deploy: contents read, pages write, id-token write */ }
[Fact] public void Pages_OnlyTheDeployJobHasPagesWriteAndIdToken() { /* no other job declares pages or id-token */ }
[Fact] public void Pages_UsesOneConcurrencyGroupWithoutCancellingInProgress() { /* workflow-level group pages, cancel-in-progress false */ }
[Fact] public void SiteUrl_ComesOnlyFromTheRepositoryVariable() { /* no literal site URL in any workflow; ci fallback is the ci.invalid sentinel */ }
[Fact] public void Ci_StaticSiteJobCallsTheReusableWorkflowWithoutUploadOrRetention() { /* uses ./.github/workflows/static-site.yml with retain false and upload false */ }
[Fact] public void StaticSite_DerivesPrefixAndAppUrlFromTheSiteUrl() { /* no literal dev-interview-trainer in static-site.yml; prefix computed from inputs.site_url */ }
[Fact] public void StaticSite_PublishUsesTheConstantVersion() { /* the publish line has both -p flags and InformationalVersion=static */ }
[Fact] public void StaticSite_AppUrlHostIsAllowedAndAppRunsFromItsPublishDirectory() { /* no AllowedHosts env; the app step's working-directory is artifacts/web */ }
[Fact] public void StaticSite_SmokeStepRequiresItsTestsAndSetsAbsoluteInputs() { /* DIT_STATIC_SMOKE_REQUIRED=1, absolute DIT_STATIC_SITE_DIR, --minimum-expected-tests present */ }
[Fact] public void Pages_PassesTheEventToGateAndRecheck() { /* both pages-gate.sh calls pass github.event_name */ }
[Fact] public void Pages_HasNoScheduleNoPushNoPullRequest() { /* on: has only workflow_run and workflow_dispatch */ }
[Fact] public void Pages_UsesNoSecrets() { /* no secrets. expression in pages.yml or static-site.yml */ }
[Fact] public void GithubPagesEnvironment_OnlyOnTheDeployJobOfPages() { /* only pages.yml's deploy job declares github-pages */ }
[Fact] public void StaticSite_OptionalInputsHaveDefaults() { /* the six optional inputs declare required: false and a default; ci.yml passes only the required ones plus retain/upload */ }
```

Shell tests in `pages-gate.test.sh` (a local HTTP server and a throw-away git history): `live_equal_skips_on_workflow_run`, `live_equal_deploys_on_dispatch`, `live_newer_skips_on_both_events`, `live_older_deploys`, `empty_site_is_first_deployment`, `root_without_manifest_fails`, `unknown_live_commit_fails`.

- [ ] **Step 2:** `dotnet test tests/Trainer.Deploy.Tests`, `tests/Trainer.Web.Tests` (`DocsTests`) and `bash deploy/tests/pages-gate.test.sh` → FAIL. **Step 3:** Write the workflows and the script; adjust the hardening tests; `actionlint`; `shellcheck`. **Step 4:** Same → PASS; push the branch: the pull request's `ci` run includes `static-site`; a green run is required and its URL recorded in the pull request.
- [ ] **Step 5:** Commit `ci(pages): build the static site in CI and deploy it to GitHub Pages`.

### Task 19: Documentation

**Files:**
- Create: `docs/static-edition.md`
- Modify: `README.md`, `AGENTS.md` (repository layout lines for `tools/Trainer.StaticSite`, `tests/Trainer.StaticSite.Tests` and `docs/static-edition.md` only; the rules were written by Tasks 4, 6, 10, 13 and 18), `docs/architecture.md` (the "Static edition" section links `docs/static-edition.md`; ADR links were added by their own tasks), `docs/deployment.md` (top note: the server edition is not deployed; the pre-public audit is in `docs/static-edition.md`), `docs/performance.md`, `tests/Trainer.Web.Tests/DocsTests.cs`

**Interfaces:**
- Produces:
  - `docs/static-edition.md` sections, in order: Overview; How the site is built; Deployments and cached pages (static 5.13, the one-deployment window, the reload recovery); Preview locally (publish with the deterministic flags, PostgreSQL, migrate, run in `StaticExport`, `export`, `verify`, `serve`); Configuration (`App:Edition`, `App:PublicBaseUrl`, `App:StaticSite:RepositoryUrl`, `App:StaticSite:GoogleSiteVerification`, `App:StaticSite:YandexVerification`, `App:Analytics:GoatCounterCode`, `App:StaticSite:BuildId`, `App:StaticSite:BrotliRuntime` (only if Task 15 added it), repository variables `SITE_URL`, `GOATCOUNTER_CODE`, `GOOGLE_SITE_VERIFICATION`, `YANDEX_VERIFICATION`; `build.json` and `immutable.json`); Moving to a custom domain (change the repository variable `SITE_URL`, set the domain in Settings, Pages; **visitors' progress does not move, accepted by the owner: announce the move beforehand**); Progress in the browser (limits of static 5.6); Content updates; Reports from visitors; Troubleshooting (prefix 404, "site updated" that persists, resetting the service worker, a failed `static-site` CI job, a retention failure (manifests of different builds), a skipped run ("already live"), the residual case of a newer commit's pending run replaced by an older one (dispatch `pages.yml`)); Spike results (link to `docs/static-edition-spike.md`); Going public (Task 20); Acceptance log (Task 21).
  - `DocsTests`: `StaticEdition_HasRequiredSections`, `StaticEdition_DocumentsEveryStaticSetting`, `StaticEdition_StatesTheOriginChangeConsequence`, `Agents_DescribesTheStaticEdition` (the layout lines and the five rules of Tasks 4, 6, 10, 13, 18 are present); existing architecture tests stay green.

- [ ] **Step 1:** Add the `DocsTests` → FAIL. **Step 2:** Write the docs. **Step 3:** Full regression set (gate 2) → PASS. **Step 4:** Commit `docs: document the static edition and reconcile the repository docs`.

### Task 20: Pre-public audit and going public (owner-confirmed steps)

**Files:**
- Modify: `docs/static-edition.md` ("Going public": each step, date, result, owner confirmation), `docs/deployment.md` (server-environment items marked "applies when the server edition is deployed"), `tests/Trainer.Web.Tests/DocsTests.cs`

**Interfaces:**
- Produces: `DocsTests.StaticEdition_GoingPublicLogComplete`: every step below listed with a date and `ok`, or `n/a` with a reason.

The implementer prepares each command and records the result; the owner confirms each step before the next. Gate 3 (full regression set) and a green `static-site` CI job on `main` precede Step 1.

- [ ] **Step 0:** Add `DocsTests.StaticEdition_GoingPublicLogComplete` (every step of this task is a row of the "Going public" table with a date and `ok`, or `n/a` with a reason) and the empty table; run `dotnet test tests/Trainer.Web.Tests` → FAIL. The test is committed with the filled table in Step 9, so `main` never carries it red.

- [ ] **Step 1: What becomes public, collected.** In a fresh directory outside the repository: `git clone --mirror https://github.com/Platonenkov/dev-interview-trainer.git audit.git`, then `git -C audit.git fetch origin '+refs/pull/*:refs/pull/*'`; record `git -C audit.git for-each-ref --format='%(refname)' | wc -l` and compare with `gh api repos/Platonenkov/dev-interview-trainer/branches --paginate` and the tag list, so the scan provably covers every branch, tag and pull request head.
- [ ] **Step 2: Secret scan of all refs.** gitleaks from a pinned image (resolve the current tag and digest first): `docker run --rm -v "$PWD/audit.git:/repo:ro" ghcr.io/gitleaks/gitleaks:<tag>@sha256:<digest> git /repo --log-opts="--all" --redact --report-path /dev/stdout`; plus `git -C audit.git log --all --name-only --format= | sort -u` filtered for `.env`, `*.pfx`, `*.key`, `id_*`, `appsettings.*.json`. Exit 0 and no finding required.
- [ ] **Step 3: Personal and infrastructure identifiers.** The owner names the patterns (Telegram id, phone, e-mail, server host names and addresses, provider account ids); run `git -C audit.git log --all -p -G '<pattern>' --format='%H %s'` per pattern; record the hits; the owner decides for each.
- [ ] **Step 4: Remediation rule.** For any real credential found in Steps 2-3 or in Step 5: revoke and rotate it first, always, and record the rotation; only then, separately, the owner decides whether to rewrite history (a force push and an owner action; it neither invalidates the credential nor removes copies already taken). Personal data that is not a credential: owner decision (rewrite or accept).
- [ ] **Step 5: Actions, issues, pull requests, packages, branches.** `gh run list --limit 1000 --json databaseId,workflowName,createdAt` and `gh api repos/Platonenkov/dev-interview-trainer/actions/artifacts --paginate`: review the logs of the deploy, rotate and ops workflows for hosts, user names and ids (variables such as `DEPLOY_HOST`, `ADMIN_TELEGRAM_IDS` are not masked), or delete the runs and artifacts (`gh run delete <id>`; owner confirms the list first); `gh issue list --state all`, `gh pr list --state all` and their comments, `gh release list`, `gh api /user/packages?package_type=container` (visibility of any image); delete stale remote branches (owner confirms the list).
- [ ] **Step 6: Hardening checklist of `docs/deployment.md`.** Items 1-4: `dotnet test tests/Trainer.Deploy.Tests` and `actionlint`. Items 5 and 8: `gh api repos/Platonenkov/dev-interview-trainer/environments`, `gh secret list`, `gh secret list --env production`; secrets not needed while no server exists are deleted by the owner or confirmed; `n/a` where the environment does not exist. Item 6: Dependabot alerts and security updates on. Item 9: covered by Steps 2-4.
- [ ] **Step 7: Settings (owner clicks; exact paths given).** Actions, General: default workflow permissions read-only; "Allow GitHub Actions to create and approve pull requests" off; approval required for first-time contributors' fork workflows. A ruleset on `main` requiring the `ci` checks and forbidding force pushes and deletion. Disable server workflows: `gh workflow disable deploy-app.yml deploy-content.yml rotate-db-password.yml ops-check.yml` (one per call; re-enabled with `gh workflow enable` when the server is deployed). Licences: `LICENSE` (MIT) and `content/LICENSE` (CC BY-SA 4.0) present and named in `README.md`.
- [ ] **Step 8:** Owner: Settings, General, "Change visibility" to public; record the date.
- [ ] **Step 9:** Owner, right after: secret scanning and push protection on (item 7); record; `dotnet test tests/Trainer.Web.Tests` → `StaticEdition_GoingPublicLogComplete` PASS; commit the test and the log, `docs: record the pre-public audit and the public release`.

### Task 21: First Pages deployment and acceptance (owner steps with recorded results)

**Files:**
- Modify: `docs/static-edition.md` ("Acceptance log"), `docs/performance.md` (Results rows "phone, Pages"), `tests/Trainer.Web.Tests/DocsTests.cs`

**Interfaces:**
- Produces: `DocsTests.Performance_RecordsAcceptedPagesPhoneRuns` (rows `phone, Pages` for `/ru` and `/en`, 5 cold and 5 warm values each, medians under 5 000 ms cold and 2 000 ms warm, usable times); `DocsTests.StaticEdition_AcceptanceLogComplete`.

- [ ] **Step 0:** Add `DocsTests.Performance_RecordsAcceptedPagesPhoneRuns` and `DocsTests.StaticEdition_AcceptanceLogComplete` with the empty "Acceptance log" table and no `phone, Pages` rows; run `dotnet test tests/Trainer.Web.Tests` → both FAIL. They are committed with the filled tables in Step 6.
- [ ] **Step 1:** Owner: `gh variable set SITE_URL --body https://platonenkov.github.io/dev-interview-trainer/`; create the GoatCounter site and `gh variable set GOATCOUNTER_CODE`; `gh label create content-report --description "Problem reported from the site"`; Settings, Pages, Source "GitHub Actions".
- [ ] **Step 2:** Dispatch `pages.yml` on `main`; all jobs green; record the run URL and the `live-check` summary (compression as decided in Task 1).
- [ ] **Step 3:** Verify on the live site and record: chooser to RU and EN; canonical, `hreflang` and `og:image` carry the prefix; Telegram preview shows the card in RU and EN; Practice, Mock, Review and profile in a fresh browser profile; "Report a problem" opens the form with id, language and page URL filled (one test issue submitted, label checked, issue closed); GoatCounter shows views without query strings; no cookie and no CSP violation in DevTools; on the first return visit `_framework/` files come from the service worker.
- [ ] **Step 4:** A second deployment (a content-only change merged to `main`): record that every `_framework/` name in `immutable.json` is unchanged and that a page left open from before the deployment still starts Practice; the `PreviousLiveIslandPage_BootsFromThisArtifact` smoke ran (not skipped) in that deployment.
- [ ] **Step 4a:** Owner: set the repository variables `GOOGLE_SITE_VERIFICATION` and `YANDEX_VERIFICATION` to the tokens the consoles issue for the URL-prefix property `https://platonenkov.github.io/dev-interview-trainer/`; redeploy by dispatching `pages.yml` on `main` (a manual run republishes the live commit with a new build id; record that `build.json` shows the new build id and the same commit); verify ownership in Google Search Console and Yandex Webmaster (if Yandex accepts only the origin, place its tag on the account's user site instead and record that); submit `https://platonenkov.github.io/dev-interview-trainer/sitemap.xml` in both; record the dates and the first processing result.
- [ ] **Step 5:** Owner: phone measurement per `docs/performance.md` "Measure on the deployed site" against `https://platonenkov.github.io/dev-interview-trainer/{ru,en}/dotnet/practice/?perf=1`: mid-range Android over 4G; 5 cold (cleared site data) and 5 warm runs (return after one earlier visit, at least one more than 10 minutes later), RU and EN; record phone, versions, network, every usable and shell value and the medians. A missed median stops acceptance; the remedy is already decided (Plan 3 owner decision 4): cut work before the first render first, with no payload growth; AOT only if the warm target alone is missed. A follow-up plan implements it and this step is repeated.
- [ ] **Step 6:** Fill the tables; `dotnet test tests/Trainer.Web.Tests` → the two `DocsTests` PASS. Commit `docs: record the static edition acceptance and the Pages phone measurement`.

---

## Designer's calls

The spec's designer's calls (static section 7) apply; the plan adds:

| Call | Choice | Reason |
|---|---|---|
| Spike first | Task 1 proves the static boot and fixes compression before any refactor | The largest unknown must not be discovered in Task 17 |
| Export environment | `StaticExport`, the only non-test environment where `App:Edition=Static` starts | A production server can never serve the export manifest |
| Server session SQL | Unchanged; parity test against the domain rules | Shared rules without risking the server edition |
| Lazy sessions in both editions | `PracticeSession` over `IQuestionSource`; the server's source is preloaded | One island code path; server requests unchanged |
| Edition-specific island rules | Outbox key, pending filtering and `Exclude` behind `IIslandBackend` | The anonymous outbox means "not sent" on the server and "the log" on Pages |
| Progress model | Full answer log, replayed on read, memoised per page, invalidated by storage changes | No derived state to keep consistent across tabs, stacks or late answers; cost estimated and tested at 10 000 answers |
| Immutable set | `StaticAssetRules` pattern, made sound by the export verifier against `immutable.json` | No per-build allowlist in the worker; a mutable file that looks fingerprinted fails the build |
| Retention | One deployment, via the live `build.json` and `immutable.json` with equal build ids | Covers the 10-minute HTML lifetime; a stale CDN manifest cannot be used |
| Supersession | Gate and recheck jobs without an environment compare with the live `build.json` | No deployment record for a run that publishes nothing; never backwards |
| Boot recovery | Inline recovery script, CSP-hashed, plus a hidden banner on every page | Works when the startup script itself is the missing file |
| Service worker lifecycle | Content-addressed eligibility, `skipWaiting` and `claim`, fill from Resource Timing, no precache | No generations; safe for old tabs because names are content-addressed |
| Verification | AngleSharp in the tool and tests; browser request log in E2E | Structural parsing and the runtime view together |
| Static smoke location | `tests/Trainer.Web.Release`, `Category=StaticSmoke`, required by `DIT_STATIC_SMOKE_REQUIRED=1` and `--minimum-expected-tests` | Test commands in `AGENTS.md` unchanged; the gate cannot pass empty |
| Site build in CI | Reusable `static-site.yml` called by `ci.yml` (every pull request) and `pages.yml` | Proven on the introducing pull request; no dispatch of a workflow absent from `main` |

## Owner decisions

All answered on 2026-11-17 (static section 8): sitemap submission to Google and Yandex with meta-tag verification (Task 21 Step 4a); progress loss on a custom-domain move accepted, no export or import; a missed phone target is remedied per Plan 3 owner decision 4. No decision is open.

## Self-review notes

- Spec coverage: static 5.1 (Tasks 3-6), 5.2 (Tasks 1, 16, 18), 5.3 (Tasks 5-7, 9, 13), 5.4 (Task 8), 5.5 (Tasks 2, 10, 11), 5.6 (Tasks 11, 12, 19), 5.7 (Tasks 9, 11), 5.8 (Task 13), 5.9 (Tasks 1, 11, 14, 15, 17), 5.10 (Tasks 4, 16, 21), 5.11 (Tasks 18, 20 Step 7), 5.12 (Task 20), 5.13 (Tasks 10, 14, 16, 17, 21 Step 4).
- Interfaces: `SitePaths` (3) used by 4, 6, 7, 13, 14, 16; `ClientEdition`, `IslandLinks` (5) by 10, 11; `StaticSiteDto` (8) by 9 (`IssuesNewUrl`) and 11; `StaticContentPaths` (8) by 11, 16; `IIslandBackend` (10) implemented in full in 11, `StaticIslandBackend` given the mock history in 12; `StaticIndex`, `LocalProfileBuilder` (11) by 12; `PracticeSelector`, `ReviewOrder`, `MockComposer` (2) by 11; `LocalProgress`, `LocalProgressCache` (11) by 12; `PagesEmulatorOptions` and the emulator hooks (16) by 17; `StaticCsp` (13) by 16; `StaticAssetList`, `StaticAssetRules` (14) by 15, 16; `PagesEmulator`, `SiteExporter`, `Retention` (16) by 17, 18.
- ADR numbering: 0022 (Task 3), 0023 (8), 0024 and 0025 (13), 0026 (14), 0027 (15, only for `brotli-decode`); each linked from `docs/architecture.md` by its own task.

## Review disposition

Every finding was checked against the code on `main` (`E:/GIT/dev-interview-trainer-plan3`) and, for third-party behaviour, against the cited documentation or marked as verified by Task 1.

| Reviewer | Finding | Disposition |
|---|---|---|
| Codex | 1 resource-collection endpoint | Fixed in spec 5.2, Task 1 Step 3, Task 16 verifier (persisted state). Verified: `IslandEndpointConventions.KeepResourceCollectionOnServer` already keeps the URL out of persisted state; the spike confirms no runtime-generated endpoint is requested |
| Codex | 2 stale HTML and the whole dependency graph | Fixed in spec 5.13, Tasks 10, 13 (inline recovery, round 2), 14, 16 (retention), 17 (`StaticDeploymentTests`), 21 Step 4 |
| Codex | 3 `AllowedHosts=localhost` | Fixed in Task 18 (no override; default allows `127.0.0.1`), test `StaticSite_AppUrlHostIsAllowedAndAppRunsFromItsPublishDirectory` |
| Codex | 4 smoke variable mismatch, skip | Fixed in Tasks 17, 18 (`DIT_STATIC_SITE_URL`, absolute dir, `DIT_STATIC_SMOKE_REQUIRED`, `--minimum-expected-tests`) |
| Codex | 5 `<base>` and `base-uri`, fragment links | Fixed in spec 5.1, Task 4 (`<base>` on island pages only, `CurrentPage` skip link), Task 13 test |
| Codex | 6 Brotli loader details | Fixed in Task 15 (module import, `defaultUri + '.br'`, integrity check, fallback tests) |
| Codex | 7 compression before deploy | Fixed in Task 1 Step 4 and Task 15 (pre-deploy decision), Task 18 live check asserts it |
| Codex | 8 measured state excludes index | Fixed in spec 5.9, Task 11 (`Usable` waits for the index and the progress replay, shell time recorded), Task 17 `Practice_UsableWaitsForTheIndexAndProgress` (delayed responses) |
| Codex | 9 SW first-visit fill, shared FIFO | Fixed (final design after rounds 2-3): one cache with separate LRU budgets for runtime and content, fill from Resource Timing (spec 5.9, Task 14); tests `ServiceWorker_FirstVisitFillNeedsNoExtraNetwork`, `ServiceWorker_ContentBrowsingDoesNotEvictRuntime` (Task 17) |
| Codex | 10 origin-wide names | Fixed in part: cache names carry the scope (Task 14), `theme` → `dit.theme` (Task 5). Rejected in part for `dit.*` storage keys: one deployment of this app per origin; a namespace needs a progress migration for no current benefit (spec 5.6) |
| Codex | 11 regex is not proof of immutability; cache write failures | Fixed (final design): the eligibility pattern is made sound by the export verifier against `immutable.json` (Tasks 14, 16), best-effort writes; tests `EligiblePatternMatchesOnlyImmutableFiles`, `ServiceWorker_QuotaFailureStillServesTheResponse`, `ServiceWorker_MutableAliasNeverCached` |
| Codex | 12 navigation preload contradiction | Fixed (final design): navigation preload enabled, the navigation handler awaits `preloadResponse` and falls back to `fetch` (Task 14); tests `ServiceWorker_NavigationWithoutPreload_FallsBackToFetch`, `ServiceWorker_NavigationDownloadedOnce` |
| Codex | 13 shared-task cache semantics | Fixed in Task 11 (`StaticContentClient` semantics, two tests) |
| Codex | 14 concurrency does not order commits | Fixed (final design): `gate` and `recheck` compare the candidate with the live `build.json` and refuse older commits (spec 5.11, Task 18); tests `live_newer_skips_on_both_events`, `Pages_RecheckRunsImmediatelyBeforeDeployWithoutEnvironment` |
| Codex | 15 verifier coverage | Fixed in spec 5.2, Task 16 (AngleSharp, CSS, import map, persisted state, base resolution), Task 17 request log |
| Codex | 16 redirects, 404, CSP on stubs | Fixed in Task 16 (no auto redirect, validated target, accepted 404, CSP on all HTML), tests |
| Codex | 17 hardcoded prefix | Fixed in Task 18 (derived `prefix`, `app_url`), Task 16 export test for root `/` |
| Codex | 18 rotation vs rewrite; scan coverage | Fixed in Task 20 Steps 1, 4 |
| Codex | 19 profile builder cannot see storage status | Fixed in Task 12 (test at the component boundary) |
| Codex | 20 full suites after every task | Fixed in Global Constraints (affected projects per task, three full gates) |
| Fable | C1 pre-public scan scope | Fixed in Task 20 Steps 1-5 (mirror with pull refs, `log -G`, runs, artifacts, issues, PRs, releases, packages, branches) |
| Fable | M1 AllowedHosts | Fixed (same as Codex 3) |
| Fable | M2 smoke skips | Fixed (same as Codex 4) |
| Fable | M3 Review and outbox semantics | Fixed in Tasks 10, 11 (outbox key, pending filter and `Exclude` behind the backend; two Review tests) |
| Fable | M4 unbounded replay | Fixed in spec 5.6 (size estimate and limit), Task 11 (replay inside the usable time, memoised; `Replay_10000Answers_AppliesTheSchedulerOncePerAnswerAndReadsTheLogOnce`), Task 17 `Practice_UsableWith10000Answers_WithinBudget` |
| Fable | M5 custom domain loses progress | Fixed by documenting: owner accepted the loss on 2026-11-17 (spec 5.6, 8), ADR 0022, Task 19 section and `DocsTests` |
| Fable | M6 fingerprints change every commit; boot failure | Fixed in Global Constraints and Task 18 (deterministic publish, test), Task 1 Step 1 (verified), spec 5.13, Task 10 recovery, Task 17 A-to-B tests |
| Fable | M7 SW not warm, preload, eviction | Fixed (final design, Task 14): fill from Resource Timing makes the first return warm, separate LRU budgets, navigation preload with a correct fallback; tests `ServiceWorker_SecondVisitServesRuntimeFromCache`, `ServiceWorker_ContentBrowsingDoesNotEvictRuntime`, `ServiceWorker_NavigationWithoutPreload_FallsBackToFetch` |
| Fable | M8 gzip budget before remedy | Fixed in Task 1 Step 4 and Task 15 (decision before deploy), Task 17 emulator gzip level stated |
| Fable | M9 spike first | Fixed: Task 1 |
| Fable | M10 older commit over newer | Fixed (same as Codex 14, final design) |
| Fable | m1 sitemap file URLs slashed | Fixed in Tasks 3, 4 (`AbsoluteAsset`, tests) |
| Fable | m2 `IJSInProcessRuntime` before build | Fixed in Task 5 (`[JSImport]` before the builder) |
| Fable | m3 `SessionCandidate` name clash | Fixed in Task 2 (`PracticeCandidate`) |
| Fable | m4 history needs an `outbox.js` operation | Fixed in Task 12 (`historyAdd`, files listed) |
| Fable | m5 search rule | Fixed in Task 7 (title substring or exact tag), Task 17 test |
| Fable | m6 index size | Fixed in spec 5.4 (hash instead of path, estimate corrected), Task 17 reports the size |
| Fable | m7 exporter copies compressed duplicates | Fixed in Task 14 (`ExportRoutes` without `.gz`/`.br`), Task 16 (from the app, no file argument) |
| Fable | m8 manifest DTO in Contracts | Fixed in Task 16 (host-side record, tool's own context) |
| Fable | m9 tool restore, working directory, project reference | Fixed in Tasks 16, 18 |
| Fable | m10 first run after going public | Fixed in Task 18 (the `ci.yml` `static-site` job runs on every pull request while private) and Task 20 precondition |
| Fable | m11 `IslandLinks` without query, missed call sites | Fixed in Task 5 |
| Fable | m12 loading state between questions | Fixed in spec 5.5, Task 10 (skeleton, live region, retry), Task 17 offline test |
| Fable | m13 time policy in replay | Fixed (final design after Codex r3 7): the effective time is `AnsweredAt`, fixed when written; replay never re-clamps (Task 11); tests `Replay_SameLogSameStatesAcrossDaysAndReloads`, `Replay_NeverReClampsAnsweredAt` |
| Fable | m14 fake clock in WASM | Fixed in Task 17 (seeded back-dated answers) |
| Fable | m15 `theme` key | Fixed in Task 5 (`dit.theme`) |
| Fable | m16 Task 2 too large | Fixed: split into Tasks 3, 4, 5; Task 4 driven by `PathBaseTests` |
| Fable | m17 server SQL rewrite risk | Fixed in Task 2 (SQL unchanged, parity tests) |
| Codex r2 | 1 recovery depends on `islands-start.js` | Fixed in spec 5.13, Task 13 (inline CSP-hashed `RecoveryScript` and `SiteUpdatedBanner`), Task 17 tests with the startup script, boot module, an assembly and the stylesheet missing, StaticSmoke `PreviousLiveIslandPage_BootsFromThisArtifact` (a real previous Release build) |
| Codex r2 | 2 waiting policy versus `skipWaiting`/`claim` | Fixed (final design after Codex r3 3): one lifecycle, `skipWaiting` and `claim`, safe because names are content-addressed (spec 5.9, 5.13, Task 14); test `CodeUpdate_OldTabOpen_GetsItsOwnBytes` |
| Codex r2 | 3 stale `immutable.json` from the CDN; OG images missing | Fixed (final design after Codex r3 1): `build.json` and `immutable.json` fetched cache-busted must carry the build id the gate read from the live site; OG images listed (spec 5.13, Tasks 16, 18); tests `Retain_ManifestsWithDifferentBuildIds_RetryThenFail`, `Retain_ManifestOfAnotherBuildThanTheGateRead_Fails`, `Retain_CopiesPreviousImmutableFilesIncludingOgImagesOnce` |
| Codex r2 | 4 Brotli fallback resolves to `undefined` | Fixed in spec 5.9 and Task 15 (the handled promise falls back to `fetch(defaultUri, { integrity })`; synchronous `undefined` only for unhandled types) |
| Codex r2 | 5 first-visit fill not coordinated with boot | Fixed (final design after Codex r3 2): no precache; the page posts the URLs it loaded on `load` and again on `dit:island-usable`, the worker adds them under `waitUntil` (Task 14); tests `ServiceWorker_SecondVisitServesRuntimeFromCache`, `ServiceWorker_FirstVisitFillNeedsNoExtraNetwork`, `IslandRoot_RaisesIslandUsableOnce` |
| Codex r2 | 6 `preloadResponse || fetch` never falls back | Fixed (final design): the handler awaits `preloadResponse` and falls back to `fetch` when it resolves to nothing or rejects (spec 5.9, Task 14); test `ServiceWorker_NavigationWithoutPreload_FallsBackToFetch` |
| Codex r2 | 7 fold computed outside the lock | Fixed by removing folding: the full log is the only record, nothing derived is written (spec 5.6 with the size estimate, Task 11) |
| Codex r2 | 8 snapshot breaks chronological replay | Fixed by full replay on read; test `Replay_LateBackdatedAnswer_TakesItsChronologicalPlace` |
| Codex r2 | 9 snapshot scoped to one index | Fixed: states for every question id, index filtering only for selection and display; test `Replay_KeepsStatesOfOtherStacksAndLanguages` |
| Codex r2 | 10 `InformationalVersion=<sha>` defeats determinism | Fixed in spec 4, Global Constraints, Task 1 Step 1 (byte comparison with the constant version), Task 18 (`InformationalVersion=static`, identity in `build.json`), test `StaticSite_PublishUsesTheConstantVersion` |
| Codex r2 | 11 public versus transport origin in the crawler | Fixed in spec 5.2 and Task 16 (`--site-url` and `--app`, URL mapping, crawl client refuses other hosts); tests `Export_WithOutboundAccessBlocked_MapsPublicUrlsToTheApp`, `Export_AcceptsRedirectLocationOnPublicOrTransportOrigin` |
| Codex r2 | 12 a new dispatch workflow cannot run from a branch | Fixed by moving the build into reusable `static-site.yml` called by a `ci.yml` job on every pull request (Task 18); the `dry_run` dispatch is removed. Verified against GitHub's documentation: `workflow_dispatch` requires the workflow on the default branch, `workflow_call` from `ci.yml` does not |
| Codex r3 | 1 deployment identity from deployment records | Fixed by removing deployment records from the design: `gate` and `recheck` without an environment read the live `build.json`; the deploy job runs only when publishing (spec 5.11, Task 18); tests `Pages_GateHasNoEnvironmentAndDeployRunsOnlyOnItsDecision`, `pages-gate.test.sh` |
| Codex r3 | 2 `addAll(IMMUTABLE)` downloads unused files | Fixed by removing the precache: content-addressed eligibility, fill only with URLs the page loaded (spec 5.9, Task 14); test `ServiceWorker_FirstVisitFillNeedsNoExtraNetwork`; first-visit traffic stated in spec 5.9 |
| Codex r3 | 3 waiting worker leaves new pages unserved | Fixed by removing generations: one content-addressed cache, `skipWaiting` and `claim`; test `CodeUpdate_OldTabOpen_GetsItsOwnBytes` |
| Codex r3 | 4 first deployment captures a 404 page | Fixed: the gate identifies the first deployment (empty site verified), retention and previous-page capture run only otherwise; test `Pages_RetainsExceptOnTheGateIdentifiedFirstDeployment`, `empty_site_is_first_deployment` |
| Codex r3 | 5 previous page from a mutable URL | Fixed: the exporter fetches it cache-busted and requires its `dit-build` meta to equal the identified live build id; test `Retain_PreviousPageOfAnotherBuild_RetryThenFail` |
| Codex r3 | 6 usable before progress; `?first=` circularity | Fixed in spec 5.9 and Task 11: usable includes the replay; `?first=` shows its question independently and selects the rest before Next; tests `Usable_WaitsForIndexAndProgress`, `FirstLink_ShowsTheQuestionWithoutWaitingForProgress_NextWaitsForSelection`, `Practice_StartImmediatelyAfterUsable_AllNewDueAndFirst` |
| Codex r3 | 7 re-clamping on replay moves anchors | Fixed in spec 5.6 and Task 11: the effective time is `AnsweredAt` fixed at write time; replay never re-clamps; tests `Replay_SameLogSameStatesAcrossDaysAndReloads`, `Replay_NeverReClampsAnsweredAt` |
| Codex r3 | 8 two URL literals | Fixed: repository variable `SITE_URL` read by both workflows (spec 4, 5.11; Task 18); test `SiteUrl_ComesOnlyFromTheRepositoryVariable` |
| Fable r2 | N1 retention against deployment records deadlocks | Fixed (same as Codex r3 1 and 4): no deployment-record dependency, no record for skipped runs |
| Fable r2 | N2 no cross-tab change notification exists | Fixed in Task 11 (`watchKey` in `outbox.js`, `ChangedElsewhere` on `OutboxStore`, `HookStore` support; files listed) and Task 17 `TwoTabs_KeepBothAnswers` assertion |
| Fable r2 | N3 Brotli branch precaches both variants | Fixed by removing the precache (Task 14); the worker caches only requested variants (Task 15) |
| Fable r2 | N4 banner for blocked analytics; link drops the query | Fixed in spec 5.13 and Task 13 (`data-recover` on boot-critical elements only, once per page and session, query kept, neutral text); tests `Recovery_BlockedAnalyticsScript_ShowsNoBanner`, `Recovery_LinkKeepsTheQuery`, `Recovery_ShownOncePerPageAndSession` |
| Codex r4 | 1 equal-commit gating blocks configuration-only redeploys | Fixed in spec 5.11 and Task 18 (`pages-gate.sh` takes the event: equal commit skips on `workflow_run`, deploys on `workflow_dispatch`; older always refused; same policy in gate and recheck), Task 21 Step 4a; tests `live_equal_skips_on_workflow_run`, `live_equal_deploys_on_dispatch`, `live_newer_skips_on_both_events`, `Pages_PassesTheEventToGateAndRecheck` |
| Codex r4 | 2 worker caches error responses | Fixed in spec 5.9 and Task 14 (store only ok, non-redirected, basic/default responses for the exact URL, in `fetch` and `fill`); tests `Script_StoresOnlyOkNonRedirectedBasicResponsesForTheRequestedUrl`, `ServiceWorker_404Then200_...`, `ServiceWorker_503Then200_...` (Task 17) |
| Fable r2 | N5 live-check after a superseded run | Fixed: `live-check` runs only after a real deploy (Task 18), test `Pages_LiveCheckRunsOnlyAfterARealDeploy` |
| Codex r2 | 13 deploy job lacks `contents: read` | Fixed (final design): every job declares what its steps use; the supersession check moved to the `recheck` job, which checks out `main` with full history; `deploy` only runs `actions/deploy-pages` (Task 18); test `Pages_JobsDeclareEveryPermissionTheirStepsNeed` |
| Pre-flight | 17 cross-task conflicts and part 3 defects (`preflight.md`) | Fixed in this revision: `LocalProgress.DueAtById`/`DueStates` and `StaticIndex` types (Tasks 2, 11); Task 4 test scope and Task 6 extension; `data-edition` in Static only (Task 4); chooser prefix via `data-base` (Task 6); `IssuesNewUrl` derived, `IndexPath` format (Task 8); full `StaticIslandBackend` in Task 11 with `LocalProfileBuilder` moved there and mock history injected in Task 12; redirect pages carry the build id, not the recovery script (Tasks 13, 16); `BrotliRuntime` option keeps Tasks 14 and 15 tests consistent; Brotli browser tests in Task 17; decoder record in ADR 0027 with its own architecture link (Task 15); `PagesEmulatorOptions` and hooks in Task 16; `StaticIslandPerformanceTests` in `StaticSmoke`; test-project run lists (Tasks 2, 7, 9, 13, 14, 18); `StorageKeys.cs`, resources and AngleSharp package edits listed; `StaticAssetRules` host-only; `pages-gate.sh` three arguments; optional `static-site.yml` inputs; `AGENTS.md` rules in Tasks 4, 6, 10, 13, 18; DocsTests red first (Tasks 20, 21); assertion lines for tests; counting replay test instead of wall-clock; parity tests `LocalProfileMatchesProgressQueries` and `CatalogFilter_MatchesServerCatalogForEveryCase`; stale disposition rows corrected |
