# Plan 4: Content Wave 1 via the Anthropic Batch API - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** About 400 bilingual (RU and EN) .NET interview questions across all 23
topics, generated, mechanically checked, fact-checked against fetched source
excerpts and revised once through paid Anthropic Message Batches, with every
submit done before 23:00 UTC on 2026-11-13; then merged in topic-group pull
requests after owner review.

**Architecture:** A stdlib-only Python driver (`tools/content-wave/wave.py`) keeps
one record per planned question in an atomically written state file, builds
batch task files for the `claude-api-work` MCP server, validates and applies
results idempotently, and gathers evidence per content hash: the existing CLI
(`validate`, `run-snippets`, `check-links`), a new `ContentCli dump` command
(the C# reader as the only YAML parser), RU/EN parity checks and fetched link
excerpts. Pipeline: outline -> cross-topic dedupe -> pilot -> generation ->
evidence -> fact-check round 1 -> repair -> evidence -> fact-check round 2 ->
finalize -> final gate -> PRs. Every step fails closed.

**Tech Stack:** Python 3.14 stdlib (`json`, `hashlib`, `html.parser`,
`urllib.request`, `subprocess`, `unittest`, `secrets`, `os.replace`),
`Trainer.ContentCli` (.NET 10, xUnit v3), Docker (snippet sandbox), MCP tools
`tasks_plan`, `tasks_submit`, `tasks_status`, `tasks_collect`, `review_budget`;
models `claude-opus-5-5` (outline, generation, repair) and `claude-sonnet-5-5`
(fact-check).

**Spec:** `docs/superpowers/specs/2026-11-11-dev-interview-trainer-design.md`
(sections 5.1, 5.2, 6, 7.2, 7.4, 7.6). Content rules: `docs/content-guide.md`
(field reference, rules per type, sandbox, quality bar, issue codes) and the
"Content rules" section of `AGENTS.md`.

**Revision:** v2 (2026-11-13), after Codex and Fable reviews
(`.review-plan4-codex.md`, `.review-plan4-fable.md`) and controller rulings:
pilot before the main batch; chunks of 2 (1 for lead/architect and code-heavy
topics) with a 32000 cap; fail-closed state machine with hash-bound evidence;
IDs allocated before generation; fetched link excerpts as fact-check evidence;
mechanical RU/EN parity; full-inventory duplicate control; budget ledger with a
UTC cutoff; per-question owner review sheet; fixture-based end-to-end tests.

## Global Constraints

- Branch base: `feat/plan-4-content` (from `main` = Plan 1). Plan 2 does not change the content schema; `content/schema-version` stays `1`. `check-links` is already on `main` (`CheckLinksCommand.cs`, registered in `Program.cs`).
- Wave size: 400 questions including the 6 seeds, so **394 new**. Level split (spec 7.6): junior 100, middle 140, senior 100, lead 32, architect 28; per-topic targets in Task 1. Seeds keep their owner verification (2026-11-11/12) and are not regenerated or re-checked.
- Wave-wide type mix (enforced, tolerance +-5): open 100, single 80, multi 50, output 70, find-bug 45, situation 55. Code-heavy topics (csharp, types-memory, collections-linq, clr-gc, exceptions, async, concurrency, performance): at least 35% `output` + `find-bug`. People topics (teamwork, behavioral, system-design): no `output` or `find-bug`, at least 50% `situation`.
- Acceptance: the wave ships if every topic keeps at least 85% of its target and has at least one junior and one middle question where targeted; the shortfall goes to wave 2 as stubs. The owner confirms this threshold in Task 1 before any spend.
- One question per file at `content/dotnet/{topic}/{slug}.yaml`, first line `# yaml-language-server: $schema=../../schema/question.schema.json`, LF, UTF-8 without BOM.
- `id`: ULID allocated by `wave.py` for every stub before generation, never written by a model, never changed. `slug`: lowercase kebab-case, at most 80 characters, unique in the stack, not equal to any slug of `content/dotnet/redirects.yaml` (`from` and `to` are `topic/slug`; compare the slug part).
- YAML is parsed only by the C# reader (`ContentCli dump`). `wave.py` edits files only by full replacement with model output plus two controlled line edits: inserting the `id` line after the schema comment and setting `meta.verified`; both are checked by `dump` afterwards.
- `ru` and `en` complete and equivalent. Natural technical Russian following the glossary in `prompts/generate.md` and the seeds as style reference. Plain hyphen `-` only: a U+2014 or U+2013 anywhere is a mechanical failure reported with its field and line, never rewritten silently. No time-relative wording ("latest", "current", "new"): always name the .NET or C# version.
- Code is not translated; comments in code are English.
- Every question has at least one link, target two: Microsoft Learn (`https://learn.microsoft.com/en-us/...`, `lang: en`), the C# specification, `dotnet/runtime` or other official repositories, RFCs, OWASP, then recognised authors (Stephen Toub, Eric Lippert, Stephen Cleary, Andrew Lock, Martin Fowler, Jon Skeet). No URL fragments unless certain. No invented APIs, overloads, flags or links.
- Executable code: `output` questions are C#, BCL only (no NuGet, no ASP.NET Core or EF Core types, no network), deterministic, short output; no timing, thread ids, hash codes, addresses or unordered-collection ordering; `harness: none` forbidden. `find-bug` on BCL code is runnable and states the observable failure; `find-bug` and code-bearing `single`/`multi` about ASP.NET Core or EF Core use `harness: none` with `meta.noRunReason` and no `fixedSource`, and are listed as "not executed".
- One defensible answer per type: `single` exactly one correct option; `multi` exactly one correct set; `output` exactly one real result; `find-bug` the accepted line sets; `open`/`situation` a reference answer that experts would accept as a strong answer.
- `meta.authors: [platonenkov]` (owner may change before merge). `meta.verified` = UTC date of the fact-check pass against fetched excerpts, set by `wave.py`; owner edits reset it to the owner's review date. `meta.dotnetVersion: "10"` whenever behaviour depends on a version, and both locales say so.
- Batch tasks: project `dev-interview-trainer`, `ref` = an exact commit SHA that `preflight` has checked, `maxOutputTokens` 32000 for generation, repair and fact-check (thinking counts against it), effort `high` unless the pilot shows truncation risk, one item <= 400,000 characters, item ids `[A-Za-z0-9_-]{1,64}`.
- Budget: day = UTC (server ledger); caps $50 per submit, $150 per day; about $146 left. Before every submit the ledger rule of Task 3 must hold. Every `tasks_submit` uses the owner's own confirmation text for that submit. No submit after 23:00 UTC 2026-11-13.
- Any usage error, cancellation, sandbox failure (exit 3), unparsed CLI line or non-zero exit that the driver does not classify stops the pipeline with a message; nothing is marked passed on missing evidence.
- Commits: Conventional Commits (`content:`, `chore:`, `docs:`, `feat:` for the CLI command). No AI attribution lines anywhere (AGENTS.md).

## Review Focus

1. **Hallucinated APIs and links.** A plausible member, overload or Learn URL that does not exist, or a real page about a neighbouring API. Pinned by: `links-fetch` excerpts in every fact-check item, two-strike dead-link rule, `unsure` blocking a pass (Task 6 test `test_verdict_pass_with_unsure_is_invalid`, Task 5 test `test_links_dead_needs_two_failures`), owner sheet column "link evidence".
2. **Wrong output expectations and bug lines.** `expect`, `acceptedOutputs`, an incorrect option's `expect`, or `bugLineSets` off by one. Pinned by: `run-snippets` evidence with actual results, numbered source in fact-check items with the verifier quoting each bug line (Task 6 test `test_verify_item_numbers_find_bug_lines`).
3. **RU/EN drift.** Different facts, numbers, code spans, options or hint strength. Pinned by: mechanical parity checks (Task 5 tests `test_parity_code_spans_differ`, `test_parity_numbers_differ`, `test_parity_cyrillic_in_en`), verifier check 5, owner sample.
4. **Level miscalibration.** Pinned by: level definitions in prompts, `apply` rejecting level or type drift from the stub (Task 5 test `test_apply_rejects_level_change`), controlled stub revision through repair only with recount (Task 6), owner reads all lead and architect questions.
5. **Duplicates.** Same fact twice, inside a topic or across neighbouring topics. Pinned by: full stack inventory in every outline item, a cross-topic dedupe item over all stubs, `check_outline` cluster comparison of `answerHinge` (Task 3 test `test_check_outline_cross_topic_hinge_overlap`), owner title scan per group.

---

## File Structure

| Path | Responsibility |
|---|---|
| `docs/content-wave-1/targets.json` | Per-topic level targets, policies, groups, acceptance threshold, cutoff. |
| `docs/content-wave-1/prompts/{outline,dedupe,generate,verify,repair}.md` | Batch instructions (shared files). |
| `docs/content-wave-1/outline/{topic}.json` | Accepted stubs per topic. |
| `docs/content-wave-1/state.json` | One record per stub (see Task 3), written atomically. |
| `docs/content-wave-1/ledger.json` | Owner confirmation texts, submits, estimates, actual spend, outstanding batches. |
| `docs/content-wave-1/tasks/*.json` | Every task file as submitted; its SHA-256 is in the ledger. |
| `docs/content-wave-1/results/<batchId>/` | Raw collected results copied verbatim, never edited. |
| `docs/content-wave-1/evidence/` | `mech-<hash>.json` per question, `links/<sha1(url)>.json` (status history, title, excerpt, fetch dates). |
| `docs/content-wave-1/quarantine/<stubId>.yaml.txt` | Unreadable model output, kept for repair. |
| `docs/content-wave-1/rejected/<topic>/<slug>.yaml.txt` | Questions rejected in round 2, reasons in state. |
| `docs/content-wave-1/review/<group>.md`, `<group>.csv` | Owner review sheets. |
| `tools/Trainer.ContentCli/Commands/DumpCommand.cs` | `dump [root]`: parsed questions as JSON. |
| `tests/Trainer.ContentCli.Tests/DumpCommandTests.cs` | Tests for `dump`. |
| `tools/content-wave/wave.py`, `test_wave.py`, `fixtures/` | Driver, tests, real CLI and batch fixtures. |

The `.yaml.txt` suffix keeps saved copies away from the content reader.

---

### Task 1: Targets, prompts and owner inputs

**Files:**
- Create: `docs/content-wave-1/targets.json`, `docs/content-wave-1/prompts/{outline,dedupe,generate,verify,repair}.md`, `docs/content-wave-1/ledger.json`

**Interfaces:**
- Produces: `targets.json` = `{"cutoffUtc": "2026-11-13T23:00:00Z", "budgetUsd": 146, "budgetFloorUsd": 5, "acceptance": {"minShare": 0.85}, "chunk": {"default": 2, "singleLevels": ["lead", "architect"], "singleTopics": ["csharp", "types-memory", "collections-linq", "clr-gc", "exceptions", "async", "concurrency", "performance"]}, "verifyChunk": 2, "topics": {"<key>": {"group", "levels": {...}, "codeHeavy", "peopleTopic", "neighbours": [keys]}}, "typeMix": {...}}`. Level counts are totals including seeds. No hard-coded "today": dates come from the UTC clock.

- [ ] **Step 1: Write `targets.json` with these values** (J, M, S, L, A; group):

| Topic | J | M | S | L | A | Group | Topic | J | M | S | L | A | Group |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| csharp | 9 | 8 | 6 | 1 | 0 | g1a | security | 4 | 7 | 5 | 1 | 1 | g3 |
| types-memory | 5 | 7 | 5 | 1 | 0 | g1a | ef-core | 5 | 8 | 5 | 1 | 1 | g3 |
| oop-solid | 7 | 6 | 3 | 0 | 0 | g1a | databases | 5 | 7 | 4 | 1 | 1 | g3 |
| collections-linq | 8 | 7 | 4 | 1 | 0 | g1b | testing | 5 | 6 | 3 | 1 | 1 | g4 |
| clr-gc | 4 | 7 | 6 | 1 | 0 | g1b | patterns | 5 | 6 | 3 | 1 | 1 | g4 |
| exceptions | 6 | 4 | 3 | 1 | 0 | g1b | architecture | 1 | 4 | 5 | 3 | 5 | g4 |
| async | 5 | 9 | 7 | 2 | 1 | g2 | distributed | 1 | 4 | 5 | 2 | 4 | g4 |
| concurrency | 4 | 8 | 6 | 1 | 1 | g2 | system-design | 0 | 3 | 4 | 2 | 5 | g4 |
| performance | 2 | 5 | 5 | 1 | 1 | g2 | devops | 4 | 5 | 3 | 1 | 1 | g5 |
| aspnet-core | 6 | 9 | 5 | 1 | 1 | g3 | ui-dotnet | 5 | 6 | 3 | 0 | 0 | g5 |
| web-api | 5 | 7 | 4 | 1 | 1 | g3 | teamwork | 1 | 3 | 3 | 5 | 2 | g5 |
| | | | | | | | behavioral | 3 | 4 | 3 | 3 | 1 | g5 |

Totals 100 / 140 / 100 / 32 / 28 = 400. Neighbour clusters: {async, concurrency, performance}, {csharp, types-memory, clr-gc, collections-linq}, {ef-core, databases}, {aspnet-core, web-api, security}, {architecture, system-design, distributed, patterns}, {teamwork, behavioral}, {testing, devops}.

- [ ] **Step 2: Write the prompts.**
  - `outline.md`: senior .NET interviewer planning one topic; input = this topic's "Covers" line, remaining counts per level, type policy, and the full stack inventory (every topic key with its Covers line, every existing slug with its EN title); cover every subtopic before repeating one; one testable fact or judgement per stub; no stub that another topic's Covers line owns; level definitions (junior: definitions and correct everyday use; middle: practical pitfalls and choosing between options; senior: internals, diagnosis, trade-offs under load; lead: technical decisions with team, process and risk; architect: system-wide trade-offs across services and years); `output` only where a deterministic BCL-only snippet exists; sources only pages the model is certain exist.
  - `dedupe.md`: given all stubs of all topics plus the existing inventory, list groups that hinge on the same fact, stubs in the wrong topic and level mismatches; propose one concrete replacement stub for each removed one, keeping topic and level counts.
  - `generate.md`: complete files exactly per `docs/content-guide.md`; keep stub slug, level, type and topic; **no `id` line**; schema comment first; raw YAML, no Markdown fences; every content-related Global Constraint copied verbatim; a short RU glossary (value type - тип значения, reference type - ссылочный тип, thread pool - пул потоков, garbage collector - сборщик мусора, race condition - состояние гонки; keep `middleware`, deadlock, code review as is); one defensible answer per type as defined above; any claim held with less than full certainty goes into `notes` with the stubId.
  - `verify.md`: independent adversarial reviewer; for each question check (1) every technical claim, option and option explanation; (2) `output`: predict the result before reading `expect`, then compare with the sandbox result in the item, which is ground truth; (3) every API, member, overload and flag exists in .NET 10 with that exact name, otherwise `blocker`; (4) each link against its fetched excerpt: the page exists and supports the specific claim; if the excerpt does not show support, `unsure` naming the claim; (5) `ru` and `en` state the same facts, options, numbers and hint strength; quote any unnatural Russian sentence with a rewrite; (6) level; (7) one defensible answer per type; (8) the hint does not give the answer; (9) no duplicate of the attached inventory; (10) `find-bug`: quote the text of every line in `bugLineSets` from the numbered source. Verdicts: `pass` only with no `blocker`, `major` or `unsure`; `fix` when a rewrite can solve every issue, including a level or type change; `reject` for a wrong premise, no defensible answer or a duplicate. In round 2 the item also contains round 1 issues and the repair rebuttals; judge rebuttals on the evidence.
  - `repair.md`: rewrite only the listed questions; fix every issue or rebut it in `notes` with evidence from the excerpts; keep stubId, slug and topic; change level or type only when a `fix` issue on that field requests it, and say so in `notes`; for `reject`, write a new question for the revised stub given in the item; same rules as `generate.md` (also a shared file).
- [ ] **Step 3: Get owner inputs in one message** and record them in `ledger.json`: the 85% acceptance threshold, the review-time estimate from Task 12, and one confirmation text per submit: outline "up to $10", dedupe "up to $2", pilot "up to $4", generation "up to $48" (each of up to three submits), fact-check round 1 "up to $45", repair "up to $49", fact-check round 2 "up to $25", retries "up to $12".
- [ ] **Step 4: Commit**

```bash
git add docs/content-wave-1
git commit -m "docs: add content wave 1 targets and batch prompts"
```

---

### Task 2: `ContentCli dump`

**Files:**
- Create: `tools/Trainer.ContentCli/Commands/DumpCommand.cs`, `tests/Trainer.ContentCli.Tests/DumpCommandTests.cs`
- Modify: `tools/Trainer.ContentCli/Program.cs` (register), `docs/content-guide.md` ("Verify locally": one line for `dump`)

**Interfaces:**
- Produces: `dump [root]` writes one JSON object to stdout: `{"hash": ContentSet.Hash, "questions": [{"path": "dotnet/<topic>/<slug>.yaml", "sha256": "<hex of file bytes>", "document": <QuestionDocument, camelCase>}], "readIssues": [{"path", "code", "message", "pointer"}]}`. Exit 0 also with read issues (they are data); `ExitCodes.UsageError` for a missing root. Uses `IContentReader` and a cached `JsonSerializerOptions` (camelCase, `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` so Cyrillic stays readable).

- [ ] **Step 1: Write the failing tests** in the style of `CliExitCodeTests` with `TempContentTree`:

```csharp
[Fact] public async Task Dump_ValidQuestion_EmitsDocumentAndSha()        // questions[0].document.slug == "printer"; sha256 is 64 hex chars
[Fact] public async Task Dump_BrokenYaml_ReportsReadIssueAndExitsZero()  // readIssues[0].code == "yaml-parse"; exit 0
[Fact] public async Task Dump_MissingRoot_ExitsUsageError()
```

- [ ] **Step 2: Run** `dotnet test tests/Trainer.ContentCli.Tests`. Expected: FAIL (type missing).
- [ ] **Step 3: Implement `internal sealed class DumpCommand(IContentReader reader, TextWriter output, TextWriter error) : ICliCommand`** (`Name` "dump", `Usage` "dump [root]").
- [ ] **Step 4: Run** `dotnet test tests/Trainer.ContentCli.Tests`, `dotnet build -warnaserror`, `dotnet format --verify-no-changes`. Expected: PASS, including `ContentGuideExampleTests`.
- [ ] **Step 5: Commit**

```bash
git add tools/Trainer.ContentCli tests/Trainer.ContentCli.Tests docs/content-guide.md
git commit -m "feat: add content cli dump command"
```

---

### Task 3: Driver core - state, ledger, preflight, outline

**Files:**
- Create: `tools/content-wave/wave.py`, `tools/content-wave/test_wave.py`

**Interfaces:**
- State record (`state.json`, key = stubId `<topic>-NN`): `{"stubId", "id", "topic", "slug", "level", "type", "stub", "status", "attempts": {"gen", "verify", "repair"}, "path", "contentHash", "yamlHistory": [{"stage", "batchId", "itemId", "sha256", "file"}], "evidence": {"mech": {"hash", "ok", "issues", "result"}, "parity": [...], "links": [...]}, "verdicts": [{"round", "hash", "verdict", "issues", "batchId", "source"}], "notes": [], "warnings": []}`.
- Statuses: `planned`, `generated`, `gen-failed`, `quarantined`, `verify-pass`, `needs-repair`, `unverified`, `repaired`, `owner-review`, `rejected`, `shortfall`, `final`. Terminal for `finalize`: `verify-pass`, `owner-review`, `rejected`, `shortfall`.
- Functions:
  - `new_ulid(now_ms: int | None = None, rand: bytes | None = None) -> str` - 48-bit ms time + 80 random bits, Crockford base32.
  - `content_hash(text: str) -> str` - SHA-256 of the LF text with the `meta.verified` value replaced by a fixed token, so setting the date keeps evidence valid.
  - `save_state(path: Path, state: dict) -> None` (write `<path>.tmp`, then `os.replace`) and `load_state(path: Path) -> dict`.
  - `ledger_check(ledger: dict, submit_estimate: float, forecast_remaining: float, budget: float, floor: float) -> tuple[bool, str]` - true iff `actual_spent + sum(outstanding estimates) + submit_estimate + forecast_remaining <= budget - floor`; `actual_spent` comes from the latest `review_budget` reading entered with `budget --record <usd>`.
  - `preflight(ref: str, server_repo: Path, files: list[str], worktree: Path) -> list[str]` - every file exists at `ref` in `server_repo` (`git -C <server_repo> cat-file -e <ref>:<file>`) and has no diff against `ref` in the worktree.
  - `check_task_file(task: dict, worktree: Path) -> list[str]` - id regex and length, unique ids, item text plus attached file sizes <= 400,000 characters, `maxOutputTokens` <= 32000, model in {`claude-opus-5-5`, `claude-sonnet-5-5`}, effort in {`medium`, `high`}.
  - `existing_inventory(dump: dict, redirects: list[dict]) -> dict[str, str]` - slug -> EN title; redirect slugs (slug part of `from` and `to`) -> "(redirect)".
  - `check_outline(outlines: dict[str, list[dict]], remaining: dict, inventory: dict, targets: dict) -> list[str]` - failures: slug invalid, taken or duplicated; level counts differ from remaining; type mix outside +-5; code-heavy and people-topic policies; `answerHinge` + `subtopic` word-set Jaccard >= 0.5 between two stubs of one topic or one neighbour cluster.
  - `allocate(outlines: dict, state: dict) -> dict` - adds a `planned` record with a ULID for every stub without one; never changes an existing id.
- Subcommands: `budget [--record <usd>]`, `preflight --task <file>`, `outline-tasks --ref`, `outline-apply --batch <dir>`, `dedupe-tasks --ref`, `outline-check`, `allocate`. Every `*-tasks` subcommand runs `check_task_file` and `preflight`, refuses to write on problems, and records the task file SHA-256 in the ledger.

- [ ] **Step 1: Write the failing tests**

```python
def test_new_ulid_shape(self)                       # 26 chars, ^[0-7][0-9A-HJKMNP-TV-Z]{25}$, two calls differ
def test_content_hash_ignores_verified_date(self)   # equal for 2026-11-12 vs 2026-11-13; differs when answer text changes
def test_save_state_atomic(self)                    # failing serializer leaves the old file intact and no .tmp behind
def test_ledger_check_blocks_over_budget(self)      # spent 60, outstanding 48, submit 40, forecast 0, budget 146, floor 5 -> False
def test_check_task_file_rejects_long_item(self)    # 400,001 characters -> problem
def test_check_outline_slug_taken_by_redirect(self) # redirect from "async/old-slug" blocks stub slug "old-slug"
def test_check_outline_level_count_mismatch(self)   # "csharp junior: expected 9, got 8"
def test_check_outline_people_topic_situation_share(self)  # teamwork with 40% situation -> problem
def test_check_outline_cross_topic_hinge_overlap(self)     # async and concurrency stubs with the same answerHinge -> problem naming both
def test_allocate_is_stable(self)                   # second run keeps every id
def test_preflight_detects_uncommitted_file(self)   # temp git repo with a modified file -> problem
```

- [ ] **Step 2: Run** `python -m unittest discover -s tools/content-wave -v`. Expected: FAIL.
- [ ] **Step 3: Implement.** Outline task: Opus, effort high, cap 32000, `sharedFiles` = `docs/content-wave-1/prompts/outline.md`, `docs/content-guide.md`, `content/dotnet/topics.yaml`; one item `outline-<topic>` per topic with remaining > 0, `text` = remaining counts, policies and the full inventory; `files` = `["content/dotnet/topics.yaml"]` plus the topic's existing questions. Outline `outputSchema`: v1 shape (`topic`, `stubs[]` with `slug`, `level` enum, `type` enum, `subtopic`, `angle`, `answerHinge`, `sources[]`, all required, `additionalProperties: false`) with `"minItems": 1` on `stubs`. Dedupe task: one item `dedupe-all`, `text` = every stub (stubId, topic, level, type, angle, answerHinge) plus the existing inventory; `outputSchema` `{"findings": [{"stubIds": [string], "kind": enum ["duplicate", "wrong-topic", "wrong-level"], "replacement": <stub object or null>, "reason": string}]}`.
- [ ] **Step 4: Run** the tests. Expected: PASS.
- [ ] **Step 5: Commit**

```bash
git add tools/content-wave
git commit -m "chore: add content wave driver core and outline steps"
```

---

### Task 4: Outline, dedupe and ID allocation

**Files:** `docs/content-wave-1/tasks/{outline,dedupe}.json`, `outline/*.json`, `state.json`, `ledger.json`, `tools/content-wave/fixtures/batch/`

- [ ] **Step 1:** `git rev-parse HEAD`, `python tools/content-wave/wave.py outline-tasks --ref <sha>`, commit the task file, `tasks_plan`. This also proves that the server (repository `E:/GIT/dev-interview-trainer`, sharing objects with this worktree) accepts a SHA of this branch; if not, use the local branch name and record that in the ledger.
- [ ] **Step 2:** `budget`, submit with the owner's text, collect, copy raw results to `results/<batchId>/`, save one result file and `index.json` as fixtures in `tools/content-wave/fixtures/batch/` (the real envelope for `validate_result`).
- [ ] **Step 3:** `outline-apply`, `dedupe-tasks`, plan, submit, collect; apply the findings by editing `outline/<topic>.json`; `outline-check` until exit 0.
- [ ] **Step 4:** Show the owner the outline table (stubId, level, type, angle, answerHinge) for lead, architect and people-topic stubs; proceed on a go or after 30 minutes without objection.
- [ ] **Step 5:** `python tools/content-wave/wave.py allocate`. Expected: 394 `planned` records with ULIDs.
- [ ] **Step 6: Commit** `docs: add content wave 1 outline and allocate question ids`.

Fallback: if the outline batch has not ended 60 minutes after submit, write the missing topics' stubs in-session with the same prompt and schema.

---

### Task 5: Driver - generation, apply and evidence

**Files:**
- Modify: `tools/content-wave/wave.py`, `test_wave.py`; create `tools/content-wave/fixtures/cli/*`

**Interfaces:**
- Generation `outputSchema`: `{"questions": [{"stubId": string, "yaml": string}] (minItems 1), "notes": [{"stubId": string, "text": string}]}`; all required, `additionalProperties: false`. The path is derived from the stub.
- `gen-tasks --ref <sha> [--only <stubIds>] [--max-estimate <usd>]` -> items `gen-<topic>-<k>`: 1 stub per item for code-heavy topics and lead/architect stubs, otherwise 2 with different types where possible; `text` = the stubs as JSON; `files` = `outline/<topic>.json` plus existing questions of the topic and its neighbours; `--max-estimate` splits into several task files by group.
- `validate_result(result: dict, item: dict, stage: str) -> tuple[list[dict], list[str]]` - stage schema checked locally; the envelope shape comes from the Task 4 fixture (no guessing); each expected stubId exactly once, no unknown stubId; one surrounding Markdown fence stripped with a warning.
- `apply --batch <dir> --stage gen|repair` - idempotent (a raw result whose SHA-256 is already in `yamlHistory` is skipped); writes `content/dotnet/<topic>/<slug>.yaml` = schema comment + `id: <state id>` + model YAML; an `id` line in model YAML is a failure. Item outcome from `index.json` / stop reason: `max_tokens`, malformed JSON, refusal, error, expiry -> every stub of the item becomes `gen-failed`. `retry-plan` splits a failed 2-stub item into two 1-stub items; one retry per stub; a 1-stub item failing twice -> `shortfall`.
- `mech` - (1) `dump`; read issues -> file copied to `quarantine/<stubId>.yaml.txt`, removed from `content/`, status `quarantined`; (2) per question: stub match (`topic`, `slug`, `level`, `type`; in repair a `level`/`type` change only when a verdict issue on that field requested it), dash scan over every string field with field path and line, `parity`; (3) `validate`, `run-snippets`, `check-links`, parsed by `parse_issue_lines` / `parse_link_lines`; an unparsed line or an exit code other than 0 or 1 stops the run (exit 3 = fix Docker, re-run); (4) `evidence/mech-<hash>.json` per question: actual snippet result, executed or skipped, compiler diagnostics, failures. `ok` = no failure for that content hash.
- `parity(doc: dict) -> list[dict]` - per field pair (`title`, `prompt`, `hint`, `answer`, `explanation`, each option text and option explanation): inline-code span multisets equal; numbers outside code equal; any Cyrillic in `en` fails; `ru` title, prompt, answer and explanation contain Cyrillic; a Latin-letter word in `ru` outside code spans and not in the glossary allow-list is a warning.
- `links-fetch` - every distinct URL of every generated question: GET with a generic user agent, 20 s timeout, 4 in parallel, redirects followed; store status history with UTC timestamps, final URL, `<title>`, and up to 4,000 characters of visible text centred on the question's key terms (EN inline-code spans and title words). `dead` only after two failed fetches at least 30 minutes apart (or one failed fetch plus a later `check-links` dead); 401/403/429/timeouts are `unknown` and go to the owner list; a redirect to a different page or a Learn landing page is a warning.
- `parse_issue_lines(output: str) -> dict[str, list[dict]]` (`path: code: message [pointer]`, message may span lines) and `parse_link_lines(output: str) -> dict[str, list[dict]]` (`dead|unknown: <url> (<detail>) cited by <p1>, <p2>`); paths normalised to `content/dotnet/...` with forward slashes.

- [ ] **Step 1: Capture real fixtures.** Copy `content/` to a temp root; add files with a snippet mismatch whose actual output has two lines, a `yaml-parse` error, a `link-scheme` error and a link to `https://example.invalid/x`; run `validate`, `run-snippets`, `check-links --content <temp>` and `dump`; save stdout, stderr and exit codes under `tools/content-wave/fixtures/cli/`.
- [ ] **Step 2: Write the failing tests**

```python
def test_parse_issue_lines_multiline_snippet_mismatch(self)  # real fixture -> one issue carrying the full two-line actual output
def test_parse_issue_lines_unparsed_line_raises(self)
def test_parse_link_lines_dead_cited_by_two(self)
def test_validate_result_missing_stub(self)
def test_validate_result_duplicate_stub(self)
def test_validate_result_strips_fence_with_warning(self)
def test_apply_is_idempotent_on_replay(self)
def test_apply_rejects_model_id_line(self)
def test_apply_truncated_item_marks_gen_failed(self)       # index.json stop reason max_tokens
def test_apply_rejects_level_change(self)
def test_retry_plan_splits_once(self)                       # 2-stub -> two 1-stub; second failure -> shortfall
def test_parity_code_spans_differ(self)
def test_parity_numbers_differ(self)
def test_parity_cyrillic_in_en(self)
def test_dash_scan_reports_field_and_line(self)             # U+2014 in code.source line 3 -> failure "code.source:3"
def test_links_dead_needs_two_failures(self)
def test_mech_quarantine_keeps_text_and_path(self)
def test_windows_paths_normalised(self)                     # results dir and index.json with backslashes
```

- [ ] **Step 3: Run** tests. Expected: FAIL.
- [ ] **Step 4: Implement.**
- [ ] **Step 5: Run** tests. Expected: PASS.
- [ ] **Step 6: Commit** `chore: add generation, apply and evidence steps to the content wave driver`.

---

### Task 6: Driver - fact-check, repair, finalize, final gate, report

**Files:**
- Modify: `tools/content-wave/wave.py`, `test_wave.py`; create `tools/content-wave/fixtures/e2e/`

**Interfaces:**
- `verify-tasks --ref <sha> --round 1|2 [--only <stubIds>] [--max-estimate <usd>]` - round 1 takes `generated` (including questions with mechanical failures, which carry their evidence); round 2 takes `repaired` and `unverified`. Sonnet 5.5, effort high, cap 32000, two questions of one topic per item, id `verify-r<round>-<topic>-<k>`. `files` = the question files and `outline/<topic>.json`; `text` per question: stubId, path, stub, generator notes, mechanical evidence (actual result, executed or skipped, failures, parity), numbered `code.source` for `find-bug`, every link's status, title, excerpt and fetch date, and in round 2 the round 1 issues and repair notes. The ledger records `{stubId: contentHash}` per item.
- Verdict `outputSchema`: `{"verdicts": [{"stubId": string, "verdict": enum ["pass", "fix", "reject"], "issues": [{"severity": enum ["blocker", "major", "minor", "unsure"], "field": string, "claim": string, "problem": string, "suggestedFix": string}]}] (minItems 1)}`.
- `verdicts --batch <dir> --round 1|2` - exactly one verdict per expected stubId; a verdict whose recorded hash differs from the current file hash is stale and ignored; a `pass` with a `blocker`, `major` or `unsure` issue is invalid; invalid, missing or duplicate verdict -> `unverified` (one retry via `verify-tasks --only`; then an explicit `resolve`). Round 1: `pass` with mechanical `ok` -> `verify-pass`; `pass` without `ok`, `fix`, `reject` -> `needs-repair`. Round 2: `pass` with `ok` -> `verify-pass`; a non-pass whose issues are all `unsure` or rebutted in repair notes -> `owner-review`; otherwise `rejected` (file saved to `rejected/`, removed from `content/`). `verify-pass` sets `meta.verified` to the UTC date.
- `repair-tasks --ref <sha> [--max-estimate <usd>]` - takes `needs-repair`, `quarantined`, and `gen-failed` with a retry left; chunking and cap as generation; `text` per question: stubId, stub (for `reject`: the revised stub from the issue's suggested fix, after which `outline-check` counts are re-run), current YAML from `yamlHistory` or quarantine, mechanical evidence, verifier issues, excerpts. `apply --stage repair` writes to the canonical path of the state record with the same id and sets `repaired`.
- `resolve --stub <id> --as owner-review|shortfall --reason <text>` - the only manual way to close an unresolved record.
- `finalize` - exit 1 listing the records while any record is not terminal; otherwise checks acceptance (85% per topic, junior and middle present, type policies on survivors) and prints the shortfall. Changes no files.
- `final-gate` - after `finalize`: `dump`, `validate`, `run-snippets`, `check-links` on the whole bank; asserts for every non-seed file under `content/dotnet` that it is `verify-pass`, its content hash equals the hash of its pass verdict and of `ok` mechanical evidence from this run, and none of its links is `dead`; marks those `final`; exit 1 on any mismatch.
- `report --group <g>` -> `review/<g>.md` and `review/<g>.csv`: one row per question ordered by risk (lead/architect, situation, owner-review, repaired, not executed, link unknown, parity warning): path, level, type, executed, actual result, link status and title, verdict rounds, warnings, `owner_read`; the seeded 10% sample (seed = group name) of the other rows; totals by topic x level and type against targets; shortfall and rejected lists.
- `owner-check` - every file whose content hash differs from its `final` hash: re-run mechanical evidence, mark `owner-edited`, set `meta.verified` to the UTC date; exit 1 on any mechanical failure.

- [ ] **Step 1: Write the failing tests**

```python
def test_verdicts_one_per_expected_stub(self)        # missing -> unverified; duplicate -> unverified
def test_verdict_stale_hash_ignored(self)
def test_verdict_pass_with_major_is_invalid(self)
def test_verdict_pass_with_unsure_is_invalid(self)
def test_verify_includes_mech_fail_questions(self)   # round 1 item contains the question with a snippet mismatch and its actual output
def test_verify_item_numbers_find_bug_lines(self)
def test_repair_restores_canonical_path_by_id(self)  # quarantined stub -> file at content/dotnet/<topic>/<slug>.yaml with the allocated id
def test_round2_rebutted_only_goes_owner_review(self)
def test_finalize_refuses_unresolved(self)
def test_final_gate_detects_edit_after_pass(self)
def test_report_sample_is_deterministic(self)
def test_e2e_dry_run(self)
```

`test_e2e_dry_run` runs the whole pipeline in a temp git repository with the CLI replaced by fixture outputs: outline result -> allocate -> generation results (one truncated item, one duplicate stub, one replayed batch) -> mech -> verify round 1 (one missing verdict, one stale hash) -> retry -> repair -> verify round 2 -> finalize -> final-gate; it asserts every stub's final status and that no record left the pipeline without a terminal status.

- [ ] **Step 2: Run** tests. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** tests. Expected: PASS.
- [ ] **Step 5: Commit** `chore: add fact-check, repair and final gate steps to the content wave driver`.

Order for speed: Task 4 starts as soon as Task 3 is committed; Tasks 5 and 6 are written while the outline and dedupe batches run.

---

### Task 7: Pilot

**Files:** `docs/content-wave-1/tasks/pilot-*.json`, pilot question files

- [ ] **Step 1:** `gen-tasks --only` with five items: one code-heavy `output` (1 stub), one code-heavy `find-bug` (1 stub), one ef-core or aspnet-core pair with a `harness: none` question, one junior pair (`single` + `multi`), one lead or architect `situation` (1 stub). Plan, `budget`, submit, collect, `apply`, `mech`, `links-fetch`, commit, `verify-tasks --round 1 --only <pilot stubs>`, submit, collect, `verdicts`.
- [ ] **Step 2:** Record per item output tokens, stop reason and actual cost (`review_budget` before and after), and read all pilot questions in both locales. Rule: if any item stopped on `max_tokens`, or a 1-stub item used more than 14,000 output tokens, use 1 stub per item everywhere and lower generation effort to `medium` for junior and middle stubs only; if every 2-stub item used at most 20,000, keep the defaults. Fix the prompts for every systematic defect seen, commit, and re-run the pilot items only after a material prompt change.
- [ ] **Step 3:** `forecast_remaining` = measured mean cost per stub x stubs left x 1.3 for generation, the same for fact-check, and for repair with the pilot failure share (at least 40%); record it in the ledger. If `ledger_check` fails for the full wave, mark stubs `shortfall` in this order until it holds: a second architect stub in a topic, then senior stubs above the topic's acceptance floor; never below 85% without asking the owner.

---

### Task 8: Main generation

- [ ] **Step 1:** `gen-tasks --max-estimate 48` -> `gen-a`, `gen-b`, `gen-c` (split by group). For each in order: `preflight`, `tasks_plan`, `budget`, submit, collect, copy raw results, `apply`, commit. Submit the next one before the previous is collected only if `ledger_check` holds with both outstanding.
- [ ] **Step 2:** After each collect, `retry-plan`; submit the split retries with the next submit when the ledger allows.
- [ ] **Step 3:** After the last collect: `mech`, `links-fetch` (first pass), commit `content: add wave 1 drafts with evidence`.

---

### Task 9: Fact-check round 1

- [ ] **Step 1:** `verify-tasks --round 1 --max-estimate 45`; preflight, plan, `budget`, submit. A verify submit for an already collected generation batch may start while later generation batches run, when `ledger_check` holds.
- [ ] **Step 2:** Collect, copy raw results, `verdicts --round 1`; one `--only` retry for `unverified`; commit `content: record wave 1 fact-check round 1`.
- [ ] **Step 3:** Spot-check five `fix` verdicts against their excerpts; note clear false alarms for the owner (the repair prompt can rebut them).

Why Sonnet 5.5 and not Opus: an Opus checker tends to confirm an API or URL that Opus itself invented; a different model with a different prompt disagrees in different places, and its weaker recall mostly produces false alarms that cost a repair, not wrong content. Agreement of two Claude models is not verification on its own: the deciding evidence is the sandbox result and the fetched excerpt, and questions where neither applies (`harness: none`, many `open` and `situation`) are flagged in the owner sheet.

---

### Task 10: Repair and fact-check round 2

- [ ] **Step 1:** `links-fetch` second pass (at least 30 minutes after the first), `repair-tasks --max-estimate 49`, preflight, plan, `budget`, submit, collect, `apply --stage repair`, `mech`, commit `content: apply wave 1 repair round`.
- [ ] **Step 2:** `verify-tasks --round 2`, submit, collect, `verdicts --round 2`, one retry for `unverified`; `resolve` what remains with a reason; commit `content: record wave 1 fact-check round 2`.

---

### Task 11: Finalize and final gate

- [ ] **Step 1:** `python tools/content-wave/wave.py finalize`. Expected: exit 0 and the acceptance summary; on failed acceptance stop and ask the owner.
- [ ] **Step 2:** `python tools/content-wave/wave.py final-gate`. Expected: exit 0; `validate` prints "Content is valid", `run-snippets` "All runnable snippets match their declared results.", `check-links` exit 0.
- [ ] **Step 3:** `dotnet build -warnaserror`; `dotnet test --filter-not-trait "Category=E2E" --filter-not-trait "Category=Docker" --ignore-exit-code 8`; `dotnet test --filter-trait "Category=Docker" --ignore-exit-code 8`. Expected: green. Record the final `review_budget` reading.
- [ ] **Step 4: Commit** `content: finalize wave 1`.

---

### Task 12: Pull requests and owner review

Spending ends with Task 10; this task does not block it.

- [ ] **Step 1:** Tooling PR from `feat/plan-4-content` (plan, `tools/content-wave`, `dump`, prompts, targets) into `main`; merge it first.
- [ ] **Step 2:** For each group in review order g2, g1a, g1b, g3, g4, g5: branch `content/wave-1-<group>` from `main`, `git checkout content/wave-1-work -- content/dotnet/<topic> ...`, run `validate` and `run-snippets`, commit `content: add wave 1 <group> questions`, push, open a PR with `review/<group>.md` as the body. Titles: g1a C# and types, g1b collections, CLR and exceptions, g2 async, concurrency and performance, g3 web and data, g4 design and architecture, g5 delivery and people.
- [ ] **Step 3:** The PR body says exactly: "Model-verified against fetched source excerpts on <date>; mechanically checked (schema, sandbox execution where runnable, link status); owner sample-reviewed.", followed by the not-executed questions, `unknown` links and `owner-review` items.
- [ ] **Step 4:** The owner reads every lead, architect and `situation` question in both locales, the seeded 10% sample of the rest, and every row flagged `owner-review`, `unknown` link or parity warning, and ticks `owner_read` in the CSV. Estimate: about 90 lead/architect/situation reads + 30 sample reads + 30 flagged rows = about 150 reads at 5-6 minutes, 12-15 hours over several days. Evidence is in each row; the owner re-runs nothing.
- [ ] **Step 5:** Owner edits are made on the group branch in-session (no API spend), then `python tools/content-wave/wave.py owner-check`, `validate` and `run-snippets`; merge when green.
- [ ] **Step 6:** After the last content PR, an audit PR from the work branch with `docs/content-wave-1/` only (state, ledger, tasks, raw results, evidence, quarantine, rejected, review sheets). Shortfall stubs are the wave 2 input.

---

## Schedule, cutoff and fallbacks

All submits before 23:00 UTC 2026-11-13; `tasks_status` once per 30 minutes, not in a loop. Batches are charged when processed: a batch submitted before the cutoff but processed after the credits expire may fail; its items then become `gen-failed` or `unverified` and follow the fallback, never counted as passed.

| Stage | Trigger | Fallback |
|---|---|---|
| Outline / dedupe | Not ended 60 minutes after submit | Write the missing stubs or the dedupe pass in-session. |
| Pilot | Not ended in 60 minutes | Proceed with default chunking (1 and 2), effort high. |
| Generation | Not submitted by 23:00 UTC | Remaining stubs become `shortfall` (wave 2). |
| Generation | Submitted, not ended by 23:00 UTC | Collect tomorrow; its questions go through the no-API checks below. |
| Fact-check or repair | Not submitted or not ended | Questions stay `generated`, `needs-repair` or `unverified` and are not published. Later checks run in-session or through the Codex subscription with the same prompts and evidence, recorded with `source` in the verdict, and the PR text names that source. |

## Cost estimate

Batch prices (50% of list): Opus 5.5 $2 input / $10 output per MTok, Sonnet 5.5 $1 / $5. Estimate method of `tasks_plan`: input at 2 characters per token, every item at its full 32,000-token cap, no cache credit. Shared block about 70,000 characters (35k tokens). Chunking gives 146 one-stub code-heavy items + 48 one-stub lead/architect items in other topics + 101 two-stub items = 295 generation items (5 of them the pilot); fact-check at 2 questions per item = 197 items.

| Step | Model | Items | Input tokens per item | $ per item (input + output at cap) | Estimate at cap | Expected actual |
|---|---|---|---|---|---|---|
| Outline | Opus | 23 | 40k | 0.080 + 0.320 = 0.400 | $9.2 | $3.2 |
| Dedupe | Opus | 1 | 140k | 0.280 + 0.320 = 0.600 | $0.6 | $0.4 |
| Pilot generation | Opus | 5 | 38k | 0.076 + 0.320 = 0.396 | $2.0 | $0.9 |
| Pilot fact-check | Sonnet | 4 | 52k | 0.052 + 0.160 = 0.212 | $0.9 | $0.3 |
| Generation | Opus | 290 | 38k | 0.396 | $114.8 (3 submits <= $48) | $42.1 |
| Fact-check round 1 | Sonnet | 197 | 52k | 0.212 | $41.8 | $14.8 |
| Repair (40% of questions) | Opus | 118 | 45k | 0.090 + 0.320 = 0.410 | $48.4 | $20.0 |
| Fact-check round 2 | Sonnet | 79 | 60k | 0.060 + 0.160 = 0.220 | $17.4 | $6.2 |
| Retries (10% generation, 10% fact-check) | both | 30 + 20 | as above | as above | $11.9 + $4.2 | $5.0 |
| **Total** | | | | | **$251.2** | **$92.9** |

Expected actual assumes about 8,000 output tokens per one-stub generation item and 14,000 per two-stub item (answer plus thinking), 8,000 per fact-check item, and half of the shared block read from cache. The sum of estimates at cap exceeds $146, so not every stage can be reserved at cap at once: the server reserves each submit's estimate until it is collected, and the ledger rule (actual spent + outstanding estimates + this submit + pilot-based forecast of remaining stages <= $141) makes the generation submits run one after another, or two at a time only when actual spend leaves room. If the pilot's cost per stub exceeds 1.5 times the assumption, Task 7 Step 3 shrinks the wave before any main submit instead of running out of budget in the repair round.

## Self-review notes

- Spec 7.6 split, all topics and types: Task 1 targets, Task 3 `check_outline`, Task 11 `finalize` on survivors. Spec 7.4 validation and execution: Tasks 5 and 11. Owner review in topic batches: Task 12.
- Review findings map to tasks: pilot and truncation (Tasks 5, 7); recovery and IDs (Tasks 3, 5, 6); fail-closed gates and hash-bound verdicts (Task 6); ledger and cutoff (Task 3, schedule); result validation and idempotence (Task 5); ref preflight and PR provenance (Tasks 3, 12); source evidence (Task 5 `links-fetch`); execution evidence and bug lines (Tasks 5, 6); verifier and repair consistency (Task 1 prompts, Task 6); target enforcement (Task 6 `finalize`); duplicates and parity (Tasks 3, 4, 5); YAML through the C# reader (Task 2); tests on real fixtures and an end-to-end dry run (Tasks 5, 6).
