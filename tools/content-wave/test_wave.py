"""Tests for the content wave driver. Run: python -m unittest discover -s tools/content-wave -v"""

import contextlib
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import wave  # noqa: E402

LEVELS = ["junior", "middle", "senior", "lead", "architect"]

QUESTION_TEMPLATE = """# yaml-language-server: $schema=../../schema/question.schema.json
id: 01KBZ3Q8N9V2K3M4P5R6S7T8W9
slug: sample
level: junior
type: open
locales:
  en:
    answer: {answer}
meta:
  authors: [platonenkov]
  verified: {verified}
"""


def stub(slug, level="junior", type_="open", hinge=None, subtopic="general"):
    return {
        "slug": slug,
        "level": level,
        "type": type_,
        "subtopic": subtopic,
        "angle": f"Asks about {slug}.",
        "answerHinge": hinge or f"unique fact {slug} {slug}-detail",
        "sources": ["https://learn.microsoft.com/en-us/dotnet/csharp/"],
    }


def small_targets(topics):
    """Targets with the given {topic: {"levels": {...}, "codeHeavy", "peopleTopic", "neighbours"}} and a loose mix."""
    full = {}
    for key, spec in topics.items():
        levels = {lv: 0 for lv in LEVELS}
        levels.update(spec.get("levels", {}))
        full[key] = {
            "group": "g1a",
            "levels": levels,
            "codeHeavy": spec.get("codeHeavy", False),
            "peopleTopic": spec.get("peopleTopic", False),
            "neighbours": spec.get("neighbours", []),
        }
    return {
        "typeMix": {"open": 0, "single": 0, "multi": 0, "output": 0, "find-bug": 0, "situation": 0},
        "typeTolerance": 1000,
        "policies": {
            "codeHeavyMinOutputFindBugShare": 0.35,
            "peopleForbiddenTypes": ["output", "find-bug"],
            "peopleMinSituationShare": 0.5,
            "outputPreferredTopics": [],
            "hingeJaccardMax": 0.5,
        },
        "topics": full,
    }


def remaining_of(targets):
    return {k: dict(v["levels"]) for k, v in targets["topics"].items()}


class UlidAndHashTests(unittest.TestCase):
    def test_new_ulid_shape(self):
        first = wave.new_ulid()
        second = wave.new_ulid()
        self.assertEqual(26, len(first))
        self.assertRegex(first, r"^[0-7][0-9A-HJKMNP-TV-Z]{25}$")
        self.assertNotEqual(first, second)

    def test_new_ulid_deterministic_parts(self):
        value = wave.new_ulid(now_ms=0, rand=bytes(10))
        self.assertEqual("0" * 26, value)
        self.assertEqual("7ZZZZZZZZZ", wave.new_ulid(now_ms=(1 << 48) - 1, rand=bytes(10))[:10])

    def test_content_hash_ignores_verified_date(self):
        a = QUESTION_TEMPLATE.format(answer="Yes.", verified="2026-11-12")
        b = QUESTION_TEMPLATE.format(answer="Yes.", verified="2026-11-13")
        c = QUESTION_TEMPLATE.format(answer="No.", verified="2026-11-13")
        self.assertEqual(wave.content_hash(a), wave.content_hash(b))
        self.assertNotEqual(wave.content_hash(b), wave.content_hash(c))

    def test_content_hash_ignores_crlf_and_bom(self):
        a = QUESTION_TEMPLATE.format(answer="Yes.", verified="2026-11-12")
        self.assertEqual(wave.content_hash(a), wave.content_hash("﻿" + a.replace("\n", "\r\n")))

    def test_content_hash_only_meta_verified(self):
        text = QUESTION_TEMPLATE.format(answer="|\n      verified: 1\n      more", verified="2026-11-12")
        other = QUESTION_TEMPLATE.format(answer="|\n      verified: 2\n      more", verified="2026-11-12")
        self.assertNotEqual(wave.content_hash(text), wave.content_hash(other))


class StateTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp())

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def test_save_state_atomic(self):
        path = self.dir / "state.json"
        wave.save_state(path, {"a": {"id": "X"}})
        before = path.read_bytes()
        with self.assertRaises(TypeError):
            wave.save_state(path, {"a": object()})
        self.assertEqual(before, path.read_bytes())
        self.assertEqual([], [p.name for p in self.dir.iterdir() if p.name.endswith(".tmp")])
        self.assertEqual({"a": {"id": "X"}}, wave.load_state(path))

    def test_load_state_missing_is_empty(self):
        self.assertEqual({}, wave.load_state(self.dir / "none.json"))

    def test_save_state_writes_lf_utf8(self):
        path = self.dir / "state.json"
        wave.save_state(path, {"a": {"title": "Заголовок"}})
        raw = path.read_bytes()
        self.assertNotIn(b"\r\n", raw)
        self.assertIn("Заголовок".encode("utf-8"), raw)


class LedgerTests(unittest.TestCase):
    def test_ledger_check_blocks_over_budget(self):
        ledger = {
            "readings": [{"utc": "2026-11-13T10:00:00Z", "usd": 60, "baselineUsd": 0, "outstandingAtReading": []}],
            "submits": [{"name": "gen-a", "batchId": "b1", "estimateUsd": 48, "status": "submitted"}],
        }
        ok, message = wave.ledger_check(ledger, 40, 0, 146, 5)
        self.assertFalse(ok)
        self.assertIn("148", message)

    def test_ledger_check_allows_within_budget(self):
        ledger = {
            "readings": [{"utc": "2026-11-13T10:00:00Z", "usd": 60, "baselineUsd": 0, "outstandingAtReading": []}],
            "submits": [{"name": "gen-a", "batchId": "b1", "estimateUsd": 48, "status": "submitted"}],
        }
        ok, _ = wave.ledger_check(ledger, 30, 0, 146, 5)
        self.assertTrue(ok)

    def test_ledger_reading_holding_outstanding_estimate_is_not_double_counted(self):
        # The server reports an uncollected batch at its estimate, so a reading taken while b1 was outstanding
        # already contains its 48 dollars.
        ledger = {
            "readings": [{"utc": "2026-11-13T10:00:00Z", "usd": 108, "baselineUsd": 0, "outstandingAtReading": ["b1"]}],
            "submits": [{"name": "gen-a", "batchId": "b1", "estimateUsd": 48, "status": "submitted"}],
        }
        self.assertAlmostEqual(60, wave.actual_spent(ledger))
        ok, _ = wave.ledger_check(ledger, 30, 0, 146, 5)
        self.assertTrue(ok)

    def test_ledger_check_without_reading_fails_closed(self):
        ok, message = wave.ledger_check({"readings": [], "submits": []}, 1, 0, 146, 5)
        self.assertFalse(ok)
        self.assertIn("budget --record", message)

    def test_ledger_baseline_excludes_pre_wave_spend(self):
        ledger = {
            "readings": [{"utc": "2026-11-13T10:00:00Z", "usd": 12, "baselineUsd": 10, "outstandingAtReading": []}],
            "submits": [],
        }
        self.assertAlmostEqual(2, wave.actual_spent(ledger))

    def test_submit_gate_refuses_after_cutoff_and_over_caps(self):
        targets = {"cutoffUtc": "2026-11-13T23:00:00Z", "capPerSubmitUsd": 50, "capPerDayUsd": 150}
        ledger = {"readings": [{"utc": "2026-11-13T10:00:00Z", "usd": 0, "baselineUsd": 0, "outstandingAtReading": []}],
                  "submits": []}
        late = wave.parse_utc("2026-11-13T23:00:01Z")
        early = wave.parse_utc("2026-11-13T12:00:00Z")
        self.assertTrue(any("cutoff" in p for p in wave.submit_gate(targets, ledger, 10, late)))
        self.assertTrue(any("per-submit" in p for p in wave.submit_gate(targets, ledger, 51, early)))
        self.assertEqual([], wave.submit_gate(targets, ledger, 10, early))


class TaskFileTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp())
        (self.root / "docs").mkdir()
        (self.root / "docs" / "a.md").write_text("hello", encoding="utf-8")

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def task(self, **overrides):
        task = {
            "project": "dev-interview-trainer",
            "ref": "abc",
            "model": "claude-opus-5-5",
            "effort": "high",
            "maxOutputTokens": 32000,
            "instructions": "x",
            "sharedFiles": ["docs/a.md"],
            "outputSchema": {"type": "object"},
            "items": [{"id": "outline-csharp", "text": "t", "files": ["docs/a.md"]}],
        }
        task.update(overrides)
        return task

    def test_check_task_file_accepts_valid(self):
        self.assertEqual([], wave.check_task_file(self.task(), self.root))

    def test_check_task_file_rejects_long_item(self):
        problems = wave.check_task_file(self.task(items=[{"id": "big", "text": "x" * 400_001, "files": []}]), self.root)
        self.assertTrue(any("400,000" in p for p in problems), problems)

    def test_check_task_file_counts_attached_files(self):
        (self.root / "docs" / "big.md").write_text("y" * 399_999, encoding="utf-8")
        problems = wave.check_task_file(self.task(items=[{"id": "big", "text": "xx", "files": ["docs/big.md"]}]), self.root)
        self.assertTrue(any("400,000" in p for p in problems), problems)

    def test_check_task_file_rejects_bad_fields(self):
        problems = wave.check_task_file(self.task(
            maxOutputTokens=32001, model="claude-haiku", effort="max",
            items=[{"id": "bad id", "text": "t", "files": []}, {"id": "dup", "text": "t", "files": []},
                   {"id": "dup", "text": "t", "files": []}, {"id": "con", "text": "t", "files": []},
                   {"id": "nomatch", "text": "t", "files": ["docs/*.cs"]}]), self.root)
        joined = "\n".join(problems)
        for needle in ["maxOutputTokens", "model", "effort", "bad id", "duplicate", "device", "docs/*.cs"]:
            self.assertIn(needle, joined)

    def test_check_task_file_missing_shared_file(self):
        problems = wave.check_task_file(self.task(sharedFiles=["docs/missing.md"]), self.root)
        self.assertTrue(any("docs/missing.md" in p for p in problems))


class PreflightTests(unittest.TestCase):
    def setUp(self):
        self.repo = Path(tempfile.mkdtemp())
        self.git("init", "-q")
        self.git("config", "user.email", "t@example.invalid")
        self.git("config", "user.name", "t")
        self.git("config", "core.autocrlf", "false")
        (self.repo / "a.md").write_text("one\n", encoding="utf-8")
        (self.repo / "b.md").write_text("two\n", encoding="utf-8")
        self.git("add", ".")
        self.git("commit", "-q", "-m", "init")
        self.sha = self.git("rev-parse", "HEAD").strip()

    def tearDown(self):
        shutil.rmtree(self.repo, ignore_errors=True)

    def git(self, *args):
        return subprocess.run(["git", "-C", str(self.repo), *args], check=True, capture_output=True, text=True).stdout

    def test_preflight_clean(self):
        self.assertEqual([], wave.preflight(self.sha, self.repo, ["a.md", "b.md"], self.repo))

    def test_preflight_detects_uncommitted_file(self):
        (self.repo / "a.md").write_text("changed\n", encoding="utf-8")
        problems = wave.preflight(self.sha, self.repo, ["a.md", "b.md"], self.repo)
        self.assertEqual(1, len(problems))
        self.assertIn("a.md", problems[0])

    def test_preflight_detects_file_missing_at_ref(self):
        (self.repo / "c.md").write_text("new\n", encoding="utf-8")
        problems = wave.preflight(self.sha, self.repo, ["c.md"], self.repo)
        self.assertTrue(any("c.md" in p for p in problems))

    def test_preflight_unknown_ref(self):
        problems = wave.preflight("0" * 40, self.repo, ["a.md"], self.repo)
        self.assertTrue(any("resolve" in p for p in problems))


class InventoryAndOutlineTests(unittest.TestCase):
    def test_existing_inventory(self):
        dump = {"questions": [{"path": "dotnet/async/a.yaml", "document": {
            "slug": "a", "topic": "async", "locales": {"en": {"title": "A title"}}}}]}
        inventory = wave.existing_inventory(dump, [{"from": "async/old-slug", "to": "async/a"}])
        self.assertEqual("A title", inventory["a"])
        self.assertEqual("(redirect)", inventory["old-slug"])

    def test_check_outline_slug_taken_by_redirect(self):
        targets = small_targets({"async": {"levels": {"junior": 1}}})
        inventory = wave.existing_inventory({"questions": []}, [{"from": "async/old-slug", "to": "async/new-slug"}])
        failures = wave.check_outline({"async": [stub("old-slug")]}, remaining_of(targets), inventory, targets)
        self.assertTrue(any("old-slug" in f and "redirect" in f for f in failures), failures)

    def test_check_outline_slug_invalid_and_duplicate(self):
        targets = small_targets({"async": {"levels": {"junior": 3}}})
        failures = wave.check_outline(
            {"async": [stub("Bad_Slug"), stub("same"), stub("same")]}, remaining_of(targets), {}, targets)
        joined = "\n".join(failures)
        self.assertIn("Bad_Slug", joined)
        self.assertIn("duplicated", joined)

    def test_check_outline_level_count_mismatch(self):
        targets = small_targets({"csharp": {"levels": {"junior": 9}}})
        stubs = [stub(f"s-{i}") for i in range(8)]
        failures = wave.check_outline({"csharp": stubs}, remaining_of(targets), {}, targets)
        self.assertIn("csharp junior: expected 9, got 8", failures)

    def test_check_outline_missing_topic(self):
        targets = small_targets({"csharp": {"levels": {"junior": 1}}, "async": {"levels": {"junior": 1}}})
        failures = wave.check_outline({"csharp": [stub("a")]}, remaining_of(targets), {}, targets)
        self.assertTrue(any("async" in f and "missing" in f for f in failures), failures)

    def test_check_outline_people_topic_situation_share(self):
        targets = small_targets({"teamwork": {"levels": {"lead": 5}, "peopleTopic": True}})
        stubs = [stub("t-1", "lead", "situation"), stub("t-2", "lead", "situation"),
                 stub("t-3", "lead", "open"), stub("t-4", "lead", "open"), stub("t-5", "lead", "single")]
        failures = wave.check_outline({"teamwork": stubs}, remaining_of(targets), {}, targets)
        self.assertTrue(any("teamwork" in f and "situation" in f for f in failures), failures)

    def test_check_outline_people_topic_forbids_output(self):
        targets = small_targets({"teamwork": {"levels": {"lead": 2}, "peopleTopic": True}})
        stubs = [stub("t-1", "lead", "situation"), stub("t-2", "lead", "output")]
        failures = wave.check_outline({"teamwork": stubs}, remaining_of(targets), {}, targets)
        self.assertTrue(any("teamwork" in f and "output" in f for f in failures), failures)

    def test_check_outline_code_heavy_share_counts_existing(self):
        targets = small_targets({"async": {"levels": {"junior": 2}, "codeHeavy": True}})
        stubs = [stub("a-1", type_="open"), stub("a-2", type_="single")]
        failures = wave.check_outline({"async": stubs}, remaining_of(targets), {}, targets)
        self.assertTrue(any("async" in f and "35%" in f for f in failures), failures)
        ok = wave.check_outline({"async": stubs}, remaining_of(targets), {}, targets,
                                existing_types={"async": {"output": 1, "find-bug": 1}})
        self.assertFalse(any("35%" in f for f in ok), ok)

    def test_check_outline_type_mix_tolerance(self):
        targets = small_targets({"csharp": {"levels": {"junior": 10}}})
        targets["typeMix"] = {"open": 10, "single": 0, "multi": 0, "output": 0, "find-bug": 0, "situation": 0}
        targets["typeTolerance"] = 5
        stubs = [stub(f"s-{i}", type_="single") for i in range(10)]
        failures = wave.check_outline({"csharp": stubs}, remaining_of(targets), {}, targets)
        self.assertTrue(any("type mix open" in f for f in failures), failures)

    def test_check_outline_cross_topic_hinge_overlap(self):
        targets = small_targets({
            "async": {"levels": {"middle": 1}, "neighbours": ["concurrency"]},
            "concurrency": {"levels": {"middle": 1}, "neighbours": ["async"]},
        })
        hinge = "SemaphoreSlim WaitAsync releases the waiting continuation on the thread pool"
        outlines = {
            "async": [stub("async-semaphore", "middle", hinge=hinge, subtopic="cancellation")],
            "concurrency": [stub("concurrency-semaphore", "middle", hinge=hinge, subtopic="locks")],
        }
        failures = wave.check_outline(outlines, remaining_of(targets), {}, targets)
        overlap = [f for f in failures if "overlap" in f]
        self.assertEqual(1, len(overlap), failures)
        self.assertIn("async/async-semaphore", overlap[0])
        self.assertIn("concurrency/concurrency-semaphore", overlap[0])

    def test_check_outline_unrelated_topics_not_compared(self):
        targets = small_targets({"async": {"levels": {"middle": 1}}, "devops": {"levels": {"middle": 1}}})
        hinge = "same words in both hinges for this check"
        outlines = {"async": [stub("a", "middle", hinge=hinge)], "devops": [stub("d", "middle", hinge=hinge)]}
        failures = wave.check_outline(outlines, remaining_of(targets), {}, targets)
        self.assertFalse(any("overlap" in f for f in failures), failures)

    def test_allocate_is_stable(self):
        outlines = {"csharp": [dict(stub("a"), stubId="csharp-01"), dict(stub("b"), stubId="csharp-02")]}
        first = wave.allocate(outlines, {})
        ids = {k: v["id"] for k, v in first.items()}
        self.assertEqual({"csharp-01", "csharp-02"}, set(ids))
        self.assertTrue(all(v["status"] == "planned" for v in first.values()))
        outlines["csharp"].append(dict(stub("c"), stubId="csharp-03"))
        second = wave.allocate(outlines, json.loads(json.dumps(first)))
        for key, value in ids.items():
            self.assertEqual(value, second[key]["id"])
        self.assertEqual(3, len({v["id"] for v in second.values()}))

    def test_assign_stub_ids_keeps_existing_and_numbers_new(self):
        stubs = [dict(stub("a"), stubId="csharp-02"), stub("b"), stub("c")]
        wave.assign_stub_ids("csharp", stubs)
        self.assertEqual(["csharp-02", "csharp-03", "csharp-04"], [s["stubId"] for s in stubs])


