# Plan 2: Sign-in, Learning Modes and Interactive Islands — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Visitors practise in Practice and Mock modes (progress kept in the
browser), signed-in users sign in with Telegram, get server-side progress, spaced
repetition in Review, statistics and a streak, and the admin gets health, import
status and a user block flag, all in RU and EN, on every screen size, without
breaking the static, cookieless, cached public site.

**Architecture:** Grading and scheduling are pure Domain code shared by the server
and a new WebAssembly client (`Trainer.Web.Client`), which hosts four
non-prerendered islands (Practice, Mock, Review, profile) on noindex pages reached
by full page loads. Islands fetch a whole session up front through `/api` (DTOs in
the new `Trainer.Contracts` with a source-generated JSON context), grade answers
locally, write every answer to a per-owner `localStorage` outbox under a cross-tab
lock (answer times already corrected by a server-clock offset) and flush it to an
idempotent, owner-checked batch endpoint. Sign-in is `Telegram.OpenIdConnect.AspNetCore` with a database-backed
gate, a persistent 30-day sliding cookie and a 5-minute security-stamp
revalidation; public pages stay static SSR and cached for anonymous requests,
while signed-in requests bypass the cache and render personal fragments.

**Tech Stack:** .NET 10, ASP.NET Core Razor components (static SSR + InteractiveWebAssembly
islands), `Telegram.OpenIdConnect.AspNetCore` 1.1.0, EF Core + Npgsql, ASP.NET Core
Data Protection (EF Core store), rate limiting middleware, xUnit v3 on
Microsoft.Testing.Platform, Testcontainers.PostgreSql, Microsoft.AspNetCore.Mvc.Testing,
Microsoft.Extensions.TimeProvider.Testing, Microsoft.Playwright.

**Spec:** `docs/superpowers/specs/2026-11-11-dev-interview-trainer-design.md`
(read it first; section numbers below refer to it). Plan 1
(`docs/superpowers/plans/2026-11-11-plan-1-foundation-content-public-site.md`) is
done; its code is the starting point and its conventions still apply.

**Revision:** v3 (2026-11-12), after the second Codex and Fable reviews. v3 changes:
client-side server-clock calibration with `answeredAt` fixed at answer time, server
rejects answers older than 1 year or more than 5 minutes in the future (no
normalisation of history); answer metadata separated from scheduling fields; no
unlocked storage fallback (memory-only instead); deadlock-free multi-key locking
with destination-first moves; mock state split into a write-once question key and a
small revisioned progress document; explicit self-assessment eligibility and Skip;
failed summaries kept recoverable; `/api` error middleware over the whole path;
sign-in log reason codes; raised rate limits in every test host; async streak
accumulator; one enforced performance run before completion; tests moved to the
tasks where their dependencies exist; caching and BREACH middlewares limited to
HTML pages; document-identity navigation test; answers keep a server id plus a
unique client id (spec 8.6); deviations needing owner approval listed in one section.

v2 (after the first reviews) changed:
ULID ids for every new entity; account-bound writes (owner header, 409 on mismatch,
cross-tab account broadcast); outbox mutations under a Web Lock, never evicted;
durable mock state and pending summaries; client-time normalisation instead of a
skew window; corrected not-yet-due rule; unanswered Mock questions only in the
summary; persistent auth cookie via `SigningIn`; access-denied redirect built in the
app; sign-in log redaction; admin list read at startup; BREACH guard narrowed and
null-safe; JSON errors under `/api`; configurable rate limits; full page loads
between public and island pages; 44 px targets everywhere; topic sidebar; island
state table; ADRs for every deviation; Tasks 4, 11 and 12 split; docs updated in
the task that changes behaviour; published Release smoke and performance job.

## Roadmap (all plans)

| Plan | Scope | Spec sections | Depends on |
|---|---|---|---|
| 1 (done) | Solution, domain keys, content schema/reader/validator/renderer, snippet sandbox, CLI, DB for content, importer, public SSR pages, i18n, SEO, output cache, themes, responsive layout, CI, scheduled link checker | 3, 5.1, 5.2 (schema), 6, 7.1-7.5, 8.1-8.6 (public part), 9, 10 | — |
| **2 (this)** | Telegram sign-in, users/roles/security stamp, Data Protection key store, grading, scheduler (SM-2 behind an interface), answers outbox + `/api`, WASM islands (Practice, Mock, Review, profile), personal fragments on public pages, admin page, catalog search and related questions, `blazor.web.js` on island pages only, ADRs, WASM performance measurement | 4, 5.1 (tags), 5.2-5.4, 8.2 (islands), 8.3, 8.6 (user tables), 8.7 (key store only), 8.8, 9/9.1 (new screens), 10 (ADRs), 11, 12 | 1 |
| 3 | Deployment: Dockerfile, compose, migration bundle, bootstrap script, `deploy-app`, `deploy-content`, `rotate-db-password`, internal import endpoint, backups (encrypted; they include `data_protection_keys`, ADR 0004), `docs/deployment.md`; renders `App__AdminTelegramIds`, `App__RateLimits__*` and `TelegramOidc__ClientId/ClientSecret` into `.env`; nginx must not compress app responses (`gzip off` on the proxied location, BREACH guard of Task 6) and must send `X-Forwarded-For`/`X-Forwarded-Proto` so per-IP rate limits see client addresses; the published client is built with the pinned `wasm-tools` workload; acceptance: spec 8.2's targets (Practice interactive within 2 s warm and 5 s cold) measured on a real mid-range phone over 4G against the deployed site with `?perf=1`, 5 cold and 5 warm runs, RU and EN (ADR 0014, `docs/performance.md`) | 8.2 (targets), 8.7 | 1, 2 |
| 4 | Content production: ~150 questions for the private build, then ~400, in topic batches reviewed by the owner | 6, 7.6 | 1 |

Each plan is reviewed by Codex and Fable before execution.

## Global Constraints

Plan 1 constraints that still hold:

- Target framework `net10.0`; `global.json` SDK `10.0.203`, `rollForward: latestFeature`, test runner Microsoft.Testing.Platform; `Directory.Build.props` (nullable, warnings as errors, `AnalysisLevel=latest-recommended`, NuGet audit `all`/`low`); central package management.
- Explicit types instead of `var`; async with `CancellationToken` for IO; `TimeProvider` instead of `DateTime.Now`/`DateTimeOffset.UtcNow` everywhere (server and client); `StringComparison.Ordinal*`; cached `JsonSerializerOptions` or source-generated contexts.
- Comments, docs, commit messages in English; Conventional Commits; no AI attribution lines anywhere.
- No FluentAssertions, no Moq, no bUnit; no package whose binaries carry a paid or fee-bearing licence (check NuGet licence and README before adding). xUnit v3 built-in `Assert`.
- Levels `junior`, `middle`, `senior`, `lead`, `architect` (ordered); question types `open`, `single`, `multi`, `output`, `find-bug`, `situation`; cultures `ru`, `en` as the first URL segment, stack second.
- WCAG 2.2 AA: 4.5:1 body text, 3:1 large text and UI colours; theme colours only through tokens in `tokens.css`.
- Responsive widths checked: 375, 768, 1280, 1920 px; no horizontal page scroll at 320 px. Code panels scroll inside themselves; code font never below 13 px.
- Migrations are never applied at production startup; tests migrate the template database in `PostgresFixture`.
- Public pages (home, stack home, about, catalog, topic, question) are static SSR: no interactive components, no `[StreamRendering]`, no `blazor.web.js`; anonymous responses carry no `Set-Cookie` and are output-cached.

Plan 2 constraints:

