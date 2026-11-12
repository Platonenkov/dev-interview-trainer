# Plan 1: Foundation, Content Pipeline and Public Site — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A buildable, tested solution that reads bilingual question files,
validates them (structure, semantics and isolated execution of code snippets),
imports them into PostgreSQL, and serves the public, indexable, cacheable site:
home, about, catalog with filters, topic pages and question pages in RU and EN,
in the approved "IDE" design with dark/light themes, on every screen size.

**Architecture:** Clean layers (Domain → Application → Infrastructure/Web).
Content lives as YAML files, is deserialized strictly into the C# content model
(which is also the source of the generated JSON Schema files) and checked
against semantic rules by `Trainer.Content`, rendered to safe HTML at import time and stored in
PostgreSQL. Snippet execution lives in a separate `Trainer.Snippets` assembly
used only by the CLI. Public pages are plain static SSR Razor components (no
interactive render modes and no `blazor.web.js` in this plan), cached
in-process for anonymous users.

**Tech Stack:** .NET 10 (SDK pinned in `global.json`), ASP.NET Core Razor
components (static SSR), EF Core + Npgsql, YamlDotNet, Markdig,
ColorCode.HTML, Cysharp `Ulid` (CLI only), xUnit v3 on Microsoft.Testing.Platform,
Testcontainers.PostgreSql, Microsoft.AspNetCore.Mvc.Testing, Microsoft.Playwright,
Docker (snippet sandbox).

**Spec:** `docs/superpowers/specs/2026-11-11-dev-interview-trainer-design.md`
(read it first; section numbers below refer to it).

**Revision:** v3, after two rounds of reviews by Codex and Fable (2026-11-11). v3 changes: JsonSchema.Net dropped (its NuGet binaries carry a maintenance-fee EULA); structure is enforced by strict `System.Text.Json` deserialization of the YAML and the JSON Schema files are generated from the C# model for editor support; in-memory content snapshot with a generation number (no DB hits on cache hits, no stale re-caching after import); .NET 10 `NotFound` handling; redirects resolved before question lookup; per-test databases; sandbox and parser refinements; shared test support project. v2 changes from
v1: explicit safe Markdig pipeline; two-container sandbox in its own assembly;
structural validation; import hash/lock/render-version fixes;
Application no longer depends on Content or ASP.NET types; normalised cache
key; background startup import with readiness; dev/test migrations; task
order fixed so every task ends green; separate web test content set;
test runner mode fixed.

## Roadmap (all plans)

| Plan | Scope | Spec sections | Depends on |
|---|---|---|---|
| **1 (this)** | Solution, domain keys, content schema/reader/validator/renderer, snippet sandbox, CLI, DB for content, importer, public SSR pages, i18n, SEO, output cache, themes, responsive layout, CI | 3, 5.1, 5.2 (schema only), 6, 7.1-7.5, 8.1-8.6 (public part), 9, 10 (architecture and content guides) | — |
| 2 | Telegram sign-in, users/roles/security stamp, grading, scheduler, answers outbox + `/api`, WASM islands (Practice, Mock, Review, profile), admin page, catalog search, `blazor.web.js` with theme re-apply on `enhancedload` | 4, 5.2-5.4, 8.2 (islands), 8.6 (user tables), 8.8, 11 | 1 |
| 3 | Deployment: Dockerfile, compose, migration bundle, bootstrap script, `deploy-app`, `deploy-content`, `rotate-db-password`, internal import endpoint, backups, scheduled link checker workflow, `docs/deployment.md` | 7.4 (link checker), 8.7 | 1 (2 for auth config) |
| 4 | Content production: ~150 questions for the private build, then ~400, in topic batches reviewed by the owner | 6, 7.6 | 1 |

Each plan is reviewed by Codex and Fable before execution.

## Global Constraints

- Target framework `net10.0`; `global.json`: SDK `10.0.203`, `rollForward: latestFeature`, `"test": { "runner": "Microsoft.Testing.Platform" }`.
- `Directory.Build.props`: `Nullable=enable`, `TreatWarningsAsErrors=true`, `ImplicitUsings=enable`, `AnalysisLevel=latest-recommended`, `NuGetAudit=true`, `NuGetAuditMode=all`, `NuGetAuditLevel=low`.
- Central package management in `Directory.Packages.props`; versions = latest stable compatible with `net10.0` at execution time. No FluentAssertions. No Moq. No packages whose binaries carry a paid or fee-bearing licence (check the NuGet licence and README before adding any package; JsonSchema.Net is excluded for this reason).
- Test projects: `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>` (set in `tests/Directory.Build.props`).
- Explicit types instead of `var` wherever possible; async with `CancellationToken` for IO; `System.Text.Json` with cached options (source-generated contexts where types are fixed); `StringComparison.Ordinal*`; `TimeProvider` instead of `DateTime.Now`.
- Comments, docs, commit messages in English; Conventional Commits; no AI attribution lines.
- Dependencies: Domain → nothing. Application → Domain. Content → Domain. Snippets → Content. Infrastructure → Application, Content. Web → Infrastructure, Application. ContentCli → Content, Snippets. **Web never references Snippets.**
- Levels (ordered): `junior`, `middle`, `senior`, `lead`, `architect`.
- Question type keys: `open`, `single`, `multi`, `output`, `find-bug`, `situation`.
- Supported cultures: `ru`, `en`; culture is the first URL segment, stack the second.
- Content schema version supported: min `1`, max `1`. `ContentSchema.RenderVersion = 1` (bump whenever rendering or payload format changes).
- Public pages: static SSR only, no `[StreamRendering]`, no `<form>`, no `blazor.web.js`, no `Set-Cookie` in anonymous responses.
- Markdown: explicit Markdig pipeline (section in Task 4), raw HTML disabled, no generic attributes, links/images only `https:` absolute or site-relative.
- Snippet sandbox: build container (no network, 2 GB memory, non-root) and run container (no network, read-only root and `/out`, 256 MB, `--pids-limit 128`, `--cap-drop ALL`, `--security-opt no-new-privileges`, logging disabled, 10 s run deadline enforced from the host with `docker kill`), invariant culture, `TZ=UTC`.
- Test commands (MTP mode; every command keeps `--ignore-exit-code 8` because some projects always have zero matching tests): unit/integration `dotnet test --filter-not-trait "Category=E2E" --filter-not-trait "Category=Docker" --ignore-exit-code 8`; Docker `dotnet test --filter-trait "Category=Docker" --ignore-exit-code 8`; E2E `dotnet test --filter-trait "Category=E2E" --ignore-exit-code 8`. Record them in `AGENTS.md`.
- WCAG 2.2 AA: 4.5:1 body text, 3:1 large text and UI colours.
- Responsive widths checked: 375, 768, 1280, 1920 px; no horizontal page scroll at 320 px.
- Migrations: never applied at production startup. Tests apply them in fixtures; local development applies them only when `ASPNETCORE_ENVIRONMENT=Development` and `App:ApplyMigrationsOnStartup=true` (startup throws if the flag is set in any other environment).

## Review Focus

1. **A question exists in only one language.** Expect: the page in the missing culture returns 404, the catalog in that culture omits it, hreflang on the existing page lists only its own culture plus `x-default`. Tests: Task 11 `QuestionPage_MissingLocale_Returns404`, Task 12 `Hreflang_ListsOnlyExistingCultures`.
2. **Content files with Windows line endings or a BOM.** Expect: parsed identically, same content hash. Test: Task 3 `ContentHash_IsIndependentOfLineEndingsAndBom`.
3. **Catalog query with junk or repeated parameters** (`?level=MIDDLE&level=middle&topic=nope&utm_source=x`). Expect: normalised filters, 200, canonical points to the normalised URL, one cache entry for all equivalent forms. Tests: Task 10 `CatalogQueryParser_NormalisesJunkQuery`, Task 13 `EquivalentCatalogQueries_ShareOneCacheEntry`.
4. **Very long code lines and multi-line tokens on a 375 px screen.** Expect: code panel scrolls inside itself, page has no horizontal scroll, line spans stay balanced. Tests: Task 4 `MultilineComment_KeepsSpansBalancedPerLine`, Task 14 `NoHorizontalOverflow` on the seed question with a 160-character line.
5. **Re-import, rollback and deletion.** Expect: same content twice ⇒ `Unchanged`; A → B → A ⇒ A active again; deleted file ⇒ retired question (readable, `noindex`, absent from catalog and sitemap). Tests: Task 8 `Import_SameContentTwice_SecondIsUnchanged`, `Import_RollbackAtoBtoA_Succeeds`, `Import_RemovedFile_RetiresQuestion`; Task 11 `RetiredQuestion_IsReadableNoindexAndHiddenFromCatalog`.

---

## File Structure

