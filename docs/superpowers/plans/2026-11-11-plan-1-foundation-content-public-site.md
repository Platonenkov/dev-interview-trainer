# Plan 1: Foundation, Content Pipeline and Public Site — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A buildable, tested solution that reads bilingual question files,
validates them (including isolated execution of code snippets), imports them
into PostgreSQL, and serves the public, indexable, cacheable site: home,
catalog with filters, topic pages and question pages in RU and EN, in the
approved "IDE" design with dark/light themes, on every screen size.

**Architecture:** Clean layers (Domain → Application → Infrastructure/Web).
Content lives as YAML files, is parsed and validated by `Trainer.Content`,
rendered to safe HTML at import time and stored in PostgreSQL. Public pages
are pure static SSR Razor components (no interactive render modes in this
plan), cached in-process for anonymous users.

**Tech Stack:** .NET 10 (SDK 10.0.x, see `global.json`), ASP.NET Core Blazor
Web App (static SSR only in this plan), EF Core + Npgsql, YamlDotNet, Markdig,
ColorCode.HTML, Cysharp `Ulid` (CLI only), xUnit v3, Testcontainers.PostgreSql,
Microsoft.AspNetCore.Mvc.Testing, Microsoft.Playwright, Docker (snippet
sandbox).

**Spec:** `docs/superpowers/specs/2026-11-11-dev-interview-trainer-design.md`
(read it first; section numbers below refer to it).

## Roadmap (all plans)

| Plan | Scope | Spec sections | Depends on |
|---|---|---|---|
| **1 (this)** | Solution, domain keys, content schema/reader/validator/renderer, snippet sandbox, CLI, DB for content, importer, public SSR pages, i18n, SEO, output cache, themes, responsive layout, CI | 3, 5.1, 5.2 (schema only), 6, 7, 8.1-8.6, 9, 10 | — |
| 2 | Telegram sign-in, users/roles/security stamp, grading, scheduler, answers outbox + `/api`, WASM islands: Practice, Mock, Review, profile, admin page | 4, 5.2-5.4, 8.2 (islands), 8.6 (user tables), 8.8, 11 | 1 |
| 3 | Deployment: Dockerfile, compose, migration bundle, bootstrap script, `deploy-app`, `deploy-content`, `rotate-db-password`, internal import endpoint, backups, `docs/deployment.md` | 8.7 | 1 (2 for auth config) |
| 4 | Content production: ~150 questions for the private build, then ~400, in topic batches reviewed by the owner | 6, 7.6 | 1 |

Each plan is reviewed by Codex and Fable before execution.

## Global Constraints

- Target framework `net10.0`; `global.json` pins SDK `10.0.203` with `rollForward: latestFeature`.
- `Directory.Build.props`: `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<AnalysisLevel>latest-recommended</AnalysisLevel>`, NuGet audit (`NuGetAudit=true`, `NuGetAuditMode=all`, `NuGetAuditLevel=low`).
- Central package management in `Directory.Packages.props`; versions = latest stable compatible with `net10.0` at execution time. No FluentAssertions. No Moq.
- Explicit types instead of `var` wherever possible; async with `CancellationToken` for IO; `System.Text.Json` with cached options; `StringComparison.Ordinal*`.
- Comments, docs, commit messages in English; Conventional Commits; no AI attribution lines.
- Domain references no other project and no framework packages.
- Levels (ordered): `junior`, `middle`, `senior`, `lead`, `architect`.
- Question type keys: `open`, `single`, `multi`, `output`, `find-bug`, `situation`.
- Supported cultures: `ru`, `en`; culture is the first URL segment.
- Content schema version supported by this app: min `1`, max `1`.
- Public pages: static SSR only, no `[StreamRendering]`, no forms, no `Set-Cookie` in anonymous responses.
- Markdown: raw HTML disabled; only `https:` absolute links and site-relative links.
- Snippet sandbox: `--network none`, read-only root, 256 MB memory, 10 s run timeout, 64 KB output cap, invariant culture, `TZ=UTC`.
- WCAG 2.2 AA: 4.5:1 body text, 3:1 large text and UI boundaries.
- Responsive widths checked: 375, 768, 1280, 1920 px; no horizontal page scroll at 320 px.

## Review Focus

1. **A question exists in only one language.** Expect: the page in the missing culture returns 404, the catalog in that culture omits it, hreflang on the existing page lists only its own culture. Test: Task 11 `QuestionPage_MissingLocale_Returns404AndHreflangListsOnlyExistingCulture`.
2. **Content files with Windows line endings or a BOM.** Expect: parsed identically, same content hash as the LF version. Test: Task 3 `ContentHash_IsIndependentOfLineEndingsAndBom`.
3. **Catalog query with junk or repeated parameters** (`?level=MIDDLE&level=middle&topic=nope&utm_source=x`). Expect: normalised filters (case-insensitive, deduplicated, unknown dropped), 200, canonical points to the normalised URL, cache key ignores `utm_source`. Test: Task 10 `Catalog_NormalisesJunkQuery` and Task 13 `OutputCache_IgnoresUnknownQueryParameters`.
4. **Very long code lines and long words on a 375 px screen.** Expect: code panel scrolls inside itself, page has no horizontal scroll. Test: Task 14 `NoHorizontalOverflow` runs on the seed question with a 160-character line.
5. **Re-importing the same content, then content where a file was deleted.** Expect: second import is `Unchanged` with no writes; after deletion the question is retired (page readable with notice, `noindex`, absent from catalog and sitemap), not deleted. Test: Task 8 `Import_SameContentTwice_SecondIsUnchanged` and `Import_RemovedFile_RetiresQuestion`; Task 11 `RetiredQuestion_IsReadableNoindexAndHiddenFromCatalog`.

---

## File Structure

