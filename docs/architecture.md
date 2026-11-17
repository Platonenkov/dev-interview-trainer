# Architecture

How Dev Interview Trainer is put together: the content pipeline, the public,
read-only site (Plan 1), and Telegram sign-in, the `/api`, the WebAssembly islands
(Practice, Mock, Review, profile) and the admin page (Plan 2), and, from Plan 3, question
feedback, Open Graph images, cookieless analytics and the deployment topology; the procedures
for running it are in [deployment.md](deployment.md). The reasons behind the design are in
`docs/superpowers/specs/2026-11-11-dev-interview-trainer-design.md`.

## Contents

1. [Layers and allowed references](#layers-and-allowed-references)
2. [Content flow](#content-flow)
3. [Reading and validating content](#reading-and-validating-content)
4. [Rendering content safely](#rendering-content-safely)
5. [Import](#import)
6. [Data model](#data-model)
7. [Review scheduling](#review-scheduling)
8. [Answer recording](#answer-recording)
9. [Session queries](#session-queries)
10. [URL scheme and routing](#url-scheme-and-routing)
11. [Page rendering rules](#page-rendering-rules)
12. [Render modes and islands](#render-modes-and-islands)
13. [Browser storage](#browser-storage)
14. [Sync, server clock and account isolation](#sync-server-clock-and-account-isolation)
15. [Question sessions and island UI](#question-sessions-and-island-ui)
16. [SEO](#seo)
17. [Output cache](#output-cache)
18. [Progress queries and personal fragments](#progress-queries-and-personal-fragments)
19. [Sign-in](#sign-in)
20. [API](#api)
21. [Security headers](#security-headers)
22. [Startup, readiness and health](#startup-readiness-and-health)
23. [Configuration](#configuration)
24. [Internal endpoints](#internal-endpoints)
25. [Question feedback](#question-feedback)
26. [Migrations policy](#migrations-policy)
27. [Snippet sandbox and the content CLI](#snippet-sandbox-and-the-content-cli)
28. [Tests and CI](#tests-and-ci)
29. [Line endings and formatting](#line-endings-and-formatting)
30. [Deployment topology](#deployment-topology)
31. [Decision records](#decision-records)

## Layers and allowed references

```text
Trainer.Domain          no references
Trainer.Contracts       no references
Trainer.Application     -> Domain, Contracts (ADR 0006)
Trainer.Content         -> Domain
Trainer.Snippets        -> Content
Trainer.Infrastructure  -> Application, Content
Trainer.Web.Client      -> Domain, Contracts
Trainer.Web             -> Infrastructure, Application, Contracts, Web.Client
tools/Trainer.ContentCli -> Content, Snippets
```

| Project | Responsibility |
|---|---|
| `Trainer.Domain` | Value types with validation: `Level`, `QuestionType`, `GradingMode`, `QuestionId` (ULID), `Slug`, `StackKey`, `TopicKey`, `CultureKey`. Grading (`Grading/`): `Grade`, `AnswerMode` and their wire keys, `AutoGrading` (single, multi, output, find-bug rules), `IAnswerGrader` and `SelfAssessmentGrader` for open and situation questions, `OutputText.Normalise` shared with the content validator (ADR 0001). Scheduling (`Scheduling/`): `IReviewScheduler` and `Sm2ReviewScheduler`, `ReviewState`, `StreakAccumulator` and `StreakCalculator`. Ids (`Ids/`): `UlidText` (regex-free validation and encoding), `UlidGenerator`, `ClientAnswerId`, `MockSessionId`; `Users/`: `UserId`, `Roles`. No IO, no framework dependencies. |
| `Trainer.Contracts` | The wire DTOs shared by the server and the WebAssembly client: `QuestionDto`, the session requests and responses, `AnswerBatch` (also the outbox document), mock and profile DTOs, `ApiErrorDto` with `ApiErrorCodes`, the island bootstrap. Plain records over string keys, serialised through the source-generated `TrainerJsonContext` (camelCase, trimming-safe); no references. |
| `Trainer.Application` | Use-case interfaces and result types: `IContentImportService`, `IContentCacheInvalidator`, the query interfaces for catalog, home, question, topic, stack and sitemap. Answers (`Answers/`): `IAnswerRecorder`, `ReportedAnswer`, `AnswerBatchResult`, `AnswerRejections` and the pure `AnswerTimePolicy` (ADR 0005); mock interviews (`Mock/`): `IMockSessionService`, `MockSummary`; sessions (`Sessions/`): `ISessionQueries` (returns the wire DTOs, ADR 0006), the criteria records, `SessionLimits` and `MockComposer`. Admin (`Admin/`): `IAdminQueries`, `AdminOverview`, `ImportRow`, `AdminUserRow`, `UserPage`, `AdminLimits`. |
| `Trainer.Content` | The content format: model records, strict reader, JSON schema generator, semantic validator, Markdown and code renderer. Shared by the app and the CLI. |
| `Trainer.Snippets` | Compiles and runs snippets in Docker containers and compares the results with the declared expectations. |
| `Trainer.Infrastructure` | EF Core with PostgreSQL, migrations, the import service, the query implementations, user accounts, the answer recorder and the mock session service. |
| `Trainer.Web.Client` | The WebAssembly islands (`Microsoft.NET.Sdk.BlazorWebAssembly`, trimmed with `TrimMode` `full` and the trim analyzers on): `IslandRoot`, the island components (`Islands/`), the client strings (`Localization/ClientResource.{ru,en}.resx`). The server names the island components only in its render markers, so `Islands/IslandRoots.cs` keeps them through the trimmer with `[DynamicDependency]` (a new island must be added there; `IslandRootsTests` compares the entries with every component in `Islands/` and every server `@rendermode` use, and the `Release` suite boots all four islands). Never references Application, Infrastructure or Content. |
| `Trainer.Web` | Blazor Web App host: static SSR pages, routing, SEO, output cache, security headers, health checks, and the `/api` minimal API the islands call (`Api/`). |
| `tools/Trainer.ContentCli` | `validate`, `run-snippets`, `check-links`, `new-question`, `generate-schemas`. |

Rules:

- Dependencies point inward. Domain references nothing.
- **`Trainer.Web` never references `Trainer.Snippets`.** Untrusted snippet code
  must not be reachable from the web process. Only the CLI and CI run it.
- No business logic in Razor components or endpoints; they call Application
  interfaces. Services are registered in DI, not static helpers with logic.
- `Trainer.Contracts` holds the DTOs shared with the WebAssembly client; Application
  references it so the session queries return them directly (ADR 0006).
  `Trainer.Web.Client` sees only Domain and Contracts, so nothing server-side can
  reach the browser runtime.

## Content flow

```text
content/*.yaml
  -> ContentReader        strict YAML -> typed documents, content hash
  -> ContentValidator     semantic rules, issue codes
  -> ContentRenderer      Markdown and code -> safe HTML, once, at import
  -> ContentImportService one transaction, advisory lock, retire removed questions
  -> PostgreSQL           questions, question_locales, topics, stacks, redirects, content_imports
  -> query services       Application interfaces implemented in Infrastructure
  -> Razor pages          static SSR, no cookies for anonymous visitors
  -> output cache         keyed by path, content generation and canonical query
```

The content directory is not part of the application image. The app reads
`App:ContentRoot` at startup, so a content fix does not need an image rebuild.
The CLI runs the first three stages in CI without a database.

Production content is delivered by `deploy-content.yml` through `deploy/remote/receive-content.sh`
(see `## Tests and CI`). Each commit becomes an immutable release `content/releases/<sha>` on the
server and `content/current` points at the live one; the running application imports it through
`POST /internal/content/import`, whose answer (200 or 422) is the only source of truth for the
outcome. A transport failure or a 5xx is not an answer: the script calls the endpoint once more, and
the second call queues on the importer's advisory lock, which is taken before the files are read, behind
an import that is still running. Two non-answers end the delivery with exit 6 and leave `.import-pending`
(the release sha and the release `current` pointed to before); the next delivery settles it first by
importing what `current` points to. A 422, or a 401, 403 or 404 (the endpoint refused the call, so no
import started), switches `current` back to the previous release, or removes it on a first delivery,
which is the fresh-install state. The importer's advisory lock key is `0x4449_5443_4F4E_5431`; pg_locks reports it
as `classid = 0x44495443`, `objid = 0x4F4E5431`, `objsubid = 1`.

## Reading and validating content

`ContentReader` (`Trainer.Content/Reading`) reads a content root:
`schema-version`, `stacks.yaml`, and for each stack directory `topics.yaml`,
optional `redirects.yaml` and the question files. It never stops at the first bad
file; every problem becomes a `ContentIssue(RelativePath, Code, Message, JsonPath)`.

How a file is parsed:

1. Bytes are normalised: a leading UTF-8 BOM is removed and CRLF becomes LF.
   The same normalisation feeds the hash, so line endings and BOMs do not change
   it.
2. The text must be strict UTF-8, one YAML document, with no anchors, aliases,
   merge keys or tags.
3. `YamlToJson` converts the YAML tree to a JSON tree with narrow scalar typing.
   Plain `true`/`false` are booleans, empty, `null` and `~` are null, whole
   numbers are integers, and everything else (`012`, `1.5`, `2026-11-11`) stays
   text. Quoted scalars are always text. Nesting is limited to 64 levels.
4. `System.Text.Json` deserialises the tree into immutable records through the
   source-generated `ContentJsonContext`: camelCase names, unknown members are
   errors, required members must be present, nulls in non-nullable members are
   errors.

The content hash is SHA-256 over the sorted files. Each file contributes its
relative path and its normalised bytes, each preceded by its 64-bit length, so
moving bytes across a file boundary always changes the hash. The render version (`ContentSchema.RenderVersion`) is not in the
hash; the importer compares the pair.

`ContentValidator` is stateless and takes `IContentRenderer` and `TimeProvider`.
It runs one private method per concern: schema version, stacks, topics, identity
(ULID and slug uniqueness), placement (path matches fields), classification,
tags, options, output expectations, code and harness, bug lines, links, locales,
Markdown link targets, redirects and the verified date. Every violation is
collected. The codes are listed in `docs/content-guide.md`.

`ContentVocabulary` holds the closed vocabularies (harnesses, expectation kinds,
link kinds) and key patterns. `SchemaGenerator` and the validator read the same
values, so editors and the build cannot disagree. The JSON schemas in
`content/schema/` are generated from the C# model; a test fails if they drift.

Supported content schema versions: 1 to 1 (`ContentSchema.MinSupported`,
`MaxSupported`). The reader reports `schema-version-unsupported` outside the
range.

## Rendering content safely

Content arrives through public pull requests, so every text field is untrusted.

- Markdown goes through one explicit Markdig pipeline
  (`MarkdownPipelineFactory`): pipe tables, extra emphasis, extra lists, auto
  identifiers, `DisableHtml()`. There are no generic attributes, media embeds,
  auto-links, math or diagrams.
- `SafeLinkPolicy` decides which link and image targets may be rendered: an
  absolute `https:` URL with a host, or a site-relative path that does not start
  with `//` or `/\`. It rejects control characters, whitespace, backslashes and
  entity-like sequences, and re-checks every percent-decoded and HTML-decoded
  form (bounded to four rounds), so `/%2Fevil.com` cannot become `//evil.com`.
- Unsafe links are unwrapped to their text at render time and reported by the
  validator as `markdown-link-scheme`, so a bad link fails CI first.
- External links from content (Markdown body and the `links` list) get `rel="nofollow noopener noreferrer"`.
- Tables are wrapped in `div.table-scroll`.
- Code is HTML-encoded, highlighted on the server with CSS classes (no inline
  styles), and every line is wrapped in its own `span`, which keeps multi-line
  tokens balanced and allows line selection later.
- Table alignment is moved from Markdig's inline `style` attribute to a class,
  because the CSP forbids inline styles.
- Markdown is rendered once, during import, and stored as HTML per locale. Pages
  emit it as `MarkupString`; this is safe only because of the rules above.
- JSON-LD is written with `Utf8JsonWriter` and the default JavaScript encoder, so
  a title containing `</script>` cannot close the script element.

## Import

`ContentImportService.ImportAsync(contentRoot)` reads, validates and stores the
content in one transaction (the advisory lock is the first statement).

1. Begin a transaction and take `pg_advisory_xact_lock`, which serialises
   concurrent imports from any process.
2. Read the content. The lock is taken first on purpose: a second import queues
   behind one that is still reading. `ContentReader` resolves the link target of
   the content root once, at the start of the read, and reads every file from
   that release directory, so a deployment that switches `content/current` while
   an import is reading cannot mix two releases; the next, queued import picks
   up the new one.
3. If the active `content_imports` row has the same hash and render version,
   return `Unchanged`.
4. Collect issues: read issues, validator issues, and, only when those are
   empty, slug checks against the database:
   `slug-changed` (same `id`, new slug, no redirect) and `slug-reused` (the slug
   belongs to another question). Content that was accepted before (its hash is
   in `content_imports`) skips the rename check, so rolling back A -> B -> A
   works.
5. Any issue returns `Rejected`: the transaction is rolled back and the
   previous content stays active.
6. Otherwise upsert taxonomy, redirects and questions, mark questions whose files
   are gone as retired (topics and stacks that are gone are retired the same way,
   and one that comes back is revived), mark the new import active, and commit.
7. After the commit, call `IContentCacheInvalidator`. The web host publishes a new
   `ContentSnapshot` generation first and then evicts the output cache.
   A failure in the invalidator is logged at error level and does not change the
   result: the content is committed, so the import still returns `Imported`.
   Cancellation through the caller's own token is the exception: it propagates.

A unique-index violation that slips past the pre-checks is caught and returned as
`Rejected` with `db-unique-violation`, so the host does not end up without
content.

Question text is rendered during step 6. The language-neutral part (code,
options, expectations) is stored in `questions.payload` (JSONB) and the
localised HTML in `question_locales`. Code is rendered twice from the same source: `codeHtml`
(`IContentRenderer.RenderCode`, the question page) and `codeLinesHtml`
(`RenderCodeLines`, one self-contained `span.line` per source line, so the practice island can
mark lines). Joining the lines with a newline gives the body of `codeHtml`. The payload gained
`codeLinesHtml` at render version 3, so the first import after the upgrade re-renders everything.

## Data model

| Table | Content |
|---|---|
| `stacks`, `stack_locales` | Stacks and their localised titles. `stacks.retired_at` is set when a stack disappears from the content. |
| `topics`, `topic_locales` | Topics per stack and their localised titles and descriptions. `topics.retired_at` is set when a topic disappears from the content. |
| `questions` | One row per question: `id` (ULID), stack, topic, level, type, slug, tags, language-neutral `payload` JSONB, verified date, `retired_at`. Unique on (stack, slug). |
| `question_locales` | One row per question and culture: title, prompt, hint, answer, explanation (HTML), options and links (JSONB). **A question is published in a culture if and only if this row exists.** The catalog, sitemap and `hreflang` all query this table. |
| `redirects` | Old to new slug mappings per stack (`from_slug`, `to_slug`; the topic part of a redirect path is dropped because slugs are unique per stack). |
| `content_imports` | Hash, schema and render versions, import time, question count, and an `is_active` flag with a partial unique index so that exactly one import is active. |
| `users` | One row per signed-in person: `id` (ULID, `char(26)`), unique `telegram_user_id` (`bigint`, never the key), `display_name`, `username`, `created_at`, `last_sign_in_at`, `is_blocked`, `security_stamp` (32 hex characters, rotated on block and role changes), `preferred_culture`, `utc_offset_minutes`. Sign-in is a single `INSERT ... ON CONFLICT (telegram_user_id) DO UPDATE`, so parallel first sign-ins create one user. |
| `user_roles` | Stored roles per user (`user_id`, `role`; deleted with the user). The Admin role of a configured Telegram id (`App:AdminTelegramIds`) is computed at read time and never stored (`SetRolesAsync` rejects it), so removing an id and restarting revokes it. |
| `data_protection_keys` | The ASP.NET Core data protection key ring (`id`, `friendly_name`, `xml`), written by the framework, not by application code. Unencrypted at rest (ADR 0004). |
| `answers` | Append-only answer history: `id` (server ULID), unique `client_answer_id` (client ULID, makes retries idempotent), `user_id` (deleted with the user), `question_id` (questions are retired, never deleted), `grade`, `used_hint`, `mode`, `mock_session_id` (no foreign key: answers usually arrive before the mock summary, and anonymous mocks never send one), `answered_at` (client time after the time policy), `received_at`. Indexed on (`user_id`, `answered_at`); `ix_answers_question_id` is EF's convention index for the foreign key (questions are never deleted, so no FK check needs it; it is kept for per-question statistics, at the cost of one more index on every insert). |
| `review_states` | One row per user and question (composite key): `due_at`, `interval_days`, `ease` (`numeric(4,2)`), `repetitions`, `lapses`, `last_grade`, `last_answered_at`. PostgreSQL's `xmin` system column is the optimistic concurrency token (the "version" of spec 8.6; nothing to maintain). Indexed on (`user_id`, `due_at`); the foreign-key index on `question_id` is EF's convention, kept for the same reason. |
| `mock_sessions` | One row per finished mock interview: `id` (client ULID), `user_id` (deleted with the user), `stack_key`, `level`, `question_count`, `timed`, `started_at`, `finished_at`, and `summary` JSONB with the per-topic counts (`{"topics":[{"topic","knew","partial","didntKnow","unanswered"}]}`). |
| `question_ratings` | One row per user and question (composite key): `value` (`smallint`, `1` like, `-1` dislike), `updated_at`. `user_id` is deleted with the user; `question_id` references questions, which are retired, never deleted. A cleared rating is a deleted row. Indexed on `question_id` for per-question totals. |
| `question_reports` | One row per report: `id` (server ULID), `user_id` (deleted with the user), `question_id`, `culture` (the page the report came from), `reason` (`smallint`), `comment` (nullable text, at most 1000 characters), `created_at`, `resolved_at` and `resolved_by` (both null while open; `resolved_by` is cleared if the admin account is deleted). Indexed on (`user_id`, `created_at`) for the quota and on (`resolved_at`, `created_at` descending) for the admin list. |

Level, type, grading, grade, answer mode, rating and report reason are stored as `smallint` with pinned numeric values (`PersistedEnumValuesTests` guards them).

## Review scheduling

`Sm2ReviewScheduler` is a pure function (`IReviewScheduler.Apply(current, grade, answeredAt)`) over
`ReviewState`: the schedule (`DueAt`, `IntervalDays`, `Ease`, `Repetitions`, `Lapses`) plus the latest
answer (`LastGrade`, `LastAnsweredAt`). The first answer to a question creates its state from
`(interval 0, ease 2.3, repetitions 0, lapses 0)`. Constants are in `Sm2Constants`.

Rules, applied in this order:

1. **Stale answer.** If `answeredAt <= LastAnsweredAt`, the current state is returned unchanged. For
   equal timestamps the answer applied first wins; the recorder applies a batch ordered by
   (`AnsweredAt`, `ClientAnswerId`).
2. **Normal step.**
   - `knew`: repetitions 0 gives 1 day, repetitions 1 gives 3 days, otherwise
     `max(interval + 1, round(interval x ease))` (half away from zero); repetitions + 1; ease unchanged.
     From a first answer the intervals run 1, 3, 7, 16, 37, 85, 196, 365 days.
   - `partial`: interval `max(1, round(interval x 0.5))`; ease - 0.15; repetitions unchanged.
   - `didnt-know`: interval 1 day, repetitions 0, lapses + 1, ease - 0.20.
   - Ease never drops below 1.3, the interval is capped at 365 days, `DueAt = answeredAt + interval`.
3. **Question not yet due** (`answeredAt < DueAt`). `knew` leaves the schedule unchanged and only
   advances `LastGrade` and `LastAnsweredAt`, so a delayed older failure becomes stale and the
   "known" status is current. `partial` and `didnt-know` take the normal step, but `DueAt` becomes
   `min(current DueAt, new DueAt)`: an answer to a question that is not yet due can only bring the
   review closer, and the interval, ease, repetitions and lapses reset is never lost.

The streak is the number of consecutive local days with at least one answer, ending today or
yesterday. `StreakAccumulator` is fed distinct days newest first and returns `false` at the first gap,
so the infrastructure stops reading and any streak length is counted without a window.

## Answer recording

Clients send answers as events with client-generated ids (spec 8.8). `AnswerRecorder`
(`IAnswerRecorder.RecordAsync(userId, answers, clientUtcOffsetMinutes, ct)`) records one batch of at most
`IAnswerRecorder.MaxBatchSize` (500) answers:

1. Duplicate client ids inside the batch collapse to the first one.
2. One transaction per batch, serialised per user by
   `pg_advisory_xact_lock(hashtextextended('dit-answers:' || user_id, 0))`; a hash collision only serialises
   two users' batches.
3. Ids already in `answers` are accepted with no other effect, whoever owns them and whatever the payload: the
   first write wins. A different payload is logged at Warning with the client id only. Reporting another user's
   id as accepted reveals nothing; refusing it would let a client probe ids.
4. Only new ids are checked: `AnswerTimePolicy` (below) rejects `answered-too-old` and `answered-in-future`,
   then one query rejects `question-unknown` and `question-retired` (the question, its topic or its stack is
   retired).
5. A `clientUtcOffsetMinutes` within ±`IAnswerRecorder.MaxUtcOffsetMinutes` (840) is stored in
   `users.utc_offset_minutes` for streak days; anything else is ignored.
6. New answers are inserted (`id` from `UlidGenerator`, `received_at` = server now) and applied to the tracked
   `review_states` through `IReviewScheduler` ordered by the stored `answered_at` (after clamping), then the
   `answeredAt` the client reported, then `client_answer_id`, and saved and committed. The scheduler ignores an
   answer that is not newer than the last one applied, so the order decides ties: of two answers clamped to the
   same server now, the one the client reported earlier is applied first; the id only breaks exact ties.
7. A write conflict rolls back, clears the change tracker and retries the whole batch, up to 3 times; then the
   exception propagates. Conflicts are a `DbUpdateConcurrencyException` (stale `xmin`), a unique violation
   (23505), a deadlock (40P01) or a serialization failure (40001), found anywhere in the exception chain: no
   retrying execution strategy is configured, and Npgsql's default one only wraps the transient states in an
   `InvalidOperationException`. The lock makes conflicts rare; `xmin` still guards every review-state write.

The result lists every distinct id once, accepted or rejected with a reason from `AnswerRejections`
(`invalid` is added by the endpoint parser). Rejected answers are never stored; the client drops both kinds
from its outbox.

**Timestamps (ADR 0005).** The client stamps `answeredAt` with its server-calibrated clock when the answer is
given. `AnswerTimePolicy.Judge(answeredAt, serverNow)` returns `TooOld` before `serverNow − 365 days`,
`InFuture` after `serverNow + 5 minutes`, `Clamp` in between `serverNow` and that tolerance (stored as
`serverNow`), and `Accept` otherwise. Only the server's clock is shifted, so `DateTimeOffset.MinValue` and
`MaxValue` cannot overflow. The stored time is what the scheduler uses, so an answer given before a due date
and delivered after it still counts as not yet due. Times are stored in UTC at microsecond precision.

**Mock sessions.** `MockSessionService.SaveAsync(userId, id, summary, ct)` judges `StartedAt` and `FinishedAt`
with the same policy (`Invalid` when rejected or when the session finishes before it starts, clamped
otherwise) and upserts by id: `Updated` for the caller's own session, `Created` for a new id, `NotFound`
when the id belongs to another user, so the caller learns nothing about that session (a free id still
answers 204; guessing a live ULID is impractical, not impossible to detect).

## Session queries

`ISessionQueries` (`Trainer.Infrastructure/Sessions/SessionQueries.cs`) loads a whole session up front, so answering works
offline once it has started. It offers only live questions (`LiveQuestions.Of`: not retired, in a live stack and topic) that
have a locale for the culture. Every method runs a constant number of commands (the pool count, one query with the
questions, their locale and payload, and the topic titles), so a session of 50 costs the same as one of 1.

| Query | Behaviour |
|---|---|
| Practice | Random order (`EF.Functions.Random()`), `Count` 1..50. Empty level, topic and type lists do not restrict. `New` is "no review state" for a signed-in user and "not answered in this browser" for an anonymous one, which the client sends in `Exclude`; `Exclude` (at most 1000 ids) leaves its questions out in every status, so "New session" can skip the questions just seen; `Due` is `due_at <= now` and matches nothing without a user. The first question, when it is live in the stack and culture, comes first whatever the filters say, appears once and counts toward `Count` and `PoolSize`. `PoolSize` is the number of matches before `Count`. |
| Mock | `Count` 5..50. Pools at the chosen level and the level below, in the chosen topics. `MockComposer` takes `round(count x 0.3)` from the level below (none for Junior), the rest at the level, fills a short bucket from the other and shuffles; a pool smaller than the count yields the whole pool. `RepeatsLikely` is `poolSize < 3 x count`. `SuggestedCount` is `clamp(poolSize / 3, 5, 50)` for pools of 5 or more, the whole pool below 5 and 0 for an empty pool, so for pools of 0-4 it lies outside the mock count range (a request with it would be clamped to 5). The client offers "Use N" only when N is a valid count (5 or more) below the requested one (`MockSettings.OffersSuggestion`); for a pool of 1-4 the session already takes the whole pool, so there is nothing to suggest. |
| Review | The user's review states with `due_at <= now`, most overdue first (ties by id), `Count` 1..50, skipping retired questions and those without the culture's locale. |

Questions leave as `QuestionDto`: keys instead of enums, option correctness included (the client grades), the per-line code
and accepted outputs and bug line sets for the client graders. A count outside the limits throws
`ArgumentOutOfRangeException`; the API validates it first.

## URL scheme and routing

| Path | Page |
|---|---|
| `/` | 302 to `/{culture}`. Culture comes from the `lang` cookie, then `Accept-Language`, then `en`. The response varies on `Accept-Language, Cookie`. |
| `/lang/{culture}?return={path}` | Explicit language choice: sets the `lang` cookie (`Path=/`, 365 days, `SameSite=Lax`, `HttpOnly`, `Secure` over HTTPS) and answers 302 to `return`. `return` must be a site-relative path equal to `/{culture}` or inside it, without `//`, `\` or control characters; anything else goes to `/{culture}`. An unsupported culture is 404. The response is `no-store`, `X-Robots-Tag: noindex`, never output-cached, and `/lang/` is disallowed in `robots.txt`. |
| `/{culture}` | Home of the default stack (`App:DefaultStack`). |
| `/{culture}/{stack}` | Stack home. Its canonical URL for the default stack is `/{culture}`. |
| `/{culture}/{stack}/questions` | Catalog. Query: `level`, `topic`, `type`, `page`, and the search term `q` (ADR 0011): at most 100 characters, whitespace collapsed, matched as a case-insensitive substring of the title in the culture or as an exact tag (spaces become hyphens), LIKE wildcards escaped. A request with a term is `noindex` and never output-cached; the search box is a `GET` form without token or cookie. |
| `/{culture}/{stack}/topics/{topic}` | Topic page. |
| `/{culture}/{stack}/questions/{slug}` | Question page. Query `feedback` (a notice code after a feedback form post, see [Question feedback](#question-feedback)); it is read only for a signed-in request and is no part of the output-cache key. A `POST` to this URL is a feedback form handler (`question-rating`, `question-report`), which exists only in a signed-in render. |
| `/{culture}/about` | About the project. |
| `/signin` | Culture-neutral sign-in path (the library's `LoginPath` and the cookie handler's target). 302 to `/{culture}/signin`, keeping `denied=1` / `error=1` and the return path (`returnUrl` or `ReturnUrl`) validated like `/lang/`. The culture comes from the return path, then the `lang` cookie, then `Accept-Language`. `no-store`, `X-Robots-Tag: noindex`. |
| `/{culture}/signin` | Sign-in page; `noindex`, never output-cached. |
| `/{culture}/{stack}/practice` | Practice island page (`[InteractiveIslandPage]`). Query `level`, `topic`, `type`, normalised like the catalog's, plus `status` (`all`, `new`, `due`) and `first` (a question id: the session starts at once with it first); unknown values are dropped. `{culture}` is constrained to two letters so `POST /api/sessions/practice` never matches it. `noindex`, never output-cached, never compressed, disallowed in `robots.txt` (as are `/mock`, `/review` and `/profile`). See [Render modes and islands](#render-modes-and-islands). |
| `/{culture}/{stack}/mock` | Mock island page (`[InteractiveIslandPage]`). Query `level` and `topic`, normalised like the catalog's (the first level preselects the setup form; types are dropped, a mock mixes them). Same culture constraint and the same rules as Practice: `noindex`, never output-cached, never compressed. See [Mock island](#mock-island). |
| `/{culture}/{stack}/review` | Review island page (`[InteractiveIslandPage]`, `[Authorize]`): anonymous requests get 302 to `/signin?ReturnUrl=...`. No query. Same culture constraint and island rules as Practice. See [Review island](#review-island). |
| `/{culture}/{stack}/profile` | Profile island page (ADR 0010; `[InteractiveIslandPage]`, `[Authorize]`): statistics per stack and the anonymous import (`#import`). No query. Same rules as Review. See [Profile and anonymous import](#profile-and-anonymous-import). |
| `/{culture}/admin` | Admin page (ADR 0007): policy `Admin` (anonymous: 302 to `/signin?ReturnUrl=...`; signed in without the role: 302 to `/signin?denied=1&ReturnUrl=...`). Shows readiness, database reachability, the last 10 content imports and a user list (50 per page, GET search `q` by Telegram id or a case-insensitive name/username substring under the ICU collation `und-x-icu`, as the catalog search, `page`). Each row has a `POST` form (`block-{userId}`, hidden `userId` and `blocked`) with an antiforgery token: 400 without one. Also lists feedback reports (`reports`, `reportsPage`, a resolve button per open row) and the most disliked questions, see [Question feedback](#question-feedback). A configured admin cannot be blocked (`ProtectedAdmin` message). `noindex`, never output-cached, `private, no-store`, not compressed, not in `robots.txt`. |
| `/signin-telegram/challenge?returnUrl=` | Starts the Telegram OIDC flow; 503 while `TelegramOidc:ClientId` or `ClientSecret` is empty. |
| `/signin-telegram` | OIDC callback, handled by the authentication middleware. |
| `/signin-telegram/logout` | `POST` with an antiforgery token: signs out and answers 302 to `/signin` (ADR 0003); 400 without a valid token. |
| `/sitemap.xml`, `/sitemap-{lang}.xml`, `/robots.txt` | Sitemap index, one sitemap per culture, robots. |
| `/og/{lang}/{stackKey}/{slug}/{fingerprint}.png`, `/og/{lang}/site/{fingerprint}.png` | Open Graph card, see [SEO](#seo) and ADR 0017. The route values are deliberately not named `culture` or `stack`, which `CultureAndStackMiddleware` judges. Every miss is an empty 404. |
| `/health/live`, `/health/ready` | Liveness and readiness. |
| `/not-found` | Target of the status-code re-execution; `noindex`. A re-executed request is rendered as `GET` (`StatusPageAsGetMiddleware`), so a `POST` to an unknown path is 404 too, not 400 for a missing antiforgery token. |
| `/api/...` | The JSON API of the islands, see [API](#api). Never re-executed into the not-found page. |

Pipeline order (`Program.cs`): forwarded headers, security headers, response
compression, status code pages with re-execution, `StatusPageAsGetMiddleware`,
`ApiErrorMiddleware` (only for `/api`), routing, `PrivateResponseMiddleware`,
authentication, `BreachGuardMiddleware`, `CultureAndStackMiddleware`, request
localisation, authorization, rate limiter, output cache, antiforgery, endpoints. Authentication runs before the culture check because the
OIDC callback `/signin-telegram` also matches the home page route `/{culture}`;
the output cache runs after authentication so it sees the signed-in user.

- `CultureAndStackMiddleware` answers 404 for an unsupported culture or an
  unknown stack. It reads only `ContentSnapshotProvider`, so a cache hit never
  touches the database. Routing runs before localisation so that
  `RouteCultureProvider` sees the route culture.
- `ContentSnapshot` is an immutable view of which stacks and topics exist, with a
  `Generation` number that grows on every reload.
- Unknown slugs return a real 404. A redirect entry returns 301 to the new slug.
  A retired question keeps its page with `noindex`; once a redirect points away
  from it, the redirect wins.
- A retired stack is never deleted either. It leaves the routing snapshot (so
  `/{culture}/{stack}` answers 404), the sitemap and every query; its topics are
  retired with it.
- A retired topic is never deleted, so a rollback can restore it. Its page answers
  404, and it is left out of the routing snapshot (the catalog ignores it as a
  filter), the catalog and home queries, and the sitemap. A retired question
  whose topic is retired shows the topic as plain text and drops the back link
  and the JSON-LD breadcrumb item, so no link points to the 404 page.
- A question that exists in one language only: the page in the other culture
  returns 404, the catalog in that culture omits it, and `hreflang` lists only
  the existing culture plus `x-default`.
- The language switcher links to `/lang/{target}?return={canonical path and query, URL-encoded}`
  with `rel="nofollow"`, so choosing a language is the only action that sets a
  cookie and public pages stay cookie-free. The `hreflang` alternates in the head
  stay canonical URLs.
- Catalog queries are normalised by `CatalogQueryParser`: case, order, repeats,
  unknown values and tracking parameters collapse to one canonical query string
  (`page` is clamped to 1..1000). The page size is 25. The search term `q`
  (`ParseSearch`) is trimmed, has whitespace and control characters collapsed and
  is cut to 100 characters; it is appended last and an empty term is dropped.
  `CatalogQueries` applies it to the live set before the facets are derived, as
  `ILIKE` on the culture's title under the explicit ICU collation `und-x-icu` (bound
  parameter, `%`, `_` and `\` escaped; the collation makes Cyrillic case folding independent of the
  database's `LC_CTYPE`, and needs a PostgreSQL built with ICU, as the official images are) or an
  exact tag match, so the facet counts honour it (ADR 0011).
- The question page lists up to five related questions
  (`IQuestionQueries.GetRelatedAsync`): live questions of the stack in the culture
  that share a tag, ordered by the number of shared tags, then the same topic, then
  the title. The question itself, retired questions, topics and stacks and
  questions without a locale for the culture are left out. A retired question's own page
  shows no related list.

## Page rendering rules

- Public, indexable pages (home, catalog, topic, question, about) are static SSR
  only: no interactive components, no `[StreamRendering]`, no `blazor.web.js`,
  no POST `<form>` or antiforgery token, and no `Set-Cookie` in anonymous
  responses. The catalog's search box is the one `<form method="get">` (ADR 0011).
  Tests enforce the form and cookie rules because the output cache depends on them.
- The question page is the one public page that also holds POST forms, and only in a
  signed-in render: the rate and report forms of `QuestionFeedbackForms` (ADR 0016). The
  page renders them after `HttpContext.User` is known to be signed in, and a signed-in
  request is never served from or stored in the output cache, so the cached anonymous HTML
  stays free of forms, tokens and cookies (`QuestionFeedbackPageTests` checks the anonymous
  page, and that it is still a cache hit after a signed-in post).
- The header's account controls follow the same rule: anonymous visitors get a
  plain "Sign in" link whose `returnUrl` is the page's canonical path from
  `PageLinks`, never the raw request, so cached HTML is the same for everyone.
  Only signed-in responses carry the sign-out `<form method="post">` with its
  antiforgery token (cookie `.dit.af`).
- The answer and explanation are in the HTML, inside `<details>`, so crawlers
  and users without JavaScript see them.
- Themes: light and dark come from one set of CSS custom properties
  (`wwwroot/css/tokens.css`). A small inline script sets `data-theme` before the
  first paint, from `localStorage` or `prefers-color-scheme`. The server never
  needs the theme, so cached pages are theme-independent.
- Every text and background token pair meets WCAG 2.2 AA (4.5:1 for body text,
  3:1 for large text and UI boundaries); `ThemeTokenContrastTests` checks it.
- Layout is mobile-first and checked at 375, 768, 1280 and 1920 px, with no
  horizontal page scroll down to 320 px. Code panels scroll inside themselves.
- UI strings are `.resx` resources (`Localization/Resources`) used through
  `IStringLocalizer<SharedResource>`. A page never mixes languages. Adding a
  language means new resources, content locale blocks and one entry in
  `CultureKey.Supported`.

## Render modes and islands

Two render modes exist and nothing else: static SSR for every page, and
`InteractiveWebAssembly` without prerendering (`IslandRenderMode.NoPrerender`)
for the islands inside the noindex pages `/{culture}/{stack}/practice`, `/mock`,
`/review` and `/profile`. `AddInteractiveServerComponents` is never called.

- A page that hosts an island carries `[InteractiveIslandPage]`. The attribute is
  in the endpoint metadata, and `App.razor` renders only for such pages:
  `<base href="/">`, the viewport with `interactive-widget=resizes-content` (the
  on-screen keyboard resizes the layout instead of covering inputs; iOS Safari
  ignores it, so `islands.css` also pads with `env(safe-area-inset-bottom)`),
  `css/islands.css`, `<ResourcePreloader />`, `js/outbox.js` (browser storage,
  below), `js/shortcuts.js` (keyboard shortcuts, below), `js/page-lifecycle.js`
  (tells the Mock island the page is being hidden, see [Mock island](#mock-island)),
  `_framework/blazor.web.js` with `autostart="false"` and
  `js/islands-start.js`, and `<ImportMap />` (ADR 0012). The import map
  translates the plain module name `blazor.web.js` imports
  (`./_framework/dotnet.js`) to the fingerprinted file the preload fetched;
  without it the runtime is downloaded twice. It is an inline script, allowed by
  a per-request nonce (`CspNonce`, a scoped service) that the island page CSP
  names as `'nonce-{value}'`; public pages carry neither.
- `IslandEndpointConventions.KeepResourceCollectionOnServer` removes the
  resource collection URL from the page endpoints, so the WebAssembly runtime does
  not import and deserialise the site's whole asset list before the first island
  renders (the islands never call `Assets[...]`; the server and the import map keep
  using the collection).
- `IslandEndpointConventions.KeepRenderModesOnIslandPagesOnly` gives every other
  page an empty set of configured render modes. With one configured mode the
  framework would persist component state, including a new antiforgery token and
  its cookie, on every page, which would break the cookie-free public pages.
- `islands-start.js` calls `Blazor.start` with enhanced navigation off
  (`ssr.disableDomPreservation`) and `webAssembly.applicationCulture` from
  `<html lang>`. The client runs with invariant globalization (ADR 0013), under
  which the runtime ignores `applicationCulture`, so `PageCulture.Apply` sets the
  named culture from `<html lang>` before the host starts (the host then loads that
  culture's satellite resources), plural forms and links take the language from the
  culture name (`UiLanguage`), and `LocalTime` formats dates through the browser's
  `Intl` (`window.ditFormatTime`, a synchronous `[JSImport]`). Every navigation from or to an island page is a full page load,
  so each island document starts one runtime (ADR 0009). The culture is fixed for
  the runtime's lifetime; the language switcher's links carry
  `data-enhance-nav="false"` and reload into the other culture.
- The theme survives: the inline head script defines `window.ditApplyTheme`, and
  `islands-start.js` re-applies it after start if the attribute is missing and on
  `enhancedload`. `theme-toggle.js` uses one delegated `document` click handler.
- `<base href="/">` makes the runtime, the preloads and the JS initializers find
  `/_framework/` from any page path. Under it a bare `#fragment` link would leave
  an island page, so the layout writes the skip link of island pages with the
  request's own path (island pages are never cached, so that is safe).
- The page renders a static `<div class="island" data-island="...">` with a
  fixed-height `.island-skeleton` and the island component. The island renders
  `IslandRoot` (`.island-root`), and `.island:has(.island-root) > .island-skeleton`
  hides the skeleton. After its first render `IslandRoot` calls, once per
  runtime, `window.ditCountRuntimeStart` (a `[JSImport]`), which increments
  `window.__ditStarts`. It sets `data-island-ready="true"` after the first render
  in which the island reports `Usable` (it has left its loading phase and shows
  its form, a question or a state with its action), and keeps it; tests wait for
  it and `docs/performance.md` times it as "interactive". With `?perf=1`,
  `islands-start.js` then shows a small `.perf-readout` (milliseconds since
  navigation start, `_framework/` bytes from the Resource Timing API) for
  measurements on a real phone. It also exposes `data-island-culture` and
  `data-island-signed-in`, and wraps the island in an `ErrorBoundary` with a
  localised message and a reload link. Above the boundary it renders the shell's
  notices, the same for every island: `AccountChangedNotice`, `StorageNotice`
  and `SyncStatus`.
- Island parameters are primitives plus one JSON string from
  `IslandBootstrapFactory`: an `IslandBootstrapDto` serialised with
  `TrainerJsonContext`, holding the culture, stack, live topic titles
  (`IStackQueries.GetTopicTitlesAsync`, ordered by title in the culture's
  collation, then key; catalog and home facets and the profile's topics use the
  same `LiveQuestions.TitleComparer` of the requested culture, never the thread
  culture, which under `/api` comes from `Accept-Language`), levels, types, the initial filters from the query string
  and, for a signed-in user, `Me` (`IProgressQueries.GetMeAsync`: streak, due
  count, per-topic progress). The island needs no request before it is usable.
- Public pages load `js/runtime-preload.js` (deferred) instead. Links into
  island pages are rendered by `IslandLink`: always `data-enhance-nav="false"`,
  and for the Practice and Mock entry points (home cards, "Practise this
  question", "Practise these questions" on the catalog and topic pages)
  `data-preload-runtime` with the fingerprinted
  `Assets["_framework/blazor.web.js"]` and `Assets["_framework/dotnet.js"]`.
  On the first `pointerover`, `focusin` or `touchstart` that reaches such a link
  the script appends one `<link rel="prefetch">` per URL, once per page
  (spec 8.2). The public page itself never loads a runtime script.
- Island pages call `PageLinks.Set` (through `SeoHead`) with their own path and
  canonical filters in both cultures, so "Sign in" and the language switch come
  back to the same mode.
- Auth state reaches islands through `AddAuthenticationStateSerialization` with
  `AuthenticationStateSerialization.SerializeAsync`: only `telegram_user_id`,
  `name` (display name, else username, else omitted) and `ClaimTypes.Role` values,
  with `name` as the name claim type. The session's `dit:uid`, `dit:stamp` and
  `sub` never reach the browser. The client reads it with
  `AddAuthenticationStateDeserialization` and `AddCascadingAuthenticationState`.
- The framework persists an antiforgery token for the WebAssembly components of
  an island page, so island pages carry one even for anonymous visitors, and
  `BreachGuardMiddleware` never compresses them. The islands themselves take
  their token from `GET /api/antiforgery` (spec 8.8).
- Client services (`Trainer.Web.Client/Program.cs`): authorization core, cascading
  auth state, localisation (`IStringLocalizer<ClientResource>`, RU and EN with a
  key-parity test), `HttpClient` on the host base address, `TimeProvider.System`,
  `UlidGenerator`, `IAnswerGrader` (`SelfAssessmentGrader`), `AnswerFactory`,
  `KeyboardShortcuts` and `MockPersistence` (scoped) and the browser
  storage services below (`IBrowserStore`, `OutboxStore`, `SummaryStore`,
  `ActiveMockStore`, `PracticeFiltersStore`) and the sync services (`ServerClock`,
  `AntiforgeryTokenProvider`, `TrainerApiClient`, `OutboxSyncService`,
  `AccountWatcher`, `AccountNotice`), all scoped, so one per page. `Program.cs` loads the
  persisted server-clock offset before the first render. The client never sets
  `InvariantTimezone`.

## Browser storage

The islands keep answers and mock sessions in `localStorage`, so nothing is lost
offline or on a reload (spec 8.8). All of it goes through `js/outbox.js`, which
island pages load before `blazor.web.js`, and the client services in
`Trainer.Web.Client/Storage/`.

**Keys (`StorageKeys`).** One key per owner (ADR 0008), so a shared browser never
mixes accounts:

| Key | Holds | Written by |
|---|---|---|
| `dit.outbox.v1.anon` | anonymous answers | `append`, `remove`, `move` |
| `dit.outbox.v1.u.{telegramUserId}` | answers given while signed in | `append`, `remove`, `move` |
| `dit.mock.v1.pending.{telegramUserId}` | mock summaries still to send | `summaryAdd`, `summaryRemove`, `move` |
| `dit.mock.v1.failed.{telegramUserId}` | summaries the server refused, with the reason | `move`, `summaryRemove` |
| `dit.mock.v1.{owner}.active` | progress of the active mock (`MockProgress`) | `putIfRevision`, `delete` |
| `dit.mock.v1.{owner}.{sessionId}.questions` | its questions, written once at start | `put`, `delete` |
| `dit.clock.v1`, `dit.practice.v1.filters` | last-writer-wins preferences | `put` |
| `dit.outbox.v1.corrupt.{unix ms}` | a quarantined unparseable value | `quarantine` |
| `dit.account` | `owner\|unix ms`: the owner (Telegram id or `anon`) a tab last saw signed in and when | `ditStore.claimAccount`, `account-publish.js` (plain `setItem`) |

`{owner}` is the Telegram user id or `anon`. The Telegram id stays in key names
after sign-out; accepted in ADR 0008 because it is not a secret.

**Formats.** An outbox key holds exactly the spec 8.8 document,
`{ "version": 1, "answers": [{ clientAnswerId, questionId, grade, usedHint, mode,
mockSessionId?, answeredAt }] }` (`AnswerBatch`). A summary list is
`{ "version": 1, "items": [{ mockSessionId, summary, reason }] }`
(`MockSummaryList`); the questions are `StoredMockQuestions` and the progress is
`MockProgress` (`version`, `sessionId`, `revision`, the phase, the deadline,
`finishedAt`, the attempts and the completion state). Every document is serialised through `TrainerJsonContext`
(`Trainer.Contracts/Storage/`). A list that becomes empty removes its key.

**Locking.** `window.ditStore.mutate(opJson)` is the only writer. The op names
every key it touches; the script computes that set up front, sorts it and
acquires `navigator.locks.request('dit-store:' + key)` for each key exactly once,
nested in sorted order, so two moves in opposite directions cannot deadlock.
Inside the locks it reads, applies and writes in one synchronous task. No op
writes a whole list: `append` and `summaryAdd` merge by id (a known id is
replaced in place), `remove` and `summaryRemove` delete only the given ids, so a
tab appending while another removes acknowledged entries loses nothing. The op
and its result (`StoreOp`, `StoreResult { ok, persisted, error, revision, count }`)
are JSON through a client-side source-generated context; `read(key)` returns the
raw string without a lock. `quarantine` also takes the lock on the copy prefix
`dit.outbox.v1.corrupt.`, so two tabs never pick the same copy name.

**Key families.** Before taking any lock, every op checks its keys with anchored
patterns and answers `bad-op` otherwise, so no op writes outside its family:

| Op | Allowed keys |
|---|---|
| `append`, `remove` | outboxes (`dit.outbox.v1.anon`, `dit.outbox.v1.u.{id}`) |
| `summaryAdd`, `summaryRemove` | summary lists (`dit.mock.v1.pending.{id}`, `dit.mock.v1.failed.{id}`) |
| `move` | both keys in the family of its shape (`outbox` or `summaries`) |
| `put` | the preference keys and the questions key (session id a ULID) |
| `putIfRevision` | the progress key |
| `delete` | the progress and questions keys |
| `quarantine` | any of the above; never `dit.account` or a quarantined copy |

The owner inside a key is not checked: the script does not know who is signed
in, so a move between owners (the anonymous import) is the C# caller's decision.

A document deeper than 64 levels (`StoredDocument.MaxDepth`, the `JsonDocument`
default) is corrupt on both sides, and the script refuses to write one
(`bad-op`), so the client and the script always classify a value the same way.

Web Locks serialise the critical sections across tabs. That every tab then reads
the value the previous lock holder wrote is how Chromium, Firefox and Safari
behave (the two-tab E2E tests check it), not something the Web Locks or Web
Storage specifications promise, since `localStorage` is synchronised between
processes outside the lock manager.

**Durability rules.**

1. Without `navigator.locks` (or `localStorage`) `mutate` refuses with `no-locks`
   (`no-storage`) and writes nothing; the client switches to memory-only for the
   rest of the tab's life. There is never an unlocked shared read-modify-write.
2. A failed write (`quota`, `write-failed`) leaves the stored value unchanged.
   `OutboxStore` keeps every answer it could not write in memory and retries all
   of them with the next write; nothing is ever evicted, whatever the count.
3. `move(from, to, ids?)` is destination first: merge into `to`, then remove
   exactly the moved ids from `from`. A failure between the two writes leaves the
   entries in both keys (`persisted: true`, `ok: false`); every write merges by id,
   and the next `move` finishes the removal. An outbox entry that another owner's
   outbox (any `dit.outbox.v1.*` key besides the two) already holds is never moved
   again, only removed from the source, so a half-finished import into one account
   can never put the same answer into a second one. `OutboxStore.MoveAsync`
   counts an entry as moved only once it has left the source: after a partial move
   it reports the rest as `SourcePending`, hides them from reads of the source and
   retries their removal at once and on every later move from that key.
4. Unparseable JSON (or a document whose list is not an array, whose `revision`
   is not an integer, or that is nested too deep) is copied to
   `dit.outbox.v1.corrupt.{unix ms}` by the `quarantine` op under the key's lock,
   and removed only if the copy succeeded; otherwise it stays in place and that
   key runs memory-only. The stores quarantine on read and when an op reports
   `corrupt`, then retry once.
5. A document with an unknown `version` (written by a newer app) is never
   touched: ops on it answer `newer-version` and that key runs memory-only.
6. Each outbox entry is validated on read (ULID `clientAnswerId`, a question id,
   known grade and mode, `answeredAt` present). Invalid entries stay in storage
   and are never returned, so they are never sent. `remove` drops only the ids it
   names; entries without a string `clientAnswerId` (`null`, non-objects) are
   never swept. No API wipes an outbox wholesale: answers leave it only when
   acknowledged, moved or discarded by id.
7. The active mock's progress is saved with `putIfRevision(key, json,
   revision - 1, sessionId)` (an absent key is revision 0). Another tab that saved
   the same mock first, or a stored document of another mock session at any
   revision (a stale tab must not overwrite a mock that happens to sit at the
   revision it expects), makes the save fail with `revision`, which
   `ActiveMockStore` reports as `SaveResult.OtherWriter`. `delete` takes the
   session id and leaves another session's progress alone;
   `ActiveMockStore.ClearAsync` deletes the questions only after the progress
   delete succeeded and reports whether both did. When the stored progress is
   unreadable (`corrupt`), it is quarantined and the save is retried expecting
   revision 0: an unreadable value names no revision or session, so it cannot be
   another tab's newer save, and without this every later save of the mock would
   fail as `OtherWriter`. An absent key mid-mock is still `OtherWriter` (another
   tab finished the mock).

**Status.** `OutboxStore.Status` is `Persistent`, `MemoryOnly` (no locks, no
storage, or an unreadable value that could not be set aside), `Full` (some
answers only in memory after a failed write; `UnsavedCount` says how many),
`NewerVersion`, or `WriteFailed` (a removal of acknowledged answers did not reach
storage; the ids stay hidden from reads and the removal is retried by the next
read or write of that key, after which the status is `Persistent` again; until
then the answers may be sent once more, which the server deduplicates). A failed append keeps exactly the answers it carried (by
instance), so an answer added while another append was in flight is never marked
saved by it. A summary that storage does not take stays in `SummaryStore` memory
the same way (`UnsavedCount`): reads return it, so it is still sent, and removing
or moving it works in memory. `SummaryStore.HasNewerVersion` and `ActiveMockStore.HasNewerVersion`
report a newer app's summary list or active mock the same way. `StorageNotice`
shows `MemoryOnly` first, then `NewerVersion` from any of the three stores, then
`Full` and `WriteFailed`, with the matching localised message ("Answers are kept
only until you close this tab", "Browser storage is full; N answers are not saved
yet", "Progress saved by a newer version of the app is kept; reload to
continue", "Browser storage did not take a change; it will be tried again"), and
listens to the `Changed` event of all three. While `OutboxStore.UnsavedCount` or
`SummaryStore.UnsavedCount` is above zero, `UnsavedGuard` (in `IslandRoot`) arms
a `beforeunload` prompt through `ditPageLifecycle.setUnsaved` (`js/page-lifecycle.js`),
so closing the tab, the sign-out POST or any navigation asks before answers held
only in memory are lost; the listener exists only while armed, so it never keeps
a page out of the back/forward cache otherwise.

**Tests.** `Trainer.Web.Client.Tests` covers how the stores react to results with
`FakeBrowserStore`; `Trainer.Web.E2E/OutboxConcurrencyTests` runs the real script
in two tabs of one Chromium context: concurrent appends, an append during a
remove, opposite moves started at the same moment in both tabs (no deadlock
within 2 s; without the sorted lock order this test deadlocks), a real quota
overflow, a missing `navigator.locks`, a crash between the two writes of a move
(and a move into a second owner after it),
concurrent summary adds and removes, concurrent quarantines, every op refused
outside its key family, a non-integer revision and the depth limit.

## Sync, server clock and account isolation

The client side of spec 8.8 lives in `Trainer.Web.Client/Api/` and `Sync/`.

**API client.** `TrainerApiClient` has one method per `/api` endpoint and
(de)serialises through `TrainerJsonContext`. It never throws for HTTP or network
failures: every call returns `ApiResult<T>(Value, Status, ErrorCode)`, where
`Status` is 0 when no response arrived and `ErrorCode` is the `ApiErrorDto.Code`
(null for a body that is not that JSON, such as a proxy's HTML page); `RetryAfter`
carries the response's `Retry-After` (sent with 429) as a delay. Writes take
the owner as a parameter and send `X-CSRF-TOKEN` (from
`AntiforgeryTokenProvider`, which caches the token of `GET /api/antiforgery`),
`X-Outbox-Owner` and `X-Client-Utc-Offset` (`TimeProvider.GetLocalNow().Offset`
in minutes). On 400 `antiforgery` the token is invalidated, fetched again and the
write retried once with the same owner, so an account switch in another tab
between the two attempts still ends in 409, never in a write to the new account.
A token fetch that fails reports its error status (401, 429, 5xx) or, when it
came back 2xx without a token (a captive portal's page), status 0: the write was
never sent, so it can never look successful. `PutMockSummaryAsync` reports
`Value = true` only for the endpoint's 204; a 200 is not an acknowledgement.

**Server clock (ADR 0005).** Every response's `Date` header calibrates
`ServerClock`: `offset = Date + 0.5 s − midpoint(sentAt, receivedAt)` (the header
truncates to whole seconds). Samples with a round trip over 10 s (or a negative
one) and offsets beyond ±1 year are ignored; otherwise the latest sample becomes
the anchor: the server time at that moment plus the device's monotonic timestamp
(`TimeProvider.GetTimestamp`). `Now()` is the anchor plus the monotonic time
elapsed since, so a wall-clock change on the device (by the user or NTP) within a
page never moves a stamp; the next response re-anchors. Each accepted sample is
saved once with `put` under `dit.clock.v1` as `{ "version": 1, "offsetMs",
"observedAt" }` (never per answer). At start the saved offset is used only when
`observedAt` is at most 1 hour old (and not in the future), because the device
clock may have been corrected since; otherwise, and before any sample, `Now()` is
the device clock. An answer takes `answeredAt = ServerClock.Now()` when it is
given and keeps it, so a retry or a later device clock change never redates it;
the server judges it with its own clock (`answered-too-old`,
`answered-in-future`, clamping).

**Outbox sync.** `OutboxSyncService.Start(owner)` binds one Telegram user id for
the page's life; only `dit.outbox.v1.u.{owner}` and that owner's summary keys are
read, and every write names that owner. An attempt reads the outbox afresh (the
island and other tabs write to it too), posts it in chunks of 200 and removes
exactly the ids named in `Accepted` or `Rejected` of a 200 response. Every other
outcome keeps the entries and backs off on a `TimeProvider` timer: 5 s, doubling
to 5 min. On 400 `batch-too-large` or 413 the chunk is halved for the next attempt
(minimum 1). A clean attempt resets both the delay and the chunk size (200).
After the answers it sends the pending mock summaries with
`PUT /api/mock-sessions/{id}`: only 204 removes one; 404 `not-found` or 400
`validation` moves it to the failed list with that code as the reason (kept,
listed on the profile with Discard); any other 2xx, a bare 404, 5xx or no
response keeps it for the next attempt. The summaries are skipped when the answer
phase just ended without a response, with 429 or with 5xx (they would meet the
same outage and spend the per-user write limit); a 400 on the answers does not
block them. 409 `owner-mismatch` raises `AccountMismatch`, 401 raises
`SignedOut`; both stop syncing for good and keep everything. After `Stop()` no
further chunk or summary leaves, even within the attempt in progress (the
service cancels the token it passes to every write, which also skips the
antiforgery refetch-and-retry). The cancellation aborts the fetch in flight too, so
a batch the server stored may go unacknowledged locally; its ids stay in the
owner's outbox and are sent again later, when the server deduplicates them. A summary the server took (204) or refused but
whose removal or move to the failed list did not reach storage makes the attempt
retry; the PUT is idempotent. An
unexpected exception in an attempt (storage or interop) is treated like a 5xx: it
backs off and retries. `RequestFlush` respects a running back-off;
`FlushAsync(maxWait)` (flush-before-fetch: Review, Practice with `due` or
signed-in `new`, profile) tries at once and returns true when the owner's outbox
and pending summaries are empty afterwards, and false when they are not, cannot
be read, or the attempt outlives `maxWait`. Its only exception is
`OperationCanceledException` when the caller's token is cancelled (also when
already cancelled on entry); that never cancels the attempt itself. On false the
island continues and `SyncStatus` shows "N answers not synced yet" from
`Pending`.

The not-found rule relies on the code: `ApiErrorMiddleware` writes `not-found` for
every unmatched `/api` route, so it does not prove the summary id is unknown in
general. It does here because every deployed version maps
`PUT /api/mock-sessions/{id}`, while a proxy's or CDN's 404 carries no
`ApiErrorDto` and is kept. Even a wrong call loses nothing: the failed list keeps
the summary.

**Account isolation between tabs.** The auth cookie is shared by all tabs, so an
island can outlive the account it was rendered for. `AccountWatcher.StartAsync`
first listens to `storage` events on `dit.account` (`ditStore.watchAccount`),
then claims the key for the island's owner (`ditStore.claimAccount`). The value is
`owner|unix ms`: when another owner was published after this page started loading
(`performance.timeOrigin`), the page was rendered for an account the cookie no
longer holds, so the claim writes nothing and reports that owner as a change; an
older value is stale and is replaced. Listening before claiming means a change
between the two still arrives as an event. Signed-in pages
publish theirs with `js/account-publish.js` (`data-owner`, rendered by
`AccountControls`), and so does the sign-in page for `anon`, which is where a
sign-out lands. Anonymous public pages are cached and shared, so they publish
nothing. When another tab publishes a different owner, `OutboxSyncService`
(which takes the page's `AccountWatcher`) stops itself, also when the change came
before `Start`, and `AccountChangedNotice` shows "You signed in or out in another
tab. Reload to continue." with a Reload button; it shows the same notice on
`AccountMismatch` (both through `AccountNotice`, below). The stop does not depend on any component being rendered.
The watcher is a courtesy: the server's owner check (ADR 0008) stays
authoritative and catches whatever the events miss.

**Tests.** `Trainer.Web.Client.Tests` (`ServerClockTests`, `TrainerApiClientTests`,
`OutboxSyncServiceTests`, `AccountWatcherTests`) drives the services with
`FakeHttpHandler`, `FakeBrowserStore`, `FakeJsRuntime` and `FakeTimeProvider`.
The sync tests wait on the attempt in progress (`CurrentAttempt`, internal), never
on wall-clock delays;
`SignInPageTests.SignedInPublicPage_AndSignInPage_PublishOwner` checks the
`account-publish.js` tags. The two-tab and flush-before-Review scenarios run as
E2E tests with the Practice and Review islands.

## Question sessions and island UI

The question model lives in `Trainer.Web.Client/Sessions/`, the components in
`Components/`. Practice, Mock and Review share them.

**Model.** `QuestionKinds.Of` maps a question to a `QuestionKind`: `single` and
`multi` are choices, `output` with options is `OutputChoice` and without them
`OutputTyped`, `find-bug` is `FindBug`, `open` and `situation` are
`SelfAssessed`. `QuestionAttempt` is one question as the user works on it:

- The answer id (`ClientAnswerId`) is fixed when the attempt is created, so
  completing or restoring it again never yields a second answer.
- Options are shuffled with `OptionShuffler` (Fisher-Yates) in every mode
  (spec 5.3); the shown order is part of the state.
- Input: `Toggle` (a single-answer kind replaces the pick, multi toggles),
  `ToggleLine` (find-bug, 1-based), `SetTyped`, `SetNotes`, `RevealHint`.
  `LastInputAt` is `ServerClock.Now()` at every pick, line toggle and non-empty
  typed change, never at a hint or notes. `Freeze()` ignores all later input;
  grading still works (Mock grades at Finish).
- `HasResponse`: a pick, a line or non-empty typed text for automatic kinds (a
  hint alone is not a response); for self-assessed kinds only an explicit
  self-grade. `CanSubmit` is true for an unfrozen automatic kind with a response.
- `SubmitAutomatic()` grades with `AutoGrading` (a hint caps at `partial`),
  stamps `AnsweredAt = ServerClock.Now()` and freezes.
  `SubmitSelfAssessmentAsync(choice, grader, ct)` throws until
  `RevealReference()` was called (Mock calls it when it enters Assessment), then
  grades through `IAnswerGrader`; an explicit self-grade is the answer whether
  or not notes were written. While the grader works, `IsAssessing` is true and
  `CanSelfAssess` false: the grade buttons are disabled and the 1-3 keys do
  nothing, so a second grade can never start. `Skip()` (self-assessed only,
  after the reference) records nothing. Once the reference is shown the hint is
  gone: opening it then could only lower the grade.
- `Snapshot()` and `Restore()` round-trip `AttemptState`: `Grade` holds the
  final grade of either kind, `SelfGrade` the user's own choice, `AnsweredAt` the
  time of an automatic grade and `AssessedAt` that of a self-grade;
  `ReferenceRevealed` keeps a shown reference across a reload (documents without
  it read as hidden). Restore keeps the saved option order (options added since
  go last) and drops picks or lines the content no longer has.

`PracticeSession` walks a question list one at a time, creating each attempt
when its question is shown; `Results` lists graded questions only and `SeenIds`
every shown one (so "New session" can exclude them). `AnswerFactory.Create`
turns a graded attempt into the outbox `AnswerDto` with the attempt's own id and
`AnsweredAt`; it throws for an ungraded or skipped attempt, and a mock session
id is required for `mock` and refused otherwise.

**Island states.** `IslandState.FromResult` maps a session response: status 0
is `Offline`, 401 `SignedOut`, 409 `AccountChanged`, 429 `RateLimited`, a
success `Empty` or `Ready`, anything else (including a 2xx without a readable
body) `ServerError`. `IslandStateView` renders each state in RU and EN with its
action: Change filters, Try again (with the `Retry-After` wait for 429, in
seconds below two minutes and in minutes from there), or a sign-in link that
returns to the page. `AccountChanged` renders nothing itself: it calls
`AccountNotice.Show()`. `AccountNotice` (scoped) is the page's single source for
that notice: it also listens to `AccountWatcher.Changed` and
`OutboxSyncService.AccountMismatch` from the moment it exists, and only the
shell's `AccountChangedNotice` renders it, so the notice appears once whatever
comes first. `OutboxSyncService` keeps `HasAccountMismatch` and `IsSignedOut`
after it stops, so a notice resolved later still starts shown. A 401 to a sync
write shows the same notice with the signed-out reason ("You were signed out" and
a sign-in link back to the page); an account change replaces it, never the other
way round. Storage problems are `StorageNotice`'s and
unsent answers `SyncStatus`'s, both in `IslandRoot`; answers the server
rejected are not listed one by one.

**Components** follow `docs/design/mockups/question-screen-themes.html`:
`SessionBar` (level chip, topic chip, `i / N`, the streak for signed-in users,
progress as `<progress>`), `TopicSidebar` (topic links into Practice; known/live
as `<meter>` and the review-queue count for signed-in users, names only for
anonymous ones), `QuestionCard` (title, prompt, `CodePanel`, a "Show hint"
button: a button rather than a `<details>` because opening the hint caps the
grade), `OptionList` (native radio or checkbox inputs inside full-size labels;
verdict marks when the parent asks; an `opts--fit-N` class from
`OptionLayout.Columns`), `CodeLineSelector` (each line a
`<button type="button" aria-pressed>` with a hidden "Line N" prefix),
`TypedOutputInput` (a textarea: output may span lines), `SelfAssessment` (notes,
"Show reference answer", then knew / partially / didn't know and an optional
Skip) and `FeedbackPanel` (terminal output: the verdict line with "expected ...,
got ...", the options with their verdicts and "why", the explanation, the fixed
code for find-bug and the sources with a language marker when a link's language
differs from the page's). The terminal commands are localised like the rest of
the page.

Rules every island screen keeps:

- After a submit, `FeedbackPanel`'s visually hidden `role="status"` region
  (rendered empty beforehand, so the change is announced) carries only the
  verdict, and the island moves focus to its Next button. The terminal itself is
  not a live region, so nothing is read twice.
- Dynamic sizes only through `<progress>`, `<meter>` or SVG attributes: the CSP
  allows no inline styles.
- Every interactive element is at least 44 x 44 px on every pointer type,
  including find-bug line buttons; nothing depends on hover. Code keeps its line
  breaks, scrolls inside its panel (the panel takes focus for keyboard
  scrolling) and is never below 13 px.
- Layout (spec 9.1, `islands.css`): below 640 px one column, options stacked and
  the primary action in a sticky `.action-bar` padded by
  `env(safe-area-inset-bottom)`; 640-1023 px options in two columns, feedback
  below. From 640 px the bar is inline, not sticky (spec 9.1 asks for the sticky
  bar on phones only), so on a long question the primary action can sit below
  the first screen (Practice's Answer at y 868-912 of a 1280 x 900 viewport): an
  accepted behaviour, not a defect. The island sweep therefore checks that the
  action is inside the first viewport only below 640 px, and at every width that
  it is visible and reachable (`elementFromPoint`). Keeping it always in view on
  larger screens would be a design change to spec 9.1. From 1024 px the full
  layout of the mockup applies: the topic sidebar is always open (a `<details>` drawer below that, through `::details-content` as in the
  catalog) and options take `OptionLayout.Columns`: up to four in one row when
  every option is short (at most 24 visible characters, no block such as code
  or a list), otherwise two. From 1440 px the content is capped by the 80rem
  container and feedback sits beside the question; the question column is then
  half as wide, so its options drop to two columns.
- Browsers without `::details-content` (older Firefox and Safari) keep the
  "Topics" toggle from 1024 px too, closed until opened, exactly like the
  catalog filters: the sidebar stays reachable without a script that would have
  to watch the viewport.
- Links that leave an island page are full page loads
  (`data-enhance-nav="false"`) and include their path (`<base href="/">`).

**Keyboard.** `js/shortcuts.js` (island pages only) listens on `document` and
drops what must stay native: events with Ctrl, Alt or Meta, repeats and IME
composition, every key inside a text input, `textarea`, `select` or
`[contenteditable]`, and Enter on a button, link or `summary` (their own
activation wins). Digits and N are read from the physical key (`event.code`), so
they work on a Russian layout too; the key value counts only when an event has
no code (some on-screen keyboards), so on layouts such as Dvorak one key, not
two, means Next. It passes `1`-`4`, `enter` and `next` to
`KeyboardShortcuts.OnShortcut` through a `DotNetObjectReference`;
`KeyboardShortcuts.Resolve` decides what a key means for the attempt on screen:
1-4 pick the n-th displayed option of an unfrozen choice question (toggle for
multi), or choose knew / partially / didn't know while a self-assessment waits;
Enter submits when `CanSubmit`; N goes next when the island allows it. Hints
are `<kbd class="kbd-hint">`, shown only under `@media (pointer: fine)`.

**Tests.** In `Trainer.Web.Client.Tests`: `QuestionAttemptTests`,
`PracticeSessionTests`, `OptionLayoutTests`, `IslandStateTests`,
`KeyboardShortcutsTests`, `AccountNoticeTests`, and
`IslandComponentRenderTests`, which renders the components with the framework's
`HtmlRenderer` (no inline styles, ARIA state, `<progress>`/`<meter>`, strings,
one account notice whatever the order). In a browser, `PracticeFlowTests`
(below) drives them through the Practice island, including
`Practice_OptionColumns_FollowTheLayout`.

### Practice island

`PracticeIsland` (`Islands/PracticeIsland.razor`) puts the pieces together:

1. **Start.** It reads the bootstrap, publishes and watches the account
   (`AccountWatcher.StartAsync` with the Telegram id or `anon`) and, for a
   signed-in user, binds `OutboxSyncService.Start(id)`. The watcher starts first,
   so a sync never binds after another tab already switched accounts.
2. **Filters.** `PracticeFilterSet.Resolve` picks the filters: filters in the URL
   win as a whole; without them the ones remembered under
   `dit.practice.v1.filters` (`PracticeFiltersStore`, written when Start is
   pressed in the form); otherwise the defaults. `first=` alone counts as a URL
   selection, so "Practise this question" is followed by questions from the whole
   stack, not by a remembered filter. The count is never in the URL, so it is
   always the remembered one (default 20, 1..50). Unknown keys are
   dropped and `due` falls back to `all` without a sign-in. `PracticeFilters`
   renders the form (levels, topics, types, status, count, Start).
3. **Fetch.** `?first=` skips the form and fetches at once. Before a signed-in
   `new` or `due` session the island flushes (`FlushAsync`, up to 5 s), because
   the server computes those statuses from the answers it has. An anonymous
   `new` session sends the question ids of the anonymous outbox, newest first,
   as `ExcludeQuestionIds`; "New session" sends the ids just seen first, in every
   status (`PracticeFilterSet.Excluded`, at most 1000). The result goes through
   `IslandState.FromResult`; anything but `Ready` shows `IslandStateView`, whose
   Try again repeats the same request and Change filters returns to the form.
4. **Questions.** One `QuestionAttempt` at a time with `SessionBar`,
   `QuestionCard`, the answer component of its kind and `FeedbackPanel`, which
   is always rendered so its live region exists before the answer. Every graded
   answer goes to the owner's outbox (`dit.outbox.v1.u.{id}` or `.anon`) and, for
   a signed-in owner, `RequestFlush()`; answering works offline once the session
   is loaded. Outbox first: while the write is in flight the island does not
   render (`ShouldRender` is false), so the verdict and Next appear only once the
   answer is in the outbox. The wait is bounded by `SlowStorage` (5 s, the same
   helper and bound `MockPersistence` uses): a Web Lock that is never granted
   cannot freeze the page; after the bound the graded state shows, the answer
   stays in `OutboxStore`'s memory until a write succeeds, and a "saving is taking
   longer" notice shows until the write ends. A write storage refuses also leaves
   the answer in memory, and `StorageNotice` says so; the session goes on either
   way. `SelfAssessment` keeps its grade buttons, disabled and with a saving
   note, while the island holds a chosen grade back. The island counts graded
   answers whose write returned or outlasted the bound in `data-answers-saved`
   (tests wait on it). This protocol lives in `Sessions/AnsweringFlow.cs`, which
   Practice and Review share (with `Components/AnswerArea.razor` for the input of
   each kind): submit, self-grade, skip, Next's guard and the shortcut dispatch.
   Start, Try
   again and New session are ignored while a fetch is in flight. The primary action (Answer, then Next or Finish) sits in
   `.action-bar`, which is sticky on phones; after an answer focus moves to Next.
   Keyboard shortcuts go through `KeyboardShortcuts.Resolve`.
5. **Summary.** Results by topic (`PracticeSummary.ByTopic`) with links to the
   public question pages, New session and Change filters.

A focused text field stays visible when the on-screen keyboard shrinks the
layout: on a coarse pointer `islands-start.js` scrolls it back into view on `resize`, and
`scroll-padding-bottom` on phones keeps it clear of the sticky bar.

Tests: `PracticeFiltersTests` (client), `PracticePageTests` (page shell, query
parsing, entry points and their preload attributes, island links without
enhanced navigation) and `PracticeFlowTests` (E2E: every question kind, the
keyboard, the outbox and its sync, offline, empty and storage-blocked states
(with the leave prompt only while answers exist only in memory),
remembered filters, New session, layout at 320-1920 px with screenshots
`practice-{width}-{theme}.png`, 44 px targets, the sticky bar, the shrinking
viewport, RU strings, one session fetch per island document, the preload URLs
against the runtime's requests, and the two-tab account isolation).

### Mock island

`MockIsland` (`Islands/MockIsland.razor`) runs a mock interview (spec 5.3). The
model is `MockSession` (`Sessions/MockSession.cs`, with `MockContext` and
`MockSettings`), the storage and hand-over `MockPersistence`, the summary
`MockSummaryBuilder`; the components are `MockSetup`, `MockTimer`,
`MockJumpStrip`, `MockAssessment` and `MockSummaryView`, next to the shared
question components.

1. **Start.** Like Practice: bootstrap, `AccountWatcher`, then
   `OutboxSyncService.Start(id)` for a signed-in user. The island then loads the
   owner's active mock (`dit.mock.v1.{owner}.active` and its questions key) and
   resumes it in whatever phase it was, with its deadline; a deadline that passed
   while the page was closed finishes the mock at once (its `finishedAt` is the
   deadline, not the moment the page came back). A mock of another culture or
   stack is not shown here (a page never mixes languages): the island links to
   its own page, or ends it here ("End it and start here": unanswered questions
   stay unanswered, open ones are skipped, the results are handed over). Only an
   adopted load counts: `MockPersistence.LoadAsync` changes nothing, and
   `Adopt(progress)` makes the tab continue the mock. When the load takes longer
   than 5 s the setup form shows with Start disabled until the load ends, and a
   mock it finds is still resumed.
2. **Setup** (`MockSetup`, `MockSettings`): a level (required; the first level of
   the URL, else Middle), optional topics, N (5..50, default 20) and the timer
   (off by default; `N × 90 s`, shown before the start). Start sends
   `POST /api/sessions/mock`. An empty pool shows the empty state.
3. **Prepared.** When `RepeatsLikely` (the pool is under about 3 × N) the warning
   "Only P questions match; sessions will repeat questions." is shown before any
   question, with "Use N = {SuggestedCount}" (only when it is a valid count, 5
   or more, below N, `MockSettings.OffersSuggestion`; it fetches again) and "Start anyway" (uses the prepared questions). Changing any setup
   field here drops the prepared questions. Without the warning the mock starts
   at once.
4. **Answering.** `MockSession` creates every attempt, and with it its answer id,
   at the start; the timer allows 90 s per question received. No feedback of any
   kind: options show no verdicts, open and situation questions take notes but
   cannot reveal the reference. `SessionBar` shows `i / N`, `MockJumpStrip` one
   44 px button per question with an "answered" marker (it scrolls inside itself
   on phones), `MockTimer` (`role="timer"`) the time left, computed from the
   deadline on every tick (never counted down, so a throttled background tab is
   right at once), with one polite announcement at one minute. The timer's end
   and Finish both call `MockSession.Finish`, which runs once: it freezes every
   attempt, grades each automatic one that has a response with `answeredAt` set to
   its last input, reveals the reference of every self-assessed one and moves to
   the assessment. A hint alone is not a response.
5. **Assessment** (`MockAssessment`): every open and situation question with the
   user's notes (read-only), the reference, the grades 1-3 and Skip. An explicit
   grade is an answer, notes or not (`answeredAt` is the moment of the grade); a
   skip is not. A grade appears on screen only after the progress document that
   holds it was saved.
6. **Completion.** `MockPersistence.HandOffAsync`, in order and resumable:
   (1) `MockSession.Complete` stores `MockCompletionState` (the answers with their
   ids and times, the summary) in the progress document; (2) the answers go to
   the owner's outbox (`OutboxStore.AddAllAsync`), then `AnswersQueued` is saved;
   (3) a signed-in owner's summary goes to `dit.mock.v1.pending.{id}`
   (`SummaryStore`), then `SummaryQueued` is saved; an anonymous summary is only
   shown (spec 8.8 imports answers, not summaries); (4) only when (2) and (3)
   reached storage are both mock keys deleted. A reload at any point resumes from
   the stored state and repeats only the unfinished steps with the stored ids, so
   nothing is queued twice. In memory-only storage the keys are never deleted.
   When (2) leaves the answers only in this tab's memory (no locks, no storage,
   quota), a signed-in owner's summary is held in `SummaryStore` memory beside
   them (`HoldPending`), never in storage, so a reload cannot leave a stored
   summary without its answers; the sync service sends both from memory.
   The summary shows after the hand-over: totals, a table by topic (knew,
   partially, didn't know, unanswered) and every question with its grade or
   "unanswered", linking to its public page. Unanswered questions count only in
   the summary and never reach the scheduler.

**Saving.** The questions are written once (`put` on the questions key); the
small `MockProgress` document is saved with `putIfRevision` on every pick, line,
hint, move, phase change and grade, and typed output and notes 500 ms after the
last keystroke. `js/page-lifecycle.js` reports `visibilitychange` (hidden) and
`pagehide`, and the island saves pending text at once (best effort: a write still
waiting for its lock may not finish before the page goes). A revision conflict
means another tab saved this mock after this one: the tab turns read-only for
good with "This mock is open in another tab" (inputs disabled, nothing written,
nothing handed over; a tab that turns read-only during the hand-over stops before
the outbox).

**Starting next to a stored mock** (`MockPersistence.StartAsync`, P24). The
check reads storage afresh inside the write gate; nothing a tab loaded earlier
authorises a delete. A stored mock is deleted only when nothing in it is lost:
a finished one is handed over first, from the stored document (answers, then
summary, each flag saved), and deleted once both are durable; an unfinished one
that can be resumed is never replaced (Start returns false and the island
resumes it); only an unfinished one whose questions are gone (nothing can be
graded or resumed) is replaced. When two tabs start at the same moment, the
loser's first `putIfRevision` fails: it removes its own questions key and
resumes the winner's mock. A new mock is detached (it writes nothing) from the
moment the start begins until its first progress document is written, so any
refusal leaves the stored mock untouched and the island resumes it. A flag save
that fails while a stored mock is released does not move the expected revision
on, and a failed delete refuses the start. The start is the one storage step
that is not cut short at 5 s: taking it as done while the release still runs
could let the new mock overwrite the stored one, so past 5 s the island keeps
Start blocked on the loading heading with the slow notice until it ends. "New interview" after a hand-over that did not
complete tries the hand-over again; while the stored mock still waits for it,
the summary stays with "The results of this interview are not saved in this
browser yet".

**A mock left from before sign-in** (`MockPersistence.ReleaseAnonymousAsync`).
Every signed-in island start (`IslandRoot`, before `ImportNotice` reads the
anonymous key) and the profile's import offer (`ImportPanel`, before it counts)
look at `dit.mock.v1.anon.active`, whose answers the anonymous import cannot see
while they sit inside a progress document. A finished one is handed over at once
to the anonymous outbox (no summary: an anonymous summary is only shown) and its
keys are deleted once the answers are stored; the import then offers its answers
(a Mock page that does the hand-over itself also says so, with a link to
`/profile#import`). An unfinished one is not touched until the user chooses "End
it and keep the answers": unanswered questions stay unanswered, open ones are
skipped, and it is handed over the same way. A hand-over that storage refuses
keeps the mock and says so.

**Focus.** Focus moves to whatever replaced the control the user used, never to
the page body: Start to the loading heading, then to the small-pool warning or
the first question's title (`QuestionCard` titles take `tabindex="-1"`); Finish
to the assessment heading; a grade or Skip first to the item's title (its
buttons are disabled while the grade is saved), then to the next open item, and
after the last one through the "Saving the results" heading to the summary
heading. In the assessment, 1-3 grade the item that holds focus (or the one
focus was last moved to). Status texts go through one live region that exists
from the start.

**Bounded waits.** Every storage step the island waits for (load, save, outbox
append, summary queue, delete; not the start, above) is bounded by `MockPersistence.WriteTimeout` (the shared `SlowStorage` helper, also used by Practice)
(5 s), because a Web Lock that is never granted must not freeze the island. On a
timeout the step goes on in the background, the answers stay in this tab's
memory (the outbox keeps them as unsaved), the island moves on and "Saving is
taking longer than usual" shows until a write completes; the keys stay, so a
reload resumes the hand-over. Rendering is never suspended: a grade being saved
shows "Saving…", the hand-over "Saving the results…".

Tests: `MockSessionTests` (eligibility, idempotent and concurrent finish and
completion, answer times, the deadline, snapshot and restore, saving and
debouncing, the hand-over interrupted after every step, memory-only and full
storage, the second writer, every start path next to a stored mock including a
tab closed mid-hand-over, a load that was not adopted and two starts at once,
the ordered hand-over, a timer expiry during a save),
`MockSummaryBuilderTests`, `MockPageTests` (page shell and bootstrap) and
`MockFlowTests` (E2E: the whole flow without feedback, focus after Start,
grades, shortcuts and the last grade, ending another culture's mock here,
ending an anonymous mock left at sign-in, a finished one offered on the profile,
"New interview" while the hand-over is not durable, the timer with a fake
clock, the small-pool warning, Use N and changing a field while prepared, the
empty pool, reloads while answering and during the assessment, the second tab,
the signed-in and offline hand-over to the server, an outbox write that never
completes, option columns at 1024 and 1280 px, layout at 320-1920 px with
screenshots `mock-{width}-{theme}.png` and 44 px targets).

### Review island

`Islands/ReviewIsland.razor` on `/{culture}/{stack}/review` (spec 5.3). It needs a
signed-in user: the page itself redirects anonymous visitors, and a bootstrap
without `Me` shows the signed-out state.

- **Flush-before-fetch.** Every fetch (on start and "Check for more") first runs
  `OutboxSyncService.FlushAsync` with a 5 s bound, then `POST /api/sessions/review`
  for 20 questions. A question answered a moment ago (in this tab or an earlier
  page) has reached the server by then, so its new due date keeps it out of the
  queue; what does not make it in time stays pending and `SyncStatus` says so.
- **Recall step.** `ReviewSession` wraps `PracticeSession` and adds
  `AwaitingRecall`: a choice question (`single`, `multi`, `output` with options)
  first shows its prompt and hint without options, with `RecallStep` asking "Can
  you answer?". "Show options" (or Enter) calls `ShowOptions`, which reveals the
  options in the order the attempt already shuffled; `Pick` and `CanSubmit`
  refuse input while they are hidden, so 1-4 pick nothing. Focus moves to the
  question title, never to the body. Other kinds start answerable.
- **Answers.** Practice's `AnsweringFlow` with `mode = review`, into the user's
  outbox, then a background flush; Review adds only the recall branch (Enter shows
  the options, hidden options cannot be submitted).
- **Pending answers.** A question whose answer is still in the user's outbox (the
  flush did not get through) is dropped from the fetched queue: the server still
  counts it as due. While a fetch started by a button runs, focus waits on the
  main column, then moves to the new question's title.
- **Empty queue.** "Nothing is due now" with links to Practice and to the profile,
  which shows the next due date.
- **Summary.** Counts by topic, "Check for more" (a new flush and fetch) and a link
  to the profile.

Tests: `ReviewSessionTests`, `AnsweringFlowTests`, `ReviewAndProfilePageTests` (sign-in redirect, island
shell, noindex, `private, no-store`, no compression) and `ReviewFlowTests` (E2E:
due states seeded through `IAnswerRecorder`; recall then options with keyboard,
`mode = review` in the batch, the empty queue, a second session that does not
repeat the answer the first page could not send, "Check for more" that does not
serve an answer still pending, layout at 320-1920 px with
screenshots `review-recall-{width}-{theme}.png` and `review-{width}-{theme}.png`,
44 px targets at 375 px).

### Question feedback bar

`Components/QuestionFeedbackBar.razor` (ADR 0016) sits below `FeedbackPanel` in Practice and
Review, only when the page has a signed-in user (`Me`) and the attempt is done; Mock shows
none, and an anonymous visitor never sees it. Its parameters are the question, the user's
stored rating (`InitialRating`), the owner id and the culture.

- `SessionDto.MyRatings` carries the signed-in user's rating keys by question id for the
  questions of the session (one extra query in `SessionQueries`); it is null for an
  anonymous session. The islands hand the matching key to the bar.
- Like and Dislike are `aria-pressed` toggle buttons; pressing the active one clears the
  rating (`DELETE`), anything else sets it (`PUT`). A pressed button also gets an inset edge
  and shows a check mark, so the state never rests on colour alone; the box never changes
  (the check's space is reserved in both states), so pressing does not reflow the row.
- "Report a problem" is a native `<details>` with five radio reasons, an optional
  `<textarea maxlength="1000">` and Send, which stays disabled until a reason is chosen. The
  browser owns whether the `<details>` is open; a stored report replaces the element (a new
  `@key`), which closes it and clears its fields, so no toggle event can reopen it.
- `QuestionFeedbackModel` holds the state and makes the calls through
  `TrainerApiClient.SetRatingAsync` and `ReportAsync` (both writes, so they carry the token and
  the owner). A call while another runs is ignored (the controls use `aria-disabled`, never `disabled`, while
  saving, so keyboard focus stays put; the reasons and the comment are frozen then, so nothing typed during a
  save is wiped, and a stored report moves focus to the status line); a failed write keeps the stored rating. The
  status line is a live region: saving, saved or sent, failed (try again), or the report limit,
  shown as "You can send more reports later" and not as an error.
- `shortcuts.js` ignores keys inside `.feedback-bar__form`, so Enter and N in the form do not skip
  the question.
- At 1440 px the card spans two grid rows so the bar sits directly under the feedback terminal.
  The signed-in answered state (form open) is a swept page (`practice-feedback-signed-in`), and
  `FeedbackIslandTests` checks the bar at 375, 768, 1280 and 1440 px in both cultures.
- Component key: the bar is keyed by the question id (not the answer id, which `QuestionCard`
  already uses among the same siblings).

### Profile and anonymous import

`Islands/ProfileIsland.razor` on `/{culture}/{stack}/profile` (ADR 0010).

- **Load.** Flush-before-fetch (5 s), then `GET /api/profile`: streak (plural
  forms), answered questions, due now (a link to Review), the next due date
  (`<time>` in the device's zone), progress per topic and per level
  (`ProgressBars`: a `<meter>` plus the numbers as text) and the last five mocks
  with their unanswered counts. Pending answers show through `SyncStatus`.
- **Refused mock summaries.** `SummaryStore.ReadFailedAsync` lists the summaries the
  server refused (a coded `validation` or `not-found`), each with "Could not be
  saved" and the reason. Retrying cannot change a coded refusal, so the only action
  is Discard (`DiscardFailedAsync`). A status line then says whether it was
  discarded or the write failed (the list is re-read either way) and takes focus,
  in every phase of the profile.
- **Anonymous import (spec 4, 11.4).** Answers given before sign-in stay under
  `dit.outbox.v1.anon`; nothing moves them without a click. `ImportPanel`
  (`#import`) shows "N answers from this browser before you signed in" with:
  - **Import into my account:** `AnonymousImport.ImportAsync`:
    `OutboxStore.MoveAsync` from the anonymous key to `dit.outbox.v1.u.{id}` (one
    `move` op under both keys' locks, destination first), then the ordinary
    `FlushAsync`. Success is judged by reading the anonymous key afterwards: only
    an empty key is "N answers were imported" (or "queued" when the flush did not
    finish within 5 s); otherwise the panel says how many are still in the browser
    and Import finishes the move. A read that fails (`IBrowserStore.ReadCheckedAsync`
    tells it from an absent key) or a key this app cannot list (corrupt, a newer
    version) counts as "still in the browser", never as empty, and the panel then says
    the move could not be confirmed instead of naming a count it does not know;
    Discard reports such a read as a failed write. The server deduplicates by client answer id
    (spec 8.8).
  - **Discard:** the confirmation reads the ids stored at that moment and names
    their count; "Keep them" goes back, "Discard answers" removes exactly those ids
    (an answer another tab added since stays) and reports a failed write.
  - **Stale tab:** once another tab changed the account (`AccountWatcher`) or
    syncing stopped, both buttons are disabled with a note to reload, and the
    service refuses.
  Writes are bounded by `SlowStorage`; past the bound the panel says saving is
  slow and keeps its buttons disabled, while the rest of the island stays usable.
  Focus moves to the confirmation or the result, never to the body.
- **Notices elsewhere.** Signed-in islands show `ImportNotice` in `IslandRoot` (a
  plain read of the key, no store op) linking to `/{culture}/{stack}/profile#import`;
  the profile itself hides it. Signed-in public pages render a hidden, localised
  `.import-notice` in `AccountControls` (its `data-key` is
  `StorageKeys.AnonymousOutbox`), and `js/import-notice.js` reveals it when that
  key exists (an emptied outbox removes its key). Anonymous
  pages render neither.

Tests: `AnonymousImportTests` and `OutboxStoreTests` (a failed source removal is
not imported and never duplicated into another owner, a failed read or a newer
version under the anonymous key is not reported as imported, a stale tab is refused,
Discard removes only the counted ids and reports a failed write),
`ReviewAndProfilePageTests` (the hidden notice on signed-in public pages
only) and `ProfileAndImportTests` (E2E: streak, due link, next due and meters;
a refused summary discarded; two anonymous answers kept until Import, with the
notice on a public page and on an island; a source removal that fails twice is
reported as still pending, then completed by a second Import; Discard after
confirmation; layout at
320-1920 px with screenshots `profile-{width}-{theme}.png` and
`import-notice-{width}-{theme}.png`; 44 px targets at 375 px).

## SEO

Every public page writes through `SeoHead`:

- `<title>`, meta description, `canonical`, Open Graph tags.
- `hreflang` alternates for every culture the page exists in, plus `x-default`.
- `noindex` for retired questions, catalogs with more than one level, more than
  one topic, any type filter or `page > 1`, and the 404 page.
- `BreadcrumbList` JSON-LD on every public page.
- `og:image` (1200 by 630, with `og:image:width`, `og:image:height` and `og:image:alt`) and
  `twitter:card` `summary_large_image`: a question page advertises its own card, every other
  page the site card of its culture (the localised tagline).

The sitemap is generated from the database, one file per culture, with
`xhtml:link` alternates. The default stack home is left out because its canonical
is the bare culture home. Absolute URLs are built by `SiteUrls`.

### Open Graph images

`OgCardRenderer` (SkiaSharp, ADR 0017) draws the card as a PNG: dark theme colours from
`tokens.css` (`OgPalette`, checked against the stylesheet by a test), a monospace
`~/dotnet/<topic>` header, a level chip, the title in Inter SemiBold 60 px wrapped on spaces to
at most four lines (a word wider than a line breaks between characters, the last line ends
with an ellipsis) and a footer with the localised site name and the public host. The same
card always renders to the same bytes. The fonts are embedded resources (OFL, `OpenGraph/Fonts`).

- The URL carries a fingerprint: the first 16 hex characters of SHA-256 over
  `OgFingerprint.Version`, the culture and every card field. A request with another
  fingerprint gets 301 to the current URL (`Cache-Control: public, max-age=3600`); the current
  one gets 200 `image/png` with `Cache-Control: public, max-age=31536000, immutable` and no
  cookie (`OgResponseHeadersMiddleware` strips `Set-Cookie` and restores the policy after the cookie handler, so a signed-in visitor cannot make a shared cache store a cookie). Only `GET` and `HEAD` serve images. An unknown slug, a missing locale, a retired question or an unsupported culture is an
  empty 404 (status-code pages are switched off under `/og`).
- `OgImageLinks` builds the card and the URL for both the page and the endpoint, so they
  agree on the fingerprint. A page without a card of its own gets the site card of its culture
  from `SeoHead`.
- `OgImageCache` keeps the rendered files in `App:OpenGraph:CacheDirectory` as
  `<culture>.<stack>.<slug>.<fingerprint>.png` (site cards `<culture>.site.<fingerprint>.png`; the dot cannot occur in a stack key or slug, so names are unambiguous),
  written under a temp name and moved. At most two renders run at once; a request that finds both busy for two seconds gets 503 with `Retry-After: 5` and `Cache-Control: no-store`. A missing or unwritable
  directory only disables caching: the image is still served and one warning is logged.
- After every committed import `CompositeContentCacheInvalidator` clears the page cache and
  then this directory; a changed title therefore gets a new URL and a new file.

## Output cache

`PublicPageCachePolicy` is a custom `IOutputCachePolicy` registered under two
names: `public` (pages) and `public-catalog` (the catalog, whose key also carries
the canonical query).

- **Cacheable requests:** `GET` or `HEAD` without the session cookie
  (`AuthCookieNames.Session`, `.dit.session`, used from Plan 2), without an
  `Authorization` header and not authenticated. Such requests are neither served
  from the cache nor stored in it.
- **Key:** the request method, scheme, host and path (the output cache's own
  base key), the content generation from `ContentSnapshot`, and for the
  catalog the canonical query string. The raw query string is never part of the
  key, so every spelling of one selection shares one entry.
- **Generation is read when the request starts**, so a render that overlaps an
  import is stored under the old generation and can never be served as the new
  content.
- **Search is rate limited (ADR 0011):** the uncached search requests are the only public
  requests that always reach PostgreSQL, so `ApiRateLimits` registers a `GlobalLimiter` that
  partitions only them (`CatalogSearchRateLimit.IsSearchRequest`: GET or HEAD to
  `/{culture}/{stack}/questions` with a non-empty term) by client address into fixed one-minute
  windows of `App:RateLimits:SearchPerMinute`; every other request gets `NoLimiter`. The limiter runs
  after request localisation and before the output cache. A rejected search gets 429, `Retry-After`,
  and a small localised HTML page (never JSON) that is `noindex` and `no-store` and links back to the
  catalog without the term. Behind the proxy the address is the forwarded client address, so nginx
  must send `X-Forwarded-For`.
- **Search is not cached (ADR 0011):** a catalog request with a non-empty search
  term (`q`, parsed and normalised as above) is neither served from nor stored in
  the cache, because free text is an unbounded set of keys. The key allow-list
  stays `level`, `topic`, `type`, `page`; a blank `q` is no search and shares the
  entry of the same selection without it.
- **Not stored:** responses that are not 200 or that carry `Set-Cookie`.
- **Lifetime and size:** 10 minutes, 100 MB total, 2 MB per body.
- **Eviction:** entries are tagged `content`. `OutputCacheInvalidator` refreshes
  the snapshot first and then evicts the tag, so a page rendered after the
  eviction already sees the new taxonomy. The tag is evicted even when the
  snapshot refresh throws, and the exception is then rethrown. If the eviction
  fails as well, both exceptions are surfaced in an `AggregateException`.
- Compression sits outside the cache: entries are stored uncompressed and
  compressed per request (Brotli, gzip).

## Progress queries and personal fragments

Signed-in visitors see their own progress on the public pages; anonymous visitors see exactly what they saw before.

- **Queries.** `IProgressQueries` (`Trainer.Infrastructure/Progress/ProgressQueries.cs`) reads `review_states`, `answers`,
  `mock_sessions` and `users` for one user. Every count uses live questions only (`LiveQuestions.Of`: not retired, with a
  locale in the culture), the set the catalog shows.
  - `GetStatusesAsync` returns `QuestionStatus(LastGrade, DueAt, IsDue)` only for answered questions among the given ids;
    `IsDue` is `due_at <= now`.
  - `GetDueCountAsync` counts due live questions in the culture.
  - `GetProfileAsync` aggregates per topic and per level: live, answered (has a review state), known (`last_grade = knew`)
    and due questions. `TotalAnswered` is the sum of the answered counts, `NextDueAt` the earliest future `due_at` among live
    questions, `RecentMocks` the last 5 finished mocks of the stack, newest first (totals summed from the stored per-topic
    summary). `GetMeAsync` is the slice an island needs at startup.
- **Streak.** Local answer days are `date(answered_at + users.utc_offset_minutes)`, de-duplicated and ordered newest first
  in SQL, then streamed with `await foreach` into `StreakAccumulator`, which stops at the first gap: the client reads only
  the streak plus one row, and the history has no window. The day boundary uses the offset of the user's latest write
  (`X-Client-Utc-Offset`, stored by the answer recorder), so an evening learner keeps the streak in any time zone; it is UTC
  until the first write.
- **Fragments.** The catalog and topic rows (`QuestionRow`), the line under the question title and the Review card on the
  home page render a `StatusChip` (`new`, `due`, `in N days` with Russian and English plural forms, the last grade as text; the border colour only repeats it) when
  the request is authenticated. The pages call `IProgressQueries` only for an authenticated user, and an anonymous render
  emits no extra markup or whitespace.
- **Cache invariant (spec 8.4).** A request with the session cookie or an authenticated user is neither served from nor
  stored in the output cache (`PublicPageCachePolicy`), and signed-in HTML is `Cache-Control: private, no-store`.
  `PersonalFragmentsTests` and `SignedInCacheTests` prove that the cache entry count and write count stay unchanged by
  signed-in requests and that a fresh anonymous render equals the one made before anyone signed in.

## Sign-in

Sign-in uses Telegram OpenID Connect through the `Telegram.OpenIdConnect.AspNetCore`
package (authorization code with PKCE, `ResponseMode=query`), wired in
`Auth/AuthServiceCollectionExtensions.AddTrainerAuth`.

1. A visitor opens `/{culture}/signin?returnUrl=...`. The page validates the return
   path with the `/lang/` rules and links to `/signin-telegram/challenge`. When the
   client id or secret is missing it says "Sign-in is not configured" instead.
2. The challenge redirects to Telegram. The library keeps the OIDC `state` short
   by storing the protected properties in a `.TelegramOidcState.*` cookie
   (Telegram rejects a `state` over 256 characters).
3. Telegram redirects back to `/signin-telegram`. The handler validates the
   id_token and calls `TrainerSignInGate` with the Bot-API id (the `id` claim,
   never `sub`), username and display name.
4. The gate calls `IUserAccountService.SignInAsync`, which creates the user on the
   first sign-in. A blocked user is refused: the library redirects to
   `/signin?denied=1` and no session cookie is issued. Otherwise the gate adds
   `dit:uid`, `dit:stamp` and one role claim per role (`UserClaimsFactory`); the
   library adds `telegram_user_id`.
5. `TrainerCookieEvents.SigningIn` makes the ticket persistent and handles the
   language preference: a valid `lang` cookie on this device is stored as
   `preferred_culture`; a device without one gets the stored preference back as a
   `lang` cookie. The root redirect order (cookie, `Accept-Language`, `en`) does
   not change; the preference only restores an explicit choice on a new device.
6. The browser lands on the return path. A remote failure or a declined consent
   lands on `/signin?error=1`.

Session cookie: `.dit.session` (`AuthCookieNames.Session`; the library has no
cookie-name option, so it is set by a `Configure<CookieAuthenticationOptions>`
registered after the library, ADR 0002), persistent, 30 days with sliding
expiration, `Secure`, `HttpOnly`, `SameSite=Lax`. An authorization failure
redirects to `/signin?denied=1&ReturnUrl={original path and query}`. Signing out
lands on the sign-in page of the visitor's culture (ADR 0003). For a path under
`/api` the cookie handler answers 401 or 403 without a `Location` header instead
of redirecting.

**Session lifetime and revalidation.** The cookie lasts 30 days and slides: a
request in the second half of the lifetime reissues it with a fresh 30 days (the
handler's own rule; a request after a revalidation reissues it too). The cookie
alone does not decide for long. `TrainerCookieEvents.ValidatePrincipal`
checks the session against the database at most every five minutes
(`TelegramAuth.RevalidationInterval`). The time of the last check is kept in the
ticket property `.dit.checked` (round-trip format), falling back to `IssuedUtc`;
`IssuedUtc` alone would not do, because the sliding renewal moves it. Inside the
interval no database call is made. At the check:

- a user that no longer exists or is blocked ends the session: the principal is
  rejected and the cookie deleted;
- when the security stamp or the set of roles differs from the cookie, the
  identity is rebuilt with the current `dit:uid`, `dit:stamp` and role claims and
  every other claim kept, `sub` above all, because antiforgery binds tokens to it
  and a refresh must not invalidate a token already handed out;
- otherwise only `.dit.checked` is updated, and the cookie is reissued.

A last check dated in the future (a clock stepped back, another instance ahead)
does not count as recent and triggers a check. Only component pages and `/api`
requests reissue the cookie after a check: on a static asset the check still runs
and still ends a blocked user's session, but nothing is reissued, so a fingerprinted
asset never gets a `Set-Cookie` or the handler's no-cache headers from this code
(the handler's own sliding renewal, once per half lifetime, can still reissue on an
asset request; a page request almost always comes first). If the database call
fails, the request is treated as anonymous and a warning with the exception type is
logged, but the cookie is kept, so one outage does not sign everyone out and the next
request checks again (fail closed per request). Such a request is marked
(`RevalidationOutage`) so it does not look like a sign-out: under `/api` it gets 503
`server-error` wherever it would otherwise get 401 `sign-in-required` (the challenge,
the read owner check, the endpoints' own checks), and so does `GET /api/antiforgery`,
whose token would otherwise be bound to the anonymous principal and fail the user's
next write, because the island client stops
syncing and shows "signed out" on a 401 but retries a 5xx with back-off; and the
sign-in page does not publish the anonymous owner to the user's other tabs.
Requests that arrive together after
the interval each check once and the last reissued cookie wins, so "at most once
per five minutes" is exact for a sequential client and bounded by the number of
concurrent requests otherwise.

So a block, an unblock-and-rotate or a role change takes effect within five
minutes, and a configured admin id removed from `App:AdminTelegramIds` loses the
role at the first check after the restart. The handler reads its clock from the
cookie options, so `TimeProvider` is set there; tests move it with
`FakeTimeProvider`.

**Key storage.** The data protection key ring is stored in PostgreSQL
(`data_protection_keys`, application name `dev-interview-trainer`), so sessions and
antiforgery tokens survive restarts; the keys are not encrypted at rest (ADR 0004).

**Compression and caching of signed-in pages (BREACH).** A signed-in page renders
the logout form's antiforgery token, so two middlewares sit right after
authentication (`PrivateResponseMiddleware` sits just before it, see below). Both act
on Razor component pages (HTML), recognised by the matched endpoint carrying
`ComponentTypeMetadata`, and never on static assets or health checks. The only
other response they touch is `/api/antiforgery`, which returns a token as JSON and
is covered by `BreachGuardMiddleware` alone; all other `/api` JSON is untouched.

- `BreachGuardMiddleware` sets `IHttpsCompressionFeature.Mode` to `DoNotCompress`
  for an authenticated component page and for `/api/antiforgery` (any letter case,
  with or without a trailing slash, like the endpoint's own routing). The feature is
  null when the client sent no usable `Accept-Encoding`, in which case nothing is
  compressed anyway. Authenticated `/api` JSON stays compressed: it carries no
  token. Anonymous pages carry no token and stay compressed.
- `PrivateResponseMiddleware` sends `Cache-Control: private, no-store` on an
  authenticated component page, written when the response starts so it replaces
  the antiforgery service's own header. It is registered before authentication
  so that its start-of-response callback runs after the cookie handler's, whose
  no-cache headers on a reissued cookie would otherwise win; a test covers a page
  that reissues the cookie. Static assets get nothing from either middleware, so a
  signed-in request sees the same `Cache-Control` as an anonymous one (a published
  build serves fingerprinted assets as `max-age=31536000, immutable`, which
  `ReleaseSmokeTests.FingerprintedAssets_AreImmutable_AnonymousAndSignedIn` asserts
  literally on the published build; the unpublished test host does not, so the web
  tests compare signed-in with anonymous).
- The reverse proxy must not compress application responses either (the nginx site sets `gzip off`).

Admins are the Telegram ids in `App:AdminTelegramIds`, read once at startup into
`AccessOptions`. The Admin role is computed, never stored, so a change takes
effect on restart, which every deploy does. The `Admin` authorization policy
requires that role.

The admin page (`/{culture}/admin`, ADR 0007) reads through `IAdminQueries`
(`Trainer.Infrastructure/Admin/AdminQueries.cs`): the overview probes the database with
`CanConnectAsync` (an unreachable database yields `DatabaseReachable = false` and empty
counts instead of an error), lists the last 10 `content_imports` rows and counts users,
blocked users and answers received in the last 24 hours (`received_at`, the server clock,
not the client-supplied `answered_at`), backed by `ix_answers_received_at`. The user list is ordered by last sign-in; the
search text is trimmed and cut to 100 characters, matches a Telegram id exactly when it is
a number, and in every case matches a case-insensitive `ILIKE` substring of the display
name or username, with `%`, `_` and the backslash escaped so they are literal. Roles shown include the
computed Admin. Blocking goes through `IUserAccountService.SetBlockedAsync`, which rotates
the security stamp and refuses a configured admin; the page posts the form to itself, so a
failed antiforgery check is answered 400 by the framework before the handler runs.

Return target: the library's challenge endpoint `/signin-telegram/challenge` is
public. Before 1.1.1 its local-URL check accepted control characters
(`/%09/evil.test` became `/<tab>/evil.test`, which a browser reads as
`//evil.test`); 1.1.1 refuses those itself and falls back to `/`. The
`OnTicketReceived` hook stays as defence in depth and validates the final target again
(`SignInRedirectEndpoint.ResolveReturnPathAfterSignIn`): `/`, or a path inside a
culture that passes the `/lang/` rules; an unsafe path inside a culture becomes
that culture's home, anything else becomes `/`; non-ASCII characters are
percent-encoded because the library writes the value into `Location` as is.

Logging: `SignInLogging.AddSignInLogRules` turns the library's `TelegramOidc`
category off (before 1.1.1 it logged the username and the client IP; 1.1.1 does so only with
`LogPersonalData`, which defaults to false, and the filter stays as defence in depth) and holds
`Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler` and
`Microsoft.AspNetCore.Hosting.Diagnostics` at Warning (below that they print the
challenge and callback URLs with the authorization code, state and nonce, and
every request line). The rules are added for every provider as well, because a
provider-specific level such as `Logging:Console:LogLevel:Default` would
otherwise outrank a provider-agnostic rule. `SignInLogSink` writes one entry per attempt,
`Sign-in {Outcome} for Telegram id {TelegramUserId}: {Reason}`, at Information
for a success and Warning otherwise, where `Reason` is one of `ok`,
`gate-denied`, `missing-id`, `access-denied`, `remote-failure`. Names, the
picture, the IP and the raw failure detail are never logged, and there is no
sign-in audit table. The OIDC handler itself logs an error response verbatim at
Error level, so an `OnMessageReceived` hook drops `error_description` and
`error_uri` (free text from the provider) before it does.

## API

The WebAssembly islands talk to a minimal API under `/api` on the same origin,
authenticated by the session cookie (spec 8.8). It is mapped in
`Api/ApiEndpoints.MapTrainerApi`; bodies and responses are serialised through the
source-generated `TrainerJsonContext` (inserted first in the resolver chain by
`ConfigureHttpJsonOptions`), and the endpoints only parse, call an Application
interface and map the result.

| Method, path | Auth | Write checks | Rate policy | Body → response |
|---|---|---|---|---|
| `GET /api/antiforgery` | any | | | → `AntiforgeryTokenDto` (`GetAndStoreTokens`; never compressed) |
| `POST /api/sessions/practice` | any | optional `X-Outbox-Owner` | `api-reads` | `PracticeSessionRequest` → `SessionDto`; anonymous `status: "due"` ⇒ 401 `sign-in-required` |
| `POST /api/sessions/mock` | any | optional `X-Outbox-Owner` | `api-reads` | `MockSessionRequest` → `MockSessionDto` |
| `POST /api/sessions/review` | signed in | optional `X-Outbox-Owner` | `api-reads` | `ReviewSessionRequest` → `SessionDto` |
| `POST /api/answers/batch` | signed in | `X-CSRF-TOKEN`, `X-Outbox-Owner`, optional `X-Client-Utc-Offset` | `api-writes` | `AnswerBatch` → `AnswerBatchResponse`; more than 500 answers ⇒ 400 `batch-too-large`; `version` ≠ 1 ⇒ 400 `validation` |
| `PUT /api/mock-sessions/{id}` | signed in | `X-CSRF-TOKEN`, `X-Outbox-Owner` | `api-writes` | `MockSummaryDto` → 204; an id that is not a ULID or belongs to another user ⇒ 404 `not-found`; an invalid summary ⇒ 400 `validation` |
| `GET /api/profile?culture=&stack=` | signed in | optional `X-Outbox-Owner` | `api-reads` | → `ProfileDto`; unknown culture or stack ⇒ 400 `validation` |
| `PUT /api/questions/{id}/rating` | signed in | `X-CSRF-TOKEN`, `X-Outbox-Owner` | `api-writes` | `RatingRequest` → `RatingDto`; a value other than `like` or `dislike`, or an id that is not a ULID ⇒ 400 `validation`; a retired or unknown question ⇒ 404 `question-not-found` |
| `DELETE /api/questions/{id}/rating` | signed in | `X-CSRF-TOKEN`, `X-Outbox-Owner` | `api-writes` | → 200 `RatingDto` with a null value, also when nothing was rated or the question is retired; a bad id ⇒ 400 `validation` |
| `POST /api/questions/{id}/reports` | signed in | `X-CSRF-TOKEN`, `X-Outbox-Owner` | `api-writes` | `ReportRequest` → `ReportResponse`: 201 `created`, 200 `updated` (an open report of the same reason was edited); 429 `report-limit` with `Retry-After` in whole seconds until the oldest counted report leaves the window; a bad id, unknown reason, unsupported culture or a comment over 1000 characters ⇒ 400 `validation`; a retired or unknown question ⇒ 404 `question-not-found` |

**Reads and the owner.** Session reads and the profile run `ReadOwnerCheckFilter`:
when the request carries `X-Outbox-Owner` (a Telegram user id, or `anon`) that is
not who the cookie says is signed in now, the answer is 409 `owner-mismatch`
before anything is read. Islands bind their page's owner once
(`TrainerApiClient.BindOwner`) and every read names it, so a tab left open across
a sign-in or sign-out in another tab gets `AccountChanged` instead of the new
account's sessions or progress under its old state. When the header names a user
but no session is signed in at all (the cookie expired, or ended in another tab)
the answer is 401 `sign-in-required` and the island shows its signed-out state.
A read without the header is served as before.

**Writes.** A request whose declared body is over 256 KB is refused with 413 before
anything else (`ApiErrorMiddleware`, even before authentication). After that a write
passes, in this order: authorization (401), the write rate
limit (429), JSON binding (400, 413 for a chunked body, 415), `AntiforgeryValidationFilter`
(`IAntiforgery.ValidateRequestAsync` on the `X-CSRF-TOKEN` header, 400
`antiforgery`) and `OwnerCheckFilter` (ADR 0008: `X-Outbox-Owner` must be one
positive decimal integer equal to the session's `telegram_user_id`, else 409
`owner-mismatch` and nothing is stored). The filter also reads
`X-Client-Utc-Offset`: an integer within ±840 minutes
(`IAnswerRecorder.MaxUtcOffsetMinutes`) reaches the recorder, anything else is
ignored. Antiforgery tokens are bound to the user (`sub`), so a token fetched
before signing in fails after it; the client refetches on sign-in, sign-out and a
400 `antiforgery`.

**Session reads (ADR 0008).** They are `POST` because an anonymous `new` filter
sends up to 1000 excluded ids, and they change no state, so they take no
antiforgery token. Only `application/json` binds (415 for anything else,
including a missing content type) and there is no CORS policy, so a cross-site page
can send them only after a preflight, which is refused, and never reads the answer.

**Parsing (`ApiRequestParser`).** JSON binding enforces neither nullability nor
required members, so every body is checked structurally before a service sees it:

- Batch: a null envelope or `answers`, or a version other than 1, is 400
  `validation`; more than 500 answers is 400 `batch-too-large`. Inside a valid
  envelope a bad entry never fails the batch: a null entry or one without a valid
  ULID `clientAnswerId` is acknowledged as `("", "invalid")`, an entry with any
  other invalid field (question id, grade, mode, a mock id that does not match the
  mode, a missing `answeredAt`) as `(id, "invalid")`. The first entry with a client
  id decides, later ones are dropped. The response lists the recorder's accepted
  and rejected ids plus these.
- Practice, mock, review: null lists or elements, an unknown culture, level, type,
  status or a malformed question id are 400 `validation`; so is an unknown stack.
  Unknown topics are dropped, but a filter whose topics are all unknown is 400 (an
  empty list would mean every topic). Counts are clamped to `SessionLimits`
  (practice and review 1..50, mock 5..50); more than 1000 excluded ids is 400.
- Mock summary: a known stack, level and topics (each once), a question count of
  1..50, `finishedAt` not before `startedAt`, non-negative counts, and
  `knew + partial + didntKnow + unanswered` over all topics equal to
  `questionCount`; otherwise 400 `validation`. The service then judges the times
  with `AnswerTimePolicy` (rejected ⇒ 400 `validation`).

**Errors (`ApiErrorMiddleware`).** Every non-success `/api` response has an
`ApiErrorDto { code, detail }` body with one of the `ApiErrorCodes`. The middleware
is mapped with `UseWhen` for `/api` right after status code pages and before
routing, so it wraps authentication, authorization, rate limiting, binding,
endpoint filters and unmatched paths. It switches status-code re-execution off
(otherwise a 404 would become the HTML not-found page) and writes the body for
every bodiless error status: 400 `validation`, 401 `sign-in-required` (the cookie
handler answers 401 or 403 without a `Location` and without a body), 403
`forbidden`, 404 `not-found`, 405 `method-not-allowed`, 413 `payload-too-large`,
415 `unsupported-media-type`, 429 `rate-limited`. A `BadHttpRequestException`
(thrown in Development for malformed JSON, binding failures and wrong content
types) is answered with its status and the matching code. Any other exception is
logged at Error with the method and path and answered 500 `server-error` without
details. Endpoints write their own `antiforgery`, `owner-mismatch`,
`batch-too-large` and `validation` bodies. A `GET` to a `POST`-only path is 404,
not 405: the static-asset fallback route accepts every `GET`.

**Limits.** Request bodies are limited to 256 KB: a declared `Content-Length`
above it is refused with 413 before anything is read (the in-memory test server
ignores the server's body size limit), and the body size feature is set to the
same value for chunked bodies. Every `/api` response is `Cache-Control: no-store`,
set when the response starts so it wins over the cookie handler's headers.
Responses are compressed like any other JSON except `/api/antiforgery`
(`BreachGuardMiddleware`).

**Rate limits (`ApiRateLimits`).** Fixed one-minute windows, configured in
`App:RateLimits` (`RateLimitOptions`): `api-writes` (default 60 per minute) per
signed-in user (`dit:uid`), `api-reads` (default 30) per signed-in user, else per
client address, else one shared `unknown` partition. A rejected request is 429
with `Retry-After` (seconds) and `rate-limited`. The limiter runs after
authorization, so an anonymous call to a signed-in endpoint is answered 401 without
being counted. A `POST` to an `/api` read without a JSON content type never reaches the
limiter: routing refuses it (415) while it selects the endpoint, so a cross-site
`text/plain` post cannot spend a visitor's budget (`Reads_NonJsonPosts_AreRefusedWithoutSpendingTheBudget`).
Anonymous reads are counted per client address, so visitors behind one carrier-grade
or office NAT share one budget: size `ReadsPerMinute` for the busiest shared address,
not for one person. Startup fails when either limit is not a positive integer. Every
test host runs with 100 000 per minute (`TrainerWebFactory`, and through it
`AppHostFixture`) except the dedicated low-limit tests.

## Security headers

`SecurityHeadersMiddleware` adds these headers to every response:

```text
Content-Security-Policy: default-src 'self'; script-src 'self' 'sha256-<theme script hash>'; style-src 'self'; img-src 'self' https: data:; font-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'
X-Content-Type-Options: nosniff
Referrer-Policy: strict-origin-when-cross-origin
```

- There is no `unsafe-inline`. The only inline script is the theme script, allowed
  by hash. `ThemeScript` renders the exact source and computes the hash from it,
  so the two cannot drift apart.
- There are no third-party origins: fonts, styles and scripts are served from the
  site. Images from content may come from any `https:` origin.
- Static assets are served through `MapStaticAssets` (fingerprinted and
  compressed at build time).
- Island pages (`[InteractiveIslandPage]`) get a different policy, set in
  `Response.OnStarting` once routing has matched the endpoint: `script-src` adds
  `'wasm-unsafe-eval'` (the runtime compiles WebAssembly) and `base-uri` is
  `'self'` (for their `<base href="/">`). Every other response keeps the policy
  above. Nothing on an island page may use inline styles, so dynamic sizes are
  drawn with `<progress>`, `<meter>` or SVG attributes; the E2E fixture fails a
  test on any CSP violation.
- The optional analytics script (`/stats/script.js`, ADR 0018) is same-origin, so it needs no CSP
  entry and no nonce, and the cached HTML stays identical for every visitor. Its `POST /stats/api/send`
  is covered by `default-src 'self'`.

## Startup, readiness and health

`ContentStartupImporter` is a `BackgroundService`. It yields first, so a slow or
blocking read cannot delay the host from listening, then imports the content from
`App:ContentRoot`, refreshes the snapshot and sets `ReadinessState`:

| State | Meaning |
|---|---|
| `Pending` | The startup import has not finished. |
| `Ready` | The content is imported or was already current. |
| `Failed` | The import was rejected, threw a non-transient error, or kept failing transiently until the backoff attempts ran out. The previously stored content keeps serving, the snapshot is loaded from the database anyway, and the issues are logged as errors. |

Transient database failures (`NpgsqlException.IsTransient`, `TimeoutException`,
socket errors, found anywhere in the exception chain) are retried with exponential
backoff, because on a fresh deployment the app can start before PostgreSQL accepts
connections. Delays use the injected `TimeProvider` and stop at once when the host
shuts down. A rejected import or a non-transient error is not retried. When the
backoff attempts run out the state becomes `Failed` and the stored content is
loaded, but the importer keeps retrying at `MaxDelay` until it succeeds or the
host stops, so a long database outage heals without a restart.

`Failed` is not terminal. `ReadinessReportingImportService` wraps
`IContentImportService`, and every later `Imported` or `Unchanged` result in this
process sets the state back to `Ready`, but only after the content snapshot has
been reloaded: routing answers 404 for every stack while the snapshot is empty,
so a host must not report ready before it has the taxonomy. If the reload fails
after a committed import, the result is still `Imported` (the failure is logged
at error level) but the state stays not `Ready`, and the startup importer reloads
the snapshot again.

- `/health/live` runs no checks and answers 200 while the process is up.
- `/health/ready` runs the `ReadinessHealthCheck`: 200 only in `Ready`, 503
  otherwise.

Before importing, the importer checks that `<root>/schema-version` exists. When it
does not (the root is missing, or `current` is not created yet), there is nothing to
import, and the outcome depends on what the database holds
(`IContentStatusQueries.GetActiveHashAsync`):

| Active import | Result |
|---|---|
| None (a fresh install) | A warning, "No content release yet", and `Ready`: the site serves an empty stack list until `deploy-content` runs and calls `POST /internal/content/import`. |
| One exists | An error and `Failed`, after the stored snapshot is loaded: the previous content keeps serving, but the probe stays red because the checkout that should hold it is gone. |

A transient database error in this query follows the same retry loop as the import.

A content root that exists but has no `schema-version` file counts the same way: on a database with no active import it reports `Ready` with no content (and the warning above), instead of a rejected import, so a broken checkout on a fresh install is visible only in the log and the empty site.

A relative `App:ContentRoot` is resolved against the application content root
(the `src/Trainer.Web` directory in development), not the working directory.

## Configuration

| Key | Meaning |
|---|---|
| `ConnectionStrings:Trainer` | PostgreSQL connection string. Required. Never committed: set it with an environment variable (`ConnectionStrings__Trainer`) or user secrets. |
| `App:ContentRoot` | Directory with the content checkout. `appsettings.Development.json` sets `../../content`. |
| `App:PublicBaseUrl` | Absolute origin for canonical, `hreflang` and sitemap URLs, without a trailing slash. Outside Development and Testing it must be an absolute `https` URL, or startup fails. With output caching, a request-derived origin would let a spoofed `Host` header poison cached URLs. |
| `AllowedHosts` | Host filtering. `appsettings.json` ships `*`. Outside Development and Testing, `*` or an empty value is replaced at startup by the host of `App:PublicBaseUrl` plus `localhost`, `127.0.0.1` and `[::1]` (so container and load-balancer probes on loopback still work), and the change is logged at Information, so requests for any other `Host` get 400. An explicit value other than `*` is kept as is. |
| `App:DefaultStack` | Stack shown on `/{culture}`. Default `dotnet`. |
| `App:StartupImport:MaxAttempts` | Number of backoff attempts of the startup import on transient database failures; after them it keeps retrying at `MaxDelay`. Default `8`. |
| `App:StartupImport:InitialDelay` | Delay before the second attempt, doubled after every failure. Default `00:00:01`. |
| `App:StartupImport:MaxDelay` | Upper bound of the delay between attempts. Default `00:00:30`. |
| `App:ForwardedHeaders:KnownNetworks` | Array of CIDR networks of the trusted reverse proxy (for example `172.18.0.0/16`), added to the default loopback trust. Env var form: `App__ForwardedHeaders__KnownNetworks__0`. An invalid entry fails startup with the key and value in the message. |
| `App:ForwardedHeaders:KnownProxies` | Array of IP addresses of the trusted reverse proxy, added to the default loopback trust. Invalid entries fail startup. |
| `TelegramOidc:ClientId` | Telegram bot id (the OIDC client id). Without it the site runs and the sign-in page says sign-in is not configured. Required in Production, or startup fails. |
| `TelegramOidc:ClientSecret` | OIDC client secret from BotFather. Only from environment variables (`TelegramOidc__ClientSecret`) or user secrets, never `appsettings*.json` (a test checks). Required in Production. |
| `TelegramOidc:MetadataAddress` | Discovery address of the fake provider. Honoured only in the `Testing` environment (where https metadata is not required); anywhere else startup fails. |
| `App:AdminTelegramIds` | Telegram user ids with the Admin role: an array (`App__AdminTelegramIds__0=123`) or one comma-separated value (`App__AdminTelegramIds=123,456`). Read once at startup; a change takes effect on restart. A non-positive or non-numeric entry fails startup with the key and value. |
| `App:RateLimits:WritesPerMinute` | `/api` writes per signed-in user per minute. Default `60`. Must be positive, or startup fails. |
| `App:RateLimits:ReadsPerMinute` | `/api` session and profile reads per signed-in user, else per client address, per minute. Default `30`. Must be positive, or startup fails. |
| `App:RateLimits:SearchPerMinute` | Catalog requests with a search term (`q`) per client address per minute (ADR 0011); a request over the limit gets 429 with `Retry-After` and a localised HTML page. Cached catalog pages without a term are never limited. Default `30`. Must be positive, or startup fails. |
| `App:Feedback:ReportsPerDay` | New question reports one signed-in user may file within a rolling 24 hours (ADR 0016); editing an open report does not count. Default `10`. Must be positive, or startup fails. |
| `App:Analytics:WebsiteId` | Umami website id (a GUID) for cookieless page-view counting (ADR 0018). Unset or empty: no script is rendered anywhere. A value that is not a GUID fails startup. |
| `App:ApplyMigrationsOnStartup` | Applies EF Core migrations at startup. Allowed only when `ASPNETCORE_ENVIRONMENT` is `Development`; in any other environment startup throws. |
| `App:MaintenanceFlagPath` | Path of a flag file. While it exists, `MaintenanceGateMiddleware` (registered right after `UseForwardedHeaders`, before routing) answers 503 with `Retry-After: 60` and an empty body to every request whose method is not `GET`, `HEAD` or `OPTIONS` and to everything under `/signin-telegram`, so no form post, `/api` write or sign-in callback reaches a handler. The check is cached for one second. Only the `/internal` path prefix on the internal port is exempt (the pages and `/api` are also served on that port and stay gated), and the startup import is not an HTTP request, so content still loads during a restore. This is the second layer: nginx answers 503 to every request, GET included, while the flag exists, so the application lets a read through only when it does not arrive via nginx. An unreadable flag (a missing mount, a permission error) counts as absent: the gate fails open, so a broken mount never takes the site down. The 503 carries `Cache-Control: no-store` and the security headers. Unset (the default) leaves the gate off; Compose sets `/flags/maintenance`, the read-only mount of `${APP_DIR}/flags`. The gate is in the application because nginx buffers a request body before proxying, so a request admitted before the flag appeared can still arrive after it. |
| `App:OpenGraph:CacheDirectory` | Directory of the rendered Open Graph PNG cache (ADR 0017). Unset: a `dit-og` directory under the system temp path. Compose sets `/var/cache/dit/og`, a volume. A missing or unwritable directory disables caching only (one warning is logged); the directory is emptied after every committed import. |
| `App:Internal:Port` | Local port of the internal endpoints (`/internal/...`, see [Internal endpoints](#internal-endpoints)). `0` (the default) does not map them. Required positive in Production, or startup fails. Env var form: `App__Internal__Port`. |
| `ContentImport:Token` | Bearer token of `POST /internal/content/import`: at least 32 characters whenever `App:Internal:Port` is positive, and required in Production, or startup fails. Only from environment variables (`ContentImport__Token`) or user secrets, never `appsettings*.json`. It is never logged. |

`StartupMigrations.EnsureAllowed`, `PublicBaseUrlValidation.EnsureValid`,
`ForwardedHeadersSetup.Parse`, `AllowedHostsDefault.Apply` and `InternalStartupCheck.EnsureValid` run before the host is built.

### Behind the reverse proxy

Production runs behind nginx, which ends TLS. `UseForwardedHeaders` is the first
middleware and honours only `X-Forwarded-For` and `X-Forwarded-Proto`, with
`ForwardLimit = 1`, and only from a peer that is loopback or listed in
`App:ForwardedHeaders:KnownNetworks` or `KnownProxies`. A request from any other peer keeps
its own scheme and address, so a client cannot claim to be HTTPS. `X-Forwarded-Host` is
never honoured: `Host` stays the real one and `AllowedHosts` keeps filtering on it.
The proxy must send both headers. With `ForwardLimit = 1` the last entry of
`X-Forwarded-For` is the one used, so a proxy that appends to a client-supplied value is
also safe.
Because the scheme is correct behind the proxy, the `lang` cookie gets its `Secure` flag
in production. The application itself neither redirects to HTTPS nor sends HSTS; configure both at the proxy.

The nginx templates in `deploy/nginx/` implement the proxy side (the `${DOMAIN}` and `${APP_DIR}` placeholders are filled by
`envsubst` during bootstrap). The site overwrites `X-Forwarded-For` with `$remote_addr` and sets `X-Forwarded-Proto`, so a
client-supplied chain never reaches the application. It redirects port 80 to HTTPS, sends HSTS without `preload`, answers
`/internal/` with 404, turns compression off for proxied responses and returns 503 while `${APP_DIR}/flags/maintenance`
exists. Umami is reachable only through two exact paths, `/stats/script.js` and `/stats/api/send` (rate limited, 16 KiB body
limit); any other `/stats/` path is 404. Other host names get no response, and the TLS server uses the `http2` parameter of
`listen` because the nginx 1.24 that Ubuntu 24.04 ships predates the standalone directive. `nginx.conf.template` switches the
error log and the default access log off for every server, and the site's own access log records only the method, path,
protocol, status, size and duration: no address and no query string ([ADR 0007](adr/0007-admin-url.md)).
`deploy/tests/nginx-check.sh` renders the templates in an `ubuntu:24.04` container, runs `nginx -t`, drives the cases above and
asserts that no log file contains a request query or `127.0.0.1`; it is skipped when Docker is unavailable.

The server is IPv4-only: the templates have no `listen [::]` lines, so the domain must have no AAAA record. The reason is that
a container without IPv6 cannot bind `[::]` in nginx, which would make `nginx-check.sh` fail on runners without it. Umami is
pinned to `X-Forwarded-For` through `CLIENT_IP_HEADER`, and the proxy also blanks the other client-IP headers Umami knows
(`CF-Connecting-IP`, `X-Real-IP`, `Forwarded` and the rest), so a visitor cannot choose the hash input. Rate-limited requests
to `/stats/api/send` get 429, which keeps them distinct from the 503 of maintenance mode.

## Internal endpoints

Two endpoints serve the deployment, not visitors (ADR 0015). They are mapped only
when `App:Internal:Port` is positive, and the production host listens on that port
through a second Kestrel endpoint (`http://+:8081`, published on the server's
loopback only).

| Endpoint | Auth | Result |
|---|---|---|
| `GET /internal/version` | none: the port is the boundary and the values are not secret | `InternalVersionDto`: `appVersion` (the Web assembly informational version, `dev` when absent), `contentSchemaMin`, `contentSchemaMax`, `renderVersion` (from `ContentSchema`) and `activeContentHash` (the `content_imports` row with `is_active`, null when none). The hash lets a deploy script tell whether an import call that failed on the wire had committed. |
| `POST /internal/content/import` (no body) | `Authorization: Bearer <ContentImport:Token>` | Imports `App:ContentRoot` and answers `InternalImportDto`: `status` (`imported`, `unchanged` or `rejected`), `hash`, `imported`, `retired` and `issues` (`path`, `code`, `message`). 200 for `imported` and `unchanged`; 422 for `rejected` and for a content root that is not configured or does not exist (one issue with code `content-root-missing`). The output cache and the content snapshot are refreshed exactly as after a startup import. |

Rules that hold for both:

- `InternalPortFilter` answers an empty 404 unless `HttpContext.Connection.LocalPort`
  equals `App:Internal:Port`. The check is on the socket, never on the `Host` header,
  so `Host: x:8081` through nginx on the public port cannot reach the routes. The
  filter switches the status-code pages feature off for the request, so a 404 or 401
  stays empty instead of becoming the HTML not-found page.
- `ImportTokenFilter` compares the UTF-8 bytes of the presented token with
  `CryptographicOperations.FixedTimeEquals`; a token of another length is compared
  against a same-length dummy, so timing does not reveal the length. A missing or wrong
  token is 401 with `WWW-Authenticate: Bearer` and an empty body, and nothing imported.
  The header is never logged.
- The import runs under `IHostApplicationLifetime.ApplicationStopping`, not
  `RequestAborted`: a client that disconnects or times out never cancels an import
  half-way. A retry queues behind the running one on the importer's advisory lock and
  gets a definitive answer (200 or 422). An import cut short by the host shutting down
  answers 503, which is retryable on the next process.
- Any other path under `/internal/`, with any method, answers an empty 404 on every port
  (a catch-all behind the same `InternalPortFilter`), so it never reaches the HTML pages.
- Bodies are serialised through the source-generated `InternalJsonContext` (camelCase).
- `InternalStartupCheck.EnsureValid` fails startup when the port is positive and the
  token is shorter than 32 characters, and in Production when the port is not positive
  or the token is missing or blank, or when it starts or ends with whitespace (the bearer
  comparison does not trim).
- The in-memory test server has no sockets: `LocalPortFromHeaderStartupFilter`
  (registered only by `TrainerWebFactory`) copies `X-Test-Local-Port` into
  `Connection.LocalPort`, and `TrainerWebFactory.InternalPort` sets the port the host
  maps the endpoints on. The `Release` suite runs the published build with a real second URL.

## Question feedback

Signed-in users like or dislike a question and report it with a reason (ADR 0016). The
domain types are in `Trainer.Domain/Feedback`: `QuestionRating` (`like`, `dislike`),
`ReportReason` (`wrong-answer`, `unclear-wording`, `outdated`, `broken-link`, `other`)
with their wire keys, `ReportComment` and `ReportId`. `IQuestionFeedbackService` writes
and `IQuestionFeedbackQueries` reads; both are scoped services over `TrainerDbContext`.

- A rating is one `INSERT ... ON CONFLICT (user_id, question_id) DO UPDATE`, so a double
  click stores one row; `null` deletes it. A retired question, or one in a retired topic
  or stack, answers `QuestionNotFound` for setting a rating and for reports; clearing a
  rating always works.
- `ReportComment.TryNormalise` runs at the edge: it turns `\r\n` and `\r` into `\n`,
  drops control characters other than `\n` and `\t`, trims, maps blank to no comment and
  rejects more than 1000 characters. It also drops zero-width characters and bidirectional
  overrides. `ReportAsync` runs it itself (an over-long comment throws `ArgumentException`)
  and stores the result; the service never
  logs it.
- A report takes `pg_advisory_xact_lock(hashtextextended('feedback:' || user_id, 0))`.
  Under the lock, an open report of the same user, question and reason is updated (comment
  and culture; `Updated`, not counted). Otherwise the user's reports newer than 24 hours are
  counted with the server `TimeProvider`: `App:Feedback:ReportsPerDay` or more gives
  `LimitReached` with `RetryAt` = the oldest counted report plus 24 hours; fewer inserts
  the report (`Created`).
- `ResolveReportAsync` closes an open report once, storing the time and the admin.
- The queries return a user's ratings for a set of questions, reports newest first
  (25 per page, open or resolved; a page out of range is clamped) with the reporter's
  Telegram id and name, and the live questions with at least one dislike, most disliked first.
- Feedback is not queued in the browser outbox: a failed write shows an error and can be
  repeated.

### Question page forms

`Components/Shared/QuestionFeedbackForms.razor` renders a `<section id="feedback">` on the
question page, only when the request is signed in and the question is not retired. It holds
two static SSR `POST` forms with an antiforgery token each (no JavaScript, no island):

- `question-rating`: submit buttons `name="value"` with `like`, `dislike` and (only while
  a rating is stored) `clear`. The stored rating shows as `aria-pressed="true"` with the
  same check mark as the island bar; pressing a pressed rating again stores it again, which
  is harmless.
- `question-report` inside a `<details>`: radio `reason` (five required options),
  `<textarea name="comment" maxlength="1000">`.

The handler reads the form, calls `IQuestionFeedbackService` with the question id and
culture the component was given (never a posted id), and answers with post-redirect-get: a
`302` to the page path plus `?feedback=<code>#feedback`. Codes: `rated`, `cleared`,
`reported`, `report-updated`, `report-limit`, `rate-limited` and `invalid` (an unknown value, an over-long
comment, a retired question). Both handlers first take a permit from `FormWriteRateLimiter`: the
per-user write limit of the `/api` writes (`App:RateLimits:WritesPerMinute`, same partition) in its own
window, because a page POST never passes the `/api` limiter. The page maps the code to a localised notice inside the
section; an unknown code shows nothing. A post without a valid token is answered 400 by
the framework before the handler runs, and a post from an anonymous visitor finds no form
of that name on the page and is refused. `feedback` is not in the output-cache key and an
anonymous render never reads it.

The stored rating comes from `IQuestionFeedbackQueries.GetMyRatingsAsync` in the same
signed-in branch of `QuestionPage` that loads the statuses. The `.feedback-bar` rules the
forms share with the island bar live in `site.css`, because the question page does not load
`islands.css`.

### Admin: reports and most disliked

The admin page lists two more sections, read through `IQuestionFeedbackQueries`:

- **Reports**: open reports by default, resolved ones with `?reports=resolved`, 25 per page
  with `reportsPage`. Columns: date (formatted like the users table), the question as a
  link in the culture of the report, reason, comment, reporter name with Telegram id, and a
  `POST` button per open row that points (`form="resolve-report"`, `name="reportId"`, value the report id) at one
  stable form `resolve-report` with the antiforgery token. The form exists on every render, so a
  resubmit of a report resolved meanwhile still reaches the handler. The handler redirects (post-redirect-get)
  to the admin page with `?notice=report-resolved` or `report-not-found`, keeping the filter and page numbers. The
  comment is user text: it is HTML-encoded by Razor and shown with `white-space: pre-wrap`.
  `ResolveReportAsync` closes an open report once; a report that is not open gives the
  "already resolved" warning notice and changes nothing.
- **Most disliked**: the top 20 questions by dislikes (`GetMostDislikedAsync`).

Both are loaded in the same guarded block as the user list, so an unreachable database
shows empty sections instead of an error. The page keeps the user search, both page numbers
and the report filter in its links.

## Migrations policy

- Production never applies migrations at startup. A schema change is a deliberate
  deployment step (`deploy-app` runs the migration bundle; see [deployment.md](deployment.md)).
- Local development may set `App:ApplyMigrationsOnStartup=true` in the
  Development environment only.
- Tests apply migrations once into a template database (`PostgresFixture`) and
  give each test a private copy.
- Migrations live in `Trainer.Infrastructure/Persistence/Migrations` and are
  marked as generated code in `.editorconfig`.
- A migration must keep the previous release working. A failed deploy rolls the image
  back after migrations ran, and every deploy runs the previous image against the
  migrated schema, so a migration expands first (new tables, nullable or defaulted
  columns, non-unique indexes) and the release after the one that stopped using a
  structure contracts it. `MigrationSafetyTests.NewMigrations_AreExpandOnly` rejects,
  for every migration later than `20261115000000`, drops and renames, a column made
  non-nullable, narrowed or stripped of its default, a non-nullable column added without
  a default, a unique index, foreign key, check or unique constraint on a table that
  existed before the migration, and any raw SQL. `[ContractMigration("reason")]`
  (`ContractMigrationAttribute`) lifts only this static check; the deploy still runs the
  deployed release's own `Release` suite on the migrated database.
- The production image carries the migrations as a framework-dependent EF Core bundle at
  `/app/migrate/efbundle`. It reads the connection string from `TRAINER_DB` through
  `TrainerDbContextFactory`, never from an argument.

## Snippet sandbox and the content CLI

`DockerSnippetRunner` builds and runs each snippet in two throw-away containers
with `SandboxOptions` limits, from an image pinned by digest. The build container
has no network and 2 GB memory, and on Linux hosts it runs as the host user (on Docker Desktop no `--user` is set, because Docker maps the ownership of shared folders itself). The run container has no network,
a read-only root and output directory, 256 MB memory, `--pids-limit 128`,
`--cap-drop ALL`, `no-new-privileges`, logging disabled, `TZ=UTC` and a 10 second
deadline enforced from the host with `docker kill`.

`SnippetVerifier` compares the results with the declared expectations and reports
the `snippet-*` codes. The runner and the verifier are in `Trainer.Snippets`; only
the CLI uses them.

Container cleanup (`kill`, `rm -f`) never reuses the caller's token or the snippet
deadline: each command gets its own `SandboxOptions.CleanupTimeout` (15 s), a hung
`docker` client is killed with its process tree, and a second `rm -f` after the client
is reaped removes a container that was created at the last moment. Both containers set
`--memory-swap` equal to `--memory`.

`check-links` collects every absolute `https` URL from the `links` and from the Markdown
of both locales, removes fragments and duplicates, and checks each with `HEAD` (falling
back to `GET` on 403, 405 and 501). Redirects are followed by hand, at most 5; one
attempt has a 15 s budget; a network error, a 5xx, a 429 or a timeout is retried once
after 2 s; four URLs are in flight at a time. The client sends the user agent
`dev-interview-trainer-link-check`, no cookies and no credentials. A link is `ok` (2xx),
`dead` (404, 410, other 4xx except 401/403/429, name resolution or TLS failure, redirect
loop or limit, a redirect from `https` to plain `http`) or `unknown` (401, 403, 429, timeout, 5xx, other network errors). Only
`dead` fails the run.

CLI exit codes: `0` success, `1` content issues, `2` usage error, `3` sandbox
failure (the content was not judged), `130` cancelled. CI uses `3` to report an
infrastructure failure separately from a content defect.

## Tests and CI

| Project | Scope |
|---|---|
| `Trainer.Domain.Tests` | Keys, levels, question types, grading rules and keys, persisted enum values. |
| `Trainer.Application.Tests` | `AnswerTimePolicy` boundaries and extreme dates, `MockComposer` (level mix, short pools, suggested count) and the JSON round trip of every contract DTO through `TrainerJsonContext`. |
| `Trainer.Content.Tests` | Reader, `YamlToJson`, hasher, validator, renderer, `SafeLinkPolicy`, schema generation, the repository content, and the content guide (`ContentGuideExampleTests`). |
| `Trainer.Snippets.Tests` | Result parsing, verifier, sandbox argument and cleanup unit tests, and the guard that `ci.yml` pre-pulls the default sandbox image; the Docker sandbox tests carry `Category=Docker`. |
| `Trainer.ContentCli.Tests` | The `check-links` command: URL collection over a temporary content tree, and the link checker against a loopback Kestrel server (never the internet). |
| `Trainer.Infrastructure.Tests` | Import, queries, user accounts, answer recording (idempotency, time policy, ordering, concurrent batches, `xmin` conflict retry), mock sessions and the session queries (filters, first question, due and review order, constant query count) over PostgreSQL (Testcontainers). |
| `Trainer.Web.Tests` | Pages, routing, SEO, cache, readiness, island hosting (where the runtime and the WebAssembly CSP may appear, bootstrap JSON, serialised auth state) and the `/api` endpoints (antiforgery, owner checks, JSON errors, rate limits, request parsing) through `WebApplicationFactory` and PostgreSQL. |
| `Trainer.Web.Client.Tests` | The WebAssembly client: RU/EN key parity of `ClientResource` and that every key resolves in its culture; browser storage, server clock, API client, outbox sync and account watcher against in-memory fakes; the question session model (attempts, grading eligibility, snapshots, island states, keyboard shortcut resolution). |
| `Trainer.Web.E2E` | Playwright and Chromium against the real site on Kestrel at `http://localhost:{port}` with a fake Telegram provider; `Category=E2E`. Each test class gets its own host, but the run shares one Chromium (`BrowserFixture`, an assembly fixture; tests isolate themselves in browser contexts): a browser and Playwright driver per class kept the machine CPU-bound at startup long enough for navigations to time out. `AppHostFixture.NewPageAsync` records CSP violations of every document a tab shows (`AssertNoCspViolations`) and counts `/api/sessions/` requests; `SignInAsync` signs a browser context in through the real OIDC flow. The island sweep (`IslandLayoutTests`, `IslandAccessibilityTests`, one shared `IslandSweepFixture` host) renders Practice (question and feedback), the signed-in question page with its feedback forms, Mock (answering and summary), Review, profile, sign-in, admin (with seeded report rows) and the 429 page of a rate-limited catalog search (its host allows one search a minute) at 375, 768, 1280 and 1920 px (dark), 375 and 1280 px (light) and 320 px (sign-in, the 429 page, Practice feedback, the question page feedback forms and profile also in Russian), measures each rendering once and checks overflow, the primary action, overlapping key elements, words broken in the header, code font size, CSP violations, 44 px targets for a touch screen, a mouse and a mouse with a touch screen, accessible names and one language per page in both cultures, the project's focus ring (a 2 px `--accent` outline) on every Tab stop of every swept page, and a single verdict announcement. Writes screenshots to `TestResults/screenshots`. |
| `Trainer.Web.Release` | The published Release build (`dotnet publish -c Release`, `DIT_RELEASE_PUBLISH_DIR`) started as a child process on `http://localhost:5080` over a migrated database (`DIT_RELEASE_CONNECTION`) and the repository `content/`, with a fake Telegram provider and Chromium; `Category=Release`, skipped when the two variables are unset (`PublishedAppFixture`). `ReleaseSmokeTests`: every island boots (Practice, Mock, Review, profile); Practice in RU and EN with the real DTOs (request and response bodies deserialised through `TrainerJsonContext`, acknowledged ids equal to stored rows), Mock and Review, public pages without `_framework/` requests, prefetched runtime URLs equal to what the island requests, every `_framework/` file requested once per cold load under its fingerprinted name (`Trainer.Web.staticwebassets.endpoints.json` labels), no CSP violation or failed runtime request, a signed-in warm visit loading the runtime from the browser cache, `X-Client-Utc-Offset: 420` from a device in `Asia/Novosibirsk`, the import map nonce and no development scripts in the HTML, and `Cache-Control: max-age=31536000, immutable` on fingerprinted assets for anonymous and signed-in requests. `IslandPerformanceTests`: the runtime transfer budget and Practice cold and warm timings under the phone profile (`docs/performance.md`); measurements go to `TestResults/perf/perf.json`. |
| `Trainer.Deploy.Tests` | The Compose file (loopback ports, digests, hardening, bounded logs), `pg_hba.conf`, the shell conventions of `deploy/` (strict mode, no xtrace, postgres commands as the `postgres` user), `deploy/tests/render-env.test.sh`, `lib.test.sh`, `backup-lib.test.sh` and `rotate.test.sh` (the exit codes of the rotation script against a stand-in for docker), `LinkCheckScriptTests` (the two `run:` scripts of `link-check.yml` under bash with a recording `gh` and a scripted `dotnet`: which exit codes may open, edit or close the issue), the systemd units and the conventions of the backup scripts (credentials never on a command line, only the rotation and umami-recovery guards allowed over a trainer backup, no automatic rename back), the structure of `deploy-smoke.yml`, `schema-compat.yml`, `content-checks.yml` and the `ci.yml` calls (permissions, pinned actions, inputs through `env`, the check list of `smoke.sh`) and that `deploy/tests/content` is a copy of the Web test content. |
| `Trainer.TestSupport` | Shared helpers: `PostgresFixture`, `TrainerWebFactory`, `ContentWorkspace`, and the sign-in doubles: `FakeTelegramOidcServer` (a loopback OIDC provider), `TelegramSignInDriver`, `SessionCookieReader`, `CapturingLoggerProvider`. |

xUnit v3 with the built-in `Assert` on Microsoft.Testing.Platform. Commands are in
`AGENTS.md`.

`.github/workflows/ci.yml` runs on pull requests and on pushes to `main`:

| Job | Steps |
|---|---|
| `build-test` | Restore, `dotnet format --verify-no-changes`, build with warnings as errors, unit and integration tests (without `Docker`, `E2E` and `Release`). |
| `content` | Calls `content-checks.yml` (read-only permissions, no secrets): build, `validate`, pull the pinned sandbox image, Docker tests, `run-snippets`; exit code 3 is annotated as an infrastructure failure. |
| `e2e` | Build, install Playwright Chromium, E2E tests, upload `TestResults/screenshots` as an artifact. |
| `release` | PostgreSQL started by `.github/actions/postgres-from-compose` from the image `deploy/compose/compose.yaml` pins (the only place its digest is written), build, `dotnet publish -c Release`, `dotnet tool restore`, `dotnet ef database update` against the service, install Playwright Chromium, the `Release` tests, upload `TestResults/perf` and `TestResults/release` (the measurements and the app log). Timings are held to regression budgets (8 500 ms cold, 4 750 ms warm, ADR 0014) and fail the job only when the workflow is dispatched by hand with `enforce = true` (`DIT_PERF_ENFORCE=1`); see `docs/performance.md`. |
| `schema-base` | Resolves the two commits the schema check compares: for a pull request `GITHUB_SHA` (the merge commit of the pull request merge branch) and the pull request base commit, for a push to `main` the pushed commit and `github.event.before`, for a manual run the optional `previous` input, which defaults to the parent of the dispatched commit (`HEAD^`). |
| `schema-compat` | Calls `schema-compat.yml` with those commits (read-only permissions). |
| `workflow-lint` | Downloads actionlint at a pinned version, checks the archive against a pinned SHA-256 before extracting it, and lints every workflow under `.github/workflows` (ShellCheck runs over each `run:` script; read-only permissions, no `needs`). |

`content`, `e2e`, `release` and `schema-compat` start after `build-test` succeeds; `workflow-lint` and `schema-base` start at once. The workflow can also
be started by hand (`workflow_dispatch`), which is how the enforced performance run is made. A newer push cancels the
running job of the same pull request. Every push to `main` has its own concurrency group
(the commit SHA), so no run on `main` is cancelled or replaced by a later one.

`.github/workflows/schema-compat.yml` is a reusable workflow (`workflow_call`, inputs `previous` and `current`, both full commit
SHAs, read-only permissions) with one job `check`. It proves that the release that is running still reads and writes on the
schema the next release migrates to, which is what makes a rollback after a migration safe (expand-only migrations, ADR 0020).
It skips with a notice when `git diff --quiet` finds no change under `src/Trainer.Infrastructure/Persistence/Migrations`.
Otherwise it checks out `current` by SHA (for a pull request the merge commit, `refs/pull/N/merge`; the `ref` is always `inputs.current`) and adds `previous` as a worktree, starts PostgreSQL from the compose pin (`.github/actions/postgres-from-compose`) and migrates it to `previous`, publishes `previous` and runs its own
`Release` suite once (the suite signs in through the fake provider and leaves users, answers, review states and mock sessions
behind), applies the migrations of `current`, and runs the `previous` suite again on the migrated, non-empty database. The
suite must run at least ten tests (`--minimum-expected-tests`), so a run that executes nothing fails. The
second run happens only when the previous commit carries `tests/Trainer.Web.Release/RERUNNABLE`, which says that its suite
tolerates a non-empty database; the one commit without the marker is skipped with the log line `previous Release suite is not
re-runnable; second run skipped`. The `previous` commit of a deployment is the commit running in production.

`.github/workflows/deploy-smoke.yml` checks the deployment bundle on pull requests that touch `deploy/`, the `Dockerfile` or
`src/`, when a deploy workflow calls it with the image it is about to ship (`workflow_call`, inputs `image` and `mode`) and on
demand (`workflow_dispatch`, input `mode`). It declares `contents: read` and `packages: read` statically; a caller must grant
the same on its calling job.

The smoke has two tiers (`DIT_SMOKE_MODE`, `quick` or `full`, default `full`; any other value exits 2). `quick` stops after the
rollback section; `full` runs everything with `DIT_SMOKE_KILL_ITERATIONS=4`. The `smoke` job picks the tier in its first step
after the checkout: a pull request gets `full` when `git diff --name-only <base>...<head>` names a path under `deploy/`, a
`deploy-*.yml` workflow, anything under `.github/actions/`, the `Dockerfile` or `.dockerignore` (or the list cannot be
read), `quick` otherwise; a call or a manual run uses its `mode` input, validated against the two values. `deploy-app.yml`
calls with `mode: quick`, because the scripts on `main` already passed `full` on their pull request. The step receives every
event value through `env`. The checkout uses `fetch-depth: 0` on a pull request, with `persist-credentials: false`. The job
limit is 45 minutes; the smoke step measured about 16 minutes in the full tier with 20 kills on a hosted runner.

| Job | Steps |
|---|---|
| `checks` | ShellCheck over `deploy/**/*.sh`, `render-env.test.sh`, `lib.test.sh`, `backup-lib.test.sh`, `rotate.test.sh`, `nginx-check.sh` (the nginx templates in an Ubuntu container), `provision-check.sh` (the `/srv` layout, its owners and modes, and the sshd drop-in of the bootstrap script in an Ubuntu container), the Pester tests of `deploy/bootstrap` (they run the bootstrap logic against mocked `ssh`, `scp` and `gh`) and `Trainer.Deploy.Tests`. |
| `smoke` | Gets the image (pull with a `docker login --password-stdin` from `github.token` when one is passed, else a build), runs `MigrationBundleTests` against it (`DIT_MIGRATE_IMAGE`), after installing `age` and `rclone` (the backup scripts run on the host), then `deploy/tests/smoke.sh`. |

`deploy/tests/smoke.sh` drives the real Compose project through `deploy-app.sh` in a temporary `APP_DIR`, using the same staging
upload protocol as the workflows and a throw-away local registry for the application image. Each named check prints `ok <name>`
or stops the run: first install without content, a failed and re-run first install, the internal port, content switches
without a restart, forwarded protocol, anonymous caching, Open Graph rendering, the image's user and relinked runtime, guard
files, a stale password, an untraversable flags path, rollback after a failed readiness check, after a terminated run and after
a `pg_hba.conf` that locks the application out (everything up to here is the `quick` tier, which then prints the skipped sections and stops), serialisation of concurrent runs, kills at every step boundary of activation and
publication (`DIT_TEST_KILL_AT`, honoured only with `DIT_TEST_HOOKS=1`) plus random kills, pruning, and the Umami guard, then backups and restores: the encrypted dump and its key ring, the sidecars, scratch restores, a wrong identity, a replace that migrates an older schema, the maintenance gate against a half-sent POST while the restored application runs (`DIT_TEST_PAUSE_FILE` holds `restore.sh` before it lifts the flag), a failure after the swap, a rebuild of an empty server from the bucket, an off-site outage, retention, the daily check, content delivery (`receive-content.sh`: a schema outside the supported range, a rejected import, a rejected first delivery, a lost import response retried and decided by the second call, an import that stays unresolved and is settled by the next delivery (also when the settled import is rejected), a release uploaded twice with the same and with different content, a release with a missing file but an intact manifest, unsafe archives (parent paths, absolute paths, links, FIFOs, a reserved name, an unpacked-size bomb), retention of five releases, kills after the guard, the switch and the import, a refused endpoint, a missing token, a `current` outside the releases and a lock timeout; `deploy/tests/import-proxy.py` drops the client connection once the importer holds its advisory lock, which now covers the read phase too; `DIT_TEST_IMPORT_URL` and `DIT_TEST_KILL_AT` are honoured only with `DIT_TEST_HOOKS=1`) and the second half of the Umami recovery, then password rotation (`rotate-db-password.sh`: a completed rotation of each role with the process list of the runner and of the postgres container sampled for the value, kills after the journal, after `ALTER ROLE` (the password in `.env` no longer logs in and a restarted application logs the authentication failure), after `.env` and after `last-good.env`, with the `finish` that converges each time, a failing `ALTER ROLE` (exit 1, the value absent from the PostgreSQL log), the refusal of a deployment, a content delivery and the daily check while the journal exists, `finish` and `commit` for the other role, and a deployment with the new secrets once committed). The
smoke project is named `dit-smoke`; the proxy listens on loopback port 8082 and needs `python3`; `DIT_SMOKE_KILL_ITERATIONS` shortens the random kill loop for local runs (the workflow sets 4).

`.github/workflows/link-check.yml` has one job `check-links`, which runs the CLI's
`check-links` every Monday at 06:00 UTC and on demand (`workflow_dispatch`), with
`contents: read` and `issues: write` and no secrets other than the job's own `GITHUB_TOKEN`, passed to `gh` through `env:`
(`GH_TOKEN`). Runs are serialised by the concurrency group `link-check` (never cancelled), so two runs cannot each open an issue. Two steps share the job: `Check source links` runs the CLI with `--report`, which writes the same Markdown as the job summary to a file, and has no token in its environment, because it fetches URLs taken from the content; it records the CLI exit code in the step output `code`. `Update the tracking issue` holds `GH_TOKEN` and acts only on `0` and `1`, and only when the report file is non-empty (the file is deleted before the run, so a build failure or a crash, which also exit non-zero, never reads as a verdict). With dead links (`1`) it updates the oldest open issue labelled `dead-links` (label created with `--force`) or opens "Dead source links", closes any further open `dead-links` issues as duplicates, then fails the job so the owner also gets the workflow notification. With none (`0`) it closes every open `dead-links` issue with the comment "All source links resolve as of <date>". The body is always passed with `--body-file`, never on a command line, because it holds URLs taken from the content; the table cells are escaped, and the report is cut to GitHub's 65536-character limit, with a closing line that counts the omitted rows and points to
the full table in the job summary. Any other exit code fails the job and leaves the issue as it is. A green run can still list
`unknown` links (blocked, throttled or timed out), for example when a publisher refuses
runner addresses, so read the job summary, not only the status. GitHub disables scheduled
workflows after 60 days without repository activity; re-enable it from the Actions tab.

`.github/workflows/deploy-app.yml` ships one release to the production server. It runs after `ci` succeeds on a push to `main`
(`workflow_run`) or by hand (`workflow_dispatch`, inputs `ref`, a commit on `main`, and `rollback`), under the workflow-level
concurrency group `production-app` with `permissions: {}` at the top. A running deployment is never cancelled, but GitHub keeps one pending run per group, so a newer queued run replaces the pending one. That loses nothing, because a run does not deploy the commit that triggered it: `resolve` deploys the newest commit on `main` (by ancestry) whose `ci` push run concluded successfully (`gh api` over `actions/workflows/ci.yml/runs?branch=main&event=push&status=success`, with `actions: read` on that job), and `prepare` never goes older than the deployed commit. A late `ci` of an older commit therefore deploys the newest green commit or nothing, and a manual run with an empty `ref` resolves the same way. Only a manual run with an explicit `ref` deploys a commit whose `ci` was not checked, and a rollback ships the `deploy/` bundle of the tip of `main` with the older image. A rollback by dispatch also sets a hold: after a successful run the deploy job writes `flags/deploy-hold` on the server over ssh (`GITHUB_TOKEN` has no permission that writes environment variables), `prepare` ends every `workflow_run` run with a notice while the file exists (and, also for an automatic run, when the resolved commit already runs, so out-of-order `ci` runs do not repeat a deployment), and only a manual run without `rollback` removes it (the `keep_hold` input exists for the run that `rotate-db-password` dispatches, is accepted only with `ref` equal to the deployed commit, and otherwise fails the run early; `ops-check` fails `deploy-hold` daily while the file exists; if writing the hold fails after a successful rollback the job says so and the owner creates the file by hand). Do not add required reviewers to the `production` environment: they would gate the scheduled `ops-check`, the content deliveries and the follow-up dispatch, and a waiting run is replaced by the next one, so the daily alert would stop silently. The branch policy (`main` only) is the boundary.
`workflow_run` carries the repository's secrets even when the triggering run was not privileged, so `resolve` re-checks the
conclusion, the event (`push`), the head branch, the head repository and that the commit is an ancestor of `origin/main`
before anything else runs. The server's `last-good.env` is the only record of what runs (`DIT_COMMIT`, `APP_IMAGE`); no job
asks the GitHub API which run deployed last. SSH goes through the composite action `.github/actions/ssh-setup`: the key and
the pinned host key are written to `$RUNNER_TEMP/ssh` from `env`, `StrictHostKeyChecking yes`, `BatchMode yes`, the `Port` of the `port` input (every caller passes the `DEPLOY_SSH_PORT` variable, default 22), host alias
`dit`, and every calling job ends with an `always()` step that removes `$RUNNER_TEMP/ssh` and `$RUNNER_TEMP/env`.

| Job | Steps |
|---|---|
| `resolve` | Picks the commit: after the provenance checks of a `workflow_run`, or for a manual run with an empty `ref`, the newest commit on `main` with a successful `ci` push run (`newest_green`: candidates that are not on `origin/main` are ignored and the one that descends from the others wins); for a manual run with `ref`, that SHA validated as 40 digits and an ancestor of `origin/main` (`rollback` requires `ref`). A run of `ci` that does not qualify ends here with a notice. |
| `prepare` | Production environment. Reads `DIT_COMMIT` and the presence of `flags/deploy-hold` from the server and sets `deploy` to false for a `workflow_run` while the hold exists, for a `workflow_run` push whose diff to the deployed commit is non-empty and touches only `content/`, and for a commit that is a strict ancestor of the deployed one unless `rollback` is set. A manual run of the deployed commit always deploys, which re-renders `.env` after a setting changed. |
| `build` | The only job with `packages: write`. Builds the image at the commit with `APP_VERSION=<sha>` and pushes `ghcr.io/<owner>/dev-interview-trainer:<sha>` unless that tag exists (`deploy/scripts/manifest-missing.sh` classifies the lookup: "manifest unknown", "not found" and "denied", which GHCR answers for a package never pushed, mean missing; any other error fails the job); outputs the digest reference every later step uses. |
| `compat` | Calls `schema-compat.yml` with the deployed commit as `previous` and the new one as `current`; for a rollback to an ancestor of the deployed commit the arguments are swapped (the older release on the schema already in production). Skipped on the first deployment. |
| `smoke` | Calls `deploy-smoke.yml` with the digest reference and `mode: quick` (`contents: read`, `packages: read`). |
| `deploy` | Runs when `prepare` said so, `smoke` succeeded and `compat` succeeded or was skipped (`!cancelled()`, because a skipped `needs` job would otherwise skip it). Repeats the stale-commit check, renders `.env` with `deploy/scripts/render-env.sh` (secrets and variables through `env`, `APP_IMAGE` and `APP_DIR` the same values the script receives), uploads the bundle and `.env.new` into `staging/<run>-<attempt>/`, logs the server in to the registry through stdin into a Docker config inside that directory, and starts `deploy-app.sh` under `setsid` with every descriptor redirected so an SSH drop cannot signal it. A wrapper records the exit status in `deploy.exit`; the job polls it, prints the end of `deploy-app.log` and fails unless the status is 0. After the wait step a rollback run writes `flags/deploy-hold` and a manual run without `rollback` or `keep_hold` removes it (both only after a successful deployment). Its last step checks out the tip of `main` as it is then (path `main-tip`) and dispatches `deploy-content.yml` for that tip, without `rollback`, when the `content` tree of the live release (`readlink content/current`) differs from the tip's: that delivers content held back because its schema was newer than the previous application, and content whose own delivery failed on the lock (exit 4) or was replaced. `actions: write` is granted to this job only. |

`.github/workflows/ops-check.yml` has one job `check`, which runs every day at 07:00 UTC and on demand under the concurrency
group `production-ops` with the production environment and `contents: read`: it uploads the bundle into a staging directory,
waits up to 20 minutes while a `deploy-app.sh`, `receive-content.sh` or `rotate-db-password.sh` process is alive (so `.import-pending` and `.rotation-pending` of a running operation do not alert; a marker without a live process still fails the check; the job timeout is 30 minutes, longer than the wait), runs
`remote/ops-check.sh` from there and removes the directory. The job failing is the owner's daily alert.

`.github/workflows/rotate-db-password.yml` has one job `rotate`, started by hand only (`workflow_dispatch`, inputs `role`: `trainer` or
`umami`, and `mode`: `rotate` or `finish`), under the workflow-level concurrency group `production-rotate` with `permissions: {}` at the
top and `contents: read` plus `actions: write` on the job (the production environment). It rolls forward only; there is no revert. The
first steps check that the run is on `main` and, before anything is changed, that the secret `SECRETS_ADMIN_TOKEN` (a fine-grained
token for this repository with the Environments permission "read and write", because `GITHUB_TOKEN` cannot write secrets) can set and
delete a probe secret `DIT_TOKEN_CHECK`. Mode `rotate` generates 48 hex characters with `openssl rand -hex 24` into a mode 600 file
under `$RUNNER_TEMP/env` and pipes it on stdin to `rotate-db-password.sh <role> begin`; mode `finish` (after an interrupted run) first
compares the role input with the journal's (`pending-role`), runs `finish` and reads the pending value back over the SSH channel.
In both modes the secret name is derived from the role alone (`trainer` is `DB_PASSWORD`, `umami` is `UMAMI_DB_PASSWORD`), `gh secret
set` reads the value from the file on stdin with three attempts, and only a confirmed update lets `commit` delete the journal; then
`deploy-app.yml` is dispatched with `GITHUB_TOKEN` for the commit that runs (`DIT_COMMIT` read from `last-good.env`, validated as a full SHA, with `keep_hold=true` so a rollback hold survives), not for the tip of `main`, whose `ci` may have failed and which an explicit `ref` does not check; a newer green commit refused meanwhile waits for the next `ci` completion or a manual run.
Only the commit that runs is redeployed, which is what retries the refused re-render of `.env`. A failed or ambiguous update keeps the journal and the deployment block until the owner runs mode `finish` for the same
role, which is safe to repeat. The value is never an argument on the runner or the server, never printed and never in `run:` text.

`.github/workflows/content-checks.yml` is a reusable workflow (`workflow_call`, input `ref`, empty by default, meaning the commit of the
caller) with one job `content` and `contents: read` at the top, no secrets and no environment. It checks out `ref`, builds, runs the
CLI's `validate`, pulls the pinned sandbox image (`SANDBOX_IMAGE`, which `CiWorkflowDriftTests` keeps equal to the runner's default),
runs the Docker-trait tests and `run-snippets`. `ci.yml` calls it for the commit under test, `deploy-content.yml` for the commit it is
about to publish.

`.github/workflows/deploy-content.yml` publishes the `content/` tree of one commit to the production server without restarting the
application. It runs on a push to `main` that touches `content/**` and by hand (`workflow_dispatch`, inputs `ref`, a commit on `main`,
and `rollback`), under the workflow-level concurrency group `production-content` with `permissions: {}` at the top. GitHub keeps one pending run per
group, whatever commit it names, so every run without `rollback` publishes the newest tree on `main`: a replaced run loses nothing.

| Job | Steps |
|---|---|
| `resolve` | Checks out `main` by name and picks one immutable commit. Without `rollback` that is the tip of `main` as fetched at the start of the run; a `ref` input is validated (40-digit SHA, ancestor of `origin/main`) and only serves as a lower bound. With `rollback` it is exactly `ref` (required). A manual run must be on `main`, because the server scripts come from the workflow's own commit. |
| `checks` | Calls `content-checks.yml` with that commit (`contents: read`). |
| `deploy` | Production environment. Checks out the workflow's own commit (never the content commit) and sets up SSH. Stale guard, on the runner: it reads the live release from `readlink content/current` and ends with a notice when the commit is a strict ancestor of it, unless `rollback` is set; only `rollback` overrides it, so the follow-up of `deploy-app` can never move content backwards. Uploads `deploy/` into `staging/<run>-<attempt>/bundle` and pipes `git archive --format=tar.gz <sha> content` into `receive-content.sh <staging> <sha>`. Exit 0 is success. Exit 5 (the content schema is newer than the running application; nothing was switched) ends with a warning, because `deploy-app` delivers it after the application that supports it, except for a `rollback` run, which fails; exit 7 (older than the application supports), 6, 4 (lock timeout) and every other status fail the job. A final `always()` step removes the staging directory (unless `receive-content.sh` still runs) and the SSH material. |

`deploy/remote/receive-content.sh` runs under the deployment lock and refuses on the guards of `dit_refuse_guards` (exit 3). It settles an
`.import-pending` left by an earlier delivery first (exit 6 when that import still gives no answer). It spools the archive and accepts only
regular files and directories under `content/` (no absolute path, no `..`, no link, no reserved `.manifest`, at most 256 MiB unpacked and 20000 entries; exit 2),
extracts it into the run's staging directory, writes a manifest and creates `content/releases/<sha>` through a temporary directory and a
rename. A release is immutable: an existing `<sha>` is reused only when its stored `.manifest`, the manifest recomputed from the files on
disk and the new delivery's manifest are all equal, otherwise the delivery fails. Leftover `*.tmp` and `*.del.*` directories of an
interrupted run are swept at the start of a run. It then reads `schema-version` and compares it with `contentSchemaMin` and
`contentSchemaMax` of `GET /internal/version` (exit 5 above the range, exit 7 below it, both before switching), refuses a `current` that
is not a release path (exit 2), writes `.import-pending`, switches `current` (`ln -sfn` and `mv -T`) and calls the import endpoint with the
token piped on stdin (no `-f`, no client timeout; the token is checked before anything is switched). The deployment lock timing out is
exit 4. After a definitive answer it removes the guard; on 200 it keeps the five newest releases and the one `current` points to, and a
release is removed by renaming it to `<sha>.del.<pid>` first. A release whose import was rejected (422, direct or settled by a later run) is removed the same way after the switch-back, unless it is the previous target; a release held back by exit 5 stays. `deploy-app.sh` does not refuse on `.import-pending`, but it settles it before it starts the application: `dit_revert_import_pending` switches `current` back to the previous target recorded on the file's second line (only while `current` still names the pending release) and removes the file, so the startup import reads a release an earlier delivery had accepted; `restore.sh --replace <dump> trainer` does the same. A malformed file is exit 2.

`.github/dependabot.yml` updates `github-actions` (the SHA pins), `nuget`, the `Dockerfile` and `deploy/compose` weekly, and
ignores PostgreSQL major versions (a dump and restore is needed) and every update of the Umami image (pinned exactly; upgrades
follow the manual Umami procedure).

`WorkflowHardeningTests` in `Trainer.Deploy.Tests` parse every workflow and composite action: remote actions pinned by full SHA,
top-level `permissions` and no `write-all`, `packages: write` only in the image build job, callers granting what a called
workflow declares, no `pull_request_target`, no secrets in pull request workflows, secrets only under `env` (or as an input of
`ssh-setup`), no inputs, event data, outputs or secrets interpolated into `run`, the deploy workflow's provenance, stale and
`compat` rules, uploads only into staging, the `always()` cleanup, a detached start with a recorded exit status, that no
production workflow names `DIT_TEST_HOOKS`, `DIT_TEST_KILL_AT` or `DIT_TEST_IMPORT_URL`, that `content-checks.yml` stays read-only and
secret-free and that `ci.yml` calls it, the triggers, resolution (the tip of `main` without `rollback`), stale guard, wiring, exit-code mapping and cleanup of `deploy-content.yml`, and the
content dispatch at the end of `deploy-app.yml` (`actions: write` on the deploy job only, no `rollback`, the comparison with a fresh checkout of the tip). The same file pins the newest-green resolution, the rollback hold (file on the server, set only after success, cleared only by a manual run without `rollback`, kept by `rotate-db-password`), the `ops-check` wait (all three scripts, a job timeout above the wait), that PostgreSQL comes from the compose pin only, and the deploy set: it is derived from the files, so a workflow that uses `ssh-setup` or an `environment` without being listed fails, a job has the `production` environment exactly when it uses `ssh-setup`, and the pull request secret check follows called reusable workflows. Each rule has a mutation case that must fail.

## Line endings and formatting

- `.gitattributes` sets `* text=auto eol=lf` and pins `*.yaml`, `*.yml`,
  `content/schema/*.json` and `content/schema-version` to LF. Binary assets are
  marked binary.
- `.editorconfig` sets UTF-8, LF, four-space indent for C# and two spaces for
  project, JSON and YAML files, a final newline, and the naming and `var` rules
  that the build enforces as warnings (which are errors).
- The content hash ignores CRLF and BOM, so a Windows checkout and a Linux
  checkout produce the same hash.

## Deployment topology

`deploy/` is the bundle that workflows upload to the server; nothing in it is edited by hand there.
The operating procedures (first-time setup, the configuration reference, restore, rollback, rotation and the
guard files) are in [deployment.md](deployment.md).

- `compose/compose.yaml` (project `dev-interview-trainer`) runs `postgres`, `app` and `umami` on one network with the fixed
  subnet `172.30.0.0/24`. Every published port is bound to `127.0.0.1`, PostgreSQL publishes none, and every image except the
  application's is pinned by digest. The `app` service is read-only, drops all capabilities, forbids new privileges and trusts
  only the network gateway (`172.30.0.1`) as a forwarded-headers proxy. Its health check is `/health/ready`.
- `compose/postgres/pg_hba.conf` allows `postgres` only over the local socket (peer) and the `trainer` and `umami` roles only
  from the Compose subnet, with `scram-sha-256` ([ADR 0019](adr/0019-database-roles.md)).
- `scripts/render-env.sh` turns environment variables (GitHub secrets and variables) into the server `.env` and exits 2
  naming the first missing or invalid variable, never its value.
- `remote/lib.sh` is sourced by every server script: `dit_compose`, `dit_psql` and `dit_pg_dump` (always as the operating-system
  user `postgres`), `dit_lock`, `dit_env_get`, `dit_env_set`, `dit_apply_hba`, `dit_refuse_guards` (the guard files, exit 3 and the `docs/deployment.md` section to follow) and `dit_check_traversable` (every directory down to the
  flags directory needs the `o+x` bit, because nginx runs as `www-data` and treats a permission error on the maintenance flag as "absent"). `remote/deploy-lib.sh` holds the pure helpers of `deploy-app.sh` (Umami image parsing and comparison, bundle hashing over content, modes and links, the list of good bundles and fail-closed bundle pruning) and is covered by `deploy/tests/deploy-lib.test.sh`; an entry script takes the lock itself, because only a script that inherited the lock holder's open descriptor may skip it. `remote/ensure-databases.sh` creates the two
  roles and databases, setting a password only when the role is created.
- `remote/rotate-db-password.sh <staging-dir> <trainer|umami> begin|finish|commit` (and `<staging-dir> pending-role`) rolls the
  password of one database role forward under the deploy lock. `begin` takes the new value on stdin, refuses over any guard file
  (exit 3), writes `.rotation-pending` (role and value, mode 600, temp file and rename) before anything else changes and then runs
  `finish`: `ALTER ROLE ... PASSWORD :'pw'` through psql's `\getenv` (the value travels in the exec environment; `log_min_error_statement`
  and `log_statement` are switched off in the same session first, because the server would log a failing statement with the value), the one line in
  `.env` and in `last-good.env` through `dit_env_set`, `compose up -d --wait --no-deps` for `app` or `umami` (`--no-deps` because the
  postgres service renders `DB_PASSWORD` too and is not recreated by a rotation), then a login as the role with the journaled value (from inside the postgres container, the same way `pg_hba.conf` admits the
  application roles) and `/health/ready` or Umami's `/api/heartbeat` as a liveness wait. A failing step exits 1 whatever the failing
  command returned (psql says 3, `dit_env_set` 2), because the workflow reads 2 and 3 as "nothing changed".
  Every step moves towards "new password everywhere", so `finish` converges from a crash at any point; `finish` and `commit` exit 2
  without a change when the given role differs from the journal's, and `commit` also refuses (exit 1) while `.env` does not yet hold
  the journaled value. The journal blocks `deploy-app.sh`, `receive-content.sh` and `restore.sh` (guard `rotation`; `backup.sh` proceeds over it, so the `trainer` backup is not stopped) and
  fails the `guards` check of `ops-check.sh`; the script switches xtrace off before any variable holds the value.
  `DIT_TEST_KILL_AT` (honoured only with `DIT_TEST_HOOKS=1`) stops it after the journal, `ALTER ROLE`, `.env` or `last-good.env`.
- `remote/deploy-app.sh <staging-dir> <image-ref> <commit-sha>` deploys one release under the deploy lock: it refuses to start
  while a guard file exists (`flags/maintenance`, `.rotation-pending`, `.umami-recovery`; exit 3) or when the new `.env`
  carries different database passwords or the flags path is not traversable by nginx (exit 2), takes a pre-deploy backup (required
  whenever a previous good state exists: a failed dump stops the run before anything is touched, a first install skips it), activates the bundle and `.env` (a `.deploy-in-progress` marker brackets the activation and records whether Umami's image changes, which is decided from the two bundles' compose files before anything is changed; an unparseable image refuses the deployment with exit 2),
  runs `ensure-databases.sh` and the migration bundle, starts the stack, waits for `/health/ready` and publishes
  `last-good.env` with one rename. `backup.sh predeploy` prints the path of the umami dump it wrote as the last line of stdout, which the recovery guard names; `deploy-app.sh` refuses to continue when that path is not an existing file. The script ignores `SIGPIPE`, logs to `$APP_DIR/logs/deploy-app.log` (mode 600, one 5 MiB generation kept) and must be started detached (`setsid`, output redirected). Any failure after activation, or a signal, restores `last-good.env`, its bundle and its
  `pg_hba.conf` and exits 1; a run killed without the chance to do that is repaired by the next run before it deploys. A failed
  first install stops `app` and `umami` and keeps PostgreSQL. When a failed deployment changed the Umami image, Umami stays
  stopped and `.umami-recovery` blocks further deployments until the pre-deploy dump is restored with `restore.sh --replace <dump> umami`, which proceeds over `.umami-recovery` and over a leftover
  `.deploy-in-progress` (a double failure) and then clears the guard with `dit_clear_umami_recovery`, which first resets the marker's Umami fields so the next run does not write the guard again; the next `deploy-app` then repairs the interrupted state first ([ADR 0020](adr/0020-deploy-serialisation.md)
  describes the serialisation around it).
- `remote/backup.sh daily|predeploy` writes `$APP_DIR/backups/<db>-<UTC yyyyMMddTHHmmssZ>-<kind>.dump.age` for `trainer` and
  `umami` (`pg_dump -Fc | age -R backup/recipients.txt`, mode 600, a `.partial` file renamed only after a successful
  pipeline) with a plain-text `.meta` sidecar (`trainer`: image, commit, last row of `ef_migrations_history`, active content
  hash, content release; `umami`: image only), copies every dump without a `.uploaded` marker to `offsite:$BACKUP_S3_BUCKET`
  with `rclone` configured by `RCLONE_CONFIG_OFFSITE_*` variables of a subshell (never on a command line), and prunes to 14
  daily and 5 pre-deploy files per database without ever deleting an un-uploaded file. It takes the deploy lock itself and
  skips it only when called by `deploy-app.sh`, which holds it; it refuses `flags/maintenance` and `.deploy-in-progress` (a backup of a mixed or
  half-restored state is worse than none) but proceeds over `.rotation-pending` and `.umami-recovery`, which leave the `trainer` database consistent, and skips the `umami` dump while `.umami-recovery` exists and exits 4 without `backup/recipients.txt`. `daily` fails when a dump or the
  upload fails, `predeploy` (which uploads only its own two files, never a backlog) only when a dump fails, so an off-site outage never stops a deployment. `last-success`,
  `last-offsite` and the markers are the records `ops-check.sh` reads. The pure rules (names, retention, sidecar text, record
  age, restart counts, the table list of a dump) are in `remote/backup-lib.sh` and covered by `deploy/tests/backup-lib.test.sh`.
  [ADR 0021](adr/0021-encrypted-offsite-backups.md) records the decision.
- `remote/restore.sh <dump> <db>` restores into a scratch database `<db>_restore_<ts>` and validates it (row counts for every
  table in the dump; for `trainer` the migration history and a non-empty key ring) without touching the live database;
  `--replace` additionally swaps it in under `flags/maintenance`, the migration bundle and a restart of the writers, and any
  failure after the flag was written keeps it, stops the writers and prints the rename commands for each state the three names in the flag can be
  in. The age identity is read from stdin before anything else runs (tracing is switched off first, CRLF is stripped, the variable is unset after the second decryption); after the start only the key ring is compared with the dump, because a newer migration may drop or rename any other table. `--drop-old` and `--drop-scratch` remove the databases it
  leaves behind. `restore.sh --replace <dump> umami` is the only restore that proceeds over `.umami-recovery` and an
  interrupted-deploy marker.
- `remote/ops-check.sh` prints `ok|fail <check> <detail>` for readiness, the recipient file, the age of the last backup and of the
  last off-site copy (26 h), disk (85 %), the TLS certificate (14 days), container restarts (more than 3), guard files (including a leftover `.deploy-in-progress`, which also stops the nightly backup) and `flags/deploy-hold` (the `deploy-hold` check).
  `systemd/dit-backup.timer` starts `dit-backup.service` daily at 03:30 UTC (`Persistent=true`, up to 10 minutes of random delay);
  the service runs the bundled `backup.sh daily` as `deploy` once the file exists, so bootstrap can enable the timer before the
  first deployment.
- On the server, `bundles/<sha>/` holds an unpacked bundle, `current-bundle` and `compose.yaml` are symlinks switched under the
  deploy lock, and `postgres/` (mounted as a directory, so a renamed `pg_hba.conf` is seen) receives the active bundle's rules.

## Decision records

Decisions that depart from the design document, or that the design leaves open, are
recorded one per file in `docs/adr/` (Status, Context, Decision, Consequences).
`DocsTests` fails when a record is missing from this list.

| Record | Decision |
|---|---|
| [0001](adr/0001-grading-in-domain.md) | Grading lives in `Trainer.Domain`, so the WebAssembly client can grade without referencing Application. |
| [0002](adr/0002-session-cookie-name.md) | The session cookie is `.dit.session`, set through cookie options. |
| [0003](adr/0003-logout-landing.md) | Logout lands on the sign-in page. |
| [0004](adr/0004-data-protection-keys-in-plan-2.md) | The data protection key ring is stored in PostgreSQL in Plan 2, unencrypted at rest. |
| [0005](adr/0005-answer-timestamps.md) | Answer timestamps: client-calibrated, rejected when older than a year or more than 5 minutes ahead. |
| [0006](adr/0006-application-references-contracts.md) | Application references `Trainer.Contracts`. |
| [0007](adr/0007-admin-url.md) | The admin page is `/{culture}/admin`. |
| [0008](adr/0008-per-owner-outbox-and-owner-header.md) | One outbox per owner and the `X-Outbox-Owner` header (the read behaviour awaits the owner's confirmation). |
| [0009](adr/0009-islands-full-page-navigation.md) | Navigation between public and island pages is a full page load. |
| [0010](adr/0010-profile-url.md) | The profile page is `/{culture}/{stack}/profile`. |
| [0011](adr/0011-catalog-search-get-form.md) | Catalog search is a `GET` form; search results are not cached. |
| [0012](adr/0012-import-map-nonce.md) | The import map carries a per-request CSP nonce. |
| [0013](adr/0013-invariant-globalization-in-the-client.md) | The WebAssembly client runs with invariant globalization. |
| [0014](adr/0014-lab-regression-budgets-and-phone-targets.md) | Lab timings are regression budgets; spec 8.2's phone targets are a Plan 3 acceptance criterion. |
| [0015](adr/0015-internal-endpoint-and-content-mount.md) | The internal endpoints listen on a separate port, and the content directory is mounted read-only from its parent so symlink switches are seen. |
| [0016](adr/0016-question-feedback.md) | Question feedback is two plain tables with a per-user report quota; it is not queued in the outbox. |
| [0017](adr/0017-open-graph-images.md) | Open Graph images are rendered lazily with SkiaSharp and cached on a volume under fingerprinted URLs. |
| [0018](adr/0018-cookieless-analytics.md) | Page views are counted by self-hosted Umami, served same-origin under `/stats`, without cookies. |
| [0019](adr/0019-database-roles.md) | Least-privilege database roles and `pg_hba`; the superuser is reachable only through `docker compose exec --user postgres` on the server. |
| [0020](adr/0020-deploy-serialisation.md) | Deploy workflows serialise per workflow and on the server lock, never deploy an older commit over a newer one automatically, and `deploy-app` starts after a green `ci` run on `main`. |
| [0021](adr/0021-encrypted-offsite-backups.md) | Database backups are encrypted with `age`, copied to a Backblaze B2 bucket with a key that cannot delete, and restored only through a guarded script that stops for the operator on any failure. |