class WaveDirTests(unittest.TestCase):
    def test_task_instructions_name_the_wave_dir(self):
        env = VerifyEnv(wave="content-wave-2")
        try:
            wave_dir, worktree = env.wave_dir, env.worktree
            for i, level in enumerate(("junior", "junior"), 1):
                env.add(f"testing-{i:02d}", topic="testing", level=level, type_="open")
            env.write_question(env.add("testing-03"))
            env.add("testing-04")["status"] = "needs-repair"
            (wave_dir / "outline").mkdir(parents=True)
            (wave_dir / "outline" / "testing.json").write_text("{}", encoding="utf-8")
            full = json.loads((wave.REPO_ROOT / "docs" / "content-wave-1" / "targets.json").read_text(encoding="utf-8"))
            dump = {"questions": [], "topics": {"dotnet": [
                {"key": k, "locales": {"en": {"title": k, "description": f"Covers {k}."}}} for k in full["topics"]]},
                "redirects": {"dotnet": []}, "readIssues": []}
            outline = wave.build_outline_task(dump, full, "abc", wave_dir, worktree)
            dedupe = wave.build_dedupe_task(dump, full, {}, "abc", wave_dir, worktree)
            gen_tasks, entries = wave.build_gen_tasks(env.state, env.targets, "abc", worktree, wave_dir, {}, None, None,
                                                      "2026-11-13", lister=lambda *a: [])
            gen = gen_tasks[0]
            repair_tasks, _, _ = wave.build_repair_tasks(env.state, env.targets, "abc", worktree, wave_dir, {}, {},
                                                         None, lister=lambda *a: [])
            verify_tasks, _ = wave.build_verify_tasks(env.state, env.targets, "abc", worktree, wave_dir, {}, {}, 1,
                                                      None, None)
            repair, verify = repair_tasks[0], verify_tasks[0]
            strings = []
            for task in (outline, dedupe, gen, repair, verify):
                strings.append(task["instructions"])
                strings.extend(task["sharedFiles"])
                for item in task["items"]:
                    strings.extend(item["files"])
            self.assertTrue(strings)
            self.assertTrue(all("content-wave-1" not in s for s in strings))
            self.assertIn("docs/content-wave-2/prompts/verify.md", verify["sharedFiles"])
            self.assertIn("docs/content-wave-2/prompts/outline.md", outline["sharedFiles"])
            self.assertIn("docs/content-wave-2/prompts/dedupe.md", dedupe["sharedFiles"])
            self.assertIn("docs/content-wave-2/prompts/generate.md", gen["sharedFiles"])
            self.assertIn("docs/content-wave-2/prompts/repair.md", repair["sharedFiles"])
            self.assertEqual(["docs/content-wave-2/outline/testing.json"], gen["items"][0]["files"])
        finally:
            env.cleanup()

    def test_wave_dir_is_required(self):
        with self.assertRaises(SystemExit):
            wave.main(["budget"])

    def test_relative_wave_dir_resolves_from_any_cwd(self):
        previous = os.getcwd()
        os.chdir(Path(wave.__file__).resolve().parent)
        try:
            relative = Path("../../docs/content-wave-2")
            self.assertEqual("docs/content-wave-2/prompts/verify.md",
                             wave.wave_rel(relative, wave.REPO_ROOT, "prompts", "verify.md"))
        finally:
            os.chdir(previous)

    def test_wave_dir_outside_worktree_is_an_error(self):
        with self.assertRaises(wave.WaveError):
            wave.wave_rel(Path(tempfile.gettempdir()) / "elsewhere", wave.REPO_ROOT, "x")


class OutlineTaskTests(unittest.TestCase):
    def test_type_quotas_sum_and_policies(self):
        targets = json.loads((wave.REPO_ROOT / "docs" / "content-wave-1" / "targets.json").read_text(encoding="utf-8"))
        remaining = {k: dict(v["levels"]) for k, v in targets["topics"].items()}
        existing_types = {"async": {"find-bug": 1, "open": 1, "single": 1, "multi": 1},
                          "concurrency": {"situation": 1}, "csharp": {"output": 1}}
        for topic, types in existing_types.items():
            remaining[topic]["middle"] -= sum(types.values())
        quotas = wave.type_quotas(targets, remaining, existing_types)
        for topic, levels in remaining.items():
            self.assertEqual(sum(levels.values()), sum(quotas[topic].values()), topic)
        totals = {t: sum(q[t] for q in quotas.values()) + sum(e.get(t, 0) for e in existing_types.values())
                  for t in targets["typeMix"]}
        self.assertEqual(targets["typeMix"], totals)
        for topic, spec in targets["topics"].items():
            q = quotas[topic]
            total = sum(q.values()) + sum(existing_types.get(topic, {}).values())
            if spec["peopleTopic"]:
                self.assertEqual(0, q["output"] + q["find-bug"], topic)
                self.assertGreaterEqual(q["situation"], 0.5 * total, topic)
            if spec["codeHeavy"]:
                code = q["output"] + q["find-bug"] + sum(existing_types.get(topic, {}).get(t, 0) for t in ("output", "find-bug"))
                self.assertGreaterEqual(code, 0.35 * total, topic)

    def test_build_outline_task(self):
        targets = json.loads((wave.REPO_ROOT / "docs" / "content-wave-1" / "targets.json").read_text(encoding="utf-8"))
        dump = {
            "questions": [{"path": "dotnet/async/seed.yaml", "document": {
                "slug": "seed", "topic": "async", "level": "middle", "type": "single",
                "locales": {"en": {"title": "Seed title"}}}}],
            "topics": {"dotnet": [{"key": k, "locales": {"en": {"title": k, "description": f"Covers {k}."}}}
                                  for k in targets["topics"]]},
            "redirects": {"dotnet": [{"from": "async/gone", "to": "async/seed"}]},
            "readIssues": [],
        }
        task = wave.build_outline_task(dump, targets, "abc123", wave.REPO_ROOT / "docs" / "content-wave-1",
                                       wave.REPO_ROOT)
        self.assertEqual("abc123", task["ref"])
        self.assertEqual("claude-opus-5-5", task["model"])
        self.assertEqual(32000, task["maxOutputTokens"])
        self.assertEqual(23, len(task["items"]))
        ids = [i["id"] for i in task["items"]]
        self.assertIn("outline-async", ids)
        item = next(i for i in task["items"] if i["id"] == "outline-async")
        self.assertEqual(["content/dotnet/topics.yaml", "content/dotnet/async/seed.yaml"], item["files"])
        self.assertIn("middle: 8", item["text"])
        self.assertIn("seed: Seed title", item["text"])
        self.assertIn("gone", item["text"])
        self.assertIn("Covers system-design.", item["text"])
        schema = task["outputSchema"]
        self.assertEqual(1, schema["properties"]["stubs"]["minItems"])
        self.assertFalse(schema["properties"]["stubs"]["items"]["additionalProperties"])

    def test_outline_apply_reads_results(self):
        targets = small_targets({"csharp": {"levels": {"junior": 1}}})
        tmp = Path(tempfile.mkdtemp())
        try:
            results = tmp / "results"
            results.mkdir()
            (results / "outline-csharp.json").write_text(json.dumps({"topic": "csharp", "stubs": [stub("a")]}), encoding="utf-8")
            outline_dir = tmp / "outline"
            written = wave.outline_apply(results, outline_dir, targets)
            self.assertEqual(["csharp"], written)
            saved = json.loads((outline_dir / "csharp.json").read_text(encoding="utf-8"))
            self.assertEqual("csharp-01", saved["stubs"][0]["stubId"])
        finally:
            shutil.rmtree(tmp, ignore_errors=True)



FIXTURES = Path(__file__).resolve().parent / "fixtures"

MODEL_YAML = """# yaml-language-server: $schema=../../schema/question.schema.json
slug: {slug}
stack: dotnet
topic: {topic}
level: {level}
type: {type}
grading: self
locales:
  en:
    title: Sample {slug}
meta:
  authors: [platonenkov]
  verified: 2026-11-13
"""


def model_yaml(record, **overrides):
    values = {k: record[k] for k in ("slug", "topic", "level", "type")}
    values.update(overrides)
    return MODEL_YAML.format(**values)


class WaveEnv:
    """A temp worktree with content/, a wave dir, state records and registered items."""

    def __init__(self, targets=None, wave="content-wave-1"):
        self.root = Path(tempfile.mkdtemp())
        self.worktree = self.root / "repo"
        self.wave_dir = self.worktree / "docs" / wave
        (self.worktree / "content" / "dotnet").mkdir(parents=True)
        self.wave_dir.mkdir(parents=True)
        self.targets = targets or small_targets({"csharp": {"levels": {"junior": 4}, "codeHeavy": True},
                                                 "testing": {"levels": {"junior": 4}}})
        self.state = {}
        self.registry = {}

    def add(self, stub_id, topic="testing", slug=None, level="junior", type_="open"):
        record = wave.new_record(stub_id, topic, stub(slug or stub_id, level, type_), wave.new_ulid())
        self.state[stub_id] = record
        return record

    def register(self, item_id, stubs, stage="gen"):
        self.registry[item_id] = {"stage": stage, "stubs": list(stubs), "task": "t", "applied": False}

    def save(self):
        wave.save_state(self.wave_dir / "state.json", self.state)
        wave.save_registry(self.wave_dir, self.registry)

    def load(self):
        self.state = wave.load_state(self.wave_dir / "state.json")
        self.registry = wave.load_registry(self.wave_dir)
        return self.state

    def batch(self, name, items):
        """items: {item_id: (status, problem, payload or text or None, suffix)} in the tasks_collect layout."""
        batch_dir = self.root / name
        (batch_dir / "results").mkdir(parents=True)
        index = []
        for item_id, (status, problem, payload, suffix) in items.items():
            index.append({"id": item_id, "status": status, "problem": problem, "outputTokens": 1234})
            if payload is not None:
                text = payload if isinstance(payload, str) else json.dumps(payload, ensure_ascii=False, indent=2)
                (batch_dir / "results" / f"{item_id}{suffix}").write_text(text, encoding="utf-8")
        (batch_dir / "index.json").write_text(json.dumps(index, indent=2), encoding="utf-8")
        return batch_dir

    def apply(self, batch_dir, stage="gen"):
        self.save()
        summary = wave.apply_batch(batch_dir, stage, self.wave_dir, self.worktree)
        self.load()
        return summary

    def cleanup(self):
        shutil.rmtree(self.root, ignore_errors=True)


def ok_item(*records, notes=()):
    return ("ok", None, {"questions": [{"stubId": r["stubId"], "yaml": model_yaml(r)} for r in records],
                         "notes": [{"stubId": s, "text": n} for s, n in notes]}, ".json")


class CliParsingTests(unittest.TestCase):
    def test_parse_issue_lines_multiline_snippet_mismatch(self):
        output = (FIXTURES / "cli" / "run-snippets-mismatch.stdout.txt").read_text(encoding="utf-8")
        issues = wave.parse_issue_lines(output)
        self.assertEqual(["content/dotnet/csharp/two-line-mismatch.yaml"], list(issues))
        found = issues["content/dotnet/csharp/two-line-mismatch.yaml"]
        self.assertEqual(["snippet-mismatch", "snippet-accepted-mismatch", "snippet-option-mismatch"],
                         [i["code"] for i in found])
        self.assertIn("actual output 'first\nsecond'", found[0]["message"])
        self.assertEqual("expect", found[0]["pointer"])
        self.assertEqual("options[1].expect", found[2]["pointer"])
        result = wave.snippet_result({"code": {"harness": "statements", "source": "x"},
                                      "expect": {"kind": "output", "value": "333"}}, found)
        self.assertEqual({"kind": "output", "value": "first\nsecond"}, result["actual"])
        self.assertTrue(result["executed"])

    def test_parse_issue_lines_real_validate_fixture(self):
        output = (FIXTURES / "cli" / "validate-errors.stdout.txt").read_text(encoding="utf-8")
        issues = wave.parse_issue_lines(output)
        self.assertEqual("yaml-parse", issues["content/dotnet/csharp/broken-yaml.yaml"][0]["code"])
        self.assertIsNone(issues["content/dotnet/csharp/broken-yaml.yaml"][0]["pointer"])
        self.assertEqual("links[0].url", issues["content/dotnet/csharp/bad-links.yaml"][0]["pointer"])
        valid = (FIXTURES / "cli" / "validate-mismatch.stdout.txt").read_text(encoding="utf-8")
        self.assertEqual({}, wave.parse_issue_lines(valid))

    def test_parse_issue_lines_unparsed_line_raises(self):
        with self.assertRaises(wave.WaveError):
            wave.parse_issue_lines("Something unexpected happened\n")
        with self.assertRaises(wave.WaveError):
            wave.parse_issue_lines("dotnet/a/b.yaml: snippet-mismatch: never closed\nstill open\n")

    def test_parse_link_lines_dead_cited_by_two(self):
        output = ("dead: https://example.invalid/x (DNS failure) cited by dotnet/a/one.yaml, dotnet/b/two.yaml\n"
                  "unknown: https://example.com/y (HTTP 403) cited by dotnet/a/one.yaml\n"
                  "Checked 3 URL(s): 1 ok, 1 dead, 1 unknown.\n")
        links = wave.parse_link_lines(output)
        self.assertEqual({"content/dotnet/a/one.yaml", "content/dotnet/b/two.yaml"}, set(links))
        self.assertEqual(["dead", "unknown"], [l["status"] for l in links["content/dotnet/a/one.yaml"]])
        self.assertEqual("DNS failure", links["content/dotnet/b/two.yaml"][0]["detail"])
        real = wave.parse_link_lines((FIXTURES / "cli" / "check-links-dead.stdout.txt").read_text(encoding="utf-8"))
        self.assertEqual("https://example.invalid/x", real["content/dotnet/csharp/bad-links.yaml"][0]["url"])
        with self.assertRaises(wave.WaveError):
            wave.parse_link_lines("dead https://x\n")

    def test_windows_paths_normalised(self):
        env = WaveEnv()
        try:
            record = env.add("testing-01")
            env.register("gen-testing-1", ["testing-01"])
            batch_dir = env.batch("msgbatch_win", {"gen-testing-1": ok_item(record)})
            windows = str(batch_dir).replace("/", "\\")
            loaded = wave.load_batch(windows + "\\index.json")
            self.assertEqual("msgbatch_win", loaded["batchId"])
            self.assertEqual(["gen-testing-1"], list(loaded["index"]))
            self.assertEqual("msgbatch_win", wave.load_batch(windows + "\\results")["batchId"])
            summary = env.apply(windows)
            self.assertEqual(["testing-01"], summary["written"])
        finally:
            env.cleanup()
        issues = wave.parse_issue_lines("dotnet\\csharp\\x.yaml: schema: bad [locales.ru]\n")
        self.assertEqual(["content/dotnet/csharp/x.yaml"], list(issues))
        self.assertEqual("content/dotnet/csharp/x.yaml", wave.norm_content_path("C:\\tmp\\root\\dotnet\\csharp\\x.yaml"))


class ValidateResultTests(unittest.TestCase):
    def setUp(self):
        self.item = {"stage": "gen", "stubs": ["t-01", "t-02"]}

    def result(self, payload, status="ok", problem=None):
        text = payload if isinstance(payload, str) else json.dumps(payload)
        return {"id": "gen-t-1", "status": status, "problem": problem, "text": text, "file": None}

    def test_validate_result_missing_stub(self):
        payload = {"questions": [{"stubId": "t-01", "yaml": "slug: a\n"}], "notes": []}
        with self.assertRaises(wave.ResultError) as caught:
            wave.validate_result(self.result(payload), self.item, "gen")
        self.assertIn("missing t-02", str(caught.exception))

    def test_validate_result_duplicate_stub(self):
        payload = {"questions": [{"stubId": "t-01", "yaml": "a"}, {"stubId": "t-01", "yaml": "b"},
                                 {"stubId": "t-02", "yaml": "c"}], "notes": []}
        with self.assertRaises(wave.ResultError) as caught:
            wave.validate_result(self.result(payload), self.item, "gen")
        self.assertEqual("stub-mismatch", caught.exception.reason)
        self.assertIn("duplicate t-01", str(caught.exception))

    def test_validate_result_unknown_stub_and_schema(self):
        payload = {"questions": [{"stubId": "t-01", "yaml": "a"}, {"stubId": "t-02", "yaml": "b"},
                                 {"stubId": "x-09", "yaml": "c"}], "notes": []}
        with self.assertRaises(wave.ResultError):
            wave.validate_result(self.result(payload), self.item, "gen")
        with self.assertRaises(wave.ResultError) as caught:
            wave.validate_result(self.result({"questions": [{"stubId": "t-01"}], "notes": []}), self.item, "gen")
        self.assertEqual("schema", caught.exception.reason)

    def test_validate_result_strips_fence_with_warning(self):
        payload = {"questions": [{"stubId": "t-01", "yaml": "```yaml\nslug: a\n```"},
                                 {"stubId": "t-02", "yaml": "slug: b\n"}], "notes": [{"stubId": "t-02", "text": "n"}]}
        fenced = "```json\n" + json.dumps(payload) + "\n```"
        entries, warnings = wave.validate_result(
            self.result(fenced, status="failed", problem="answer is not valid JSON"), self.item, "gen")
        self.assertEqual("slug: a", entries[0]["yaml"])
        self.assertEqual(["n"], entries[1]["notes"])
        self.assertEqual(2, len(warnings))
        self.assertTrue(any("answer" in w for w in warnings))

    def test_validate_result_classifies_failures(self):
        for problem, reason in (("stop_reason=max_tokens", "max_tokens"), ('stop_reason="refusal"', "refusal"),
                                ("no result returned", "no-result"), ('{"type":"expired"}', "expired"),
                                ('{"type":"errored"}', "error")):
            with self.assertRaises(wave.ResultError) as caught:
                wave.validate_result(self.result("{}", status="failed", problem=problem), self.item, "gen")
            self.assertEqual(reason, caught.exception.reason, problem)


