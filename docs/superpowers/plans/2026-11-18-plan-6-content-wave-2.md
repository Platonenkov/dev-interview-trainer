# Plan 6: Content Wave 2 - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** About 400 new bilingual (RU and EN) .NET interview questions that even out the level grid of every topic and fill four new topics (Blazor, .NET MAUI, cloud and containers, gRPC and real-time), produced by the wave 1 pipeline and merged in topic-group pull requests.

**Architecture:** The wave 1 driver `tools/content-wave/wave.py` and its process (Plan 4) are reused: outline -> cross-topic dedupe -> pilot -> generation -> evidence -> fact-check round 1 -> repair -> evidence -> fact-check round 2 -> finalize -> final gate -> PRs, every step fail-closed. Wave 1 ran against six seed questions; wave 2 runs against a bank of 401, which breaks several driver assumptions (wave paths, cutoff, budget envelope, generation context size, acceptance). Two preparatory tasks fix those and add four topics before any spend.

**Tech Stack:** Python 3.14 stdlib driver, `Trainer.ContentCli` (.NET 10), Docker snippet sandbox, MCP server `claude-api-work` (`tasks_plan`, `tasks_submit`, `tasks_status`, `tasks_collect`, `review_budget`); models `claude-opus-5-5` (outline, dedupe, generation, repair) and `claude-sonnet-5-5` (fact-check).

**Spec:** `docs/superpowers/specs/2026-11-11-dev-interview-trainer-design.md` (sections 5.1, 5.2, 6, 7.2, 7.4, 7.6), `docs/content-guide.md`, "Content rules" in `AGENTS.md`, and Plan 4 (`docs/superpowers/plans/2026-11-13-plan-4-content-wave-1.md`) for every pipeline rule this plan does not restate.

**Revision:** v3 (2026-11-18). v2 followed the Codex and Fable reviews of v1; v3 follows their reviews of v2 (hard money ceiling, stage shares with outstanding batches, shortfall that keeps both floors and the 85% share, a fetch pass for repaired links, outline-level type rules in code, prompts before pricing, refusal handling, no partitioned dedupe). v2 changes: required `--wave-dir`; optional cutoff; a reservation envelope separate from the $120 actual stop; capped generation context; acceptance on the new stubs with level floors; type mix as bank totals and fewer `output`; per-topic `neighbours`; a read-only `plan-counts` command; a link fetch after repair; snippet runnability by dependency, not by topic.

## Owner decisions (2026-11-18)

- Size: about 400 new questions (801 in total).
- Focus: even out the level grid of every topic.
- New topics: Blazor, .NET MAUI (both split out of "UI frameworks"), cloud and containers, gRPC and real-time messaging.
- No human read of generated content (owner decision 2026-11-15): the bar is the model fact-check against fetched sources plus the mechanical checks; PR text says exactly that.
- Merges happen only on the owner's explicit word.

## Controller calls (the owner may overrule them while reviewing this plan)

- Topic keys and scope: `blazor` (render modes, components, state, JS interop, forms); `maui` (layouts, bindings and MVVM, navigation, platform code, lifecycle); `cloud-containers` (Docker images for .NET, Kubernetes, configuration and secrets in the cloud, Azure and AWS hosting choices, scaling); `grpc-realtime` (gRPC and Protobuf contracts, deadlines and streaming, SignalR, WebSockets, Server-Sent Events). Message brokers stay in `distributed`, CI/CD and observability in `devops`, WPF, WinUI, Avalonia and general MVVM in `ui-dotnet`, REST and HTTP API design in `web-api`.
- Moves (Task 2): `ui-dotnet/blazor-*` (6) -> `blazor`; `ui-dotnet/maui-*` (1) -> `maui`; `web-api/grpc-*`, `web-api/signalr-*`, `web-api/websocket-*` (6) -> `grpc-realtime`; `devops/aspnet-container-default-port-8080`, `devops/docker-*`, `devops/dockerfile-*` (3) -> `cloud-containers`. `web-api/rest-vs-grpc-public-and-internal` stays in `web-api`. A topic move keeps `id` and `slug` and needs no redirect (`docs/content-guide.md`, "Ids, slugs, renames, redirects and retiring").
- Grid rule: after the wave every topic has at least middle 9, senior 8, lead 4, architect 2, and junior 4; architecture, system-design, distributed, teamwork and behavioral keep their junior count (people and design topics). Table in Task 4. Acceptance floors sit one lower for middle, senior and lead (Task 2). `performance` lands at exactly the 35% code-heavy share (0.36): losing one of its code questions fails `finalize` and stops for the owner.
- Money: the wave stops at $120 actual (expected about $100). Reservations at cap are about three times actual, so the driver's reservation envelope is $260 and a separate actual stop holds the $120.
- Between the taxonomy PR (Task 3) and the content PRs the site shows the new topics with 1-6 questions each; accepted to keep the outline on `main`.

