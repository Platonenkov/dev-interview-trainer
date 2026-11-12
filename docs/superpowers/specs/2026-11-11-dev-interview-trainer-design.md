# Dev Interview Trainer: Design

Status: draft for owner approval · Date: 2026-11-11

## 1. Intent

A web application for preparing for software developer job interviews. The first
stack is .NET; the data model and URLs are built so other stacks (Java, Go, ...)
can be added later without restructuring.

Why it exists:

- Interview questions are unpredictable and depend on the company, so preparation
  means broad coverage: language and runtime, frameworks, data, architecture,
  patterns, system design, and situational "what would you do if..." questions.
- Knowing an answer is not enough; you need to know *why*. Every question comes
  with an explanation, the correct answer when you are wrong, why the wrong
  options are wrong, and links to authoritative sources.
- The owner uses it first, then shares it with friends and colleagues. Later it
  goes public as an open-source project.
- It is also a portfolio piece built on Blazor. Code quality, architecture and
  documentation are part of the deliverable, not an afterthought.
- Content is available in Russian and English. Switching to English is also how
  the owner practises English for interviews.

## 2. Goals, non-goals, anti-goals

### Goals (v1)

1. Question bank for .NET with five levels: Junior, Middle, Senior, Lead,
   Architect, and a topic taxonomy (section 6).
2. Six question types (section 5.2) with explanations, hints and source links.
3. Three modes: Practice (filtered), Mock interview (timed), Review (spaced
   repetition). Review requires sign-in.
4. Sign-in with Telegram using the owner's library
   `Telegram.OpenIdConnect.AspNetCore`.
5. Full RU/EN localisation of both UI and content; language is part of the URL.
6. Public pages (home, catalog, topic pages, question pages) are indexable and
   fast: server-rendered static HTML, correct SEO metadata.
7. Dark and light themes from one set of design tokens, no flash on load.
   Responsive layout for phone, tablet, laptop and desktop (section 9.1).
8. Self-hosted on the owner's Linux VPS.
9. Clean architecture and English documentation from the first commit.

### Non-goals (v1)

- LLM grading of free-text answers. Only an extension point is designed in
  (section 5.4).
- Content for stacks other than .NET (the model supports it, the content does not
  exist yet).
- An in-app content editor. Content is edited as files through pull requests.
- Comments, discussions, user-submitted questions inside the app, leaderboards.
- Payments, mobile apps.
- UI languages other than RU and EN (the mechanism must allow adding them).

### Anti-goals (would make the project a failure even if it works)

- **Wrong content.** A wrong "correct" answer is worse than no question. Accuracy
  checks are part of the content pipeline (section 7.4).
- **Page freezes, "reconnecting" overlays, double data loads, visible
  re-renders.** These are the typical Blazor failure modes and are ruled out by
  the rendering design (section 8.2).
- **Mixed languages on one page.** A page is entirely RU or entirely EN.
- **Dry tables.** The visual design (section 9) is part of the product.
- **Content that is not indexable** because it only exists inside a client-side
  app.

## 3. Constraints

- .NET 10 (LTS), Blazor Web App, ASP.NET Core, EF Core, PostgreSQL.
- Telegram OIDC requires a client secret, so sign-in only works server-side
  (Blazor WebAssembly alone cannot do it). The host is a confidential ASP.NET Core
  server.
- Repository is private until there is a working version, then public.
  Licences: code MIT, content (`content/`) CC BY-SA 4.0.
- Code comments, docs, commit messages and PRs are in English. Commits follow
  Conventional Commits. No AI attribution lines anywhere in git or GitHub
  artifacts.
- Owner's C# conventions apply (explicit types instead of `var` where possible,
  async all the way with `CancellationToken`, comments explain why, xUnit v3 with
  built-in `Assert`, no FluentAssertions, `System.Text.Json`). They are written
  into `AGENTS.md`.
- Repository: `E:\GIT\dev-interview-trainer`, name `dev-interview-trainer`.
- The production domain is not chosen yet; it must be one configuration value.

## 4. Users and access

| Who | Can do |
|---|---|
| Anonymous visitor | Browse home, catalog, topic and question pages; Practice and Mock modes. Progress is kept only in the browser (`localStorage`). |
| Signed-in user | Everything above plus server-side progress, Review mode, statistics per topic and level, streak. |
| Editor (role) | Reserved for future in-app tooling; no extra UI in v1. |
| Admin (role) | Health page, content import status, user block flag. |

Sign-in flow: `Telegram.OpenIdConnect.AspNetCore` with cookie auth. The
`ITelegramSignInGate` implementation allows everyone who is not blocked,
creates the user record on first sign-in (keyed on Telegram user id) and adds
role claims from the database. On first sign-in, if the browser holds anonymous
progress, the app offers to import it into the account (builder's call on UI;
import is idempotent, see section 8.8).

Session lifetime: auth cookie lasts 30 days with sliding expiration (the
library default of 12 hours would force a daily sign-in). Blocking a user or
changing roles takes effect within 5 minutes: the cookie carries a security
stamp, and `CookieAuthenticationEvents.OnValidatePrincipal` re-checks it
against the database at most every 5 minutes, rejecting blocked users and
refreshing role claims.

## 5. Learning model