```
global.json, Directory.Build.props, Directory.Packages.props, .editorconfig
DevInterviewTrainer.slnx
LICENSE (MIT), README.md
docs/architecture.md, docs/content-guide.md
.github/workflows/ci.yml
content/
  LICENSE                                   CC BY-SA 4.0 notice + link to legal code
  schema-version                            "1"
  schema/question.schema.json, schema/topics.schema.json, schema/stacks.schema.json, schema/redirects.schema.json   generated from the C# model (Task 3), for editors
  stacks.yaml
  dotnet/topics.yaml                        23 topics from spec section 6
  dotnet/redirects.yaml                     []
  dotnet/<topic>/<slug>.yaml                6 seed questions (one per type)
src/Trainer.Domain/                         Levels/, Questions/, Keys/, AssemblyMarker.cs
src/Trainer.Content/
  Model/*.cs                                YAML document records
  Schema/ContentSchema.cs, Schema/SchemaGenerator.cs
  Reading/ContentReader.cs, Reading/ContentHasher.cs, Reading/YamlToJson.cs, Reading/ContentJsonContext.cs
  Validation/ContentValidator.cs, Validation/ContentIssue.cs, Validation/IssueCodes.cs
  Rendering/ContentRenderer.cs, Rendering/SafeLinkPolicy.cs, Rendering/MarkdownPipelineFactory.cs, Rendering/LineWrapper.cs
src/Trainer.Snippets/                       ISnippetRunner.cs, DockerSnippetRunner.cs, SnippetResult.cs, SnippetResultParser.cs, SnippetVerifier.cs, SandboxOptions.cs
src/Trainer.Application/
  Abstractions/IContentCacheInvalidator.cs
  Content/IContentImportService.cs, Content/ContentImportResult.cs, Content/ImportIssue.cs
  Catalog/ICatalogQueries.cs, Catalog/CatalogQuery.cs, Catalog/CatalogPage.cs
  Questions/IQuestionQueries.cs, Questions/QuestionPageModel.cs
  Topics/ITopicQueries.cs, Home/IHomeQueries.cs, Seo/ISitemapQueries.cs, Stacks/IStackQueries.cs
src/Trainer.Infrastructure/
  Persistence/TrainerDbContext.cs, Persistence/Entities/*.cs, Persistence/Configurations/*.cs, Persistence/Migrations/*
  Content/ContentImportService.cs, Content/PayloadJson.cs
  Queries/*.cs
  DependencyInjection.cs
src/Trainer.Web/
  Program.cs
  Routing/CultureAndStackMiddleware.cs, Routing/RootRedirectEndpoint.cs
  Catalog/CatalogQueryParser.cs
  Localization/SharedResource.cs, Localization/Resources/SharedResource.{ru,en}.resx
  Components/App.razor, Routes.razor, Layout/*, Pages/{Home,About,Catalog,TopicPage,QuestionPage,NotFound}.razor, Shared/*, Seo/*
  Endpoints/SitemapEndpoints.cs
  Caching/PublicPageCachePolicy.cs, Caching/OutputCacheInvalidator.cs
  Startup/ContentStartupImporter.cs, Startup/ReadinessState.cs, Startup/SecurityHeadersMiddleware.cs
  ContentState/ContentSnapshot.cs, ContentState/ContentSnapshotProvider.cs
  wwwroot/css/tokens.css, wwwroot/css/site.css, wwwroot/js/theme-toggle.js
tools/Trainer.ContentCli/                   Program.cs, Commands/*.cs
tests/Directory.Build.props                 MTP runner setting
tests/Trainer.TestSupport/                  PostgresFixture (template databases), TrainerWebFactory, content copy helpers
tests/Trainer.Domain.Tests/
tests/Trainer.Content.Tests/                TestContent/valid/**, TestContent/broken/**
tests/Trainer.Snippets.Tests/               unit + Category=Docker
tests/Trainer.Infrastructure.Tests/         Testcontainers PostgreSQL
tests/Trainer.Web.Tests/                    TestContent/web/**, WebApplicationFactory + Testcontainers
tests/Trainer.Web.E2E/                      Category=E2E, Playwright
```

`Trainer.Contracts` and `Trainer.Web.Client` are created in Plan 2.

---

### Task 1: Solution scaffold and CI

**Files:**
- Create: root files above, all projects (empty except `AssemblyMarker` in Domain), `tests/Trainer.Domain.Tests/SmokeTests.cs`, `.github/workflows/ci.yml`, `LICENSE`, `content/LICENSE`, `README.md` stub

**Interfaces:**
- Produces: project references per Global Constraints; `public sealed class AssemblyMarker;` in `Trainer.Domain`.

- [ ] **Step 1:** Create root files with Global Constraints settings. `.editorconfig`: `csharp_style_var_for_built_in_types = false:warning`, `csharp_style_var_when_type_is_apparent = false:warning`, `csharp_style_var_elsewhere = false:warning`, private field naming `_camelCase`, file-scoped namespaces.
- [ ] **Step 2:** Create `tests/Directory.Build.props` with `UseMicrosoftTestingPlatformRunner=true` and `tests/Trainer.TestSupport` (class library, filled by Tasks 8-9). Write `Solution_Builds`: `Assert.Equal("Trainer.Domain", typeof(AssemblyMarker).Assembly.GetName().Name)`.
- [ ] **Step 3:** `ci.yml`: triggers `pull_request`, `push` to `main`; top-level `permissions: contents: read`; all actions pinned by full commit SHA with version comment. Job `build-test` (`ubuntu-latest`): setup-dotnet from `global.json`, `dotnet restore`, `dotnet format --verify-no-changes`, `dotnet build -warnaserror --no-restore`, unit/integration test command from Global Constraints (Docker is available on the runner for Testcontainers).
- [ ] **Step 4:** Run build and tests locally → build succeeded, 1 test passed. Put the three test commands from Global Constraints into `ci.yml` and `AGENTS.md` ("Before declaring work done").
- [ ] **Step 5:** Commit `chore: scaffold solution, central packages and CI`.

### Task 2: Domain keys and enums

**Files:**
- Create: `src/Trainer.Domain/Levels/*`, `Questions/*`, `Keys/*`
- Test: `tests/Trainer.Domain.Tests/LevelKeysTests.cs`, `QuestionTypeKeysTests.cs`, `KeysTests.cs`

**Interfaces:**
- Produces:
  - `enum Level { Junior = 1, Middle = 2, Senior = 3, Lead = 4, Architect = 5 }`; `static class LevelKeys { string ToKey(Level); bool TryParse(string? key, out Level level); IReadOnlyList<Level> All { get; } }` (parse case-insensitive, `ToKey` lowercase).
  - `enum QuestionType { Open, Single, Multi, Output, FindBug, Situation }`; `static class QuestionTypeKeys` with the same members (`"find-bug"` ↔ `FindBug`).
  - `enum GradingMode { Self, Auto }`; `static class GradingModeKeys` (`self`, `auto`; `llm` rejected in v1).
  - `readonly record struct QuestionId(string Value)` + `static bool TryCreate(string?, out QuestionId)`: `^[0-7][0-9A-HJKMNP-TV-Z]{25}$` (first character 0-7: a ULID is 128 bits).
  - `Slug`, `StackKey`, `TopicKey`: `readonly record struct X(string Value)` + `TryCreate`, `^[a-z0-9]+(-[a-z0-9]+)*$`, length 1..80.
  - `CultureKey`: `TryCreate` accepts `ru`, `en` (case-sensitive lowercase); `static IReadOnlyList<CultureKey> Supported`; `static CultureKey Default => en`.

- [ ] **Step 1: Write failing tests**

```csharp
[Theory] [InlineData("junior", Level.Junior)] [InlineData("ARCHITECT", Level.Architect)]
public void TryParse_AcceptsKnownKeysCaseInsensitive(string key, Level expected)
{ Assert.True(LevelKeys.TryParse(key, out Level level)); Assert.Equal(expected, level); }
[Fact] public void Levels_AreOrdered() => Assert.True(Level.Senior > Level.Middle);
[Fact] public void FindBug_RoundTrips()
{ Assert.Equal("find-bug", QuestionTypeKeys.ToKey(QuestionType.FindBug)); Assert.True(QuestionTypeKeys.TryParse("find-bug", out QuestionType t)); Assert.Equal(QuestionType.FindBug, t); }
[Theory] [InlineData("01JD4X7Q8N9V2K3M4P5R6S7T8W", true)] [InlineData("01JD4X7Q8N9V2K3M4P5R6S7T8I", false)] [InlineData("81JD4X7Q8N9V2K3M4P5R6S7T8W", false)] [InlineData("short", false)]
public void QuestionId_Validates(string value, bool ok) => Assert.Equal(ok, QuestionId.TryCreate(value, out _));
[Theory] [InlineData("result-deadlock-ui-thread", true)] [InlineData("Result", false)] [InlineData("a--b", false)] [InlineData("-a", false)]
public void Slug_Validates(string value, bool ok) => Assert.Equal(ok, Slug.TryCreate(value, out _));
[Fact] public void Culture_RejectsUnsupported() => Assert.False(CultureKey.TryCreate("de", out _));
[Fact] public void Grading_RejectsLlmInV1() => Assert.False(GradingModeKeys.TryParse("llm", out _));
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement (regexes via `[GeneratedRegex]`). **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(domain): add levels, question types and validated keys`.

### Task 3: Content model, strict reader, generated schemas and hash

**Files:**
- Create: `src/Trainer.Content/Model/*`, `Schema/*`, `Reading/*`, `Validation/ContentIssue.cs`; generated `content/schema/*.schema.json`
- Create fixtures: `tests/Trainer.Content.Tests/TestContent/valid/**` (schema-version, stacks.yaml, dotnet/topics.yaml with 3 topics, empty redirects, 6 questions one per type, both locales), `TestContent/broken/**` (one good question; one YAML syntax error; one unknown property; one locale without `title`; one `level: 5`; one `verified: not-a-date`; one using an anchor/alias; one with two YAML documents)
- Test: `tests/Trainer.Content.Tests/ContentReaderTests.cs`, `YamlToJsonTests.cs`, `SchemaGenerationTests.cs`, `ContentHasherTests.cs`