class ApplyTests(unittest.TestCase):
    def setUp(self):
        self.env = WaveEnv()

    def tearDown(self):
        self.env.cleanup()

    def test_apply_is_idempotent_on_replay(self):
        first = self.env.add("testing-01", type_="open")
        second = self.env.add("testing-02", type_="single")
        self.env.register("gen-testing-1", ["testing-01", "testing-02"])
        batch_dir = self.env.batch("msgbatch_a", {"gen-testing-1": ok_item(first, second, notes=[("testing-01", "unsure")])})
        summary = self.env.apply(batch_dir)
        self.assertEqual(["testing-01", "testing-02"], summary["written"])
        path = self.env.worktree / first["path"]
        text = path.read_text(encoding="utf-8")
        self.assertTrue(text.startswith(wave.SCHEMA_COMMENT + "\nid: " + first["id"] + "\nslug: testing-01\n"))
        record = self.env.state["testing-01"]
        self.assertEqual("generated", record["status"])
        self.assertEqual(wave.content_hash(text), record["contentHash"])
        self.assertEqual("msgbatch_a", record["yamlHistory"][0]["batchId"])
        self.assertEqual("unsure", record["notes"][0]["text"])
        self.env.registry["gen-testing-1"]["applied"] = False
        replay = self.env.apply(batch_dir)
        self.assertEqual([], replay["written"])
        self.assertEqual(["testing-01", "testing-02"], replay["skipped"])
        self.assertEqual(1, len(self.env.state["testing-01"]["yamlHistory"]))
        self.assertEqual(text, path.read_text(encoding="utf-8"))

    def test_apply_rejects_model_id_line(self):
        record = self.env.add("testing-01")
        self.env.register("gen-testing-1", ["testing-01"])
        yaml = model_yaml(record).replace("slug:", "id: 01KBZ3Q8N9V2K3M4P5R6S7T8W9\nslug:", 1)
        batch_dir = self.env.batch("msgbatch_b", {"gen-testing-1": (
            "ok", None, {"questions": [{"stubId": "testing-01", "yaml": yaml}], "notes": []}, ".json")})
        summary = self.env.apply(batch_dir)
        self.assertEqual([], summary["written"])
        self.assertIn("id-line", summary["failed"][0])
        self.assertEqual("gen-failed", self.env.state["testing-01"]["status"])
        self.assertFalse((self.env.worktree / record["path"]).exists())

    def test_apply_truncated_item_marks_gen_failed(self):
        self.env.add("testing-01")
        self.env.add("testing-02", type_="single")
        self.env.register("gen-testing-1", ["testing-01", "testing-02"])
        batch_dir = self.env.batch("msgbatch_c", {"gen-testing-1": (
            "failed", "stop_reason=max_tokens", '{"questions": [{"stubId": "testing-01", "yaml": "slug', ".raw.txt")})
        summary = self.env.apply(batch_dir)
        self.assertEqual(2, len(summary["failed"]))
        for stub_id in ("testing-01", "testing-02"):
            record = self.env.state[stub_id]
            self.assertEqual("gen-failed", record["status"])
            self.assertEqual("max_tokens", record["failures"][0]["reason"])
        self.env.registry["gen-testing-1"]["applied"] = False
        self.env.apply(batch_dir)
        self.assertEqual(1, len(self.env.state["testing-01"]["failures"]))

    def test_apply_rejects_level_change(self):
        record = self.env.add("testing-01", level="junior")
        self.env.register("gen-testing-1", ["testing-01"])
        batch_dir = self.env.batch("msgbatch_d", {"gen-testing-1": (
            "ok", None, {"questions": [{"stubId": "testing-01", "yaml": model_yaml(record, level="senior")}],
                         "notes": []}, ".json")})
        summary = self.env.apply(batch_dir)
        self.assertIn("stub-drift", summary["failed"][0])
        self.assertIn("level", summary["failed"][0])
        self.assertEqual("gen-failed", self.env.state["testing-01"]["status"])

    def test_apply_refuses_unregistered_item(self):
        self.env.add("testing-01")
        batch_dir = self.env.batch("msgbatch_e", {"gen-other-1": ("ok", None, {"questions": [], "notes": []}, ".json")})
        with self.assertRaises(wave.WaveError):
            self.env.apply(batch_dir)

    def test_retry_plan_splits_once(self):
        self.env.add("testing-01", type_="open")
        second = self.env.add("testing-02", type_="single")
        self.env.register("gen-testing-1", ["testing-01", "testing-02"])
        self.env.apply(self.env.batch("msgbatch_f", {"gen-testing-1": ("failed", "stop_reason=max_tokens", None, "")}))
        (self.env.worktree / "docs/content-wave-1/outline").mkdir(parents=True, exist_ok=True)
        (self.env.worktree / "docs/content-wave-1/outline/testing.json").write_text("{}", encoding="utf-8")
        tasks, entries = wave.build_gen_tasks(self.env.state, self.env.targets, "abc", self.env.worktree,
                                              self.env.wave_dir, self.env.registry, None, None, "2026-11-13",
                                              lister=lambda *a: [], retry=True)
        self.assertEqual(1, len(tasks))
        self.assertEqual(2, len(tasks[0]["items"]))
        self.assertEqual({"gen-testing-2": ["testing-01"], "gen-testing-3": ["testing-02"]},
                         {k: v["stubs"] for k, v in entries.items()})
        for item_id, entry in entries.items():
            self.env.registry[item_id] = {**entry, "task": "retry", "applied": False}
        batch_dir = self.env.batch("msgbatch_g", {
            "gen-testing-2": ("failed", "stop_reason=max_tokens", None, ""),
            "gen-testing-3": ok_item(second)})
        self.env.apply(batch_dir)
        self.assertEqual("shortfall", self.env.state["testing-01"]["status"])
        self.assertEqual("generated", self.env.state["testing-02"]["status"])
        self.assertFalse(any(r["status"] == "gen-failed" for r in self.env.state.values()))


class GenTaskTests(unittest.TestCase):
    def test_gen_chunking_and_items(self):
        env = WaveEnv()
        try:
            for i, (topic, level, type_) in enumerate([("csharp", "junior", "output"), ("csharp", "junior", "open"),
                                                       ("testing", "junior", "open"), ("testing", "junior", "open"),
                                                       ("testing", "junior", "single"), ("testing", "lead", "situation")], 1):
                env.add(f"{topic}-{i:02d}", topic=topic, level=level, type_=type_)
            outline = env.worktree / "docs/content-wave-1/outline"
            outline.mkdir(parents=True)
            for topic in ("csharp", "testing"):
                (outline / f"{topic}.json").write_text("{}", encoding="utf-8")
            for shared in wave.gen_shared(env.wave_dir, env.worktree):
                (env.worktree / shared).parent.mkdir(parents=True, exist_ok=True)
                (env.worktree / shared).write_text("x" * 1000, encoding="utf-8")
            tasks, entries = wave.build_gen_tasks(env.state, env.targets, "abc", env.worktree, env.wave_dir, {},
                                                  None, None, "2026-11-13", lister=lambda *a: [])
            stubs = sorted(e["stubs"] for e in entries.values())
            self.assertEqual([["csharp-01"], ["csharp-02"], ["testing-03", "testing-05"], ["testing-04"],
                              ["testing-06"]], stubs)
            self.assertEqual([], wave.check_task_file(tasks[0], env.worktree))
            item = next(i for i in tasks[0]["items"] if entries[i["id"]]["stubs"] == ["testing-03", "testing-05"])
            self.assertIn('"stubId": "testing-03"', item["text"])
            self.assertEqual(["docs/content-wave-1/outline/testing.json"], item["files"])
            split, _ = wave.build_gen_tasks(env.state, env.targets, "abc", env.worktree, env.wave_dir, {}, None, 0.70,
                                            "2026-11-13", lister=lambda *a: [])
            self.assertGreater(len(split), 1)
            registry = {k: {**v, "applied": False} for k, v in entries.items()}
            with self.assertRaises(wave.WaveError):
                wave.build_gen_tasks(env.state, env.targets, "abc", env.worktree, env.wave_dir, registry, None, None,
                                     "2026-11-13", lister=lambda *a: [])
        finally:
            env.cleanup()


def parity_doc(ru, en, **extra):
    base = {"title": "T", "prompt": "P", "answer": "A", "explanation": "E"}
    ru_loc = {k: "Текст" for k in base}
    en_loc = dict(base)
    ru_loc.update(ru)
    en_loc.update(en)
    doc = {"locales": {"ru": ru_loc, "en": en_loc}}
    doc.update(extra)
    return doc


class ParityTests(unittest.TestCase):
    def failures(self, doc):
        return [f for f in wave.parity(doc) if f["severity"] == "failure"]

    def test_parity_clean(self):
        doc = parity_doc({"answer": "Метод `Task.Run` вернёт 3 задачи в .NET 10."},
                         {"answer": "The `Task.Run` method returns 3 tasks in .NET 10."})
        self.assertEqual([], self.failures(doc))

    def test_parity_code_spans_differ(self):
        doc = parity_doc({"answer": "Вызов `Task.Run` возвращает задачу."},
                         {"answer": "Calling `Task.Factory.StartNew` returns a task."})
        found = self.failures(doc)
        self.assertEqual(["answer"], [f["field"] for f in found])
        self.assertIn("code spans", found[0]["message"])

    def test_parity_numbers_differ(self):
        doc = parity_doc({"prompt": "Сколько будет 2 задачи и 1,5 секунды?"},
                         {"prompt": "How many with 3 tasks and 1.5 seconds?"})
        found = self.failures(doc)
        self.assertEqual(1, len(found))
        self.assertIn("numbers", found[0]["message"])
        same = parity_doc({"prompt": "Сколько: `x = 7` и 1,5 секунды?"},
                          {"prompt": "How much: `x = 7` and 1.5 seconds?"})
        self.assertEqual([], self.failures(same))

    def test_parity_cyrillic_in_en(self):
        doc = parity_doc({}, {"explanation": "Explained with слово."})
        found = self.failures(doc)
        self.assertIn("locales.en.explanation", [f["field"] for f in found])
        no_ru = parity_doc({"title": "Only English"}, {})
        self.assertIn("locales.ru.title", [f["field"] for f in self.failures(no_ru)])

    def test_parity_numbers_follow_locale_grouping(self):
        grouped = parity_doc({"explanation": "Порог 85 000 байт, а ещё 5 000 строк и 1,5 секунды."},
                             {"explanation": "The threshold is 85,000 bytes, plus 5,000 rows and 1.5 seconds."})
        self.assertEqual([], self.failures(grouped))
        differs = parity_doc({"explanation": "Порог 85 000 байт."},
                             {"explanation": "The threshold is 80,000 bytes."})
        self.assertIn("numbers", self.failures(differs)[0]["message"])

    def test_parity_numbers_accept_ordinals_and_identical_values(self):
        ordinal = parity_doc({"explanation": "Руководство (2-е издание 2008 года)."},
                             {"explanation": "The guidelines (2nd edition from 2008)."})
        self.assertEqual([], self.failures(ordinal))
        same = parity_doc({"answer": "1,2"}, {"answer": "1,2"})
        self.assertEqual([], self.failures(same))

    def test_parity_language_neutral_answer_needs_no_cyrillic(self):
        same = parity_doc({"answer": "True False"}, {"answer": "True False"})
        self.assertEqual([], self.failures(same))
        differs = parity_doc({"answer": "Only English here"}, {"answer": "Only English text"})
        self.assertIn("locales.ru.answer", [f["field"] for f in self.failures(differs)])
        long_same = "Some English sentence that is long enough to be prose and not a value"
        prose = parity_doc({"answer": long_same}, {"answer": long_same})
        self.assertIn("locales.ru.answer", [f["field"] for f in self.failures(prose)])

    def test_parity_latin_word_in_ru_is_warning(self):
        doc = parity_doc({"answer": "Это blabla в тексте и middleware."}, {"answer": "This is text with middleware."})
        warnings = [f for f in wave.parity(doc) if f["severity"] == "warning"]
        self.assertEqual(1, len(warnings))
        self.assertIn("blabla", warnings[0]["message"])

    def test_dash_scan_reports_field_and_line(self):
        doc = {"code": {"source": "int a = 1;\nint b = 2;\n// a \u2014 b\n"},
               "locales": {"en": {"title": "Fine", "options": {"a": "x \u2013 y"}}}}
        found = wave.dash_scan(doc)
        self.assertEqual(2, len(found))
        self.assertIn("code.source:3", found[0])
        self.assertIn("U+2014", found[0])
        self.assertIn("locales.en.options.a:1", found[1])


class LinksTests(unittest.TestCase):
    def test_links_dead_needs_two_failures(self):
        env = WaveEnv()
        try:
            record = env.add("testing-01")
            url = "https://learn.microsoft.com/en-us/x"
            docs = {record["path"]: {"links": [{"url": url}],
                                     "locales": {"en": {"title": "Sample", "prompt": "", "answer": "", "explanation": ""}}}}
            start = wave.parse_utc("2026-11-13T10:00:00Z")

            def fail(u):
                return {"status": 404, "finalUrl": u, "html": "", "error": "HTTP 404"}

            summary = wave.links_fetch(env.state, docs, env.wave_dir, env.worktree, fetch=fail, now=lambda: start)
            self.assertEqual([url], summary["suspect"])
            summary = wave.links_fetch(env.state, docs, env.wave_dir, env.worktree, fetch=fail,
                                       now=lambda: start + wave.dt.timedelta(minutes=10))
            self.assertEqual([url], summary["suspect"])
            summary = wave.links_fetch(env.state, docs, env.wave_dir, env.worktree, fetch=fail,
                                       now=lambda: start + wave.dt.timedelta(minutes=40))
            self.assertEqual([url], summary["dead"])
            self.assertEqual("dead", env.state["testing-01"]["evidence"]["links"][0]["status"])

            def blocked(u):
                return {"status": 403, "finalUrl": u, "html": "", "error": "HTTP 403"}

            other = "https://example.com/y"
            docs[record["path"]]["links"] = [{"url": other}]
            for minutes in (0, 60):
                summary = wave.links_fetch(env.state, docs, env.wave_dir, env.worktree, fetch=blocked,
                                           now=lambda: start + wave.dt.timedelta(minutes=minutes))
            self.assertEqual([other], summary["unknown"])
        finally:
            env.cleanup()

    def test_page_text_ignores_svg_titles(self):
        html = ("<html><head><title>How Async Works - .NET Blog</title></head><body>"
                "<svg><title>Your Privacy Choices Opt-Out Icon</title></svg><p>Body</p></body></html>")
        title, text = wave.page_text(html)
        self.assertEqual("How Async Works - .NET Blog", title)
        self.assertNotIn("Opt-Out", text)
        head_svg = "<html><head><svg><title>Icon</title></svg><title>Real Title</title></head><body></body></html>"
        self.assertEqual("Real Title", wave.page_text(head_svg)[0])

    def test_links_fetch_drops_links_no_longer_cited(self):
        env = WaveEnv()
        try:
            record = env.add("testing-01")
            old_url = "https://learn.microsoft.com/en-us/dotnet/old"
            new_url = "https://learn.microsoft.com/en-us/dotnet/new"
            record["evidence"]["links"] = [{"url": old_url, "status": "suspect", "title": None}]
            docs = {record["path"]: {"links": [{"url": new_url}], "locales": {"en": {
                "title": "T", "prompt": "", "answer": "", "explanation": ""}}}}

            def fetch(u):
                return {"status": 200, "finalUrl": u, "html": "<title>New</title>", "error": None}

            wave.links_fetch(env.state, docs, env.wave_dir, env.worktree, fetch=fetch)
            self.assertEqual([new_url], [l["url"] for l in record["evidence"]["links"]])
        finally:
            env.cleanup()

    def test_links_fetch_stores_title_excerpt_and_redirect_warning(self):
        env = WaveEnv()
        try:
            record = env.add("testing-01")
            url = "https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.run"
            docs = {record["path"]: {"links": [{"url": url}], "locales": {"en": {
                "title": "When does Task.Run help", "prompt": "Use `Task.Run`.", "answer": "", "explanation": ""}}}}
            html = ("<html><head><title>Task.Run Method</title><script>var x;</script></head><body>"
                    + "filler " * 2000 + "Task.Run queues work " + "tail " * 2000 + "</body></html>")

            def fetch(u):
                return {"status": 200, "finalUrl": "https://learn.microsoft.com/en-us/dotnet/", "html": html, "error": None}

            evidence = wave.link_file(env.wave_dir, url)
            evidence.parent.mkdir(parents=True)
            evidence.write_text(json.dumps({"url": url, "history": [], "title": None, "finalUrl": None,
                                            "excerpts": {"testing-01": "old page text"}}), encoding="utf-8")
            summary = wave.links_fetch(env.state, docs, env.wave_dir, env.worktree, fetch=fetch)
            raw = evidence.read_text(encoding="utf-8")
            data = json.loads(raw)
            self.assertEqual("Task.Run Method", data["title"])
            self.assertNotIn("excerpts", data)
            self.assertNotIn("Task.Run queues work", raw)
            self.assertNotIn("old page text", raw)
            cached = json.loads(wave.excerpt_cache_file(env.wave_dir, url).read_text(encoding="utf-8"))["testing-01"]
            self.assertIn("Task.Run queues work", cached)
            self.assertLessEqual(len(cached), wave.EXCERPT_CHARS)
            self.assertNotIn("var x", cached)
            self.assertEqual(wave.sha256_bytes(cached.encode("utf-8")), data["excerptSha256"]["testing-01"])
            self.assertTrue(wave.excerpt_cache_file(env.wave_dir, url).is_relative_to(env.wave_dir / ".local"))
            self.assertIn("landing page", summary["warnings"][0])
        finally:
            env.cleanup()

    def test_link_lines_read_the_local_excerpt_and_refuse_a_missing_or_altered_one(self):
        env = WaveEnv()
        try:
            record = env.add("testing-01")
            url = "https://learn.microsoft.com/en-us/x"
            doc = {"links": [{"url": url}], "locales": {"en": {"title": "Task.Run", "prompt": "", "answer": "",
                                                               "explanation": ""}}}

            def fetch(u):
                return {"status": 200, "finalUrl": u, "html": "<title>X</title><p>page body</p>", "error": None}

            wave.links_fetch(env.state, {record["path"]: doc}, env.wave_dir, env.worktree, fetch=fetch)
            self.assertIn("  excerpt: page body", "\n".join(wave._link_lines(record, doc, env.wave_dir)))
            cache = wave.excerpt_cache_file(env.wave_dir, url)
            cache.write_text(json.dumps({"testing-01": "edited"}), encoding="utf-8")
            with self.assertRaises(wave.WaveError):
                wave._link_lines(record, doc, env.wave_dir)
            cache.unlink()
            with self.assertRaises(wave.WaveError) as caught:
                wave._link_lines(record, doc, env.wave_dir)
            self.assertIn("run links-fetch", str(caught.exception))
        finally:
            env.cleanup()

    def test_redact_task_replaces_every_excerpt_line_with_its_hash(self):
        task = {"items": [{"id": "a", "text": "Links:\n- https://x: status ok\n  excerpt: secret page text\nend"},
                          {"id": "b", "text": "  excerpt: other"}]}
        redacted = wave.redact_task(task)
        marker = wave.redaction_marker("secret page text")
        self.assertEqual(f"Links:\n- https://x: status ok\n  excerpt: {marker}\nend", redacted["items"][0]["text"])
        self.assertEqual("  excerpt: " + wave.redaction_marker("other"), redacted["items"][1]["text"])
        self.assertIn("secret page text", task["items"][0]["text"])

    def test_write_task_keeps_the_full_task_local_and_commits_a_redacted_copy(self):
        from unittest import mock
        env = WaveEnv()
        try:
            (env.wave_dir / "ledger.json").write_text("{}", encoding="utf-8")
            task = {"ref": "abc", "sharedFiles": [], "items": [{"id": "a", "text": "  excerpt: page text"}]}
            args = type("Args", (), {"overwrite": False, "worktree": str(env.worktree), "server_repo": str(env.worktree)})
            with mock.patch.object(wave, "check_task_file", return_value=[]), \
                    mock.patch.object(wave, "preflight", return_value=[]):
                path = wave.write_task(task, "verify-r1", args, env.wave_dir)
                with self.assertRaises(wave.WaveError):
                    wave.write_task(task, "verify-r1", args, env.wave_dir)
            self.assertEqual(env.wave_dir / ".local" / "tasks" / "verify-r1.json", path)
            self.assertIn("page text", path.read_text(encoding="utf-8"))
            audit = env.wave_dir / "tasks" / "verify-r1.json"
            self.assertNotIn("page text", audit.read_text(encoding="utf-8"))
            entry = json.loads((env.wave_dir / "ledger.json").read_text(encoding="utf-8"))["taskFiles"][0]
            self.assertEqual(wave.task_sha256(path), entry["sha256"])
            self.assertEqual("docs/content-wave-1/tasks/verify-r1.json", entry["auditFile"])
            self.assertEqual(wave.task_sha256(audit), entry["auditSha256"])
        finally:
            env.cleanup()