## Global Constraints

- Every content constraint of Plan 4's Global Constraints applies verbatim (file layout, `id`/`slug` rules, YAML through the C# reader only, RU/EN equivalence, plain hyphen only, no time-relative wording, links policy, one defensible answer per type, `meta.authors`, `meta.verified`, `meta.dotnetVersion`, batch task limits, fail-closed handling), except the executable-code rule, which this plan restates below.
- Executable code by dependency, not by topic: a snippet that needs only the BCL is runnable whatever its topic; `output` questions are BCL-only, always; a snippet that needs ASP.NET Core, EF Core, Blazor, MAUI, gRPC, SignalR, Docker or a cloud SDK appears only in `find-bug`, `single` or `multi`, with `harness: none`, `meta.noRunReason` and no `fixedSource`. No `output` or `find-bug` on lead or architect stubs.
- Fetched third-party page text never enters a commit: excerpts and full task files live in the git-ignored `docs/content-wave-2/.local/`; `ContentWaveAuditTests` enforces it.
- Every `wave.py` call passes `--wave-dir docs/content-wave-2` (Task 1 makes it required, so a forgotten flag fails instead of touching wave 1).
- Type mix as bank totals (the driver's semantics: existing plus new), tolerance +-5: open 215, single 165, multi 105, output 110, find-bug 86, situation 120 (= 801). The 400 new questions are then about open 115, single 85, multi 55, output 40, find-bug 40, situation 65.
- Budget: server caps $50 per submit and $150 per UTC day; the wave stops at $120 actual. Every `tasks_submit` carries the owner's own confirmation text for that submit, given after `tasks_plan` has priced it (recorded in `docs/content-wave-2/ledger.json`). No submit cut-off date.
- Machine load: one dotnet process at a time, `dotnet build -m:2 -nodeReuse:false`, tests with `--max-threads 2`, E2E per class only.
- Commits: Conventional Commits (`content:`, `feat:`, `docs:`, `chore:`), no AI attribution lines.

## Review Focus

1. **Topic boundary drift between a new topic and its old home.** Pinned by: narrowed descriptions (Task 3), per-topic `neighbours` (Task 4), the dedupe item (Task 5), `check_outline` hinge overlap.
2. **Grid holes after shortfall.** Lead and architect stubs are the expensive ones and the first a budget cut would drop. Pinned by: Task 2 acceptance on new stubs with level floors (test `test_acceptance_fails_when_a_level_floor_is_missed`) and the shortfall order that never cuts a stub below a floor.
3. **Hallucinated APIs in fast-moving areas** (Blazor render modes, MAUI handlers, Aspire, Azure SDK names). Pinned by: `links-fetch` excerpts, the sandbox where runnable, `unsure` blocking a pass, `meta.dotnetVersion: "10"`.
4. **Lead and architect questions that are senior questions in disguise.** Pinned by: level definitions in `prompts/outline.md` and `prompts/verify.md` (check 6), no `output`/`find-bug` on those levels, `apply` rejecting level drift.
5. **Wave 1 state touched by a wave 2 command.** Pinned by: Task 1 test `test_wave_dir_is_required`.

---

## File Structure

| Path | Responsibility |
|---|---|
| `tools/content-wave/wave.py`, `test_wave.py` | Driver changes of Tasks 1 and 2. |
| `content/dotnet/topics.yaml` | Four new topics; narrowed descriptions of `ui-dotnet`, `devops`, `web-api`. |
| `content/dotnet/{blazor,maui,cloud-containers,grpc-realtime}/` | Moved and new questions. |
| `tests/Trainer.Content.Tests/RepositoryContentTests.cs` | Topic count 27. |
| `docs/content-wave-2/targets.json`, `prompts/*.md`, `ledger.json`, `outline/`, `state.json`, `tasks/`, `results/`, `evidence/`, `quarantine/`, `rejected/`, `review/` | Wave 2 working set, same layout as wave 1. |

---

### Task 1: The driver works on a named wave

**Files:**
- Modify: `tools/content-wave/wave.py` (literals at 25, 721, 725, 801, 822, 1007, 1012, 1016, 1018, 1091, 2386, 2389; argument parsing near 3606 and 3731)
- Test: `tools/content-wave/test_wave.py` (`WaveEnv` at 472-480; `GEN_SHARED`/`REPAIR_SHARED`/`VERIFY_SHARED` users at 757 and 1482)

**Interfaces:**
- Produces: `def wave_rel(wave_dir: Path, worktree: Path, *parts: str) -> str` (POSIX path of the resolved `wave_dir/parts` relative to the resolved worktree); every task instruction and every `sharedFiles`/`files` entry naming the wave directory is built from it, so the shared-file lists become functions of `wave_dir`. `--wave-dir` becomes required and is resolved to an absolute path. `WaveEnv(wave="content-wave-1")` takes the wave directory name.

- [ ] **Step 1: Write the failing tests** in a new `class WaveDirTests(unittest.TestCase)`:

```python
def test_task_instructions_name_the_wave_dir(self):
    # outline, dedupe, gen, repair and verify tasks built against WaveEnv(wave="content-wave-2")
    self.assertTrue(all("content-wave-1" not in s for s in strings))
    self.assertIn("docs/content-wave-2/prompts/verify.md", verify_task["sharedFiles"])

def test_wave_dir_is_required(self):
    with self.assertRaises(SystemExit):
        wave.main(["budget"])

def test_relative_wave_dir_resolves_from_any_cwd(self):
    # chdir into tools/content-wave, pass "../../docs/content-wave-2": wave_rel gives "docs/content-wave-2/..."
```

- [ ] **Step 2:** `cd tools/content-wave && python -m unittest test_wave.WaveDirTests` -> FAIL.
- [ ] **Step 3: Implement** `wave_rel`, required and resolved `--wave-dir`, the `WaveEnv` parameter; replace every literal `docs/content-wave-1`.
- [ ] **Step 4:** `python -m unittest test_wave` -> all pass.
- [ ] **Step 5: Commit** `feat(content-wave): build task paths from a required --wave-dir`.

---

### Task 2: Targets semantics for a wave on a full bank

**Files:**
- Modify: `tools/content-wave/wave.py` (`submit_gate` 210-215, `ledger_check` 195-206, `context_files` 1088-1102, acceptance near 2855-2870, shortfall selection used by the pilot forecast, subcommand table near 3612)
- Test: `tools/content-wave/test_wave.py`

**Interfaces:**
- Produces, all read from `targets.json`:
  - `cutoffUtc` optional: absent means no cutoff.
  - `actualStopUsd`, `maxActualShare` and `expectedActualShare` optional. When present, `ledger_check` adds two rules to the existing envelope rule (`budgetUsd` - `budgetFloorUsd`, which keeps governing reservations):
    - hard ceiling: actual spent + (sum of outstanding estimates + this submit's estimate) x `maxActualShare` (0.6; the highest single-batch actual/estimate in wave 1 was 0.49) <= `actualStopUsd`;
    - completion forecast: actual spent + sum over outstanding submits of (estimate x share of its stage) + this submit's estimate x share of its stage + `forecastRemainingUsd` <= `actualStopUsd`, where `forecastRemainingUsd` covers only the stages after this submit. Shares per stage, above wave 1's measured 0.24 gen, 0.19 verify, 0.42 repair: `{"outline": 0.45, "dedupe": 0.45, "gen": 0.30, "verify": 0.25, "repair": 0.50}`.
    `ledger-submit` gains `--stage` (one of those keys) and records it. The refusal message names which rule failed: `envelope`, `ceiling` or `forecast`.
  - `generationContext`: `{"topicSamples": 6, "neighbourSamples": 0}`; `context_files` attaches `outline/<topic>.json` and up to that many committed questions of the stub's topic, questions of the stub's type first, then the rest, each group in path order, skipping every file whose `id` belongs to a wave 2 state record (unverified drafts never serve as samples); no neighbour files. Wave 1 keeps its behaviour when the key is absent.
  - `acceptance`: `{"minShare": 0.85, "basis": "new", "levelFloors": {"junior": 4, "middle": 8, "senior": 7, "lead": 3, "architect": 2}}`; with `basis: "new"` the share is survivors / planned new stubs per topic (the denominator is the plan's "New" column, fixed, never reduced by a shortfall), and every level cell must reach `min(target, floor)`; a cell whose target is below the floor (junior in the design and people topics) is held to its target. Floors sit one below the grid for middle, senior and lead, so one rejected stub there does not fail the wave; architect stays at 2, so a rejected architect stub gets one regeneration through `gen-retry` before `finalize`, and a second failure stops for the owner. Absent keys keep wave 1 behaviour.
  - `policies.leadArchitectForbiddenTypes: ["output", "find-bug"]`: `check_outline` rejects such a stub.
  - `def pick_shortfall(state: dict, targets: dict, existing: dict, count: int) -> list[str]` and the subcommand `shortfall --count N [--apply]` (dry run by default; `--apply` marks the stubs `shortfall` with reason "budget"). A stub may be cut only if, after the cut, its level cell stays at or above `min(target, floor)` and its topic keeps at least ceil(0.85 x planned new) stubs; order: middle, then junior, then senior, never lead or architect; across topics, largest remaining slack first. If fewer than `count` stubs qualify it returns what qualifies and says so.
  - `plan-counts` subcommand, read-only: prints per topic and level existing / target / remaining and the type quotas, and, once prompts exist, the serialized size of each outline item; writes nothing.

- [ ] **Step 1: Write the failing tests:**

```python
def test_submit_gate_without_cutoff_has_no_cutoff_problem(self): ...
def test_ledger_ceiling_counts_outstanding_and_this_submit_at_max_share(self):
    # spent 100, nothing outstanding, submit 45 -> 100 + 45 x 0.6 = 127 > 120: refused, message names "ceiling"
def test_ledger_forecast_counts_outstanding_by_stage(self):
    # spent 60, one outstanding gen 45, gen submit 45, forecast 50 -> 60 + 13.5 + 13.5 + 50 = 137 > 120: "forecast"
    # same with forecast 20 -> 107 and ceiling 60 + 90 x 0.6 = 114: accepted
def test_check_outline_rejects_output_and_find_bug_on_lead_and_architect(self): ...
def test_generation_samples_prefer_the_stub_type_and_skip_wave_drafts(self): ...
def test_generation_context_is_capped(self):
    # topic dir with 20 files, generationContext topicSamples 6 -> 1 outline + 6 files, none from neighbours
def test_acceptance_fails_when_a_level_floor_is_missed(self):
    # topic with existing lead 1, target lead 4, survivors give lead 3 -> failure names topic and lead
def test_acceptance_share_counts_new_stubs_only(self): ...
def test_pick_shortfall_keeps_floors_and_the_topic_share(self):
    # csharp: planned new 12, middle 9 -> 12; asking for 3 cuts at most 1 (ceil(0.85 x 12) = 11 must survive)
def test_pick_shortfall_never_cuts_lead_or_architect(self): ...
def test_plan_counts_writes_nothing(self):
    # snapshot of the wave dir before and after `plan-counts` is identical
```

- [ ] **Step 2:** run them -> FAIL. **Step 3:** implement. **Step 4:** `python -m unittest test_wave` -> all pass.
- [ ] **Step 5: Commit** `feat(content-wave): optional cutoff, actual stop, capped context and floor acceptance`.

---

### Task 3: Four new topics and the moves

**Files:**
- Modify: `content/dotnet/topics.yaml`; `tests/Trainer.Content.Tests/RepositoryContentTests.cs:35-39` (rename `RepositoryContent_DeclaresAllTwentyThreeTopics` to `RepositoryContent_DeclaresAllTwentySevenTopics`, count 27)
- Move: the 16 files listed under "Controller calls", each with its `topic:` field changed

**Interfaces:**
- Produces: topic keys `blazor`, `maui`, `cloud-containers`, `grpc-realtime` with RU and EN `title` and `description` (titles: "Blazor" / "Blazor"; ".NET MAUI" / ".NET MAUI"; "Облако и контейнеры" / "Cloud and containers"; "gRPC и real-time" / "gRPC and real-time"); descriptions follow the scope lines above. `ui-dotnet` becomes "WPF, WinUI и Avalonia: привязка данных, MVVM и модель отрисовки" / "WPF, WinUI and Avalonia: data binding, MVVM and the rendering model"; `devops` drops Docker; `web-api` drops gRPC, SignalR and WebSockets.

- [ ] **Step 1:** change the test, run `dotnet test --project tests/Trainer.Content.Tests -- --filter-class "*RepositoryContentTests"` -> FAIL (23).
- [ ] **Step 2:** add the topics, narrow the three descriptions, `git mv` the 16 files and set `topic:`.
- [ ] **Step 3:** `validate` -> "Content is valid"; `run-snippets` -> "All runnable snippets match their declared results."; Content.Tests and Web.Tests (`--max-threads 2`) green.
- [ ] **Step 4: Commit** `content: add blazor, maui, cloud-containers and grpc-realtime topics`; PR with Tasks 1-2 or on its own; CI green; the owner merges. The outline needs this on `main`.

---

### Task 4: Wave 2 targets, prompts and a priced owner confirmation

**Files:**
- Create: `docs/content-wave-2/targets.json`, `docs/content-wave-2/prompts/{outline,dedupe,generate,verify,repair}.md`, `docs/content-wave-2/ledger.json`

**Interfaces:**
- Consumes: Tasks 1-3.
- Produces: `targets.json` in the wave 1 schema plus Task 2's keys: no `cutoffUtc`; `budgetUsd` 260, `budgetFloorUsd` 5, `actualStopUsd` 120, `capPerSubmitUsd` 50, `capPerDayUsd` 150; `generationContext` as in Task 2; `acceptance` as in Task 2; `typeMix` as in Global Constraints; `chunk` and `verifyChunk` as wave 1 with `blazor`, `maui`, `grpc-realtime`, `cloud-containers` not code-heavy; level counts are bank totals after the wave.

- [ ] **Step 1: Write `targets.json`.** Totals J/M/S/L/A per topic (existing after Task 3 -> target):

| Topic | Existing | Target | New | Topic | Existing | Target | New |
|---|---|---|---|---|---|---|---|
| architecture | 1/4/5/3/5 | 1/9/9/4/5 | 10 | ef-core | 5/8/5/1/1 | 6/11/9/4/2 | 12 |
| aspnet-core | 6/9/5/1/1 | 7/12/9/4/2 | 12 | exceptions | 6/4/3/1/0 | 7/9/9/4/2 | 17 |
| async | 5/9/7/2/1 | 6/12/9/4/2 | 9 | grpc-realtime | 1/2/3/0/0 | 4/9/9/4/2 | 22 |
| behavioral | 3/4/3/3/1 | 3/9/9/4/2 | 13 | maui | 0/1/0/0/0 | 4/9/9/4/2 | 27 |
| blazor | 2/3/1/0/0 | 4/9/9/4/2 | 22 | oop-solid | 7/6/3/0/0 | 8/9/9/4/2 | 16 |
| cloud-containers | 1/2/0/0/0 | 4/9/9/4/2 | 25 | patterns | 5/6/3/1/1 | 6/9/9/4/2 | 14 |
| clr-gc | 4/7/6/1/0 | 5/10/9/4/2 | 12 | performance | 2/5/5/1/1 | 4/9/9/4/2 | 14 |
| collections-linq | 8/7/4/1/0 | 9/10/9/4/2 | 14 | security | 4/7/5/1/1 | 5/10/9/4/2 | 12 |
| concurrency | 4/8/6/1/1 | 5/11/9/4/2 | 11 | system-design | 0/3/4/2/5 | 0/9/8/4/5 | 12 |
| csharp | 9/9/6/1/0 | 10/12/9/4/2 | 12 | teamwork | 1/3/3/5/2 | 1/9/8/6/2 | 12 |
| databases | 5/7/4/1/1 | 6/10/9/4/2 | 13 | testing | 5/6/3/1/1 | 6/9/8/4/2 | 13 |
| devops | 3/3/3/1/1 | 4/9/9/4/2 | 17 | types-memory | 5/7/5/1/0 | 6/10/8/4/2 | 12 |
| distributed | 1/4/5/2/4 | 1/9/9/4/4 | 11 | ui-dotnet | 3/2/2/0/0 | 4/9/8/4/2 | 20 |
| | | | | web-api | 4/5/1/1/1 | 5/9/8/4/2 | 16 |

New by level: junior 31, middle 120, senior 137, lead 78, architect 34 = 400 (401 existing + 400 = 801). Groups: g1 csharp, types-memory, oop-solid, collections-linq, clr-gc, exceptions; g2 async, concurrency, performance; g3 aspnet-core, web-api, grpc-realtime, security, ef-core, databases; g4 testing, patterns, architecture, distributed, system-design; g5 ui-dotnet, blazor, maui; g6 devops, cloud-containers, teamwork, behavioral. `neighbours` per topic (the driver reads only `topics.<t>.neighbours`): wave 1's lists, plus `grpc-realtime` <-> `aspnet-core`, `web-api`, `distributed`; `cloud-containers` <-> `devops`, `aspnet-core`, `distributed`; `blazor` <-> `ui-dotnet`, `maui`, `aspnet-core`; `maui` <-> `ui-dotnet`, `blazor` (both directions written).

- [ ] **Step 2: Write the prompts** as copies of the wave 1 prompts with these changes only: paths name `content-wave-2`; `outline.md` lists the four new scope lines, says that moved questions are existing inventory, and forbids `output`/`find-bug` on lead and architect; `generate.md`, `repair.md` and `verify.md` carry the executable-code rule of the Global Constraints verbatim.
- [ ] **Step 3:** `python tools/content-wave/wave.py --wave-dir docs/content-wave-2 plan-counts` -> remaining equals the "New" column for every topic and level; type quotas resolve without a negative entry and no code-heavy topic gets more `output` + `find-bug` than its junior + middle + senior new stubs; every outline item under 400,000 characters. Any mismatch stops the task. The dedupe item depends on the outline and is measured in Task 5.
- [ ] **Step 4: Commit** `docs: add content wave 2 targets and prompts`.
- [ ] **Step 5: Owner confirmation, priced, per stage.** When a stage's task files exist (outline here; dedupe after the outline is applied; then pilot, generation, fact-check 1, repair, fact-check 2), run `tasks_plan` on each and send the owner one message with each submit's estimate at cap and its expected actual (estimate x stage share); record the owner's confirmation text per submit in `ledger.json`.

---

### Task 5: Outline and dedupe

- [ ] **Step 1:** `wave.py --wave-dir docs/content-wave-2 outline-tasks` (27 items, one task file, about $16 at cap), `preflight`, `tasks_plan`, `budget --estimate`, submit, collect, copy raw results, `outline-apply`, `outline-check`; commit `content: record wave 2 outline`.
- [ ] **Step 2:** `dedupe-tasks` (one item, expected about 200,000 characters; over 400,000 stop and ask the owner, because a partitioned dedupe would miss duplicates between partitions), price it (Task 4 Step 5), submit, collect, apply the replacements, `outline-check`, `allocate`; commit `content: record wave 2 dedupe and ids`.

### Task 6: Pilot

- [ ] **Step 1:** Five items as in Plan 4 Task 7, one of them from `blazor` or `grpc-realtime` with a `harness: none` question. Record per item output tokens, stop reason and actual cost, then `forecastRemainingUsd`. If actual spent + forecast exceeds $120, `shortfall --count N` (dry run, then `--apply`) and recompute; if the floors and the 85% share leave no room, stop and ask the owner.

### Task 7: Generation

- [ ] **Step 1:** `gen-tasks --max-estimate 45` (the driver splits by estimate in group order); for each task file: `preflight`, `tasks_plan`, `budget --estimate`, submit, collect, copy raw results, `apply`, commit. `retry-plan` after each collect. A refusal is handled by its cause: the server's $150 day cap -> wait for the next UTC day; the `envelope` rule -> collect outstanding batches first; the `ceiling` or `forecast` rule -> `shortfall` within its limits or a smaller `--max-estimate`, and if neither helps, stop and ask the owner. Never retry the same refused submit unchanged.
- [ ] **Step 2:** `mech`, `links-fetch` (first pass); commit `content: add wave 2 drafts with evidence`; `git status --short docs/content-wave-2/.local` prints nothing.

### Task 8: Fact-check round 1

- [ ] **Step 1:** `verify-tasks --round 1 --max-estimate 45`, submit each, collect, `verdicts --round 1`, one `--only` retry for `unverified`; commit `content: record wave 2 fact-check round 1`.

### Task 9: Repair and fact-check round 2

- [ ] **Step 1:** `links-fetch` second pass (30+ minutes after the first), `repair-tasks --max-estimate 45`, submit, collect, `apply --stage repair`, `mech`, then `links-fetch` again so URLs introduced or changed by the repair have evidence for round 2 (the earlier passes stay in the history for the two-strike dead-link rule); commit.
- [ ] **Step 2:** `verify-tasks --round 2`, submit, collect, `verdicts --round 2`, one retry, `resolve` the rest with a reason; then `links-fetch` once more, at least 30 minutes after the post-repair fetch, because `final-gate` requires two passes 30+ minutes apart for every link and repair can introduce URLs; commit `content: record wave 2 fact-check round 2`.

### Task 10: Finalize, final gate and pull requests

- [ ] **Step 1:** `finalize` (acceptance on new stubs with level floors; on failure stop and ask the owner), `final-gate` (validate, run-snippets, check-links exit 0; `--accept-owner-review "<reason>"` only with the owner's word if `owner-review` records remain), `dotnet build -warnaserror -m:2 -nodeReuse:false`, unit tests without the E2E and Docker categories, then the Docker category. Record the final `review_budget` reading.
- [ ] **Step 2:** One PR per group (g1-g6) from `main`, each with only its topics' files, body: "Model-verified against fetched source excerpts on <date>; mechanically checked (schema, sandbox execution where runnable, link status). Not human-reviewed (owner decision 2026-11-15)." followed by the not-executed questions and `unknown` links. CI green, then the owner merges.
- [ ] **Step 3:** The audit PR with `docs/content-wave-2/` (no `.local/`); `ContentWaveAuditTests` passes in its CI.

## Cost estimate

Wave 1 actual: $83 for 394 questions, about $0.21 per question, with generation items carrying a median of five context files. Wave 2 caps generation context at the outline plus six topic samples (Task 2), which keeps a generation item near wave 1's input; without the cap the review measured about $55-65 of extra input and a $135-140 wave. Expected actual with the cap: about $100 (more lead and architect stubs run one per item); stop at $120. Reservations at cap run about three times actual, so the envelope is $260 while the hard ceiling (Task 2) keeps actual spend under $120, generation takes about four submits under the $50 cap, and the $150 day cap puts generation and fact-check round 1 on different UTC days. Task 4 Step 2 and each stage's `tasks_plan` replace these figures with measured ones before the owner confirms.

## Self-review notes

- Owner decisions -> Task 3 (topics, moves), Task 4 (size, grid), Task 10 (PR wording, merges).
- Review findings -> Task 1 (paths, required wave dir, resolve, WaveEnv), Task 2 (cutoff, envelope vs actual stop, context cap, acceptance on new stubs with floors, shortfall order, plan-counts), Task 4 (type totals, fewer outputs, neighbours, payload sizes, behavioral junior exempt, priced confirmations), Task 9 (fetch after repair), Global Constraints (runnability by dependency).
- Review Focus 1 -> Tasks 3-5; 2 -> Task 2 tests; 3-4 -> Tasks 6-9 gates and prompts; 5 -> Task 1 test.