```
global.json
Directory.Build.props
Directory.Packages.props
.editorconfig
DevInterviewTrainer.slnx
LICENSE                                     MIT
README.md
docs/architecture.md
docs/content-guide.md
.github/workflows/ci.yml
content/
  LICENSE                                   CC BY-SA 4.0 legal code reference + notice
  schema-version                            "1"
  stacks.yaml
  dotnet/topics.yaml                        23 topics from spec section 6
  dotnet/redirects.yaml                     empty list
  dotnet/<topic>/<slug>.yaml                6 seed questions (one per type)
src/Trainer.Domain/
  Levels/Level.cs, Levels/LevelKeys.cs
  Questions/QuestionType.cs, Questions/QuestionTypeKeys.cs, Questions/GradingMode.cs
  Keys/QuestionId.cs, Keys/StackKey.cs, Keys/TopicKey.cs, Keys/Slug.cs, Keys/CultureKey.cs
src/Trainer.Content/
  Model/*.cs                                YAML document records
  Reading/ContentReader.cs, Reading/ContentHasher.cs, Reading/YamlSetup.cs
  Validation/ContentValidator.cs, Validation/ContentIssue.cs, Validation/IssueCodes.cs
  Rendering/ContentRenderer.cs, Rendering/SafeLinkPolicy.cs
  Snippets/ISnippetRunner.cs, Snippets/DockerSnippetRunner.cs, Snippets/SnippetVerifier.cs
  ContentSchema.cs
src/Trainer.Application/
  Content/IContentImportService.cs, Content/ContentImportResult.cs
  Catalog/ICatalogQueries.cs, Catalog/CatalogQuery.cs, Catalog/CatalogPage.cs
  Questions/IQuestionQueries.cs, Questions/QuestionPageModel.cs
  Topics/ITopicQueries.cs, Home/IHomeQueries.cs, Seo/ISitemapQueries.cs
  Abstractions/IClock.cs, Abstractions/IContentCacheInvalidator.cs
src/Trainer.Infrastructure/
  Persistence/TrainerDbContext.cs, Persistence/Entities/*.cs, Persistence/Configurations/*.cs
  Persistence/Migrations/*
  Content/ContentImportService.cs
  Queries/CatalogQueries.cs, Queries/QuestionQueries.cs, Queries/TopicQueries.cs, Queries/HomeQueries.cs, Queries/SitemapQueries.cs
  DependencyInjection.cs
src/Trainer.Web/
  Program.cs
  Routing/CultureRouteConstraint.cs, Routing/RootRedirectEndpoint.cs
  Localization/Resources/SharedResource.{ru,en}.resx, Localization/SharedResource.cs
  Components/App.razor, Components/Routes.razor, Components/Layout/MainLayout.razor, Components/Layout/Header.razor
  Components/Pages/Home.razor, Catalog.razor, TopicPage.razor, QuestionPage.razor, NotFound.razor
  Components/Seo/SeoHead.razor, Components/Seo/BreadcrumbJsonLd.razor
  Endpoints/SitemapEndpoints.cs
  Caching/PublicPageCachePolicy.cs, Caching/OutputCacheInvalidator.cs
  Startup/ContentStartupImporter.cs
  wwwroot/css/tokens.css, wwwroot/css/site.css, wwwroot/js/theme.js
tools/Trainer.ContentCli/Program.cs, Commands/*.cs
tests/Trainer.Domain.Tests/
tests/Trainer.Content.Tests/                (+ TestContent/ fixtures)
tests/Trainer.Infrastructure.Tests/         Testcontainers PostgreSQL
tests/Trainer.Web.Tests/                    WebApplicationFactory + Testcontainers
tests/Trainer.Web.E2E/                      Playwright layout checks
```

`Trainer.Contracts` and `Trainer.Web.Client` are created in Plan 2 (no islands here).

---

### Task 1: Solution scaffold and CI

**Files:**
- Create: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `DevInterviewTrainer.slnx`, `LICENSE`, `content/LICENSE`, `README.md` (stub: one paragraph + "see docs/"), `.github/workflows/ci.yml`
- Create projects: all `src/*`, `tools/Trainer.ContentCli`, `tests/*` from File Structure (empty except one smoke test)
- Test: `tests/Trainer.Domain.Tests/SmokeTests.cs`

**Interfaces:**
- Produces: project references — Application→Domain; Content→Domain; Infrastructure→Application, Content; Web→Infrastructure, Application; ContentCli→Content; each test project references its subject.

- [ ] **Step 1:** Create the files with the Global Constraints settings. `.editorconfig`: `csharp_style_var_for_built_in_types = false:warning`, `csharp_style_var_when_type_is_apparent = false:warning`, `csharp_style_var_elsewhere = false:warning`, private fields `_camelCase` rule, file-scoped namespaces.
- [ ] **Step 2:** `ci.yml` job `build-test` on `ubuntu-latest`: `actions/setup-dotnet` (from `global.json`), `dotnet restore`, `dotnet format --verify-no-changes`, `dotnet build -warnaserror --no-restore`, `dotnet test --no-build --filter "Category!=E2E"`. Pin every action by full commit SHA with a version comment. Top-level `permissions: contents: read`. Triggers: `pull_request`, `push` to `main`.
- [ ] **Step 3:** Smoke test `Solution_Builds`: add `public sealed class AssemblyMarker;` to `Trainer.Domain` and assert `Assert.Equal("Trainer.Domain", typeof(AssemblyMarker).Assembly.GetName().Name)`.
- [ ] **Step 4:** Run `dotnet build -warnaserror` and `dotnet test`. Expected: build succeeded, 1 test passed.
- [ ] **Step 5:** Commit `chore: scaffold solution, central packages and CI`.

### Task 2: Domain keys and enums

**Files:**
- Create: `src/Trainer.Domain/Levels/*`, `Questions/*`, `Keys/*`
- Test: `tests/Trainer.Domain.Tests/LevelKeysTests.cs`, `QuestionTypeKeysTests.cs`, `KeysTests.cs`

**Interfaces:**
- Produces:
  - `enum Level { Junior = 1, Middle = 2, Senior = 3, Lead = 4, Architect = 5 }`
  - `static class LevelKeys { string ToKey(Level); bool TryParse(string? key, out Level level); IReadOnlyList<Level> All }` — parsing is case-insensitive, `ToKey` returns lowercase.
  - `enum QuestionType { Open, Single, Multi, Output, FindBug, Situation }`, `static class QuestionTypeKeys` with the same three members (`"find-bug"` ↔ `FindBug`).
  - `enum GradingMode { Self, Auto }` with keys `self`, `auto` (`llm` rejected in v1).
  - `readonly record struct QuestionId(string Value)` with `static bool TryCreate(string?, out QuestionId)`: 26 chars, Crockford base32 `^[0-9A-HJKMNP-TV-Z]{26}$`.
  - `Slug`, `StackKey`, `TopicKey`: `readonly record struct X(string Value)` + `TryCreate`, regex `^[a-z0-9]+(-[a-z0-9]+)*$`, length 1..80.
  - `CultureKey`: `TryCreate` accepts only `ru`, `en`; `static IReadOnlyList<CultureKey> Supported`.

- [ ] **Step 1: Write failing tests**

```csharp
[Theory]
[InlineData("junior", Level.Junior)] [InlineData("ARCHITECT", Level.Architect)]
public void TryParse_AcceptsKnownKeysCaseInsensitive(string key, Level expected)
{ Assert.True(LevelKeys.TryParse(key, out Level level)); Assert.Equal(expected, level); }

[Fact] public void Levels_AreOrdered() => Assert.True(Level.Senior > Level.Middle);
[Fact] public void FindBug_RoundTrips() { Assert.Equal("find-bug", QuestionTypeKeys.ToKey(QuestionType.FindBug)); Assert.True(QuestionTypeKeys.TryParse("find-bug", out QuestionType t)); Assert.Equal(QuestionType.FindBug, t); }
[Theory] [InlineData("01JD4X7Q8N9V2K3M4P5R6S7T8W", true)] [InlineData("01JD4X7Q8N9V2K3M4P5R6S7T8I", false)] [InlineData("short", false)]
public void QuestionId_Validates(string value, bool ok) => Assert.Equal(ok, QuestionId.TryCreate(value, out _));
[Theory] [InlineData("result-deadlock-ui-thread", true)] [InlineData("Result", false)] [InlineData("a--b", false)] [InlineData("-a", false)]
public void Slug_Validates(string value, bool ok) => Assert.Equal(ok, Slug.TryCreate(value, out _));
[Fact] public void Culture_RejectsUnsupported() => Assert.False(CultureKey.TryCreate("de", out _));
[Fact] public void Grading_RejectsLlmInV1() => Assert.False(GradingModeKeys.TryParse("llm", out _));
```