### 5.1 Levels and tags

- Level: exactly one of `junior`, `middle`, `senior`, `lead`, `architect`
  (ordered). Shared by all stacks.
- Topic: exactly one primary topic from the stack's taxonomy.
- Tags: free-form, lowercase kebab-case, used for search and "related questions".

### 5.2 Question types

| Type key | What the user does | How it is checked |
|---|---|---|
| `open` | Reads the question, thinks or types an answer, reveals the reference answer | Self-assessment: knew / partially / did not know |
| `single` | Picks one option | Automatic |
| `multi` | Picks all correct options | Automatic; exact set = `knew`, at least one correct pick and at most one mistake = `partial`, otherwise `didnt-know`; the explanation shows each option's verdict |
| `output` | Reads a code snippet and picks or types the result | Automatic. The result is one of: console output, an exception (type), "does not compile", or "nondeterministic". Typed output is compared after trimming and normalising line endings against one or more accepted values |
| `find-bug` | Clicks the line(s) in a snippet that contain the defect | Automatic: the selection must equal one of the accepted line sets; explanation shows the fix |
| `situation` | Reads a scenario ("prod is down after deploy..."), writes or thinks a plan | Self-assessment against a reference plan |

Every type has: optional hint (using it is recorded and caps the result at
"partially"), explanation, and at least one source link. Choice-based types have
a per-option explanation ("why this is wrong").

### 5.3 Modes

- **Practice.** Filter by level(s), topic(s), type(s), status (new / due / all);
  questions come in random order; feedback after each answer.
- **Mock interview.** N questions (default 20) matched to a chosen level
  (that level plus a share of the level below), mixed topics, optional timer,
  no feedback until the end. When the timer ends or the user finishes, an
  **assessment phase** follows: for each `open`/`situation` question the user
  sees the reference answer and self-assesses; automatic questions are graded
  then. Then a summary by topic with links to each question. If the pool for
  the chosen level and filters is smaller than about 3×N, the UI warns that
  sessions will repeat questions and suggests a smaller N.
- **Review.** Spaced repetition over questions the user has answered. Requires
  sign-in. To train recall rather than recognition, choice-based questions in
  Review first show the prompt without options and ask "can you answer?";
  options (in shuffled order) appear after that. Option order is shuffled in
  every mode.

### 5.4 Grading and spaced repetition

Every answer produces a grade: `knew`, `partial`, `didnt-know`.

- Automatic types: correct = `knew`, correct with hint = `partial`, wrong =
  `didnt-know`.
- Self-assessed types: the user's choice; hint caps at `partial`.

Scheduling uses an SM-2-style algorithm in the domain layer (pure function,
fully unit-tested): `didnt-know` resets the interval to 1 day, `partial` keeps
it short, `knew` grows it (roughly 1, 3, 7, 16, 35 days...). Exact constants are
the builder's call; the algorithm must sit behind an interface so it can be
replaced (for example by FSRS) without a data migration beyond the scheduling
fields.

Answers from every mode feed the scheduler, with one rule: an answer to a
question that is not yet due can only shorten its interval (`didnt-know` or
`partial` reschedule it), never lengthen it. A first answer to a new question
creates its review state.

LLM grading extension point: grading of `open` and `situation` answers goes
through an `IAnswerGrader` abstraction. v1 ships only the self-assessment
implementation. The question schema has a `grading` field (`self` | `auto`,
reserved `llm`).

## 6. .NET topic taxonomy (v1)

Owner delegated the taxonomy. Keys are stable (used in URLs and progress);
titles are localised.

| Key | Title (EN) | Covers |
|---|---|---|
| `csharp` | C# language | Syntax, generics, delegates, lambdas, closures, records, pattern matching, nullable, language versions |
| `types-memory` | Types and memory | Value vs reference, boxing, struct design, `Span<T>`, `ref`, stack vs heap |
| `oop-solid` | OOP and SOLID | Encapsulation, inheritance vs composition, interfaces, SOLID, DRY/KISS/YAGNI |
| `collections-linq` | Collections and LINQ | Collection choice and complexity, hashing, `IEnumerable` vs `IQueryable`, deferred execution |
| `async` | async / await | Tasks, state machine, `SynchronizationContext`, `ConfigureAwait`, `ValueTask`, cancellation, deadlocks |
| `concurrency` | Multithreading | Threads, thread pool, locks, `Interlocked`, concurrent collections, channels, race conditions |
| `clr-gc` | CLR and GC | JIT, assemblies, GC generations, LOH, finalisation, `IDisposable`, memory leaks |
| `exceptions` | Exceptions and diagnostics | Exception design, `finally`, rethrow, logging, dumps, debugging production |
| `aspnet-core` | ASP.NET Core | Middleware, DI lifetimes, configuration, hosting, minimal APIs vs controllers, filters |
| `web-api` | Web APIs and HTTP | REST, status codes, versioning, idempotency, gRPC, SignalR/WebSockets, `HttpClient` |
| `security` | Security | AuthN/AuthZ, OAuth/OIDC, JWT, OWASP Top 10, secrets, CORS, CSRF |
| `ef-core` | EF Core | Tracking, migrations, N+1, loading strategies, transactions, concurrency tokens, raw SQL safety |
| `databases` | Databases and SQL | Indexes, isolation levels, locks, normalisation, query plans, NoSQL trade-offs |
| `testing` | Testing | Unit/integration/e2e, mocks vs fakes, test design, TDD, testing async code |
| `patterns` | Design patterns | GoF patterns in .NET, when not to use them, anti-patterns |
| `architecture` | Application architecture | Layered, clean/hexagonal, DDD, CQRS, modular monolith vs microservices |
| `distributed` | Distributed systems and messaging | Queues, Kafka/RabbitMQ, outbox, sagas, retries, idempotency, consistency |
| `system-design` | System design | Designing services end to end, scaling, caching, trade-offs |
| `performance` | Performance | Profiling, allocations, benchmarking, caching, pooling |
| `devops` | DevOps and observability | Docker, CI/CD, logging, metrics, tracing, deployment strategies |
| `ui-dotnet` | .NET UI frameworks | Blazor, MAUI, WPF: render model, state, binding |
| `teamwork` | Teamwork and leadership | Code review, estimates, incidents, mentoring, conflict, process |
| `behavioral` | Situational and behavioral | "What would you do if..." questions without a single technical answer |

