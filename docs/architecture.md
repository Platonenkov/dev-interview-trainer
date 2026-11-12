# Architecture

How Dev Interview Trainer is put together as of Plan 1: the content pipeline and
the public, read-only site. Interactive practice, sign-in and the WebAssembly
islands arrive in Plan 2. Deployment and the scheduled link checker arrive in
Plan 3. The reasons behind the design are in
`docs/superpowers/specs/2026-11-11-dev-interview-trainer-design.md`.

## Contents

1. [Layers and allowed references](#layers-and-allowed-references)
2. [Content flow](#content-flow)
3. [Reading and validating content](#reading-and-validating-content)
4. [Rendering content safely](#rendering-content-safely)
5. [Import](#import)
6. [Data model](#data-model)
7. [URL scheme and routing](#url-scheme-and-routing)
8. [Page rendering rules](#page-rendering-rules)
9. [SEO](#seo)
10. [Output cache](#output-cache)
11. [Security headers](#security-headers)
12. [Startup, readiness and health](#startup-readiness-and-health)
13. [Configuration](#configuration)
14. [Migrations policy](#migrations-policy)
15. [Snippet sandbox and the content CLI](#snippet-sandbox-and-the-content-cli)
16. [Tests and CI](#tests-and-ci)
17. [Line endings and formatting](#line-endings-and-formatting)

## Layers and allowed references

```text
Trainer.Domain          no references
Trainer.Application     -> Domain
Trainer.Content         -> Domain
Trainer.Snippets        -> Content
Trainer.Infrastructure  -> Application, Content
Trainer.Web             -> Infrastructure, Application
tools/Trainer.ContentCli -> Content, Snippets
```

| Project | Responsibility |
|---|---|
| `Trainer.Domain` | Value types with validation: `Level`, `QuestionType`, `GradingMode`, `QuestionId` (ULID), `Slug`, `StackKey`, `TopicKey`, `CultureKey`. No IO, no framework dependencies. |
| `Trainer.Application` | Use-case interfaces and result types: `IContentImportService`, `IContentCacheInvalidator`, the query interfaces for catalog, home, question, topic, stack and sitemap. |
| `Trainer.Content` | The content format: model records, strict reader, JSON schema generator, semantic validator, Markdown and code renderer. Shared by the app and the CLI. |
| `Trainer.Snippets` | Compiles and runs snippets in Docker containers and compares the results with the declared expectations. |
| `Trainer.Infrastructure` | EF Core with PostgreSQL, migrations, the import service and the query implementations. |
| `Trainer.Web` | Blazor Web App host: static SSR pages, routing, SEO, output cache, security headers, health checks. |
| `tools/Trainer.ContentCli` | `validate`, `run-snippets`, `check-links`, `new-question`, `generate-schemas`. |

Rules:

- Dependencies point inward. Domain references nothing.
- **`Trainer.Web` never references `Trainer.Snippets`.** Untrusted snippet code
  must not be reachable from the web process. Only the CLI and CI run it.
- No business logic in Razor components or endpoints; they call Application
  interfaces. Services are registered in DI, not static helpers with logic.
- `Trainer.Web.Client` (WebAssembly islands) and `Trainer.Contracts` (DTOs shared
  with the client) are created in Plan 2.

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
content in one transaction.

1. Read the content.
2. Begin a transaction and take `pg_advisory_xact_lock`, which serialises
   concurrent imports from any process.
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
localised HTML in `question_locales`.

## Data model

| Table | Content |
|---|---|
| `stacks`, `stack_locales` | Stacks and their localised titles. `stacks.retired_at` is set when a stack disappears from the content. |
| `topics`, `topic_locales` | Topics per stack and their localised titles and descriptions. `topics.retired_at` is set when a topic disappears from the content. |
| `questions` | One row per question: `id` (ULID), stack, topic, level, type, slug, tags, language-neutral `payload` JSONB, verified date, `retired_at`. Unique on (stack, slug). |
| `question_locales` | One row per question and culture: title, prompt, hint, answer, explanation (HTML), options and links (JSONB). **A question is published in a culture if and only if this row exists.** The catalog, sitemap and `hreflang` all query this table. |
| `redirects` | Old to new slug mappings per stack (`from_slug`, `to_slug`; the topic part of a redirect path is dropped because slugs are unique per stack). |
| `content_imports` | Hash, schema and render versions, import time, question count, and an `is_active` flag with a partial unique index so that exactly one import is active. |

Level, type and grading are stored as `smallint` with pinned numeric values (`PersistedEnumValuesTests` guards them). The tables for
users, answers, review state and mock sessions come with Plan 2.

## URL scheme and routing

| Path | Page |
|---|---|
| `/` | 302 to `/{culture}`. Culture comes from the `lang` cookie, then `Accept-Language`, then `en`. The response varies on `Accept-Language, Cookie`. |
| `/lang/{culture}?return={path}` | Explicit language choice: sets the `lang` cookie (`Path=/`, 365 days, `SameSite=Lax`, `HttpOnly`, `Secure` over HTTPS) and answers 302 to `return`. `return` must be a site-relative path equal to `/{culture}` or inside it, without `//`, `\` or control characters; anything else goes to `/{culture}`. An unsupported culture is 404. The response is `no-store`, `X-Robots-Tag: noindex`, never output-cached, and `/lang/` is disallowed in `robots.txt`. |
| `/{culture}` | Home of the default stack (`App:DefaultStack`). |
| `/{culture}/{stack}` | Stack home. Its canonical URL for the default stack is `/{culture}`. |
| `/{culture}/{stack}/questions` | Catalog. Query: `level`, `topic`, `type`, `page`. |
| `/{culture}/{stack}/topics/{topic}` | Topic page. |
| `/{culture}/{stack}/questions/{slug}` | Question page. |
| `/{culture}/about` | About the project. |
| `/sitemap.xml`, `/sitemap-{lang}.xml`, `/robots.txt` | Sitemap index, one sitemap per culture, robots. |
| `/health/live`, `/health/ready` | Liveness and readiness. |
| `/not-found` | Target of the status-code re-execution; `noindex`. |

Pipeline order (`Program.cs`): security headers, response compression, status code
pages with re-execution, routing, `CultureAndStackMiddleware`, request
localisation, output cache, antiforgery, endpoints.

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
  (`page` is clamped to 1..1000). The page size is 25.

## Page rendering rules

- Public, indexable pages (home, catalog, topic, question, about) are static SSR
  only: no interactive components, no `[StreamRendering]`, no `<form>`, no
  `blazor.web.js`, and no `Set-Cookie` in anonymous responses. Tests enforce the
  cookie rule because the output cache depends on it.
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

## SEO

Every public page writes through `SeoHead`:

- `<title>`, meta description, `canonical`, Open Graph tags.
- `hreflang` alternates for every culture the page exists in, plus `x-default`.
- `noindex` for retired questions, catalogs with more than one level, more than
  one topic, any type filter or `page > 1`, and the 404 page.
- `BreadcrumbList` JSON-LD on every public page.

The sitemap is generated from the database, one file per culture, with
`xhtml:link` alternates. The default stack home is left out because its canonical
is the bare culture home. Absolute URLs are built by `SiteUrls`.

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
- **Not stored:** responses that are not 200 or that carry `Set-Cookie`.
- **Lifetime and size:** 10 minutes, 100 MB total, 2 MB per body.
- **Eviction:** entries are tagged `content`. `OutputCacheInvalidator` refreshes
  the snapshot first and then evicts the tag, so a page rendered after the
  eviction already sees the new taxonomy. The tag is evicted even when the
  snapshot refresh throws, and the exception is then rethrown. If the eviction
  fails as well, both exceptions are surfaced in an `AggregateException`.
- Compression sits outside the cache: entries are stored uncompressed and
  compressed per request (Brotli, gzip).

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
| `App:ApplyMigrationsOnStartup` | Applies EF Core migrations at startup. Allowed only when `ASPNETCORE_ENVIRONMENT` is `Development`; in any other environment startup throws. |

`StartupMigrations.EnsureAllowed`, `PublicBaseUrlValidation.EnsureValid`,
`ForwardedHeadersSetup.Parse` and `AllowedHostsDefault.Apply` run before the host is built.

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

## Migrations policy

- Production never applies migrations at startup. A schema change is a deliberate
  deployment step (Plan 3 documents the procedure).
- Local development may set `App:ApplyMigrationsOnStartup=true` in the
  Development environment only.
- Tests apply migrations once into a template database (`PostgresFixture`) and
  give each test a private copy.
- Migrations live in `Trainer.Infrastructure/Persistence/Migrations` and are
  marked as generated code in `.editorconfig`.

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
| `Trainer.Domain.Tests` | Keys, levels, question types, persisted enum values. |
| `Trainer.Content.Tests` | Reader, `YamlToJson`, hasher, validator, renderer, `SafeLinkPolicy`, schema generation, the repository content, and the content guide (`ContentGuideExampleTests`). |
| `Trainer.Snippets.Tests` | Result parsing, verifier, sandbox argument and cleanup unit tests, and the guard that `ci.yml` pre-pulls the default sandbox image; the Docker sandbox tests carry `Category=Docker`. |
| `Trainer.ContentCli.Tests` | The `check-links` command: URL collection over a temporary content tree, and the link checker against a loopback Kestrel server (never the internet). |
| `Trainer.Infrastructure.Tests` | Import and queries over PostgreSQL (Testcontainers). |
| `Trainer.Web.Tests` | Pages, routing, SEO, cache and readiness through `WebApplicationFactory` and PostgreSQL. |
| `Trainer.Web.E2E` | Playwright and Chromium against the real site on Kestrel; `Category=E2E`. Writes screenshots to `TestResults/screenshots`. |
| `Trainer.TestSupport` | Shared helpers: `PostgresFixture`, `TrainerWebFactory`, `ContentWorkspace`. |

xUnit v3 with the built-in `Assert` on Microsoft.Testing.Platform. Commands are in
`AGENTS.md`.

`.github/workflows/ci.yml` runs on pull requests and on pushes to `main`:

| Job | Steps |
|---|---|
| `build-test` | Restore, `dotnet format --verify-no-changes`, build with warnings as errors, unit and integration tests (without `Docker` and `E2E`). |
| `content` | Build, `validate`, pull the pinned sandbox image, Docker tests, `run-snippets`. No secrets, read-only permissions; exit code 3 is annotated as an infrastructure failure. |
| `e2e` | Build, install Playwright Chromium, E2E tests, upload `TestResults/screenshots` as an artifact. |

`content` and `e2e` start after `build-test` succeeds. A newer push cancels the
running job of the same pull request. Every push to `main` has its own concurrency group
(the commit SHA), so no run on `main` is cancelled or replaced by a later one.

`.github/workflows/link-check.yml` runs `check-links` every Monday at 06:00 UTC and on
demand (`workflow_dispatch`), with read-only permissions and no secrets. The job
failing is the report: it means at least one source link is dead, and the log and the
job summary list the URL and the question files that cite it. A green run can still list
`unknown` links (blocked, throttled or timed out), for example when a publisher refuses
runner addresses, so read the job summary, not only the status. GitHub disables scheduled
workflows after 60 days without repository activity; re-enable it from the Actions tab.

## Line endings and formatting

- `.gitattributes` sets `* text=auto eol=lf` and pins `*.yaml`, `*.yml`,
  `content/schema/*.json` and `content/schema-version` to LF. Binary assets are
  marked binary.
- `.editorconfig` sets UTF-8, LF, four-space indent for C# and two spaces for
  project, JSON and YAML files, a final newline, and the naming and `var` rules
  that the build enforces as warnings (which are errors).
- The content hash ignores CRLF and BOM, so a Windows checkout and a Linux
  checkout produce the same hash.