**Interfaces:**
- Produces:
  - `record ContentIssue(string RelativePath, string Code, string Message, string? JsonPath = null)`.
  - Model records (all collections `IReadOnlyList`/`IReadOnlyDictionary`, required members marked `required`, optional ones nullable): `QuestionDocument`, `CodeBlock { Language, Harness, Source }`, `Expectation { Kind, Value? }`, `OptionDocument { Id, Correct, Expect? }`, `LinkDocument { Url, Lang, Kind }`, `QuestionLocaleDocument { Title, Prompt, Hint?, Options?, OptionExplanations?, Answer, Explanation, LinkTitles }`, `QuestionMeta { Authors, Verified: DateOnly, DotnetVersion?, NoRunReason? }`, `StackDocument`, `TopicDocument { Key, Locales: culture → { Title, Description } }`, `RedirectDocument { From, To }`. `QuestionDocument` fields: `Id, Slug, Stack, Topic, Level, Type, Grading, Tags, Code?, BugLineSets?, FixedSource?, FixedExpect?, Expect?, AcceptedOutputs?, Options?, Links, Locales, Meta`. String-valued enums (level, type, grading, harness, expect kind, link kind) stay strings in the model; their allowed values are enforced by the validator (Task 5) and listed in the generated schema.
  - `ContentJsonContext` (source-generated `JsonSerializerContext`): camelCase, `UnmappedMemberHandling = Disallow`, `RespectNullableAnnotations = true`, `RespectRequiredConstructorParameters = true`.
  - `static class YamlToJson { JsonNode Convert(YamlStream stream, out IReadOnlyList<ContentIssue> issues, string relativePath); }` — rules: exactly one document (`yaml-multiple-documents` otherwise); anchors, aliases and merge keys rejected (`yaml-alias-forbidden`); mapping keys must be plain strings. Scalar typing: quoted scalars are always strings; plain scalars `true`/`false` ⇒ bool, `null`/`~`/empty ⇒ null, `^-?(0|[1-9][0-9]*)$` ⇒ integer, everything else (including `012`, `1.5`, `2026-11-11`) ⇒ string. Block scalars are strings.
  - `record SourcedQuestion(string RelativePath, QuestionDocument Document)`.
  - `record ContentSet(int SchemaVersion, IReadOnlyList<StackDocument> Stacks, IReadOnlyDictionary<string, IReadOnlyList<TopicDocument>> TopicsByStack, IReadOnlyList<SourcedQuestion> Questions, IReadOnlyDictionary<string, IReadOnlyList<RedirectDocument>> RedirectsByStack, string Hash, IReadOnlyList<ContentIssue> ReadIssues)`.
  - `interface IContentReader { Task<ContentSet> ReadAsync(string rootPath, CancellationToken ct); }`
  - `static class ContentHasher { string Compute(IEnumerable<(string RelativePath, byte[] Bytes)> files); }` — SHA-256 lowercase hex over files ordered by ordinal path, each `path\n` + bytes with BOM stripped and CRLF→LF. The render version is not part of the hash; the importer compares the pair (hash, render version) (Task 8).
  - `static class ContentSchema { const int MinSupported = 1; const int MaxSupported = 1; const int RenderVersion = 1; }`
  - `SchemaGenerator.Generate()` → JSON Schema documents produced with `System.Text.Json.Schema.JsonSchemaExporter` from `ContentJsonContext`, post-processed (`TransformSchemaNode`) to add `additionalProperties: false`, `enum` lists for the string enums, and `pattern`s from Task 2. Written to `content/schema/` by running `SchemaGenerationTests` with `DIT_UPDATE_SCHEMAS=1` (the test then writes instead of comparing) and later by CLI `generate-schemas` (Task 7); used only by editors, the application never reads them.