- [ ] **Step 2:** `dotnet test tests/Trainer.Domain.Tests` → FAIL (types missing).
- [ ] **Step 3:** Implement the types listed in Interfaces. Regexes as `[GeneratedRegex]`.
- [ ] **Step 4:** Run tests → PASS.
- [ ] **Step 5:** Commit `feat(domain): add levels, question types and validated keys`.

### Task 3: Content model, reader and hash

**Files:**
- Create: `src/Trainer.Content/Model/*.cs`, `Reading/ContentReader.cs`, `Reading/ContentHasher.cs`, `Reading/YamlSetup.cs`, `ContentSchema.cs`
- Create fixtures: `tests/Trainer.Content.Tests/TestContent/valid/**` (schema-version, stacks.yaml, dotnet/topics.yaml with 3 topics, dotnet/redirects.yaml, 6 questions one per type, both locales)
- Test: `tests/Trainer.Content.Tests/ContentReaderTests.cs`, `ContentHasherTests.cs`

**Interfaces:**
- Produces (records, all properties `required`, collections `IReadOnlyList`/`IReadOnlyDictionary`):
  - `QuestionDocument { Id, Slug, Stack, Topic, Level, Type, Grading (strings as in YAML); Tags; Code: CodeBlock?; BugLineSets: IReadOnlyList<IReadOnlyList<int>>?; FixedSource: string?; FixedExpect: Expectation?; Expect: Expectation?; AcceptedOutputs: IReadOnlyList<string>?; Options: IReadOnlyList<OptionDocument>?; Links: IReadOnlyList<LinkDocument>; Locales: IReadOnlyDictionary<string, QuestionLocaleDocument>; Meta: QuestionMeta }`
  - `CodeBlock { Language, Harness ("statements"|"program"|"none"), Source }`
  - `Expectation { Kind ("output"|"exception"|"compile-error"|"nondeterministic"), Value: string? }`
  - `OptionDocument { Id, Correct, Expect: Expectation? }`
  - `LinkDocument { Url, Lang, Kind }`
  - `QuestionLocaleDocument { Title, Prompt, Hint?, Options: IReadOnlyDictionary<string,string>?, OptionExplanations: IReadOnlyDictionary<string,string>?, Answer, Explanation, LinkTitles: IReadOnlyList<string> }`
  - `QuestionMeta { Authors, Verified: DateOnly, DotnetVersion?, NoRunReason? }`
  - `StackDocument { Key, Locales: dict culture → { Title, Description } }`, `TopicDocument` same shape, `RedirectDocument { From, To }`
  - `ContentSet { SchemaVersion: int, Stacks, TopicsByStack: dict stack → list, Questions: IReadOnlyList<SourcedQuestion>, RedirectsByStack, Hash: string, ParseIssues: IReadOnlyList<ContentIssue> }`, `SourcedQuestion { RelativePath (forward slashes), Document }`
  - `ContentIssue(string RelativePath, string Code, string Message)` (record; defined here, used by Tasks 5-8)
  - `interface IContentReader { Task<ContentSet> ReadAsync(string rootPath, CancellationToken ct); }`
  - `static class ContentHasher { string Compute(IEnumerable<(string RelativePath, byte[] Bytes)> files) }` — SHA-256 hex (lowercase) over files ordered by ordinal path, each as `path\n` + bytes with BOM stripped and CRLF→LF.
  - `static class ContentSchema { const int MinSupported = 1; const int MaxSupported = 1; }`