Topics are not tied to levels; level distribution emerges from the content
(for example `teamwork` is mostly Lead, `system-design` mostly Senior+).

## 7. Content

### 7.1 Storage

Content lives in the repository as files, one question per file, reviewed
through pull requests:

```
content/
  LICENSE                       # CC BY-SA 4.0
  stacks.yaml                   # stack list with localised titles
  dotnet/
    topics.yaml                 # taxonomy from section 6, localised titles/descriptions
    async/
      result-deadlock-ui-thread.yaml
      ...
```

YAML is chosen over JSON for readable multi-line Markdown and code. Builder may
choose Markdown-with-front-matter instead if it validates equally well; the
schema below is what matters.

### 7.2 Question schema

```yaml
id: 01JD4X7Q8N9V2K3M4P5R6S7T8W   # ULID, immutable, never reused; progress references it
slug: result-deadlock-ui-thread   # URL part, unique per stack, same for all languages
stack: dotnet
topic: async
level: middle
type: single                      # open | single | multi | output | find-bug | situation
grading: auto                     # self | auto  (reserved: llm)
tags: [synchronization-context, deadlock, winforms]
code:                             # optional, language-neutral (code is not translated)
  language: csharp
  harness: statements             # statements | program | none (see 7.4)
  source: |
    private void Button_Click(object sender, EventArgs e)
    {
        string text = LoadAsync().Result;
    }
bugLineSets: [[3]]                # find-bug only: accepted selections (each a set of lines)
fixedSource: |                    # find-bug only, optional: corrected code shown in the
  ...                             # explanation; CI runs it and checks fixedExpect
fixedExpect: { kind: output, value: "..." }
expect:                           # output only: the machine-checked truth
  kind: output                    # output | exception | compile-error | nondeterministic
  value: "333"                    # stdout for output; exception type name for exception
acceptedOutputs: ["333"]          # output only, typed answers: accepted values (one or more)
options:                          # single / multi / output-with-choices
  - id: a
    correct: false
    expect: { kind: output, value: "012" }   # output-with-choices: each option's
  - id: b                                    # machine-readable result, so CI can
    correct: true                            # verify that exactly the correct option
    expect: { kind: output, value: "333" }   # matches the real execution
links:
  - url: https://learn.microsoft.com/...
    lang: en                      # language of the linked page
    kind: docs                    # docs | spec | article | video | book
locales:
  ru:
    title: Почему вызов .Result в UI-потоке приводит к deadlock?
    prompt: |                     # Markdown
      ...
    hint: ...
    options:
      a: Вернёт результат
      b: Deadlock
    optionExplanations:
      a: ...
    answer: |                     # reference answer (open/situation) or short correct answer
      ...
    explanation: |                # the "why", Markdown
      ...
    linkTitles:                   # localised titles for links, by index
      - Async/await best practices
  en:
    title: ...
    ...
meta:
  authors: [platonenkov]
  verified: 2026-11-11            # date the content was last checked against sources
  dotnetVersion: "10"             # behaviour version the answer is true for, if relevant
```

Rules:

- A question is published in a language only if that `locales` block exists and
  passes validation. No fallback to another language on a page.
- `id` and `slug` never change after publication. Renaming a slug requires a
  redirect entry (`content/dotnet/redirects.yaml`).
