# Plan 6: Content Wave 2 - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** About 400 new bilingual (RU and EN) .NET interview questions that even out the level grid of every topic and fill four new topics (Blazor, .NET MAUI, cloud and containers, gRPC and real-time), produced by the wave 1 pipeline and merged in topic-group pull requests.

**Architecture:** The wave 1 driver `tools/content-wave/wave.py` and its process (Plan 4) are reused unchanged in substance: outline -> cross-topic dedupe -> pilot -> generation -> evidence -> fact-check round 1 -> repair -> evidence -> fact-check round 2 -> finalize -> final gate -> PRs, every step fail-closed. Two preparatory changes come first: the driver takes its wave from `--wave-dir` everywhere (wave 1 paths are hard-coded in the task instructions today), and the taxonomy gains the four topics, with the matching existing questions moved into them.

**Tech Stack:** Python 3.14 stdlib driver, `Trainer.ContentCli` (.NET 10), Docker snippet sandbox, MCP server `claude-api-work` (`tasks_plan`, `tasks_submit`, `tasks_status`, `tasks_collect`, `review_budget`); models `claude-opus-5-5` (outline, dedupe, generation, repair) and `claude-sonnet-5-5` (fact-check).

**Spec:** `docs/superpowers/specs/2026-11-11-dev-interview-trainer-design.md` (sections 5.1, 5.2, 6, 7.2, 7.4, 7.6), `docs/content-guide.md`, "Content rules" in `AGENTS.md`, and Plan 4 (`docs/superpowers/plans/2026-11-13-plan-4-content-wave-1.md`) for every pipeline rule this plan does not restate.

## Owner decisions (2026-11-18)

- Size: about 400 new questions (about 800 in total).
- Focus: even out the level grid of every topic.
- New topics: Blazor, .NET MAUI (both split out of "UI frameworks"), cloud and containers, gRPC and real-time messaging.
- No human read of generated content (owner decision 2026-11-15): the bar is the model fact-check against fetched sources plus the mechanical checks; PR text says exactly that.
- Merges happen only on the owner's explicit word.

## Controller calls (the owner may overrule them while reviewing this plan)

- Topic keys and scope: `blazor` (render modes, components, state, JS interop, forms); `maui` (layouts, bindings and MVVM, navigation, platform code, lifecycle); `cloud-containers` (Docker images for .NET, Kubernetes, configuration and secrets in the cloud, Azure and AWS hosting choices, scaling); `grpc-realtime` (gRPC and Protobuf contracts, deadlines and streaming, SignalR, WebSockets, Server-Sent Events). Message brokers stay in `distributed`, CI/CD and observability in `devops`, WPF, WinUI, Avalonia and general MVVM in `ui-dotnet`, REST and HTTP API design in `web-api`.
- Moves (Task 2): `ui-dotnet/blazor-*` (6) -> `blazor`; `ui-dotnet/maui-*` (1) -> `maui`; `web-api/grpc-*`, `web-api/signalr-*`, `web-api/websocket-*` (6) -> `grpc-realtime`; `devops/aspnet-container-default-port-8080`, `devops/docker-*`, `devops/dockerfile-*` (3) -> `cloud-containers`. `web-api/rest-vs-grpc-public-and-internal` stays in `web-api` (it is an API-style choice). Per the content guide a topic move keeps `id` and `slug` and needs no redirect.
- Grid rule: after the wave every topic has at least middle 9, senior 8, lead 4, architect 2, and junior 4 where junior exists (architecture, system-design, distributed and teamwork keep their junior count); the remainder goes to middle and junior. Table in Task 3.
- Budget ceiling for the wave: $120 actual, about $95 expected (wave 1: $83 for 394 questions).

## Global Constraints