- Read pipeline per file: bytes → strip BOM, normalise line endings → YamlDotNet `YamlStream.Load` (syntax error ⇒ `yaml-parse` with line/column) → `YamlToJson.Convert` → `JsonSerializer.Deserialize(node, ContentJsonContext...)`; any `JsonException`, `FormatException` or `InvalidOperationException` from conversion or deserialization ⇒ `content-structure` with the exception's JSON path, and the file is skipped. Reading never stops at the first bad file. File IO is async.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public async Task ReadAsync_ValidFixture_ReadsAllParts()
{
    ContentSet set = await new ContentReader().ReadAsync(Fixtures.Valid, CancellationToken.None);
    Assert.Equal(1, set.SchemaVersion);
    Assert.Equal(6, set.Questions.Count);
    Assert.Equal(3, set.TopicsByStack["dotnet"].Count);
    Assert.Empty(set.ReadIssues);
    SourcedQuestion output = Assert.Single(set.Questions, q => q.Document.Type == "output");
    Assert.Equal(("output", "333"), (output.Document.Expect!.Kind, output.Document.Expect.Value));
    Assert.Equal("dotnet/csharp/closure-for-loop.yaml", output.RelativePath);
}
[Fact] public async Task ReadAsync_BrokenFixture_ReportsEachProblemAndKeepsGoodFile()
{
    ContentSet set = await Read(Fixtures.Broken);
    Assert.Single(set.Questions);
    Assert.Contains(set.ReadIssues, i => i.Code == "yaml-parse");
    Assert.Contains(set.ReadIssues, i => i.Code == "content-structure" && i.RelativePath.EndsWith("unknown-property.yaml", StringComparison.Ordinal));
    Assert.Contains(set.ReadIssues, i => i.Code == "content-structure" && i.RelativePath.EndsWith("missing-title.yaml", StringComparison.Ordinal));
    Assert.Contains(set.ReadIssues, i => i.Code == "content-structure" && i.RelativePath.EndsWith("numeric-level.yaml", StringComparison.Ordinal));
    Assert.Contains(set.ReadIssues, i => i.Code == "content-structure" && i.RelativePath.EndsWith("bad-date.yaml", StringComparison.Ordinal));
    Assert.Contains(set.ReadIssues, i => i.Code == "yaml-alias-forbidden");
    Assert.Contains(set.ReadIssues, i => i.Code == "yaml-multiple-documents");
}
[Theory] [InlineData("v: 012", "012")] [InlineData("v: '10'", "10")] [InlineData("v: 2026-11-11", "2026-11-11")] [InlineData("v: 1.5", "1.5")]
public void PlainAndQuotedScalars_BecomeStrings(string yaml, string expected) => Assert.Equal(expected, Convert(yaml)["v"]!.GetValue<string>());
[Theory] [InlineData("v: 10", 10)] [InlineData("v: -3", -3)]
public void PlainIntegers_BecomeNumbers(string yaml, int expected) => Assert.Equal(expected, Convert(yaml)["v"]!.GetValue<int>());
[Fact] public void ContentHash_IsIndependentOfLineEndingsAndBom()
{
    byte[] lf = "a: 1\nb: 2\n"u8.ToArray();
    byte[] crlfBom = [0xEF, 0xBB, 0xBF, .. "a: 1\r\nb: 2\r\n"u8];
    Assert.Equal(ContentHasher.Compute([("x.yaml", lf)]), ContentHasher.Compute([("x.yaml", crlfBom)]));
}
[Fact] public void ContentHash_ChangesWithPath() { /* same bytes, different path ⇒ different hash */ }
[Fact] public void GeneratedSchemas_MatchCommittedFiles() { /* SchemaGenerator.Generate() equals content/schema/*.json byte for byte (LF) */ }
[Fact] public void GeneratedQuestionSchema_ForbidsAdditionalPropertiesAndListsLevels() { /* "additionalProperties": false at root; level enum = 5 keys */ }
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement model, `YamlToJson`, reader, hasher, schema generator; generate and commit `content/schema/*.json`. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(content): add strict content model, reader, generated schemas and hash`.

### Task 4: Safe Markdown and code rendering

**Files:**
- Create: `src/Trainer.Content/Rendering/*`
- Test: `tests/Trainer.Content.Tests/ContentRendererTests.cs`, `SafeLinkPolicyTests.cs`

**Interfaces:**
- Produces:
  - `static class SafeLinkPolicy { bool IsAllowed(string url); bool IsAllowedAbsolute(string url); }` — allowed: `https://` absolute; site-relative paths starting with `/` but not `//`. Decode HTML entities and percent-encoding before checking the scheme; reject control characters and whitespace in the scheme.
  - `MarkdownPipelineFactory.Create()`: `new MarkdownPipelineBuilder().UsePipeTables(new PipeTableOptions { InferColumnWidthsFromSeparator = false }).UseEmphasisExtras().UseListExtras().UseAutoIdentifiers().DisableHtml().Build()`; the renderer replaces table cell alignment `style` attributes with classes `align-left|center|right` (CSP forbids inline styles) — **no** `UseAdvancedExtensions`, `UseGenericAttributes`, `UseMediaLinks`, `UseAutoLinks`, `UseMathematics`, `UseDiagrams`.
  - `interface IContentRenderer { string RenderMarkdown(string markdown); string RenderCode(string source, string language); IReadOnlyList<string> FindUnsafeLinks(string markdown); }`
  - `ContentRenderer`: parse to AST; walk every `LinkInline` (including images and resolved reference links) and `AutolinkInline`; unsafe target ⇒ replace the inline with its literal text; safe external link ⇒ add `rel="nofollow noopener"`. `FindUnsafeLinks` returns the unsafe targets (used by the validator). Code: ColorCode.HTML `HtmlClassFormatter` (CSS classes, no inline styles) → `LineWrapper` splits on `\n`, closes all open spans at each line end and reopens them at the next line start, wraps each line in `<span class="line" data-line="N">`, whole block in `<pre class="code"><code>`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void RawHtml_IsEscaped() => Assert.DoesNotContain("<script", R.RenderMarkdown("<script>alert(1)</script>"), StringComparison.OrdinalIgnoreCase);
[Theory]
[InlineData("[x](https://a.dev){onmouseover=alert(1)}")]
[InlineData("![x](https://a.dev/i.png){onerror=alert(1)}")]
[InlineData("# T {onclick=alert(1)}")]
public void GenericAttributes_AreNotRendered(string md) => Assert.DoesNotContain(AttributeNames(R.RenderMarkdown(md)), n => n.StartsWith("on", StringComparison.OrdinalIgnoreCase));
[Fact] public void Markdown_NeverEmitsStyleAttributes() => Assert.DoesNotContain("style=", R.RenderMarkdown("| a | b |
|:-|-:|
| 1 | 2 |"), StringComparison.Ordinal);
[Theory] [InlineData("[x](javascript:alert(1))")] [InlineData("[x](javascript&#58;alert(1))")] [InlineData("<http://plain.dev>")] [InlineData("![x](data:image/png;base64,AA)")] [InlineData("[x][r]\n\n[r]: javascript:alert(1)")]
public void UnsafeLinks_AreRenderedAsText(string md) { string html = R.RenderMarkdown(md); Assert.DoesNotContain("href=\"javascript", html, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("src=\"data:", html, StringComparison.Ordinal); Assert.DoesNotContain("href=\"http:", html, StringComparison.Ordinal); }
[Fact] public void HttpsLink_GetsNofollow() => Assert.Contains("rel=\"nofollow noopener\"", R.RenderMarkdown("[x](https://learn.microsoft.com)"));
[Fact] public void Code_IsEncodedAndLineNumbered() { string html = R.RenderCode("var a = \"<b>\";\nint b = 1;", "csharp"); Assert.Contains("&lt;b&gt;", html); Assert.Contains("data-line=\"2\"", html); Assert.DoesNotContain("style=", html); }
[Fact] public void MultilineComment_KeepsSpansBalancedPerLine() { string html = R.RenderCode("/* a\n b */\nint c;", "csharp"); foreach (string line in Lines(html)) Assert.Equal(Count(line, "<span"), Count(line, "</span>")); }
[Theory] [InlineData("https://x.dev", true)] [InlineData("/ru/dotnet", true)] [InlineData("//evil", false)] [InlineData("http://x", false)] [InlineData("JaVaScRiPt:x", false)] [InlineData("java\tscript:x", false)] [InlineData("data:text/html,x", false)]
public void Policy(string url, bool ok) => Assert.Equal(ok, SafeLinkPolicy.IsAllowed(url));
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(content): render markdown and code safely`.

### Task 5: Content validator

**Files:**
- Create: `src/Trainer.Content/Validation/ContentValidator.cs`, `IssueCodes.cs`
- Test: `tests/Trainer.Content.Tests/ContentValidatorTests.cs` (each test mutates a copy of the valid fixture set in memory with `with` expressions)

**Interfaces:**
- Consumes: `ContentSet` (Task 3), domain keys (Task 2), `IContentRenderer.FindUnsafeLinks`, `SafeLinkPolicy` (Task 4).
- Produces: `interface IContentValidator { IReadOnlyList<ContentIssue> Validate(ContentSet set); }`; `ContentValidator(IContentRenderer renderer, TimeProvider time)`; `static class IssueCodes` with the codes below. Structural problems are already caught by strict deserialization (Task 3); these are semantic rules, including allowed values of the string enums (`level-invalid`, `type-invalid`, `grading-invalid`, `harness-invalid`, `expect-kind-invalid`, `link-kind-invalid`).

| Code | Rule |
|---|---|
| `schema-version-unsupported` | `SchemaVersion` outside `ContentSchema` range |
| `id-invalid`, `id-duplicate` | `QuestionId.TryCreate`; unique across all stacks |
| `slug-invalid`, `slug-duplicate`, `path-mismatch` | `Slug.TryCreate`; unique per stack; file is `<stack>/<topic>/<slug>.yaml` |
| `stack-unknown`, `topic-unknown`, `taxonomy-duplicate`, `taxonomy-locale-incomplete` | referenced stack/topic exist; stack and topic keys unique; every stack/topic has title + description in every supported culture |
| `grading-type-mismatch` | `open`/`situation` ⇒ `self`; others ⇒ `auto` |
| `options-required`, `options-forbidden`, `options-correct-count`, `option-id-duplicate` | `single`: ≥2 options, exactly 1 correct; `multi`: ≥2, ≥1 correct; `output`: options optional, if present exactly 1 correct and every option has `expect`; `open`/`situation`/`find-bug`: no options; option ids unique and non-empty |
| `output-expect-required`, `output-answer-mode`, `accepted-outputs-mismatch` | `output` requires `expect`; without options and `expect.kind == output` requires `acceptedOutputs`, and the normalised `expect.value` must be one of the normalised `acceptedOutputs` (normalise = trim + LF) |
| `expect-value-required` | kinds `output`/`exception` need `value` (also for `fixedExpect` and option `expect`) |
| `code-required`, `harness-none-forbidden`, `harness-none-reason` | `output`/`find-bug` need `code`; harness `none` is forbidden for `output` and requires `meta.noRunReason` otherwise |
| `buglines-out-of-range` | every line in every `bugLineSets` set within 1..line count of `code.source` |
| `fixed-expect-required` | `fixedSource` present ⇒ `fixedExpect` present |
| `locale-incomplete` | for every present locale: option text for every option id; `optionExplanations` for every incorrect option; `linkTitles.Count == links.Count` |
| `link-scheme`, `markdown-link-scheme` | `links[].url` passes `SafeLinkPolicy.IsAllowedAbsolute`; `FindUnsafeLinks` empty for every Markdown field |
| `tag-invalid` | tags satisfy `Slug` rules |
| `redirect-target-unknown`, `redirect-cycle`, `redirect-from-live` | redirect targets exist, no cycles, `from` is not a live slug |
| `verified-in-future` | `meta.verified` ≤ today (`TimeProvider`) |

- [ ] **Step 1:** Write `ValidFixture_HasNoIssues` plus one `[Fact]` per code named `Reports_<CodeInPascalCase>`, each `Assert.Contains(issues, i => i.Code == IssueCodes.X && i.RelativePath == expectedPath)`.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement (one private method per rule group; collect all issues). **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(content): validate content semantics`.

### Task 6: Snippet sandbox and verifier

**Files:**
- Create: `src/Trainer.Snippets/*`
- Test: `tests/Trainer.Snippets.Tests/SnippetResultParserTests.cs` (unit), `DockerSnippetRunnerTests.cs` (`[Trait("Category","Docker")]`)

**Interfaces:**
- Consumes: `ContentSet`, `CodeBlock`, `Expectation`, `ContentIssue` (Task 3).
- Produces:
  - `record SnippetResult(string Kind, string? Value)`; kinds: `output`, `exception`, `compile-error`, `timeout`, `sandbox-error` (`Value` carries diagnostics for `sandbox-error`).
  - `record SandboxOptions { string Image = "mcr.microsoft.com/dotnet/sdk:10.0"; TimeSpan BuildDeadline = 3 min; TimeSpan RunDeadline = 10 s; int BuildMemoryMb = 2048; int RunMemoryMb = 256; int OutputCapBytes = 65536; }`
  - `interface ISnippetRunner { Task<SnippetBuild> BuildAsync(CodeBlock code, CancellationToken ct); Task<SnippetResult> RunAsync(SnippetBuild build, CancellationToken ct); }` — `SnippetBuild` is `IAsyncDisposable` (owns the temp dirs) and carries either the output dir or a build `SnippetResult` (`compile-error` / `sandbox-error`).
  - `static class SnippetResultParser { SnippetResult ParseBuild(int exitCode, string output); SnippetResult ParseRun(int exitCode, string stdout, string stderr, bool killedByDeadline, bool truncated); }` — build: exit ≠ 0 and output contains `error CS` ⇒ `compile-error`; exit ≠ 0 otherwise ⇒ `sandbox-error`. Run, checked in this order: deadline ⇒ `timeout`; truncated ⇒ `sandbox-error`; stderr matching `^Unhandled exception\. (?<type>[\w.]+)` ⇒ `exception` with that type; exit 0 ⇒ `output` (trimmed, LF); any other exit code (OOM 137, stack overflow 134 without the line, `Environment.Exit(n)`) ⇒ `sandbox-error` with the exit code. Content that wants to demonstrate a crash uses `exception`, not exit codes.
  - `SnippetVerifier(ISnippetRunner runner)`: `Task<IReadOnlyList<ContentIssue>> VerifyAsync(ContentSet set, CancellationToken ct)` with codes `snippet-mismatch`, `snippet-option-mismatch` (choice output: options whose `expect` equals the actual result must be exactly the correct ones), `snippet-accepted-mismatch` (typed output: actual not in `acceptedOutputs`), `snippet-fixed-mismatch`, `snippet-not-nondeterministic` (build once, run 5 times, need ≥2 distinct outputs), `snippet-timeout`, `snippet-sandbox-error` (CI must treat this as an infrastructure failure, not as content).