- New packages (exact IDs): `Telegram.OpenIdConnect.AspNetCore` `1.1.0`; `Microsoft.AspNetCore.Authentication.OpenIdConnect`, `Microsoft.AspNetCore.Components.WebAssembly`, `Microsoft.AspNetCore.Components.WebAssembly.Server`, `Microsoft.AspNetCore.Components.WebAssembly.Authentication`, `Microsoft.AspNetCore.Components.Authorization`, `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` at the same 10.0.x patch as `Microsoft.AspNetCore.Mvc.Testing`; tests only: `Microsoft.Extensions.TimeProvider.Testing`, `Microsoft.IdentityModel.JsonWebTokens`. No UI component library and no ULID package.
- Project references: Domain → nothing. Contracts → nothing. Application → Domain, Contracts (ADR 0006). Content → Domain. Snippets → Content. Infrastructure → Application, Content. Web.Client → Domain, Contracts. Web → Infrastructure, Application, Contracts, Web.Client. ContentCli → Content, Snippets. **Web never references Snippets; Web.Client never references Application, Infrastructure or Content.**
- Ids (spec 8.6): every new entity id is a ULID (`char(26)`): users, answers (server-generated `id` plus the unique client-generated `client_answer_id`, as spec 8.6 lists both), mock sessions (client-generated). `telegram_user_id` is a unique `bigint` column, never a primary key. ULIDs come from `UlidGenerator` (Domain).
- Islands: `InteractiveWebAssembly` only, `new InteractiveWebAssemblyRenderMode(prerender: false)`, only on the noindex pages `/{culture}/{stack}/practice`, `/mock`, `/review`, `/profile`. `AddInteractiveServerComponents` is never called. `blazor.web.js` is rendered only on pages marked `[InteractiveIslandPage]` and starts with enhanced navigation disabled, so every navigation between public and island pages is a full page load (ADR 0009).
- Forms: anonymous public responses contain no POST form and no antiforgery token; the only form there is the catalog search `<form method="get">` (ADR 0011). POST forms appear only in signed-in responses (logout) and on the admin page.
- Auth (spec 4, 8.8, 11.2): cookie scheme `TelegramCookie`, OIDC scheme `TelegramOidc`, callback `/signin-telegram`, `LoginPath` `/signin`, `AccessDeniedPath` `/signin?denied=1`; cookie name `.dit.session` (`AuthCookieNames.Session`, ADR 0002), persistent, `SameSite=Lax`, `Secure`, `HttpOnly`, "auth cookie lasts 30 days with sliding expiration" (`CookieTtlHours = 720`), security stamp re-checked "at most every 5 minutes". Admin ids from `App:AdminTelegramIds`, read at startup; a change takes effect on restart (every deploy restarts the app).
- Secrets: `TelegramOidc:ClientSecret` only from environment variables or user secrets; never in `appsettings*.json`.
- Antiforgery (spec 8.8): token from `GET /api/antiforgery` (no-store), sent in the `X-CSRF-TOKEN` header, validated explicitly by every `/api` write; antiforgery cookie `.dit.af`.
- BREACH: no response compression for authenticated HTML responses and `/api/antiforgery` (they carry antiforgery tokens). Authenticated HTML responses also send `Cache-Control: private, no-store`.
- Answers (spec 8.8): outbox format `{ version, answers: [{ clientAnswerId, questionId, grade, usedHint, mode, mockSessionId?, answeredAt }] }`, `version = 1`; batch size ≤ 500; every write carries `X-Outbox-Owner` (Telegram user id) and `X-Client-Utc-Offset` (device offset in minutes, used only for streak days). Timestamp policy (ADR 0005): the client calibrates a server-clock offset from the `Date` header of its `/api` responses and writes `answeredAt` already corrected at the moment of answering, so retries and later clock changes never redate it; the server rejects (acknowledges as rejected, never stores) answers older than 1 year (`answered-too-old`) or more than 5 minutes in the future (`answered-in-future`), clamps an `answeredAt` up to 5 minutes ahead to its own now, and schedules with the stored `answeredAt`; date arithmetic is done on the server's clock (`serverNow − 365 days`, `serverNow + 5 min`) and only compared with client values, so extreme dates cannot overflow. Per-user rate limit on writes; the grade itself is trusted. The client never drops unacknowledged answers.
- The WASM client never sets `InvariantTimezone` (the device offset feeds `X-Client-Utc-Offset`).
- Grades `knew`, `partial`, `didnt-know`; modes `practice`, `mock`, `review`. Multi: "exact set = `knew`, at least one correct pick and at most one mistake = `partial`, otherwise `didnt-know`"; a used hint caps the result at `partial`.
- Scheduler: pure Domain function behind `IReviewScheduler`; "`didnt-know` resets the interval to 1 day"; "an answer to a question that is not yet due can only shorten its interval"; "a first answer to a new question creates its review state".
- Mock: "N questions (default 20)"; warn when the pool is "smaller than about 3×N", before the session starts.
- WASM: trimming on, trim analyzers on (warnings are errors), every `/api` body (de)serialised through `Trainer.Contracts.TrainerJsonContext`; content arrives as pre-rendered HTML; dynamic sizes use `<progress>`, `<meter>` or SVG attributes (the CSP allows no inline styles). Performance targets (spec 8.2): "on a mid-range phone over 4G, Practice is interactive within 2 s with a warm cache and within 5 s cold", measured and recorded in `docs/performance.md`.
- Keyboard (spec 9.1): "1–4 to pick an option, Enter to submit, N for next", hints shown only under `@media (pointer: fine)`. Touch targets at least 44×44 px for every interactive element on every pointer type; nothing depends on hover.
- Every new screen has RU and EN strings in `.resx` (server: `SharedResource`, client: `ClientResource`); a page never mixes languages; key-parity tests cover both resource sets.
- Logging: never log `Authorization`, `Cookie`, `Set-Cookie`, `X-CSRF-TOKEN` or token values; Telegram usernames, display names and client IPs are not logged (the library's `TelegramOidc` category is filtered, Task 5); no `HttpLogging` middleware.
- Rate limits: every test host (`TrainerWebFactory`, and through it `AppHostFixture`; `PublishedAppFixture` through `App__RateLimits__*`) runs with 100 000 per minute except the dedicated low-limit tests; startup rejects non-positive limits.
- Every decision recorded in `docs/adr/NNNN-title.md` (Status, Context, Decision, Consequences) is written by the task that introduces it; the ones that change spec text are listed under "Deviations needing owner approval" and are not executed before that approval. `docs/architecture.md`, `README.md` and `AGENTS.md` are updated in the task that changes the behaviour they describe.
- `ContentSchema.RenderVersion = 3` after Task 8 (payload gains per-line code HTML).
- Test commands (MTP): unit/integration `dotnet test --filter-not-trait "Category=E2E" --filter-not-trait "Category=Docker" --filter-not-trait "Category=Release" --ignore-exit-code 8`; Docker `--filter-trait "Category=Docker"`; E2E `--filter-trait "Category=E2E"`; Release `--filter-trait "Category=Release"` (needs `DIT_RELEASE_PUBLISH_DIR` and `DIT_RELEASE_CONNECTION`, skips without them). All keep `--ignore-exit-code 8`. Recorded in `AGENTS.md` and `ci.yml`.

## Review Focus

1. **The same answers flushed twice** (two open tabs, or a retry after the acknowledgement was lost, possibly after the question was retired). Expect: one `answers` row per id, the review state advanced once, the retry acknowledged as accepted. Tests: Task 7 `RecordBatch_SameBatchConcurrently_StoresOnceAndSchedulesOnce`, `RecordBatch_RetryAfterQuestionRetired_IsAccepted`; Task 13 E2E `Outbox_ConcurrentAppendsFromTwoTabs_LoseNothing`.
2. **A device clock that is wrong or changes, and late answers from an offline device.** Expect: `answeredAt` is corrected once, when answering, and never redated by retries or later clock changes; stored times are never in the future; an answer given before the due time but delivered after it is scheduled by when it was given; an older answer never rewinds the schedule. Tests: Task 14 `ServerClock_CalibratesFromDateHeader`, `Answer_ClockChangedBeforeFlush_KeepsOriginalTime`; Task 7 `RecordBatch_FutureBeyondFiveMinutes_Rejected`, `RecordBatch_DeliveredAfterDue_UsesAnsweredAt`, `RecordBatch_OlderAnswerAfterNewer_DoesNotRewindSchedule`; Task 2 `Apply_EarlyKnewThenDelayedFailure_FailureIsStale`.
3. **`localStorage` unavailable, full, or holding corrupted or unknown-version JSON.** Expect: Practice works for the session with a visible notice, pending answers are never evicted, unreadable data is kept, never overwritten. Tests: Task 13 `Load_CorruptedJson_KeepsRawAndStartsEmpty`, `Load_UnknownVersion_LeftUntouchedAndReportsNewerVersion`, `Add_QuotaExceeded_KeepsSnapshotInMemoryAndReports`, `Outbox_NeverEvictsPendingAnswers`, `NoLocks_SwitchesToMemoryOnly`; Task 16 E2E `Practice_WithStorageBlocked_ShowsNotice`.
4. **One browser, two accounts.** Expect: answers written while signed in as A are never stored under B, even after B signs in in another tab and the old tab flushes. Tests: Task 11 `Batch_OwnerHeaderMismatch_Returns409AndStoresNothing`; Task 14 `Flush_On409OwnerMismatch_KeepsEntriesAndStops`; Task 16 E2E `TwoTabs_AnswersOfAStayWithA_WhenBSignsIn`.
5. **A blocked user or a user whose roles changed, holding a 30-day cookie.** Expect: within 5 minutes pages treat them as signed out (blocked) or reflect the new roles, `/api` answers 401, and no database check happens before the interval. Tests: Task 6 `BlockedUser_IsSignedOutAfterRevalidationInterval`, `RoleChange_RefreshedAfterInterval`, `WithinInterval_NoDatabaseCheck`; Task 11 `BlockedUser_ApiReturns401AfterInterval`.

---

## File Structure

```
src/Trainer.Domain/
  Grading/Grade.cs, GradeKeys.cs, AnswerMode.cs, AnswerModeKeys.cs, OutputText.cs, AutoGrading.cs, IAnswerGrader.cs, SelfAssessmentGrader.cs
  Ids/UlidText.cs, UlidGenerator.cs, ClientAnswerId.cs, MockSessionId.cs        (QuestionId switches to UlidText)
  Scheduling/ReviewState.cs, IReviewScheduler.cs, Sm2ReviewScheduler.cs, Sm2Constants.cs, StreakCalculator.cs
  Users/UserId.cs, Roles.cs
src/Trainer.Contracts/                       DTOs + TrainerJsonContext (new project)
src/Trainer.Application/                     Users/*, Answers/*, Sessions/*, Progress/*, Mock/*, Admin/*
src/Trainer.Infrastructure/
  Persistence/Entities/{User,UserRole,Answer,ReviewState,MockSession}Entity.cs, Configurations/*, Migrations/{UsersAndAccess,DataProtectionKeys,LearningProgress}
  Users/UserAccountService.cs, Answers/AnswerRecorder.cs, Sessions/SessionQueries.cs, Progress/ProgressQueries.cs, Mock/MockSessionService.cs, Admin/AdminQueries.cs
src/Trainer.Content/Rendering/              RenderCodeLines (per-line HTML)
src/Trainer.Web/
  Auth/*, Security/*, Api/*, Islands/*
  Components/Pages/{SignIn,Practice,Mock,Review,Profile,Admin}.razor, Components/Layout/AccountControls.razor, Components/Shared/StatusChip.razor
  wwwroot/js/{islands-start,outbox,shortcuts,runtime-preload,import-notice}.js, wwwroot/css/islands.css
  Properties/launchSettings.json
src/Trainer.Web.Client/                      WASM islands (new project): Program.cs, Localization/*, Api/*, Storage/*, Sync/*, Sessions/*, Islands/*, Components/*
tests/Trainer.Application.Tests/             (new)
tests/Trainer.Web.Client.Tests/              (new)
tests/Trainer.Web.Release/                   (new) Category=Release: published smoke and performance
tests/Trainer.TestSupport/                   FakeTelegramOidcServer.cs, TelegramSignInDriver.cs, SessionCookieReader.cs, CapturingLoggerProvider.cs
docs/adr/0001..0011-*.md, docs/performance.md (new); docs/architecture.md, README.md, AGENTS.md
```

---

### Task 1: Grading in the domain

**Files:**
- Create: `src/Trainer.Domain/Grading/*`; `docs/adr/0001-grading-in-domain.md`
- Modify: `src/Trainer.Content/Validation/ContentValidator.cs` (replace private `NormaliseOutput` with `OutputText.Normalise`), `tests/Trainer.Domain.Tests/PersistedEnumValuesTests.cs`, `docs/architecture.md` (Domain row), `AGENTS.md` (`docs/adr/` now exists)
- Test: `tests/Trainer.Domain.Tests/AutoGradingTests.cs`, `GradeKeysTests.cs`, `SelfAssessmentGraderTests.cs`

**Interfaces:**
- Produces:
  - `enum Grade { DidntKnow = 0, Partial = 1, Knew = 2 }`; `static class GradeKeys { string ToKey(Grade); bool TryParse(string? key, out Grade grade); IReadOnlyList<Grade> All { get; } }` keys `didnt-know`, `partial`, `knew` (case-sensitive: keys travel only between our own client and server).
  - `enum AnswerMode { Practice = 1, Mock = 2, Review = 3 }`; `static class AnswerModeKeys` (same members; `practice`, `mock`, `review`).
  - `static class OutputText { string Normalise(string value); }` — `\r\n` and `\r` → `\n`, then `Trim()` (the rule the validator already applies).
  - `static class AutoGrading`: `Grade CapForHint(Grade grade, bool usedHint)`; `Grade Single(string correctOptionId, string pickedOptionId, bool usedHint)`; `Grade Multi(IReadOnlyCollection<string> correctOptionIds, IReadOnlyCollection<string> pickedOptionIds, bool usedHint)`; `Grade TypedOutput(IReadOnlyList<string> acceptedOutputs, string typed, bool usedHint)`; `Grade FindBug(IReadOnlyList<IReadOnlyList<int>> acceptedLineSets, IReadOnlyCollection<int> selectedLines, bool usedHint)`. Ordinal comparisons; sets compared as sets.
  - Multi rule: `mistakes = |picked \ correct| + |correct \ picked|`; `mistakes == 0` ⇒ `Knew`; `|picked ∩ correct| ≥ 1 && mistakes ≤ 1` ⇒ `Partial`; else `DidntKnow`; then `CapForHint`.
  - `record SelfGradedAnswer(QuestionId QuestionId, QuestionType Type, string? Text, Grade SelfAssessment, bool UsedHint)`; `interface IAnswerGrader { ValueTask<Grade> GradeAsync(SelfGradedAnswer answer, CancellationToken ct); }`; `sealed class SelfAssessmentGrader : IAnswerGrader` — `CapForHint(SelfAssessment, UsedHint)`; `ArgumentException` for types other than `Open` and `Situation`. `Text` is for a future LLM grader and ignored in v1.
  - ADR 0001: spec 8.1 lists `IAnswerGrader` under Application; spec 11.5 requires grading in the WASM client, which references Domain only, so grading and `IAnswerGrader` live in Domain.

- [ ] **Step 1: Write failing tests**

```csharp
[Theory]
[InlineData(new[] { "a", "b", "c" }, new[] { "a", "b", "c" }, false, Grade.Knew)]
[InlineData(new[] { "a", "b", "c" }, new[] { "c", "a", "b" }, true, Grade.Partial)]
[InlineData(new[] { "a", "b", "c" }, new[] { "a", "b" }, false, Grade.Partial)]          // one missed
[InlineData(new[] { "a", "b" }, new[] { "a", "b", "d" }, false, Grade.Partial)]          // one extra
[InlineData(new[] { "a", "b", "c" }, new[] { "a" }, false, Grade.DidntKnow)]             // two missed
[InlineData(new[] { "a", "b" }, new[] { "a", "c", "d" }, false, Grade.DidntKnow)]
[InlineData(new[] { "a" }, new[] { "b" }, false, Grade.DidntKnow)]                       // no correct pick
[InlineData(new[] { "a", "b" }, new string[0], false, Grade.DidntKnow)]
public void Multi_FollowsSpecRule(string[] correct, string[] picked, bool hint, Grade expected) => Assert.Equal(expected, AutoGrading.Multi(correct, picked, hint));
[Fact] public void Single_CorrectWithHint_IsPartial() => Assert.Equal(Grade.Partial, AutoGrading.Single("b", "b", usedHint: true));
[Fact] public void Single_WrongWithHint_StaysDidntKnow() => Assert.Equal(Grade.DidntKnow, AutoGrading.Single("b", "a", true));
[Theory] [InlineData("333", true)] [InlineData("  333\r\n", true)] [InlineData("3 3 3", false)] [InlineData("", false)]
public void TypedOutput_NormalisesLikeTheValidator(string typed, bool knew) => Assert.Equal(knew ? Grade.Knew : Grade.DidntKnow, AutoGrading.TypedOutput(["333"], typed, false));
[Fact] public void TypedOutput_MultilineCrLf_MatchesLf() => Assert.Equal(Grade.Knew, AutoGrading.TypedOutput(["1\n2"], "1\r\n2", false));
[Fact] public void FindBug_SelectionEqualToAnyAcceptedSet_IsKnew() => Assert.Equal(Grade.Knew, AutoGrading.FindBug([[7], [3, 4]], [4, 3], false));
[Fact] public void FindBug_Superset_IsDidntKnow() => Assert.Equal(Grade.DidntKnow, AutoGrading.FindBug([[7]], [7, 8], false));
[Fact] public async Task SelfAssessment_HintCapsKnew() => Assert.Equal(Grade.Partial, await new SelfAssessmentGrader().GradeAsync(new(Id, QuestionType.Open, null, Grade.Knew, true), CancellationToken.None));
[Fact] public async Task SelfAssessment_RejectsAutomaticTypes() => await Assert.ThrowsAsync<ArgumentException>(() => new SelfAssessmentGrader().GradeAsync(new(Id, QuestionType.Single, null, Grade.Knew, false), default).AsTask());
[Fact] public void GradeKeys_RoundTrip() { foreach (Grade g in GradeKeys.All) { Assert.True(GradeKeys.TryParse(GradeKeys.ToKey(g), out Grade back)); Assert.Equal(g, back); } }
[Fact] public void GradeKeys_RejectUnknown() => Assert.False(GradeKeys.TryParse("KNEW", out _));
```

`PersistedEnumValuesTests`: add `Grade` (0, 1, 2) and `AnswerMode` (1, 2, 3) pins.

- [ ] **Step 2:** Run `dotnet test tests/Trainer.Domain.Tests` → FAIL (types missing). **Step 3:** Implement; switch `ContentValidator` to `OutputText.Normalise`; write ADR 0001 and the doc rows. **Step 4:** Domain and Content tests PASS.
- [ ] **Step 5:** Commit `feat(domain): add grades, answer modes and automatic grading`.

### Task 2: Scheduling, streak and ids

**Files:**
- Create: `src/Trainer.Domain/Scheduling/*`, `Ids/*`, `Users/UserId.cs`, `Users/Roles.cs`
- Modify: `src/Trainer.Domain/Questions/QuestionId.cs` (validate through `UlidText.IsValid`, drop its regex), `docs/architecture.md` (scheduling rules)
- Test: `tests/Trainer.Domain.Tests/Sm2ReviewSchedulerTests.cs`, `StreakCalculatorTests.cs`, `UlidTextTests.cs`

**Interfaces:**
- Produces:
  - `static class UlidText { bool IsValid(string? value); string Create(DateTimeOffset time, ReadOnlySpan<byte> randomness); }` — `IsValid` is a 26-character loop (first char `0`-`7`, then Crockford base32 `0-9A-HJKMNP-TV-Z`), no regex, so the WASM client does not pull in the regex engine through ids; `Create` encodes 48-bit Unix milliseconds + exactly 10 random bytes (`ArgumentException` for another length or a time before 1970).
  - `sealed class UlidGenerator(TimeProvider time) { string Next(); }` — `RandomNumberGenerator.Fill` of 10 bytes + `UlidText.Create(time.GetUtcNow(), ...)`; used by the server (user ids) and the client (answer and mock ids).
  - `readonly record struct ClientAnswerId(string Value)`, `readonly record struct MockSessionId(string Value)`, `readonly record struct UserId(string Value)`, each with `static bool TryCreate(string?, out T)` through `UlidText.IsValid`.
  - `static class Roles { const string Admin = "Admin"; const string Editor = "Editor"; IReadOnlyList<string> All { get; } }`.
  - `sealed record ReviewState(DateTimeOffset DueAt, int IntervalDays, decimal Ease, int Repetitions, int Lapses, Grade LastGrade, DateTimeOffset LastAnsweredAt)`.
  - `interface IReviewScheduler { ReviewState Apply(ReviewState? current, Grade grade, DateTimeOffset answeredAt); }`; `sealed class Sm2ReviewScheduler : IReviewScheduler`.
  - `static class Sm2Constants { const decimal InitialEase = 2.3m; const decimal MinEase = 1.3m; const decimal PartialEasePenalty = 0.15m; const decimal LapseEasePenalty = 0.20m; const int MaxIntervalDays = 365; }`.
  - `sealed class StreakAccumulator(DateOnly today) { bool Add(DateOnly day); int Days { get; } }` — fed distinct days newest first; returns false (stop reading) at the first gap or when the newest day is older than yesterday; duplicates ignored. `static class StreakCalculator { int Compute(IEnumerable<DateOnly> answerDaysDescending, DateOnly today); }` is a thin loop over it; the infrastructure feeds the accumulator from `await foreach`, so any streak length is counted without a window.
- `ReviewState` holds two groups: scheduling fields (`DueAt`, `IntervalDays`, `Ease`, `Repetitions`, `Lapses`) and last-answer metadata (`LastGrade`, `LastAnsweredAt`).
- Scheduling rules (Planner's call on constants: `knew` keeps the ease, so intervals run 1, 3, 7, 16, 37, 85, 196, 365 days, the spec's "roughly 1, 3, 7, 16, 35"):
  1. `current is not null && answeredAt <= current.LastAnsweredAt` ⇒ return `current` (stale answer). Equal timestamps: the answer applied first wins; the recorder applies a batch in (`AnsweredAt`, `ClientAnswerId`) order.
  2. Normal step from `current` (or from `(_, 0, InitialEase, 0, 0, _, _)` when null):
     - `Knew`: `Repetitions == 0` ⇒ 1 day; `== 1` ⇒ 3 days; else `max(Interval + 1, round(Interval × Ease, AwayFromZero))`; `Repetitions + 1`; ease unchanged.
     - `Partial`: interval `max(1, round(Interval × 0.5, AwayFromZero))`; ease `max(MinEase, Ease − 0.15)`; repetitions unchanged.
     - `DidntKnow`: interval 1; repetitions 0; `Lapses + 1`; ease `max(MinEase, Ease − 0.20)`.
     - Interval capped at `MaxIntervalDays`; `DueAt = answeredAt + interval days`; `LastGrade = grade`; `LastAnsweredAt = answeredAt`.
  3. Not yet due (`current is not null && answeredAt < current.DueAt`): `Knew` ⇒ scheduling fields unchanged, metadata advanced (`LastGrade = Knew`, `LastAnsweredAt = answeredAt`), so a delayed older failure becomes stale and "known" status is current; `Partial`/`DidntKnow` ⇒ the normal step with `DueAt = min(current.DueAt, normal.DueAt)` (interval, ease, repetitions and lapses always come from the normal step, so the reset is never lost; the due date can only move earlier).

- [ ] **Step 1: Write failing tests**

```csharp
private static readonly DateTimeOffset T0 = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
[Fact] public void FirstAnswer_CreatesState() { ReviewState s = S.Apply(null, Grade.Knew, T0); Assert.Equal((1, 1, T0.AddDays(1)), (s.IntervalDays, s.Repetitions, s.DueAt)); }
[Fact] public void KnewSequence_Is_1_3_7_16_37() => Assert.Equal([1, 3, 7, 16, 37], Intervals(Grade.Knew, Grade.Knew, Grade.Knew, Grade.Knew, Grade.Knew)); // each answer at the previous DueAt
[Fact] public void DidntKnow_ResetsToOneDay_AndCountsLapse() { ReviewState s = S.Apply(Mature(interval: 16), Grade.DidntKnow, Due); Assert.Equal((1, 0, 1), (s.IntervalDays, s.Repetitions, s.Lapses)); }
[Fact] public void Partial_HalvesInterval_AndLowersEase() { ReviewState s = S.Apply(Mature(interval: 16, ease: 2.3m), Grade.Partial, Due); Assert.Equal((8, 2.15m), (s.IntervalDays, s.Ease)); }
[Fact] public void Ease_NeverBelowMinimum() => Assert.Equal(1.3m, S.Apply(Mature(ease: 1.35m), Grade.DidntKnow, Due).Ease);
[Fact] public void Interval_CappedAt365() => Assert.Equal(365, S.Apply(Mature(interval: 300, reps: 6), Grade.Knew, Due).IntervalDays);
[Fact] public void Apply_KnewBeforeDue_KeepsScheduleAdvancesMetadata()
{
    ReviewState c = Mature(interval: 7, lastGrade: Grade.DidntKnow);
    DateTimeOffset at = c.DueAt.AddDays(-3);
    ReviewState s = S.Apply(c, Grade.Knew, at);
    Assert.Equal((c.DueAt, c.IntervalDays, c.Ease, c.Repetitions, c.Lapses), (s.DueAt, s.IntervalDays, s.Ease, s.Repetitions, s.Lapses));
    Assert.Equal((Grade.Knew, at), (s.LastGrade, s.LastAnsweredAt));
}
[Fact] public void Apply_EarlyKnewThenDelayedFailure_FailureIsStale() { ReviewState c = Mature(interval: 7); DateTimeOffset t2 = c.DueAt.AddDays(-2), t1 = t2.AddHours(-1); ReviewState k = S.Apply(c, Grade.Knew, t2); Assert.Same(k, S.Apply(k, Grade.DidntKnow, t1)); }
[Fact] public void Apply_EqualTimestamp_IsStale() { ReviewState c = Mature(); Assert.Same(c, S.Apply(c, Grade.DidntKnow, c.LastAnsweredAt)); }
[Fact] public void Apply_DidntKnowJustBeforeDue_ResetsIntervalAndCountsLapse()
{
    ReviewState c = Mature(interval: 16, reps: 4, lapses: 0, ease: 2.3m);
    ReviewState s = S.Apply(c, Grade.DidntKnow, c.DueAt.AddHours(-12));
    Assert.Equal((1, 0, 1, 2.1m), (s.IntervalDays, s.Repetitions, s.Lapses, s.Ease));
    Assert.Equal(c.DueAt, s.DueAt);                                   // normal due (+24 h) is later; the earlier date stays
    Assert.Equal(1, S.Apply(s, Grade.Knew, s.DueAt).IntervalDays);   // the follow-up knew restarts at 1 day, not 37
}
[Fact] public void Apply_PartialBeforeDue_WhoseResultIsLater_KeepsDueButLowersEase() { ReviewState c = S.Apply(null, Grade.Knew, T0); ReviewState s = S.Apply(c, Grade.Partial, T0.AddHours(12)); Assert.Equal((T0.AddDays(1), 1, 2.15m, 1, 0), (s.DueAt, s.IntervalDays, s.Ease, s.Repetitions, s.Lapses)); }
[Fact] public void Apply_DidntKnowLongBeforeDue_MovesDueEarlier() { ReviewState c = Mature(interval: 16); ReviewState s = S.Apply(c, Grade.DidntKnow, c.DueAt.AddDays(-10)); Assert.Equal(c.DueAt.AddDays(-9), s.DueAt); }
[Fact] public void Apply_StaleAnswer_ReturnsCurrentUnchanged() { ReviewState c = Mature(); Assert.Same(c, S.Apply(c, Grade.DidntKnow, c.LastAnsweredAt.AddMinutes(-1))); }
[Theory] [InlineData("2026-11-12", 3)] [InlineData("2026-11-13", 3)] [InlineData("2026-11-14", 0)]
public void Streak_EndsTodayOrYesterday(string today, int expected) => Assert.Equal(expected, StreakCalculator.Compute([D("2026-11-12"), D("2026-11-11"), D("2026-11-10")], D(today)));
[Fact] public void Streak_GapBreaksIt() => Assert.Equal(1, StreakCalculator.Compute([D("2026-11-12"), D("2026-11-09")], D("2026-11-12")));
[Fact] public void Streak_LongerThan400Days_IsCounted() => Assert.Equal(500, StreakCalculator.Compute(Days(D("2026-11-12"), count: 500), D("2026-11-12")));
[Fact] public void Streak_StopsReadingAtFirstGap() { CountingEnumerable<DateOnly> days = new([D("2026-11-12"), D("2026-11-10"), D("2026-11-09")]); StreakCalculator.Compute(days, D("2026-11-12")); Assert.Equal(2, days.Read); }
[Fact] public void Accumulator_ReturnsFalseAtGap_AndIgnoresDuplicates() { StreakAccumulator a = new(D("2026-11-12")); Assert.True(a.Add(D("2026-11-12"))); Assert.True(a.Add(D("2026-11-12"))); Assert.True(a.Add(D("2026-11-11"))); Assert.False(a.Add(D("2026-11-09"))); Assert.Equal(2, a.Days); }
[Fact] public void Ulid_KnownVector() => Assert.Equal("01ARZ3NDEK0000000000000000", UlidText.Create(DateTimeOffset.FromUnixTimeMilliseconds(1469922850259), new byte[10])); // time part from the ULID specification
[Fact] public void Ulid_AllOnesRandomness() => Assert.EndsWith("ZZZZZZZZZZZZZZZZ", UlidText.Create(T0, Enumerable.Repeat((byte)0xFF, 10).ToArray()));
[Fact] public void Ulid_Create_RejectsWrongRandomLength() => Assert.Throws<ArgumentException>(() => UlidText.Create(T0, new byte[9]));
[Theory] [InlineData("01JD4X7Q8N9V2K3M4P5R6S7T8W", true)] [InlineData("01JD4X7Q8N9V2K3M4P5R6S7T8I", false)] [InlineData("81JD4X7Q8N9V2K3M4P5R6S7T8W", false)] [InlineData("01jd4x7q8n9v2k3m4p5r6s7t8w", false)] [InlineData("01JD4X7Q8N9V2K3M4P5R6S7T8W\n", false)]
public void Ulid_IsValid(string value, bool ok) => Assert.Equal(ok, UlidText.IsValid(value));
[Fact] public void Generator_UsesClockAndRandomness() { UlidGenerator g = new(new FakeTimeProvider(T0)); string a = g.Next(), b = g.Next(); Assert.NotEqual(a, b); Assert.Equal(a[..10], b[..10]); }
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement; existing `KeysTests.QuestionId_Validates` keeps passing. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(domain): add SM-2 scheduler, streak and ULID ids`.

### Task 3: User accounts

**Files:**
- Create: `src/Trainer.Application/Users/*`; `src/Trainer.Infrastructure/Persistence/Entities/UserEntity.cs`, `UserRoleEntity.cs`, `Configurations/UserConfiguration.cs`, `UserRoleConfiguration.cs`; migration `UsersAndAccess`; `src/Trainer.Infrastructure/Users/UserAccountService.cs`
- Modify: `TrainerDbContext.cs` (DbSets), `DependencyInjection.cs` (`IUserAccountService` scoped, `UlidGenerator` singleton), `docs/architecture.md` (data model rows)
- Test: `tests/Trainer.Infrastructure.Tests/UserAccountServiceTests.cs`

**Interfaces:**
- Produces:
  - Tables: `users(id char(26) pk, telegram_user_id bigint not null unique, display_name text null, username text null, created_at timestamptz, last_sign_in_at timestamptz, is_blocked bool not null default false, security_stamp char(32) not null, preferred_culture text null, utc_offset_minutes smallint not null default 0)`; `user_roles(user_id char(26) fk → users on delete cascade, role text, pk(user_id, role))`.
  - `sealed class AccessOptions { IReadOnlyList<long> AdminTelegramIds { get; set; } = []; }` (Application), bound once at startup (Task 5).
  - `record TelegramProfile(long TelegramUserId, string? Username, string? DisplayName)`.
  - `record UserAccess(UserId UserId, long TelegramUserId, string SecurityStamp, bool IsBlocked, IReadOnlyList<string> Roles, string? DisplayName, string? Username, CultureKey? PreferredCulture)` — `Roles` = rows of `user_roles` ∪ `{Admin}` when the id is in `AccessOptions.AdminTelegramIds`, ordinal-sorted, distinct.
  - `enum BlockResult { Updated, NotFound, ProtectedAdmin }`.
  - `UserAccountService(TrainerDbContext db, IOptions<AccessOptions> access, UlidGenerator ids, TimeProvider time)`; `interface IUserAccountService`:
    - `Task<UserAccess> SignInAsync(TelegramProfile profile, CancellationToken ct)` — upsert by `telegram_user_id` with interpolated `FromSql` (`INSERT ... ON CONFLICT (telegram_user_id) DO UPDATE ... RETURNING *`), so parallel first sign-ins cannot race into a unique violation; new rows get `UlidGenerator.Next()` and a random 32-hex stamp; refreshes names and `last_sign_in_at`.
    - `Task<UserAccess?> GetAccessAsync(long telegramUserId, CancellationToken ct)` — one query (user + roles); null when unknown.
    - `Task<BlockResult> SetBlockedAsync(UserId userId, bool blocked, CancellationToken ct)` — rotates the stamp; `ProtectedAdmin` for configured admin ids (Planner's call: prevents lock-out).
    - `Task SetRolesAsync(UserId userId, IReadOnlyCollection<string> roles, CancellationToken ct)` — replaces rows, rotates the stamp; unknown names throw `ArgumentException`.
    - `Task SetPreferredCultureAsync(UserId userId, CultureKey culture, CancellationToken ct)`.
  - Planner's call: the configured Admin role is computed, never stored, so removing an id from `App:AdminTelegramIds` and restarting revokes Admin at the next revalidation without a data change.

- [ ] **Step 1: Write failing tests** (own database per test; `FakeTimeProvider`)
  - `SignIn_NewUser_CreatesRowWithUlidAndStamp` (`UlidText.IsValid(id)`, 32 hex chars, `created_at == last_sign_in_at == now`).
  - `SignIn_ExistingUser_UpdatesProfileKeepsStampAndId`.
  - `SignIn_ConcurrentFirstSignIns_CreateOneUser` (two services, `Barrier`, same Telegram id ⇒ one row, same `UserId`).
  - `SignIn_ConfiguredAdmin_GetsAdminRoleWithoutRow`; `SignIn_BlockedUser_ReturnsBlockedAccess`.
  - `SetBlocked_RotatesStamp`; `SetBlocked_ConfiguredAdmin_IsProtected`; `SetBlocked_UnknownUser_NotFound`.
  - `SetRoles_ReplacesRowsAndRotatesStamp`; `SetRoles_UnknownRole_Throws`.
  - `GetAccess_UnknownTelegramId_ReturnsNull`; `SetPreferredCulture_IsReturnedByGetAccess`.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement entities, configurations, `dotnet ef migrations add UsersAndAccess --project src/Trainer.Infrastructure`, service, doc rows. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(infrastructure): add user accounts, roles and security stamps`.

### Task 4: Sign-in test doubles

**Files:**
- Create: `tests/Trainer.TestSupport/FakeTelegramOidcServer.cs`, `FakeTelegramIdentity.cs`, `TelegramSignInDriver.cs`, `SessionCookieReader.cs`, `CapturingLoggerProvider.cs`; `tests/Trainer.Web.Tests/TestDoubleTests.cs`
- Modify: `tests/Trainer.TestSupport/TrainerWebFactory.cs` (`TrainerWebFactoryOptions.Telegram`, `TimeProvider? Time`, `CapturingLoggerProvider? Logs`; clients default to `https://localhost`), `Trainer.TestSupport.csproj` (`Microsoft.IdentityModel.JsonWebTokens`)

**Interfaces:**
- Produces:
  - `sealed class FakeTelegramOidcServer : IAsyncDisposable` — `static Task<FakeTelegramOidcServer> StartAsync(string clientId)`; Kestrel on `127.0.0.1:0` serving `/.well-known/openid-configuration` (issuer = its origin, `authorization_endpoint`, `token_endpoint`, `jwks_uri`, `id_token_signing_alg_values_supported: ["RS256"]`), `/jwks`, `/auth` (stores `nonce` and `redirect_uri` per one-time code, answers 302 to `redirect_uri?code=...&state=...`; with `FailNext` it answers `?error=access_denied&state=...`; with `FailNextWith(string description)` it answers `?error=server_error&error_description=...&state=...`), `/token` (RS256 `id_token` with `iss`, `aud`, `sub`, `id`, `name`, `preferred_username`, `nonce`, `iat`, `exp`, signed with `JsonWebTokenHandler`). Token times use the real clock because the OIDC handler validates lifetimes with the real clock; tests that use `FakeTimeProvider` start it at the real now. `FakeTelegramIdentity NextIdentity { get; set; }` (`record FakeTelegramIdentity(long Id, string? Name, string? Username)`); `Uri MetadataAddress`; `bool FailNext`.
  - `TrainerWebFactoryOptions.Telegram`: sets `TelegramOidc:ClientId = "1000"`, a generated `TelegramOidc:ClientSecret`, and `TelegramOidc:MetadataAddress` = the server's address (honoured only in `Testing`, Task 5).
  - `static class TelegramSignInDriver { Task<HttpResponseMessage> SignInAsync(HttpClient app, FakeTelegramOidcServer telegram, FakeTelegramIdentity who, string returnUrl); }` — challenge → fake `/auth` on its own `HttpClient` → callback on the app client (whose cookie container keeps correlation, nonce and state cookies); returns the final response.
  - `static class SessionCookieReader { ClaimsPrincipal? Read(TrainerWebFactory factory, HttpResponseMessage response); AuthenticationProperties? ReadProperties(TrainerWebFactory factory, HttpResponseMessage response); }` — unprotects `.dit.session` from `Set-Cookie` with `IOptionsMonitor<CookieAuthenticationOptions>.Get("TelegramCookie").TicketDataFormat`, so tests assert on issued claims without adding endpoints to the app.
  - `sealed class CapturingLoggerProvider : ILoggerProvider` — records category, level, formatted message, structured state values, scopes and exception text.

- [ ] **Step 1: Write failing tests** (`TestDoubleTests`, exercising the doubles with a bare `JsonWebTokenHandler` and an in-test minimal host, not the app)
  - `FakeServer_Discovery_ListsEndpointsAndIssuer`; `FakeServer_IdToken_ValidatesAgainstJwksAndCarriesNonce`; `FakeServer_CodeIsSingleUse`; `FakeServer_FailNext_RedirectsWithAccessDenied`.
  - `CapturingLogger_RecordsStateValuesScopesAndExceptions`.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `test: add a fake Telegram OIDC server and sign-in test helpers`.

### Task 5: Telegram sign-in

**Files:**
- Create: `src/Trainer.Web/Auth/TelegramAuth.cs`, `TrainerClaimTypes.cs`, `AdminTelegramIds.cs`, `TelegramOidcStartupCheck.cs`, `UserClaimsFactory.cs`, `TrainerSignInGate.cs`, `TrainerCookieEvents.cs` (sign-in part), `SignInLogSink.cs`, `AuthServiceCollectionExtensions.cs`, `SignInRedirectEndpoint.cs`, `CurrentUser.cs`; `Components/Pages/SignIn.razor`; `Components/Layout/AccountControls.razor`; `Properties/launchSettings.json`; `docs/adr/0002-session-cookie-name.md`, `docs/adr/0003-logout-landing.md`
- Modify: `Program.cs`, `MainLayout.razor`, `Trainer.Web.csproj` (`UserSecretsId`), resx (RU + EN), `site.css`, `docs/architecture.md` (sign-in flow, configuration rows), `README.md` ("Sign-in in development"), `AGENTS.md` (security section)
- Test: `tests/Trainer.Web.Tests/SignInTests.cs`, `SignInRedirectTests.cs`, `SignInPageTests.cs`, `AdminTelegramIdsTests.cs`, `SignInLoggingTests.cs`

**Interfaces:**
- Consumes: `IUserAccountService`, `AccessOptions` (Task 3); test doubles (Task 4); `RootRedirectEndpoint.ResolveCulture`, `RootRedirectEndpoint.CookieName`, `LanguageEndpoint.ResolveReturnPath`, `CulturePath.FromPath`, `PageLinks` (Plan 1).
- Produces:
  - `static class TelegramAuth { const string CookieScheme = "TelegramCookie"; const string OidcScheme = "TelegramOidc"; const string CallbackPath = "/signin-telegram"; const string LoginPath = "/signin"; const string AccessDeniedPath = "/signin?denied=1"; const int CookieTtlHours = 720; static readonly TimeSpan RevalidationInterval = TimeSpan.FromMinutes(5); }`.
  - `static class TrainerClaimTypes { const string TelegramUserId = "telegram_user_id"; const string UserId = "dit:uid"; const string SecurityStamp = "dit:stamp"; const string DisplayName = "name"; const string Username = "preferred_username"; const string Subject = "sub"; }`; roles use `ClaimTypes.Role`.
  - `static class AdminTelegramIds { IReadOnlyList<long> Parse(IConfiguration configuration); }` — `App:AdminTelegramIds` as an array section (`App__AdminTelegramIds__0`) or one comma-separated value (`App__AdminTelegramIds=1,2`, the form Plan 3 renders); a non-positive or non-numeric entry throws `InvalidOperationException` naming the key and value. Read once at startup into `AccessOptions` (`services.AddSingleton(Options.Create(...))`); a change needs a restart, which every deploy does (documented in `architecture.md`).
  - `static class TelegramOidcStartupCheck { void Validate(IConfiguration configuration, IHostEnvironment environment); }` — `Production` without `TelegramOidc:ClientId` or `ClientSecret` throws (Planner's call: fail fast); `TelegramOidc:MetadataAddress` outside `Testing` throws (it exists only for the fake provider).
  - `sealed class UserClaimsFactory { IReadOnlyList<Claim> Create(UserAccess access); }` — `dit:uid`, `dit:stamp`, one `ClaimTypes.Role` per role.
  - `sealed class TrainerSignInGate(IUserAccountService users, UserClaimsFactory claims) : ITelegramSignInGate` — implements the identity overload the library calls: `SignInAsync(new TelegramProfile(id, username, displayName))`; blocked ⇒ `new TelegramSignInDecision(false)`; else `new(true, claims.Create(access))`. The id-only overload forwards with no names.
  - `sealed class TrainerCookieEvents : CookieAuthenticationEvents` (scoped; attached with `Configure<CookieAuthenticationOptions>(CookieScheme, o => o.EventsType = typeof(TrainerCookieEvents))`), sign-in part:
    - `SigningIn`: `Properties.IsPersistent = true` (the library's challenge never sets it, so without this the 30-day ticket would live in a browser-session cookie); preferred culture: a valid `lang` cookie on the request ⇒ `SetPreferredCultureAsync` when it differs; no `lang` cookie and a stored preference ⇒ append the `lang` cookie with it (same options as `LanguageEndpoint`). Planner's call: spec 8.3's redirect order stays cookie → `Accept-Language` → `en`; the stored preference only restores the explicit choice on a new device, and it is written only during the authenticated sign-in callback, never from a GET.
    - `RedirectToAccessDenied` (non-`/api`): redirects to exactly `/signin?denied=1&ReturnUrl={escaped original path and query}` (the default turns the `?` inside `AccessDeniedPath` into `%3F`). `/api` handling arrives in Task 6.
  - `sealed class SignInLogSink(ILogger<SignInLogSink> logger) : ITelegramSignInAuditSink` — logs `Sign-in {Outcome} for Telegram id {TelegramUserId}: {Reason}` at Information (Success) or Warning (Denied, Failure), where `Reason` is an application-owned code from `static class SignInReasons { ok, gate-denied, missing-id, access-denied, remote-failure }` mapped from the outcome and the library's known `Detail` strings (`"rejected by gate"`, `"missing id claim"`, `"access_denied"`; anything else ⇒ `remote-failure`); the raw `Detail` (which can carry a remote error message), the username, display name, picture and IP are never logged. `Program.cs` adds `builder.Logging.AddFilter("TelegramOidc", LogLevel.None)`, dropping the library's own messages (which carry username and client IP); the sink is the only sign-in log. No sign-in audit table (Planner's call: no stored personal audit data; logs follow the host's retention).
  - `static IServiceCollection AddTrainerAuth(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)`: `AddTelegramOidc(o => { ClientId, ClientSecret from config; CallbackPath, LoginPath, AccessDeniedPath, CookieTtlHours from TelegramAuth; DefaultReturnUrl = "/"; })`; in `Testing` with `TelegramOidc:MetadataAddress`: `Configure<OpenIdConnectOptions>(OidcScheme, o => { o.MetadataAddress = ...; o.RequireHttpsMetadata = false; })`; `AddScoped<ITelegramSignInGate, TrainerSignInGate>()`; `AddScoped<ITelegramSignInAuditSink, SignInLogSink>()`; `Configure<CookieAuthenticationOptions>(CookieScheme, o => o.Cookie.Name = AuthCookieNames.Session)` (registered after `AddTelegramOidc`, so it runs after the library's configuration; ADR 0002: `TelegramOidcOptions` has no cookie-name option); `AddAuthorizationBuilder().AddPolicy("Admin", p => p.RequireRole(Roles.Admin))`; `AddCascadingAuthenticationState()`.
  - Pipeline: `UseRouting` → `CultureAndStackMiddleware` → `UseRequestLocalization` → `UseAuthentication` → `UseAuthorization` → `UseOutputCache` → `UseAntiforgery` → endpoints; `app.MapTelegramOidcEndpoints()`. Output caching after authentication so `PublicPageCachePolicy` sees the user.
  - `static class SignInRedirectEndpoint { IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints); }` — `GET /signin`: return path from `returnUrl` or `ReturnUrl`; culture from that path (`CulturePath.FromPath`), else `lang` cookie / `Accept-Language`; 302 to `/{culture}/signin` keeping `denied=1` / `error=1` and the path validated by `LanguageEndpoint.ResolveReturnPath` (absolute, protocol-relative, backslash, control-character, encoded and other-culture forms become the culture home); `Cache-Control: no-store`, `X-Robots-Tag: noindex`.
  - `SignIn.razor` (`@page "/{Culture}/signin"`, `SeoHead NoIndex`, no output cache, `PageLinks.Set` with the sign-in path in both cultures keeping the return path): heading, two sentences on what is stored (Telegram id, name, username), link "Sign in with Telegram" `href="/signin-telegram/challenge?returnUrl={escaped validated path}" data-enhance-nav="false"`; `role="alert"`: `denied` ⇒ "You cannot open that page, or your account is blocked" (covers gate denial and a missing admin role), `error` ⇒ "Sign-in did not complete; try again"; signed in ⇒ "Signed in as {name}" and a link back; empty `ClientId` ⇒ "Sign-in is not configured" instead of a link to a 503.
  - `AccountControls.razor` (header): anonymous ⇒ "Sign in" link to `/{culture}/signin?returnUrl={PageLinks.PathFor(current) ?? "/" + culture}` (subscribes to `PageLinks.Changed`; cached HTML stays canonical); signed in ⇒ display name (else username, else localised "Telegram user"), link to `/{culture}/{defaultStack}/profile`, `<form method="post" action="/signin-telegram/logout"><AntiforgeryToken /><button type="submit">` "Sign out". ADR 0003: the library's logout always redirects to `LoginPath`, so a signed-out user lands on the sign-in page of their culture.
  - `static class CurrentUser { UserId? GetUserId(this ClaimsPrincipal user); long? GetTelegramUserId(this ClaimsPrincipal user); }`.
  - `launchSettings.json`: profile `https` on `https://localhost:7180`. README "Sign-in in development" (spec 11.3), numbered: create a separate dev bot in BotFather, enable OIDC, register `https://localhost:7180/signin-telegram`, `dotnet user-secrets set "TelegramOidc:ClientId" ...` and `"TelegramOidc:ClientSecret" ...` in `src/Trainer.Web`, set `App:AdminTelegramIds`, run the `https` profile; without a bot the app runs and shows "Sign-in is not configured". Whether BotFather accepts a `localhost` redirect URI is unverified; the builder or the owner records the outcome in the README.

- [ ] **Step 1: Write failing tests** (sign-in through `FakeTelegramOidcServer` + `TelegramSignInDriver`, client base `https://localhost`)
  - `SignIn_NewTelegramUser_CreatesUserAndRedirectsToReturnUrl` (final `Location` `/ru/dotnet/questions`; a `users` row).
  - `SignIn_SessionCookieIsPersistentThirtyDays` (`.dit.session` has `expires` ≈ now + 30 days, `secure`, `httponly`, `samesite=lax`; `ReadProperties(...).IsPersistent`).
  - `SignIn_IssuedPrincipal_CarriesTelegramIdUserIdStampAndNoRole`; `SignIn_ConfiguredAdmin_HasAdminRole`.
  - `SignIn_BlockedUser_RedirectsToDeniedAndSetsNoSessionCookie` (`Location` `/signin?denied=1`); `SignIn_RemoteFailure_RedirectsToError` (`FailNext`).
  - `SignIn_UnsafeReturnUrl_FallsBackToDefault` (`//evil.test` ⇒ `/`).
  - `SignIn_WithLangCookie_StoresPreferredCulture`; `SignIn_NewDeviceWithoutLangCookie_SetsLangFromPreference`; `Root_SignedIn_KeepsSpecRedirectOrder`.
  - `Challenge_Unconfigured_Returns503`.
  - `SignInRedirect_PreservesDeniedAndUsesReturnPathCulture` (`/signin?denied=1&ReturnUrl=%2Fru%2Fadmin` ⇒ `/ru/signin?denied=1&returnUrl=%2Fru%2Fadmin`); `SignInRedirect_WithoutReturnUrl_UsesLangCookie`.
  - `[Theory] SignInRedirect_UnsafeReturnPaths_BecomeCultureHome` and `[Theory] SignInPage_UnsafeReturnPaths_BecomeCultureHome` over `https://evil.test/x`, `//evil.test`, `/\evil.test`, `%2F%2Fevil.test`, `/en/x` from `/ru`, `/ru%0d%0aX`.
  - `SignInPage_Anonymous_HasChallengeLinkNoFormNoCookie` (no `<form`, no `Set-Cookie`, `data-enhance-nav="false"`); `SignInPage_LanguageSwitchKeepsReturnUrl`.
  - `Layout_Anonymous_ShowsSignInLinkWithCanonicalReturnUrl` (`/ru/dotnet/questions?level=MIDDLE&utm_source=x` ⇒ `returnUrl=%2Fru%2Fdotnet%2Fquestions%3Flevel%3Dmiddle`).
  - `Layout_SignedIn_ShowsNameAndLogoutFormWithToken`; `Logout_WithToken_ClearsSessionAndLandsOnSignIn`; `Logout_WithoutToken_Returns400`.
  - `SignInLogs_ContainNoUsernameIpCookieOrToken` (`CapturingLoggerProvider` over success, denial and failure: no message, state value, scope or exception text contains the username, the display name, `127.0.0.1`, the `.dit.session` value or the antiforgery token; one `SignInLogSink` entry per outcome with the expected reason code).
  - `SignInLogs_RemoteErrorSentinels_NeverLogged` (the fake provider fails with `error_description` = `sentinel-user@x 203.0.113.7 eyJsentinel`; no captured text contains any sentinel; the entry's reason is `remote-failure`).
  - `AdminIds_ParseArrayAndCommaForms`; `AdminIds_InvalidEntry_ThrowsWithKey`; `StartupCheck_ProductionWithoutSecret_Throws`; `StartupCheck_MetadataAddressOutsideTesting_Throws`.
  - `Appsettings_ContainNoClientSecret`.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement; ADRs 0002, 0003; docs. **Step 4:** Run → PASS; Plan 1 web tests still PASS.
- [ ] **Step 5:** Commit `feat(web): sign in with Telegram through a database-backed gate`.

### Task 6: Session revalidation, key storage and BREACH guard

**Files:**
- Create: `src/Trainer.Web/Security/BreachGuardMiddleware.cs`, `Security/PrivateResponseMiddleware.cs`; migration `DataProtectionKeys`; `docs/adr/0004-data-protection-keys-in-plan-2.md`
- Modify: `TrainerCookieEvents.cs` (validation part), `TrainerDbContext.cs` (`IDataProtectionKeyContext`), `AuthServiceCollectionExtensions.cs`, `Program.cs`, `Trainer.Infrastructure.csproj`, `docs/architecture.md` (session lifetime, key storage, compression rule), `AGENTS.md` (security section)
- Test: `tests/Trainer.Web.Tests/SessionLifetimeTests.cs`, `BreachGuardTests.cs`, `DataProtectionTests.cs`, `SignedInCacheTests.cs`

**Interfaces:**
- Consumes: `IUserAccountService.GetAccessAsync`, `UserClaimsFactory`, `TrainerCookieEvents` (Tasks 3, 5).
- Produces:
  - `TrainerCookieEvents(IUserAccountService users, UserClaimsFactory claims, TimeProvider time)`:
    - `ValidatePrincipal`: last check = `Properties.Items[".dit.checked"]` (round-trip `"O"`), else `IssuedUtc`. `now − last < RevalidationInterval` ⇒ return, no database access. Else `GetAccessAsync(telegram_user_id)`: null or blocked ⇒ `RejectPrincipal()` + `SignOutAsync(CookieScheme)`; otherwise, when the stamp or the role set differs, build a new `ClaimsIdentity` with the same `AuthenticationType`, `NameClaimType` and `RoleClaimType`, keeping every original claim (including `sub`, which antiforgery binds tokens to) except `dit:uid`, `dit:stamp` and roles, plus `claims.Create(access)`; `ReplacePrincipal`; then `.dit.checked = now`, `ShouldRenew = true`.
    - `RedirectToLogin` / `RedirectToAccessDenied` for `/api`: 401 / 403 with no `Location` (Task 11 writes the JSON body).
  - `TrainerDbContext : IDataProtectionKeyContext` (`public DbSet<DataProtectionKey> DataProtectionKeys`); `AddDataProtection().SetApplicationName("dev-interview-trainer").PersistKeysToDbContext<TrainerDbContext>()`. ADR 0004: spec 8.7 puts key persistence with hosting (Plan 3), but Plan 2 issues auth and antiforgery cookies, so the store is built now; keys are unencrypted in the table, accepted for a single-instance app whose database is not exposed; Plan 3 encrypts backups.
  - Both middlewares sit right after `UseAuthentication` (routing has already matched the endpoint; the compression middleware sits earlier and has installed its feature when the client accepts an encoding) and act only on HTML pages, identified by the matched endpoint carrying `ComponentTypeMetadata` (a Razor component page), never on static assets or `/api` JSON.
  - `sealed class BreachGuardMiddleware(RequestDelegate next)`: for an authenticated request to a Razor component page, or the path `/api/antiforgery`, `context.Features.Get<IHttpsCompressionFeature>()?.Mode = HttpsCompressionMode.DoNotCompress` — the feature is absent when nothing will be compressed. Planner's call: authenticated `/api` JSON stays compressed; it carries no token.
  - `sealed class PrivateResponseMiddleware(RequestDelegate next)`: authenticated requests to a Razor component page get `Cache-Control: private, no-store` in `OnStarting` (Back after logout on a shared browser shows no personal page); static assets keep their immutable caching.

- [ ] **Step 1: Write failing tests** (factory with `FakeTimeProvider` started at the real now)
  - `SessionCookie_SlidingRenewal` (after +20 days a request renews the cookie with a fresh 30-day expiry).
  - `BlockedUser_IsSignedOutAfterRevalidationInterval` (+4 min still signed in; +6 min the "Sign in" link shows and `.dit.session` is deleted).
  - `RoleChange_RefreshedAfterInterval` (`SetRolesAsync(["Admin"])`: no `Admin` in a cookie renewed at +4 min, `Admin` in the one renewed at +6 min, via `SessionCookieReader`).
  - `RoleRefresh_KeepsSubAndAntiforgeryTokenStillValid` (a token taken before the refresh still validates; the logout POST with it succeeds).
  - `WithinInterval_NoDatabaseCheck` (counting decorator of `GetAccessAsync`: 0 calls for 10 requests within 5 min, 1 after +6 min).
  - `ConfiguredAdminIds_ReadAtStartup` (a factory with `App:AdminTelegramIds = 77` grants Admin to 77; a new factory over the same database without it does not, after the interval).
  - `[Theory] SignedInHtml_IsNotCompressed(acceptEncoding: "br" | "identity" | null)` — 200, no `Content-Encoding`; `AnonymousHtml_IsCompressedWithBr`.
  - `SignedInHtml_IsPrivateNoStore`; `AnonymousHtml_HasNoPrivateNoStore`; `SignedIn_StaticAssets_KeepImmutableCacheControl` (a fingerprinted CSS and JS asset requested with the session cookie keep `Cache-Control` with `immutable` and no `no-store`).
  - `SignedInCacheSequence_NeverLeaks`: anonymous `/ru/dotnet/questions` warmed (second call is a hit), then user A, then user B, then anonymous: each signed-in response shows only its own display name and has no `Age` (no lookup); the store's entry count is unchanged (no storage); the final anonymous body equals the first and contains neither name.
  - `ExpiredSessionCookie_IsTreatedAsAnonymousAndNotStored`.
  - `DataProtectionKeys_ArePersistedInDatabase` (rows exist after sign-in; a second factory on the same database reads the first one's cookie as signed in).
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement; `dotnet ef migrations add DataProtectionKeys`; ADR 0004; docs. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(web): revalidate sessions, persist keys and keep tokens out of compressed responses`.

### Task 7: Answer recording

**Files:**
- Create: `src/Trainer.Application/Answers/*`, `Mock/*`; `src/Trainer.Infrastructure/Persistence/Entities/AnswerEntity.cs`, `ReviewStateEntity.cs`, `MockSessionEntity.cs`, configurations; migration `LearningProgress`; `src/Trainer.Infrastructure/Answers/AnswerRecorder.cs`, `Mock/MockSessionService.cs`; `tests/Trainer.Application.Tests/` (project, `AnswerTimePolicyTests.cs`); `docs/adr/0005-answer-timestamps.md`
- Modify: `DependencyInjection.cs` (`IReviewScheduler → Sm2ReviewScheduler` singleton, recorder, mock service), `DevInterviewTrainer.slnx`, `docs/architecture.md` (answers flow, data model)
- Test: `tests/Trainer.Infrastructure.Tests/AnswerRecorderTests.cs`, `MockSessionServiceTests.cs`

**Interfaces:**
- Consumes: `Grade`, `AnswerMode`, ids, `IReviewScheduler`, `ReviewState` (Tasks 1-2).
- Produces:
  - Tables: `answers(id char(26) pk (server ULID), client_answer_id char(26) not null unique, user_id char(26) fk → users cascade, question_id char(26) fk → questions, grade smallint, used_hint bool, mode smallint, mock_session_id char(26) null, answered_at timestamptz, received_at timestamptz; index (user_id, answered_at))`; `review_states(user_id char(26), question_id char(26); pk both; due_at timestamptz, interval_days int, ease numeric(4,2), repetitions int, lapses int, last_grade smallint, last_answered_at timestamptz; xmin concurrency token; index (user_id, due_at))`; `mock_sessions(id char(26) pk, user_id char(26) fk cascade, stack_key text, level smallint, question_count int, timed bool, started_at timestamptz, finished_at timestamptz, summary jsonb)`. Planner's calls: no foreign key from `answers.mock_session_id` (answers usually arrive before the summary; anonymous mocks never send one); `xmin` is the spec 8.6 "version" (native, nothing to maintain).
  - `enum TimeVerdict { Accept, Clamp, TooOld, InFuture }`; `static class AnswerTimePolicy { static readonly TimeSpan MaxAge = TimeSpan.FromDays(365); static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5); TimeVerdict Judge(DateTimeOffset answeredAt, DateTimeOffset serverNow); DateTimeOffset Effective(DateTimeOffset answeredAt, DateTimeOffset serverNow); }` — `answeredAt < serverNow − MaxAge` ⇒ `TooOld`; `answeredAt > serverNow + FutureTolerance` ⇒ `InFuture`; `serverNow < answeredAt ≤ serverNow + FutureTolerance` ⇒ `Clamp` (`Effective` = `serverNow`); else `Accept`. Only the server's own clock is shifted, then compared, so `DateTimeOffset.MinValue`/`MaxValue` cannot overflow. ADR 0005 (needs owner approval, see the deviations section): spec 8.8 says "`answeredAt` not in the future and not older than 1 year"; the client already corrects its clock at answer time (Task 14), so the server rejects as specified except for a 5-minute tolerance, which it clamps so the stored value is never in the future. `answeredAt` stays client-controlled (a user can backdate within a year, accepted like the trusted grade).
  - `record ReportedAnswer(ClientAnswerId ClientAnswerId, QuestionId QuestionId, Grade Grade, bool UsedHint, AnswerMode Mode, MockSessionId? MockSessionId, DateTimeOffset AnsweredAt)`.
  - `static class AnswerRejections { const string QuestionUnknown = "question-unknown"; const string QuestionRetired = "question-retired"; const string AnsweredTooOld = "answered-too-old"; const string AnsweredInFuture = "answered-in-future"; const string Invalid = "invalid"; }` (`Invalid` comes from the endpoint parser, Task 11).
  - `record RejectedAnswer(string ClientAnswerId, string Reason)`; `record AnswerBatchResult(IReadOnlyList<string> Accepted, IReadOnlyList<RejectedAnswer> Rejected)`.
  - `interface IAnswerRecorder { Task<AnswerBatchResult> RecordAsync(UserId userId, IReadOnlyList<ReportedAnswer> answers, short? clientUtcOffsetMinutes, CancellationToken ct); }` with `const int MaxBatchSize = 500`.
  - Algorithm: (1) collapse duplicate ids inside the batch (first wins); (2) transaction; `pg_advisory_xact_lock(hashtextextended('dit-answers:' || {userId}, 0))` (a hash collision only serialises two users); (3) ids already in `answers` ⇒ accepted with no other effect, whoever owns them and whatever the payload (Planner's call: first write wins; a differing payload is logged at Warning with the id only; reporting another user's id as accepted reveals nothing, refusing would let a client probe ids); (4) only for new ids: `AnswerTimePolicy.Judge` ⇒ `answered-too-old` / `answered-in-future`, then one query for the question ids ⇒ `question-unknown` / `question-retired`; (5) when `clientUtcOffsetMinutes` is present (−840..840) store it in `users.utc_offset_minutes`; (6) insert rows (`id = UlidGenerator.Next()`, `answered_at = AnswerTimePolicy.Effective(...)`, `received_at = now`); (7) load tracked `review_states`, apply `IReviewScheduler.Apply` per new answer ordered by (`answered_at`, `ClientAnswerId`), upsert; (8) save, commit. On `DbUpdateConcurrencyException` or `23505`: roll back, clear the tracker, retry the whole batch up to 3 times, then rethrow. Planner's call: the lock makes conflicts rare; `xmin` still guards every review-state write.
  - `record MockSummary(StackKey Stack, Level Level, int QuestionCount, bool Timed, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, IReadOnlyList<MockTopicResult> Topics)`; `record MockTopicResult(TopicKey Topic, int Knew, int Partial, int DidntKnow, int Unanswered)`; `enum MockSaveResult { Created, Updated, NotFound }`; `enum MockSaveResult { Created, Updated, NotFound, Invalid }`; `interface IMockSessionService { Task<MockSaveResult> SaveAsync(UserId userId, MockSessionId id, MockSummary summary, CancellationToken ct); }` — `StartedAt`/`FinishedAt` judged by `AnswerTimePolicy` (too old or beyond the tolerance ⇒ `Invalid`, clamped otherwise); upsert by id; another user's id ⇒ `NotFound` (Planner's call: 404 so ids cannot be probed).

- [ ] **Step 1: Write failing tests** (users via `IUserAccountService`, content from the Infrastructure fixture, `FakeTimeProvider`)
  - Application: `[Theory] Judge_Boundaries` (now − 365 d − 1 ms ⇒ TooOld, now − 365 d ⇒ Accept, now + 5 min ⇒ Clamp, now + 5 min + 1 ms ⇒ InFuture); `Judge_ExtremeDates_DoNotThrow` (`DateTimeOffset.MinValue` ⇒ TooOld, `MaxValue` ⇒ InFuture); `Effective_ClampsToNow`.
  - `RecordBatch_NewAnswers_StoresRowsAndCreatesReviewStates`.
  - `RecordBatch_RetryOfSameBatch_IsIdempotent` (same `Accepted`, row count and `repetitions` unchanged).
  - `RecordBatch_SameBatchConcurrently_StoresOnceAndSchedulesOnce` (two recorders, separate contexts, `Barrier`).
  - `RecordBatch_RetryAfterQuestionRetired_IsAccepted`; `RecordBatch_RetryAfterOneYearPassed_IsAccepted`.
  - `RecordBatch_DuplicateIdInsideBatch_StoredOnce`; `RecordBatch_SameIdDifferentPayload_FirstWinsAndLogsIdOnly`.
  - `RecordBatch_AppliesInAnsweredAtOrder`; `RecordBatch_OlderAnswerAfterNewer_DoesNotRewindSchedule`.
  - `RecordBatch_FutureWithinFiveMinutes_ClampedToNow`; `RecordBatch_FutureBeyondFiveMinutes_Rejected`; `RecordBatch_OlderThanOneYear_Rejected` (nothing stored, reported rejected); `RecordBatch_ExtremeDates_RejectedWithoutError`.
  - `RecordBatch_DeliveredAfterDue_UsesAnsweredAt` (state due at T; a `knew` answered at T − 1 min arrives at T + 2 h ⇒ treated as not yet due: schedule unchanged, metadata advanced).
  - `RecordBatch_RetryAfterClockMoved_SameStoredTime` (same batch replayed after `FakeTimeProvider` advanced 3 h: acknowledged, stored `answered_at` unchanged).
  - `RecordBatch_StoresClientOffsetOnUser`; `RecordBatch_InvalidOffsetIgnored` (±900 ⇒ not stored).
  - `RecordBatch_RetiredQuestion_Rejected`; `RecordBatch_UnknownQuestion_Rejected`; `RecordBatch_IdOwnedByAnotherUser_ReportedAcceptedAndUntouched`.
  - `RecordBatch_ConcurrencyConflict_IsRetried` (a `SaveChangesInterceptor` runs `UPDATE review_states SET lapses = lapses WHERE ...` on a separate connection before the first save; the batch succeeds, the answer applied once).
  - `MockSave_CreatesThenUpdatesSameId`; `MockSave_OtherUsersId_NotFound`; `MockSave_FinishedTooOld_Invalid`.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement; `dotnet ef migrations add LearningProgress`; ADR 0005; docs. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(infrastructure): record answers idempotently and schedule reviews`.

### Task 8: Contracts, per-line code HTML and session queries

**Files:**
- Create: `src/Trainer.Contracts/Trainer.Contracts.csproj` and the DTO files below; `src/Trainer.Application/Sessions/*`; `src/Trainer.Infrastructure/Sessions/SessionQueries.cs`; `tests/Trainer.Application.Tests/MockComposerTests.cs`, `ContractsJsonTests.cs`; `docs/adr/0006-application-references-contracts.md`
- Modify: `src/Trainer.Content/Rendering/IContentRenderer.cs`, `ContentRenderer.cs`, `LineWrapper.cs` (expose `Split`), `Schema/ContentSchema.cs` (`RenderVersion = 3`); `src/Trainer.Infrastructure/Content/PayloadJson.cs` (`CodeLinesHtml`), `QuestionRowMapper.cs`; `DevInterviewTrainer.slnx`; `Trainer.Application.csproj`; `docs/architecture.md` (layers table, payload)
- Test: `tests/Trainer.Content.Tests/ContentRendererTests.cs`, `tests/Trainer.Infrastructure.Tests/SessionQueriesTests.cs`, `ContentImportServiceTests.cs`

**Interfaces:**
- Produces (Contracts, namespace `Trainer.Contracts`, `sealed record`s, string keys only):
  - `QuestionDto(string Id, string Slug, string Topic, string TopicTitle, string Level, string Type, string Title, string PromptHtml, string? HintHtml, IReadOnlyList<string>? CodeLinesHtml, string? FixedCodeHtml, IReadOnlyList<OptionDto> Options, IReadOnlyList<string>? AcceptedOutputs, IReadOnlyList<IReadOnlyList<int>>? BugLineSets, string AnswerHtml, string ExplanationHtml, IReadOnlyList<LinkDto> Links)`; `OptionDto(string Id, string TextHtml, bool Correct, string? ExplanationHtml)`; `LinkDto(string Url, string Title, string Lang)`.
  - `PracticeSessionRequest(string Culture, string Stack, IReadOnlyList<string> Levels, IReadOnlyList<string> Topics, IReadOnlyList<string> Types, string Status, int Count, string? FirstQuestionId, IReadOnlyList<string> ExcludeQuestionIds)`; `MockSessionRequest(string Culture, string Stack, string Level, IReadOnlyList<string> Topics, int Count)`; `ReviewSessionRequest(string Culture, string Stack, int Count)`.
  - `SessionDto(IReadOnlyList<QuestionDto> Questions, int PoolSize)`; `MockSessionDto(IReadOnlyList<QuestionDto> Questions, int PoolSize, bool RepeatsLikely, int SuggestedCount)`.
  - `AnswerDto(string ClientAnswerId, string QuestionId, string Grade, bool UsedHint, string Mode, string? MockSessionId, DateTimeOffset? AnsweredAt)` (nullable on the wire so a missing value is detected and rejected as `invalid` instead of binding to `0001-01-01`; the client always sets it); `AnswerBatch(int Version, IReadOnlyList<AnswerDto> Answers)` (request body and outbox document, spec 8.8; `const int CurrentVersion = 1`); `AnswerBatchResponse(IReadOnlyList<string> Accepted, IReadOnlyList<RejectedAnswerDto> Rejected)`; `RejectedAnswerDto(string ClientAnswerId, string Reason)`.
  - `MockSummaryDto(string Stack, string Level, int QuestionCount, bool Timed, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, IReadOnlyList<MockTopicResultDto> Topics)`; `MockTopicResultDto(string Topic, int Knew, int Partial, int DidntKnow, int Unanswered)`.
  - `ProfileDto(int StreakDays, int TotalAnswered, int DueNow, DateTimeOffset? NextDueAt, IReadOnlyList<TopicProgressDto> Topics, IReadOnlyList<LevelProgressDto> Levels, IReadOnlyList<MockHistoryDto> RecentMocks)`; `TopicProgressDto(string Topic, string Title, int LiveQuestions, int Answered, int Known, int Due)`; `LevelProgressDto(string Level, int LiveQuestions, int Answered, int Known)`; `MockHistoryDto(string Id, string Level, int QuestionCount, DateTimeOffset FinishedAt, int Knew, int Partial, int DidntKnow, int Unanswered)`.
  - `AntiforgeryTokenDto(string RequestToken, string HeaderName)`; `ApiErrorDto(string Code, string? Detail)` with codes `antiforgery`, `validation`, `batch-too-large`, `rate-limited`, `sign-in-required`, `forbidden`, `not-found`, `method-not-allowed`, `owner-mismatch`, `unsupported-media-type`, `payload-too-large`, `server-error`.
  - Island bootstrap: `TopicDto(string Key, string Title)`; `MeDto(long TelegramUserId, int StreakDays, int DueNow, IReadOnlyList<TopicProgressDto> Topics)`; `PracticeFiltersDto(IReadOnlyList<string> Levels, IReadOnlyList<string> Topics, IReadOnlyList<string> Types, string Status, string? FirstQuestionId)`; `IslandBootstrapDto(string Culture, string Stack, IReadOnlyList<TopicDto> Topics, IReadOnlyList<string> Levels, IReadOnlyList<string> Types, PracticeFiltersDto InitialFilters, MeDto? Me)`.
  - `[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)] [JsonSerializable(...every type above...)] partial class TrainerJsonContext : JsonSerializerContext` (Task 13 adds its storage records).
  - ADR 0006: Application references Contracts so query services return the wire DTOs directly; a parallel Application model would be a field-for-field copy.
- Produces (Content): `IReadOnlyList<string> IContentRenderer.RenderCodeLines(string source, string language)`; `RenderCode` is rebuilt on it. Payload gains `CodeLinesHtml`; `RenderVersion = 3` re-renders everything at the next import.
- Produces (Application):
  - `enum PracticeStatus { All, New, Due }`; `record PracticeCriteria(CultureKey Culture, StackKey Stack, IReadOnlyList<Level> Levels, IReadOnlyList<TopicKey> Topics, IReadOnlyList<QuestionType> Types, PracticeStatus Status, int Count, QuestionId? First, IReadOnlyList<QuestionId> Exclude)`; `record MockCriteria(CultureKey Culture, StackKey Stack, Level Level, IReadOnlyList<TopicKey> Topics, int Count)`; `record ReviewCriteria(CultureKey Culture, StackKey Stack, int Count)`. Limits (Planner's call): Practice `Count` 1..50 (default 20), Mock 5..50 (default 20), Review 1..50 (default 20); `Exclude` ≤ 1000 ids.
  - `interface ISessionQueries { Task<SessionDto> GetPracticeAsync(PracticeCriteria criteria, UserId? user, CancellationToken ct); Task<MockSessionDto> GetMockAsync(MockCriteria criteria, CancellationToken ct); Task<SessionDto> GetReviewAsync(ReviewCriteria criteria, UserId user, CancellationToken ct); }`.
  - `sealed class MockComposer(Random random) { const double LowerLevelShare = 0.3; IReadOnlyList<QuestionId> Compose(Level level, IReadOnlyList<QuestionId> atLevel, IReadOnlyList<QuestionId> below, int count); static bool RepeatsLikely(int poolSize, int count) => poolSize < 3 * count; static int SuggestedCount(int poolSize); }` — Planner's call (level mix): `round(count × 0.3)` from the level below (none for Junior), rest at the level, a short bucket filled from the other, result shuffled. `SuggestedCount`: `clamp(⌊P/3⌋, 5, 50)` for `P ≥ 5`, `P` for `0 < P < 5` (the session uses the whole pool), `0` for an empty pool.
- Behaviour (`SessionQueries`): live questions with a locale in the culture (`LiveQuestions.Of`); Practice orders by `EF.Functions.Random()`; `New` = no state for the user (signed in) or not in `Exclude` (anonymous); `Due` = `due_at ≤ now` (signed in only); `First`, when live, in the requested stack and culture, goes first, is removed from the random part and counts toward `Count`; `PoolSize` = matches before `Count`. Mock: pools at `Level` and the level below matching `Topics` (empty = all); a pool under 5 yields the whole pool. Review: the user's states with `due_at ≤ now`, most overdue first, skipping retired questions and those without the culture's locale. Locales and payload in one query (no N+1).

- [ ] **Step 1: Write failing tests**
  - `RenderCodeLines_MatchRenderCodeLineSpans`; `Import_RenderVersion3_ReimportsAndStoresCodeLines`.
  - `Compose_Middle20_Takes6FromJunior`; `Compose_Junior_TakesNoLowerLevel`; `Compose_ShortLowerPool_FillsFromLevel`; `Compose_PoolSmallerThanCount_ReturnsAll`; `RepeatsLikely_UnderThreeTimesN`; `[Theory] SuggestedCount(0→0, 3→3, 14→5, 30→10, 400→50)`.
  - `ContractsJson_RoundTripsEveryDtoWithSourceGeneratedContext` (camelCase; the `AnswerBatch` sample equals the spec 8.8 shape).
  - `Practice_FiltersAndCount`; `Practice_ExcludesRetiredAndOtherCultureOnly`; `Practice_NewForSignedIn_SkipsAnswered`; `Practice_NewForAnonymous_UsesExclude`; `Practice_Due_ReturnsOnlyDue`; `Practice_FirstQuestionComesFirstOnceAndCounts`; `Practice_FirstFromOtherStack_Ignored`; `Practice_NoMatches_ReturnsEmptyWithPoolZero`; `Practice_QuestionDto_CarriesCorrectnessAndCodeLines`.
  - `Mock_ReturnsComposedSetAndWarnsOnSmallPool`; `Mock_PoolUnderFive_ReturnsWholePool`.
  - `Review_DueOnly_MostOverdueFirst_DropsRetired`.
  - `Practice_SessionOfFifty_UsesConstantQueryCount` (EF command counter ≤ 3 for 1 and 50 questions).
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement; ADR 0006; docs. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat: add contracts, per-line code html and session queries`.

### Task 9: Progress queries and personal fragments on public pages

**Files:**
- Create: `src/Trainer.Application/Progress/*`; `src/Trainer.Infrastructure/Progress/ProgressQueries.cs`; `src/Trainer.Web/Components/Shared/StatusChip.razor`
- Modify: `src/Trainer.Application/Catalog/CatalogPage.cs` (`CatalogRow` gains trailing `QuestionId Id`), `CatalogQueries.cs`, `TopicQueries.cs`; `QuestionRow.razor`, `Catalog.razor`, `TopicPage.razor`, `QuestionPage.razor`, `Home.razor`; resx; `tests/Trainer.Web.Tests/SignedInCacheTests.cs` (statuses join the sequence); `docs/architecture.md` (personal fragments)
- Test: `tests/Trainer.Infrastructure.Tests/ProgressQueriesTests.cs`, `tests/Trainer.Web.Tests/PersonalFragmentsTests.cs`

**Interfaces:**
- Consumes: Task 7 tables, `users.utc_offset_minutes`; `StreakCalculator`; `CurrentUser`.
- Produces:
  - `record QuestionStatus(Grade LastGrade, DateTimeOffset DueAt, bool IsDue)`.
  - `interface IProgressQueries { Task<IReadOnlyDictionary<QuestionId, QuestionStatus>> GetStatusesAsync(UserId user, IReadOnlyCollection<QuestionId> ids, CancellationToken ct); Task<int> GetDueCountAsync(UserId user, CultureKey culture, StackKey stack, CancellationToken ct); Task<ProfileDto> GetProfileAsync(UserId user, CultureKey culture, StackKey stack, CancellationToken ct); Task<MeDto> GetMeAsync(UserId user, CultureKey culture, StackKey stack, CancellationToken ct); }`.
  - Profile: per live topic and level — live questions in the culture, answered (has a state), known (`last_grade = knew`), due; `TotalAnswered`; `NextDueAt` = earliest future `due_at` among live questions; last 5 mocks. Streak: distinct local days `date(answered_at + utc_offset_minutes)` ordered newest first, read with `await foreach` over `AsAsyncEnumerable()` into a `StreakAccumulator` (Task 2); the loop breaks when `Add` returns false, so the query reads only the streak plus one row — full history, no window. Planner's call: the day boundary uses the offset of the user's last write (`X-Client-Utc-Offset`), so evening learners in any time zone keep their streak; UTC until the first write.
  - Status rendering (signed-in only, spec 8.4): `StatusChip` (`new`, `due`, `in N days`, last grade as text plus colour token, never colour alone) on catalog and topic rows and under the question title; home Review card shows the due count. Pages call `IProgressQueries` only for authenticated users; anonymous rendering is unchanged.

- [ ] **Step 1: Write failing tests**
  - `Statuses_ReturnsOnlyAnsweredWithDueFlag`; `DueCount_CountsLiveInCultureOnly`; `Profile_AggregatesByTopicAndLevel`; `Profile_NextDueAt_IsEarliestFuture`; `Profile_RecentMocks_NewestFirstMaxFive`; `Me_MatchesProfileTotals`.
  - `Streak_UsesUserOffset` (offset +180: answers at 2026-11-10T01:00+03:00 and 2026-11-11T23:30+03:00 are local days 10 and 11, a streak of 2, while their UTC days 9 and 11 would give 1); `Streak_StopsQueryAtGap` (EF command reads ≤ streak + 1 rows, checked with a row-count interceptor); `Streak_LongerThan400Days` (seeded via SQL).
  - `SignedInCatalog_ShowsStatuses_AndAnonymousNextRequestDoesNot`; `SignedInQuestionPage_ShowsStatusLine`; `SignedInHome_ShowsDueCountOnReviewCard`; Plan 1 `AnonymousPublicPages_SetNoCookies` and `CacheHit_ExecutesNoSql` still PASS.
- [ ] **Step 2:** FAIL. **Step 3:** Implement; docs. **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat(web): show personal progress on public pages for signed-in users`.

### Task 10: Admin page

**Files:**
- Create: `src/Trainer.Application/Admin/*`; `src/Trainer.Infrastructure/Admin/AdminQueries.cs`; `src/Trainer.Web/Components/Pages/Admin.razor`; `docs/adr/0007-admin-url.md`
- Modify: resx; `site.css`; `docs/architecture.md` (URL table, admin)
- Test: `tests/Trainer.Infrastructure.Tests/AdminQueriesTests.cs`, `tests/Trainer.Web.Tests/AdminPageTests.cs`

**Interfaces:**
- Consumes: `ReadinessState`, `IUserAccountService.SetBlockedAsync`, policy `Admin`.
- Produces:
  - `record ImportRow(string Hash, int SchemaVersion, int RenderVersion, DateTimeOffset ImportedAt, int QuestionCount, bool IsActive)`; `record AdminOverview(bool DatabaseReachable, IReadOnlyList<ImportRow> RecentImports, int UserCount, int BlockedCount, int AnswersLast24Hours)`; `record AdminUserRow(UserId Id, long TelegramUserId, string? DisplayName, string? Username, DateTimeOffset CreatedAt, DateTimeOffset LastSignInAt, bool IsBlocked, IReadOnlyList<string> Roles)`; `record UserPage(IReadOnlyList<AdminUserRow> Rows, int Total, int Page, int PageCount)` (50 per page).
  - `interface IAdminQueries { Task<AdminOverview> GetOverviewAsync(CancellationToken ct); Task<UserPage> GetUsersAsync(string? search, int page, CancellationToken ct); }` — search: Telegram id exact, or escaped case-insensitive substring of display name / username; last 10 imports.
  - `Admin.razor`: `@page "/{Culture}/admin"` (ADR 0007: spec 8.3 lists no admin URL; the culture prefix gives the UI language), `[Authorize(Policy = "Admin")]`, noindex, no output cache, `PageLinks.Set`. Sections: health (readiness, database reachable), imports (short hash, ISO 8601 UTC, active marker), users with a GET search form and pager, per-user block/unblock `<form method="post" @formname="block-{id}">` + `<AntiforgeryToken />` via `[SupplyParameterFromForm]`; `ProtectedAdmin` shows a localised message. Tables in `div.table-scroll`, stacked cards below 640 px; every control ≥ 44×44 px.

- [ ] **Step 1: Write failing tests**
  - `Overview_ReportsActiveImportAndCounts`; `Users_SearchByUsernameAndId`; `Users_SearchEscapesWildcards`; `Users_Pages`.
  - `AdminPage_Anonymous_RedirectsToSignIn` (`Location` = `/signin?ReturnUrl=%2Fru%2Fadmin`); `AdminPage_NonAdmin_RedirectsToDenied` (`Location` = `/signin?denied=1&ReturnUrl=%2Fru%2Fadmin` exactly); `AdminPage_Admin_ShowsHealthImportsAndUsers`.
  - `AdminPage_BlockUser_RotatesStampAndShowsBlocked`; `AdminPage_BlockWithoutToken_Returns400`; `AdminPage_BlockConfiguredAdmin_ShowsProtectedMessage`.
  - `AdminPage_IsNoindexPrivateAndNotCompressed`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement; ADR 0007; docs. **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat(web): add admin page with health, imports and user blocking`.

### Task 11: `/api` endpoints

**Files:**
- Create: `src/Trainer.Web/Api/ApiEndpoints.cs`, `AntiforgeryEndpoints.cs`, `SessionEndpoints.cs`, `AnswerEndpoints.cs`, `MockSessionEndpoints.cs`, `ProfileEndpoints.cs`, `AntiforgeryValidationFilter.cs`, `OwnerCheckFilter.cs`, `ApiErrorMiddleware.cs`, `ApiRequestParser.cs`, `ApiRateLimits.cs`, `RateLimitOptions.cs`; `docs/adr/0008-per-owner-outbox-and-owner-header.md`
- Modify: `Program.cs` (`ConfigureHttpJsonOptions` with `TrainerJsonContext.Default` first; `AddRateLimiter`; `UseRateLimiter` after `UseAuthorization`; `AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = ".dit.af"; })`; the `/api` error branch), `TrainerCookieEvents.cs` (JSON 401/403 bodies), `Trainer.Web.csproj` (reference Contracts), `TrainerWebFactory.cs` (every factory, and so `AppHostFixture`, sets `App:RateLimits:*` to 100 000 unless a test overrides them), `docs/architecture.md` (`/api` table, rate limits, errors)
- Test: `tests/Trainer.Web.Tests/ApiAntiforgeryTests.cs`, `AnswerApiTests.cs`, `SessionApiTests.cs`, `MockSessionApiTests.cs`, `ProfileApiTests.cs`, `ApiRateLimitTests.cs`, `ApiErrorTests.cs`, `ApiRequestParserTests.cs`

**Interfaces:**
- Consumes: `IAnswerRecorder`, `IMockSessionService` (Task 7), `ISessionQueries` + Contracts (Task 8), `IProgressQueries` (Task 9), `CurrentUser` (Task 5).
- Produces (under `/api`, `Cache-Control: no-store`, JSON through `TrainerJsonContext`, errors as `ApiErrorDto`, body limit 256 KB ⇒ 413 `payload-too-large`):

| Method, path | Auth | Write checks | Rate policy | Body → response |
|---|---|---|---|---|
| `GET /api/antiforgery` | any | — | — | → `AntiforgeryTokenDto` (`GetAndStoreTokens`; uncompressed) |
| `POST /api/sessions/practice` | any | — | `api-reads` | `PracticeSessionRequest` → `SessionDto`; anonymous `Status = "due"` ⇒ 401 `sign-in-required` |
| `POST /api/sessions/mock` | any | — | `api-reads` | `MockSessionRequest` → `MockSessionDto` |
| `POST /api/sessions/review` | signed in | — | `api-reads` | `ReviewSessionRequest` → `SessionDto` |
| `POST /api/answers/batch` | signed in | `X-CSRF-TOKEN`, `X-Outbox-Owner`, optional `X-Client-Utc-Offset` | `api-writes` | `AnswerBatch` → `AnswerBatchResponse`; > 500 ⇒ 400 `batch-too-large`; `Version ≠ 1` ⇒ 400 `validation` |
| `PUT /api/mock-sessions/{id}` | signed in | `X-CSRF-TOKEN`, `X-Outbox-Owner` | `api-writes` | `MockSummaryDto` → 204; `NotFound` ⇒ 404 `not-found`; `Invalid` ⇒ 400 `validation` |
| `GET /api/profile?culture=&stack=` | signed in | — | `api-reads` | → `ProfileDto` |

  - Planner's call: session requests are `POST` JSON because the anonymous `New` filter sends up to 1000 excluded ids; they change no state, so no antiforgery token. Binding accepts only `application/json` (415 otherwise) and no CORS policy exists, so a cross-site page can neither send them without a preflight nor read the response.
  - `sealed class AntiforgeryValidationFilter : IEndpointFilter` — `IAntiforgery.ValidateRequestAsync`; failure ⇒ 400 `antiforgery`.
  - `sealed class OwnerCheckFilter : IEndpointFilter` — `X-Outbox-Owner` must parse as a `long` equal to the principal's `telegram_user_id`, else 409 `owner-mismatch` and nothing is stored (ADR 0008); `X-Client-Utc-Offset`, when present, must be an integer in −840..840 (else ignored) and reaches the recorder as `clientUtcOffsetMinutes`.
  - `sealed class ApiErrorMiddleware`, mapped with `app.UseWhen(c => c.Request.Path.StartsWithSegments("/api"), b => b.UseMiddleware<ApiErrorMiddleware>())` right after `UseStatusCodePagesWithReExecute` and before `UseRouting`, so it wraps authentication, authorization, rate limiting, endpoint filters, binding and unmatched routes: sets `IStatusCodePagesFeature.Enabled = false` (otherwise the HTML not-found page is re-executed), rejects a declared `Content-Length` above 256 KB with 413 before reading (the body-size feature is not enforced by `TestServer`), catches `BadHttpRequestException` and writes its status (400 malformed JSON or binding failure, 413, 415) with codes `validation`, `payload-too-large`, `unsupported-media-type`; writes an `ApiErrorDto` for every bodiless 400, 401, 403, 404, 405, 413, 415, 429; any other exception is logged at Error and answered 500 `server-error` without details.
  - `static class ApiRequestParser`: structural validation before iteration — null envelope, null `Answers`, null filter lists ⇒ 400 `validation`; a null element or one without a valid ULID `ClientAnswerId` is skipped and reported as `RejectedAnswerDto("", "invalid")` (the client never sends such entries, Task 13); a valid id with any invalid field (question id, grade, mode, mock id, missing `AnsweredAt`) ⇒ `invalid` for that id; `bool TryParsePractice(PracticeSessionRequest?, IReadOnlySet<TopicKey> knownTopics, out PracticeCriteria, out string error)`, same for mock and review (counts clamp, unknown topics dropped); `bool TryParseSummary(MockSummaryDto?, out MockSummary, out string error)` (finished ≥ started; non-negative counts; `Knew + Partial + DidntKnow + Unanswered` summed over topics = `QuestionCount`; known stack, level and topics).
  - `sealed class RateLimitOptions { int WritesPerMinute { get; set; } = 60; int ReadsPerMinute { get; set; } = 30; }` (`App:RateLimits`); `static class ApiRateLimits { const string Writes = "api-writes"; const string Reads = "api-reads"; }` — fixed windows; writes partitioned by `dit:uid`; reads by `dit:uid` when signed in, else `RemoteIpAddress`, else the key `"unknown"`; 429 with `Retry-After` and `rate-limited`; startup throws when either limit is not positive. Planner's call: generous for a person, stops scripted floods.

- [ ] **Step 1: Write failing tests**
  - `Antiforgery_ReturnsTokenNoStoreAndSetsCookie`; `Antiforgery_IsNotCompressed`; `BatchWithoutToken_Returns400Antiforgery`; `BatchWithAnonymousToken_AfterSignIn_Returns400Antiforgery`; `BatchWithFreshToken_Accepts`.
  - `Batch_OwnerHeaderMismatch_Returns409AndStoresNothing`; `Batch_MissingOwnerHeader_Returns409`; `Batch_UtcOffsetHeaderStoredWhenValid`.
  - `Batch_Anonymous_Returns401Json`; `Batch_Over500_Returns400`; `Batch_MixedValidAndInvalid_AcceptsValidRejectsRest`; `Batch_MissingAnsweredAt_RejectedAsInvalid`; `[Theory] Batch_NullsAndMissingFields_NeverReturn500` (`answers: null`, `[null]`, missing `grade`, missing `clientAnswerId`); `[Theory] Batch_MalformedJson_Returns400Json` (truncated JSON, `answeredAt: "x"`, wrong types; run in `Development` and `Testing` environments); `Batch_BodyOver256KB_Returns413Json`.
  - `Practice_Anonymous_ReturnsSession`; `Practice_AnonymousDue_Returns401`; `[Theory] Sessions_RejectNonJson` (`text/plain`, `multipart/form-data`, form-encoded, missing content type ⇒ 415 for practice, mock and review); `Sessions_CrossOriginPreflight_IsNotGranted`; `Practice_UnknownTopicDropped_CountClamped`.
  - `Review_Anonymous_Returns401Json`; `Review_SignedIn_ReturnsDue`.
  - `MockPut_Idempotent`; `MockPut_OtherUsersId_Returns404Json`; `[Theory] MockPut_InvalidSummary_Returns400` (finished before started; negative count; counts not equal to `QuestionCount`; unknown topic).
  - `Profile_SignedIn_ReturnsStats`; `ApiUnauthenticated_Returns401WithoutLocation`; `BlockedUser_ApiReturns401AfterInterval`.
  - `[Theory] ApiErrors_AreJsonNotHtml` (400 bodiless, 401, 403, 404 for `/api/x`, 405, 413, 415, 429); `ApiUnhandledException_Logged_And500Json`.
  - `Writes_RateLimitedPerUser` (limit 3 in this factory: 4th write ⇒ 429 with `Retry-After`; another user unaffected); `Reads_PartitionByUserWhenSignedIn`; `Reads_NullRemoteAddress_UsesFallbackPartition`; `Startup_NonPositiveRateLimit_Throws`; `TestHosts_UseRaisedLimits` (`TrainerWebFactory` and `AppHostFixture` resolve 100 000 for both limits).
  - `SignedInSessionJson_IsCompressed`.
  - `NoInteractiveServerEndpoints` (`POST /_blazor/negotiate` ⇒ 404).
- [ ] **Step 2:** FAIL. **Step 3:** Implement; ADR 0008; docs. **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat(web): add the /api endpoints with owner checks, json errors and rate limits`.

### Task 12: WebAssembly island host

**Files:**
- Create: `src/Trainer.Web.Client/Trainer.Web.Client.csproj` (`Microsoft.NET.Sdk.BlazorWebAssembly`; `PublishTrimmed` true, `TrimMode` full, `EnableTrimAnalyzer` true, `SatelliteResourceLanguages` `ru;en`), `Program.cs`, `_Imports.razor`, `Localization/ClientResource.cs`, `ClientResource.ru.resx`, `ClientResource.en.resx`, `Components/IslandRoot.razor`; `src/Trainer.Web/Islands/InteractiveIslandPageAttribute.cs`, `IslandRenderMode.cs`, `IslandBootstrapFactory.cs`, `Auth/AuthenticationStateSerialization.cs`; `wwwroot/js/islands-start.js`, `wwwroot/css/islands.css`; `src/Trainer.Web/Components/Pages/Practice.razor` (shell); `tests/Trainer.Web.Client.Tests/` (project); `docs/adr/0009-islands-full-page-navigation.md`
- Modify: `Program.cs` (`AddInteractiveWebAssemblyComponents`, `AddAuthenticationStateSerialization`, `MapRazorComponents<App>().AddInteractiveWebAssemblyRenderMode()`), `App.razor`, `Startup/ThemeScript.cs`, `Startup/SecurityHeadersMiddleware.cs`, `wwwroot/js/theme-toggle.js`, `Components/Layout/LanguageSwitcher.razor`, `Endpoints/SitemapEndpoints.cs` (`profile` disallowed), `src/Trainer.Application/Stacks/IStackQueries.cs` + `StackQueries.cs`, `tests/Trainer.Web.E2E/AppHostFixture.cs`, `Trainer.Web.csproj`, `DevInterviewTrainer.slnx`, `docs/architecture.md` (render modes, islands), `AGENTS.md` (layout, Blazor rules)
- Test: `tests/Trainer.Web.Tests/IslandHostingTests.cs`, `tests/Trainer.Web.E2E/IslandNavigationTests.cs`

**Interfaces:**
- Consumes: Contracts (Task 8), `IProgressQueries.GetMeAsync` (Task 9).
- Produces (server):
  - `[AttributeUsage(Class)] sealed class InteractiveIslandPageAttribute : Attribute` — appears in endpoint metadata; `App.razor` renders, only for such pages: viewport `width=device-width, initial-scale=1, interactive-widget=resizes-content` (Planner's call: the on-screen keyboard resizes the layout instead of covering inputs; iOS Safari ignores it, covered by `env(safe-area-inset-bottom)` padding and noted in `docs/performance.md`), `<link rel="stylesheet" href="@Assets["css/islands.css"]">`, `<ResourcePreloader />`, `<script src="@Assets["_framework/blazor.web.js"]" autostart="false">`, `<script src="@Assets["js/islands-start.js"]">`. No `<ImportMap />` (an inline script the CSP would block; islands import no JS modules by bare name); Task 21 proves on a published build that the runtime boots, requests only expected URLs and that preloads match them.
  - `islands-start.js`: `Blazor.start({ ssr: { disableDomPreservation: true }, webAssembly: { applicationCulture: document.documentElement.lang } })` — enhanced navigation off, so leaving or entering an island page is always a full load and a WASM runtime never outlives its page; it still registers `Blazor.addEventListener('enhancedload', () => window.ditApplyTheme())` as a safety net. Public pages' links to islands carry `data-enhance-nav="false"`. ADR 0009: spec 8.2 allows enhanced navigation with `[PersistentState]`; islands never prerender, so full loads are the only way to guarantee one runtime start and one session fetch per visit.
  - `static class IslandRenderMode { static readonly IComponentRenderMode NoPrerender = new InteractiveWebAssemblyRenderMode(prerender: false); }`.
  - `SecurityHeadersMiddleware`: in `Response.OnStarting`, island endpoints get `'wasm-unsafe-eval'` in `script-src`; every other response keeps Plan 1's policy.
  - `ThemeScript.Source` becomes `window.ditApplyTheme=function(){...};ditApplyTheme();` (same logic, hash follows). `theme-toggle.js` uses a delegated `document` click handler.
  - `LanguageSwitcher` links get `data-enhance-nav="false"` (spec 8.3).
  - `record TopicTitle(TopicKey Key, string Title)`; `Task<IReadOnlyList<TopicTitle>> IStackQueries.GetTopicTitlesAsync(CultureKey culture, StackKey stack, CancellationToken ct)` (live topics, by title then key).
  - `sealed class IslandBootstrapFactory(IStackQueries stacks, IProgressQueries progress)`: `Task<string> CreateJsonAsync(CultureKey culture, StackKey stack, PracticeFiltersDto filters, ClaimsPrincipal user, CancellationToken ct)` — `IslandBootstrapDto` with `Me` for signed-in users (streak, due count, per-topic progress for the sidebar), serialised with `TrainerJsonContext`. Planner's call: island parameters are primitives plus this one JSON string, trimming-safe, nothing fetched before the island is usable.
  - Island pages call `PageLinks.Set` with their path (including canonical filters) in both cultures, so "Sign in" and the language switch return to the same mode.
  - Auth state (spec 8.8): `AddAuthenticationStateSerialization(o => o.SerializationCallback = AuthenticationStateSerialization.SerializeAsync)`; `static class AuthenticationStateSerialization { ValueTask<AuthenticationStateData?> SerializeAsync(AuthenticationState state); }` sends `telegram_user_id`, `name` (display name, else username, else omitted) and `ClaimTypes.Role` values, `NameClaimType = "name"`.
  - Static skeleton: `<div class="island" data-island="practice">` with a fixed-height skeleton and the island; `.island:has(.island-root) > .island-skeleton { display: none }`.
  - `Practice.razor` shell (filled in Task 16): `[InteractiveIslandPage]`, noindex `SeoHead`, h1, bootstrap JSON, `<PracticeIsland @rendermode="IslandRenderMode.NoPrerender" BootstrapJson="@_bootstrap" />`; `PracticeIsland` initially renders only `IslandRoot`.
- Produces (client):
  - `Program.cs`: `AddAuthorizationCore()`, `AddCascadingAuthenticationState()`, `AddAuthenticationStateDeserialization()`, `AddLocalization()`, `HttpClient` (`BaseAddress = HostEnvironment.BaseAddress`), `TimeProvider.System`, `UlidGenerator`, `IAnswerGrader → SelfAssessmentGrader`; later tasks add services.
  - `IslandRoot.razor` — `.island-root`, and after the first interactive render `data-island-ready="true"` (measured in Task 21); increments `window.__ditStarts` once per runtime.
  - `ClientResource` RU/EN; `IStringLocalizer<ClientResource>`; culture only from `applicationCulture`.
- Produces (tests): `AppHostFixture` — fake Telegram server, base `http://localhost:{port}` (Chromium keeps `Secure` cookies on `localhost`), `Task SignInAsync(IBrowserContext context, FakeTelegramIdentity who)`, `IServiceProvider Services`, `Task<IPage> NewPageAsync(IBrowserContext context)` that installs a `securitypolicyviolation` listener and counts `/api/sessions/` requests, `void AssertNoCspViolations(IPage page)`.

- [ ] **Step 1: Write failing tests**
  - Web: `PublicPages_HaveNoBlazorScript` (`/ru`, catalog, question page, sign-in page: no `blazor.web.js`, CSP without `wasm-unsafe-eval`); `IslandPage_HasBlazorScriptAndWasmCsp`; `IslandPage_IsNoindexAndNotOutputCached`; `PublicPages_IslandLinksDisableEnhancedNav`; `TopicTitles_LiveTopicsInCulture`; `Bootstrap_SignedIn_HasMe_AnonymousHasNull`; `IslandPage_SerialisesAuthStateWithCustomClaimsOnly`; `Robots_DisallowsProfile`; `ClientResx_RuAndEn_HaveSameKeys`.
  - E2E: `IslandToPublicToIsland_EachVisitIsANewDocumentWithOneRuntimeStart` (before each click the test sets `window.__ditDocId` to a fresh random value; after practice → brand link → practice the marker is absent on each arriving page, proving a new document; `__ditStarts` is 1 on each island document; no `_framework/` request while on the public page; the session-fetch count is asserted in Task 16, where sessions exist); `ThemePersistsAcrossIslandNavigation`; `ThemeToggleWorksOnIslandPage`; `LanguageSwitchFromIsland_IsFullReloadIntoSameMode`; `IslandPage_HasNoCspViolations`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement; ADR 0009; docs. **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat: host webassembly islands with full-page boundaries and island-only csp`.

### Task 13: Browser storage for answers and mock sessions

**Files:**
- Create: `src/Trainer.Web/wwwroot/js/outbox.js`; `src/Trainer.Web.Client/Storage/IBrowserStore.cs`, `BrowserStore.cs`, `StorageKeys.cs`, `OutboxStore.cs`, `SummaryStore.cs`, `ActiveMockStore.cs`, `StorageStatus.cs`, `Components/StorageNotice.razor`; `tests/Trainer.Web.Client.Tests/OutboxStoreTests.cs`, `SummaryStoreTests.cs`, `ActiveMockStoreTests.cs`, `FakeBrowserStore.cs`; `tests/Trainer.Web.E2E/OutboxConcurrencyTests.cs`
- Modify: `App.razor` (island pages load `outbox.js`), client `Program.cs`, `TrainerJsonContext` (storage records), `docs/architecture.md` (outbox keys, format, durability)
- Test: the files above

**Interfaces:**
- Produces:
  - `outbox.js` (`window.ditStore`): one entry point `mutate(opJson)`. The op declares every key it touches; the function computes that set up front, sorts it, and acquires each lock (`navigator.locks.request('dit-store:' + key, ...)`) exactly once, nested in sorted order, so two moves in opposite directions cannot deadlock and no lock is requested twice. Inside the locks it reads, applies and writes in one synchronous JS task. When `navigator.locks` is missing, `mutate` refuses (`{ ok: false, error: "no-locks" }`) and the client switches to memory-only mode with a visible notice; there is never an unlocked shared read-modify-write (no per-tab journal: Web Locks exist in every browser the app targets, and the notice is enough for the rest). Ops: `append(key, entries)` and `remove(key, ids)` (merge or delete by id); `move(from, to, ids?)` destination-first — merge the entries into `to`, then remove exactly those ids from `from`; a crash between the two writes leaves the entries in both keys, which is harmless because every write and every import merges by id and the server deduplicates, and the next `move` finishes the removal; `summaryAdd(key, item)`, `summaryRemove(key, id)` (merge by mock session id, never a whole-value put); `putIfRevision(key, json, expectedRevision)` and `delete(key)` for the active mock (Task 17); `put(key, json)` only for last-writer-wins preferences (`ServerClock`, `PracticeFilters`). Returns `{ ok, persisted, error, revision? }`; `QuotaExceededError` or no `localStorage` ⇒ `ok: false`, stored data unchanged. `read(key)` returns the raw string. Unparseable JSON is copied to `dit.outbox.v1.corrupt.{unix ms}` and removed only if the copy succeeded, otherwise left in place (memory-only for that key). Parseable content with an unknown `version` (a newer app) is never touched: that key's store runs memory-only with the notice "Progress saved by a newer version of the app is kept; reload to continue".
  - `static class StorageKeys { const string AnonymousOutbox = "dit.outbox.v1.anon"; static string UserOutbox(long telegramUserId) => $"dit.outbox.v1.u.{telegramUserId}"; const string CorruptPrefix = "dit.outbox.v1.corrupt."; static string PendingSummaries(long telegramUserId) => $"dit.mock.v1.pending.{telegramUserId}"; static string FailedSummaries(long telegramUserId) => $"dit.mock.v1.failed.{telegramUserId}"; static string MockQuestions(string owner, string sessionId) => $"dit.mock.v1.{owner}.{sessionId}.questions"; static string MockProgress(string owner) => $"dit.mock.v1.{owner}.active"; const string Account = "dit.account"; const string ServerClock = "dit.clock.v1"; const string PracticeFilters = "dit.practice.v1.filters"; }` (owner = Telegram id or `anon`). Planner's call: one key per owner, so the stored outbox stays exactly the spec 8.8 document and a shared browser never mixes accounts (ADR 0008; it notes that the Telegram id stays in key names after sign-out on a shared browser, accepted because the id is not a secret).
  - `interface IBrowserStore { ValueTask<StoreResult> MutateAsync(StoreOp op); ValueTask<string?> ReadAsync(string key); }` (`BrowserStore` over `IJSRuntime`).
  - `sealed class OutboxStore(IBrowserStore store)`: `ValueTask<IReadOnlyList<AnswerDto>> ReadAsync(string key)`, `ValueTask AddAsync(string key, AnswerDto answer)`, `ValueTask RemoveAsync(string key, IReadOnlyCollection<string> ids)`, `ValueTask<int> MoveAsync(string fromKey, string toKey)`, `ValueTask ClearAsync(string key)`, `StorageStatus Status` (`Persistent`, `MemoryOnly`, `Full`, `NewerVersion`), `event Action? Changed`. On read it validates each entry (ULID id, known grade and mode, `answeredAt` present); invalid entries stay in storage and are never sent. When a write fails, the in-memory snapshot keeps every pending entry (loaded plus new) and the next write retries persisting all of them; nothing is evicted (no cap). `StorageNotice` shows `MemoryOnly` ("Answers are kept only until you close this tab"), `Full` ("Browser storage is full; N answers are not saved yet") and `NewerVersion`.
  - `sealed class SummaryStore(IBrowserStore store)`: `AddPendingAsync(long owner, string mockSessionId, MockSummaryDto summary)`, `ReadPendingAsync(long owner)`, `RemovePendingAsync(long owner, string mockSessionId)`, `MoveToFailedAsync(long owner, string mockSessionId, string reason)`, `ReadFailedAsync(long owner)`, `DiscardFailedAsync(long owner, string mockSessionId)` — every mutation through `summaryAdd`/`summaryRemove` (or `move`) under the same lock discipline.
  - `sealed class ActiveMockStore(IBrowserStore store)`: `SaveQuestionsAsync(string owner, string sessionId, IReadOnlyList<QuestionDto> questions)` (written once at start), `LoadAsync(string owner)` ⇒ `(MockProgress? progress, IReadOnlyList<QuestionDto>? questions)`, `ValueTask<SaveResult> SaveProgressAsync(string owner, MockProgress progress)` (`putIfRevision` with `progress.Revision − 1`; a mismatch ⇒ `SaveResult.OtherWriter`), `ClearAsync(string owner, string sessionId)` (deletes both keys).
  - `record MockProgress(int Version, string SessionId, string Owner, string Stack, string Culture, string Level, int QuestionCount, bool Timed, DateTimeOffset StartedAt, DateTimeOffset? Deadline, string Phase, int Index, long Revision, IReadOnlyList<AttemptState> Attempts, MockCompletionState? Completion)`; `record AttemptState(string ClientAnswerId, IReadOnlyList<string> OptionOrder, IReadOnlyList<string> Picked, IReadOnlyList<int> Lines, string Typed, string Notes, bool UsedHint, DateTimeOffset? LastInputAt, bool Frozen, string? Grade, DateTimeOffset? AnsweredAt, string? SelfGrade, DateTimeOffset? AssessedAt, bool Skipped)`; `record MockCompletionState(DateTimeOffset FinishedAt, IReadOnlyList<AnswerDto> Answers, MockSummaryDto Summary, bool AnswersQueued, bool SummaryQueued)` (all added to `TrainerJsonContext`; `Version = 1`).

- [ ] **Step 1: Write failing tests**
  - Client: `Add_ThenRead_RoundTripsSpecFormat` (`{"version":1,"answers":[{"clientAnswerId":...}]}`); `Remove_OnlyRemovesGivenIds`; `Move_AnonymousToUser_DeduplicatesById`; `Move_CrashBetweenWrites_NextMoveCompletesWithoutDuplicates` (fake store fails the source write once); `Load_CorruptedJson_KeepsRawAndStartsEmpty`; `Load_UnknownVersion_LeftUntouchedAndReportsNewerVersion`; `Load_InvalidEntry_ExcludedButKept`; `Add_QuotaExceeded_KeepsSnapshotInMemoryAndReports`; `Add_AfterQuotaFreed_PersistsWholeSnapshot`; `Outbox_NeverEvictsPendingAnswers` (10 000 entries stay); `NoLocks_SwitchesToMemoryOnly`; `Summary_ConcurrentAddAndRemove_MergeById`; `Summary_MoveToFailed_IsRecoverable`; `ActiveMock_QuestionsWrittenOnce_ProgressRoundTrips`; `ActiveMock_StaleRevision_ReportsOtherWriter`.
  - E2E (two pages in one context): `Outbox_ConcurrentAppendsFromTwoTabs_LoseNothing` (each tab appends 200 distinct entries in a tight loop; 400 remain); `Outbox_AppendDuringRemove_LosesNothing` (tab 1 removes 100 acknowledged ids while tab 2 appends 100; exactly the 100 new remain); `Outbox_OppositeMovesInTwoTabs_DoNotDeadlock` (anon→user and user→anon at once complete within 2 s); `Outbox_QuotaExceeded_LeavesStoredValueIntact`; `Outbox_WithoutWebLocks_ShowsMemoryOnlyNotice` (init script deletes `navigator.locks`; storage is never written).
- [ ] **Step 2:** FAIL. **Step 3:** Implement; docs. **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat(client): add lock-protected browser storage for answers and mock sessions`.

### Task 14: Sync, API client and account isolation

**Files:**
- Create: `src/Trainer.Web.Client/Api/TrainerApiClient.cs`, `AntiforgeryTokenProvider.cs`, `ApiResult.cs`, `ServerClock.cs`; `Sync/OutboxSyncService.cs`, `Sync/AccountWatcher.cs`; `Components/SyncStatus.razor`, `Components/AccountChangedNotice.razor`; `src/Trainer.Web/wwwroot/js/account-publish.js`; `tests/Trainer.Web.Client.Tests/OutboxSyncServiceTests.cs`, `TrainerApiClientTests.cs`, `ServerClockTests.cs`, `FakeHttpHandler.cs`
- Modify: `outbox.js` (`publishAccount(owner)`, `watchAccount(dotnetRef)` over the `storage` event on `dit.account`), `AccountControls.razor` and `SignIn.razor` (load `account-publish.js` with `data-owner` = Telegram id or `anon`; signed-in public pages and the sign-in page publish their owner), client `Program.cs`, `docs/architecture.md` (sync, clock calibration, account isolation)
- Test: the files above

**Interfaces:**
- Consumes: `OutboxStore`, `SummaryStore` (Task 13), `/api` (Task 11).
- Produces:
  - `sealed class ServerClock(TimeProvider device, IBrowserStore store)`: `DateTimeOffset Now()` = device UTC now + current offset; `void Observe(DateTimeOffset serverDate, DateTimeOffset sentAt, DateTimeOffset receivedAt)` — offset = `serverDate + 0.5 s − midpoint(sentAt, receivedAt)` (the `Date` header has 1 s resolution); samples with a round trip over 10 s are ignored; the latest accepted sample wins, bounded to ±1 year (larger is ignored as a broken header); persisted under `StorageKeys.ServerClock` with its observation time and loaded at start; before the first sample the offset is 0. Answers take `answeredAt = ServerClock.Now()` when answered and keep it forever (ADR 0005).
  - `sealed class TrainerApiClient(HttpClient http, AntiforgeryTokenProvider tokens, ServerClock clock, TimeProvider device)`: one method per Task 11 endpoint through `TrainerJsonContext.Default.*`; every response's `Date` header feeds `clock.Observe`; writes take `long owner` (bound for the whole call, including the antiforgery retry) and send `X-CSRF-TOKEN`, `X-Outbox-Owner` and `X-Client-Utc-Offset` (`device.GetLocalNow().Offset` in minutes); returns `ApiResult<T>` (`T? Value`, `int Status`, `string? ErrorCode`); a non-JSON error body yields `ErrorCode = null`; a network failure yields `Status = 0`.
  - `sealed class AntiforgeryTokenProvider(HttpClient http) { ValueTask<string> GetAsync(CancellationToken ct); void Invalidate(); }`.
  - `sealed class OutboxSyncService(OutboxStore outbox, SummaryStore summaries, TrainerApiClient api, TimeProvider time)`: `void Start(long owner)`; `void RequestFlush()`; `Task<bool> FlushAsync(TimeSpan maxWait, CancellationToken ct)` (true when the owner's outbox and pending summaries are empty afterwards). Invariant: an answer leaves the outbox only when its id is in `Accepted` or `Rejected` of a 200 response; every other outcome (400 `validation`, 400 `batch-too-large`, 413, 429, 5xx, network) keeps it and backs off (5 s doubling to 5 min, `TimeProvider` timer); on 400 `batch-too-large` or 413 the next attempt halves the chunk (minimum 1). Chunks of 200 (Planner's call: under the 500 limit, small enough to retry). Then PUTs pending summaries: 204 ⇒ remove; 404 with code `not-found` or 400 `validation` ⇒ `MoveToFailedAsync` (kept, listed on the profile with "Could not be saved" and Discard); a 404 without that code, 5xx or network ⇒ keep and retry. On 400 `antiforgery` refetch the token once and retry; on 409 `owner-mismatch` or 401 stop, keep everything and raise `AccountMismatch` / `SignedOut`. `int Pending`, `event Action? Changed`.
  - `sealed class AccountWatcher(IJSRuntime js)`: publishes the island's owner (Telegram id or `anon`) on `dit.account` at start and raises `Changed(string newOwner)` when another tab publishes a different one; the island then stops syncing and shows `AccountChangedNotice` ("You signed in or out in another tab. Reload to continue.") with a Reload button. `account-publish.js` does the same publishing on signed-in public pages and the sign-in page. The server's owner check stays authoritative.
  - Flush-before-fetch: islands that read server state (Review, Practice with `due` or signed-in `new`, profile) call `FlushAsync(TimeSpan.FromSeconds(5))` first; on `false` they continue and show "N answers not synced yet".

- [ ] **Step 1: Write failing tests**
  - Client: `ServerClock_CalibratesFromDateHeader`; `ServerClock_IgnoresSlowRoundTripAndAbsurdOffset`; `ServerClock_PersistsAndReloadsOffset`; `Answer_ClockChangedBeforeFlush_KeepsOriginalTime` (answer taken, device clock moved +3 h, flush sends the original `answeredAt`); `Flush_AcceptedAndRejected_LeaveOutbox`; `[Theory] Flush_NonSuccess_KeepsEntriesAndBacksOff` (400 `validation`, 400 `batch-too-large`, 413, 429, 500, network); `Flush_TooLarge_HalvesChunk`; `Flush_On400Antiforgery_RefetchesTokenAndRetriesWithSameOwner`; `Flush_On409OwnerMismatch_KeepsEntriesAndStops`; `Flush_On401_KeepsEntriesAndStops`; `Flush_ChunksOf200`; `Flush_OnlySendsBoundOwnersOutbox`; `Flush_SendsOwnerAndUtcOffsetHeaders`; `Flush_SkipsInvalidEntries`; `Flush_Summary_RemovedOn204_FailedOnNotFoundCode_KeptOnBare404And500`; `Api_NonJsonErrorBody_HasNoCode`.
  - Web: `SignedInPublicPage_AndSignInPage_PublishOwner` (`account-publish.js` with the right `data-owner`).
  - The two-tab isolation and flush-before-Review scenarios need Practice and Review and are tested in Tasks 16 and 18.
- [ ] **Step 2:** FAIL. **Step 3:** Implement; docs. **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat(client): sync answers per account with owner checks and a cross-tab account watch`.

### Task 15: Question session model and island components

**Files:**
- Create: `src/Trainer.Web.Client/Sessions/QuestionKind.cs`, `QuestionAttempt.cs`, `OptionShuffler.cs`, `PracticeSession.cs`, `AnswerFactory.cs`, `IslandState.cs`; `Components/QuestionCard.razor`, `OptionList.razor`, `CodeLineSelector.razor`, `CodePanel.razor`, `TypedOutputInput.razor`, `SelfAssessment.razor`, `FeedbackPanel.razor`, `SessionBar.razor`, `TopicSidebar.razor`, `IslandStateView.razor`, `KeyboardShortcuts.cs`; `src/Trainer.Web/wwwroot/js/shortcuts.js`
- Modify: `islands.css`, client resx, `docs/architecture.md` (island UI rules)
- Test: `tests/Trainer.Web.Client.Tests/QuestionAttemptTests.cs`, `PracticeSessionTests.cs`, `IslandStateTests.cs`

**Interfaces:**
- Consumes: `AutoGrading`, `IAnswerGrader`, `UlidGenerator` (Tasks 1-2), Contracts, `ApiResult` and `ServerClock` (Task 14), `AttemptState` (Task 13).
- Produces:
  - `enum QuestionKind { SingleChoice, MultiChoice, OutputChoice, OutputTyped, FindBug, SelfAssessed }` (`output` with options ⇒ `OutputChoice`, without ⇒ `OutputTyped`).
  - `static class OptionShuffler { IReadOnlyList<OptionDto> Shuffle(IReadOnlyList<OptionDto> options, Random random); }` (Fisher–Yates; every mode, spec 5.3).
  - `sealed class QuestionAttempt(QuestionDto question, string clientAnswerId, Random random, ServerClock clock)`: `string ClientAnswerId` (fixed at creation, so repeated completion never creates a second id), `Kind`, `Options` (shuffled), `bool UsedHint` + `void RevealHint()`, `IReadOnlySet<string> Picked` + `void Toggle(string optionId)`, `IReadOnlySet<int> SelectedLines` + `void ToggleLine(int line)`, `string Typed` + `void SetTyped(string value)`, `string Notes` + `void SetNotes(string value)`, `DateTimeOffset? LastInputAt` (`clock.Now()` at every pick, toggle and non-empty typed change; not at hints or notes), `bool HasResponse` (automatic kinds: a pick, a line or non-empty typed text; a hint alone is not a response; self-assessed kinds: false until graded), `bool CanSubmit`, `bool IsFrozen` + `void Freeze()` (later input ignored), `Grade? Grade`, `DateTimeOffset? AnsweredAt`, `bool Skipped` + `void Skip()` (self-assessed only: no answer is created), `Grade SubmitAutomatic()` (`AnsweredAt = clock.Now()`, freezes), `void RevealReference()`, `ValueTask<Grade> SubmitSelfAssessmentAsync(Grade choice, IAnswerGrader grader, CancellationToken ct)` (an explicit self-grade creates the answer whether or not notes were written; `AnsweredAt = clock.Now()`), `AttemptState Snapshot()`, `static QuestionAttempt Restore(QuestionDto question, AttemptState state, ServerClock clock)` (restores frozen state, grade, times and skip).
  - Eligibility (both modes): automatic kinds produce an answer only when submitted (Practice) or, in Mock, frozen with `HasResponse`; self-assessed kinds produce an answer only from an explicit self-grade; Practice offers "Skip" on self-assessed questions after the reference is shown (no answer, moves on).
  - `sealed class PracticeSession(IReadOnlyList<QuestionDto> questions, UlidGenerator ids, Random random, ServerClock clock)`: `bool IsEmpty`, `int Index`, `int Count`, `QuestionAttempt? Current` (null when empty), `bool IsFinished`, `void Next()`, `IReadOnlyList<(QuestionDto Question, Grade Grade)> Results`, `IReadOnlyList<string> SeenIds`.
  - `sealed class AnswerFactory { AnswerDto Create(QuestionAttempt attempt, AnswerMode mode, string? mockSessionId); }` (uses the attempt's id and `AnsweredAt`).
  - `enum IslandStateKind { Loading, Ready, Empty, Offline, RateLimited, SignedOut, AccountChanged, ServerError }`; `static class IslandState { IslandStateKind FromResult<T>(ApiResult<T> result, bool sessionEmpty); }`; `IslandStateView` (RU/EN) per state: Empty ⇒ "No questions match these filters" + "Change filters"; Offline ⇒ "You are offline; starting a session needs the network" + "Try again"; RateLimited ⇒ "Too many requests; try again in N s"; SignedOut ⇒ "You were signed out" + sign-in link; AccountChanged ⇒ Task 14 notice; ServerError ⇒ "Something went wrong" + "Try again". Storage states use `StorageNotice` (Task 13). Answers rejected by the server are not surfaced one by one (invalid or retired); `SyncStatus` shows pending counts. Mock and Review reuse this view.
  - Components follow `docs/design/mockups/question-screen-themes.html`: `SessionBar` (level chip, topic chip, `i / N`, streak from `Me` when signed in, session progress as `<progress>`); `TopicSidebar` (topics with known/live as `<meter>` and the review-queue count for signed-in users from `Me`, topic names only for anonymous users; full sidebar ≥ 1024 px, collapsible 640–1023 px, `<details>` drawer below 640 px, spec 9.1); `QuestionCard` (title, prompt, `CodePanel`); `OptionList` (native radio/checkbox inputs in labels ≥ 44×44 px); `CodeLineSelector` (each line a `<button type="button" aria-pressed>` with number and line HTML, ≥ 44 px tall on every pointer type, horizontal scroll inside the panel, code font ≥ 13 px); `TypedOutputInput`; `SelfAssessment` (notes textarea bound to the attempt, "Show reference answer", then knew / partially / didn't know); `FeedbackPanel` as terminal output (`✗ expected …` / `✓`, `$ explain`, per-option verdicts with "why", `$ fix` for find-bug, `$ refs` with link-language markers). After submit a visually hidden `role="status"` region announces only the verdict and focus moves to the Next button; the feedback panel itself is not a live region, so nothing is read twice. Dynamic sizes only through `<progress>`, `<meter>` or SVG attributes.
  - Keyboard (`shortcuts.js` + `KeyboardShortcuts` through `DotNetObjectReference`): ignores events with Ctrl, Alt or Meta; ignores all keys inside text inputs, `textarea` and `[contenteditable]`; ignores Enter when the target is a button, link or `summary` (native activation wins); `1`–`4` pick the n-th displayed option (toggle for multi), or `1`/`2`/`3` choose knew / partially / didn't know during self-assessment; `Enter` submits when `CanSubmit`; `N` goes next after feedback. `<kbd class="kbd-hint">` visible only under `@media (pointer: fine)`.
  - Layout (spec 9.1): below 640 px single column, options stacked, sticky bottom bar (`position: sticky; bottom: 0; padding-bottom: env(safe-area-inset-bottom)`) with the primary action; 640–1023 px options in two columns, feedback below; ≥ 1440 px content capped at 1280 px with feedback beside the question.

- [ ] **Step 1: Write failing tests**
  - `Kind_OutputWithOptions_IsChoice_WithoutIsTyped`; `Shuffle_IsPermutation_AndSeedDependent`; `Single_SubmitCorrect_IsKnew`; `Hint_CapsAutomaticAtPartial`; `Multi_ToggleAndSubmit_UsesSpecRule`; `Typed_SubmitNormalises`; `FindBug_ToggleLines_GradesBySet`; `SelfAssessed_RequiresReveal_ThenGradesThroughGrader`; `CanSubmit_FalseUntilSomethingPicked`.
  - `LastInputAt_UpdatedByPicksLinesAndTyped_NotByHintOrNotes`; `HintOnly_IsNotAResponse`; `SelfAssessed_BlankNotes_ExplicitGradeCreatesAnswer`; `SelfAssessed_Skip_CreatesNoAnswer`; `AnsweredAt_UsesServerClock`; `Freeze_IgnoresFurtherInput`; `SnapshotRestore_KeepsIdOptionOrderPicksNotesFrozenGradeAndSkip`.
  - `Session_Empty_HasNoCurrentAndIsFinished`; `Session_NextAdvancesAndFinishes`; `AnswerFactory_UsesAttemptIdAndTime`.
  - `[Theory] IslandState_FromResult` (status 0 ⇒ Offline, 429 ⇒ RateLimited, 401 ⇒ SignedOut, 409 ⇒ AccountChanged, 500 ⇒ ServerError, 200 with no questions ⇒ Empty).
- [ ] **Step 2:** FAIL. **Step 3:** Implement; docs. **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat(client): add the question session model and island components`.

### Task 16: Practice island and entry points

**Files:**
- Create: `src/Trainer.Web.Client/Islands/PracticeIsland.razor`, `Components/PracticeFilters.razor`; `src/Trainer.Web/wwwroot/js/runtime-preload.js`; `tests/Trainer.Web.E2E/PracticeFlowTests.cs`
- Modify: `src/Trainer.Web/Components/Pages/Practice.razor`, `Home.razor` (Practice and Mock cards become links; the Review card links to `/review` and says "sign in" for anonymous users), `QuestionPage.razor` ("Practise this question" → `/{culture}/{stack}/practice?first={id}`), `Catalog.razor`, `TopicPage.razor` ("Practise these questions" with the filters), `App.razor` (public pages load `runtime-preload.js`, deferred), resx, `docs/architecture.md`, `README.md` (practice screenshot)
- Test: `tests/Trainer.Web.Tests/PracticePageTests.cs`, `tests/Trainer.Web.E2E/PracticeFlowTests.cs`

**Interfaces:**
- Consumes: Tasks 12-15.
- Produces:
  - `PracticeIsland` (parameter `string BootstrapJson`): filters (levels, topics, types, status `all`/`new`, plus `due` when signed in, count); the last chosen filters are remembered under `StorageKeys.PracticeFilters` and win over defaults, not over URL filters; Start ⇒ flush-before-fetch when the status reads server state, then `POST /api/sessions/practice` (anonymous `New`: `ExcludeQuestionIds` = distinct question ids from the anonymous outbox, newest 1000). `?first=` starts the session immediately with that question first. One question at a time with the Task 15 components; every answer goes to the owner's outbox and, when signed in, `RequestFlush()`. Session end: summary by topic with links to the public question pages; "New session" excludes the ids just seen, in every status. Empty and error results use `IslandStateView`.
  - `Practice.razor`: filters parsed with `CatalogQueryParser` plus `status` and `first`; bootstrap JSON; `PageLinks.Set`.
  - `runtime-preload.js` (public pages only): on the first `pointerenter`, `focus` or `touchstart` of an element with `data-preload-runtime`, appends `<link rel="prefetch">` for the URLs in that attribute (rendered from `Assets["_framework/blazor.web.js"]` and `Assets["_framework/dotnet.js"]`), once per page (spec 8.2); Task 21 checks the URLs equal what the runtime requests.

- [ ] **Step 1: Write failing tests**
  - Web: `PracticePage_ShellHasSkeletonIslandMarkerAndBootstrap`; `PracticePage_FiltersFromQueryNormalised`; `Home_ModeCardsLinkToModesWithoutEnhancedNav`; `QuestionPage_HasPractiseThisQuestionLink`; `PublicPages_PracticeLinksCarryPreloadAttribute_AndNoBlazorScript`.
  - E2E (anonymous unless stated; `AssertNoCspViolations` after each): `Practice_SingleWrongAnswer_ShowsTerminalFeedbackAndCorrectOption`; `Practice_FindBug_SelectLineAndSubmit`; `Practice_TypedOutput_Accepted`; `Practice_SelfAssessed_RevealThenGrade`; `Practice_KeyboardOnly_1To4EnterN`; `Practice_EnterOnFocusedHintButton_DoesNotSubmit`; `Practice_CtrlN_IsIgnored`; `Practice_KeyboardHintsHiddenOnTouch`; `Practice_AnswersStoredInAnonymousOutbox` (spec shape); `Practice_SignedIn_AnswersReachServer`; `Practice_OfflineAnswers_SyncWhenOnline` (offline after load, answer 2 with feedback, online again, rows appear within the retry window); `Practice_OfflineAtStart_ShowsOfflineState`; `Practice_NoMatches_ShowsEmptyState`; `Practice_WithStorageBlocked_ShowsNotice`; `Practice_FirstQuestion_StartsImmediately`; `Practice_RemembersLastFilters`; `Practice_NewSession_ExcludesJustSeen`; `Practice_SidebarDrawerOnPhone_CollapsibleOnTablet`; `Practice_NoHorizontalOverflow` at 320, 375, 768, 1280, 1920 (dark) and 375, 1280 (light), screenshots `practice-{width}-{theme}.png`; `Practice_TouchTargetsAtLeast44px` (375 coarse, 1280 fine, 1280 fine with touch); `Practice_StickyActionVisibleOnPhone`; `Practice_TypedInputVisibleWhenViewportShrinks` (375×667, focus, resize to 375×360: the input stays in the viewport); `Practice_RuCulture_UsesRuStrings` (the Start button, empty-state message and feedback heading equal their RU resource values).
  - E2E moved here from earlier tasks because they need a working Practice: `IslandToPublicToIsland_OneSessionFetchPerVisit` (practice → public page → practice with `?first=`: exactly one `/api/sessions/practice` request per island document); `TwoTabs_AnswersOfAStayWithA_WhenBSignsIn` — tab 1 signed in as A on Practice goes offline and answers 2 questions; tab 2 signs out and signs in as B (landing on the sign-in page and a public page, which publish the owner); tab 1 shows the account-changed notice and sends nothing; with the broadcast suppressed by an init script, tab 1 is forced to flush online: the server answers 409, A's key still holds 2 entries, B has no answer rows; signing in as A again and reloading flushes them under A; `Preload_UrlsMatchRuntimeRequests_DebugHost` (hover the Practice card on the home page, then navigate: every prefetched URL is among the runtime's requests; Task 21 repeats it on the published build).
- [ ] **Step 2:** FAIL. **Step 3:** Implement; docs. **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat(client): add the practice island and its entry points`.

### Task 17: Mock interview island

**Files:**
- Create: `src/Trainer.Web.Client/Sessions/MockSession.cs`, `MockSummaryBuilder.cs`; `Islands/MockIsland.razor`; `Components/MockSetup.razor`, `MockTimer.razor`, `MockJumpStrip.razor`, `MockAssessment.razor`, `MockSummaryView.razor`; `src/Trainer.Web/Components/Pages/Mock.razor`
- Modify: `islands.css`, resx, `docs/architecture.md` (mock flow)
- Test: `tests/Trainer.Web.Client.Tests/MockSessionTests.cs`, `MockSummaryBuilderTests.cs`; `tests/Trainer.Web.Tests/MockPageTests.cs`; `tests/Trainer.Web.E2E/MockFlowTests.cs`

**Interfaces:**
- Consumes: `QuestionAttempt`, `AnswerFactory`, `IslandStateView` (Task 15), `ActiveMockStore`, `MockProgress`, `SummaryStore`, `OutboxStore` (Task 13), `OutboxSyncService`, `ServerClock` (Task 14), `TrainerApiClient`.
- Produces:
  - `enum MockPhase { Setup, Prepared, Answering, Assessment, Summary }`; `sealed class MockSession(IReadOnlyList<QuestionDto> questions, MockContext context, UlidGenerator ids, Random random, ServerClock clock)` with `record MockContext(string SessionId, string Owner, string Stack, string Culture, string Level, bool Timed, TimeSpan? Limit, DateTimeOffset StartedAt)`: `MockPhase Phase`, `IReadOnlyList<QuestionAttempt> Attempts`, `int Index`, `void GoTo(int index)`, `DateTimeOffset? Deadline`, `TimeSpan Remaining(DateTimeOffset now)` (from the deadline, never from tick counts, so throttled background tabs stay right), `bool Finish()` (idempotent: the first call freezes all attempts and moves to Assessment; later calls return false), `IReadOnlyList<QuestionAttempt> PendingAssessment` (every self-assessed attempt not yet graded or skipped), `ValueTask AssessAsync(int index, Grade choice, IAnswerGrader grader, CancellationToken ct)`, `void SkipAssessment(int index)`, `MockCompletionState Complete()` (callable once Assessment is done; idempotent, returns the stored instance on repeat). Eligibility (controller ruling): automatic attempts with `HasResponse` are graded at `Finish` with `answeredAt = LastInputAt`; self-assessed attempts produce an answer from an explicit self-grade (`answeredAt = AssessedAt`), notes or not; an automatic attempt without a response and a skipped self-assessed one count as `Unanswered` in the summary only and feed no answer to the scheduler. `MockProgress Snapshot(long revision)` holds the full frozen and completion state; `static MockSession Restore(MockProgress progress, IReadOnlyList<QuestionDto> questions, ...)`.
  - `sealed class MockSummaryBuilder { MockSummaryDto Build(MockSession session, DateTimeOffset finishedAt); }` — per-topic `Knew`/`Partial`/`DidntKnow`/`Unanswered`.
  - Persistence (ruling D): questions are written once to `MockQuestions(owner, sessionId)` at start; the small `MockProgress` document is saved on every pick, phase change and assessment, and text input (notes, typed output) is debounced by 500 ms with a forced save on `visibilitychange` and `pagehide`. Each save uses `putIfRevision`; `OtherWriter` means another tab continues this mock, so this tab turns read-only with the notice "This mock is open in another tab". Completion handoff is ordered and resumable: (1) `Complete()` stores `MockCompletionState` (answers with their ids and times, summary) in the progress document; (2) answers are appended to the outbox, then `AnswersQueued = true` is saved; (3) signed-in users' summary is added to `SummaryStore`, then `SummaryQueued = true` is saved (anonymous summaries are shown only, Planner's call: spec 8.8 imports answers, not summaries); (4) only when both steps report `persisted` are both mock keys cleared. A reload at any point resumes from the stored state and repeats only the unfinished steps; ids are the stored ones, so a repeat is deduplicated. In memory-only storage the mock keys are never cleared before the tab closes.
  - Setup (spec 5.3): level (required), topics (optional), N (5–50, default 20), timer off/on with `N × 90 s` (Planner's call: typical screening pace, shown before start). Start ⇒ `POST /api/sessions/mock` ⇒ `Prepared`: `PoolSize == 0` ⇒ empty state; `RepeatsLikely` ⇒ warning "Only P questions match; sessions will repeat questions. Try N = {SuggestedCount}." with "Use N = {SuggestedCount}" (refetches) and "Start anyway" (uses the prepared questions); otherwise straight to Answering. Changing a filter in `Prepared` discards the prepared questions.
  - Answering: no feedback; `i / N` and `MockJumpStrip` (answered markers, horizontally scrollable on phones, 44 px buttons); `MockTimer` (`role="timer"`; one polite announcement at 1 minute left). On load an active session of the current owner resumes (Answering, Assessment, Summary or an unfinished handoff) with its deadline; an expired deadline finishes it on resume. Timer end and "Finish" both call `Finish`.
  - Assessment: every `open`/`situation` question appears, with the user's notes, the reference answer, three buttons (`1`–`3`) and "Skip". Summary: totals, per-topic table and a per-question list with grade or "unanswered", linking to each question page.
  - `Mock.razor`: `[InteractiveIslandPage]`, noindex, bootstrap JSON, `PageLinks.Set`.

- [ ] **Step 1: Write failing tests**
  - Client: `Finish_GradesAutomaticWithResponseAndListsAllSelfAssessed`; `Finish_OpenWithoutNotes_IsAssessedAndRecorded`; `Assessment_Skip_CountsUnanswered`; `HintOnlyAutomatic_IsUnanswered`; `Finish_IsIdempotent`; `FinishAndTimerAtOnce_CompleteOnce` (two concurrent `Finish` calls and two `Complete` calls yield one answer set with the attempts' ids); `AnsweredAt_IsLastInputForAutomatic_AssessmentForSelf`; `Remaining_ComputedFromDeadline`; `NoGradeVisibleBeforeFinish`; `SnapshotRestore_KeepsFrozenGradesCompletionAndContext`; `ActiveMock_InputSave_DoesNotRewriteQuestions`; `TypedInput_IsDebouncedAndFlushedOnPagehide`; `[Theory] Handoff_TerminatedAfterEachStep_ResumesWithoutLossOrDuplicates` (fake store throws after step 1, 2, 3 and before clear; restore and resume: the outbox holds each answer once, the summary is queued once, keys cleared at the end); `Handoff_MemoryOnly_NeverClears`; `SecondWriter_GetsOtherWriterAndTurnsReadOnly`; `SummaryBuilder_CountsPerTopicIncludingUnanswered`.
  - Web: `MockPage_IsIslandNoindexWithBootstrap`.
  - E2E: `Mock_UntimedFiveQuestions_AssessmentThenSummary` (no feedback while answering; the summary lists each question); `Mock_TimerExpires_GoesToAssessment` (`page.Clock.InstallAsync` before navigation, `FastForwardAsync` 450 s); `Mock_SmallPool_WarnsBeforeAnswering` (no question is visible until "Start anyway"); `Mock_UseSuggestedCount_RefetchesWithSuggestion`; `Mock_EmptyPool_ShowsEmptyState`; `Mock_ReloadMidSession_Resumes`; `Mock_ReloadDuringAssessment_KeepsGrades`; `Mock_SecondTab_IsReadOnly`; `Mock_OfflineFinish_SummaryStoredAfterReopen` (signed in: finish offline, close the page, reopen online: a `mock_sessions` row appears); `Mock_SignedIn_SummaryStored`; `Mock_NoHorizontalOverflow` matrix, screenshots `mock-{width}-{theme}.png`; `Mock_TouchTargetsAtLeast44px`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement; docs. **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat(client): add the mock interview island`.

### Task 18: Review and profile islands, anonymous progress import

**Files:**
- Create: `src/Trainer.Web.Client/Sessions/ReviewSession.cs`; `Islands/ReviewIsland.razor`, `ProfileIsland.razor`; `Components/RecallStep.razor`, `ImportPanel.razor`, `ImportNotice.razor`, `ProgressBars.razor`; `src/Trainer.Web/Components/Pages/Review.razor`, `Profile.razor`; `src/Trainer.Web/wwwroot/js/import-notice.js`; `docs/adr/0010-profile-url.md`
- Modify: `AccountControls.razor` (signed-in responses render a hidden, localised import notice linking to the profile and load `import-notice.js`; owner publishing already exists from Task 14), `islands.css`, resx, `docs/architecture.md`, `README.md` (modes)
- Test: `tests/Trainer.Web.Client.Tests/ReviewSessionTests.cs`; `tests/Trainer.Web.Tests/ReviewAndProfilePageTests.cs`; `tests/Trainer.Web.E2E/ReviewFlowTests.cs`, `ProfileAndImportTests.cs`

**Interfaces:**
- Consumes: `POST /api/sessions/review`, `GET /api/profile`; Tasks 13-15.
- Produces:
  - `sealed class ReviewSession(IReadOnlyList<QuestionDto> questions, UlidGenerator ids, Random random, ServerClock clock)` — like `PracticeSession`, plus `bool AwaitingRecall` for choice kinds: the prompt shows without options and asks "Can you answer?"; `void ShowOptions()` reveals the shuffled options (spec 5.3). Answers use `mode = review`.
  - `Review.razor`: `[InteractiveIslandPage]`, `[Authorize]`, noindex, `PageLinks.Set`. `ReviewIsland`: flush-before-fetch, then due questions; empty ⇒ "Nothing is due now" with links to Practice and the profile (which shows the next due date).
  - `Profile.razor` (`/{Culture}/{Stack}/profile`; ADR 0010: spec 8.3 lists no profile URL; per stack because statistics are per stack and routing checks are reused): `[InteractiveIslandPage]`, `[Authorize]`, noindex. `ProfileIsland`: flush-before-fetch, then `GET /api/profile`: streak, total answered, due now (link to Review), next due date, per-topic and per-level progress (`<meter>` with text values), last five mocks with unanswered counts, `SyncStatus`, failed mock summaries from `SummaryStore.ReadFailedAsync` ("Could not be saved" with the reason and a Discard action), `ImportPanel`.
  - Anonymous import (spec 4, 11.4; Planner's call on the UI): when a signed-in user's browser holds entries under `StorageKeys.AnonymousOutbox`, `ImportPanel` (anchor `#import`) shows "N answers from this browser before you signed in" with **Import into my account** (`MoveAsync` to the user's key under both locks, then flush, then the result) and **Discard** (confirmation, then clear). Nothing is imported without the click. Islands show a compact `ImportNotice` linking to `#import`; signed-in public pages reveal the server-rendered notice through `import-notice.js` (checks only the key's presence; text is server-localised). Import is the ordinary idempotent flush (spec 8.8).

- [ ] **Step 1: Write failing tests**
  - Client: `Review_ChoiceKinds_StartWithRecallStep`; `Review_ShowOptions_RevealsShuffled`; `Review_OpenKind_SkipsRecallStep`; `Review_AnswersUseReviewMode`; `Review_Empty_HasNoCurrent`.
  - Web: `ReviewPage_Anonymous_RedirectsToSignIn`; `ProfilePage_Anonymous_RedirectsToSignIn`; `ReviewAndProfile_SignedIn_AreIslandsNoindexPrivate`; `SignedInPage_RendersHiddenImportNotice_AnonymousPageDoesNot`.
  - E2E: `Review_DueQuestion_RecallThenOptions` (a due state seeded through `IAnswerRecorder`); `Review_EmptyQueue_ShowsNothingDue`; `Review_SecondSession_DoesNotRepeatJustAnswered`; `Profile_ShowsStreakNextDueAndTopicProgress`; `Profile_ShowsFailedSummaryAndDiscards`; `AnonymousAnswers_ImportedAfterSignIn_OnlyWhenChosen` (answer 2 anonymously, sign in: no rows, notice visible on a public page, Import ⇒ 2 rows); `AnonymousAnswers_Discard_ClearsAfterConfirmation`; `Review_NoHorizontalOverflow`, `Profile_NoHorizontalOverflow` matrices with screenshots; touch targets at 375.
- [ ] **Step 2:** FAIL. **Step 3:** Implement; ADR 0010; docs. **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat(client): add review and profile islands with anonymous progress import`.

### Task 19: Catalog search and related questions

**Files:**
- Create: `docs/adr/0011-catalog-search-get-form.md`
- Modify: `src/Trainer.Application/Catalog/CatalogQuery.cs` (trailing `string? Search = null`), `ICatalogQueries.cs` (doc), `Questions/IQuestionQueries.cs` (`GetRelatedAsync`); `src/Trainer.Infrastructure/Queries/CatalogQueries.cs`, `QuestionQueries.cs`; `src/Trainer.Web/Catalog/CatalogQueryParser.cs`, `Caching/PublicPageCachePolicy.cs`, `Components/Pages/Catalog.razor`, `Components/Shared/FilterPanel.razor`, `QuestionPage.razor`, resx, `site.css`; `tests/Trainer.Web.Tests/CatalogPageTests.cs` (`Catalog_AndHome_AndAbout_HaveNoFormsAndNoSetCookie` becomes `PublicPages_HaveOnlyTheGetSearchFormAndNoSetCookie`); `docs/architecture.md` (catalog, cache), `AGENTS.md` (Blazor rule on forms)
- Test: `tests/Trainer.Web.Tests/CatalogQueryParserTests.cs`, `CatalogSearchTests.cs`, `RelatedQuestionsTests.cs`; `tests/Trainer.Infrastructure.Tests/CatalogQueriesTests.cs`

**Interfaces:**
- Produces:
  - `CatalogQueryParser`: `public const string SearchParameter = "q"` — trimmed, inner whitespace collapsed, at most 100 characters, empty ⇒ null; `ToCanonicalQueryString` appends `q=` (escaped) last when present.
  - Search (Planner's call: `ILIKE` plus tags instead of full-text search; ~1000 questions per culture at most, and Russian stemming would need its own text-search configuration): title in the culture contains the term case-insensitively (`%`, `_`, `\` escaped) or a tag equals the term lowercased with spaces turned into `-`; facets respect it.
  - `PublicPageCachePolicy`: a catalog request with a parsed `Search` is not cacheable (no lookup, no storage); search results are `noindex`. ADR 0011: spec 8.4's key allow-list (`level`, `topic`, `type`, `page`) stays and search results render per request (unbounded keys); public pages gain one `<form method="get">` without token or cookie, which spec 11.2's "no form" rule excluded only for antiforgery and caching reasons that do not apply to it.
  - Search box in `FilterPanel`: `<form method="get" action="/{culture}/{stack}/questions" role="search">`, labelled `<input type="search" name="q" maxlength="100">`, hidden inputs for current filters, submit button (44 px).
  - `Task<IReadOnlyList<CatalogRow>> IQuestionQueries.GetRelatedAsync(CultureKey culture, StackKey stack, QuestionId id, int limit, CancellationToken ct)` — live questions in the culture sharing a tag, by shared-tag count descending, then same topic, then title; 5 on the question page (spec 5.1), part of the cached anonymous page.

- [ ] **Step 1: Write failing tests**
  - `Parser_Search_TrimsCollapsesAndCaps`; `Canonical_IncludesSearchLast`.
  - `Search_MatchesTitleCaseInsensitive`; `Search_MatchesTagWithSpaces`; `Search_EscapesWildcards`; `Search_RespectsCultureAndFilters`.
  - `CatalogSearch_IsNoindexAndNotCached`; `CatalogWithoutSearch_StillCached`.
  - `PublicPages_HaveOnlyTheGetSearchFormAndNoSetCookie`.
  - `Related_SharesTagsOrderedByOverlap_ExcludesSelfRetiredAndOtherCulture`; `QuestionPage_ShowsRelatedList`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement; ADR 0011; docs. **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat(web): add catalog search and related questions`.

### Task 20: Island accessibility and responsive sweep

**Files:**
- Create: `tests/Trainer.Web.E2E/IslandLayoutTests.cs`, `IslandAccessibilityTests.cs`
- Modify: `islands.css`, `site.css` (fixes the tests find)

**Interfaces:**
- Consumes: all islands (Tasks 16-18), sign-in and admin pages (Tasks 5, 10).
- Produces: one matrix over `practice` (question), `practice-feedback`, `mock`, `mock-summary`, `review`, `profile`, `signin`, `admin` × 375, 768, 1280, 1920 (dark) and 375, 1280 (light), plus 320 for `practice-feedback` and `profile`; screenshots `TestResults/screenshots/island-{page}-{width}-{theme}.png`.

- [ ] **Step 1: Write failing tests**
  - `NoHorizontalOverflow` (matrix).
  - `PrimaryActionVisible` (matrix; spec 11.8).
  - `NoOverlappingKeyElements` (title, code panel, options, primary action).
  - `TouchTargetsAtLeast44px` (matrix widths, pointer configurations coarse, fine, fine with touch) for every interactive element in `.island-root`, the account controls and the admin forms.
  - `CodeFontAtLeast13px` (`.code-line, pre.code` at every width).
  - `EveryControlHasAnAccessibleName` (accessibility snapshot in both cultures).
  - `FocusIsVisible` (tabbing through a practice question: each focused element shows an outline or ring).
  - `VerdictAnnouncedOnce` (exactly one `role="status"` region receives the verdict; the feedback panel has no `aria-live`).
  - `IslandPages_HaveNoCspViolations` (matrix, via the fixture listener).
- [ ] **Step 2:** Run → some FAIL. **Step 3:** Fix until PASS. **Step 4:** PASS locally and in CI.
- [ ] **Step 5:** Commit `test(web): check islands for overflow, touch targets and accessibility at every width`.

### Task 21: Published Release smoke and performance

**Files:**
- Create: `tests/Trainer.Web.Release/` (`Trainer.Web.Release.csproj`, `PublishedAppFixture.cs`, `ReleaseSmokeTests.cs`, `IslandPerformanceTests.cs`), `docs/performance.md`
- Modify: `.github/workflows/ci.yml` (unit step gains `--filter-not-trait "Category=Release"`; new job `release`), `AGENTS.md` (commands), `src/Trainer.Web.Client/Trainer.Web.Client.csproj` and `runtime-preload.js` only when a test below fails (each change and its measured effect recorded in `docs/performance.md`)

**Interfaces:**
- Consumes: a `dotnet publish -c Release` output (`DIT_RELEASE_PUBLISH_DIR`), a migrated PostgreSQL database (`DIT_RELEASE_CONNECTION`), the repository `content/`.
- Produces:
  - `PublishedAppFixture` — starts `FakeTelegramOidcServer`, then `dotnet {publish}/Trainer.Web.dll` as a child process with `ASPNETCORE_ENVIRONMENT=Testing`, `ASPNETCORE_URLS=http://localhost:5080`, the connection string, `App__ContentRoot`, `App__PublicBaseUrl=http://localhost:5080`, `App__RateLimits__WritesPerMinute=100000`, `App__RateLimits__ReadsPerMinute=100000`, `TelegramOidc__ClientId/ClientSecret/MetadataAddress`; waits for `/health/ready`; launches Chromium. Tests carry `[Trait("Category","Release")]` and `Assert.Skip` when the variables are unset.
  - `ReleaseSmokeTests` (blocking): `[Theory("ru","en")] Practice_PublishedBuild_RoundTripsRealDtos` (signed in through the fake provider: start a session, answer every kind present, outbox flushes, rows exist; one `/api/sessions/practice` request and one runtime start); `MockAndReview_PublishedBuild_Work`; `PublicPages_RequestNoFrameworkFiles` (home, catalog, question page: no `_framework/` request); `Preload_UrlsMatchRuntimeRequests` (hover the Practice card, navigate: every prefetched URL is among the runtime's requests); `Island_NoCspViolations_AllFrameworkRequestsSucceed`; `SignedInWarmVisit_FrameworkFromHttpCache` (signed in, second island visit: every `_framework/*` response comes from the browser cache); `Batch_CarriesDeviceUtcOffset` (context `TimezoneId = "Asia/Novosibirsk"`: the batch request sends `X-Client-Utc-Offset: 420`, proving `InvariantTimezone` stayed off).
  - Throttling (Planner's call: fixed stand-in for "a mid-range phone over 4G"): CDP latency 150 ms, download 9 Mbit/s, upload 1.5 Mbit/s, CPU ×4, viewport 375×812, `HasTouch`.
  - `IslandPerformanceTests`: `InitialWasmTransfer_WithinBudget` (blocking: `_framework/*` encoded bytes on a cold load ≤ 2.5 MB; Planner's call: leaves the 5 s cold target reachable at 9 Mbit/s); `PracticeInteractive_ColdAndWarm_Recorded` (median of 5 cold and 5 warm runs from `navigationStart` to `data-island-ready="true"`, compared with 5000 ms and 2000 ms; failing only with `DIT_PERF_ENFORCE=1`). PR and push runs are advisory, because shared runners are noisy; the plan is not complete until one enforced run passes (ruling K). Every number goes to `TestResults/perf/perf.json` and the job summary.
  - CI job `release` (`needs: build-test`, `permissions: contents: read`, PostgreSQL 17 service): publish, `dotnet tool restore`, `dotnet ef database update` against the service, install Playwright Chromium, run the Release category, upload `TestResults/perf/**`. A `workflow_dispatch` input `enforce` sets `DIT_PERF_ENFORCE=1`; the enforced run uses the documented runner `ubuntu-latest` (4 vCPU, GitHub-hosted, standard image), with the throttling profile above.
  - `docs/performance.md`: throttling profile, the documented runner, local commands, a table (date, commit, runner, enforced or advisory, cold median, warm median, transfer size), and the iOS Safari `interactive-widget` gap.

- [ ] **Step 1:** Write the tests → run against a local Release publish → record results.
- [ ] **Step 2:** When the budget, a smoke test or a target fails, reduce the payload or fix the cause and re-measure; never raise a threshold without the owner's decision.
- [ ] **Step 3:** Add the CI job; run it green (advisory timings); fill `docs/performance.md`.
- [ ] **Step 3a:** Trigger the job with `enforce = true` on the documented runner; it must pass (cold < 5000 ms, warm < 2000 ms, transfer ≤ 2.5 MB). Record the run URL and numbers in `docs/performance.md`. Plan 2 is not complete without this enforced green run; if it fails, go back to Step 2.
- [ ] **Step 4:** Commit `test(release): smoke-test the published build and measure island performance`.

### Task 22: Documentation consistency pass

**Files:**
- Modify: `docs/architecture.md`, `README.md`, `AGENTS.md` (corrections only; Tasks 1-21 wrote the content)
- Test: `tests/Trainer.Web.Tests/DocsTests.cs`

**Interfaces:**
- Produces:
  - `DocsTests`: `Architecture_ListsEveryApiEndpoint` (each Task 11 route appears in `architecture.md`); `Architecture_ReferencesEveryAdr` (each `docs/adr/*.md` is linked from `architecture.md`); `Adrs_HaveStatusContextDecisionConsequences`; `Agents_TestCommandsMatchCi` (the four commands in `AGENTS.md` appear in `ci.yml`); `Readme_DocumentsDevBotSetup`.

- [ ] **Step 1:** Tests → FAIL where something drifted. **Step 2:** Correct the docs. **Step 3:** Tests PASS; full check: `dotnet build -warnaserror`, the unit/integration command, CLI `validate`; Docker, E2E and Release suites green in CI.
- [ ] **Step 4:** Commit `docs: reconcile architecture, readme and agents with plan 2`.

---

## Planner's calls (spec section 12 and gaps)

| Call | Choice | Reason |
|---|---|---|
| SM-2 constants | Initial ease 2.3, min 1.3, partial −0.15 and halves the interval, lapse −0.20 and 1 day, `knew` keeps ease, 1 → 3 → ×ease, cap 365 days | Produces 1, 3, 7, 16, 37 days, the spec's "roughly 1, 3, 7, 16, 35" |
| Mock level mix | 30 % from the level below (none for Junior), rest at the level, shortfall filled from the other bucket | "That level plus a share of the level below", chosen level dominant |
| Mock timer | Off by default; on = N × 90 s, deadline-based | Typical screening pace; robust to throttled tabs |
| Mock small pool | Warning in a `Prepared` phase before Answering; suggestion `clamp(⌊P/3⌋, 5, 50)`, whole pool when P < 5 | Spec warns before start; suggestions stay valid |
| Anonymous import UI | Explicit Import / Discard on the profile, notices elsewhere | A shared browser must not import silently |
| Outbox per owner | Separate keys per owner, document format unchanged, owner header on writes | Keeps the spec 8.8 format, isolates accounts |
| Chunk size | 200 per request | Under the 500 limit, small enough to retry |
| Duplicate id with a different payload | First write wins, logged with the id only | Idempotency; no probing |
| Review-state version | PostgreSQL `xmin` | Native row version |
| Concurrency for answers | Per-user advisory lock + `xmin` check, 3 retries | Conflicts rare; optimistic check enforced |
| `answers.mock_session_id` | No foreign key | Answers precede summaries; anonymous mocks have none |
| Admin role from config | Computed, never stored; read at startup | Revocation by config + restart, no data change |
| Configured admins | Cannot be blocked from the UI | Prevents lock-out |
| Production without OIDC secrets | Startup fails | Misconfiguration |
| Preferred culture | Stored from the `lang` cookie at sign-in; restores `lang` on a new device; redirect order unchanged | Spec 8.3 order kept; no GET writes |
| Sign-in logging | Library category filtered; own sink logs outcome, Telegram id and an application-owned reason code; no audit table | No usernames, IPs or remote error text in logs |
| Answer times | Client corrects with a server-clock offset from `Date` headers at answer time; server rejects > 1 year old or > 5 min ahead, clamps ≤ 5 min ahead | Spec 8.8 bounds; history is never redated |
| Early `knew` | Metadata advances, schedule does not | Keeps the stale-answer watermark and "known" status current |
| Storage without Web Locks | Memory-only with a notice; no per-tab journal | Never an unlocked shared read-modify-write; locks exist in every targeted browser |
| Mock persistence | Questions written once; small revisioned progress document; 500 ms debounce; ordered, resumable completion handoff; second tab read-only | Cheap saves, no lost completion, one writer |
| Self-assessed eligibility | Explicit self-grade = answer; Skip = unanswered; hint alone is not a response | Open questions are answered by thinking |
| Failed summaries | Kept in a failed-items store, shown on the profile | 404 is not delivery |
| Session request method | `POST` JSON, no antiforgery | Up to 1000 excluded ids; read-only; JSON needs a preflight |
| Rate limits | `App:RateLimits`: writes 60/min per user; reads 30/min per user or IP, fallback key | Generous for a person; configurable for tests and proxies |
| Request limits | Practice 1-50 (20), Mock 5-50 (20), Review 1-50 (20), exclude ≤ 1000, body ≤ 256 KB | Small session fetches |
| Compression | Off for authenticated HTML and `/api/antiforgery`; `/api` JSON compressed | Token-bearing responses only |
| Streak days | User's offset from the last write's `X-Client-Utc-Offset`, UTC until then; full history streamed into an accumulator | Evening learners anywhere keep streaks |
| Search | `ILIKE` on title + exact tag, not cached, noindex | Small corpus; unbounded keys |
| Island parameters | Primitives + one JSON string through `TrainerJsonContext`, `Me` included | Trimming-safe, no extra request |
| Display-name fallback | Claim omitted, localised "Telegram user" in the UI | A page never mixes languages |
| Shortcuts | `shortcuts.js` with modifier and focus rules | Native activation wins; no double submit |
| Announcements | Verdict in one status region, focus to Next | Read once |
| Viewport | `interactive-widget=resizes-content` + safe-area padding | On-screen keyboard must not hide inputs |
| Perf profile | 150 ms, 9/1.5 Mbit/s, CPU ×4, 375×812; transfer ≤ 2.5 MB blocking; timings advisory on PRs, one enforced run on `ubuntu-latest` required before completion | Reproducible; noisy runners; targets still proven |
| Related questions | Shared tags, top 5 | Spec 5.1; no plan owned it |
| Testing-only metadata address | `TelegramOidc:MetadataAddress` honoured only in `Testing` | Integration, E2E and Release runs sign in through a fake provider |

## Deviations needing owner approval

Not to be executed before the owner approves each row; the ADR is written by the task named.

| ADR | Spec text | Plan choice | Reason | Task |
|---|---|---|---|---|
| 0001 | 8.1: "`Trainer.Application/` # use cases/services, interfaces (repositories, `IAnswerGrader`, clock)" | `IAnswerGrader`, `SelfAssessmentGrader` and automatic grading live in `Trainer.Domain` | 11.5 requires the WASM client to grade and "Domain grading code stays free of server dependencies so the WASM client can reference `Trainer.Domain`"; the client must not reference Application | 1 |
| 0005 | 8.8: "`answeredAt` not in the future and not older than 1 year" | Rejects older than 1 year and more than 5 minutes ahead; an `answeredAt` up to 5 minutes ahead is clamped to server now and stored | The client's calibrated clock has about one second of error from the 1 s `Date` header resolution; rejecting answers a few seconds ahead would drop real answers. The stored value is never in the future | 7 |
| 0006 | 8.1: "Application depends on Domain" | Application also references `Trainer.Contracts` | Query services return the wire DTOs; a parallel Application model would be a field-for-field copy | 8 |
| 0007 | 8.3 URL scheme (no admin URL) | `/{culture}/admin` | Admin page needs a URL; the culture prefix gives it the UI language | 10 |
| 0008 | 8.8: "written first to an outbox in `localStorage`" (one outbox) | One outbox key per owner (same document format) and the `X-Outbox-Owner` / `X-Client-Utc-Offset` headers on writes | A shared browser must never upload A's answers under B; the offset makes streak days local. The Telegram id remains in key names after sign-out (not a secret) | 11 |
| 0009 | 8.2: "Where an island does prerender, it uses `[PersistentState]` with `AllowUpdates = true` so enhanced navigation to the page ... refreshes the state" (enhanced navigation assumed) | Island pages start Blazor with enhanced navigation disabled; public ↔ island is always a full page load | Islands never prerender, so a full load is the only way to guarantee one runtime start and one session fetch per visit; theme re-apply on `enhancedload` stays as a safety net | 12 |
| 0010 | 8.3 URL scheme (no profile URL) | `/{culture}/{stack}/profile` | Statistics are per stack; reuses the culture/stack routing checks | 18 |
| 0011 | 8.4: output cache "applies only to public static pages" with query allow-list `level`, `topic`, `type`, `page`; 11.2: anonymous responses have "no form" | Catalog search adds `q`; search result pages are not cached and are noindex; the catalog gets one `<form method="get">` with no token or cookie | Unbounded key space for free text; the "no form" rule exists to avoid antiforgery cookies, which a GET form does not create | 19 |

ADRs that record decisions without contradicting spec text (no approval needed): 0002 auth cookie name `.dit.session` set through cookie options because `TelegramOidcOptions` has no option (Task 5); 0003 logout lands on the sign-in page because the library always redirects to `LoginPath` (Task 5); 0004 Data Protection key store built in Plan 2 (spec 8.7 requires it; only the plan order moves) with keys unencrypted at rest and encrypted backups in Plan 3 (Task 6).

## Self-review notes

- Coverage: spec 4 (Tasks 3, 5, 6, 10, 18); 5.1 tags (Task 19); 5.2 checking rules (Tasks 1, 15); 5.3 modes (Tasks 15-18); 5.4 (Tasks 1-2, 7); 8.2 islands, no double loads, payload, preload, performance (Tasks 12, 16, 21); 8.3 culture, full reloads, sign-in URLs (Tasks 5, 12); 8.4 signed-in bypass and personal fragments (Tasks 6, 9); 8.6 user tables with ULIDs (Tasks 3, 7); 8.7 key store (Task 6); 8.8 API, antiforgery, outbox, offline, mock completion, server checks, auth state (Tasks 7, 11-18); 9/9.1 new screens incl. sidebar and drawer (Tasks 5, 10, 15-20); 10 ADRs (per task, checked in Task 22); 11.1-11.5, 11.7 (Tasks 5, 8, 11); 12 (Planner's calls).
- Interfaces checked across tasks: `UserId` is a ULID string everywhere (Tasks 2, 3, 7, 9, 10); `IAnswerRecorder.RecordAsync(..., clientUtcOffsetMinutes, ...)` (Task 7) is fed by `OwnerCheckFilter` (Task 11) from `X-Client-Utc-Offset` sent by `TrainerApiClient` (Task 14); `answeredAt` comes from `ServerClock.Now()` (Task 14) through `QuestionAttempt` (Task 15) and is judged by `AnswerTimePolicy` (Task 7); `StorageKeys`, `OutboxStore`, `SummaryStore`, `ActiveMockStore` (Task 13) are used by Tasks 14 and 16-18; `MockProgress`/`AttemptState`/`MockCompletionState` (Task 13, in `TrainerJsonContext`) are produced by `QuestionAttempt.Snapshot` (Task 15) and `MockSession.Snapshot` (Task 17); `StreakAccumulator` (Task 2) is fed asynchronously by `ProgressQueries` (Task 9); `MockTopicResultDto.Unanswered` (Task 8) is filled by `MockSummaryBuilder` (Task 17) and validated by `ApiRequestParser` (Task 11); `MeDto` (Task 8) is built by `IProgressQueries.GetMeAsync` (Task 9) through `IslandBootstrapFactory` (Task 12); `IslandRoot`'s `data-island-ready` and `__ditStarts` (Task 12) are used in Tasks 12 and 21.
- Task 12 creates the `Practice.razor` shell so hosting is testable before Task 16 fills it. Tests that need a working mode live in the mode's task: session-fetch counts and two-tab isolation in Task 16, flush-before-Review in Task 18; owner publishing for public and sign-in pages is in Task 14, before its first use. No task depends on a later one.