class FakeCli:
    """Replays CLI outputs: {command: [(returncode, stdout, stderr), ...]} consumed in order (last one repeats)."""

    def __init__(self, outputs):
        self.outputs = {k: list(v) for k, v in outputs.items()}
        self.calls = []

    def __call__(self, args):
        self.calls.append(args)
        queue = self.outputs[args[0]]
        code, out, err = queue.pop(0) if len(queue) > 1 else queue[0]
        return subprocess.CompletedProcess(args, code, out, err)


VALID_OUT = "Content is valid: 1 question(s), hash ab12.\n"
SNIPPETS_OUT = "All runnable snippets match their declared results.\n"
LINKS_OUT = "Checked 0 URL(s): 0 ok, 0 dead, 0 unknown.\n"


def dump_json(questions, read_issues=()):
    return json.dumps({"hash": "h", "questions": questions, "topics": {}, "redirects": {},
                       "readIssues": list(read_issues)})


class MechTests(unittest.TestCase):
    def test_mech_quarantine_keeps_text_and_path(self):
        env = WaveEnv()
        try:
            record = env.add("testing-01")
            broken = wave.SCHEMA_COMMENT + "\nid: " + record["id"] + "\nslug: testing-01\n  level: [junior\n"
            (env.worktree / record["path"]).parent.mkdir(parents=True, exist_ok=True)
            (env.worktree / record["path"]).write_text(broken, encoding="utf-8")
            cli = FakeCli({
                "dump": [(0, dump_json([], [{"path": "dotnet\\testing\\testing-01.yaml", "code": "yaml-parse",
                                            "message": "bad", "pointer": None}]), ""), (0, dump_json([]), "")],
                "validate": [(0, "Content is valid: 0 question(s), hash ab12.\n", "")],
                "run-snippets": [(0, "All runnable snippets match their declared results.\n", "")],
                "check-links": [(0, "Checked 0 URL(s): 0 ok, 0 dead, 0 unknown.\n", "")]})
            summary = wave.mech(env.state, env.worktree, env.wave_dir, cli)
            self.assertEqual(["testing-01"], summary["quarantined"])
            self.assertFalse((env.worktree / record["path"]).exists())
            saved = env.wave_dir / "quarantine" / "testing-01.yaml.txt"
            self.assertEqual(broken, saved.read_text(encoding="utf-8"))
            self.assertEqual("quarantined", record["status"])
            self.assertEqual("content/dotnet/testing/testing-01.yaml", record["path"])
            self.assertFalse(record["evidence"]["mech"]["ok"])
        finally:
            env.cleanup()

    def test_mech_sandbox_failure_stops(self):
        env = WaveEnv()
        try:
            cli = FakeCli({"dump": [(0, dump_json([]), "")], "validate": [(0, VALID_OUT, "")],
                           "run-snippets": [(3, "", "docker down")], "check-links": [(0, LINKS_OUT, "")]})
            with self.assertRaises(wave.WaveError) as caught:
                wave.mech(env.state, env.worktree, env.wave_dir, cli)
            self.assertIn("exit 3", str(caught.exception))
        finally:
            env.cleanup()

    def test_mech_writes_evidence_per_hash(self):
        env = WaveEnv()
        try:
            record = env.add("csharp-01", topic="csharp", type_="output")
            text = wave.SCHEMA_COMMENT + "\nid: " + record["id"] + "\n" + model_yaml(record).split("\n", 1)[1]
            path = env.worktree / record["path"]
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8")
            doc = {"id": record["id"], "slug": "csharp-01", "stack": "dotnet", "topic": "csharp", "level": "junior",
                   "type": "output", "code": {"harness": "statements", "source": "x"},
                   "expect": {"kind": "output", "value": "333"}, "links": [],
                   "locales": {"ru": {"title": "Т", "prompt": "П", "answer": "333", "explanation": "О \u2014 е"},
                               "en": {"title": "T", "prompt": "P", "answer": "333", "explanation": "E"}},
                   "meta": {}}
            mismatch = (FIXTURES / "cli" / "run-snippets-mismatch.stdout.txt").read_text(encoding="utf-8")
            mismatch = mismatch.replace("two-line-mismatch", "csharp-01")
            cli = FakeCli({"dump": [(0, dump_json([{"path": "dotnet/csharp/csharp-01.yaml", "document": doc}]), "")],
                           "validate": [(0, VALID_OUT, "")], "run-snippets": [(1, mismatch, "")],
                           "check-links": [(0, LINKS_OUT, "")]})
            summary = wave.mech(env.state, env.worktree, env.wave_dir, cli)
            self.assertEqual(["csharp-01"], summary["failed"])
            mech = record["evidence"]["mech"]
            self.assertEqual(wave.content_hash(text), mech["hash"])
            evidence = json.loads((env.wave_dir / "evidence" / f"mech-{mech['hash']}.json").read_text(encoding="utf-8"))
            self.assertEqual("first\nsecond", evidence["result"]["actual"]["value"])
            self.assertTrue(any("locales.ru.explanation:1" in f for f in evidence["failures"]))
        finally:
            env.cleanup()


def e2e_doc(record, title="T", mismatch=False, code=None):
    return {"stack": "dotnet", "topic": record["topic"], "slug": record["slug"], "level": record["level"],
            "type": record["type"], "mismatch": mismatch, "links": [], "code": code,
            "locales": {"ru": {"title": f"Вопрос {title}", "prompt": "Текст", "answer": "Ответ",
                               "explanation": "Пояснение"},
                        "en": {"title": f"Question {title}", "prompt": "Text", "answer": "Answer",
                               "explanation": "Explanation"}},
            "meta": {}}


def e2e_yaml(record, **doc_args):
    doc = e2e_doc(record, **doc_args)
    return (f"{wave.SCHEMA_COMMENT}\n# doc: {json.dumps(doc, ensure_ascii=False)}\nslug: {record['slug']}\n"
            f"stack: dotnet\ntopic: {record['topic']}\nlevel: {record['level']}\ntype: {record['type']}\n"
            f"meta:\n  authors: [platonenkov]\n  verified: 2026-11-13\n")


class ContentFakeCli:
    """Stands in for Trainer.ContentCli: the document of each file is the JSON after `# doc:`, the id its id line."""

    def __init__(self, worktree):
        self.worktree = Path(worktree)

    def docs(self, root):
        questions, issues = [], []
        for path in sorted(Path(root).rglob("*.yaml")):
            if path.name in ("topics.yaml", "redirects.yaml"):
                continue
            relative = path.relative_to(root).as_posix()
            text = path.read_text(encoding="utf-8")
            doc_line = re.search(r"^# doc: (.*)$", text, re.M)
            id_line = re.search(r"^id: (\S+)$", text, re.M)
            if "BROKEN" in text or not doc_line or not id_line:
                issues.append({"path": relative, "code": "yaml-parse", "message": "broken", "pointer": None})
                continue
            document = json.loads(doc_line.group(1))
            document["id"] = id_line.group(1)
            verified = re.search(r"^  verified: (\S+)$", text, re.M)
            document["meta"] = {**(document.get("meta") or {}), "verified": verified.group(1) if verified else None}
            questions.append({"path": relative, "sha256": "x", "document": document})
        return questions, issues

    def __call__(self, args, worktree=None):
        command = args[0]
        root = args[-1]
        questions, issues = self.docs(root)
        if command == "dump":
            out = json.dumps({"hash": "h", "questions": questions, "topics": {}, "redirects": {}, "readIssues": issues})
            return subprocess.CompletedProcess(args, 0, out, "")
        if command == "validate":
            return subprocess.CompletedProcess(args, 0, f"Content is valid: {len(questions)} question(s), hash ab.\n", "")
        if command == "run-snippets":
            lines = [f"{q['path']}: snippet-mismatch: Declared output '1', actual output '2'. [expect]"
                     for q in questions if q["document"].get("mismatch")]
            if lines:
                return subprocess.CompletedProcess(args, 1, "\n".join(lines) + "\n", "1 snippet issue(s) found.")
            return subprocess.CompletedProcess(args, 0, "All runnable snippets match their declared results.\n", "")
        if command == "check-links":
            return subprocess.CompletedProcess(args, 0, "Checked 0 URL(s): 0 ok, 0 dead, 0 unknown.\n", "")
        raise AssertionError(command)


class VerifyEnv(WaveEnv):
    def write_question(self, record, text=None, ok=True, status="generated", result=None):
        text = text or e2e_yaml(record)
        text = text.replace(wave.SCHEMA_COMMENT + "\n", wave.SCHEMA_COMMENT + "\nid: " + record["id"] + "\n", 1)
        path = self.worktree / record["path"]
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        digest = wave.content_hash(text)
        record["status"] = status
        record["contentHash"] = digest
        record["evidence"]["mech"] = {"hash": digest, "ok": ok, "issues": [] if ok else ["snippet-mismatch: x"],
                                      "warnings": [], "result": result or {"executed": False, "skipped": "no code"},
                                      "utc": "2026-11-13T10:00:00Z"}
        return text

    def register_verify(self, item_id, stubs, round_=1):
        self.registry[item_id] = {"stage": "verify", "round": round_, "stubs": list(stubs), "task": "v",
                                  "applied": False, "hashes": {s: self.state[s]["contentHash"] for s in stubs}}

    def verdicts(self, batch_dir, round_=1):
        self.save()
        summary = wave.apply_verdicts(batch_dir, round_, self.wave_dir, self.worktree, today="2026-11-14")
        self.load()
        return summary


def verdict(stub_id, kind="pass", *issues):
    return {"stubId": stub_id, "verdict": kind, "issues": [
        {"severity": sev, "field": field, "claim": "c", "problem": "p", "suggestedFix": fix}
        for sev, field, fix in issues]}


def verdict_item(*verdicts):
    return ("ok", None, {"verdicts": list(verdicts)}, ".json")


class VerdictTests(unittest.TestCase):
    def setUp(self):
        self.env = VerifyEnv()
        self.a = self.env.add("testing-01")
        self.b = self.env.add("testing-02", type_="single")
        self.env.write_question(self.a)
        self.env.write_question(self.b)

    def tearDown(self):
        self.env.cleanup()

    def test_verdicts_one_per_expected_stub(self):
        self.env.register_verify("verify-r1-testing-1", ["testing-01", "testing-02"])
        summary = self.env.verdicts(self.env.batch("msgbatch_v1", {"verify-r1-testing-1": verdict_item(verdict("testing-01"))}))
        self.assertEqual(["testing-01"], summary["verify-pass"])
        self.assertEqual(["testing-02"], summary["unverified"])
        self.assertIn("verified: 2026-11-14", (self.env.worktree / self.a["path"]).read_text(encoding="utf-8"))
        self.assertEqual(self.env.state["testing-01"]["contentHash"],
                         wave.file_hash(self.env.worktree, self.env.state["testing-01"]))
        other = VerifyEnv()
        try:
            for record in (other.add("testing-01"), other.add("testing-02")):
                other.write_question(record)
            other.register_verify("verify-r1-testing-1", ["testing-01", "testing-02"])
            summary = other.verdicts(other.batch("msgbatch_v2", {"verify-r1-testing-1": verdict_item(
                verdict("testing-01"), verdict("testing-01"), verdict("testing-02"))}))
            self.assertEqual(["testing-01"], summary["unverified"])
            self.assertEqual(["testing-02"], summary["verify-pass"])
        finally:
            other.cleanup()

    def test_verdict_stale_hash_ignored(self):
        self.env.register_verify("verify-r1-testing-1", ["testing-01", "testing-02"])
        path = self.env.worktree / self.a["path"]
        path.write_text(path.read_text(encoding="utf-8").replace("Question T", "Question T2"), encoding="utf-8")
        summary = self.env.verdicts(self.env.batch("msgbatch_v3", {"verify-r1-testing-1": verdict_item(
            verdict("testing-01"), verdict("testing-02"))}))
        self.assertEqual(["testing-01"], summary["stale"])
        self.assertEqual("generated", self.env.state["testing-01"]["status"])
        self.assertTrue(self.env.state["testing-01"]["verdicts"][0]["stale"])
        self.assertEqual("verify-pass", self.env.state["testing-02"]["status"])

    def test_verdict_pass_with_major_is_invalid(self):
        self.env.register_verify("verify-r1-testing-1", ["testing-01", "testing-02"])
        summary = self.env.verdicts(self.env.batch("msgbatch_v4", {"verify-r1-testing-1": verdict_item(
            verdict("testing-01", "pass", ("major", "locales.en.answer", "x")),
            verdict("testing-02", "pass", ("minor", "locales.ru.title", "y")))}))
        self.assertEqual(["testing-01"], summary["unverified"])
        self.assertEqual(["testing-02"], summary["verify-pass"])

    def test_verdict_pass_with_unsure_is_invalid(self):
        self.env.register_verify("verify-r1-testing-1", ["testing-01", "testing-02"])
        summary = self.env.verdicts(self.env.batch("msgbatch_v5", {"verify-r1-testing-1": verdict_item(
            verdict("testing-01", "pass", ("unsure", "links[0]", "x")), verdict("testing-02", "fix", ("major", "hint", "z")))}))
        self.assertEqual(["testing-01"], summary["unverified"])
        self.assertEqual(["testing-02"], summary["needs-repair"])

    def test_verdict_pass_without_mech_ok_needs_repair(self):
        self.env.write_question(self.b, ok=False)
        self.env.register_verify("verify-r1-testing-1", ["testing-01", "testing-02"])
        summary = self.env.verdicts(self.env.batch("msgbatch_v6", {"verify-r1-testing-1": verdict_item(
            verdict("testing-01"), verdict("testing-02"))}))
        self.assertEqual(["testing-02"], summary["needs-repair"])

    def test_round2_rebutted_only_goes_owner_review(self):
        """Only unsure (or minor) issues reach owner-review; a major issue named in a repair note is still rejected."""
        for record in (self.a, self.b):
            record["notes"].append({"stage": "repair", "text": "Rebuttal for locales.en.answer: the excerpt shows it."})
        self.env.write_question(self.a, status="repaired")
        self.env.write_question(self.b, status="repaired")
        self.env.register_verify("verify-r2-testing-1", ["testing-01", "testing-02"], round_=2)
        summary = self.env.verdicts(self.env.batch("msgbatch_v7", {"verify-r2-testing-1": verdict_item(
            verdict("testing-01", "fix", ("minor", "locales.en.answer", "x"), ("unsure", "links[0]", "y")),
            verdict("testing-02", "fix", ("major", "locales.en.answer", "x")))}), round_=2)
        self.assertEqual(["testing-01"], summary["owner-review"])
        self.assertEqual(["testing-02"], summary["rejected"])
        self.assertFalse((self.env.worktree / self.b["path"]).exists())
        self.assertTrue((self.env.wave_dir / "rejected" / "testing" / "testing-02.yaml.txt").is_file())


class VerifyTaskTests(unittest.TestCase):
    def test_verify_includes_mech_fail_questions(self):
        env = VerifyEnv()
        try:
            good = env.add("testing-01")
            bad = env.add("testing-02")
            env.write_question(good)
            env.write_question(bad, ok=False, result={
                "executed": True, "declared": {"kind": "output", "value": "333"},
                "actual": {"kind": "output", "value": "first\nsecond"}, "diagnostics": ["snippet-mismatch: m"]})
            bad["notes"].append({"stage": "gen", "text": "not sure about the overload"})
            (env.worktree / "docs/content-wave-1/outline").mkdir(parents=True)
            (env.worktree / "docs/content-wave-1/outline/testing.json").write_text("{}", encoding="utf-8")
            tasks, entries = wave.build_verify_tasks(env.state, env.targets, "abc", env.worktree, env.wave_dir, {},
                                                     {}, 1, None, None)
            self.assertEqual({"verify-r1-testing-1": ["testing-01", "testing-02"]},
                             {k: v["stubs"] for k, v in entries.items()})
            item = tasks[0]["items"][0]
            self.assertEqual("claude-sonnet-5-5", tasks[0]["model"])
            self.assertIn('"value": "first\\nsecond"', item["text"])
            self.assertIn("failure: snippet-mismatch: x", item["text"])
            self.assertIn("not sure about the overload", item["text"])
            self.assertEqual(["docs/content-wave-1/outline/testing.json", good["path"], bad["path"]], item["files"])
            self.assertEqual(bad["evidence"]["mech"]["hash"], entries["verify-r1-testing-1"]["hashes"]["testing-02"])
            (env.worktree / bad["path"]).write_text("changed", encoding="utf-8")
            with self.assertRaises(wave.WaveError):
                wave.build_verify_tasks(env.state, env.targets, "abc", env.worktree, env.wave_dir, {}, {}, 1, None, None)
        finally:
            env.cleanup()

    def test_verify_item_numbers_find_bug_lines(self):
        env = VerifyEnv()
        try:
            record = env.add("testing-01", type_="find-bug")
            env.write_question(record)
            (env.worktree / "docs/content-wave-1/outline").mkdir(parents=True)
            (env.worktree / "docs/content-wave-1/outline/testing.json").write_text("{}", encoding="utf-8")
            docs = {record["path"]: {"code": {"source": "int a = 1;\nint b = a / 0;\nConsole.WriteLine(b);\n"},
                                     "links": [{"url": "https://learn.microsoft.com/en-us/x"}]}}
            tasks, _ = wave.build_verify_tasks(env.state, env.targets, "abc", env.worktree, env.wave_dir, {}, docs,
                                               1, None, None)
            text = tasks[0]["items"][0]["text"]
            self.assertIn("  1 | int a = 1;\n  2 | int b = a / 0;\n  3 | Console.WriteLine(b);", text)
            self.assertIn("https://learn.microsoft.com/en-us/x: not fetched", text)
        finally:
            env.cleanup()