- Code is not translated; comments inside code snippets are English.
- Links prefer official sources (Microsoft Learn, C# spec, runtime repo, RFCs),
  then recognised authors. Each question has at least one link. A link may point
  to an English page from a Russian question; the UI marks the link language.

### 7.3 Import

Content is delivered **separately from the application image** (owner's
decision): fixing a typo in a question must not require rebuilding or
restarting the app. Delivery pipeline is in section 8.7.

A `ContentImporter` reads a content release directory, validates every file and
upserts questions into PostgreSQL in one transaction. It runs:

- on application start, from the currently active content release (guarded by
  a content hash, so it is a no-op when nothing changed);
- on demand, when the content workflow triggers the internal import endpoint
  after uploading a new release (section 8.7); the running app imports, then
  evicts its output cache and regenerates the sitemap;
- as a CLI command in CI for validation (no database writes).

Removed files mark questions as retired, not deleted, so user progress
survives. A failed validation or import leaves the previous content active.

**Schema compatibility.** `content/schema-version` holds an integer. The app
declares the range of content schema versions it supports and refuses to
import anything outside it. A change that needs a new schema version is shipped
in two steps: first the app release that supports both old and new versions,
then content using the new version. The content workflow checks the deployed
app's supported range before uploading.

### 7.4 Validation and accuracy

- JSON Schema for the YAML plus C# semantic checks: unique ids and slugs, topic
  exists, correct option count matches type, `bugLines` within the snippet,
  every published locale complete, at least one link, URLs absolute https.
- `output` questions: CI compiles and runs each snippet and asserts the real
  result equals `expect` (and, for choice-based output questions, that exactly
  the options marked correct match it). `find-bug` questions with
  `fixedSource` are run and checked against `fixedExpect`.
  - Harness convention: `statements` wraps the snippet in a top-level program
    with default `using`s; `program` compiles the snippet as a full program;
    `none` skips execution (illustrative code only, validator requires a
    reason in `meta`).
  - Pinned settings: .NET 10 SDK, C# language version from the SDK, invariant
    culture, UTC time zone, 10 s timeout, 256 MB memory, 64 KB output cap.
    `nondeterministic` snippets are run several times and must produce at least
    two different outputs.
  - **Isolation.** Snippets come from pull requests and are untrusted code.
    They run only in a disposable container started per run: no network, no
    secrets in the environment, read-only root, no repository mount except the
    snippet files, CPU/memory/time limits. Execution never happens in the web
    process. The CI job that runs snippets has no access to repository secrets
    and runs only after a maintainer approves workflows for outside
    contributors.
- Link checker runs on a schedule (not on every PR) and reports dead links.
- `meta.verified` and `meta.dotnetVersion` make stale content visible.

### 7.5 Rendering content safely

Content arrives through public pull requests, so Markdown is untrusted input.

- Markdig with raw HTML disabled; links and images allowed only with `https:`
  (and relative links inside the site). The validator rejects other schemes.
- Code snippets are HTML-encoded and highlighted server-side.
- JSON-LD is produced by a JSON serializer, never by string concatenation.
- Markdown is rendered to HTML once, during import, and stored per locale.
  Islands receive this pre-rendered HTML in their DTOs, so neither Markdig nor
  the highlighter ships to WebAssembly.

### 7.6 Initial content

Target for the first public version: about 400 questions with both RU and EN,
spread over all topics and levels (rough split: Junior 25%, Middle 35%,
Senior 25%, Lead 8%, Architect 7%), all six types represented. Content is
produced in batches by topic, each batch reviewed by the owner before merge.
A usable private build can start with fewer (my call: around 150, covering every
topic at least at Junior/Middle). Content production is a separate workstream
from the application code and has its own plan.

## 8. Architecture

### 8.1 Solution layout

```
src/
  Trainer.Domain/          # entities, value objects, grading, scheduling algorithm; no IO
  Trainer.Application/     # use cases/services, interfaces (repositories, IAnswerGrader, clock)
  Trainer.Infrastructure/  # EF Core + Npgsql, repositories, migrations, content importer
  Trainer.Content/         # content schema, YAML parsing, validation (shared by app and CLI)
  Trainer.Web/             # Blazor Web App host: static SSR pages, auth, API endpoints, SEO
  Trainer.Web.Client/      # WebAssembly interactive islands (quiz, review, mock)
  Trainer.Contracts/       # DTOs shared by Web and Web.Client
tools/
  Trainer.ContentCli/      # validate / import / run output snippets
tests/
  Trainer.Domain.Tests/
  Trainer.Application.Tests/
  Trainer.Content.Tests/   # includes "every content file is valid" and snippet execution
  Trainer.Web.Tests/       # integration tests with WebApplicationFactory + Testcontainers PostgreSQL
content/
docs/
```

Rules: Domain depends on nothing; Application depends on Domain; Infrastructure
and Web depend inward. No business logic in components or endpoints. Services
via DI, no static service classes. `Directory.Packages.props` for central
package management, `Directory.Build.props` with nullable, warnings as errors,
analyzers and NuGet audit.

### 8.2 Rendering (no freezes, no double updates)

- **Public, indexable pages are pure static SSR with no interactive
  components**: home, stack home, catalog, topic pages, question pages, about.
  No circuit, no WASM runtime, no antiforgery token or cookie in the response,
  so they are cacheable (section 8.4). They do **not** use `[StreamRendering]`:
  streaming commits a 200 status before data is known, which would turn missing
  slugs into soft-404s and make 301 redirects impossible.
- The answer reveal on a question page is a native `<details>` element (plus,
  at most, a few lines of plain JS for keyboard shortcuts), not a Blazor island.
  A "Practise this question" link opens Practice with that question first.
- **Interactive islands use InteractiveWebAssembly**, never InteractiveServer,
  and live only on non-indexed pages: Practice, Mock, Review, profile.
  Reason: InteractiveServer keeps a circuit per visitor and shows
  "reconnecting" on poor networks.
- **No double data loads.** Interactive pages disable prerendering, render a
  fixed-size skeleton on the server and load data once on the client. Where an
  island does prerender, it uses `[PersistentState]` with `AllowUpdates = true`
  so enhanced navigation to the page with an already running runtime refreshes
  the state instead of refetching.
- WASM payload kept small: trimming on, source-generated `System.Text.Json`
  contexts for all DTOs (trimming-safe), no heavy UI component libraries, no
  Markdown or highlighting libraries on the client (content arrives as
  pre-rendered HTML, section 7.5). Performance targets, measured and recorded
  by the builder: on a mid-range phone over 4G, Practice is interactive within
  2 s with a warm cache and within 5 s cold. Public pages link-preload the
  runtime only on hover/focus of the Practice/Mock entry points.

### 8.3 Localisation and URLs

- Culture is the first URL segment: `/ru/...`, `/en/...`. `/` redirects (302)
  to a culture root using, in order: the language cookie (explicit choice),
  then `Accept-Language`, then `en`. Never cookie-only for content, so both
  languages are indexable.
- Server: route culture is authoritative. `UseRouting` runs before
  `UseRequestLocalization` so a route-data culture provider sees the
  `{culture}` value.
- WebAssembly: the client starts with `Blazor.start({ webAssembly: {
  applicationCulture } })` set from `<html lang>`, ships satellite resource
  assemblies for every supported culture, and never changes culture at
  runtime. The language switcher is a plain link with
  `data-enhance-nav="false"`, forcing a full reload into the other culture, so
  islands can never render in a stale language.
- UI strings in `.resx` via `IStringLocalizer`; adding a language = adding
  resources + content locale blocks + one entry in the supported-cultures
  config.
- URL scheme:
  - `/{culture}` home
  - `/{culture}/{stack}` stack home
  - `/{culture}/{stack}/questions?level=&topic=&type=` catalog (filters in query)
  - `/{culture}/{stack}/topics/{topic}` topic page
  - `/{culture}/{stack}/questions/{slug}` question page
  - `/{culture}/{stack}/practice`, `/mock`, `/review` interactive modes (noindex)
  - `/{culture}/signin` sign-in page; library endpoints `/signin-telegram/*` stay
    outside the culture prefix (section 11, item 2)

### 8.4 SEO and caching

- Per page: `<title>`, meta description, canonical, `hreflang` alternates for
  every language the page exists in plus `x-default`, Open Graph tags.
- Question page HTML contains the question, the correct answer and the
  explanation (inside `<details>`).
- JSON-LD: `BreadcrumbList` on all public pages. No `Quiz`/practice-problem
  markup: Google's support for that type is limited to specific subjects and
  not worth the effort for v1.
- `sitemap.xml` generated from the database per language, `robots.txt`;
  interactive pages and catalog combinations beyond a single level and/or a
  single topic are `noindex`.
- Unknown slugs return a real 404; redirect entries return 301.
- **Output cache** (in-process, single app instance):
  - Applies only to public static pages and only to requests without the auth
    cookie; signed-in requests bypass it and render personal fragments
    (progress, statuses).
  - Varies by path plus an explicit, normalised query allow-list
    (`level`, `topic`, `type`, `page`); other query parameters are ignored for
    the key. Size limit configured.
  - Evicted by tag after content import.
  - Integration tests assert that anonymous public responses carry no
    `Set-Cookie` and that a second request is a cache hit.
- Response compression, immutable static assets with fingerprinting.

### 8.5 Themes

- Dark and light themes from one set of CSS custom properties (tokens). Default
  follows `prefers-color-scheme`; an explicit choice is stored in
  `localStorage` only (the server never needs it).
- No flash: a tiny inline script in `<head>` sets `data-theme` before first
  paint. Theme is not part of server-rendered HTML, so cached pages stay
  theme-independent.
- All text/background token pairs meet WCAG 2.2 AA contrast (4.5:1 for body
  text, 3:1 for large text and UI boundaries). Example fixed during design:
  primary button in the dark theme uses dark text on green.

### 8.6 Data model (relational, PostgreSQL)

- `users` (id, telegram_user_id unique, display_name, username, created_at,
  is_blocked, security_stamp, preferred_culture)
- `user_roles`
- `stacks`, `topics` (key, stack) and `topic_locales` (topic, culture, title,
  description)
- `questions` (id ULID, stack, topic, level, type, slug, language-neutral
  payload JSONB: code, options, expectations; content_hash, retired_at)
- `question_locales` (question_id, culture, title, prompt_html, answer_html,
  explanation_html, hint_html, options JSONB) — a question is published in a
  culture iff a row exists; sitemap, hreflang and catalog query this table
- `answers` (id, client_answer_id ULID unique, user_id, question_id, grade,
  used_hint, mode, mock_session_id null, answered_at, received_at) append-only
- `review_states` (user_id, question_id, due_at, interval, ease, repetitions,
  version for optimistic concurrency)
- `mock_sessions` (id client-generated ULID, user_id, level, started_at,
  finished_at, summary JSONB)
- `content_imports` (hash, imported_at, question_count)

Builder may refine columns; ULID ids, the locale table and append-only answer
history with a client-generated unique id are decided.

### 8.7 Hosting and operations

- Docker Compose on the VPS: `app` (ASP.NET Core) + `postgres`, nginx on the
  host as reverse proxy with TLS (Let's Encrypt). No WebSocket upgrade needed
  (no circuits). Forwarded headers configured as the Telegram library
  documents.
- **Data Protection keys** persist in PostgreSQL (or a protected volume) with a
  fixed application name, included in backups, so deployments do not log users
  out or invalidate antiforgery tokens.
- **Migrations** are applied by an EF Core migration bundle run as a separate
  deploy step before the new container starts, never by `Migrate()` at startup.
- Content import runs at startup; the readiness endpoint reports not-ready
  until import finishes, and nginx/compose health checks use it.
- Configuration: `App:PublicBaseUrl` (domain), `App:AdminTelegramIds`,
  `TelegramOidc:ClientId` / `ClientSecret`, connection string. **GitHub is the
  single source of truth for configuration** (owner's decision): secrets in
  GitHub Secrets, non-secret settings in GitHub Variables, both in the
  `production` environment. `deploy-app.yml` renders the server `.env` from them
  on every deploy. Nothing is edited by hand on the server, and a lost server
  can be rebuilt from bootstrap + deploy + the latest backup.
- Structured logging (no auth headers or cookies in logs), daily PostgreSQL
  backup script.

#### 8.7.1 Server layout

```
/srv/dev-interview-trainer/
  compose.yaml
  .env                       # mode 600, rendered by deploy-app.yml from GitHub secrets/variables
  content/
    releases/<git-sha>/      # one directory per content release
    current -> releases/<sha>  # symlink, mounted read-only into the app container
  backups/
```

The app container mounts `content/current` read-only. The last 5 releases are
kept; rollback = re-run the content workflow on an older commit (or repoint the
symlink and trigger import).

#### 8.7.2 Workflows (GitHub Actions)

Everything is published through Actions. There are four workflows:

1. **`ci.yml`** (every PR and push): build, unit and integration tests, format
   check, content validation, isolated snippet execution, Playwright layout
   checks.
2. **`deploy-app.yml`** (push to `main` that changes anything outside
   `content/`, or manual): build the image, push it to GitHub Container
   Registry tagged with the commit sha, then over SSH as the deploy user:
   render `.env` and `compose.yaml` from the repository templates and GitHub
   secrets/variables (written atomically, mode 600), pull the image, run the
   EF Core migration bundle, `docker compose up -d`, wait for readiness, roll
   back to the previous image tag and previous `.env` if readiness fails.
   Changing only a secret or variable = re-run this workflow manually.
4. **`rotate-db-password.yml`** (manual only): generates a new password, runs
   `ALTER ROLE` on the server, stores it with `gh secret set` (using a
   fine-grained token limited to this repository's secrets), then triggers
   `deploy-app`. Changing `DB_PASSWORD` in GitHub by hand does not change the
   database password, so rotation always goes through this workflow.
3. **`deploy-content.yml`** (push to `main` that changes `content/`, or manual):
   validate content and run snippets (same jobs as CI), check the deployed
   app's supported schema range, pack `content/` as an archive, upload it over
   SSH into `releases/<sha>`, switch the `current` symlink, then call the
   internal import endpoint. If the import fails, the symlink is switched back
   and the workflow fails. No image is built.

All deploy workflows use the GitHub environment `production` with a deployment
branch rule (only `main`) and a shared concurrency group, so two deploys never
overlap. Hardening, required before the repository goes public:

- third-party actions pinned by full commit SHA;
- `permissions:` set per job to the minimum (`contents: read`,
  `packages: write` only where the image is pushed);
- workflows triggered by pull requests never receive secrets (GitHub default
  for forks) and never use `pull_request_target`;
- secrets are passed to remote commands through stdin or env, never on the
  command line, and never echoed (masked output is not relied upon).

**Internal import endpoint.** `POST /internal/content/import` is served on a
separate port bound to `127.0.0.1` on the host and is not proxied by nginx, so
it is unreachable from the internet. It also requires a bearer token
(`CONTENT_IMPORT_TOKEN`), compared with `CryptographicOperations.FixedTimeEquals`.
The workflow calls it over the SSH session with `curl`. `GET /internal/version`
on the same port returns the app version and supported content schema range.

#### 8.7.3 Bootstrap (first-time provisioning from the admin's PC)

A bootstrap script in `deploy/bootstrap/` is run once by the admin from their
own machine against a fresh VPS (Ubuntu LTS), over SSH with the admin's key. It
is idempotent: re-running it repairs or updates the setup without destroying
data. Written in PowerShell 7 (`pwsh`, runs on Windows, Linux and macOS);
the remote part is a bash script it uploads and runs.

It asks for: SSH host and admin user, domain, admin Telegram user id(s),
Telegram OIDC client id and secret, e-mail for Let's Encrypt. These values are
not stored on the server by bootstrap; they go to GitHub (step 6).

It does:

1. Installs Docker Engine with the compose plugin, nginx and certbot;
   enables the firewall (22, 80, 443 only) and unattended security upgrades.
2. Creates a `deploy` user with no sudo except the narrow commands it needs
   (or docker group membership), and generates a dedicated SSH key pair for
   GitHub Actions; the public key goes to `deploy`'s `authorized_keys`, the
   private key is shown once for GitHub and not stored on the server.
3. Creates `/srv/dev-interview-trainer/` with the directory layout, owned by
   `deploy`. Generates the PostgreSQL password and `CONTENT_IMPORT_TOKEN`
   locally on the admin's PC (they go to GitHub, step 6). The first admin user
   is seeded through the `ADMIN_TELEGRAM_IDS` variable: the gate grants `Admin`
   to these ids on their first sign-in.
4. Configures nginx for the domain, obtains the TLS certificate, sets up
   renewal.
5. Installs the daily backup job (PostgreSQL dump + Data Protection keys).
6. Creates the `production` environment (branch rule: `main` only) and fills
   it, after showing the list and asking for confirmation, with
   `gh secret set` / `gh variable set` (values passed through stdin, never as
   arguments). Without an authenticated `gh` it prints the checklist instead,
   showing each secret once:
   - **Secrets**: `TELEGRAM_OIDC_CLIENT_SECRET`, `DB_PASSWORD`,
     `CONTENT_IMPORT_TOKEN`, `DEPLOY_SSH_KEY`, `DEPLOY_KNOWN_HOSTS`.
   - **Variables**: `TELEGRAM_OIDC_CLIENT_ID`, `ADMIN_TELEGRAM_IDS`,
     `PUBLIC_BASE_URL`, `DEPLOY_HOST`, `DEPLOY_USER`, `APP_DIR`.
   Nothing is written to disk on the admin's PC except a temporary file that
   is deleted at the end.
7. Prints the BotFather redirect URI to register
   (`https://<domain>/signin-telegram`).

After bootstrap, the first `deploy-app` run starts the app and the first
`deploy-content` run loads the questions. `docs/deployment.md` documents the
whole procedure, including how to change a setting (edit in GitHub, re-run
`deploy-app`), how to rotate the deploy key, the import token and the DB
password, and how to rebuild a lost server.

### 8.8 API contract, reliability and security

- **Islands talk to minimal API endpoints under `/api`** on the same origin,
  authenticated by the auth cookie (`SameSite=Lax`, `Secure`, `HttpOnly`).
- **Antiforgery for writes:** islands obtain a token from
  `GET /api/antiforgery` (no-store, never cached), send it in the
  `X-CSRF-TOKEN` header, and every write endpoint validates it explicitly
  (`IAntiforgery.ValidateRequestAsync` or endpoint antiforgery metadata). The
  client refetches the token after sign-in/sign-out or on a 400 antiforgery
  failure.
- **Answers are events with client ids.** Every answer gets a ULID
  `clientAnswerId` on the client and is written first to an outbox in
  `localStorage` (for anonymous and signed-in users alike). Signed-in clients
  flush the outbox to `POST /api/answers/batch`, which is idempotent: the unique
  index on `client_answer_id` deduplicates retries, and review-state updates use
  optimistic concurrency with retry. Entries leave the outbox only after the
  server acknowledges them. Importing anonymous progress after first sign-in is
  the same flush. Outbox format: `{ version, answers: [{ clientAnswerId,
  questionId, grade, usedHint, mode, mockSessionId?, answeredAt }] }`.
- **Offline scope:** once a Practice/Mock/Review session has loaded its
  questions (the whole session is fetched up front), answering and feedback
  work without network; answers sync when the network returns. Starting a new
  session needs the network.
- **Mock completion:** individual answers flow through the outbox like any
  other answer (tagged with the mock session id); the summary is sent with
  `PUT /api/mock-sessions/{id}` (idempotent by id).
- **Server-side checks on reported answers:** question exists and is not
  retired, `answeredAt` not in the future and not older than 1 year, batch size
  ≤ 500, per-user rate limit. The grade itself is trusted (section 11, item 5).
- **Auth state in islands:** `AddAuthenticationStateSerialization` (available
  since .NET 9) on the server with a custom claim set: Telegram user id,
  display name (falling back to username, then "Telegram user"), roles.
  Islands never call Telegram directly.

## 9. Visual design

Direction chosen: **"IDE"**, the look of a code editor, with dark and light
themes. Mockups approved by the owner are in `docs/design/mockups/`:

- `question-screen-themes.html`: question screen after a wrong answer, dark and
  light.
- `home-and-catalog.html`: home page (dark) and catalog with filters (light).

Key elements: monospace accents (JetBrains Mono) with Inter for text; sidebar
tree of topics with progress; level chips colour-coded (junior green, middle
amber, senior orange, lead purple, architect blue); code in an editor-like
panel with line numbers; feedback rendered like terminal output
(`explain`, `fix`, `refs`); progress as XP bar and streak; home page "terminal"
showing a sample session. Palette in the light theme follows GitHub-light-like
syntax colours.

### 9.1 Responsive layout

The owner studies from a desktop PC, a laptop, a tablet and a phone; every page
and every mode must be fully usable on all four. This is a v1 requirement, not
polish.

| Width | Target devices | Layout |
|---|---|---|
| < 640 px | Phones | Single column; topic sidebar and catalog filters become a drawer; answer options stacked full width; sticky bottom bar with the primary action (Answer / Next); code panel scrolls horizontally inside itself, never the page |
| 640–1023 px | Tablets, small laptops, phone landscape | Sidebar collapsible; options in two columns; feedback below the question |
| 1024–1439 px | Laptops | Full layout as in the mockups |
| ≥ 1440 px | Desktop monitors | Content width capped (about 1280 px) and centred; on the question screen, feedback may sit beside the question instead of below |

Rules:

- Mobile-first CSS; breakpoints defined once as tokens.
- Touch targets at least 44×44 px; nothing depends on hover.
- Keyboard shortcuts on desktop (1–4 to pick an option, Enter to submit, N for
  next), shown as hints only on devices with a fine pointer.
- Code snippets keep their line breaks at every width (horizontal scroll inside
  the panel); font size never below 13 px.
- Interactions that need text input (typed `output` answers, notes on `open`
  questions) must not be hidden by the on-screen keyboard.
- No horizontal page scroll at 320 px width.
- Tests: the builder checks every page at 375, 768, 1280 and 1920 px widths
  (Playwright screenshots in CI are the expected way).

## 10. Documentation and repository hygiene

- `README.md`: what it is, screenshots, how to run locally (Docker Compose for
  PostgreSQL + `dotnet run`), how to contribute content.
- `AGENTS.md`: rules for AI agents and contributors (exists from the first
  commit).
- `docs/architecture.md`: layers, render modes, data flow; kept current with
  the code.
- `docs/content-guide.md`: how to write a question, schema reference, quality
  bar, how to verify.
- `docs/deployment.md`: VPS setup.
- `LICENSE` (MIT) at the root, `content/LICENSE` (CC BY-SA 4.0).
- Decisions that change this design are recorded as short ADRs in
  `docs/adr/`.

## 11. Settled details (designer's calls, owner may override)

These came out of a builder review and two independent design reviews (Codex
and Fable) of this document. They are decisions made by the designer, not by
the owner; the owner can change any of them. Most review findings were folded
directly into sections 4-8; this list keeps the remaining calls in one place.

1. **Admin bootstrap.** Config key `App:AdminTelegramIds` (list), from the
   GitHub variable `ADMIN_TELEGRAM_IDS` set by bootstrap (section 8.7.3). The sign-in gate
   grants `Admin` to these ids on every sign-in. Other roles are managed from
   the admin page later.
2. **Auth and culture routing.** Library endpoints (`/signin-telegram/*`) live
   outside the culture prefix and are excluded from culture redirects. The
   library's `LoginPath` and `AccessDeniedPath` point to a culture-neutral
   `/signin` that redirects to `/{culture}/signin` (culture from the cookie or
   `Accept-Language`), preserving `denied`/`error` query flags. The sign-in
   link passes `returnUrl` with the culture path. Sign-in and logout controls
   are rendered by the static SSR layout. Anonymous responses show only a plain
   "Sign in" link (no form, so no antiforgery cookie and they stay cacheable);
   signed-in responses, which bypass the cache, render the logout form with its
   antiforgery token.
   Auth state reaches islands as described in section 8.8.
3. **Separate bots per environment.** Production and development use different
   Telegram bots (OIDC enabled on each, own redirect URIs, own secrets). Owner
   action: create the dev bot in BotFather before sign-in can be tested locally.
4. **Anonymous progress.** Anonymous users keep the answer outbox
   (section 8.8) in `localStorage`. No scheduling and no "due" filter for
   anonymous users; Review stays sign-in only. On first sign-in the outbox is
   flushed and replayed through the server scheduler.
5. **Where grading runs.** Islands receive full question data including correct
   options and grade client-side, so feedback is instant and works offline
   within a loaded session. The server records the reported grade without
   re-checking (it validates everything else, section 8.8). This is a
   self-study tool with no stakes, so trusting the client is accepted.
   Domain grading code stays free of server dependencies so the WASM client can
   reference `Trainer.Domain`.
6. **Content delivery.** Owner's decision: content is delivered separately
   from the app image, through its own workflow (sections 7.3 and 8.7).
   Designer's calls inside it: release directories plus a `current` symlink,
   import through a localhost-only internal endpoint with a token, schema
   version range check, automatic rollback of the symlink on failed import.
7. **Retired questions.** Their pages stay readable with a "retired" notice, get
   `noindex`, leave the sitemap and catalog, and stop appearing in Practice and
   Mock. Review drops them. If a redirect entry exists, the old URL redirects
   (301) instead.
8. **Responsive checks in CI.** Playwright runs at 375, 768, 1280 and 1920 px,
   uploads screenshots as artifacts and fails only on functional layout
   assertions (no horizontal page overflow, primary action visible, no
   overlapping key elements). No pixel-diff baselines in v1.
9. **Multi-choice partial credit, Review recall step, scheduling rule for
   not-yet-due answers, Mock assessment phase**: as described in section 5.

## 12. Left to the builder

- Exact SM-2 constants and the mock-interview level mix.
- Exact UI of anonymous-progress import on first sign-in.
- YAML vs Markdown-with-front-matter for content files (schema is fixed).
- Column-level details of the data model beyond the decided points.
- Component structure inside Blazor projects, CSS organisation (CSS isolation
  plus a global tokens file is the expected default).
- Choice of Markdown renderer (Markdig expected) and syntax highlighter
  (server-side highlighting preferred so static pages need no JS for it).