- YAML: YamlDotNet with camelCase naming convention; unknown properties are a parse issue (`yaml-unknown-property`), not silently ignored. A file that fails to parse yields a `ContentIssue` with code `yaml-parse` and does not stop reading others.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public async Task ReadAsync_ValidFixture_ReadsAllParts()
{
    ContentSet set = await new ContentReader().ReadAsync(Fixtures.Valid, CancellationToken.None);
    Assert.Equal(1, set.SchemaVersion);
    Assert.Equal(6, set.Questions.Count);
    Assert.Equal(3, set.TopicsByStack["dotnet"].Count);
    Assert.Empty(set.ParseIssues);
    SourcedQuestion output = Assert.Single(set.Questions, q => q.Document.Type == "output");
    Assert.Equal("output", output.Document.Expect!.Kind);
    Assert.Equal("333", output.Document.Expect.Value);
    Assert.Equal("dotnet/csharp/closure-for-loop.yaml", output.RelativePath);
}
[Fact] public async Task ReadAsync_BrokenYaml_ReportsIssueAndKeepsOthers() // fixture "broken": one bad file + one good
{ ContentSet set = await Read(Fixtures.Broken); Assert.Single(set.Questions); Assert.Contains(set.ParseIssues, i => i.Code == "yaml-parse"); }
[Fact] public async Task ReadAsync_UnknownProperty_ReportsIssue() { ...; Assert.Contains(set.ParseIssues, i => i.Code == "yaml-unknown-property"); }
[Fact] public void ContentHash_IsIndependentOfLineEndingsAndBom()
{
    byte[] lf = "a: 1\nb: 2\n"u8.ToArray();
    byte[] crlfBom = [0xEF, 0xBB, 0xBF, .. "a: 1\r\nb: 2\r\n"u8];
    Assert.Equal(ContentHasher.Compute([("x.yaml", lf)]), ContentHasher.Compute([("x.yaml", crlfBom)]));
}
[Fact] public void ContentHash_ChangesWhenPathChanges() => Assert.NotEqual(Hash("a.yaml", lf), Hash("b.yaml", lf));
```

- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement model, `YamlSetup.CreateDeserializer()`, `ContentReader`, `ContentHasher`. Reader enumerates `schema-version`, `stacks.yaml`, `*/topics.yaml`, `*/redirects.yaml`, `*/*/*.yaml`; all file IO async.
- [ ] **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(content): add content model, YAML reader and content hash`.

### Task 4: Safe Markdown and code rendering

**Files:**
- Create: `src/Trainer.Content/Rendering/ContentRenderer.cs`, `SafeLinkPolicy.cs`
- Test: `tests/Trainer.Content.Tests/ContentRendererTests.cs`, `SafeLinkPolicyTests.cs`

**Interfaces:**
- Produces:
  - `static class SafeLinkPolicy { bool IsAllowed(string url); bool IsAllowedAbsolute(string url); }` — allowed: `https://` absolute URLs; site-relative paths starting with `/` but not `//`. Rejected: `http:`, `javascript:`, `data:`, `vbscript:`, protocol-relative, anything with control characters.
  - `interface IContentRenderer { string RenderMarkdown(string markdown); string RenderCode(string source, string language); }`
  - `ContentRenderer`: Markdig pipeline `UseAdvancedExtensions()` minus raw HTML (`DisableHtml()`), links with disallowed targets rendered as plain text, external links get `rel="nofollow noopener"` and `target` not set; code via ColorCode.HTML `HtmlClassFormatter` (CSS classes, no inline styles) wrapped in `<pre class="code"><code>` with one `<span class="ln">` per line for line numbers (needed by find-bug in Plan 2: each line wrapped in `<span class="line" data-line="N">`).

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void RawHtml_IsEscaped() => Assert.DoesNotContain("<script", R.RenderMarkdown("<script>alert(1)</script>"), StringComparison.OrdinalIgnoreCase);
[Fact] public void JavascriptLink_IsNotRenderedAsAnchor() => Assert.DoesNotContain("href=\"javascript:", R.RenderMarkdown("[x](javascript:alert(1))"), StringComparison.OrdinalIgnoreCase);
[Fact] public void HttpsLink_GetsNofollow() => Assert.Contains("rel=\"nofollow noopener\"", R.RenderMarkdown("[x](https://learn.microsoft.com)"));
[Fact] public void Code_IsEncodedAndLineNumbered()
{ string html = R.RenderCode("var a = \"<b>\";\nint b = 1;", "csharp");
  Assert.Contains("&lt;b&gt;", html); Assert.Contains("data-line=\"2\"", html); Assert.DoesNotContain("style=", html); }
[Theory] [InlineData("https://x.dev", true)] [InlineData("/ru/dotnet", true)] [InlineData("//evil", false)] [InlineData("http://x", false)] [InlineData("JaVaScRiPt:x", false)] [InlineData("data:text/html,x", false)]
public void Policy(string url, bool ok) => Assert.Equal(ok, SafeLinkPolicy.IsAllowed(url));
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(content): render markdown and code safely`.

### Task 5: Content validator

**Files:**
- Create: `src/Trainer.Content/Validation/ContentValidator.cs`, `IssueCodes.cs`
- Test: `tests/Trainer.Content.Tests/ContentValidatorTests.cs` (each test mutates a copy of the valid fixture in memory via a `with` expression)

**Interfaces:**
- Consumes: `ContentSet`, `ContentIssue`, domain key types (Task 2), `SafeLinkPolicy.IsAllowed(string url)` and Markdig (Task 4).
- Produces: `interface IContentValidator { IReadOnlyList<ContentIssue> Validate(ContentSet set); }`, `ContentValidator` implementation; `static class IssueCodes` with the constants below.

Rules (one issue code each; a test per code asserts the code is reported for a minimal violating input and absent for the valid fixture):

| Code | Rule |
|---|---|
| `schema-version-unsupported` | `SchemaVersion` outside `ContentSchema` range |
| `id-invalid`, `id-duplicate` | `QuestionId.TryCreate`; unique across all stacks |
| `slug-invalid`, `slug-duplicate` | `Slug.TryCreate`; unique per stack; file name equals `<slug>.yaml` |
| `stack-unknown`, `topic-unknown`, `path-mismatch` | stack/topic exist; file lives under `<stack>/<topic>/` |
| `level-invalid`, `type-invalid`, `grading-invalid`, `grading-type-mismatch` | keys parse; `open`/`situation` ⇒ `self`, others ⇒ `auto` |
| `options-required`, `options-forbidden`, `options-correct-count` | `single`: ≥2 options, exactly 1 correct; `multi`: ≥2, ≥1 correct; `output`: options optional, if present exactly 1 correct and every option has `expect`; `open`/`situation`/`find-bug`: no options |
| `output-expect-required`, `output-answer-mode` | `output` requires `expect`; without options requires non-empty `acceptedOutputs` when `expect.kind == output` |
| `expect-kind-invalid`, `expect-value-required` | kinds from Global Constraints; `output`/`exception` need `value` |
| `code-required`, `harness-invalid`, `harness-none-reason` | `output`/`find-bug` need `code`; harness `none` needs `meta.noRunReason` and is forbidden for `output` |
| `buglines-required`, `buglines-out-of-range` | `find-bug` needs ≥1 non-empty set; lines within 1..line count |
| `fixed-expect-required` | `fixedSource` present ⇒ `fixedExpect` present |
| `locale-missing-required` | at least one supported locale present |
| `locale-unsupported`, `locale-incomplete` | locale keys in `CultureKey.Supported`; title, prompt, answer, explanation non-empty; option texts for every option id; `optionExplanations` for every incorrect option; `linkTitles.Count == links.Count` |
| `links-required`, `link-scheme` | ≥1 link; `SafeLinkPolicy.IsAllowed` (absolute https only for `links`) |
| `markdown-link-scheme` | every link/image target inside Markdown fields passes `SafeLinkPolicy` (parsed with Markdig AST, not regex) |
| `tag-invalid` | tags match `Slug` rules |
| `redirect-target-unknown`, `redirect-cycle` | redirects point to existing slugs, no cycles |
| `verified-in-future` | `meta.verified` ≤ today (use injected `TimeProvider`) |

- [ ] **Step 1:** Write tests: `ValidFixture_HasNoIssues` plus one `[Fact]` per code named `Reports_<CodeInPascalCase>`, each asserting `Assert.Contains(issues, i => i.Code == IssueCodes.X && i.RelativePath == expectedPath)`.
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement `ContentValidator(TimeProvider timeProvider)`; one private method per rule group; collect all issues (never stop at first).
- [ ] **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(content): validate content against schema and semantic rules`.

### Task 6: Snippet sandbox and verifier

**Files:**
- Create: `src/Trainer.Content/Snippets/ISnippetRunner.cs`, `DockerSnippetRunner.cs`, `SnippetVerifier.cs`, `SnippetResult.cs`
- Test: `tests/Trainer.Content.Tests/Snippets/SnippetResultParserTests.cs` (unit), `DockerSnippetRunnerTests.cs` (`[Trait("Category","Docker")]`)

**Interfaces:**
- Produces:
  - `record SnippetResult(string Kind, string? Value)` — `Kind` uses the expectation kind strings; `Value`: stdout (trimmed, LF) for `output`, exception type full name for `exception`, null for `compile-error`.
  - `interface ISnippetRunner { Task<SnippetResult> RunAsync(CodeBlock code, CancellationToken ct); }`
  - `DockerSnippetRunner(SnippetSandboxOptions options)`; `SnippetSandboxOptions { Image = "mcr.microsoft.com/dotnet/sdk:10.0", RunTimeout = 10 s, BuildTimeout = 120 s, MemoryMb = 256, OutputCapBytes = 65536 }`.
  - `static class SnippetResultParser { SnippetResult Parse(int buildExit, string buildOutput, int runExit, string stdout, string stderr, bool timedOut); }`
  - `SnippetVerifier(ISnippetRunner runner)`: `Task<IReadOnlyList<ContentIssue>> VerifyAsync(ContentSet set, CancellationToken ct)` — issue codes `snippet-mismatch`, `snippet-option-mismatch` (choice output: options whose `expect` equals actual must be exactly the correct ones), `snippet-fixed-mismatch`, `snippet-not-nondeterministic` (5 runs, need ≥2 distinct outputs), `snippet-timeout`.
- Runner behaviour: writes `Snippet.csproj` (`net10.0`, `OutputType Exe`, `ImplicitUsings enable`, `Nullable disable`, `InvariantGlobalization true`, `TreatWarningsAsErrors false`) and `Program.cs` (harness `statements`: source as top-level statements; `program`: source as-is) into a temp dir; runs `docker run --rm --network none --read-only --tmpfs /tmp:rw,exec,size=512m --memory 256m --memory-swap 256m --cpus 1 --pids-limit 256 --security-opt no-new-privileges --cap-drop ALL -e TZ=UTC -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -e HOME=/tmp -v <dir>:/src:ro <image> sh -c "cp -r /src /tmp/w && cd /tmp/w && dotnet build -c Release -o /tmp/out -v q && timeout 10 dotnet /tmp/out/Snippet.dll"` (build and run exit codes separated by printing a marker line between phases). Stdout/stderr truncated at the cap.

- [ ] **Step 1: Write failing unit tests for the parser**

```csharp
[Fact] public void BuildFailure_IsCompileError() => Assert.Equal("compile-error", P.Parse(1, "error CS0103: ...", -1, "", "", false).Kind);
[Fact] public void UnhandledException_YieldsTypeName()
{ SnippetResult r = P.Parse(0, "", 134, "", "Unhandled exception. System.InvalidOperationException: boom\n   at ...", false);
  Assert.Equal(("exception", "System.InvalidOperationException"), (r.Kind, r.Value)); }
[Fact] public void Output_IsTrimmedAndLf() => Assert.Equal("1\n2", P.Parse(0, "", 0, "1\r\n2\r\n", "", false).Value);
[Fact] public void Timeout_IsReported() => Assert.Equal("timeout", P.Parse(0, "", 124, "", "", true).Kind);
```

- [ ] **Step 2: Write Docker integration tests** (`Category=Docker`): `ClosureForLoop_Prints333` (the seed snippet → `("output","333")`), `NetworkIsBlocked` (snippet doing `new HttpClient().GetAsync("https://example.com").Wait()` → `exception`), `InfiniteLoop_TimesOut` (`while(true){}` → `timeout` within 30 s), `CultureIsInvariant` (`Console.Write(1.5.ToString())` → `"1.5"`).
- [ ] **Step 3:** Run → FAIL. **Step 4:** Implement parser, runner (Process with `ArgumentList`, async reads, `CancellationToken` linked to build timeout), verifier. **Step 5:** Run unit tests → PASS; run `dotnet test --filter Category=Docker` locally with Docker → PASS.
- [ ] **Step 6:** Commit `feat(content): run code snippets in an isolated docker sandbox`.

### Task 7: Content CLI and seed content

**Files:**
- Create: `tools/Trainer.ContentCli/Program.cs`, `Commands/ValidateCommand.cs`, `Commands/RunSnippetsCommand.cs`, `Commands/NewQuestionCommand.cs`
- Create: `content/schema-version`, `content/stacks.yaml`, `content/dotnet/topics.yaml` (all 23 topics from spec section 6, RU+EN titles and one-sentence descriptions), `content/dotnet/redirects.yaml`, six seed questions (both locales, real sources, `meta.verified: 2026-11-11`):
  - `csharp/closure-for-loop.yaml` — output, choices `012`/`333`/`222`/compile error, `expect` 333 (mockup question)
  - `async/result-deadlock-ui-thread.yaml` — single
  - `async/configureawait-false-when.yaml` — open
  - `async/valuetask-when-justified.yaml` — multi
  - `async/async-lambda-parallel-foreach.yaml` — find-bug with `fixedSource`/`fixedExpect`
  - `concurrency/thread-pool-starvation-incident.yaml` — situation
  One seed question includes a code line of 160 characters (Review Focus 4).
- Modify: `.github/workflows/ci.yml` — add job `content` (`needs: build-test`, no secrets, `permissions: contents: read`): `dotnet run --project tools/Trainer.ContentCli -- validate content` then `-- run-snippets content`.
- Test: `tests/Trainer.Content.Tests/RepositoryContentTests.cs`

**Interfaces:**
- Consumes: `IContentReader`, `IContentValidator`, `SnippetVerifier`.
- Produces: CLI `validate <root>` (exit 0 = no issues, 1 = issues printed as `path: code: message`, 2 = usage error), `run-snippets <root>`, `new-question <stack> <topic> <slug> <type>` (writes a template file with a fresh ULID via Cysharp `Ulid`). Uses `System.CommandLine` (stable) or plain argument parsing — builder's call.

- [ ] **Step 1:** Test `RepositoryContent_IsValid`: reads `<repo>/content`, validates, `Assert.Empty(issues)` (message lists issues). Test `RepositoryContent_CoversAllTypes`: the six type keys are all present.
- [ ] **Step 2:** Run → FAIL (no content). **Step 3:** Write CLI and content. **Step 4:** Run tests and both CLI commands → PASS / exit 0.
- [ ] **Step 5:** Commit `feat(content): add content CLI, topic taxonomy and seed questions`.

### Task 8: Persistence and content import

**Files:**
- Create: `src/Trainer.Application/Abstractions/*`, `Content/IContentImportService.cs`, `Content/ContentImportResult.cs`
- Create: `src/Trainer.Infrastructure/Persistence/**`, `Content/ContentImportService.cs`, `DependencyInjection.cs`, initial migration `InitialContent`
- Test: `tests/Trainer.Infrastructure.Tests/ContentImportServiceTests.cs` (shared `PostgresFixture` using Testcontainers `postgres:17-alpine`)

**Interfaces:**
- Consumes: `IContentReader`, `IContentValidator`, `IContentRenderer`.
- Produces:
  - Tables (snake_case via `UseSnakeCaseNamingConvention` from EFCore.NamingConventions): `stacks(key pk)`, `stack_locales(stack_key, culture, title, description)`, `topics(stack_key, key, pk both)`, `topic_locales(...)`, `questions(id char(26) pk, stack_key, topic_key, slug, level smallint, type smallint, grading smallint, tags text[], payload jsonb, content_hash, retired_at timestamptz null, unique(stack_key, slug))`, `question_locales(question_id, culture, title, prompt_html, answer_html, explanation_html, hint_html null, options jsonb, links jsonb, pk(question_id, culture))`, `redirects(stack_key, from_slug, to_slug)`, `content_imports(id, hash unique, imported_at, question_count, schema_version)`. `payload` holds language-neutral data: rendered code HTML, raw code, options (id, correct, expect), expectations, bug line sets, accepted outputs, fixed code HTML.
  - `interface IContentImportService { Task<ContentImportResult> ImportAsync(string contentRoot, CancellationToken ct); }`
  - `record ContentImportResult(ContentImportStatus Status, string Hash, int Imported, int Retired, IReadOnlyList<ContentIssue> Issues)`, `enum ContentImportStatus { Imported, Unchanged, Rejected }`
  - `interface IContentCacheInvalidator { Task InvalidateAsync(CancellationToken ct); }` (implemented in Web, Task 13; Infrastructure calls it after a successful import)
  - `IServiceCollection AddTrainerInfrastructure(this IServiceCollection services, string connectionString)`
- Import algorithm: read → if hash equals latest `content_imports.hash` ⇒ `Unchanged` (no writes) → validate (parse issues + validator) ⇒ any issue ⇒ `Rejected` (no writes) → one transaction: upsert stacks/topics/locales, upsert questions by id (slug change on an existing id is rejected as `slug-changed` unless a redirect entry maps old→new), delete+insert locales of changed questions, set `retired_at = now` for ids not in the set (clear it for re-added), replace redirects, insert `content_imports` row → commit → `IContentCacheInvalidator.InvalidateAsync`.
- A PostgreSQL advisory lock (`pg_advisory_xact_lock`) serialises concurrent imports.

- [ ] **Step 1: Write failing tests**
  - `Import_ValidContent_WritesQuestionsAndLocales` — 6 questions, 12 locale rows, prompt HTML contains no `<script`.
  - `Import_SameContentTwice_SecondIsUnchanged` — second result `Unchanged`, `content_imports` count 1.
  - `Import_InvalidContent_IsRejectedAndWritesNothing` — previous content intact.
  - `Import_RemovedFile_RetiresQuestion` — `retired_at` not null, locale rows kept.
  - `Import_ReaddedFile_ClearsRetired`.
  - `Import_SlugChangedWithoutRedirect_IsRejected` with code `slug-changed`.
  - `Import_Success_InvalidatesCache` — fake invalidator called once; not called for `Unchanged`/`Rejected`.
  - `Import_ConcurrentCalls_ProduceOneImportRow`.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement entities, configurations, migration (`dotnet ef migrations add InitialContent`), service. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(infrastructure): persist content and import it transactionally`.

### Task 9: Web host, culture routing, localisation, layout and themes

**Files:**
- Create: `src/Trainer.Web/Program.cs`, `Routing/*`, `Localization/**`, `Components/App.razor`, `Routes.razor`, `Layout/*`, `Pages/NotFound.razor`, `Startup/ContentStartupImporter.cs`, `wwwroot/css/tokens.css`, `site.css`, `wwwroot/js/theme.js`
- Test: `tests/Trainer.Web.Tests/` — `TrainerWebFactory` (WebApplicationFactory + Testcontainers + test content root), `RoutingTests.cs`, `LayoutTests.cs`, `ThemeTokenContrastTests.cs`

**Interfaces:**
- Consumes: `AddTrainerInfrastructure`, `IContentImportService`.
- Produces:
  - Config keys: `ConnectionStrings:Trainer`, `App:PublicBaseUrl`, `App:ContentRoot`.
  - Route constraint `culture` (`CultureRouteConstraint`) accepting `ru|en`; all public pages use `@page "/{Culture:culture}/..."`.
  - `GET /` → 302 to `/{culture}` from cookie `lang` (set only by the language switcher in Plan 2; read here), then `Accept-Language` best match among `ru`,`en`, else `en`. `Vary: Accept-Language, Cookie`.
  - Middleware order: `UseRouting` → `UseRequestLocalization` (custom `RouteDataRequestCultureProvider` reading `culture` route value, only provider) → `UseOutputCache` → `UseAntiforgery` → map components.
  - `<html lang="@culture" data-theme>`; `theme.js` inline in `<head>` (tiny, sets `data-theme` from `localStorage['theme']` or `prefers-color-scheme`); theme toggle button in header uses plain JS, no Blazor interactivity.
  - `tokens.css`: tokens from approved mockups (`docs/design/mockups/`) for `[data-theme=dark]` and `[data-theme=light]`, including `--on-ok`; breakpoints as custom media comments plus documented values 640/1024/1440.
  - Language switcher: plain links to the same path in the other culture with `data-enhance-nav="false"`.
  - `ContentStartupImporter : IHostedService` — runs `ImportAsync(App:ContentRoot)` at start; readiness health check `/health/ready` reports unhealthy until it completes; `/health/live` always healthy. Import failure is logged and leaves the previous content (app still starts).
  - `IStringLocalizer<SharedResource>` keys for all UI strings; RU and EN resx contain the same key set.
- [ ] **Step 1: Write failing tests**
  - `Root_WithRussianAcceptLanguage_RedirectsToRu` (302, `Location: /ru`), `Root_WithUnknownLanguage_RedirectsToEn`, `Root_CookieWinsOverAcceptLanguage`.
  - `UnsupportedCulture_Returns404` (`/de`).
  - `Ru_Page_HasHtmlLangRu_AndRussianStrings`.
  - `Resx_RuAndEn_HaveSameKeys` (reads both resx XML files).
  - `ThemeTokens_MeetWcagAA`: parses `tokens.css` for both themes and asserts contrast ≥ 4.5 for pairs (`--text`/`--bg`, `--muted`/`--bg`, `--on-ok`/`--ok`, `--link`/`--bg`, `--strong`/`--panel`) and ≥ 3.0 for UI colours against the background (`--ok`/`--bg`, `--bad`/`--bg`, `--warn`/`--bg`, `--accent`/`--side`). Decorative `--border`/`--line` are exempt. Contrast formula: WCAG relative luminance.
  - `Ready_IsUnhealthyUntilImportCompletes` (factory with a gated fake importer).
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(web): add culture routing, localisation, layout and themes`.

### Task 10: Catalog and home pages

**Files:**
- Create: `src/Trainer.Application/Catalog/*`, `Home/IHomeQueries.cs`, `src/Trainer.Infrastructure/Queries/CatalogQueries.cs`, `HomeQueries.cs`, `src/Trainer.Web/Components/Pages/Home.razor`, `Catalog.razor`, components for filter panel, question row, level chip, pager
- Test: `tests/Trainer.Infrastructure.Tests/CatalogQueriesTests.cs`, `tests/Trainer.Web.Tests/CatalogPageTests.cs`, `HomePageTests.cs`

**Interfaces:**
- Produces:
  - `record CatalogQuery(CultureKey Culture, StackKey Stack, IReadOnlyList<Level> Levels, IReadOnlyList<TopicKey> Topics, IReadOnlyList<QuestionType> Types, int Page)`; page size constant 25.
  - `static class CatalogQueryParser { CatalogQuery Parse(CultureKey, StackKey, IQueryCollection, IReadOnlySet<TopicKey> knownTopics); string ToCanonicalQueryString(CatalogQuery) }` — case-insensitive, dedupe, drop unknown, sorted by enum order / key; page < 1 ⇒ 1.
  - `record CatalogPage(IReadOnlyList<CatalogRow> Rows, int Total, int Page, int PageCount, IReadOnlyList<FacetCount> LevelCounts, IReadOnlyList<FacetCount> TopicCounts, IReadOnlyList<FacetCount> TypeCounts)`; `record CatalogRow(string Slug, string Title, Level Level, TopicKey Topic, string TopicTitle, QuestionType Type, IReadOnlyList<string> Tags)`; facet counts computed with the other filters applied (standard faceting).
  - `interface ICatalogQueries { Task<CatalogPage> GetAsync(CatalogQuery query, CancellationToken ct); }` — only non-retired questions that have a locale row for the culture; order: level, then title (ordinal of culture-specific collation is the builder's call).
  - `interface IHomeQueries { Task<HomeStats> GetStatsAsync(CultureKey, StackKey, CancellationToken); }` → question count, topic count, type count, per-topic counts.
  - Routes: `/{culture}` (home; stack `dotnet` is the only stack and the default), `/{culture}/dotnet` (stack home = same component), `/{culture}/dotnet/questions`.
  - Filters are links, not forms: forms are forbidden on anonymous pages because Blazor adds antiforgery tokens and cookies to them. Each filter is an `<a>` that toggles one query value (checkbox look, `aria-pressed`). The search box is deferred to Plan 2.
- [ ] **Step 1: Write failing tests**
  - Parser: `Catalog_NormalisesJunkQuery` — `?level=MIDDLE&level=middle&topic=nope&utm_source=x` ⇒ levels `[Middle]`, topics `[]`, canonical query `level=middle`.
  - Queries: `Filters_ByLevelTopicType`, `FacetCounts_IgnoreOwnDimension`, `Excludes_RetiredAndMissingLocale`, `Paging_Works`.
  - Page: `Catalog_RendersRowsWithLinksToQuestionPages` (`href="/ru/dotnet/questions/closure-for-loop"`), `Catalog_HasNoFormsAndNoSetCookie`, `Home_ShowsStatsAndAllFiveLevels`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement following `docs/design/mockups/home-and-catalog.html` (home sections: hero with terminal sample, stats, levels, topics, modes; the mode cards link to Plan 2 routes and are marked "coming soon" until Plan 2). **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat(web): add home and catalog pages`.

### Task 11: Question and topic pages, 404 and redirects

**Files:**
- Create: `src/Trainer.Application/Questions/*`, `Topics/ITopicQueries.cs`, `src/Trainer.Infrastructure/Queries/QuestionQueries.cs`, `TopicQueries.cs`, `src/Trainer.Web/Components/Pages/QuestionPage.razor`, `TopicPage.razor`, question view components
- Test: `tests/Trainer.Web.Tests/QuestionPageTests.cs`, `TopicPageTests.cs`

**Interfaces:**
- Produces:
  - `record QuestionPageModel(QuestionId Id, string Slug, StackKey Stack, TopicKey Topic, string TopicTitle, Level Level, QuestionType Type, string Title, string PromptHtml, string? HintHtml, string? CodeHtml, IReadOnlyList<OptionView> Options, string AnswerHtml, string ExplanationHtml, IReadOnlyList<LinkView> Links, IReadOnlyList<CultureKey> AvailableCultures, bool IsRetired, DateOnly Verified)`; `record OptionView(string Id, string TextHtml, bool Correct, string? ExplanationHtml)`; `record LinkView(string Url, string Title, string Lang, string Kind)`.
  - `interface IQuestionQueries { Task<QuestionPageModel?> GetAsync(CultureKey, StackKey, Slug, CancellationToken); Task<Slug?> GetRedirectAsync(StackKey, Slug, CancellationToken); }`
  - `interface ITopicQueries { Task<TopicPageModel?> GetAsync(CultureKey, StackKey, TopicKey, CancellationToken); }` — title, description, counts per level, first 25 questions (rows reuse `CatalogRow`).
  - Question page layout per `docs/design/mockups/question-screen-themes.html`: chips (level, topic, type), title, prompt, code panel, options listed (no selection on the public page), `<details>` "Show answer" containing correct answer, per-option explanations, explanation, refs (with language marker when link language ≠ page culture), "Practise this question" link (to Plan 2 route, hidden until Plan 2), verified date.
  - Status codes: unknown slug ⇒ 404 with `NotFound.razor` body (set via `HttpContext.Response.StatusCode` before rendering — no streaming); redirect entry ⇒ 301 to new slug (same culture) handled by an endpoint filter/middleware **before** component rendering; retired ⇒ 200 with notice and `noindex`.
- [ ] **Step 1: Write failing tests**
  - `QuestionPage_RendersAnswerInsideDetails` (HTML contains `<details` and the answer text).
  - `QuestionPage_UnknownSlug_Returns404`.
  - `QuestionPage_RedirectEntry_Returns301ToNewSlug`.
  - `QuestionPage_MissingLocale_Returns404AndHreflangListsOnlyExistingCulture` (fixture question with only `en`).
  - `RetiredQuestion_IsReadableNoindexAndHiddenFromCatalog`.
  - `QuestionPage_LinkLanguageMarkerShownWhenDifferent`.
  - `TopicPage_ShowsDescriptionAndCounts`; `TopicPage_UnknownTopic_Returns404`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat(web): add question and topic pages with 404 and redirects`.

### Task 12: SEO metadata, structured data and sitemap

**Files:**
- Create: `src/Trainer.Web/Components/Seo/SeoHead.razor`, `BreadcrumbJsonLd.razor`, `Endpoints/SitemapEndpoints.cs`, `src/Trainer.Application/Seo/ISitemapQueries.cs`, `src/Trainer.Infrastructure/Queries/SitemapQueries.cs`
- Test: `tests/Trainer.Web.Tests/SeoTests.cs`

**Interfaces:**
- Produces:
  - `SeoHead` parameters: `Title`, `Description`, `CanonicalPath`, `IReadOnlyList<CultureKey> AlternateCultures`, `AlternatePathFor: Func<CultureKey,string>`, `NoIndex`, `OgType`. Absolute URLs from `App:PublicBaseUrl`.
  - Rules: canonical = normalised URL (catalog uses `ToCanonicalQueryString`); hreflang for each available culture + `x-default` → `en` version if available else the only one; `noindex` for retired questions and catalog combinations with more than one level or more than one topic, or any `type` filter, or page > 1.
  - `BreadcrumbJsonLd` serialises with `System.Text.Json` (`JsonSerializerOptions` cached, `JavaScriptEncoder.Default` so `</script>` cannot break out).
  - `GET /sitemap.xml` — sitemap index pointing to `/sitemap-ru.xml`, `/sitemap-en.xml`; each lists home, stack home, catalog, topic pages and published non-retired questions in that culture with `<xhtml:link rel="alternate" hreflang>` entries. `GET /robots.txt` with `Sitemap:` line; disallows `/ru/dotnet/practice` etc. (Plan 2 routes) and `/api/`.
- [ ] **Step 1: Write failing tests**
  - `QuestionPage_HasCanonicalAndHreflangPair` (absolute URLs, `x-default`).
  - `Catalog_MultiLevelFilter_IsNoindex`, `Catalog_SingleLevel_IsIndexable`.
  - `JsonLd_IsValidJsonAndEscapesScript` (title containing `</script>` in a fixture question).
  - `Sitemap_ListsPublishedQuestionsPerCulture_ExcludesRetired`.
  - `Robots_ReferencesSitemap`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat(web): add seo metadata, breadcrumbs and sitemaps`.

### Task 13: Output cache, compression and cache invalidation

**Files:**
- Create: `src/Trainer.Web/Caching/PublicPageCachePolicy.cs`, `OutputCacheInvalidator.cs`
- Modify: `src/Trainer.Web/Program.cs`, public page components (apply policy by endpoint metadata `[OutputCache(PolicyName = "public")]`)
- Test: `tests/Trainer.Web.Tests/OutputCacheTests.cs`

**Interfaces:**
- Consumes: `IContentCacheInvalidator` (Task 8).
- Produces:
  - Policy `"public"`: cache only GET/HEAD 200 responses; bypass when request has the auth cookie (name constant `AuthCookieNames.Session = ".dit.session"`, defined here, used by Plan 2); vary by path + `level`, `topic`, `type`, `page` only (`SetVaryByQuery` with those keys; other keys ignored); duration 10 min; tag `"content"`. Size limit 100 MB, max body 2 MB.
  - `OutputCacheInvalidator : IContentCacheInvalidator` → `IOutputCacheStore.EvictByTagAsync("content", ct)`.
  - Response compression (Brotli + Gzip, HTTPS enabled); static assets via `MapStaticAssets()` (fingerprinted, immutable).
- [ ] **Step 1: Write failing tests**
  - `AnonymousPublicPage_SecondRequestIsCacheHit` (assert via a test-only header or a counter incremented in the page model query — builder's choice, but must be deterministic).
  - `AnonymousPublicPages_SetNoCookies` — for `/ru`, `/ru/dotnet/questions`, a topic page, a question page: no `Set-Cookie` header.
  - `RequestWithSessionCookie_BypassesCache`.
  - `OutputCache_IgnoresUnknownQueryParameters` (`?utm_source=a` and `?utm_source=b` → one cache entry).
  - `Import_EvictsCachedPages` (change fixture content, import, page shows new title).
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat(web): cache public pages for anonymous users and evict on import`.

### Task 14: Responsive layout checks with Playwright

**Files:**
- Create: `tests/Trainer.Web.E2E/` (`[Trait("Category","E2E")]`), `LayoutTests.cs`, `AppHostFixture.cs`
- Modify: `src/Trainer.Web/wwwroot/css/site.css` (fixes found by the tests), `.github/workflows/ci.yml` (job `e2e`: install Playwright Chromium, run `dotnet test --filter Category=E2E`, upload `TestResults/screenshots/**` as artifact)

**Interfaces:**
- Consumes: the running app. `AppHostFixture` starts the app on a real port: `WebApplicationFactory<Program>` with `UseKestrel()` (.NET 10) if available; otherwise `dotnet run` of the built app with test configuration — record which one in the test project README line.
- Produces: screenshots `TestResults/screenshots/{page}-{width}-{theme}.png`.

Pages × widths (375, 768, 1280, 1920) × themes (dark, light): home, catalog (`?level=middle`), topic page, question page with the long-line seed question, 404 page.

- [ ] **Step 1: Write failing tests** (one `[Theory]` over the matrix)
  - `NoHorizontalOverflow`: `document.documentElement.scrollWidth <= window.innerWidth`; also at 320 px for home and question page.
  - `CodePanelScrollsInternally`: on the question page, `pre.code` has `scrollWidth > clientWidth` at 375 and the page does not overflow.
  - `PrimaryNavigationReachable`: at 375 the filter drawer toggle is visible and opens the filters (plain `<details>`-based drawer, no Blazor).
  - `TouchTargetsAtLeast44px`: all `a, button, summary` in header and filter panel have bounding box ≥ 44×44 at 375.
  - `NoThemeFlash`: page loaded with `localStorage.theme = 'light'` has `data-theme="light"` at `DOMContentLoaded`.
- [ ] **Step 2:** Run → some FAIL. **Step 3:** Fix CSS until PASS (mobile-first; filters become a drawer below 640 px; sidebar collapsible 640–1023; content max width 1280 px from 1440). **Step 4:** PASS locally and in CI.
- [ ] **Step 5:** Commit `test(web): add responsive layout checks across widths and themes`.

### Task 15: Documentation

**Files:**
- Create: `docs/architecture.md`, `docs/content-guide.md`; Modify: `README.md`, `AGENTS.md` (only if commands or layout changed during Plan 1)
- Test: `tests/Trainer.Content.Tests/ContentGuideExampleTests.cs`

**Interfaces:**
- Produces: `content-guide.md` contains one complete example question per type in fenced `yaml` blocks; the test extracts these blocks and asserts each parses and validates (so the guide cannot drift from the schema).

- [ ] **Step 1:** Test `ContentGuide_ExamplesAreValid` → FAIL (file missing).
- [ ] **Step 2:** Write `content-guide.md`: schema reference (every field, required/optional, per type), harness conventions, issue codes table (copy from Task 5 and Task 6 codes), quality bar (sources, verification date, both languages), how to run `validate` / `run-snippets` / `new-question`. `architecture.md`: layers diagram (text), render-mode rules, content flow (files → reader → validator → renderer → importer → DB → pages → cache), URL scheme, caching rules. `README.md`: what it is, screenshot from E2E artifacts, quick start (`docker run postgres`, `dotnet run --project src/Trainer.Web`), licences.
- [ ] **Step 3:** Test → PASS. Run full suite: `dotnet build -warnaserror`, `dotnet test --filter "Category!=E2E&Category!=Docker"`, CLI `validate content` → all green.
- [ ] **Step 4:** Commit `docs: add architecture and content guides`.

---

## Self-review notes

- Spec coverage for Plan 1: 5.1 (Task 2), 5.2 schema (Tasks 3, 5), 6 (Task 7), 7.1-7.5 (Tasks 3-8), 7.6 seed only (Task 7; volume is Plan 4), 8.1 (Task 1, minus Contracts/Web.Client → Plan 2), 8.2 public half (Tasks 9-11), 8.3 (Task 9), 8.4 (Tasks 12-13), 8.5 (Task 9), 8.6 content tables (Task 8), 9/9.1 (Tasks 9-14), 10 partial (Task 15; deployment.md → Plan 3). Everything else is assigned in the Roadmap.