class RepairTests(unittest.TestCase):
    def test_repair_restores_canonical_path_by_id(self):
        env = VerifyEnv()
        try:
            record = env.add("testing-01")
            env.write_question(record)
            broken = (env.worktree / record["path"]).read_text(encoding="utf-8") + "BROKEN\n"
            (env.worktree / record["path"]).write_text(broken, encoding="utf-8")
            wave.mech(env.state, env.worktree, env.wave_dir, ContentFakeCli(env.worktree), run_links=False)
            self.assertEqual("quarantined", record["status"])
            (env.worktree / "docs/content-wave-1/outline").mkdir(parents=True)
            (env.worktree / "docs/content-wave-1/outline/testing.json").write_text("{}", encoding="utf-8")
            tasks, entries, _ = wave.build_repair_tasks(env.state, env.targets, "abc", env.worktree, env.wave_dir, {},
                                                        {}, None, lister=lambda *a: [])
            self.assertEqual({"repair-testing-1": ["testing-01"]}, {k: v["stubs"] for k, v in entries.items()})
            self.assertIn("BROKEN", tasks[0]["items"][0]["text"])
            self.assertIn("from quarantine", tasks[0]["items"][0]["text"])
            env.registry.update({k: {**v, "task": "repair", "applied": False} for k, v in entries.items()})
            batch_dir = env.batch("msgbatch_r1", {"repair-testing-1": (
                "ok", None, {"questions": [{"stubId": "testing-01", "yaml": e2e_yaml(record)}],
                             "notes": [{"stubId": "testing-01", "text": "fixed the YAML"}]}, ".json")})
            env.apply(batch_dir, stage="repair")
            repaired = env.state["testing-01"]
            self.assertEqual("repaired", repaired["status"])
            text = (env.worktree / "content/dotnet/testing/testing-01.yaml").read_text(encoding="utf-8")
            self.assertTrue(text.startswith(wave.SCHEMA_COMMENT + "\nid: " + record["id"] + "\n"))
            self.assertEqual("content/dotnet/testing/testing-01.yaml", repaired["path"])
        finally:
            env.cleanup()

    def test_revised_stub_from_reject(self):
        env = VerifyEnv()
        try:
            record = env.add("testing-01")
            record["verdicts"].append({"round": 1, "verdict": "reject", "hash": "h", "issues": [
                {"severity": "blocker", "field": "premise", "claim": "c", "problem": "p",
                 "suggestedFix": "slug: other; level: middle; type: single; angle: Asks X; answerHinge: Y holds"}]})
            revised = wave.revised_stub(record)
            self.assertEqual({"slug": "other", "level": "middle", "type": "single", "angle": "Asks X",
                              "answerHinge": "Y holds"}, revised)
            self.assertEqual(["angle", "answerHinge", "level", "type"], wave.apply_revised_stub(record, revised))
            self.assertEqual("testing-01", record["slug"])
            self.assertEqual("middle", record["level"])
        finally:
            env.cleanup()


class FinalizeTests(unittest.TestCase):
    def test_finalize_refuses_unresolved(self):
        env = VerifyEnv()
        try:
            env.add("testing-01")
            done = env.add("testing-02")
            done["status"] = "verify-pass"
            env.save()
            code = wave.main(["--wave-dir", str(env.wave_dir), "--worktree", str(env.worktree), "finalize"])
            self.assertEqual(1, code)
            with self.assertRaises(wave.WaveError):
                wave.resolve(env.state, "testing-02", "shortfall", "x", env.worktree, env.wave_dir)
            wave.resolve(env.state, "testing-01", "shortfall", "no time", env.worktree, env.wave_dir)
            self.assertEqual([], wave.unresolved(env.state))
        finally:
            env.cleanup()

    def test_final_gate_detects_edit_after_pass(self):
        env = VerifyEnv()
        try:
            record = env.add("testing-01")
            env.write_question(record, status="verify-pass")
            record["verdicts"].append({"round": 1, "verdict": "pass", "hash": record["contentHash"], "issues": [],
                                       "verifiedDate": "2026-11-13"})
            record["verifiedDate"] = "2026-11-13"
            path = env.worktree / record["path"]
            path.write_text(path.read_text(encoding="utf-8").replace("Answer", "Answer edited"), encoding="utf-8")
            problems, _ = wave.final_gate(env.state, env.worktree, env.wave_dir, ContentFakeCli(env.worktree), set())
            self.assertTrue(any("content changed after its pass verdict" in p for p in problems), problems)
            self.assertEqual("verify-pass", record["status"])
            path.write_text(path.read_text(encoding="utf-8").replace("Answer edited", "Answer"), encoding="utf-8")
            problems, _ = wave.final_gate(env.state, env.worktree, env.wave_dir, ContentFakeCli(env.worktree), set())
            self.assertEqual([], problems)
            self.assertEqual("final", record["status"])
            path.write_text(path.read_text(encoding="utf-8").replace("Answer", "Answer by owner"), encoding="utf-8")
            failures, edited = wave.owner_check(env.state, env.worktree, env.wave_dir, ContentFakeCli(env.worktree),
                                                "2026-11-20")
            self.assertEqual([], failures)
            self.assertEqual([record["path"]], edited)
            self.assertEqual("owner-edited", record["status"])
            self.assertIn("verified: 2026-11-20", path.read_text(encoding="utf-8"))
        finally:
            env.cleanup()

    def test_report_drops_mech_link_unknown_when_fetch_evidence_is_ok(self):
        env = VerifyEnv()
        try:
            record = env.add("testing-01")
            ok_url = "https://learn.microsoft.com/en-us/dotnet/api/a"
            bad_url = "https://learn.microsoft.com/en-us/dotnet/api/b"
            record["evidence"]["mech"] = {"hash": "h", "ok": True, "issues": [], "result": {}, "warnings": [
                f"link unknown: {ok_url} (429)", f"link unknown: {bad_url} (429)", "parity x: y"]}
            record["evidence"]["links"] = [{"url": ok_url, "status": "ok", "title": "A"},
                                           {"url": bad_url, "status": "unknown", "title": None}]
            row = wave.report_row(record)
            self.assertNotIn(ok_url, row["warnings"])
            self.assertIn(f"link unknown: {bad_url} (429)", row["warnings"])
            self.assertIn("parity x: y", row["warnings"])
            spaced = "https://example.com/a b"
            record["evidence"]["mech"]["warnings"] = [f"link unknown: {spaced} (429)"]
            record["evidence"]["links"].append({"url": spaced, "status": "ok", "title": "S"})
            self.assertIn(spaced, wave.report_row(record)["warnings"])
        finally:
            env.cleanup()

    def test_report_sample_is_deterministic(self):
        env = VerifyEnv()
        try:
            for i in range(1, 31):
                record = env.add(f"testing-{i:02d}")
                record["status"] = "verify-pass"
                record["evidence"]["mech"] = {"hash": "h", "ok": True, "issues": [], "warnings": [],
                                              "result": {"executed": False, "skipped": "no code"}}
            env.state["testing-05"]["level"] = "lead"
            env.state["testing-06"]["status"] = "owner-review"
            first, rows = wave.build_report(env.state, env.targets, "g1a", {})
            second, _ = wave.build_report(env.state, env.targets, "g1a", {})
            self.assertEqual(first, second)
            self.assertEqual(2 + 3, len(rows))
            self.assertEqual("content/dotnet/testing/testing-05.yaml", rows[0]["path"])
            md_path, csv_path = wave.write_report(env.wave_dir, "g1a", first, rows)
            self.assertTrue(csv_path.read_text(encoding="utf-8").startswith(",".join(wave.REPORT_COLUMNS)))
        finally:
            env.cleanup()


def git(repo, *args):
    result = subprocess.run(["git", "-C", str(repo), *args], capture_output=True, text=True)
    if result.returncode != 0:
        raise AssertionError(result.stderr)
    return result.stdout.strip()


class EndToEndTests(unittest.TestCase):
    def setUp(self):
        self.env = VerifyEnv()
        self.repo = self.env.worktree
        targets = small_targets({"testing": {"levels": {"junior": 4, "middle": 4}}})
        targets.update({"acceptance": {"minShare": 0.85}, "verifyChunk": 2, "cutoffUtc": "2099-01-01T00:00:00Z",
                        "chunk": {"default": 2, "singleLevels": ["lead", "architect"], "singleTopics": []}})
        (self.env.wave_dir / "targets.json").write_text(json.dumps(targets), encoding="utf-8")
        (self.env.wave_dir / "ledger.json").write_text("{}", encoding="utf-8")
        for shared in {*wave.gen_shared(self.env.wave_dir, self.repo),
                       *wave.repair_shared(self.env.wave_dir, self.repo),
                       *wave.verify_shared(self.env.wave_dir, self.repo)}:
            (self.repo / shared).parent.mkdir(parents=True, exist_ok=True)
            (self.repo / shared).write_text("placeholder\n", encoding="utf-8")
        git(self.repo, "init", "-q", "-b", "main")
        git(self.repo, "config", "user.email", "t@example.com")
        git(self.repo, "config", "user.name", "t")
        git(self.repo, "config", "core.autocrlf", "false")
        self.fake = ContentFakeCli(self.repo)
        self.original_cli = wave.run_cli
        wave.run_cli = lambda args, worktree=None: self.fake(args)

    def tearDown(self):
        wave.run_cli = self.original_cli
        self.env.cleanup()

    def run_wave(self, *args):
        code = wave.main(["--wave-dir", str(self.env.wave_dir), "--worktree", str(self.repo),
                          "--server-repo", str(self.repo), *args])
        self.assertEqual(0, code, args)
        return self.env.load()

    def commit(self):
        git(self.repo, "add", "-A")
        git(self.repo, "commit", "-q", "-m", "step", "--allow-empty")
        return git(self.repo, "rev-parse", "HEAD")

    def gen_batch(self, name, items):
        batch = {}
        for item_id, spec in items.items():
            if spec == "truncated":
                batch[item_id] = ("failed", "stop_reason=max_tokens", '{"questions": [', ".raw.txt")
                continue
            questions = [{"stubId": s, "yaml": e2e_yaml(self.env.state[s], **kw)} for s, kw in spec]
            batch[item_id] = ("ok", None, {"questions": questions, "notes": []}, ".json")
        return self.env.batch(name, batch)

    def test_e2e_dry_run(self):
        env = self.env
        results = env.root / "outline-results"
        results.mkdir()
        stubs = [stub(f"q{i}", "junior" if i <= 4 else "middle", "open" if i % 2 else "single",
                      hinge=f"alpha{i} beta{i} gamma{i}") for i in range(1, 9)]
        (results / "outline-testing.json").write_text(json.dumps({"topic": "testing", "stubs": stubs}), encoding="utf-8")
        wave.outline_apply(results, env.wave_dir / "outline", wave.load_targets(env.wave_dir))
        self.commit()
        state = self.run_wave("allocate")
        self.assertEqual(8, len(state))
        ids = {k: r["id"] for k, r in state.items()}

        state = self.run_wave("gen-tasks", "--ref", self.commit())
        self.assertEqual({"gen-testing-1": ["testing-01", "testing-02"], "gen-testing-2": ["testing-03", "testing-04"],
                          "gen-testing-3": ["testing-05", "testing-06"], "gen-testing-4": ["testing-07", "testing-08"]},
                         {k: v["stubs"] for k, v in env.registry.items()})
        batch1 = self.gen_batch("msgbatch_gen1", {
            "gen-testing-1": [("testing-01", {}), ("testing-02", {})],
            "gen-testing-2": "truncated",
            "gen-testing-3": [("testing-05", {}), ("testing-05", {})],
            "gen-testing-4": [("testing-07", {"mismatch": True}), ("testing-08", {})]})
        state = self.run_wave("apply", "--batch", str(batch1), "--stage", "gen")
        self.assertEqual(["generated", "generated", "gen-failed", "gen-failed", "gen-failed", "gen-failed",
                          "generated", "generated"], [state[f"testing-0{i}"]["status"] for i in range(1, 9)])
        for item in env.registry.values():
            item["applied"] = False
        env.save()
        state = self.run_wave("apply", "--batch", str(batch1), "--stage", "gen")
        self.assertEqual(1, len(state["testing-01"]["yamlHistory"]))
        self.assertEqual(1, len(state["testing-03"]["failures"]))

        state = self.run_wave("retry-plan", "--ref", self.commit())
        retry = {k: v["stubs"] for k, v in env.registry.items() if k not in
                 ("gen-testing-1", "gen-testing-2", "gen-testing-3", "gen-testing-4")}
        self.assertEqual([["testing-03"], ["testing-04"], ["testing-05"], ["testing-06"]], sorted(retry.values()))
        batch2 = self.gen_batch("msgbatch_gen2", {k: [(v[0], {})] for k, v in retry.items()})
        state = self.run_wave("apply", "--batch", str(batch2), "--stage", "gen")
        self.assertTrue(all(r["status"] == "generated" for r in state.values()))

        state = self.run_wave("mech")
        self.assertFalse(state["testing-07"]["evidence"]["mech"]["ok"])
        self.assertTrue(state["testing-01"]["evidence"]["mech"]["ok"])

        state = self.run_wave("verify-tasks", "--ref", self.commit(), "--round", "1")
        r1 = {k: v["stubs"] for k, v in env.registry.items() if k.startswith("verify-r1")}
        self.assertEqual(4, len(r1))
        self.assertIn("verifyItems", wave.load_ledger(env.wave_dir))
        stale_path = self.repo / state["testing-02"]["path"]
        stale_path.write_text(stale_path.read_text(encoding="utf-8").replace("Question T", "Question T2"), encoding="utf-8")
        reject_fix = "slug: q6; level: middle; type: single; angle: Asks a better thing; answerHinge: a better hinge"
        verdicts = {"testing-01": verdict("testing-01"), "testing-02": verdict("testing-02"),
                    "testing-03": verdict("testing-03"),
                    "testing-05": verdict("testing-05", "fix", ("major", "locales.en.answer", "say Y")),
                    "testing-06": verdict("testing-06", "reject", ("blocker", "premise", reject_fix)),
                    "testing-07": verdict("testing-07"), "testing-08": verdict("testing-08")}
        batch3 = env.batch("msgbatch_ver1", {item_id: verdict_item(*[verdicts[s] for s in stubs if s in verdicts])
                                             for item_id, stubs in r1.items()})
        state = self.run_wave("verdicts", "--batch", str(batch3), "--round", "1")
        expected = {"testing-01": "verify-pass", "testing-02": "generated", "testing-03": "verify-pass",
                    "testing-04": "unverified", "testing-05": "needs-repair", "testing-06": "needs-repair",
                    "testing-07": "needs-repair", "testing-08": "verify-pass"}
        self.assertEqual(expected, {k: r["status"] for k, r in state.items()})

        state = self.run_wave("mech")
        state = self.run_wave("verify-tasks", "--ref", self.commit(), "--round", "1", "--only", "testing-02,testing-04",
                              "--name", "verify-r1-retry")
        retry_items = {k: v["stubs"] for k, v in env.registry.items() if k.startswith("verify-r1") and not v["applied"]}
        self.assertEqual([["testing-02", "testing-04"]], list(retry_items.values()))
        batch4 = env.batch("msgbatch_ver1b", {k: verdict_item(*[verdict(s) for s in v]) for k, v in retry_items.items()})
        state = self.run_wave("verdicts", "--batch", str(batch4), "--round", "1")
        self.assertEqual("verify-pass", state["testing-02"]["status"])
        self.assertEqual("verify-pass", state["testing-04"]["status"])

        state = self.run_wave("repair-tasks", "--ref", self.commit())
        repair = {k: v["stubs"] for k, v in env.registry.items() if k.startswith("repair-")}
        self.assertEqual(["testing-05", "testing-06", "testing-07"], sorted(s for v in repair.values() for s in v))
        self.assertEqual("Asks a better thing", state["testing-06"]["stub"]["angle"])
        self.assertEqual("single", state["testing-06"]["type"])
        repair_results = {}
        for item_id, stubs_ in repair.items():
            questions = [{"stubId": s, "yaml": e2e_yaml(state[s], title="R")} for s in stubs_]
            notes = [{"stubId": "testing-05", "text": "Rebuttal on locales.en.answer: the excerpt supports it."}] \
                if "testing-05" in stubs_ else []
            repair_results[item_id] = ("ok", None, {"questions": questions, "notes": notes}, ".json")
        state = self.run_wave("apply", "--batch", str(env.batch("msgbatch_rep", repair_results)), "--stage", "repair")
        self.assertEqual({"repaired"}, {state[s]["status"] for s in ("testing-05", "testing-06", "testing-07")})

        state = self.run_wave("mech")
        self.assertTrue(state["testing-07"]["evidence"]["mech"]["ok"])
        state = self.run_wave("verify-tasks", "--ref", self.commit(), "--round", "2")
        r2 = {k: v["stubs"] for k, v in env.registry.items() if k.startswith("verify-r2")}
        verdicts2 = {"testing-05": verdict("testing-05", "fix", ("unsure", "locales.en.answer", "say Y")),
                     "testing-06": verdict("testing-06", "reject", ("blocker", "premise", "no defensible answer")),
                     "testing-07": verdict("testing-07")}
        batch5 = env.batch("msgbatch_ver2", {k: verdict_item(*[verdicts2[s] for s in v]) for k, v in r2.items()})
        state = self.run_wave("verdicts", "--batch", str(batch5), "--round", "2")
        self.assertEqual("owner-review", state["testing-05"]["status"])
        self.assertEqual("rejected", state["testing-06"]["status"])
        self.assertEqual("verify-pass", state["testing-07"]["status"])

        self.run_wave("finalize")
        self.commit()
        code = wave.main(["--wave-dir", str(env.wave_dir), "--worktree", str(self.repo), "--server-repo",
                          str(self.repo), "final-gate", "--seed-ref", "HEAD"])
        self.assertEqual(1, code)
        state = self.run_wave("final-gate", "--seed-ref", "HEAD", "--accept-owner-review", "post-launch feedback")
        self.assertEqual("post-launch feedback", state["testing-05"]["ownerReviewAccepted"]["reason"])
        final = {k: r["status"] for k, r in state.items()}
        self.assertEqual({"testing-01": "final", "testing-02": "final", "testing-03": "final", "testing-04": "final",
                          "testing-05": "owner-review", "testing-06": "rejected", "testing-07": "final",
                          "testing-08": "final"}, final)
        self.assertEqual(ids, {k: r["id"] for k, r in state.items()})
        self.assertEqual([], wave.unresolved(state))
        for record in state.values():
            if record["status"] == "final":
                text = (self.repo / record["path"]).read_text(encoding="utf-8")
                self.assertIn("id: " + record["id"], text)
                self.assertIn("verified: ", text)
        self.run_wave("report", "--group", "g1a")
        self.assertTrue((env.wave_dir / "review" / "g1a.md").is_file())