- Runner mechanics:
  - The image is pinned by digest in `SandboxOptions.Image` (`mcr.microsoft.com/dotnet/sdk:10.0@sha256:...`) and pulled once by `PrepareAsync` before any deadline starts (CI runs `docker pull` as a separate step).
  - Host temp dir per snippet: `src/` (`Snippet.csproj`: `net10.0`, `OutputType Exe`, `ImplicitUsings enable`, `Nullable disable`, `InvariantGlobalization true`, `UseAppHost false`; `Program.cs`: `statements` = top-level statements, `program` = as is) and `out/`, created with permissions allowing the container user.
  - **Build container**: `docker run --name sb-<guid> --rm --network none --user <host uid>:<host gid> --memory 2g --cpus 2 --pids-limit 512 --cap-drop ALL --security-opt no-new-privileges --log-driver none -e HOME=/tmp -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -e NUGET_PACKAGES=/tmp/nuget --tmpfs /tmp:rw,exec,size=1g -v <src>:/src:ro -v <out>:/out:rw <image> dotnet build /src/Snippet.csproj -c Release -o /out -p:BaseIntermediateOutputPath=/tmp/obj/ -v q`. On Windows hosts `--user` is omitted (Docker Desktop maps ownership).
  - **Run container**: `docker run --name sr-<guid> --rm --network none --read-only --user 65534:65534 --memory 256m --memory-swap 256m --cpus 1 --pids-limit 128 --cap-drop ALL --security-opt no-new-privileges --log-driver none --tmpfs /tmp:rw,noexec,size=16m -e TZ=UTC -e DOTNET_gcServer=0 -e DOTNET_EnableDiagnostics=0 -v <out>:/out:ro <image> dotnet /out/Snippet.dll`.
  - Process started with `ArgumentList` (no shell); stdout/stderr drained concurrently into buffers capped at `OutputCapBytes` (excess discarded, `truncated = true`). The host enforces deadlines; on deadline or cancellation it runs `docker kill <name>` then `docker rm -f <name>` in `finally` (also when the client process already exited).

- [ ] **Step 1: Write failing unit tests for the parser**

```csharp
[Fact] public void CompilerError_IsCompileError() => Assert.Equal("compile-error", P.ParseBuild(1, "Program.cs(1,1): error CS0103: ...").Kind);
[Fact] public void RestoreFailure_IsSandboxError() => Assert.Equal("sandbox-error", P.ParseBuild(1, "error NU1301: Unable to load the service index").Kind);
[Fact] public void UnhandledException_YieldsTypeName() => Assert.Equal(("exception", "System.InvalidOperationException"), Kv(P.ParseRun(134, "", "Unhandled exception. System.InvalidOperationException: boom\n   at ...", false, false)));
[Fact] public void OomKill_IsSandboxError() => Assert.Equal("sandbox-error", P.ParseRun(137, "", "", false, false).Kind);
[Fact] public void Deadline_IsTimeout() => Assert.Equal("timeout", P.ParseRun(137, "", "", true, false).Kind);
[Fact] public void Output_IsTrimmedAndLf() => Assert.Equal("1\n2", P.ParseRun(0, "1\r\n2\r\n", "", false, false).Value);
[Fact] public void Truncated_IsSandboxError_EvenWithExceptionText() => Assert.Equal("sandbox-error", P.ParseRun(134, "x", "Unhandled exception. System.Exception: x", false, true).Kind);
[Fact] public void NonZeroExitWithoutException_IsSandboxError() => Assert.Equal("sandbox-error", P.ParseRun(1, "partial", "", false, false).Kind);
```

- [ ] **Step 2: Write Docker integration tests** (`Category=Docker`):
  - `ClosureForLoop_Prints333` → `("output","333")`.
  - `RunContainer_HasNoNetwork`: run a snippet that sleeps 30 s; poll `docker inspect sr-…` until `State.Running == true`, then assert `HostConfig.NetworkMode == "none"` and cancel; separately a snippet resolving `example.com` yields `exception`.
  - `InfiniteLoop_IsKilledAtRunDeadline`: `while(true){}` → `timeout`; elapsed run phase < `RunDeadline + 5 s`; `docker ps -a --filter name=sr-` is empty afterwards.
  - `IgnoredSigterm_StillKilled`: snippet registering `AppDomain.ProcessExit`/`PosixSignalRegistration` that ignores SIGTERM and loops → `timeout`, container gone.
  - `OutputFlood_IsCappedAndReported`: infinite `Console.Write('x')` → `sandbox-error` or `timeout`, host memory bounded (buffer length ≤ cap).
  - `Cancellation_RemovesContainer`: cancel the token mid-run → `OperationCanceledException`, no container left.
  - `CultureIsInvariant`: `Console.Write(1.5.ToString())` → `"1.5"`.
- [ ] **Step 3:** Run → FAIL. **Step 4:** Implement. **Step 5:** Unit tests PASS; `dotnet test --filter-trait "Category=Docker"` with Docker running → PASS.
- [ ] **Step 6:** Commit `feat(snippets): run code snippets in isolated build and run containers`.

### Task 7: Content CLI and seed content

**Files:**
- Create: `tools/Trainer.ContentCli/**`; `content/schema-version`, `content/stacks.yaml`, `content/dotnet/topics.yaml` (23 topics from spec section 6, RU+EN title and one-sentence description), `content/dotnet/redirects.yaml`, six seed questions (both locales, real sources, `meta.verified: 2026-11-11`):
  - `csharp/closure-for-loop.yaml` — output with choices `012`/`333`/`222`/compile error, each option with `expect`
  - `async/result-deadlock-ui-thread.yaml` — single
  - `async/configureawait-false-when.yaml` — open
  - `async/valuetask-when-justified.yaml` — multi
  - `async/async-lambda-parallel-foreach.yaml` — find-bug with `fixedSource`/`fixedExpect`
  - `concurrency/thread-pool-starvation-incident.yaml` — situation
  One seed snippet contains a 160-character line (Review Focus 4).
- Modify: `.github/workflows/ci.yml` — job `content` (`needs: build-test`, no secrets, `permissions: contents: read`): `dotnet run --project tools/Trainer.ContentCli -- validate`, then Docker tests, then `-- run-snippets`; a `sandbox-error` fails the job with a distinct message.
- Test: `tests/Trainer.Content.Tests/RepositoryContentTests.cs`

**Interfaces:**
- Consumes: `IContentReader`, `IContentValidator`, `SnippetVerifier`.
- Produces: CLI commands, root argument optional (default `content` relative to the current directory): `validate [root]` (exit 0 ok, 1 content issues printed as `path: code: message [pointer]`, 2 usage error), `run-snippets [root]` (exit 3 when any `snippet-sandbox-error`), `new-question <stack> <topic> <slug> <type>` (template file with a fresh ULID from Cysharp `Ulid`, plus the `yaml-language-server` schema comment), `generate-schemas [root]` (writes `SchemaGenerator` output to `<root>/schema`). Argument parsing: builder's call.

- [ ] **Step 1:** Tests `RepositoryContent_IsValid` (reads `<repo>/content`, validates, `Assert.Empty(issues)` with issues in the message) and `RepositoryContent_CoversAllTypes`.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Write CLI and content. **Step 4:** Tests PASS; `validate` and `run-snippets` exit 0.
- [ ] **Step 5:** Commit `feat(content): add content CLI, topic taxonomy and seed questions`.

### Task 8: Persistence and content import

**Files:**
- Create: `src/Trainer.Application/Abstractions/IContentCacheInvalidator.cs`, `Content/*`; `src/Trainer.Infrastructure/Persistence/**`, `Content/ContentImportService.cs`, `Content/PayloadJson.cs`, `DependencyInjection.cs`; migration `InitialContent`
- Create: `tests/Trainer.TestSupport/PostgresFixture.cs` — one Testcontainers `postgres:17-alpine` container per test run; migrations applied once with `MigrateAsync` into a template database `dit_template`; `Task<string> CreateDatabaseAsync()` returns a connection string for a fresh `CREATE DATABASE dit_<guid> TEMPLATE dit_template`. **Every test gets its own database.**
- Test: `tests/Trainer.Infrastructure.Tests/ContentImportServiceTests.cs`

**Interfaces:**
- Consumes: `IContentReader`, `IContentValidator`, `IContentRenderer` (Content).
- Produces:
  - Application-owned types (no Content types leak out): `record ImportIssue(string Path, string Code, string Message)`; `record ContentImportResult(ContentImportStatus Status, string Hash, int Imported, int Retired, IReadOnlyList<ImportIssue> Issues)`; `enum ContentImportStatus { Imported, Unchanged, Rejected }`; `interface IContentImportService { Task<ContentImportResult> ImportAsync(string contentRoot, CancellationToken ct); }`; `interface IContentCacheInvalidator { Task InvalidateAsync(CancellationToken ct); }`.
  - Tables (snake_case via EFCore.NamingConventions): `stacks(key pk)`, `stack_locales(stack_key, culture, title, description)`, `topics(stack_key, key; pk both)`, `topic_locales(stack_key, topic_key, culture, title, description)`, `questions(id char(26) pk, stack_key, topic_key, slug, level smallint, type smallint, grading smallint, tags text[], payload jsonb, verified date, dotnet_version text null, retired_at timestamptz null, unique(stack_key, slug))`, `question_locales(question_id, culture, title, prompt_html, answer_html, explanation_html, hint_html null, options jsonb, links jsonb; pk(question_id, culture))`, `redirects(stack_key, from_slug, to_slug; pk(stack_key, from_slug))`, `content_imports(id bigserial, hash, schema_version, render_version, imported_at, question_count, is_active bool)` — **hash not unique**; exactly one row has `is_active = true` (partial unique index).
  - `payload` / `options` / `links` JSON: mapped as `string` columns of type `jsonb`, (de)serialised with a source-generated `PayloadJsonContext`. `payload` = language-neutral data: code HTML, raw code, harness, option ids/correct/expect, `expect`, `acceptedOutputs`, `bugLineSets`, fixed code HTML, `fixedExpect`.
  - `IServiceCollection AddTrainerInfrastructure(this IServiceCollection services, string connectionString)` registers DbContext (pooled), Content services, import service, query services (later tasks).