- Every content constraint of Plan 4's Global Constraints applies verbatim (file layout, `id`/`slug` rules, YAML through the C# reader only, RU/EN equivalence, plain hyphen only, no time-relative wording, links policy, executable-code rules, one defensible answer per type, `meta.authors`, `meta.verified`, `meta.dotnetVersion`, batch task limits, fail-closed handling).
- Fetched third-party page text never enters a commit: excerpts and full task files live in the git-ignored `docs/content-wave-2/.local/`; `ContentWaveAuditTests` enforces it (PR #1 of the current repository).
- Wave-wide type mix for the 400 new questions (tolerance +-5): open 95, single 80, multi 55, output 65, find-bug 50, situation 55. Code-heavy and people-topic policies as in wave 1 (`targets.json` `policies`), with `blazor`, `maui` and `grpc-realtime` treated as non-code-heavy (their code is framework code, run as `harness: none`).
- Budget: server caps $50 per submit and $150 per UTC day; the wave stops at $120 actual. Every `tasks_submit` carries the owner's own confirmation text for that submit (recorded in `docs/content-wave-2/ledger.json`). No submit cut-off date.
- Machine load: one dotnet process at a time, `dotnet build -m:2 -nodeReuse:false`, tests with `--max-threads 2`, E2E per class only.
- Commits: Conventional Commits (`content:`, `feat:`, `docs:`, `chore:`), no AI attribution lines.

## Review Focus

1. **Topic boundary drift between a new topic and its old home.** A new `grpc-realtime` stub that is really REST design, or a `cloud-containers` stub that is CI/CD. Pinned by: the narrowed Covers lines (Task 2), the new neighbour clusters in `targets.json` (Task 3), the cross-topic dedupe item (Task 4) and `check_outline` cluster hinge overlap.
2. **Duplicates of moved questions.** The outline of a new topic must see the moved questions as existing inventory, not as gaps. Pinned by: Task 2 lands before any outline, and Task 3 Step 2 checks the remaining counts against the table.
3. **Hallucinated APIs in fast-moving areas** (Blazor render modes, MAUI handlers, Aspire, Azure SDK names). Pinned by: `links-fetch` excerpts, the sandbox where runnable, `unsure` blocking a pass, `meta.dotnetVersion: "10"`.
4. **Lead and architect questions in technical topics that are senior questions in disguise.** Pinned by: level definitions in `prompts/outline.md` and `prompts/verify.md` (check 6), `apply` rejecting level drift.
5. **Wave 1 paths leaking into wave 2.** A task instruction or shared file still naming `docs/content-wave-1/`. Pinned by: Task 1 test `test_task_instructions_name_the_wave_dir`.

---

## File Structure

| Path | Responsibility |
|---|---|
| `tools/content-wave/wave.py`, `test_wave.py` | Driver: wave directory taken from `--wave-dir` in every path it writes into a task. |
| `content/dotnet/topics.yaml` | Four new topics; narrowed descriptions of `ui-dotnet`, `devops`, `web-api`. |
| `content/dotnet/{blazor,maui,cloud-containers,grpc-realtime}/` | Moved and new questions. |
| `tests/Trainer.Content.Tests/RepositoryContentTests.cs` | Topic count 27. |
| `docs/content-wave-2/targets.json`, `prompts/*.md`, `ledger.json`, `outline/`, `state.json`, `tasks/`, `results/`, `evidence/`, `quarantine/`, `rejected/`, `review/` | Wave 2 working set, same layout as wave 1. |

---

### Task 1: Driver takes the wave from `--wave-dir`

**Files:**
- Modify: `tools/content-wave/wave.py` (constants at lines 25, 721, 725, 801, 822, 1007, 1012, 1016-1018, 1091, 2386-2389)
- Test: `tools/content-wave/test_wave.py`

**Interfaces:**
- Produces: `def wave_rel(wave_dir: Path, worktree: Path, *parts: str) -> str` returning the POSIX path of `wave_dir/parts` relative to the worktree; every task instruction string and every `sharedFiles`/`files` entry that names the wave directory is built from it. `--wave-dir` stays optional with the wave 1 default, so the wave 1 audit trail can still be re-run.

- [ ] **Step 1: Write the failing test**

```python
def test_task_instructions_name_the_wave_dir(self):
    # Build an outline, dedupe, gen, repair and verify task against a wave dir named content-wave-2
    # and assert that no instruction, sharedFiles or files entry contains "content-wave-1".
    ...
    self.assertTrue(all("content-wave-1" not in s for s in strings))
    self.assertIn("docs/content-wave-2/prompts/verify.md", verify_task["sharedFiles"])
```

  The test lives in a new `class WaveDirTests(unittest.TestCase)` and builds its environment with the existing `WaveEnv`, pointing `wave_dir` at `<worktree>/docs/content-wave-2`.

- [ ] **Step 2: Run it to see it fail:** `cd tools/content-wave && python -m unittest test_wave.WaveDirTests` -> FAIL on the first hard-coded path.
- [ ] **Step 3: Implement `wave_rel` and replace every literal `docs/content-wave-1` in task-building code with it** (instructions become functions of `wave_dir`, not module constants).
- [ ] **Step 4: Run the whole suite:** `python -m unittest test_wave` -> all pass (126 + the new test).
- [ ] **Step 5: Commit** `feat(content-wave): build task paths from --wave-dir`.

---

### Task 2: Four new topics and the moves

**Files:**
- Modify: `content/dotnet/topics.yaml`, `tests/Trainer.Content.Tests/RepositoryContentTests.cs:39`
- Move: the 16 files listed under "Controller calls", each with its `topic:` field changed

**Interfaces:**
- Produces: topic keys `blazor`, `maui`, `cloud-containers`, `grpc-realtime` with RU and EN `title` and `description` (titles: "Blazor" / "Blazor"; ".NET MAUI" / ".NET MAUI"; "Облако и контейнеры" / "Cloud and containers"; "gRPC и real-time" / "gRPC and real-time"). Descriptions follow the scope lines above. `ui-dotnet` becomes "WPF, WinUI и Avalonia: привязка данных, MVVM и модель отрисовки" / "WPF, WinUI and Avalonia: data binding, MVVM and the rendering model"; `devops` drops Docker; `web-api` drops gRPC, SignalR and WebSockets.

- [ ] **Step 1: Change the expected count** in `RepositoryContentTests` to `27` and run `dotnet test --project tests/Trainer.Content.Tests -- --filter-class "*RepositoryContentTests"` -> FAIL (23).
- [ ] **Step 2: Add the topics, narrow the three descriptions, move the 16 files** (`git mv`, then set `topic:`).
- [ ] **Step 3: Verify:** `dotnet run --project tools/Trainer.ContentCli -- validate` -> "Content is valid"; `run-snippets` -> "All runnable snippets match their declared results."; Content.Tests, Web.Tests (`--max-threads 2`) green. Check the static home page and catalog in a local static build only if a Web test names a topic count.
- [ ] **Step 4: Commit** `content: add blazor, maui, cloud-containers and grpc-realtime topics`.
- [ ] **Step 5: PR, CI green, owner merges.** The site gets the new topics before any spend, so the outline sees them on `main`.

---

### Task 3: Wave 2 targets, prompts and owner inputs

**Files:**
- Create: `docs/content-wave-2/targets.json`, `docs/content-wave-2/prompts/{outline,dedupe,generate,verify,repair}.md`, `docs/content-wave-2/ledger.json`

**Interfaces:**
- Consumes: Task 1 `--wave-dir`, Task 2 topic keys.
- Produces: `targets.json` in the wave 1 schema; level counts are totals after the wave (existing questions count, as the wave 1 seeds did); `cutoffUtc` absent; `budgetUsd` 120.

- [ ] **Step 1: Write `targets.json`.** Totals J/M/S/L/A per topic (existing after Task 2 -> target):

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

New by level: junior 31, middle 120, senior 137, lead 78, architect 34 = 400. Groups: g1 C# and runtime (csharp, types-memory, oop-solid, collections-linq, clr-gc, exceptions), g2 async and performance (async, concurrency, performance), g3 web and data (aspnet-core, web-api, grpc-realtime, security, ef-core, databases), g4 design (testing, patterns, architecture, distributed, system-design), g5 UI (ui-dotnet, blazor, maui), g6 delivery and people (devops, cloud-containers, teamwork, behavioral). Neighbour clusters: wave 1's, with `grpc-realtime` joining {aspnet-core, web-api, security}, `cloud-containers` joining {testing, devops}, and a new {ui-dotnet, blazor, maui}.

- [ ] **Step 2: Check the table against the repository:** `python tools/content-wave/wave.py --wave-dir docs/content-wave-2 budget` runs, and a dry `outline-tasks` reports remaining counts equal to the "New" column for every topic; any difference stops the task.
- [ ] **Step 3: Write the prompts** as copies of the wave 1 prompts with only these changes: paths name `content-wave-2`; `outline.md` lists the four new topics' scope lines and says that moved questions are existing inventory; `generate.md` adds "Blazor, MAUI and gRPC code is framework code: `harness: none` with `meta.noRunReason`"; nothing else changes.
- [ ] **Step 4: Owner inputs in one message,** recorded in `ledger.json`: acceptance threshold (85% per topic, as wave 1), and one confirmation text per submit: outline "up to $10", dedupe "up to $2", pilot "up to $4", generation "up to $45" per submit (up to three), fact-check round 1 "up to $40", repair "up to $45", fact-check round 2 "up to $25", retries "up to $12".
- [ ] **Step 5: Commit** `docs: add content wave 2 targets and prompts`.

---

### Task 4: Outline and dedupe

- [ ] **Step 1:** `outline-tasks` (27 items), `preflight`, `tasks_plan`, `budget`, submit with the owner's text, collect, copy raw results, `outline-apply`, `outline-check`; commit `content: record wave 2 outline`.
- [ ] **Step 2:** `dedupe-tasks` (1 item over all stubs plus the full inventory), submit, collect, apply the replacements, `outline-check`, `allocate` (IDs); commit `content: record wave 2 dedupe and ids`.

### Task 5: Pilot

- [ ] **Step 1:** Five items as in Plan 4 Task 7, with one of them from a new topic (`blazor` or `grpc-realtime`, `harness: none`). Same measurement and chunking rule; record the cost per stub and the `forecastRemainingUsd`. If the forecast breaks $120, mark stubs `shortfall` in Plan 4's order before any main submit.

### Task 6: Generation

- [ ] **Step 1:** `gen-tasks --max-estimate 45` split by group; for each: `preflight`, `tasks_plan`, `budget`, submit, collect, copy raw results, `apply`, commit. `retry-plan` after each collect.
- [ ] **Step 2:** `mech`, `links-fetch` (first pass); commit `content: add wave 2 drafts with evidence`. Verify that `git status` shows nothing under `docs/content-wave-2/.local/`.

### Task 7: Fact-check round 1

- [ ] **Step 1:** `verify-tasks --round 1 --max-estimate 40`, submit, collect, `verdicts --round 1`, one `--only` retry for `unverified`; commit `content: record wave 2 fact-check round 1`.

### Task 8: Repair and fact-check round 2

- [ ] **Step 1:** `links-fetch` second pass (30+ minutes after the first), `repair-tasks --max-estimate 45`, submit, collect, `apply --stage repair`, `mech`; commit.
- [ ] **Step 2:** `verify-tasks --round 2`, submit, collect, `verdicts --round 2`, one retry, `resolve` the rest with a reason; commit `content: record wave 2 fact-check round 2`.

### Task 9: Finalize, final gate and pull requests

- [ ] **Step 1:** `finalize` (acceptance summary; on failed acceptance stop and ask the owner), `final-gate` (validate, run-snippets, check-links exit 0), `dotnet build -warnaserror -m:2 -nodeReuse:false`, unit tests without E2E and Docker categories, then the Docker category. Record the final `review_budget` reading.
- [ ] **Step 2:** One PR per group (g1-g6) from `main`, each with only its topics' files, body: "Model-verified against fetched source excerpts on <date>; mechanically checked (schema, sandbox execution where runnable, link status). Not human-reviewed (owner decision 2026-11-15)." followed by the not-executed questions and `unknown` links. CI green, then the owner merges.
- [ ] **Step 3:** After the last content PR, the audit PR with `docs/content-wave-2/` (no `.local/`); `ContentWaveAuditTests` must pass in its CI.

## Cost estimate

Wave 1 actual: $83 for 394 questions, about $0.21 per question including outline, two fact-check rounds and repair. Wave 2 expected actual: 400 x $0.21 x 1.1 (more lead and architect, which run 1 stub per item) = about $92; ceiling $120. Estimates at cap (what `tasks_plan` reserves) run about 2.7 times the actual, so generation splits into three submits under the $50 cap, and the $150 daily cap means generation and fact-check round 1 land on different UTC days when their reservations would exceed it.

## Self-review notes

- Owner decisions -> Task 2 (topics, moves), Task 3 (size 400, grid), Task 9 (PR wording without human review).
- Review Focus 1-2 -> Tasks 2-4; 3-4 -> Tasks 5-8 via wave 1 gates; 5 -> Task 1 test.
- Excerpt policy -> Global Constraints, Task 6 Step 2, Task 9 Step 3.