class FixRoundCriticalTests(unittest.TestCase):
    """Regression tests for review findings C1 and C2."""

    def mech_with(self, **outputs):
        env = WaveEnv()
        self.addCleanup(env.cleanup)
        record = env.add("testing-01")
        text = wave.SCHEMA_COMMENT + "\nid: " + record["id"] + "\n" + e2e_yaml(record).split("\n", 1)[1]
        (env.worktree / record["path"]).parent.mkdir(parents=True, exist_ok=True)
        (env.worktree / record["path"]).write_text(text, encoding="utf-8")
        doc = {**e2e_doc(record), "id": record["id"]}
        base = {"dump": [(0, dump_json([{"path": "dotnet/testing/testing-01.yaml", "document": doc}]), "")],
                "validate": [(0, VALID_OUT, "")], "run-snippets": [(0, SNIPPETS_OUT, "")],
                "check-links": [(0, LINKS_OUT, "")]}
        base.update({k.replace("_", "-"): [v] for k, v in outputs.items()})
        return lambda: wave.mech(env.state, env.worktree, env.wave_dir, FakeCli(base)), record

    def test_mech_baseline_passes(self):
        run, record = self.mech_with()
        run()
        self.assertTrue(record["evidence"]["mech"]["ok"])

    def test_mech_exit1_without_issue_lines_fails_closed(self):
        for command in ("validate", "run_snippets", "check_links"):
            run, _ = self.mech_with(**{command: (1, "", "Unhandled exception")})
            with self.assertRaises(wave.WaveError, msg=command):
                run()

    def test_mech_exit0_without_success_line_fails_closed(self):
        for command in ("validate", "run_snippets", "check_links"):
            run, _ = self.mech_with(**{command: (0, "", "")})
            with self.assertRaises(wave.WaveError, msg=command):
                run()

    def test_mech_check_links_totals_must_match(self):
        run, _ = self.mech_with(check_links=(1, "Checked 3 URL(s): 2 ok, 1 dead, 0 unknown.\n", "1 dead"))
        with self.assertRaises(wave.WaveError):
            run()
        run, _ = self.mech_with(check_links=(
            0, "unknown: https://x.example/y (HTTP 403) cited by dotnet/testing/testing-01.yaml\n"
               "Checked 1 URL(s): 0 ok, 0 dead, 1 unknown.\n", ""))
        run()

    def test_apply_forces_verified_sentinel_and_gate_binds_pass_date(self):
        env = VerifyEnv()
        try:
            record = env.add("testing-01")
            env.register("gen-testing-1", ["testing-01"])
            env.apply(env.batch("msgbatch_s1", {"gen-testing-1": ok_item(record)}))
            path = env.worktree / record["path"]
            self.assertEqual(wave.VERIFIED_SENTINEL, wave.read_verified(path.read_text(encoding="utf-8")))
            self.assertIn("verified: 2026-11-13", model_yaml(record))
        finally:
            env.cleanup()
        env = VerifyEnv()
        try:
            a = env.add("testing-01")
            b = env.add("testing-02")
            for r in (a, b):
                env.write_question(r, text=e2e_yaml(r).replace("2026-11-13", wave.VERIFIED_SENTINEL))
            env.register_verify("verify-r1-testing-1", ["testing-01", "testing-02"])
            env.verdicts(env.batch("msgbatch_s2", {"verify-r1-testing-1": verdict_item(
                verdict("testing-01"), verdict("testing-02", "fix", ("major", "x", "y")))}))
            a, b = env.state["testing-01"], env.state["testing-02"]
            self.assertEqual("2026-11-14", wave.read_verified((env.worktree / a["path"]).read_text(encoding="utf-8")))
            self.assertEqual("2026-11-14", a["verifiedDate"])
            self.assertEqual(wave.VERIFIED_SENTINEL,
                             wave.read_verified((env.worktree / b["path"]).read_text(encoding="utf-8")))
            b["status"] = "owner-review"
            problems, owner = wave.final_gate(env.state, env.worktree, env.wave_dir, ContentFakeCli(env.worktree), set(),
                                              "owner accepts")
            self.assertEqual([], problems)
            self.assertEqual([b["path"]], owner)
            a["status"] = "verify-pass"
            path = env.worktree / a["path"]
            path.write_text(wave.set_verified(path.read_text(encoding="utf-8"), "2026-11-10"), encoding="utf-8")
            problems, _ = wave.final_gate(env.state, env.worktree, env.wave_dir, ContentFakeCli(env.worktree), set())
            self.assertTrue(any("is not the date of its pass" in p for p in problems), problems)
            path.write_text(wave.set_verified(path.read_text(encoding="utf-8"), "2026-11-14"), encoding="utf-8")
            bpath = env.worktree / b["path"]
            bpath.write_text(wave.set_verified(bpath.read_text(encoding="utf-8"), "2026-11-12"), encoding="utf-8")
            problems, _ = wave.final_gate(env.state, env.worktree, env.wave_dir, ContentFakeCli(env.worktree), set())
            self.assertTrue(any("owner-review file must carry" in p for p in problems), problems)
            markdown, rows = wave.build_report(env.state, env.targets, "g1a", {})
            self.assertTrue(any(r["verified"].startswith("UNVERIFIED (accepted: owner accepts)") for r in rows))
            self.assertIn("accepted at the final gate: owner accepts", markdown)
        finally:
            env.cleanup()


class FixRoundImportantTests(unittest.TestCase):
    """Regression tests for review findings I1, I2 and I3."""

    def test_round2_rebuttal_note_does_not_override_blocker(self):
        env = VerifyEnv()
        try:
            record = env.add("testing-01")
            record["notes"].append({"stage": "repair", "text": "Changed level; rebuttal on level: it is junior."})
            env.write_question(record, status="repaired")
            env.register_verify("verify-r2-testing-1", ["testing-01"], round_=2)
            summary = env.verdicts(env.batch("msgbatch_i1", {"verify-r2-testing-1": verdict_item(
                verdict("testing-01", "fix", ("blocker", "level", "senior")))}), round_=2)
            self.assertEqual(["testing-01"], summary["rejected"])
            self.assertEqual([], summary["owner-review"])
        finally:
            env.cleanup()

    def test_replay_from_renamed_directory_adds_no_failure(self):
        env = WaveEnv()
        try:
            env.add("testing-01")
            env.register("gen-testing-1", ["testing-01"])
            first = env.batch("msgbatch_r", {"gen-testing-1": ("failed", "stop_reason=max_tokens", "{", ".raw.txt")})
            env.apply(first)
            copy = env.root / "copied-results"
            shutil.copytree(first, copy)
            env.registry["gen-testing-1"]["applied"] = False
            env.apply(copy)
            self.assertEqual(1, len(env.state["testing-01"]["failures"]))
            self.assertEqual("gen-failed", env.state["testing-01"]["status"])
        finally:
            env.cleanup()

    def test_identical_bad_retry_output_is_counted(self):
        env = WaveEnv()
        try:
            record = env.add("testing-01")
            bad = model_yaml(record, level="senior")
            env.register("gen-testing-1", ["testing-01"])
            env.apply(env.batch("msgbatch_b1", {"gen-testing-1": (
                "ok", None, {"questions": [{"stubId": "testing-01", "yaml": bad}], "notes": []}, ".json")}))
            self.assertEqual("gen-failed", env.state["testing-01"]["status"])
            env.register("gen-testing-2", ["testing-01"])
            env.apply(env.batch("msgbatch_b2", {"gen-testing-2": (
                "ok", None, {"questions": [{"stubId": "testing-01", "yaml": bad}], "notes": []}, ".json")}))
            self.assertEqual("shortfall", env.state["testing-01"]["status"])
            self.assertEqual(2, len(env.state["testing-01"]["failures"]))
        finally:
            env.cleanup()

    def test_overwrite_refused_after_submit(self):
        env = WaveEnv()
        try:
            tasks = env.wave_dir / "tasks"
            tasks.mkdir()
            (tasks / "pilot.json").write_text("{}", encoding="utf-8")
            ledger = {"submits": [{"name": "pilot", "taskFile": "docs/content-wave-1/tasks/pilot.json",
                                   "batchId": "msgbatch_p", "status": "submitted"}]}
            (env.wave_dir / "ledger.json").write_text(json.dumps(ledger), encoding="utf-8")
            args = type("Args", (), {"overwrite": True, "worktree": str(env.worktree), "server_repo": str(env.worktree)})
            with self.assertRaises(wave.WaveError) as caught:
                wave.write_tasks([{"items": []}], {}, "pilot", "gen", args, env.wave_dir)
            self.assertIn("already submitted", str(caught.exception))
            with self.assertRaises(wave.WaveError):
                wave.refuse_submitted_overwrite("pilot", env.wave_dir)
            wave.refuse_submitted_overwrite("gen", env.wave_dir)
        finally:
            env.cleanup()

    def test_overwrite_releases_unapplied_items_before_building(self):
        env = WaveEnv()
        try:
            env.add("testing-01")
            env.register("verify-r1-testing-1", ["testing-01"], stage="verify")
            env.registry["verify-r1-testing-1"]["task"] = "verify-r1"
            env.save()
            args = type("Args", (), {"overwrite": True, "worktree": str(env.worktree)})
            self.assertEqual({}, wave.registry_for_build(args, "verify-r1", env.wave_dir))
            keep = type("Args", (), {"overwrite": False, "worktree": str(env.worktree)})
            self.assertIn("verify-r1-testing-1", wave.registry_for_build(keep, "verify-r1", env.wave_dir))
        finally:
            env.cleanup()


class FixRoundMinorTests(unittest.TestCase):
    def test_failed_item_does_not_touch_finished_record(self):
        env = WaveEnv()
        try:
            record = env.add("testing-01")
            record["status"] = "verify-pass"
            env.register("gen-testing-1", ["testing-01"])
            summary = env.apply(env.batch("msgbatch_m1", {"gen-testing-1": ("failed", "stop_reason=max_tokens", None, "")}))
            self.assertEqual("verify-pass", env.state["testing-01"]["status"])
            self.assertTrue(any("ignores failed" in w for w in summary["warnings"]))
        finally:
            env.cleanup()

    def test_retire_keeps_earlier_copies_and_bytes(self):
        env = WaveEnv()
        try:
            record = env.add("testing-01")
            path = env.worktree / record["path"]
            path.parent.mkdir(parents=True, exist_ok=True)
            target = env.wave_dir / "quarantine" / "testing-01.yaml.txt"
            path.write_bytes(b"first \xff\r\n")
            wave.retire_file(record, env.worktree, target)
            path.write_bytes(b"second\n")
            second = wave.retire_file(record, env.worktree, target)
            self.assertEqual(b"first \xff\r\n", target.read_bytes())
            self.assertEqual("testing-01.2.yaml.txt", second.name)
            self.assertEqual(b"second\n", second.read_bytes())
        finally:
            env.cleanup()

    def test_retry_budget_is_per_stage(self):
        record = {"failures": [{"stage": "gen"}, {"stage": "repair"}]}
        self.assertTrue(wave.retry_left(record))
        self.assertTrue(wave.retry_left(record, "gen"))
        record["failures"].append({"stage": "repair"})
        self.assertFalse(wave.retry_left(record))

    def test_round2_candidates_and_pass_without_mech(self):
        env = VerifyEnv()
        try:
            r1 = env.add("testing-01")
            r1["status"], r1["unverifiedRound"] = "unverified", 1
            r2 = env.add("testing-02")
            env.write_question(r2, status="repaired", ok=False)
            self.assertEqual(["testing-02"], [r["stubId"] for r in wave.verify_candidates(env.state, 2, None, {})])
            env.register_verify("verify-r2-testing-1", ["testing-02"], round_=2)
            summary = env.verdicts(env.batch("msgbatch_m4", {"verify-r2-testing-1": verdict_item(verdict("testing-02"))}),
                                   round_=2)
            self.assertEqual(["testing-02"], summary["unverified"])
            self.assertTrue((env.worktree / r2["path"]).exists())
        finally:
            env.cleanup()

    def test_final_gate_accepts_owner_edited(self):
        env = VerifyEnv()
        try:
            record = env.add("testing-01")
            env.write_question(record, text=e2e_yaml(record).replace("2026-11-13", wave.VERIFIED_SENTINEL),
                               status="owner-review")
            wave.owner_approve(env.state, "testing-01", "read in both locales", env.worktree, "2026-11-15")
            self.assertEqual("owner-edited", record["status"])
            problems, _ = wave.final_gate(env.state, env.worktree, env.wave_dir, ContentFakeCli(env.worktree), set())
            self.assertEqual([], problems)
        finally:
            env.cleanup()

    def test_public_https_only(self):
        self.assertIsNone(wave.public_https("https://learn.microsoft.com/en-us/x", resolve_dns=False))
        for url in ("http://learn.microsoft.com/x", "https://127.0.0.1/x", "https://localhost/x",
                    "https://10.0.0.5/x", "file:///etc/passwd", "https://[::1]/x"):
            self.assertIsNotNone(wave.public_https(url, resolve_dns=False), url)
        env = WaveEnv()
        try:
            record = env.add("testing-01")
            docs = {record["path"]: {"links": [{"url": "https://127.0.0.1:8080/admin"}], "locales": {"en": {}}}}
            called = []
            wave.links_fetch(env.state, docs, env.wave_dir, env.worktree, fetch=lambda u: called.append(u))
            self.assertEqual([], called)
        finally:
            env.cleanup()

    def test_mech_records_links_skipped_and_gate_needs_two_fetch_passes(self):
        env = VerifyEnv()
        try:
            record = env.add("testing-01")
            env.write_question(record)
            wave.mech(env.state, env.worktree, env.wave_dir, ContentFakeCli(env.worktree), run_links=False)
            self.assertFalse(record["evidence"]["mech"]["linksChecked"])
            url = "https://learn.microsoft.com/en-us/x"
            doc = {"links": [{"url": url}]}
            self.assertIn("no links-fetch evidence", wave.link_evidence_problems(record, doc, env.wave_dir)[0])
            ok = lambda u: {"status": 200, "finalUrl": u, "html": "<title>x</title>", "error": None}
            start = wave.parse_utc("2026-11-13T10:00:00Z")
            docs = {record["path"]: {**doc, "locales": {"en": {}}}}
            wave.links_fetch(env.state, docs, env.wave_dir, env.worktree, fetch=ok, now=lambda: start)
            self.assertIn("second links-fetch pass", wave.link_evidence_problems(record, doc, env.wave_dir)[0])
            wave.links_fetch(env.state, docs, env.wave_dir, env.worktree, fetch=ok,
                             now=lambda: start + wave.dt.timedelta(minutes=31))
            self.assertEqual([], wave.link_evidence_problems(record, doc, env.wave_dir))
        finally:
            env.cleanup()

    def test_pointer_like_output_line_does_not_split_issue(self):
        output = ("dotnet/a/b.yaml: snippet-mismatch: Declared output 'x', actual output 'Result: [x]\n"
                  "done'. [expect]\nAll runnable snippets match their declared results.\n")
        issues = wave.parse_issue_lines(output)
        self.assertEqual(1, len(issues["content/dotnet/a/b.yaml"]))
        self.assertIn("Result: [x]\ndone", issues["content/dotnet/a/b.yaml"][0]["message"])

    def test_output_at_cap_is_truncated_and_missing_items_warned(self):
        env = WaveEnv()
        try:
            record = env.add("testing-01")
            env.add("testing-02")
            env.register("gen-testing-1", ["testing-01"])
            env.register("gen-testing-2", ["testing-02"])
            batch_dir = env.batch("msgbatch_m9", {"gen-testing-1": ok_item(record)})
            index = json.loads((batch_dir / "index.json").read_text(encoding="utf-8"))
            index[0]["outputTokens"] = wave.MAX_OUTPUT_TOKENS
            (batch_dir / "index.json").write_text(json.dumps(index), encoding="utf-8")
            summary = env.apply(batch_dir)
            self.assertEqual("gen-failed", env.state["testing-01"]["status"])
            self.assertIn("max_tokens", env.state["testing-01"]["failures"][0]["reason"])
            self.assertTrue(any("gen-testing-2" in w for w in summary["warnings"]))
        finally:
            env.cleanup()

    def test_verdict_file_ops_are_journalled(self):
        env = VerifyEnv()
        try:
            record = env.add("testing-01")
            env.write_question(record, status="verify-pass")
            record["verdicts"].append({"round": 1, "verdict": "pass", "hash": record["contentHash"], "issues": [],
                                       "verifiedDate": "2026-11-16"})
            record["pendingFileOp"] = {"op": "verify", "date": "2026-11-16"}
            done = wave.complete_file_ops(env.state, env.worktree, env.wave_dir)
            self.assertEqual(["testing-01"], done)
            self.assertNotIn("pendingFileOp", record)
            self.assertEqual("2026-11-16", wave.read_verified((env.worktree / record["path"]).read_text(encoding="utf-8")))
        finally:
            env.cleanup()

    def test_parity_compares_fenced_blocks_and_report_order(self):
        doc = parity_doc({"explanation": "Текст\n```csharp\nint a = 1;\n```"},
                         {"explanation": "Text\n```csharp\nint a = 2;\n```"})
        self.assertTrue(any("fenced" in f["message"] for f in wave.parity(doc)))
        env = VerifyEnv()
        try:
            for stub_id, level, type_ in (("testing-01", "junior", "situation"), ("testing-02", "lead", "open"),
                                          ("testing-03", "junior", "open")):
                record = env.add(stub_id, level=level, type_=type_)
                record["status"] = "verify-pass"
                record["evidence"]["mech"] = {"ok": True, "result": {"executed": False, "skipped": "no declared result"}}
            _, rows = wave.build_report(env.state, env.targets, "g1a", {})
            self.assertEqual(["content/dotnet/testing/testing-02.yaml", "content/dotnet/testing/testing-01.yaml"],
                             [r["path"] for r in rows[:2]])
            self.assertNotIn("not executed", rows[0]["risk"])
        finally:
            env.cleanup()