- Import algorithm (all inside one transaction):
  1. Read content (outside the transaction).
  2. Begin transaction; `SELECT pg_advisory_xact_lock(<constant>)`.
  3. Load the active import row; if its (`hash`, `render_version`) equals (new hash, `ContentSchema.RenderVersion`) ⇒ rollback, `Unchanged`.
  4. Validate (read issues + validator); also reject `slug-changed` (an existing id with a new slug and no redirect old→new) and `slug-reused` (a new id takes a slug held by a retired question). Both checks are skipped when the incoming hash already exists in `content_imports` (a rollback to previously accepted content). Any issue ⇒ rollback, `Rejected` (issues mapped to `ImportIssue`).
  5. Upsert stacks/topics/locales; upsert questions by id; replace locales of every question in the set (render Markdown and code here); set `retired_at = now` for ids absent from the set and clear it for ids present; replace redirects; set previous active row inactive; insert new row active.
  6. Commit; then `IContentCacheInvalidator.InvalidateAsync`.

- [ ] **Step 1: Write failing tests**
  - `Import_ValidContent_WritesQuestionsAndLocales` — 6 questions, 12 locale rows, `verified` stored, prompt HTML has no `<script`.
  - `Import_SameContentTwice_SecondIsUnchanged` — `Unchanged`, one active row, no second row.
  - `Import_RollbackAtoBtoA_Succeeds` — final status `Imported`, active hash = A, three rows.
  - `Import_RenderVersionChange_Reimports` — set the active row's `render_version` to 0 directly in the database, import the same files ⇒ `Imported`.
  - `Import_RollbackOverSlugRename_Succeeds` — A, then B renaming a slug with a redirect, then A again ⇒ `Imported`.
  - `Import_InvalidContent_IsRejectedAndWritesNothing` — previous content and active row intact.
  - `Import_RemovedFile_RetiresQuestion`; `Import_ReaddedFile_ClearsRetired`.
  - `Import_SlugChangedWithoutRedirect_IsRejected` (`slug-changed`); `Import_NewIdReusingRetiredSlug_IsRejected` (`slug-reused`).
  - `Import_Success_InvalidatesCache` — fake invalidator called once; not for `Unchanged`/`Rejected`.
  - `Import_MalformedFile_IsRejectedWithoutThrowing` (bad date fixture).
  - `Import_ConcurrentCalls_OneImportsOtherUnchanged` — two service instances with separate DbContexts started together via a `Barrier`; results are one `Imported` and one `Unchanged`.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement entities, configurations, `dotnet ef migrations add InitialContent`, service. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(infrastructure): persist content and import it transactionally`.

### Task 9: Web host, routing, localisation, layout, themes and readiness

**Files:**
- Create: `src/Trainer.Web/Program.cs`, `Routing/*`, `Localization/**`, `Components/App.razor`, `Routes.razor`, `Layout/*`, `Pages/Home.razor` (placeholder: title + stats line, replaced in Task 10), `Pages/NotFound.razor`, `Caching/OutputCacheInvalidator.cs`, `Startup/*`, `wwwroot/css/tokens.css`, `site.css`, `wwwroot/js/theme-toggle.js`
- Create fixtures: `tests/Trainer.Web.Tests/TestContent/web/**` — the valid set plus: `en-only` question (no `ru` locale), question whose `en` title contains `</script>`, question `to-be-retired`, redirect `old-closure` → `closure-for-loop`.
- Test: `tests/Trainer.Web.Tests/TrainerWebFactory.cs`, `RoutingTests.cs`, `LayoutTests.cs`, `ThemeTokenContrastTests.cs`, `ReadinessTests.cs`

**Interfaces:**
- Consumes: `AddTrainerInfrastructure`, `IContentImportService`, `IContentCacheInvalidator`.
- Produces:
  - Config: `ConnectionStrings:Trainer`, `App:PublicBaseUrl`, `App:ContentRoot`, `App:ApplyMigrationsOnStartup` (Development only, see Global Constraints).
  - `TrainerWebFactory : WebApplicationFactory<Program>` in `tests/Trainer.TestSupport` (shared with E2E): a fresh database from `PostgresFixture.CreateDatabaseAsync()`, content copied from `TestContent/web` to a per-factory temp directory (tests that mutate content use their own factory instance); `Task WaitUntilReadyAsync()` polls `/health/ready` (timeout 30 s). All web tests call it before requests, except the readiness tests, which use a gated fake importer and observe the pending/failed states directly.
  - `ContentSnapshot` (immutable: `long Generation`, known stacks, known topic keys per stack) and `ContentSnapshotProvider` (singleton: `ContentSnapshot Current`, `Task RefreshAsync(CancellationToken)` loads from the database and increments the generation; called after every startup import and by `OutputCacheInvalidator`). Middleware and the cache policy read only the snapshot, so cache hits never touch the database.
  - Routes: Razor component pages use `@page "/{Culture}/{Stack}/..."` without custom constraints. `CultureAndStackMiddleware` (after `UseRouting`) reads `Culture`/`Stack` route values: unsupported culture or unknown stack (checked against `ContentSnapshot`) ⇒ 404. `IStackQueries` lives in Application (`Task<IReadOnlyDictionary<StackKey, IReadOnlyList<TopicKey>>> GetTaxonomyAsync(CancellationToken)`), implemented in Infrastructure, used only by `ContentSnapshotProvider.RefreshAsync`.
  - 404 handling (.NET 10): `Routes.razor` sets `<Router NotFoundPage="typeof(NotFound)">`; pages call `NavigationManager.NotFound()` for missing entities; `app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true)` renders the same page for middleware 404s and unmatched routes. The `NotFound` page is `noindex` and never cached.
  - `GET /` (`RootRedirectEndpoint`) → 302 to `/{culture}` from cookie `lang` if valid, else best `Accept-Language` match among `ru`, `en`, else `en`; `Vary: Accept-Language, Cookie`. `/{culture}` renders the home page for the default stack `dotnet` (`App:DefaultStack`).
  - Pipeline order: forwarded headers (Plan 3 config) → `SecurityHeadersMiddleware` → `UseRouting` → `CultureAndStackMiddleware` → `UseRequestLocalization` (single custom provider reading the `Culture` route value) → `UseOutputCache` → `UseAntiforgery` → `MapStaticAssets` → `MapRazorComponents<App>()` (no interactive render modes).
  - `App.razor`: no `blazor.web.js`. `<head>` starts with an inline theme script (before any stylesheet) that sets `document.documentElement.dataset.theme` from `localStorage['theme']` or `prefers-color-scheme`; the toggle button is wired by `theme-toggle.js` (deferred, plain JS).
  - `SecurityHeadersMiddleware`: `Content-Security-Policy: default-src 'self'; script-src 'self' 'sha256-<hash of the inline theme script>'; style-src 'self'; img-src 'self' https: data:; font-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`. The hash is computed at startup from the same string constant the layout renders.
  - Fonts self-hosted in `wwwroot/fonts` (JetBrains Mono, Inter; both OFL) — CSP forbids third-party font hosts.
  - `tokens.css`: tokens from `docs/design/mockups/` for `[data-theme=dark]` and `[data-theme=light]`, including `--on-ok`, `--warn`, `--bad`; breakpoint values 640/1024/1440 documented in a comment.
  - Language switcher: plain links to the same path and query in the other culture.
  - `ReadinessState` (singleton: `Pending`, `Ready`, `Failed`), `ContentStartupImporter : BackgroundService` creates a scope, runs `ImportAsync(App:ContentRoot)`, sets `Ready` on `Imported`/`Unchanged`, `Failed` on `Rejected`/exception (logged; previous content keeps serving). Health checks: `/health/live` always healthy; `/health/ready` healthy only in `Ready`.
  - `OutputCacheInvalidator : IContentCacheInvalidator` → `ContentSnapshotProvider.RefreshAsync` (new generation) then `IOutputCacheStore.EvictByTagAsync("content", ct)`; output cache services registered here (policy in Task 13).
  - `IStringLocalizer<SharedResource>` for all UI strings.
- [ ] **Step 1: Write failing tests**
  - `Root_WithRussianAcceptLanguage_RedirectsToRu` (302, `Location: /ru`), `Root_WithUnknownLanguage_RedirectsToEn`, `Root_CookieWinsOverAcceptLanguage`.
  - `UnsupportedCulture_Returns404WithNotFoundBody` (`/de`), `UnknownStack_Returns404WithNotFoundBody` (`/ru/java`), `UnmatchedRoute_Returns404WithNotFoundBody`.
  - `RuHome_HasHtmlLangRu_AndRussianStrings`.
  - `Resx_RuAndEn_HaveSameKeys`.
  - `Pages_HaveCspWithThemeScriptHash` (CSP header present; hash matches the inline script in the HTML).
  - `ThemeScript_PrecedesStylesheets` (in `<head>`, the inline script appears before the first `<link rel="stylesheet">`).
  - `ThemeTokens_MeetWcagAA`: parses `tokens.css` for both themes; ≥ 4.5 for `--text`/`--bg`, `--muted`/`--bg`, `--strong`/`--panel`, `--link`/`--bg`, `--on-ok`/`--ok`; ≥ 3.0 for `--ok`/`--bg`, `--bad`/`--bg`, `--warn`/`--bg`, `--accent`/`--side`. WCAG relative luminance formula.
  - `Readiness_PendingThenReady` (factory with a gated fake `IContentImportService`: `/health/ready` is 503 while gated, 200 after release), `Readiness_FailedImport_StaysUnready_AndPagesStillServe`.
  - `StartupMigrationFlag_OutsideDevelopment_Throws`.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(web): add host, culture routing, localisation, layout, themes and readiness`.

### Task 10: Home, about and catalog pages

**Files:**
- Create: `src/Trainer.Application/Catalog/*`, `Home/IHomeQueries.cs`; `src/Trainer.Infrastructure/Queries/CatalogQueries.cs`, `HomeQueries.cs`; `src/Trainer.Web/Catalog/CatalogQueryParser.cs`; `Components/Pages/Home.razor` (full), `About.razor`, `Catalog.razor`, shared components (filter panel, question row, level chip, pager)
- Test: `tests/Trainer.Web.Tests/CatalogQueryParserTests.cs`, `tests/Trainer.Infrastructure.Tests/CatalogQueriesTests.cs`, `tests/Trainer.Web.Tests/CatalogPageTests.cs`, `HomePageTests.cs`

**Interfaces:**
- Produces:
  - Application: `record CatalogQuery(CultureKey Culture, StackKey Stack, IReadOnlyList<Level> Levels, IReadOnlyList<TopicKey> Topics, IReadOnlyList<QuestionType> Types, int Page)`; `const int CatalogQuery.PageSize = 25`; `record CatalogPage(IReadOnlyList<CatalogRow> Rows, int Total, int Page, int PageCount, IReadOnlyList<FacetCount> LevelCounts, IReadOnlyList<FacetCount> TopicCounts, IReadOnlyList<FacetCount> TypeCounts)`; `record CatalogRow(string Slug, string Title, Level Level, TopicKey Topic, string TopicTitle, QuestionType Type, IReadOnlyList<string> Tags)`; `record FacetCount(string Key, string Title, int Count)`; `interface ICatalogQueries { Task<CatalogPage> GetAsync(CatalogQuery query, CancellationToken ct); }` — non-retired questions with a locale row for the culture; facet counts apply the other dimensions' filters; order by level, then title.
  - Web: `static class CatalogQueryParser { CatalogQuery Parse(CultureKey culture, StackKey stack, IQueryCollection query, IReadOnlySet<TopicKey> knownTopics); string ToCanonicalQueryString(CatalogQuery query); }` (known topics come from `ContentSnapshot`) — case-insensitive, deduplicated, unknown values dropped, values sorted (levels and types by enum order, topics ordinal), page < 1 ⇒ 1, page > 1000 ⇒ 1000; canonical string omits empty dimensions and `page=1`.
  - `interface IHomeQueries { Task<HomeStats> GetStatsAsync(CultureKey culture, StackKey stack, CancellationToken ct); }`; `record HomeStats(int QuestionCount, int TopicCount, int TypeCount, IReadOnlyList<FacetCount> Topics)`.
  - Routes: `/{Culture}` and `/{Culture}/{Stack}` → Home; `/{Culture}/about` → About (what the project is, licences, how to contribute, link to the repository once public); `/{Culture}/{Stack}/questions` → Catalog.
  - Filters are links, not forms (forms would add antiforgery tokens and cookies). Each filter is an `<a>` toggling one value in the canonical query; selected state is shown visually and with `aria-current="true"` on the link plus visually hidden text "(selected)". Search is Plan 2.
  - Home follows `docs/design/mockups/home-and-catalog.html`: hero with terminal sample, stats, levels, topics with counts, modes (cards for Practice/Mock/Review render as "coming soon" until Plan 2).
- [ ] **Step 1: Write failing tests**
  - `CatalogQueryParser_NormalisesJunkQuery`: `?level=MIDDLE&level=middle&topic=nope&utm_source=x` ⇒ levels `[Middle]`, topics `[]`, canonical `level=middle`.
  - `CatalogQueryParser_ClampsPage`.
  - `CatalogQueries_FiltersByLevelTopicType`, `CatalogQueries_FacetCountsIgnoreOwnDimension`, `CatalogQueries_ExcludesRetiredAndMissingLocale`, `CatalogQueries_Pages`.
  - `Catalog_RendersRowsWithLinksToQuestionPages` (`href="/ru/dotnet/questions/closure-for-loop"`).
  - `Catalog_AndHome_AndAbout_HaveNoFormsAndNoSetCookie`.
  - `Home_ShowsStatsAndAllFiveLevels`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat(web): add home, about and catalog pages`.

### Task 11: Question and topic pages, 404, redirects, retired

**Files:**
- Create: `src/Trainer.Application/Questions/*`, `Topics/ITopicQueries.cs`; `src/Trainer.Infrastructure/Queries/QuestionQueries.cs`, `TopicQueries.cs`; `src/Trainer.Web/Components/Pages/QuestionPage.razor`, `TopicPage.razor`, question view components
- Test: `tests/Trainer.Web.Tests/QuestionPageTests.cs`, `TopicPageTests.cs`

**Interfaces:**
- Produces:
  - `record QuestionPageModel(QuestionId Id, string Slug, StackKey Stack, TopicKey Topic, string TopicTitle, Level Level, QuestionType Type, string Title, string PromptHtml, string? HintHtml, string? CodeHtml, string? FixedCodeHtml, IReadOnlyList<OptionView> Options, string AnswerHtml, string ExplanationHtml, IReadOnlyList<LinkView> Links, IReadOnlyList<CultureKey> AvailableCultures, bool IsRetired, DateOnly Verified, string? DotnetVersion)`; `record OptionView(string Id, string TextHtml, bool Correct, string? ExplanationHtml)`; `record LinkView(string Url, string Title, string Lang, string Kind)`.
  - `interface IQuestionQueries { Task<QuestionPageModel?> GetAsync(CultureKey culture, StackKey stack, Slug slug, CancellationToken ct); Task<Slug?> GetRedirectAsync(StackKey stack, Slug from, CancellationToken ct); }`
  - `interface ITopicQueries { Task<TopicPageModel?> GetAsync(CultureKey culture, StackKey stack, TopicKey topic, CancellationToken ct); }`; `record TopicPageModel(TopicKey Key, string Title, string Description, IReadOnlyList<FacetCount> LevelCounts, IReadOnlyList<CatalogRow> Questions)` (first 25).
  - `QuestionPage` (`@page "/{Culture}/{Stack}/questions/{Slug}"`) receives `[CascadingParameter] HttpContext`. In `OnInitializedAsync`, **redirects are resolved first**: a redirect entry for the slug (old question missing or retired) ⇒ `Response.StatusCode = 301`, `Response.Headers.Location = /{culture}/{stack}/questions/{to}`, render nothing (valid because public pages are not streamed). Otherwise invalid slug or no model ⇒ `NavigationManager.NotFound()`. Same `NotFound()` approach for `TopicPage`.
  - Layout per `docs/design/mockups/question-screen-themes.html`: chips (level, topic, type), title, prompt, code panel, options listed without selection, `<details>` "Show answer" with correct answer, per-option explanations, explanation, fixed code (find-bug), refs with a language marker when link language ≠ page culture, verified date and .NET version. Retired ⇒ notice banner.
- [ ] **Step 1: Write failing tests**
  - `QuestionPage_RendersAnswerInsideDetails`.
  - `QuestionPage_FindBug_ShowsFixedCode`.
  - `QuestionPage_UnknownSlug_Returns404WithNotFoundBody`.
  - `QuestionPage_RedirectEntry_Returns301ToNewSlug` (`/ru/dotnet/questions/old-closure` → `Location: /ru/dotnet/questions/closure-for-loop`).
  - `RetiredQuestionWithRedirect_Returns301` (own factory: retire a question and add a redirect from its slug, import, expect 301).
  - `QuestionPage_MissingLocale_Returns404` (`/ru/.../en-only`).
  - `RetiredQuestion_IsReadableAndHiddenFromCatalog` (own factory: delete `to-be-retired.yaml` from its temp content, trigger import through `IContentImportService`, then assert page 200 with the retired notice and the catalog lacks it; the `noindex` assertion is in Task 12).
  - `QuestionPage_LinkLanguageMarkerShownWhenDifferent`.
  - `TopicPage_ShowsDescriptionAndCounts`; `TopicPage_UnknownTopic_Returns404`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat(web): add question and topic pages with 404, redirects and retired state`.

### Task 12: SEO metadata, structured data, sitemap and robots

**Files:**
- Create: `src/Trainer.Web/Components/Seo/SeoHead.razor`, `BreadcrumbJsonLd.razor`, `Endpoints/SitemapEndpoints.cs`; `src/Trainer.Application/Seo/ISitemapQueries.cs`; `src/Trainer.Infrastructure/Queries/SitemapQueries.cs`
- Modify: all public pages to render `SeoHead` and breadcrumbs
- Test: `tests/Trainer.Web.Tests/SeoTests.cs`

**Interfaces:**
- Produces:
  - `SeoHead` parameters: `string Title`, `string Description`, `string CanonicalPathAndQuery`, `IReadOnlyList<CultureKey> AvailableCultures`, `Func<CultureKey, string> PathForCulture`, `bool NoIndex`, `string OgType`. Absolute URLs from `App:PublicBaseUrl`. Rendered through `<HeadContent>`.
  - Rules: canonical = normalised URL (catalog uses `ToCanonicalQueryString`); hreflang for each culture the page exists in + `x-default` → the `en` URL if available, else the only one; `noindex` for retired questions, catalog with >1 level or >1 topic or any `type` filter or page > 1, and the 404 page.
  - `BreadcrumbJsonLd` serialises with `System.Text.Json` using cached options with `JavaScriptEncoder.Default` (escapes `<`, `>`), so `</script>` in titles cannot break out.
  - `interface ISitemapQueries { Task<IReadOnlyList<SitemapEntry>> GetAsync(CultureKey culture, CancellationToken ct); }`; `record SitemapEntry(string Path, IReadOnlyList<CultureKey> Alternates, DateOnly? LastModified)`.
  - `GET /sitemap.xml` (index → `/sitemap-ru.xml`, `/sitemap-en.xml`), each listing home, stack home, about, catalog, topic pages and live questions published in that culture with `xhtml:link` alternates; `GET /robots.txt` with `Sitemap:` line and `Disallow: /api/` plus the Plan 2 interactive paths for both cultures. Both endpoints use the `"public"` cache policy with tag `content` (Task 13 adds the policy; until then they are uncached).
- [ ] **Step 1: Write failing tests**
  - `QuestionPage_HasAbsoluteCanonicalAndHreflangPair`.
  - `Hreflang_ListsOnlyExistingCultures` (`en-only` question: only `en` + `x-default`).
  - `RetiredQuestion_IsNoindex`, `NotFoundPage_IsNoindex`.
  - `Catalog_MultiLevelFilter_IsNoindex`, `Catalog_SingleLevel_IsIndexable`.
  - `JsonLd_IsValidJsonAndEscapesScript` (`</script>` title question: extract the JSON-LD block, `JsonDocument.Parse` succeeds, raw HTML contains no second `</script>` inside it).
  - `Sitemap_ListsPublishedQuestionsPerCulture_ExcludesRetiredAndMissingLocale`.
  - `Robots_ReferencesSitemap`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat(web): add seo metadata, breadcrumbs, sitemaps and robots`.

### Task 13: Output cache policy and compression

**Files:**
- Create: `src/Trainer.Web/Caching/PublicPageCachePolicy.cs`, `AuthCookieNames.cs`
- Modify: `Program.cs`, public page components and sitemap/robots endpoints (policy `"public"`)
- Test: `tests/Trainer.Web.Tests/OutputCacheTests.cs`

**Interfaces:**
- Produces:
  - `static class AuthCookieNames { const string Session = ".dit.session"; }` (Plan 2 uses it for the auth cookie).
  - `PublicPageCachePolicy : IOutputCachePolicy`, registered with `AddPolicy("public", new PublicPageCachePolicy(...))`. As a custom policy it sets everything itself in `CacheRequestAsync`: `EnableOutputCaching`, `AllowCacheLookup`, `AllowCacheStorage`, `AllowLocking` = true only for GET/HEAD requests without `AuthCookieNames.Session`; `CacheVaryByRules.QueryKeys` left empty (raw query is not part of the key); `CacheVaryByRules.VaryByValues["q"]` = `CatalogQueryParser.ToCanonicalQueryString(parsed query)` for catalog pages (empty for others); `CacheVaryByRules.VaryByValues["gen"]` = `ContentSnapshot.Generation` captured at request start, so a response rendered from old content can only land under the old generation's key; `Tags.Add("content")`; `ResponseExpirationTimeSpan` 10 min. `ServeResponseAsync` disables storage for non-200 responses and responses with `Set-Cookie`. Store: `SizeLimit` 100 MB, `MaximumBodySize` 2 MB.
  - Response compression: Brotli + Gzip, `EnableForHttps = true`. Static assets through `MapStaticAssets()`.
- [ ] **Step 1: Write failing tests**
  - `AnonymousPublicPage_SecondRequestIsCacheHit` — second response has an `Age` header (output cache sets it on hits).
  - `AnonymousPublicPages_SetNoCookies` — `/ru`, `/ru/about`, `/ru/dotnet/questions`, a topic page, a question page, `/sitemap.xml`: no `Set-Cookie`.
  - `RequestWithSessionCookie_BypassesCache` — no `Age` on repeated requests.
  - `EquivalentCatalogQueries_ShareOneCacheEntry` — `?level=middle`, `?level=MIDDLE&level=middle&utm_source=x`: the second is a hit.
  - `Import_EvictsCachedPages` (own factory: edit a title in temp content, import, page shows the new title without `Age`).
  - `NotFound_IsNotCached`.
  - `CacheHit_ExecutesNoSql` (an EF Core command interceptor counter registered in the test factory stays unchanged on a hit).
  - `InFlightRenderDuringImport_DoesNotRecacheStaleContent`: a test-only query decorator gates the question query; start a request (captures generation N), import changed content (generation N+1), release the gate; the next request returns the new title.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat(web): cache public pages for anonymous users with normalised keys`.

### Task 14: Responsive layout checks with Playwright

**Files:**
- Create: `tests/Trainer.Web.E2E/` (`[Trait("Category","E2E")]`), `LayoutTests.cs`, `AppHostFixture.cs`
- Modify: `src/Trainer.Web/wwwroot/css/site.css` (fixes found by the tests), `.github/workflows/ci.yml` (job `e2e`: install Playwright Chromium, run E2E tests, upload `TestResults/screenshots/**` as an artifact)

**Interfaces:**
- Consumes: the running app. `AppHostFixture` uses `TrainerWebFactory` from `Trainer.TestSupport` with `UseKestrel()` and `StartServer()` (.NET 10) on a free port; content from `TestContent/web`.
- Produces: screenshots `TestResults/screenshots/{page}-{width}-{theme}.png`.

Matrix: pages (home, about, catalog `?level=middle`, topic page, question page with the long-line seed question, 404) × widths (375, 768, 1280, 1920) in the dark theme; light theme at 375 and 1280.

- [ ] **Step 1: Write failing tests**
  - `NoHorizontalOverflow` (matrix): `document.documentElement.scrollWidth <= window.innerWidth`; plus 320 px for home and question page.
  - `CodePanelScrollsInternally`: at 375, `pre.code` has `scrollWidth > clientWidth` and the page does not overflow.
  - `FilterDrawerWorksOnPhone`: at 375 the filter drawer (`<details>`-based, no JS framework) opens and shows level links.
  - `TouchTargetsAtLeast44px`: at 375 every `a, button, summary` in the header and filter panel has a bounding box ≥ 44×44.
  - `ThemeAppliedBeforeFirstPaint`: with `localStorage.theme = 'light'` set through `context.AddInitScriptAsync`, a `MutationObserver` init script records `data-theme` at the moment `<body>` is inserted; it equals `light`.
  - `ThemePersistsAcrossNavigation`: toggle theme, click a catalog link, the new page has the chosen theme.
- [ ] **Step 2:** Run → some FAIL. **Step 3:** Fix CSS until PASS (mobile-first; filters become a drawer below 640 px; sidebar collapsible 640–1023 px; content max width 1280 px from 1440 px). **Step 4:** PASS locally and in CI.
- [ ] **Step 5:** Commit `test(web): add responsive layout checks across widths and themes`.

### Task 15: Documentation

**Files:**
- Create: `docs/architecture.md`, `docs/content-guide.md`; Modify: `README.md`, `AGENTS.md` (commands and layout as they ended up)
- Test: `tests/Trainer.Content.Tests/ContentGuideExampleTests.cs`

**Interfaces:**
- Produces: `content-guide.md` contains one complete example question per type in fenced `yaml` blocks; the test extracts them and asserts each passes schema and semantic validation, so the guide cannot drift from the schema.

- [ ] **Step 1:** Test `ContentGuide_ExamplesAreValid` → FAIL.
- [ ] **Step 2:** Write `content-guide.md` (schema reference for every field per type, harness conventions, issue codes table from Tasks 3, 5, 6 and 8, quality bar: sources, verification date, both languages; how to run `validate`, `run-snippets`, `new-question`; editor setup: YAML language server with `# yaml-language-server: $schema=../../schema/question.schema.json`). `architecture.md` (layers and allowed references, render rules, content flow files → schema → validator → renderer → importer → DB → pages → cache, URL scheme, caching and security headers, readiness, migrations policy). `README.md` (what it is, a screenshot from the E2E artifacts, quick start: PostgreSQL in Docker, `dotnet run --project src/Trainer.Web` with `App:ApplyMigrationsOnStartup=true` in Development, licences).
- [ ] **Step 3:** Test → PASS. Full check: `dotnet build -warnaserror`, unit/integration tests, CLI `validate` → all green; Docker and E2E suites green in CI.
- [ ] **Step 4:** Commit `docs: add architecture and content guides`.

---

## Self-review notes

- Coverage for Plan 1: 5.1 (Task 2); 5.2 schema (Tasks 3, 5); 6 (Task 7); 7.1-7.5 (Tasks 3-8; scheduled link checker → Plan 3 per Roadmap); 8.1 (Task 1; Contracts/Web.Client → Plan 2); 8.2 public half (Tasks 9-11); 8.3 (Tasks 9-10); 8.4 (Tasks 12-13); 8.5 (Task 9); 8.6 content tables (Task 8); 9/9.1 (Tasks 9-14); 10 architecture/content guides (Task 15; deployment guide → Plan 3).
- Interface names checked across tasks: `ContentIssue` (Content) vs `ImportIssue` (Application); `CatalogQueryParser` lives in Web and is used by Tasks 10, 12, 13; `IStackQueries`, `ContentSnapshotProvider` and `TrainerWebFactory` defined in Task 9 (`PostgresFixture` in Task 8); `OutputCacheInvalidator` defined in Task 9, policy in Task 13; `AuthCookieNames` defined in Task 13.