class FixRound2Tests(unittest.TestCase):
    """Regression tests for re-review findings N1-N7."""

    def test_n1_journal_drops_pass_when_file_changed_or_unwritable(self):
        env = VerifyEnv()
        try:
            record = env.add("testing-01")
            env.write_question(record, status="verify-pass")
            record["verdicts"].append({"round": 1, "verdict": "pass", "hash": record["contentHash"], "issues": [],
                                       "verifiedDate": "2026-11-16"})
            record["pendingFileOp"] = {"op": "verify", "date": "2026-11-16"}
            path = env.worktree / record["path"]
            path.write_text(path.read_text(encoding="utf-8").replace("Answer", "Owner text"), encoding="utf-8")
            wave.complete_file_ops(env.state, env.worktree, env.wave_dir)
            self.assertEqual("unverified", record["status"])
            self.assertNotIn("pendingFileOp", record)
            self.assertEqual("2026-11-13", wave.read_verified(path.read_text(encoding="utf-8")))
            other = env.add("testing-02")
            env.write_question(other, status="verify-pass")
            opath = env.worktree / other["path"]
            opath.write_text(opath.read_text(encoding="utf-8").replace("  verified: 2026-11-13\n", ""), encoding="utf-8")
            other["contentHash"] = wave.file_hash(env.worktree, other)
            other["verdicts"].append({"round": 1, "verdict": "pass", "hash": other["contentHash"], "issues": [],
                                      "verifiedDate": "2026-11-16"})
            other["pendingFileOp"] = {"op": "verify", "date": "2026-11-16"}
            wave.complete_file_ops(env.state, env.worktree, env.wave_dir)
            self.assertEqual("unverified", other["status"])
            self.assertNotIn("pendingFileOp", other)
        finally:
            env.cleanup()

    def test_n2_late_verdict_does_not_demote_verify_pass(self):
        env = VerifyEnv()
        try:
            record = env.add("testing-01")
            env.write_question(record, status="verify-pass")
            env.register_verify("verify-r1-testing-1", ["testing-01"])
            summary = env.verdicts(env.batch("msgbatch_n2", {"verify-r1-testing-1": verdict_item(
                verdict("testing-01", "fix", ("major", "x", "y")))}))
            self.assertEqual("verify-pass", env.state["testing-01"]["status"])
            self.assertEqual([], env.state["testing-01"]["verdicts"])
            self.assertTrue(any("ignores round 1 verdict" in w for w in summary["warnings"]))
        finally:
            env.cleanup()

    def test_n3_round2_reject_is_always_rejected(self):
        env = VerifyEnv()
        try:
            cases = {"testing-01": verdict("testing-01", "reject"),
                     "testing-02": verdict("testing-02", "reject", ("minor", "title", "x")),
                     "testing-03": verdict("testing-03", "fix"),
                     "testing-04": verdict("testing-04", "fix", ("minor", "title", "x"), ("unsure", "links[0]", "y"))}
            for stub_id in cases:
                env.write_question(env.add(stub_id), status="repaired")
            env.register_verify("verify-r2-testing-1", list(cases), round_=2)
            summary = env.verdicts(env.batch("msgbatch_n3", {"verify-r2-testing-1": verdict_item(*cases.values())}),
                                   round_=2)
            self.assertEqual(["testing-01", "testing-02"], summary["rejected"])
            self.assertEqual(["testing-03"], summary["unverified"])
            self.assertEqual(["testing-04"], summary["owner-review"])
        finally:
            env.cleanup()

    def test_n4_trailer_line_after_issue_is_accepted(self):
        output = ("dotnet/a/b.yaml: snippet-mismatch: Declared output 'x', actual output 'y'. [expect]\n"
                  "1 snippet issue(s) found.\n")
        issues = wave.parse_issue_lines(output)
        self.assertEqual("expect", issues["content/dotnet/a/b.yaml"][0]["pointer"])
        with self.assertRaises(wave.WaveError):
            wave.parse_issue_lines("dotnet/a/b.yaml: x: y [expect]\nsome new trailer\n")

    def test_n5_resolve_once_and_connect_to_checked_address(self):
        import socket
        original_getaddrinfo, original_connect = socket.getaddrinfo, socket.create_connection
        calls, connected = [], []
        answers = iter(["93.184.216.34", "127.0.0.1"])

        def fake_getaddrinfo(host, port, *args, **kwargs):
            calls.append(host)
            return [(socket.AF_INET, socket.SOCK_STREAM, 6, "", (next(answers), port))]

        def fake_connect(address, timeout=None, *args, **kwargs):
            connected.append(address)
            raise OSError("connection refused by test")

        socket.getaddrinfo, socket.create_connection = fake_getaddrinfo, fake_connect
        try:
            result = wave.http_fetch("https://docs.example.com/page")
        finally:
            socket.getaddrinfo, socket.create_connection = original_getaddrinfo, original_connect
        self.assertEqual(["docs.example.com"], calls)
        self.assertEqual([("93.184.216.34", 443)], connected)
        self.assertIsNone(result["status"])

        def failing_getaddrinfo(*args, **kwargs):
            raise OSError("no such host")

        socket.getaddrinfo = failing_getaddrinfo
        try:
            self.assertEqual((None, wave.DNS_UNRESOLVED), wave.resolve_public("nowhere.example", 443))
            result = wave.http_fetch("https://nowhere.example/x")
        finally:
            socket.getaddrinfo = original_getaddrinfo
        self.assertIn("blocked", result["error"])
        self.assertEqual("unknown", wave.fetch_outcome(result))

    def test_n6_gate_fails_on_owner_review_without_acceptance(self):
        env = VerifyEnv()
        try:
            record = env.add("testing-01")
            env.write_question(record, text=e2e_yaml(record).replace("2026-11-13", wave.VERIFIED_SENTINEL),
                               status="owner-review")
            problems, owner = wave.final_gate(env.state, env.worktree, env.wave_dir, ContentFakeCli(env.worktree), set())
            self.assertTrue(any("--accept-owner-review" in p for p in problems), problems)
            self.assertNotIn("ownerReviewAccepted", record)
            problems, _ = wave.final_gate(env.state, env.worktree, env.wave_dir, ContentFakeCli(env.worktree), set(),
                                          "verified after launch from user feedback")
            self.assertEqual([], problems)
            self.assertEqual("verified after launch from user feedback", record["ownerReviewAccepted"]["reason"])
        finally:
            env.cleanup()

    def test_n7_gate_uses_dump_date_and_key_ignores_problem_wording(self):
        env = VerifyEnv()
        try:
            record = env.add("testing-01")
            env.write_question(record, status="verify-pass")
            record["verdicts"].append({"round": 1, "verdict": "pass", "hash": record["contentHash"], "issues": [],
                                       "verifiedDate": "2026-11-13"})
            record["verifiedDate"] = "2026-11-13"

            class SkewedCli(ContentFakeCli):
                def __call__(self, args, worktree=None):
                    result = super().__call__(args, worktree)
                    if args[0] == "dump":
                        data = json.loads(result.stdout)
                        for question in data["questions"]:
                            question["document"]["meta"]["verified"] = "2026-11-01"
                        result = subprocess.CompletedProcess(args, 0, json.dumps(data), "")
                    return result

            problems, _ = wave.final_gate(env.state, env.worktree, env.wave_dir, SkewedCli(env.worktree), set())
            self.assertTrue(any("in dump" in p for p in problems), problems)
        finally:
            env.cleanup()
        env = WaveEnv()
        try:
            env.add("testing-01")
            env.register("gen-testing-1", ["testing-01"])
            first = env.batch("msgbatch_k1", {"gen-testing-1": ("failed", "stop_reason=max_tokens", "{", ".raw.txt")})
            env.apply(first)
            reworded = env.batch("msgbatch_k2", {"gen-testing-1": ("failed", 'stop_reason="MaxTokens"', "{", ".raw.txt")})
            env.registry["gen-testing-1"]["applied"] = False
            env.apply(reworded)
            self.assertEqual(1, len(env.state["testing-01"]["failures"]))
        finally:
            env.cleanup()


class FixRound3Tests(unittest.TestCase):
    """Regression tests for the fix round 2 re-review (three minors)."""

    def patched_socket(self, answers, connect):
        import socket
        originals = (socket.getaddrinfo, socket.create_connection)
        self.addCleanup(lambda: setattr(socket, "getaddrinfo", originals[0]) or
                        setattr(socket, "create_connection", originals[1]))
        socket.getaddrinfo = lambda host, port, *a, **k: [
            (socket.AF_INET6 if ":" in ip else socket.AF_INET, socket.SOCK_STREAM, 6, "", (ip, port)) for ip in answers]
        socket.create_connection = connect

    def test_all_addresses_checked_and_tried_in_order_and_socket_closed_on_tls_failure(self):
        attempts, closed = [], []

        class FakeSocket:
            def close(self):
                closed.append(True)

        def connect(address, timeout=None, *args, **kwargs):
            attempts.append(address[0])
            if address[0] == "2606:2800:220:1::1":
                raise OSError("network unreachable")
            return FakeSocket()

        self.patched_socket(["2606:2800:220:1::1", "93.184.216.34"], connect)
        result = wave.http_fetch("https://docs.example.com/page")
        self.assertEqual(["2606:2800:220:1::1", "93.184.216.34"], attempts)
        self.assertEqual([True], closed)
        self.assertIsNone(result["status"])
        self.assertEqual((["2606:2800:220:1::1", "93.184.216.34"], None),
                         wave.resolve_public("docs.example.com", 443))

    def test_any_private_address_blocks_the_host(self):
        attempts = []
        self.patched_socket(["93.184.216.34", "10.0.0.7"], lambda address, *a, **k: attempts.append(address))
        result = wave.http_fetch("https://docs.example.com/page")
        self.assertIn("non-public", result["error"])
        self.assertEqual([], attempts)

    def test_userinfo_urls_are_rejected(self):
        self.assertEqual("URL carries userinfo", wave.public_https("https://user:secret@learn.microsoft.com/x",
                                                                   resolve_dns=False))
        lookups = []
        self.patched_socket([], lambda *a, **k: lookups.append(a))
        result = wave.http_fetch("https://user@learn.microsoft.com/x")
        self.assertIn("userinfo", result["error"])
        self.assertEqual([], lookups)
        with self.assertRaises(ValueError):
            wave._pinned_get("https://u:p@learn.microsoft.com/x", ["93.184.216.34"])

    def test_owner_review_acceptance_is_bound_to_content_hash(self):
        env = VerifyEnv()
        try:
            record = env.add("testing-01")
            env.write_question(record, text=e2e_yaml(record).replace("2026-11-13", wave.VERIFIED_SENTINEL),
                               status="owner-review")
            problems, _ = wave.final_gate(env.state, env.worktree, env.wave_dir, ContentFakeCli(env.worktree), set(),
                                          "post-launch feedback")
            self.assertEqual([], problems)
            self.assertEqual(wave.file_hash(env.worktree, record), record["ownerReviewAccepted"]["hash"])
            markdown, rows = wave.build_report(env.state, env.targets, "g1a", {}, worktree=env.worktree)
            self.assertEqual("UNVERIFIED (accepted: post-launch feedback)", rows[0]["verified"])
            path = env.worktree / record["path"]
            path.write_text(path.read_text(encoding="utf-8").replace("Answer", "Edited answer"), encoding="utf-8")
            markdown, rows = wave.build_report(env.state, env.targets, "g1a", {}, worktree=env.worktree)
            self.assertIn("acceptance stale", rows[0]["verified"])
            self.assertNotIn("accepted at the final gate", markdown)
            self.assertIn("earlier acceptance is stale", markdown)
        finally:
            env.cleanup()


class PoliteFetchTests(unittest.TestCase):
    def test_retries_429_then_succeeds_and_spaces_same_host(self):
        calls, sleeps = [], []
        replies = iter([{"status": 429}, {"status": 200}, {"status": 200}])

        def fake(url):
            calls.append(url)
            return next(replies)

        clock = iter([0.0, 0.0, 0.1, 0.1, 0.2, 0.2])
        fetch = wave.polite_fetch(fake, sleep=sleeps.append, clock=lambda: next(clock),
                                  backoff=(5.0,), min_interval=1.0)
        self.assertEqual(200, fetch("https://learn.microsoft.com/a")["status"])
        self.assertEqual(200, fetch("https://learn.microsoft.com/b")["status"])
        self.assertEqual(["https://learn.microsoft.com/a"] * 2 + ["https://learn.microsoft.com/b"], calls)
        self.assertIn(5.0, sleeps)
        self.assertTrue(any(0 < s <= 1.0 for s in sleeps if s != 5.0))

    def test_gives_up_after_backoff_with_last_429(self):
        fetch = wave.polite_fetch(lambda url: {"status": 429}, sleep=lambda s: None, clock=lambda: 0.0,
                                  backoff=(1.0, 2.0), min_interval=0.0)
        self.assertEqual(429, fetch("https://example.com/x")["status"])


class TaskFileSizeTests(unittest.TestCase):
    def test_split_respects_byte_cap_without_estimate(self):
        task = {"instructions": "x", "items": []}
        items = [("g", {"id": f"i{n}", "text": "y" * 400}) for n in range(10)]
        bins = wave.split_by_estimate(task, items, Path("."), None, max_bytes=1500)
        self.assertGreater(len(bins), 1)
        self.assertEqual(10, sum(len(b) for b in bins))
        for chunk in bins:
            self.assertLessEqual(sum(wave._item_bytes(i) for i in chunk), 1500)

    def test_single_item_over_cap_is_refused(self):
        task = {"instructions": "x", "items": []}
        with self.assertRaises(wave.WaveError):
            wave.split_by_estimate(task, [("g", {"id": "big", "text": "y" * 5000})], Path("."), None,
                                   max_bytes=1000)


STOP_LIMITS = {
    "budgetUsd": 300, "budgetFloorUsd": 0, "actualStopUsd": 120, "maxActualShare": 0.6,
    "expectedActualShare": {"outline": 0.45, "dedupe": 0.45, "gen": 0.30, "verify": 0.25, "repair": 0.50},
}


def spent_ledger(usd, *submits):
    return {"readings": [{"utc": "2026-11-19T10:00:00Z", "usd": usd, "baselineUsd": 0, "outstandingAtReading": []}],
            "submits": [{"name": f"s{i}", "batchId": f"b{i}", "estimateUsd": estimate, "status": "submitted",
                         **({"stage": stage} if stage else {})} for i, (estimate, stage) in enumerate(submits)]}


class ActualStopTests(unittest.TestCase):
    def test_submit_gate_without_cutoff_has_no_cutoff_problem(self):
        targets = {"capPerSubmitUsd": 50, "capPerDayUsd": 150}
        ledger = spent_ledger(0)
        late = wave.parse_utc("2031-01-01T00:00:00Z")
        self.assertEqual([], wave.submit_gate(targets, ledger, 10, late))

    def test_ledger_ceiling_counts_outstanding_and_this_submit_at_max_share(self):
        ok, message = wave.ledger_check(spent_ledger(100), 45, 0, 300, 0, STOP_LIMITS, "gen")
        self.assertFalse(ok)
        self.assertIn("ceiling FAILED", message)
        self.assertIn("127.00", message)
        self.assertNotIn("envelope FAILED", message)
        # An outstanding estimate counts once, at the same share: 60 + (45 + 45) x 0.6 = 114.
        ok, _ = wave.ledger_check(spent_ledger(60, (45, "gen")), 45, 0, 300, 0, STOP_LIMITS, "gen")
        self.assertTrue(ok)
        ok, message = wave.ledger_check(spent_ledger(60, (45, "gen")), 60, 0, 300, 0, STOP_LIMITS, "gen")
        self.assertFalse(ok)
        self.assertIn("ceiling FAILED", message)

    def test_ledger_forecast_counts_outstanding_by_stage(self):
        ledger = spent_ledger(60, (45, "gen"))
        ok, message = wave.ledger_check(ledger, 45, 50, 300, 0, STOP_LIMITS, "gen")
        self.assertFalse(ok)
        self.assertIn("forecast FAILED", message)
        self.assertIn("137.00", message)
        self.assertNotIn("ceiling FAILED", message)
        ok, message = wave.ledger_check(ledger, 45, 20, 300, 0, STOP_LIMITS, "gen")
        self.assertTrue(ok, message)

    def test_ledger_forecast_books_an_unstaged_submit_at_max_share(self):
        # 60 + 45 x 0.6 (no stage recorded) + 13.5 + 20 = 120.5 > 120, where gen would have given 107.
        ok, message = wave.ledger_check(spent_ledger(60, (45, None)), 45, 20, 300, 0, STOP_LIMITS, "gen")
        self.assertFalse(ok)
        self.assertIn("forecast FAILED", message)

    def test_actual_stop_alone_enables_both_rules_with_default_shares(self):
        only_stop = {"budgetUsd": 300, "budgetFloorUsd": 0, "actualStopUsd": 120}
        ok, message = wave.ledger_check(spent_ledger(100), 45, 0, 300, 0, only_stop, "gen")
        self.assertFalse(ok)
        self.assertIn("ceiling FAILED", message)
        ok, message = wave.ledger_check(spent_ledger(130), 0, 0, 300, 0, only_stop, "gen")
        self.assertFalse(ok)
        self.assertIn("ceiling FAILED", message)
        self.assertIn("forecast FAILED", message)
        # Default stage shares: 60 + 45 x 0.6 (unstaged) + 45 x 0.30 (gen) + 20 = 120.5 > 120.
        ok, message = wave.ledger_check(spent_ledger(60, (45, None)), 45, 20, 300, 0, only_stop, "gen")
        self.assertFalse(ok)
        self.assertIn("forecast FAILED", message)
        ok, message = wave.ledger_check(spent_ledger(60, (45, "gen")), 45, 20, 300, 0, only_stop, "gen")
        self.assertTrue(ok, message)

    def test_ledger_envelope_still_governs_reservations(self):
        ok, message = wave.ledger_check(spent_ledger(60, (45, "gen")), 45, 20, 150, 5, STOP_LIMITS, "gen")
        self.assertFalse(ok)
        self.assertIn("envelope FAILED", message)

    def test_ledger_without_actual_stop_keeps_the_envelope_only(self):
        ok, message = wave.ledger_check(spent_ledger(100), 45, 0, 300, 0, {"budgetUsd": 300}, "gen")
        self.assertTrue(ok)
        self.assertNotIn("ceiling", message)

    def test_ledger_submit_records_the_stage(self):
        env = WaveEnv()
        try:
            task = env.wave_dir / "task.json"
            task.write_text("{}", encoding="utf-8")
            sha = wave.task_sha256(task)
            wave.write_json_atomic(env.wave_dir / "ledger.json", {"taskFiles": [{"sha256": sha}], "submits": []})
            argv = ["--wave-dir", str(env.wave_dir), "--worktree", str(env.worktree), "ledger-submit", "--name", "n",
                    "--task", str(task), "--plan-id", "p", "--batch-id", "b1", "--estimate", "10",
                    "--confirmation", "c", "--stage", "gen"]
            self.assertEqual(0, wave.main(argv))
            self.assertEqual("gen", wave.read_json(env.wave_dir / "ledger.json")["submits"][0]["stage"])
            with self.assertRaises(SystemExit):
                wave.main(argv[:-1] + ["nonsense"])
        finally:
            env.cleanup()


def wave2_targets(topics, **acceptance):
    targets = small_targets(topics)
    targets["policies"]["leadArchitectForbiddenTypes"] = ["output", "find-bug"]
    targets["acceptance"] = {"minShare": 0.85, "basis": "new",
                             "levelFloors": {"junior": 4, "middle": 8, "senior": 7, "lead": 3, "architect": 2},
                             **acceptance}
    return targets


def plan_of(state):
    counts = {}
    for record in state.values():
        counts[record["topic"]] = counts.get(record["topic"], 0) + 1
    return counts


def disk_reader(worktree):
    def read(root, ref, path):
        file = Path(worktree) / path
        return file.read_text(encoding="utf-8") if file.is_file() else None
    return read


def planned_state(env, topic, level, count, status="planned"):
    for i in range(1, count + 1):
        env.add(f"{topic}-{level}-{i:02d}", topic=topic, level=level, type_="open")["status"] = status


class OutlinePolicyTests(unittest.TestCase):
    def test_check_outline_rejects_output_and_find_bug_on_lead_and_architect(self):
        targets = wave2_targets({"testing": {"levels": {"middle": 1, "lead": 2, "architect": 1}}})
        outlines = {"testing": [stub("a", "lead", "output"), stub("b", "architect", "find-bug"),
                                stub("c", "lead", "open"), stub("d", "middle", "output")]}
        failures = wave.check_outline(outlines, remaining_of(targets), {}, targets)
        forbidden = [f for f in failures if "is not allowed at level" in f]
        self.assertEqual(2, len(forbidden), failures)
        self.assertTrue(any("testing/a" in f and "output" in f for f in forbidden))
        self.assertTrue(any("testing/b" in f and "find-bug" in f for f in forbidden))

    def test_check_outline_without_the_policy_key_allows_them(self):
        targets = small_targets({"testing": {"levels": {"lead": 1}}})
        failures = wave.check_outline({"testing": [stub("a", "lead", "output")]}, remaining_of(targets), {}, targets)
        self.assertEqual([], [f for f in failures if "not allowed" in f])


class GenerationContextTests(unittest.TestCase):
    def setUp(self):
        self.env = WaveEnv()
        self.addCleanup(self.env.cleanup)

    def write(self, name, type_, id_="01SEED"):
        path = self.env.worktree / "content/dotnet/testing" / f"{name}.yaml"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(f"id: {id_}\nslug: {name}\ntopic: testing\nlevel: junior\ntype: {type_}\n", encoding="utf-8")
        return f"content/dotnet/testing/{name}.yaml"

    def test_generation_samples_prefer_the_stub_type_and_skip_wave_drafts(self):
        targets = wave2_targets({"testing": {"levels": {"junior": 4}}})
        targets["generationContext"] = {"topicSamples": 6, "neighbourSamples": 0}
        files = [self.write("a", "open"), self.write("b", "single"), self.write("c", "open"),
                 self.write("d", "single", id_="01WAVEDRAFT"), self.write("e", "single")]
        files.append("content/dotnet/testing/f.yaml")  # not on disk: no type, still a candidate
        got = wave.context_files("testing", targets, self.env.worktree, self.env.wave_dir, "abc", [],
                                 lambda *a: files, {"single"}, {"01WAVEDRAFT"}, disk_reader(self.env.worktree))
        self.assertEqual(["docs/content-wave-1/outline/testing.json", "content/dotnet/testing/b.yaml",
                          "content/dotnet/testing/e.yaml", "content/dotnet/testing/a.yaml",
                          "content/dotnet/testing/c.yaml", "content/dotnet/testing/f.yaml"], got)

    def test_wave_drafts_are_skipped_through_build_gen_tasks(self):
        targets = wave2_targets({"testing": {"levels": {"junior": 4}}})
        targets["generationContext"] = {"topicSamples": 6, "neighbourSamples": 0}
        self.env.add("testing-01")
        draft = self.env.add("testing-02")
        draft["status"] = "generated"
        draft_path = self.write("draft", "open", id_=draft["id"])
        seed = self.write("seed", "open")
        tasks, _ = wave.build_gen_tasks(self.env.state, targets, "abc", self.env.worktree, self.env.wave_dir, {}, None,
                                        None, "2026-11-19", lister=lambda *a: [draft_path, seed],
                                        reader=disk_reader(self.env.worktree))
        self.assertEqual(["docs/content-wave-1/outline/testing.json", seed], tasks[0]["items"][0]["files"])

    def test_generation_context_is_capped(self):
        targets = wave2_targets({"testing": {"levels": {"junior": 4}, "neighbours": ["csharp"]},
                                 "csharp": {"levels": {"junior": 1}}})
        targets["generationContext"] = {"topicSamples": 6, "neighbourSamples": 0}

        def lister(worktree, ref, directory):
            name = directory.rsplit("/", 1)[1]
            return [f"{directory}/{name}-{i:02d}.yaml" for i in range(20 if name == "testing" else 5)]

        got = wave.context_files("testing", targets, self.env.worktree, self.env.wave_dir, "abc", [], lister)
        self.assertEqual(7, len(got))
        self.assertEqual("docs/content-wave-1/outline/testing.json", got[0])
        self.assertTrue(all(f.startswith("content/dotnet/testing/") for f in got[1:]))

    def test_neighbour_samples_are_taken_when_asked(self):
        targets = wave2_targets({"testing": {"levels": {"junior": 4}, "neighbours": ["csharp"]},
                                 "csharp": {"levels": {"junior": 1}}})
        targets["generationContext"] = {"topicSamples": 1, "neighbourSamples": 2}
        got = wave.context_files("testing", targets, self.env.worktree, self.env.wave_dir, "abc", [],
                                 lambda w, r, d: [f"{d}/{i}.yaml" for i in range(5)])
        self.assertEqual(1 + 1 + 2, len(got))

    def test_without_generation_context_wave_one_selection_is_kept(self):
        targets = wave2_targets({"testing": {"levels": {"junior": 4}}})
        got = wave.context_files("testing", targets, self.env.worktree, self.env.wave_dir, "abc", [],
                                 lambda w, r, d: [f"{d}/{i}.yaml" for i in range(20)])
        self.assertEqual(21, len(got))


class FloorAcceptanceTests(unittest.TestCase):
    def new_env(self):
        env = WaveEnv()
        self.addCleanup(env.cleanup)
        return env

    def test_acceptance_fails_when_a_level_floor_is_missed(self):
        targets = wave2_targets({"testing": {"levels": {"middle": 2, "lead": 4}}})
        env = self.new_env()
        planned_state(env, "testing", "middle", 2, status="verify-pass")
        planned_state(env, "testing", "lead", 3, status="verify-pass")
        env.state["testing-lead-03"]["status"] = "rejected"
        env.state["testing-lead-02"]["status"] = "rejected"
        # Existing lead 1 + one survivor = 2, below min(target 4, floor 3).
        failures, _ = wave.acceptance(env.state, targets, {"testing": {"lead": 1}}, {}, plan_of(env.state))
        self.assertTrue(any(f.startswith("testing lead") for f in failures), failures)
        env.state["testing-lead-02"]["status"] = "verify-pass"
        failures, _ = wave.acceptance(env.state, targets, {"testing": {"lead": 1}}, {}, plan_of(env.state))
        self.assertEqual([], [f for f in failures if "lead" in f])

    def test_one_rejected_middle_stub_does_not_fail_the_wave(self):
        targets = wave2_targets({"testing": {"levels": {"middle": 9}}})
        env = self.new_env()
        planned_state(env, "testing", "middle", 9, status="verify-pass")
        env.state["testing-middle-09"]["status"] = "rejected"
        failures, shortfall = wave.acceptance(env.state, targets, {}, {}, plan_of(env.state))
        self.assertEqual([], failures)
        self.assertEqual(["testing middle: 1"], shortfall)

    def test_a_target_below_the_floor_is_held_to_the_target(self):
        targets = wave2_targets({"testing": {"levels": {"junior": 3, "middle": 1}}})
        env = self.new_env()
        planned_state(env, "testing", "junior", 3, status="verify-pass")
        planned_state(env, "testing", "middle", 1, status="verify-pass")
        self.assertEqual([], wave.acceptance(env.state, targets, {}, {}, plan_of(env.state))[0])
        env.state["testing-junior-03"]["status"] = "rejected"
        failures, _ = wave.acceptance(env.state, targets, {}, {}, plan_of(env.state))
        self.assertTrue(any(f.startswith("testing junior") for f in failures), failures)

    def test_acceptance_share_counts_new_stubs_only(self):
        targets = wave2_targets({"testing": {"levels": {"middle": 10}}})
        env = self.new_env()
        planned_state(env, "testing", "middle", 4, status="verify-pass")
        env.state["testing-middle-04"]["status"] = "rejected"
        # 6 seeds + 3 survivors = 9/10 passes a share over the whole topic, but 3/4 new stubs does not.
        failures, _ = wave.acceptance(env.state, targets, {"testing": {"middle": 6}}, {}, plan_of(env.state))
        self.assertTrue(any("3/4" in f for f in failures), failures)
        env.state["testing-middle-04"]["status"] = "verify-pass"
        failures, _ = wave.acceptance(env.state, targets, {"testing": {"middle": 6}}, {}, plan_of(env.state))
        self.assertEqual([], failures)

    def test_a_shortfall_does_not_shrink_the_denominator(self):
        targets = wave2_targets({"testing": {"levels": {"middle": 10}}})
        env = self.new_env()
        planned_state(env, "testing", "middle", 10, status="verify-pass")
        for record in list(env.state.values())[:2]:
            record["status"] = "shortfall"
        failures, _ = wave.acceptance(env.state, targets, {}, {}, plan_of(env.state))
        self.assertTrue(any("8/10" in f for f in failures), failures)

    def test_wave_one_acceptance_is_unchanged_without_the_new_keys(self):
        targets = small_targets({"testing": {"levels": {"middle": 4}}})
        targets["acceptance"] = {"minShare": 0.75}
        env = self.new_env()
        planned_state(env, "testing", "middle", 3, status="verify-pass")
        self.assertEqual([], wave.acceptance(env.state, targets, {}, {}, plan_of(env.state))[0])


class ShortfallTests(unittest.TestCase):
    def test_pick_shortfall_keeps_floors_and_the_topic_share(self):
        targets = wave2_targets({"csharp": {"levels": {"junior": 3, "middle": 9}}})
        env = WaveEnv(targets)
        self.addCleanup(env.cleanup)
        planned_state(env, "csharp", "junior", 3)
        planned_state(env, "csharp", "middle", 9)
        # ceil(0.85 x 12) = 11 must survive: one cut, from middle (floor 8 of 9); junior is held to its target.
        picked = wave.pick_shortfall(env.state, targets, {}, 3, plan_of(env.state))
        self.assertEqual(["csharp-middle-01"], picked)

    def test_pick_shortfall_orders_levels_and_spreads_over_topics(self):
        targets = wave2_targets({"a": {"levels": {"junior": 10, "middle": 20, "senior": 20}},
                                 "b": {"levels": {"middle": 30}}})
        env = WaveEnv(targets)
        self.addCleanup(env.cleanup)
        planned_state(env, "a", "junior", 10)
        planned_state(env, "a", "middle", 20)
        planned_state(env, "a", "senior", 20)
        planned_state(env, "b", "middle", 30)
        picked = wave.pick_shortfall(env.state, targets, {}, 6, plan_of(env.state))
        self.assertEqual(6, len(picked))
        self.assertTrue(all("-middle-" in s for s in picked), picked)
        # Slack: a 50 - ceil(42.5) = 7, b 30 - ceil(25.5) = 4; each cut lowers the slack of its topic by one.
        self.assertEqual(["a-middle-01", "a-middle-02", "a-middle-03", "a-middle-04", "b-middle-01", "a-middle-05"],
                         picked)

    def test_pick_shortfall_never_cuts_lead_or_architect(self):
        targets = wave2_targets({"testing": {"levels": {"lead": 5, "architect": 3}}})
        env = WaveEnv(targets)
        self.addCleanup(env.cleanup)
        planned_state(env, "testing", "lead", 5)
        planned_state(env, "testing", "architect", 3)
        self.assertEqual([], wave.pick_shortfall(env.state, targets, {}, 2, plan_of(env.state)))

    def test_pick_shortfall_counts_existing_questions_in_the_cell(self):
        targets = wave2_targets({"testing": {"levels": {"middle": 12}}})
        env = WaveEnv(targets)
        self.addCleanup(env.cleanup)
        planned_state(env, "testing", "middle", 6)
        # 6 existing + 6 new: the floor 8 leaves room for 4 cuts, but ceil(0.85 x 6) = 6 allows none.
        self.assertEqual([], wave.pick_shortfall(env.state, targets, {"testing": {"middle": 6}}, 4, plan_of(env.state)))

    def test_shortfall_command_is_a_dry_run_until_apply(self):
        targets = wave2_targets({"csharp": {"levels": {"junior": 3, "middle": 9}}})
        env = WaveEnv(targets)
        self.addCleanup(env.cleanup)
        planned_state(env, "csharp", "junior", 3)
        planned_state(env, "csharp", "middle", 9)
        env.save()
        wave.save_planned_new(env.wave_dir, env.state)
        wave.write_json_atomic(env.wave_dir / "targets.json", targets)
        dump = env.root / "dump.json"
        dump.write_text(json.dumps({"questions": [], "readIssues": []}), encoding="utf-8")
        base = ["--wave-dir", str(env.wave_dir), "--worktree", str(env.worktree), "--dump-file", str(dump),
                "shortfall", "--count", "3"]
        self.assertEqual(0, wave.main(base))
        self.assertEqual("planned", env.load()["csharp-middle-01"]["status"])
        self.assertEqual(0, wave.main(base + ["--apply"]))
        record = env.load()["csharp-middle-01"]
        self.assertEqual("shortfall", record["status"])
        self.assertEqual("budget", record["failures"][-1]["reason"])
        self.assertEqual(1, sum(1 for r in env.state.values() if r["status"] == "shortfall"))


class PersistedPlanTests(unittest.TestCase):
    def test_allocate_persists_the_planned_new_count_per_topic(self):
        targets = wave2_targets({"testing": {"levels": {"junior": 2}}})
        env = WaveEnv(targets)
        self.addCleanup(env.cleanup)
        wave.write_json_atomic(env.wave_dir / "targets.json", targets)
        outline = env.wave_dir / "outline"
        outline.mkdir()
        stubs = [stub("alpha-feature", "junior", hinge="garbage collector generations promote survivors"),
                 stub("beta-feature", "junior", hinge="dependency injection lifetimes scoped singleton")]
        wave.assign_stub_ids("testing", stubs)
        (outline / "testing.json").write_text(json.dumps({"topic": "testing", "stubs": stubs}), encoding="utf-8")
        dump = env.root / "dump.json"
        dump.write_text(json.dumps({"questions": [], "readIssues": []}), encoding="utf-8")
        code = wave.main(["--wave-dir", str(env.wave_dir), "--worktree", str(env.worktree),
                          "--dump-file", str(dump), "allocate"])
        self.assertEqual(0, code)
        self.assertEqual({"testing": 2}, wave.load_planned_new(env.wave_dir))

    def test_late_existing_questions_do_not_lower_the_protected_minimum(self):
        targets = wave2_targets({"testing": {"levels": {"middle": 12}}})
        env = WaveEnv(targets)
        self.addCleanup(env.cleanup)
        planned_state(env, "testing", "middle", 12)
        persisted = plan_of(env.state)
        # Four questions of the topic showed up after planning: target - fresh existing would give 8 planned
        # and allow three cuts; the persisted 12 protects ceil(0.85 x 12) = 11 stubs.
        picked = wave.pick_shortfall(env.state, targets, {"testing": {"middle": 4}}, 3, persisted)
        self.assertEqual(1, len(picked))

    def test_a_topic_without_a_persisted_count_is_not_cut(self):
        targets = wave2_targets({"testing": {"levels": {"middle": 12}}})
        env = WaveEnv(targets)
        self.addCleanup(env.cleanup)
        planned_state(env, "testing", "middle", 12)
        self.assertEqual([], wave.pick_shortfall(env.state, targets, {}, 3, {}))

    def test_acceptance_fails_when_no_planned_count_is_persisted(self):
        targets = wave2_targets({"testing": {"levels": {"middle": 20}}})
        failures, _ = wave.acceptance({}, targets, {"testing": {"middle": 10}}, {}, {})
        self.assertTrue(any(f.startswith("testing:") and "planned count" in f for f in failures), failures)
        failures, _ = wave.acceptance({}, targets, {"testing": {"middle": 20}}, {}, {})
        self.assertEqual([], [f for f in failures if "planned count" in f])


class SampleReaderTests(unittest.TestCase):
    def test_sample_ids_and_types_are_read_at_the_requested_ref(self):
        env = WaveEnv()
        self.addCleanup(env.cleanup)
        targets = wave2_targets({"testing": {"levels": {"junior": 4}}})
        targets["generationContext"] = {"topicSamples": 6, "neighbourSamples": 0}
        on_disk = env.worktree / "content/dotnet/testing/a.yaml"
        on_disk.parent.mkdir(parents=True, exist_ok=True)
        on_disk.write_text("id: 01WORKTREE\ntype: single\n", encoding="utf-8")
        seen = []

        def reader(worktree, ref, path):
            seen.append((ref, path))
            return {"content/dotnet/testing/a.yaml": "id: 01WAVE\ntype: open\n",
                    "content/dotnet/testing/b.yaml": "id: 01B\ntype: single\n"}.get(path)

        files = ["content/dotnet/testing/a.yaml", "content/dotnet/testing/b.yaml"]
        got = wave.context_files("testing", targets, env.worktree, env.wave_dir, "abc123", [],
                                 lambda *a: files, {"single"}, {"01WAVE"}, reader)
        self.assertEqual(["docs/content-wave-1/outline/testing.json", "content/dotnet/testing/b.yaml"], got)
        self.assertEqual({"abc123"}, {ref for ref, _ in seen})


class PlanCountsCommandTests(unittest.TestCase):
    def snapshot(self, root):
        return {p.relative_to(root).as_posix(): p.read_bytes() for p in sorted(Path(root).rglob("*")) if p.is_file()}

    def run_command(self, with_prompts):
        targets = small_targets({"testing": {"levels": {"junior": 4}}})
        targets["typeMix"] = {"open": 2, "single": 2, "multi": 0, "output": 0, "find-bug": 0, "situation": 0}
        env = WaveEnv(targets)
        self.addCleanup(env.cleanup)
        env.add("testing-01")
        env.save()
        wave.write_json_atomic(env.wave_dir / "targets.json", targets)
        wave.write_json_atomic(env.wave_dir / "ledger.json", {"submits": []})
        if with_prompts:
            (env.wave_dir / "prompts").mkdir()
            (env.wave_dir / "prompts" / "outline.md").write_text("prompt", encoding="utf-8")
        dump = env.root / "dump.json"
        dump.write_text(json.dumps({"questions": [], "topics": {"dotnet": [
            {"key": "testing", "locales": {"en": {"title": "Testing", "description": "Covers testing."}}}]},
            "redirects": {"dotnet": []}, "readIssues": []}), encoding="utf-8")
        before = self.snapshot(env.wave_dir)
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = wave.main(["--wave-dir", str(env.wave_dir), "--worktree", str(env.worktree),
                              "--dump-file", str(dump), "plan-counts"])
        self.assertEqual(0, code)
        self.assertEqual(before, self.snapshot(env.wave_dir))
        return out.getvalue()

    def test_plan_counts_writes_nothing(self):
        text = self.run_command(with_prompts=False)
        self.assertIn("testing junior: 0 / 4 / 4", text)
        self.assertIn("testing: open 2, single 2", text)
        self.assertNotIn("outline items", text)

    def test_plan_counts_prints_outline_item_sizes_once_prompts_exist(self):
        text = self.run_command(with_prompts=True)
        self.assertRegex(text, r"outline-testing: \d+")


if __name__ == "__main__":
    unittest.main()
