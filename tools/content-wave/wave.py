"""Content wave driver: state, ledger, preflight and outline steps of content wave 1 (Plan 4).

Stdlib only. YAML is never parsed here: the C# reader (`ContentCli dump`) is the only YAML parser.
Every step fails closed: a problem stops the step with a message and nothing is written.
"""

from __future__ import annotations

import argparse
import datetime as dt
import glob
import hashlib
import html.parser
import json
import math
import os
import re
import secrets
import subprocess
import sys
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_SERVER_REPO = Path("E:/GIT/dev-interview-trainer")
CLI_PROJECT = "tools/Trainer.ContentCli"

PROJECT = "dev-interview-trainer"
MODELS = {"claude-opus-5-5", "claude-sonnet-5-5"}
EFFORTS = {"medium", "high"}
MAX_OUTPUT_TOKENS = 32000
MAX_ITEM_CHARS = 400_000
ITEM_ID_RE = re.compile(r"^[A-Za-z0-9_-]{1,64}$")
WINDOWS_DEVICE_NAMES = {"con", "prn", "aux", "nul"} | {f"com{i}" for i in range(1, 10)} | {f"lpt{i}" for i in range(1, 10)}
SLUG_RE = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
MAX_SLUG = 80
LEVELS = ["junior", "middle", "senior", "lead", "architect"]
TYPES = ["open", "single", "multi", "output", "find-bug", "situation"]
CODE_TYPES = ("output", "find-bug")
SCHEMA_COMMENT = "# yaml-language-server: $schema=../../schema/question.schema.json"

CROCKFORD = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

STATUSES = ["planned", "generated", "gen-failed", "quarantined", "verify-pass", "needs-repair", "unverified",
            "repaired", "owner-review", "rejected", "shortfall", "final", "owner-edited"]
TERMINAL_STATUSES = {"verify-pass", "owner-review", "rejected", "shortfall"}


class WaveError(Exception):
    """A step cannot continue; the message says why."""


def utc_now() -> dt.datetime:
    return dt.datetime.now(dt.timezone.utc)


def utc_stamp(moment: dt.datetime | None = None) -> str:
    return (moment or utc_now()).astimezone(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def parse_utc(text: str) -> dt.datetime:
    return dt.datetime.strptime(text, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=dt.timezone.utc)


# ---------------------------------------------------------------- ids and hashes

def new_ulid(now_ms: int | None = None, rand: bytes | None = None) -> str:
    """48-bit millisecond time plus 80 random bits, Crockford base32 (26 characters)."""
    if now_ms is None:
        now_ms = time.time_ns() // 1_000_000
    if rand is None:
        rand = secrets.token_bytes(10)
    if not 0 <= now_ms < (1 << 48):
        raise ValueError("ULID time out of range")
    if len(rand) != 10:
        raise ValueError("ULID randomness must be 10 bytes")
    value = (now_ms << 80) | int.from_bytes(rand, "big")
    chars = []
    for _ in range(26):
        chars.append(CROCKFORD[value & 31])
        value >>= 5
    return "".join(reversed(chars))


VERIFIED_TOKEN = "<verified>"
# A wave file that no fact-check pass has verified carries this `meta.verified` value. The C# reader requires a date,
# so the field cannot be omitted; `apply` forces it, only `verdicts` (pass), `owner-check` and `owner-approve` replace
# it, and `final-gate` checks that a published date belongs to the pass that set it.
VERIFIED_SENTINEL = "1970-01-01"


def normalise_text(text: str) -> str:
    if text.startswith("\ufeff"):
        text = text[1:]
    return text.replace("\r\n", "\n").replace("\r", "\n")


def _mask_verified(text: str) -> str:
    """Replaces the value of `meta.verified` only; a `verified:` line elsewhere (code, Markdown) is content."""
    lines = text.split("\n")
    in_meta = False
    for index, line in enumerate(lines):
        if re.match(r"^meta:\s*\{", line):
            lines[index] = re.sub(r"(\bverified:\s*)[^,}\s]+", r"\g<1>" + VERIFIED_TOKEN, line)
            in_meta = False
            continue
        if re.match(r"^meta:\s*(#.*)?$", line):
            in_meta = True
            continue
        if in_meta:
            if line and not line[0].isspace() and not line.startswith("#"):
                in_meta = False
                continue
            match = re.match(r"^(\s+verified:\s*)(\S.*)?$", line)
            if match and len(match.group(1)) - len(match.group(1).lstrip()) == _meta_indent(lines, index):
                lines[index] = match.group(1) + VERIFIED_TOKEN
    return "\n".join(lines)


def _meta_indent(lines: list[str], index: int) -> int:
    """Indent of the direct children of `meta:` (the first non-empty line after it)."""
    start = index
    while start > 0 and not re.match(r"^meta:", lines[start]):
        start -= 1
    for line in lines[start + 1:]:
        if line.strip():
            return len(line) - len(line.lstrip())
    return -1


def content_hash(text: str) -> str:
    """SHA-256 of the LF text with the `meta.verified` value replaced, so setting the date keeps evidence valid."""
    return hashlib.sha256(_mask_verified(normalise_text(text)).encode("utf-8")).hexdigest()


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


# ---------------------------------------------------------------- atomic json

def write_json_atomic(path: Path, data) -> None:
    """Serializes first, then writes `<path>.tmp` and replaces; a failure leaves the old file and no temp file."""
    text = json.dumps(data, indent=2, ensure_ascii=False, sort_keys=False) + "\n"
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".tmp")
    try:
        with open(tmp, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(tmp, path)
    finally:
        if tmp.exists():
            tmp.unlink()


def read_json(path: Path, default=None):
    if not path.exists():
        return default
    return json.loads(path.read_text(encoding="utf-8"))


def save_state(path: Path, state: dict) -> None:
    write_json_atomic(path, state)


def load_state(path: Path) -> dict:
    return read_json(path, {})


# ---------------------------------------------------------------- ledger

def _outstanding(ledger: dict) -> list[dict]:
    return [s for s in ledger.get("submits", []) if s.get("status") == "submitted"]


def actual_spent(ledger: dict) -> float | None:
    """Wave spend from the latest `review_budget` reading.

    The server books an uncollected batch at its estimate and replaces it by the actual cost on collect, so the
    estimates of batches that were outstanding when the reading was taken are inside the reading and are taken out
    here; `ledger_check` adds every currently outstanding estimate back exactly once.
    """
    readings = ledger.get("readings", [])
    if not readings:
        return None
    latest = readings[-1]
    estimates = {s.get("batchId"): float(s.get("estimateUsd", 0)) for s in ledger.get("submits", [])}
    held = sum(estimates.get(batch_id, 0.0) for batch_id in latest.get("outstandingAtReading", []))
    return float(latest["usd"]) - float(latest.get("baselineUsd", 0)) - held


STAGES = ("outline", "dedupe", "gen", "verify", "repair")
DEFAULT_MAX_ACTUAL_SHARE = 0.6
DEFAULT_EXPECTED_ACTUAL_SHARE = {"outline": 0.45, "dedupe": 0.45, "gen": 0.30, "verify": 0.25, "repair": 0.50}


def _stage_share(shares: dict, stage: str | None, fallback: float) -> float:
    return float(shares.get(stage, fallback)) if stage else fallback


def ledger_check(ledger: dict, submit_estimate: float, forecast_remaining: float, budget: float,
                 floor: float, limits: dict | None = None, stage: str | None = None) -> tuple[bool, str]:
    """True iff every active rule holds.

    envelope: actual spent + outstanding estimates + this submit + forecast of remaining stages <= budget - floor.
    With `limits` (the targets) and `actualStopUsd` set, both rules on real spend always run: `ceiling` books every
    outstanding estimate and this submit at `maxActualShare` (default 0.6), `forecast` books them at the expected
    share of their stage (`expectedActualShare`, defaults above; a submit without a recorded stage counts at
    `maxActualShare`) and adds `forecast_remaining`, which covers only the stages after this submit.
    """
    spent = actual_spent(ledger)
    if spent is None:
        return False, "no budget reading: run review_budget and `wave.py budget --record <usd>` first"
    limits = limits or {}
    estimate = float(submit_estimate)
    forecast = float(forecast_remaining)
    outstanding_submits = _outstanding(ledger)
    outstanding = sum(float(s.get("estimateUsd", 0)) for s in outstanding_submits)
    total = spent + outstanding + estimate + forecast
    limit = float(budget) - float(floor)
    results = [("envelope", total <= limit + 1e-9,
                f"spent {spent:.2f} + outstanding {outstanding:.2f} + submit {estimate:.2f} + "
                f"forecast {forecast:.2f} = {total:.2f} vs limit {limit:.2f}")]
    stop = limits.get("actualStopUsd")
    if stop is not None:
        stop = float(stop)
        max_share = float(limits.get("maxActualShare", DEFAULT_MAX_ACTUAL_SHARE))
        shares = limits.get("expectedActualShare", DEFAULT_EXPECTED_ACTUAL_SHARE)
        booked = spent + (outstanding + estimate) * max_share
        results.append(("ceiling", booked <= stop + 1e-9,
                        f"spent {spent:.2f} + (outstanding {outstanding:.2f} + submit {estimate:.2f}) x "
                        f"{max_share:g} = {booked:.2f} vs actual stop {stop:.2f}"))
        held = sum(float(s.get("estimateUsd", 0)) * _stage_share(shares, s.get("stage"), max_share)
                   for s in outstanding_submits)
        own = estimate * _stage_share(shares, stage, max_share)
        booked = spent + held + own + forecast
        results.append(("forecast", booked <= stop + 1e-9,
                        f"spent {spent:.2f} + outstanding {held:.2f} + submit {own:.2f} + "
                        f"remaining stages {forecast:.2f} = {booked:.2f} vs actual stop {stop:.2f}"))
    failed = [name for name, ok, _ in results if not ok]
    return not failed, "; ".join(f"{name}{'' if ok else ' FAILED'}: {text}" for name, ok, text in results)


def submit_gate(targets: dict, ledger: dict, estimate: float, now: dt.datetime) -> list[str]:
    """Cutoff (when the targets set one) and server caps; the budget rules are `ledger_check`."""
    problems = []
    cutoff = targets.get("cutoffUtc")
    if cutoff and now >= parse_utc(cutoff):
        problems.append(f"past the submit cutoff {cutoff}")
    if estimate > float(targets.get("capPerSubmitUsd", math.inf)):
        problems.append(f"estimate {estimate:.2f} exceeds the per-submit cap {targets['capPerSubmitUsd']}")
    readings = ledger.get("readings", [])
    if readings:
        latest = readings[-1]
        day_total = float(latest["usd"]) + sum(
            float(s.get("estimateUsd", 0)) for s in _outstanding(ledger)
            if s.get("batchId") not in latest.get("outstandingAtReading", []))
        if day_total + estimate > float(targets.get("capPerDayUsd", math.inf)):
            problems.append(f"day total {day_total + estimate:.2f} would exceed the per-day cap {targets['capPerDayUsd']}")
    return problems


# ---------------------------------------------------------------- git preflight

def _git(repo: Path, *args: str) -> subprocess.CompletedProcess:
    return subprocess.run(["git", "-C", str(repo), *args], capture_output=True, text=True)


def git_show(worktree: Path, ref: str, path: str) -> str | None:
    """Text of `path` at `ref`, None when the ref has no such file."""
    result = subprocess.run(["git", "-C", str(worktree), "show", f"{ref}:{path}"], capture_output=True,
                            encoding="utf-8")
    return result.stdout if result.returncode == 0 else None


def preflight(ref: str, server_repo: Path, files: list[str], worktree: Path) -> list[str]:
    """Every file exists at `ref` in the server repository and has no diff against `ref` in the worktree."""
    problems = []
    if _git(server_repo, "rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}").returncode != 0:
        return [f"server repository {server_repo} cannot resolve ref {ref}"]
    if _git(worktree, "rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}").returncode != 0:
        return [f"worktree {worktree} cannot resolve ref {ref}"]
    for file in files:
        if _git(server_repo, "cat-file", "-e", f"{ref}:{file}").returncode != 0:
            problems.append(f"{file}: not present at {ref} in {server_repo}")
            continue
        diff = _git(worktree, "diff", "--quiet", ref, "--", file)
        if diff.returncode == 1:
            problems.append(f"{file}: differs from {ref} in the worktree (commit it first)")
        elif diff.returncode != 0:
            problems.append(f"{file}: git diff failed: {diff.stderr.strip()}")
    return problems


# ---------------------------------------------------------------- task files

def _match(pattern: str, worktree: Path) -> list[str]:
    if os.path.isabs(pattern) or pattern.startswith("..") or "/../" in pattern:
        return []
    matches = glob.glob(pattern, root_dir=worktree, recursive=True)
    return sorted(m.replace("\\", "/") for m in matches if (worktree / m).is_file())


def task_files(task: dict, worktree: Path) -> list[str]:
    """Every repository file the task refers to (shared files and item files, globs expanded)."""
    seen = set()
    for pattern in task.get("sharedFiles", []):
        seen.update(_match(pattern, worktree))
    for item in task.get("items", []):
        for pattern in item.get("files", []):
            seen.update(_match(pattern, worktree))
    return sorted(seen)


def _chars(worktree: Path, file: str) -> int:
    return len((worktree / file).read_text(encoding="utf-8"))


def check_task_file(task: dict, worktree: Path) -> list[str]:
    problems = []
    if task.get("project") != PROJECT:
        problems.append(f"project must be {PROJECT}")
    if not task.get("ref"):
        problems.append("ref is missing")
    if task.get("model") not in MODELS:
        problems.append(f"model {task.get('model')!r} is not one of {sorted(MODELS)}")
    if task.get("effort") not in EFFORTS:
        problems.append(f"effort {task.get('effort')!r} is not one of {sorted(EFFORTS)}")
    max_tokens = task.get("maxOutputTokens")
    if not isinstance(max_tokens, int) or not 0 < max_tokens <= MAX_OUTPUT_TOKENS:
        problems.append(f"maxOutputTokens {max_tokens!r} must be an integer in 1..{MAX_OUTPUT_TOKENS}")
    for pattern in task.get("sharedFiles", []):
        if not _match(pattern, worktree):
            problems.append(f"shared file pattern {pattern} matches no file")
    items = task.get("items", [])
    if not items:
        problems.append("no items")
    seen_ids = set()
    for item in items:
        item_id = item.get("id", "")
        if not ITEM_ID_RE.match(item_id):
            problems.append(f"item id {item_id!r} must match {ITEM_ID_RE.pattern}")
        if item_id.lower() in WINDOWS_DEVICE_NAMES:
            problems.append(f"item id {item_id!r} is a Windows device name")
        if item_id in seen_ids:
            problems.append(f"item id {item_id!r} is duplicated")
        seen_ids.add(item_id)
        size = len(item.get("text", ""))
        for pattern in item.get("files", []):
            matches = _match(pattern, worktree)
            if not matches:
                problems.append(f"item {item_id}: file pattern {pattern} matches no file")
            size += sum(_chars(worktree, m) for m in matches)
        if size > MAX_ITEM_CHARS:
            problems.append(f"item {item_id}: {size:,} characters exceeds 400,000")
    return problems


def task_sha256(path: Path) -> str:
    return sha256_bytes(path.read_bytes())


# ---------------------------------------------------------------- content inventory

def existing_inventory(dump: dict, redirects: list[dict]) -> dict[str, str]:
    """slug -> EN title for existing questions; slugs reserved by redirects (`from` and `to`) -> "(redirect)"."""
    inventory: dict[str, str] = {}
    for redirect in redirects:
        for key in ("from", "to"):
            slug = redirect[key].split("/")[-1]
            inventory.setdefault(slug, "(redirect)")
    for question in dump.get("questions", []):
        document = question["document"]
        inventory[document["slug"]] = document["locales"]["en"]["title"]
    return inventory


def dump_redirects(dump: dict, stack: str = "dotnet") -> list[dict]:
    return list(dump.get("redirects", {}).get(stack, []))


def existing_counts(dump: dict, exclude_ids: set[str] | None = None) -> tuple[dict, dict]:
    """Per topic: level counts and type counts of the questions already in the bank (seeds)."""
    levels: dict[str, dict[str, int]] = {}
    types: dict[str, dict[str, int]] = {}
    for question in dump.get("questions", []):
        document = question["document"]
        if exclude_ids and document.get("id") in exclude_ids:
            continue
        topic = document["topic"]
        levels.setdefault(topic, {}).setdefault(document["level"], 0)
        levels[topic][document["level"]] += 1
        types.setdefault(topic, {}).setdefault(document["type"], 0)
        types[topic][document["type"]] += 1
    return levels, types


def remaining_counts(targets: dict, existing_levels: dict) -> dict[str, dict[str, int]]:
    remaining = {}
    for topic, spec in targets["topics"].items():
        remaining[topic] = {lv: max(0, spec["levels"].get(lv, 0) - existing_levels.get(topic, {}).get(lv, 0))
                            for lv in LEVELS}
    return remaining


# ---------------------------------------------------------------- outline checks

STOPWORDS = set("""a an the and or of to in on for with by is are be as at it its this that from when which
than then not no into over under while without within does do can may must should would will use uses using
used one two same between vs versus about how what why only each every""".split())


def hinge_words(stub: dict) -> set[str]:
    text = f"{stub.get('answerHinge', '')} {stub.get('subtopic', '')}".lower()
    return {w for w in re.findall(r"[a-z0-9#+_.<>]+", text) if w not in STOPWORDS and len(w) > 1}


def jaccard(a: set[str], b: set[str]) -> float:
    if not a or not b:
        return 0.0
    return len(a & b) / len(a | b)


def _clusters(targets: dict) -> dict[str, set[str]]:
    """Topic -> topics it is compared with (itself and its neighbours)."""
    return {topic: {topic, *spec.get("neighbours", [])} for topic, spec in targets["topics"].items()}


def check_outline(outlines: dict[str, list[dict]], remaining: dict, inventory: dict, targets: dict,
                  existing_types: dict | None = None) -> list[str]:
    existing_types = existing_types or {}
    failures: list[str] = []
    policies = targets["policies"]
    required = ("slug", "level", "type", "subtopic", "angle", "answerHinge", "sources")

    for topic in outlines:
        if topic not in targets["topics"]:
            failures.append(f"{topic}: not a topic of the targets")
    missing = [t for t, levels in remaining.items() if sum(levels.values()) > 0 and t not in outlines]
    for topic in missing:
        failures.append(f"{topic}: outline missing ({sum(remaining[topic].values())} stubs required)")

    slug_owner: dict[str, str] = {}
    for topic, stubs in outlines.items():
        for stub in stubs:
            absent = [k for k in required if k not in stub or stub[k] in (None, "", [])]
            label = f"{topic}/{stub.get('slug', '?')}"
            if absent:
                failures.append(f"{label}: missing fields {', '.join(absent)}")
            slug = stub.get("slug", "")
            if not SLUG_RE.match(slug) or len(slug) > MAX_SLUG:
                failures.append(f"{label}: slug {slug!r} is not lowercase kebab-case of at most {MAX_SLUG} characters")
            if slug in inventory:
                reason = "reserved by a redirect" if inventory[slug] == "(redirect)" else "taken by an existing question"
                failures.append(f"{label}: slug {slug} is {reason}")
            if slug in slug_owner:
                failures.append(f"{label}: slug {slug} is duplicated (also {slug_owner[slug]})")
            slug_owner.setdefault(slug, label)
            if stub.get("level") not in LEVELS:
                failures.append(f"{label}: level {stub.get('level')!r} is invalid")
            if stub.get("type") not in TYPES:
                failures.append(f"{label}: type {stub.get('type')!r} is invalid")
            if (stub.get("level") in ("lead", "architect")
                    and stub.get("type") in policies.get("leadArchitectForbiddenTypes", [])):
                failures.append(f"{label}: {stub['type']} is not allowed at level {stub['level']}")
            for source in stub.get("sources", []) or []:
                if not str(source).startswith("https://"):
                    failures.append(f"{label}: source {source!r} is not an https URL")

    for topic, stubs in outlines.items():
        if topic not in targets["topics"]:
            continue
        for level in LEVELS:
            expected = remaining.get(topic, {}).get(level, 0)
            got = sum(1 for s in stubs if s.get("level") == level)
            if expected != got:
                failures.append(f"{topic} {level}: expected {expected}, got {got}")

        spec = targets["topics"][topic]
        types = {t: existing_types.get(topic, {}).get(t, 0) for t in TYPES}
        for stub in stubs:
            if stub.get("type") in types:
                types[stub["type"]] += 1
        total = sum(types.values())
        if spec.get("peopleTopic"):
            for forbidden in policies["peopleForbiddenTypes"]:
                if types[forbidden]:
                    failures.append(f"{topic}: people topic has {types[forbidden]} {forbidden} question(s)")
            share = policies["peopleMinSituationShare"]
            if total and types["situation"] < share * total:
                failures.append(f"{topic}: situation {types['situation']}/{total} is below {share:.0%}")
        if spec.get("codeHeavy"):
            share = policies["codeHeavyMinOutputFindBugShare"]
            code = types["output"] + types["find-bug"]
            if total and code < share * total:
                failures.append(f"{topic}: output + find-bug {code}/{total} is below {share:.0%}")

    if not missing:
        tolerance = targets["typeTolerance"]
        for type_name, wanted in targets["typeMix"].items():
            got = sum(e.get(type_name, 0) for e in existing_types.values())
            got += sum(1 for stubs in outlines.values() for s in stubs if s.get("type") == type_name)
            if abs(got - wanted) > tolerance:
                failures.append(f"type mix {type_name}: expected {wanted} +-{tolerance}, got {got}")

    limit = policies["hingeJaccardMax"]
    clusters = _clusters(targets)
    flat = [(topic, stub, hinge_words(stub)) for topic, stubs in outlines.items() for stub in stubs]
    for i, (topic_a, stub_a, words_a) in enumerate(flat):
        for topic_b, stub_b, words_b in flat[i + 1:]:
            if topic_b not in clusters.get(topic_a, {topic_a}) and topic_a not in clusters.get(topic_b, {topic_b}):
                continue
            score = jaccard(words_a, words_b)
            if score >= limit:
                failures.append(f"hinge overlap {score:.2f}: {topic_a}/{stub_a.get('slug')} and "
                                f"{topic_b}/{stub_b.get('slug')}")
    return failures


# ---------------------------------------------------------------- stub ids and allocation

def assign_stub_ids(topic: str, stubs: list[dict]) -> None:
    """Gives every stub without a stubId the next free `<topic>-NN`; existing stubIds are kept."""
    used = set()
    for stub in stubs:
        match = re.match(rf"^{re.escape(topic)}-(\d+)$", stub.get("stubId", ""))
        if match:
            used.add(int(match.group(1)))
    next_number = max(used, default=0) + 1
    for stub in stubs:
        if not stub.get("stubId"):
            stub["stubId"] = f"{topic}-{next_number:02d}"
            next_number += 1


def new_record(stub_id: str, topic: str, stub: dict, ulid: str) -> dict:
    return {
        "stubId": stub_id,
        "id": ulid,
        "topic": topic,
        "slug": stub["slug"],
        "level": stub["level"],
        "type": stub["type"],
        "stub": {k: v for k, v in stub.items() if k != "stubId"},
        "status": "planned",
        "attempts": {"gen": 0, "verify": 0, "repair": 0},
        "path": f"content/dotnet/{topic}/{stub['slug']}.yaml",
        "contentHash": None,
        "yamlHistory": [],
        "evidence": {"mech": None, "parity": [], "links": []},
        "verdicts": [],
        "notes": [],
        "warnings": [],
    }


def allocate(outlines: dict, state: dict, taken_ids: set[str] | None = None) -> dict:
    """Adds a `planned` record with a fresh ULID for every stub without one; never changes an existing id."""
    taken = set(taken_ids or set()) | {r["id"] for r in state.values()}
    for topic, stubs in outlines.items():
        for stub in stubs:
            stub_id = stub.get("stubId")
            if not stub_id:
                raise WaveError(f"{topic}/{stub.get('slug')}: stub has no stubId (run outline-apply)")
            record = state.get(stub_id)
            if record is None:
                ulid = new_ulid()
                while ulid in taken:
                    ulid = new_ulid()
                taken.add(ulid)
                state[stub_id] = new_record(stub_id, topic, stub, ulid)
            elif record["status"] == "planned":
                # Dedupe may revise a stub before generation; the id stays, the plan follows the outline.
                refreshed = new_record(stub_id, topic, stub, record["id"])
                for key in ("slug", "level", "type", "stub", "path"):
                    record[key] = refreshed[key]
    return state


# ---------------------------------------------------------------- type quotas (guidance for the outline items)

# Topics where deterministic output is rare (GC timing, thread scheduling, measurements) get fewer output stubs.
OUTPUT_WEIGHT_OVERRIDES = {"clr-gc": 1.0, "concurrency": 1.0, "performance": 1.0, "oop-solid": 1.6, "patterns": 1.6}


def _type_weights(topic: str, spec: dict, policies: dict) -> dict[str, float]:
    output_ok = topic in policies.get("outputPreferredTopics", [])
    if spec.get("peopleTopic"):
        return {"open": 1.0, "single": 0.4, "multi": 0.2, "output": 0.0, "find-bug": 0.0, "situation": 3.0}
    if spec.get("codeHeavy"):
        output = OUTPUT_WEIGHT_OVERRIDES.get(topic, 2.6) if output_ok else 0.0
        return {"open": 1.0, "single": 1.2, "multi": 0.8, "output": output, "find-bug": 1.3, "situation": 0.5}
    output = OUTPUT_WEIGHT_OVERRIDES.get(topic, 0.8) if output_ok else 0.0
    return {"open": 1.2, "single": 1.1, "multi": 0.8, "output": output, "find-bug": 0.8, "situation": 1.2}


def type_quotas(targets: dict, remaining: dict, existing_types: dict) -> dict[str, dict[str, int]]:
    """Per-topic type counts whose sum per topic is the remaining stub count and whose wave-wide sum with the
    existing questions is the target type mix, respecting the people and code-heavy policies.

    Iterative proportional fitting over policy weights, then rounding with column repair and policy swaps.
    """
    policies = targets["policies"]
    topics = [t for t in targets["topics"] if sum(remaining.get(t, {}).values()) > 0]
    rows = {t: sum(remaining[t].values()) for t in topics}
    columns = {k: targets["typeMix"][k] - sum(e.get(k, 0) for e in existing_types.values()) for k in TYPES}
    if any(v < 0 for v in columns.values()):
        raise WaveError(f"existing questions exceed the type mix: {columns}")
    if sum(columns.values()) != sum(rows.values()):
        raise WaveError(f"type mix ({sum(columns.values())}) and remaining stubs ({sum(rows.values())}) differ")
    weights = {t: _type_weights(t, targets["topics"][t], policies) for t in topics}
    matrix = {t: dict(weights[t]) for t in topics}
    for _ in range(500):
        for t in topics:
            total = sum(matrix[t].values())
            for k in TYPES:
                matrix[t][k] = matrix[t][k] * rows[t] / total if total else 0.0
        for k in TYPES:
            total = sum(matrix[t][k] for t in topics)
            for t in topics:
                matrix[t][k] = matrix[t][k] * columns[k] / total if total else 0.0

    quotas = {t: {k: math.floor(matrix[t][k]) for k in TYPES} for t in topics}
    for t in topics:
        short = rows[t] - sum(quotas[t].values())
        order = sorted(TYPES, key=lambda k: matrix[t][k] - quotas[t][k], reverse=True)
        for k in order[:short]:
            quotas[t][k] += 1

    def allowed(t: str, k: str) -> bool:
        return weights[t][k] > 0

    for _ in range(1000):
        surplus = [k for k in TYPES if sum(quotas[t][k] for t in topics) > columns[k]]
        deficit = [k for k in TYPES if sum(quotas[t][k] for t in topics) < columns[k]]
        if not surplus or not deficit:
            break
        best = None
        for over in surplus:
            for under in deficit:
                for t in topics:
                    if quotas[t][over] > 0 and allowed(t, under):
                        gain = (matrix[t][under] - quotas[t][under]) + (quotas[t][over] - matrix[t][over])
                        if best is None or gain > best[0]:
                            best = (gain, t, over, under)
        if best is None:
            break
        _, t, over, under = best
        quotas[t][over] -= 1
        quotas[t][under] += 1

    def total_of(t: str) -> int:
        return rows[t] + sum(existing_types.get(t, {}).values())

    def swap_in(t: str, wanted: tuple[str, ...], need: int) -> None:
        """Moves `need` questions of the wanted types into topic t from donors, keeping column sums."""
        for _ in range(need):
            done = False
            for give in sorted(TYPES, key=lambda k: quotas[t][k], reverse=True):
                if give in wanted or quotas[t][give] == 0:
                    continue
                for donor in topics:
                    if donor == t or not allowed(donor, give):
                        continue
                    for take in wanted:
                        if quotas[donor][take] > 0 and _donor_keeps_policy(donor, take):
                            quotas[t][give] -= 1
                            quotas[t][take] += 1
                            quotas[donor][take] -= 1
                            quotas[donor][give] += 1
                            done = True
                            break
                    if done:
                        break
                if done:
                    break
            if not done:
                return

    def _donor_keeps_policy(donor: str, take: str) -> bool:
        spec = targets["topics"][donor]
        types = dict(quotas[donor])
        for k, v in existing_types.get(donor, {}).items():
            types[k] = types.get(k, 0) + v
        types[take] -= 1
        if spec.get("peopleTopic") and take == "situation":
            return types["situation"] >= policies["peopleMinSituationShare"] * total_of(donor)
        if spec.get("codeHeavy") and take in CODE_TYPES:
            return types["output"] + types["find-bug"] >= policies["codeHeavyMinOutputFindBugShare"] * total_of(donor)
        return True

    for t in topics:
        spec = targets["topics"][t]
        existing = existing_types.get(t, {})
        if spec.get("peopleTopic"):
            need = math.ceil(policies["peopleMinSituationShare"] * total_of(t)) - quotas[t]["situation"] - existing.get("situation", 0)
            if need > 0:
                swap_in(t, ("situation",), need)
        if spec.get("codeHeavy"):
            have = quotas[t]["output"] + quotas[t]["find-bug"] + existing.get("output", 0) + existing.get("find-bug", 0)
            need = math.ceil(policies["codeHeavyMinOutputFindBugShare"] * total_of(t)) - have
            if need > 0:
                swap_in(t, CODE_TYPES, need)
    return quotas


# ---------------------------------------------------------------- outline task

OUTLINE_SCHEMA = {
    "type": "object",
    "properties": {
        "topic": {"type": "string"},
        "stubs": {
            "type": "array",
            "minItems": 1,
            "items": {
                "type": "object",
                "properties": {
                    "slug": {"type": "string"},
                    "level": {"type": "string", "enum": LEVELS},
                    "type": {"type": "string", "enum": TYPES},
                    "subtopic": {"type": "string"},
                    "angle": {"type": "string"},
                    "answerHinge": {"type": "string"},
                    "sources": {"type": "array", "items": {"type": "string"}},
                },
                "required": ["slug", "level", "type", "subtopic", "angle", "answerHinge", "sources"],
                "additionalProperties": False,
            },
        },
    },
    "required": ["topic", "stubs"],
    "additionalProperties": False,
}

STUB_SCHEMA = dict(OUTLINE_SCHEMA["properties"]["stubs"]["items"])
REPLACEMENT_SCHEMA = {
    "type": "object",
    "properties": {"topic": {"type": "string"}, **STUB_SCHEMA["properties"]},
    "required": ["topic", *STUB_SCHEMA["required"]],
    "additionalProperties": False,
}

DEDUPE_SCHEMA = {
    "type": "object",
    "properties": {
        "findings": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "stubIds": {"type": "array", "items": {"type": "string"}},
                    "kind": {"type": "string", "enum": ["duplicate", "wrong-topic", "wrong-level"]},
                    "replacement": {"anyOf": [REPLACEMENT_SCHEMA, {"type": "null"}]},
                    "reason": {"type": "string"},
                },
                "required": ["stubIds", "kind", "replacement", "reason"],
                "additionalProperties": False,
            },
        },
    },
    "required": ["findings"],
    "additionalProperties": False,
}

def wave_rel(wave_dir: Path, worktree: Path, *parts: str) -> str:
    """POSIX path of `wave_dir/parts` relative to the worktree, as task files and the server name repo files.

    Both sides are resolved first: a relative `--wave-dir` must give the same text from any working directory.
    """
    target = Path(wave_dir).resolve().joinpath(*parts)
    try:
        return target.relative_to(Path(worktree).resolve()).as_posix()
    except ValueError:
        raise WaveError(f"{target} is outside the worktree {worktree}") from None


def outline_instructions(wave_dir: Path, worktree: Path) -> str:
    return (
        f"Follow {wave_rel(wave_dir, worktree, 'prompts', 'outline.md')}. Plan the question stubs of the topic named "
        "in the item, using the counts, type plan, policies and stack inventory given there. "
        "Answer with the topic key and the stubs."
    )


def dedupe_instructions(wave_dir: Path, worktree: Path) -> str:
    return (
        f"Follow {wave_rel(wave_dir, worktree, 'prompts', 'dedupe.md')}. Review every planned stub of every topic in "
        "the item against each other and against the existing inventory. "
        "Answer with the findings; an empty list when nothing needs fixing."
    )


def _covers(dump: dict, stack: str = "dotnet") -> dict[str, str]:
    return {t["key"]: t["locales"]["en"]["description"] for t in dump.get("topics", {}).get(stack, [])}


def _inventory_lines(dump: dict, targets: dict) -> list[str]:
    covers = _covers(dump)
    lines = ["Stack inventory - topics (key: Covers line):"]
    for topic in targets["topics"]:
        lines.append(f"- {topic}: {covers.get(topic, '(no description)')}")
    lines.append("")
    lines.append("Stack inventory - existing question slugs (slug: English title; \"(redirect)\" = reserved):")
    inventory = existing_inventory(dump, dump_redirects(dump))
    for slug in sorted(inventory):
        lines.append(f"- {slug}: {inventory[slug]}")
    return lines


def _policy_text(topic: str, spec: dict, policies: dict) -> str:
    if spec.get("peopleTopic"):
        return (f"people topic: no output or find-bug stubs; at least {policies['peopleMinSituationShare']:.0%} "
                "situation (counting existing questions).")
    if spec.get("codeHeavy"):
        return (f"code-heavy topic: at least {policies['codeHeavyMinOutputFindBugShare']:.0%} output + find-bug "
                "(counting existing questions).")
    return "no special policy; output only where a deterministic BCL-only snippet exists."


def build_outline_task(dump: dict, targets: dict, ref: str, wave_dir: Path, worktree: Path) -> dict:
    existing_levels, existing_types = existing_counts(dump)
    remaining = remaining_counts(targets, existing_levels)
    quotas = type_quotas(targets, remaining, existing_types)
    covers = _covers(dump)
    inventory_lines = _inventory_lines(dump, targets)
    policies = targets["policies"]
    items = []
    for topic, spec in targets["topics"].items():
        counts = remaining[topic]
        if sum(counts.values()) == 0:
            continue
        own = [q for q in dump.get("questions", []) if q["document"]["topic"] == topic]
        lines = [
            f"Topic: {topic}",
            f"Covers: {covers.get(topic, '(no description)')}",
            f"Neighbour topics (no overlapping stubs): {', '.join(spec.get('neighbours', [])) or 'none'}",
            "",
            "Stubs to plan per level (exact counts):",
            *[f"- {lv}: {counts[lv]}" for lv in LEVELS],
            f"Total: {sum(counts.values())}",
            "",
            "Type counts for this topic (match exactly; at most +-1 per type when unavoidable; the wave-wide mix is enforced at +-5):",
            *[f"- {k}: {quotas[topic][k]}" for k in TYPES],
            f"Type policy: {_policy_text(topic, spec, policies)}",
            "",
            "Existing questions of this topic (attached; do not duplicate):",
            *([f"- {q['document']['slug']}: {q['document']['locales']['en']['title']} "
               f"({q['document']['level']}, {q['document']['type']})" for q in own] or ["- none"]),
            "",
            *inventory_lines,
        ]
        items.append({
            "id": f"outline-{topic}",
            "text": "\n".join(lines) + "\n",
            "files": ["content/dotnet/topics.yaml", *[f"content/{q['path']}" for q in own]],
        })
    return {
        "project": PROJECT,
        "ref": ref,
        "model": "claude-opus-5-5",
        "effort": "high",
        "maxOutputTokens": MAX_OUTPUT_TOKENS,
        "instructions": outline_instructions(wave_dir, worktree),
        "sharedFiles": [wave_rel(wave_dir, worktree, "prompts", "outline.md"), "docs/content-guide.md",
                        "content/dotnet/topics.yaml"],
        "outputSchema": OUTLINE_SCHEMA,
        "items": items,
    }


def build_dedupe_task(dump: dict, targets: dict, outlines: dict, ref: str, wave_dir: Path, worktree: Path) -> dict:
    lines = ["Planned stubs (stubId | topic | level | type | angle | answerHinge):"]
    for topic in targets["topics"]:
        for stub in outlines.get(topic, []):
            lines.append(f"- {stub['stubId']} | {topic} | {stub['level']} | {stub['type']} | {stub['angle']} | "
                         f"{stub['answerHinge']}")
    lines.append("")
    lines.extend(_inventory_lines(dump, targets))
    return {
        "project": PROJECT,
        "ref": ref,
        "model": "claude-opus-5-5",
        "effort": "high",
        "maxOutputTokens": MAX_OUTPUT_TOKENS,
        "instructions": dedupe_instructions(wave_dir, worktree),
        "sharedFiles": [wave_rel(wave_dir, worktree, "prompts", "dedupe.md"),
                        wave_rel(wave_dir, worktree, "prompts", "outline.md"), "content/dotnet/topics.yaml"],
        "outputSchema": DEDUPE_SCHEMA,
        "items": [{"id": "dedupe-all", "text": "\n".join(lines) + "\n", "files": ["content/dotnet/redirects.yaml"]}],
    }


# ---------------------------------------------------------------- results

def extract_payload(value, required: tuple[str, ...]):
    """Finds the structured answer inside a collected result file.

    The envelope of `tasks_collect` result files is pinned by the Task 4 fixture; until then this searches the JSON
    (including JSON-encoded strings) for the first object that carries every required key, and fails closed.
    """
    queue = [value]
    while queue:
        current = queue.pop(0)
        if isinstance(current, str):
            stripped = current.strip()
            if stripped.startswith("{"):
                try:
                    queue.append(json.loads(stripped))
                except json.JSONDecodeError:
                    pass
            continue
        if isinstance(current, dict):
            if all(k in current for k in required):
                return current
            queue.extend(current.values())
        elif isinstance(current, list):
            queue.extend(current)
    return None


def _result_files(batch_dir: Path) -> dict[str, Path]:
    base = batch_dir / "results" if (batch_dir / "results").is_dir() else batch_dir
    return {p.stem: p for p in sorted(base.glob("*.json")) if p.name != "index.json"}


def outline_apply(batch_dir: Path, outline_dir: Path, targets: dict) -> list[str]:
    """Writes `outline/<topic>.json` from collected outline results; returns the topics written."""
    files = _result_files(batch_dir)
    payloads = {}
    problems = []
    for item_id, path in files.items():
        if not item_id.startswith("outline-"):
            continue
        topic = item_id[len("outline-"):]
        if topic not in targets["topics"]:
            problems.append(f"{path.name}: unknown topic {topic}")
            continue
        payload = extract_payload(json.loads(path.read_text(encoding="utf-8")), ("topic", "stubs"))
        if payload is None:
            problems.append(f"{path.name}: no outline payload (topic, stubs) found")
            continue
        if payload["topic"] != topic:
            problems.append(f"{path.name}: payload topic {payload['topic']!r} differs from item topic {topic}")
            continue
        payloads[topic] = payload
    if problems:
        raise WaveError("\n".join(problems))
    written = []
    for topic, payload in sorted(payloads.items()):
        target = outline_dir / f"{topic}.json"
        previous = read_json(target, None)
        stubs = [dict(s) for s in payload["stubs"]]
        if previous:
            raise WaveError(f"{target} exists; outline-apply never overwrites an accepted outline")
        assign_stub_ids(topic, stubs)
        write_json_atomic(target, {"topic": topic, "source": str(batch_dir.name), "stubs": stubs})
        written.append(topic)
    return written


def load_outlines(outline_dir: Path) -> dict[str, list[dict]]:
    outlines = {}
    for path in sorted(outline_dir.glob("*.json")):
        data = read_json(path)
        outlines[data["topic"]] = data["stubs"]
    return outlines


# ---------------------------------------------------------------- paths

def norm_content_path(path: str) -> str:
    """Normalises a CLI or dump path (`dotnet/x/y.yaml`, backslashes, absolute roots) to `content/dotnet/...`."""
    text = str(path).replace("\\", "/")
    while text.startswith("./"):
        text = text[2:]
    index = text.find("content/dotnet/")
    if index >= 0:
        return text[index:]
    if text.startswith("dotnet/"):
        return "content/" + text
    index = text.find("/dotnet/")
    if index >= 0:
        return "content" + text[index:]
    return text


def _rel(path: Path, worktree: Path) -> str:
    try:
        return path.resolve().relative_to(worktree.resolve()).as_posix()
    except ValueError:
        return path.as_posix()


def write_text_lf(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


# ---------------------------------------------------------------- minimal JSON schema check (the subset we emit)

def schema_errors(value, schema: dict, path: str = "$") -> list[str]:
    if "anyOf" in schema:
        if any(not schema_errors(value, option, path) for option in schema["anyOf"]):
            return []
        return [f"{path}: matches no allowed shape"]
    kind = schema.get("type")
    checks = {
        "object": lambda v: isinstance(v, dict),
        "array": lambda v: isinstance(v, list),
        "string": lambda v: isinstance(v, str),
        "null": lambda v: v is None,
        "integer": lambda v: isinstance(v, int) and not isinstance(v, bool),
        "number": lambda v: isinstance(v, (int, float)) and not isinstance(v, bool),
        "boolean": lambda v: isinstance(v, bool),
    }
    if kind and not checks[kind](value):
        return [f"{path}: expected {kind}"]
    errors = []
    if "enum" in schema and value not in schema["enum"]:
        errors.append(f"{path}: {value!r} is not one of {schema['enum']}")
    if kind == "object":
        for key in schema.get("required", []):
            if key not in value:
                errors.append(f"{path}: missing {key}")
        properties = schema.get("properties", {})
        for key, item in value.items():
            if key in properties:
                errors.extend(schema_errors(item, properties[key], f"{path}.{key}"))
            elif schema.get("additionalProperties") is False:
                errors.append(f"{path}: unexpected property {key}")
    if kind == "array":
        if len(value) < schema.get("minItems", 0):
            errors.append(f"{path}: fewer than {schema['minItems']} item(s)")
        if "items" in schema:
            for index, item in enumerate(value):
                errors.extend(schema_errors(item, schema["items"], f"{path}[{index}]"))
    return errors


# ---------------------------------------------------------------- generation tasks

GEN_SCHEMA = {
    "type": "object",
    "properties": {
        "questions": {
            "type": "array",
            "minItems": 1,
            "items": {
                "type": "object",
                "properties": {"stubId": {"type": "string"}, "yaml": {"type": "string"}},
                "required": ["stubId", "yaml"],
                "additionalProperties": False,
            },
        },
        "notes": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {"stubId": {"type": "string"}, "text": {"type": "string"}},
                "required": ["stubId", "text"],
                "additionalProperties": False,
            },
        },
    },
    "required": ["questions", "notes"],
    "additionalProperties": False,
}

def gen_instructions(wave_dir: Path, worktree: Path) -> str:
    return (
        f"Follow {wave_rel(wave_dir, worktree, 'prompts', 'generate.md')}. Write one complete question file for every "
        "stub in the item. Answer with `questions` (one {stubId, yaml} per stub, raw YAML without an id line and "
        "without Markdown fences) and `notes` (one {stubId, text} per claim you hold with less than full certainty; "
        "an empty list when none)."
    )


def repair_instructions(wave_dir: Path, worktree: Path) -> str:
    return (
        f"Follow {wave_rel(wave_dir, worktree, 'prompts', 'repair.md')} (and every rule of prompts/generate.md). "
        "Rewrite every question in the item. Answer with `questions` (one complete {stubId, yaml} per question, no id "
        "line, no Markdown fences) and `notes` (fixes that change level or type, rebuttals with their evidence; "
        "an empty list when none)."
    )


def gen_shared(wave_dir: Path, worktree: Path) -> list[str]:
    return [wave_rel(wave_dir, worktree, "prompts", "generate.md"), "docs/content-guide.md",
            "content/schema/question.schema.json", "content/dotnet/topics.yaml"]


def repair_shared(wave_dir: Path, worktree: Path) -> list[str]:
    return [wave_rel(wave_dir, worktree, "prompts", "repair.md"),
            wave_rel(wave_dir, worktree, "prompts", "generate.md"),
            "docs/content-guide.md", "content/schema/question.schema.json"]


PRICES = {"claude-opus-5-5": (2.00, 10.00), "claude-sonnet-5-5": (1.00, 5.00)}
ITEM_FILES_SOFT_LIMIT = 250_000
REGISTRY_FILE = "items.json"


def stub_payload(record: dict) -> dict:
    stub = record["stub"]
    return {"stubId": record["stubId"], "topic": record["topic"], "slug": record["slug"], "level": record["level"],
            "type": record["type"], "subtopic": stub.get("subtopic"), "angle": stub.get("angle"),
            "answerHinge": stub.get("answerHinge"), "sources": stub.get("sources", [])}


def single_stub(record: dict, targets: dict) -> bool:
    chunk = targets.get("chunk", {})
    return record["level"] in chunk.get("singleLevels", ["lead", "architect"]) or \
        record["topic"] in chunk.get("singleTopics", []) or targets["topics"].get(record["topic"], {}).get("codeHeavy")


def chunk_records(records: list[dict], targets: dict, force_single: bool = False) -> list[list[dict]]:
    """1 stub per chunk for code-heavy topics and lead/architect stubs, otherwise 2 with different types if possible."""
    chunks: list[list[dict]] = []
    by_topic: dict[str, list[dict]] = {}
    for record in sorted(records, key=lambda r: r["stubId"]):
        by_topic.setdefault(record["topic"], []).append(record)
    for topic in by_topic:
        pool = []
        for record in by_topic[topic]:
            if force_single or single_stub(record, targets):
                chunks.append([record])
            else:
                pool.append(record)
        while pool:
            first = pool.pop(0)
            partner = next((r for r in pool if r["type"] != first["type"]), pool[0] if pool else None)
            if partner is None:
                chunks.append([first])
            else:
                pool.remove(partner)
                chunks.append([first, partner])
    return chunks


def load_registry(wave_dir: Path) -> dict:
    return read_json(wave_dir / REGISTRY_FILE, {})


def save_registry(wave_dir: Path, registry: dict) -> None:
    write_json_atomic(wave_dir / REGISTRY_FILE, registry)


def pending_stubs(registry: dict, stage_prefix: str) -> set[str]:
    """Stubs of registered items of a stage that were not applied yet (built or submitted, results not in)."""
    return {s for item in registry.values() if item["stage"].startswith(stage_prefix) and not item.get("applied")
            for s in item["stubs"]}


def next_item_number(registry: dict, prefix: str, used: set[str]) -> int:
    numbers = [int(m.group(1)) for key in (*registry, *used) if (m := re.match(rf"^{re.escape(prefix)}(\d+)$", key))]
    return max(numbers, default=0) + 1


def committed_files(worktree: Path, ref: str, directory: str) -> list[str]:
    result = _git(worktree, "ls-tree", "-r", "--name-only", ref, "--", directory)
    if result.returncode != 0:
        raise WaveError(f"git ls-tree {ref} {directory} failed: {result.stderr.strip()}")
    return sorted(line for line in result.stdout.splitlines() if line.endswith(".yaml"))


def wave_markers(state: dict) -> set[str]:
    """Ids and paths of the wave's records: a draft of the wave never serves as a style sample."""
    return {r["id"] for r in state.values()} | {r["path"] for r in state.values()}


def _sample_files(worktree: Path, ref: str, directory: str, lister, skip: set[str],
                  types: set[str], reader=git_show) -> list[str]:
    """Committed questions of a directory, outside the wave, those of `types` first; each group in path order.

    Ids and types are read at `ref`, the same revision the listing comes from, not from the working tree.
    """
    preferred, rest = [], []
    for file in sorted(lister(worktree, ref, directory)):
        if file.endswith(("topics.yaml", "redirects.yaml")) or file in skip:
            continue
        text = reader(worktree, ref, file)
        scalars = top_level_scalars(text) if text is not None else {}
        if scalars.get("id") in skip:
            continue
        (preferred if scalars.get("type") in types else rest).append(file)
    return preferred + rest


def context_files(topic: str, targets: dict, worktree: Path, wave_dir: Path, ref: str, extra: list[str],
                  lister=committed_files, stub_types: set[str] | None = None,
                  markers: set[str] | None = None, reader=git_show) -> list[str]:
    """`outline/<topic>.json` plus committed questions of the topic, then of its neighbours, within a size limit.

    With `generationContext` in the targets the samples are capped instead: `topicSamples` questions of the topic
    (the stubs' types first) and `neighbourSamples` per neighbour, none of them a draft of the wave.
    """
    files = [wave_rel(wave_dir, worktree, "outline", f"{topic}.json"), *extra]
    capped = targets.get("generationContext")
    if capped is not None:
        skip = markers or set()
        names = [(topic, int(capped.get("topicSamples", 0)))]
        names += [(n, int(capped.get("neighbourSamples", 0)))
                  for n in targets["topics"].get(topic, {}).get("neighbours", [])]
        for name, limit in names:
            samples = _sample_files(worktree, ref, f"content/dotnet/{name}", lister, skip, stub_types or set(),
                                    reader)
            files.extend(f for f in samples[:limit] if f not in files)
        return files
    size = sum(_chars(worktree, f) for f in files if (worktree / f).is_file())
    for name in [topic, *targets["topics"].get(topic, {}).get("neighbours", [])]:
        for file in lister(worktree, ref, f"content/dotnet/{name}"):
            if file in files or file.endswith(("topics.yaml", "redirects.yaml")):
                continue
            chars = _chars(worktree, file) if (worktree / file).is_file() else 0
            if size + chars > ITEM_FILES_SOFT_LIMIT and name != topic:
                continue
            files.append(file)
            size += chars
    return files


def estimate_item_usd(task: dict, item: dict, worktree: Path) -> float:
    """Same method as tasks_plan: every request at its full output cap, input at 2 characters per token, +10%."""
    shared = sum(_chars(worktree, f) for p in task["sharedFiles"] for f in _match(p, worktree))
    system = len(task["instructions"]) + shared + len(json.dumps(task.get("outputSchema") or {}))
    body = len(item.get("text", "")) + sum(_chars(worktree, f) for p in item.get("files", []) for f in _match(p, worktree))
    price_in, price_out = PRICES[task["model"]]
    usd = math.ceil((system + body) / 2) * price_in / 1e6 + task["maxOutputTokens"] * price_out / 1e6
    return usd * 1.10


# The claude-api-work server refuses a task file above 2,000,000 bytes; keep a margin for the shared part.
MAX_TASK_FILE_BYTES = 1_800_000


def _item_bytes(item: dict) -> int:
    return len(json.dumps(item, ensure_ascii=False, indent=2).encode("utf-8"))


def split_by_estimate(task: dict, groups: list[tuple[str, dict]], worktree: Path, max_estimate: float | None,
                      max_bytes: int = MAX_TASK_FILE_BYTES) -> list[list[dict]]:
    """Packs items (already ordered by group) into task files whose estimate stays within `max_estimate` and
    whose serialised size stays within `max_bytes`."""
    items = [item for _, item in groups]
    base = _item_bytes({k: v for k, v in task.items() if k != "items"})
    bins: list[list[dict]] = [[]]
    total = 0.0
    size = base
    for item in items:
        cost = estimate_item_usd(task, item, worktree) if max_estimate is not None else 0.0
        if max_estimate is not None and cost > max_estimate:
            raise WaveError(f"item {item['id']} alone is estimated at {cost:.2f} > {max_estimate:.2f}")
        item_size = _item_bytes(item)
        if base + item_size > max_bytes:
            raise WaveError(f"item {item['id']} alone makes a task file of {base + item_size} bytes > {max_bytes}")
        over_cost = max_estimate is not None and total + cost > max_estimate
        if bins[-1] and (over_cost or size + item_size > max_bytes):
            bins.append([])
            total = 0.0
            size = base
        bins[-1].append(item)
        total += cost
        size += item_size
    return bins


def _group_order(targets: dict) -> dict[str, int]:
    order: dict[str, int] = {}
    for spec in targets["topics"].values():
        order.setdefault(spec.get("group", ""), len(order))
    return order


def build_question_items(chunks: list[list[dict]], targets: dict, worktree: Path, wave_dir: Path, ref: str,
                         registry: dict, stage: str, text_for, extra_files_for=None, lister=committed_files,
                         id_prefix: str = "gen",
                         markers: set[str] | None = None,
                         reader=git_show) -> tuple[list[tuple[str, dict]], dict]:
    """Items `<id_prefix>-<topic>-<k>` (k continues across task files) and their registry entries."""
    group_order = _group_order(targets)
    topic_order = {t: i for i, t in enumerate(targets["topics"])}
    used: set[str] = set()
    items = []
    entries = {}
    for chunk in sorted(chunks, key=lambda c: (group_order.get(targets["topics"][c[0]["topic"]].get("group", ""), 99),
                                               topic_order.get(c[0]["topic"], 99), c[0]["stubId"])):
        topic = chunk[0]["topic"]
        prefix = f"{id_prefix}-{topic}-"
        item_id = f"{prefix}{next_item_number(registry, prefix, used)}"
        used.add(item_id)
        extra = extra_files_for(chunk) if extra_files_for else []
        files = context_files(topic, targets, worktree, wave_dir, ref, extra, lister,
                              {r["type"] for r in chunk}, markers, reader)
        item = {"id": item_id, "text": text_for(chunk), "files": files}
        items.append((targets["topics"][topic].get("group", ""), item))
        entries[item_id] = {"stage": stage, "stubs": [r["stubId"] for r in chunk]}
    return items, entries


def gen_item_text(chunk: list[dict], today: str) -> str:
    stubs = json.dumps([stub_payload(r) for r in chunk], indent=2, ensure_ascii=False)
    return (f"Write {len(chunk)} question(s), one per stub below. Set meta.verified to the placeholder "
            f"{VERIFIED_SENTINEL}: only a fact-check pass sets the real date.\n"
            f"Keep every stub's slug, level, type and topic exactly. Do not write an id line.\n\n"
            f"Stubs (JSON):\n{stubs}\n")


def base_task(model: str, instructions: str, shared: list[str], schema: dict, ref: str, effort: str = "high") -> dict:
    return {"project": PROJECT, "ref": ref, "model": model, "effort": effort, "maxOutputTokens": MAX_OUTPUT_TOKENS,
            "instructions": instructions, "sharedFiles": shared, "outputSchema": schema, "items": []}


def build_gen_tasks(state: dict, targets: dict, ref: str, worktree: Path, wave_dir: Path, registry: dict,
                    only: list[str] | None, max_estimate: float | None, today: str, lister=committed_files,
                    retry: bool = False, reader=git_show) -> tuple[list[dict], dict]:
    """Generation (or split retry) tasks; returns the task files' contents and the new registry entries."""
    pending = pending_stubs(registry, "gen") | pending_stubs(registry, "repair")
    if retry:
        records = [r for r in state.values() if r["status"] == "gen-failed" and last_failure_stage(r) == "gen"
                   and retry_left(r, "gen")]
    elif only:
        unknown = [s for s in only if s not in state]
        if unknown:
            raise WaveError(f"unknown stubIds: {', '.join(unknown)}")
        not_planned = [s for s in only if state[s]["status"] != "planned"]
        if not_planned:
            raise WaveError(f"not planned: {', '.join(not_planned)}")
        records = [state[s] for s in only]
    else:
        records = [r for r in state.values() if r["status"] == "planned"]
    busy = [r["stubId"] for r in records if r["stubId"] in pending]
    if only and busy:
        raise WaveError(f"already in an unapplied generation or repair item: {', '.join(busy)}")
    records = [r for r in records if r["stubId"] not in pending]
    if only:
        records = [r for r in records if r["stubId"] in set(only)]
    if not records:
        raise WaveError("no stubs to generate")
    chunks = chunk_records(records, targets, force_single=retry)
    task = base_task("claude-opus-5-5", gen_instructions(wave_dir, worktree), gen_shared(wave_dir, worktree),
                     GEN_SCHEMA, ref)
    items, entries = build_question_items(chunks, targets, worktree, wave_dir, ref, registry, "gen",
                                          lambda c: gen_item_text(c, today), lister=lister,
                                          markers=wave_markers(state), reader=reader)
    tasks = []
    for chunk_items in split_by_estimate(task, items, worktree, max_estimate):
        tasks.append({**task, "items": chunk_items})
    return tasks, entries


def retry_left(record: dict, stage: str | None = None) -> bool:
    """One retry per stub and stage family: two failures of generation, or two of repair, end the stub."""
    failures = record.get("failures", [])
    if stage is None:
        stage = failures[-1]["stage"] if failures else "gen"
    return sum(1 for f in failures if f.get("stage") == stage) < 2


def last_failure_stage(record: dict) -> str | None:
    failures = record.get("failures", [])
    return failures[-1]["stage"] if failures else None


def submitted_task_names(ledger: dict) -> dict[str, str]:
    """Task file name (stem) -> batchId for every recorded submit."""
    names = {}
    for submit in (ledger or {}).get("submits", []):
        stem = Path(str(submit.get("taskFile", "")).replace("\\", "/")).stem
        if stem:
            names[stem] = submit.get("batchId")
    return names


def refuse_submitted_overwrite(name: str, wave_dir: Path) -> None:
    ledger = read_json(wave_dir / "ledger.json", {}) or {}
    submitted = submitted_task_names(ledger)
    own = re.compile(rf"^{re.escape(name)}(-[a-z])?$")
    hit = sorted(f"{n} (batch {b})" for n, b in submitted.items() if own.match(n))
    if hit:
        raise WaveError(f"refusing --overwrite: already submitted: {', '.join(hit)}; choose another --name")


def registry_for_build(args, name: str, wave_dir: Path) -> dict:
    """The registry a task builder sees. With --overwrite, the unapplied items of the task being replaced are
    dropped first, so their stubs are candidates again (they would otherwise count as pending forever)."""
    registry = load_registry(wave_dir)
    if getattr(args, "overwrite", False):
        refuse_submitted_overwrite(name, wave_dir)
        own = re.compile(rf"^{re.escape(name)}(-[a-z])?$")
        registry = {k: v for k, v in registry.items() if not (own.match(v.get("task", "")) and not v.get("applied"))}
    return registry


def write_tasks(tasks: list[dict], entries: dict, name: str, stage: str, args, wave_dir: Path) -> list[Path]:
    """Writes one or more task files (`<name>` or `<name>-a`, `-b`, ...) and registers their items."""
    registry = registry_for_build(args, name, wave_dir)
    names = [name] if len(tasks) == 1 else [f"{name}-{chr(ord('a') + i)}" for i in range(len(tasks))]
    paths = []
    for task, task_name in zip(tasks, names):
        paths.append(write_task(task, task_name, args, wave_dir))
        for item in task["items"]:
            registry[item["id"]] = {**entries[item["id"]], "task": task_name, "applied": False}
    save_registry(wave_dir, registry)
    worktree = Path(args.worktree)
    for task, path in zip(tasks, paths):
        estimate = sum(estimate_item_usd(task, item, worktree) for item in task["items"])
        print(f"{path.name}: {len(task['items'])} items, estimate about {estimate:.2f} USD at the output cap")
    return paths


# ---------------------------------------------------------------- batch results

class ResultError(WaveError):
    """A collected item cannot be used; `reason` is one of the classified failure reasons."""

    def __init__(self, reason: str, message: str):
        super().__init__(message)
        self.reason = reason


def load_batch(batch: str | Path, batch_id: str | None = None) -> dict:
    """Reads the layout `tasks_collect` writes: `<dir>/index.json` plus `<dir>/results/<id>.json|.md|.raw.txt`."""
    path = Path(str(batch).replace("\\", "/"))
    if path.is_file() and path.name == "index.json":
        base = path.parent
    elif (path / "index.json").is_file():
        base = path
    elif path.name == "results" and (path.parent / "index.json").is_file():
        base = path.parent
    else:
        raise WaveError(f"{batch}: no index.json; pass the directory tasks_collect reported")
    results = base / "results" if (base / "results").is_dir() else base
    index = json.loads((base / "index.json").read_text(encoding="utf-8"))
    if not isinstance(index, list) or any(not isinstance(e, dict) or "id" not in e for e in index):
        raise WaveError(f"{base / 'index.json'}: expected a JSON array of {{id, status, problem}}")
    if batch_id is None:
        batch_id = next((p for p in reversed(base.resolve().parts) if p.startswith("msgbatch_")), base.name)
    return {"batchId": batch_id, "dir": base, "results": results, "index": {e["id"]: e for e in index}}


def read_item_result(batch: dict, item_id: str) -> dict:
    entry = batch["index"].get(item_id, {"id": item_id, "status": "failed", "problem": "no result returned"})
    result = {"id": item_id, "status": entry.get("status"), "problem": entry.get("problem"),
              "outputTokens": entry.get("outputTokens"), "text": None, "file": None}
    for suffix in (".json", ".md", ".raw.txt"):
        candidate = batch["results"] / f"{item_id}{suffix}"
        if candidate.is_file():
            result["text"] = candidate.read_text(encoding="utf-8")
            result["file"] = candidate
            break
    tokens = result["outputTokens"]
    if result["status"] == "ok" and isinstance(tokens, int) and tokens >= MAX_OUTPUT_TOKENS:
        # The server already fails non-end_turn stops; an answer at the cap is treated as truncated regardless.
        result["status"] = "failed"
        result["problem"] = f"stop_reason=max_tokens (outputTokens {tokens} at the cap)"
    problem = entry.get("problem")
    result["key"] = sha256_bytes(json.dumps(
        [item_id, entry.get("status"), classify_failure(problem) if problem else None, result["text"] or ""],
        ensure_ascii=False).encode("utf-8"))
    # Key of fix round 1 (raw problem text): records applied before this change must still read as replays.
    result["legacyKey"] = sha256_bytes(json.dumps(
        [item_id, entry.get("status"), problem, result["text"] or ""], ensure_ascii=False).encode("utf-8"))
    result["keys"] = {result["key"], result["legacyKey"]}
    return result


def missing_registered_items(batch: dict, registry: dict) -> list[str]:
    """Items of the same task files as this batch that are absent from its index.json."""
    tasks = {registry[i]["task"] for i in batch["index"] if i in registry}
    return sorted(k for k, v in registry.items() if v.get("task") in tasks and k not in batch["index"]
                  and not v.get("applied"))


def classify_failure(problem: str | None) -> str:
    key = re.sub(r"[^a-z]", "", (problem or "").lower())
    for needle, reason in (("maxtokens", "max_tokens"), ("refusal", "refusal"), ("expired", "expired"),
                           ("cancel", "canceled"), ("notvalidjson", "malformed-json"), ("noresult", "no-result"),
                           ("pausetur", "pause_turn")):
        if needle in key:
            return reason
    return "error"


FENCE_RE = re.compile(r"^\s*```[A-Za-z0-9_+-]*[ \t]*\n(.*?)\n?[ \t]*```\s*$", re.S)


def strip_fence(text: str) -> tuple[str, bool]:
    match = FENCE_RE.match(text)
    if not match:
        return text, False
    return match.group(1), True


STAGE_KEYS = {"gen": "questions", "repair": "questions", "verify": "verdicts"}

SEVERITIES = ["blocker", "major", "minor", "unsure"]
VERDICT_SCHEMA = {
    "type": "object",
    "properties": {
        "verdicts": {
            "type": "array",
            "minItems": 1,
            "items": {
                "type": "object",
                "properties": {
                    "stubId": {"type": "string"},
                    "verdict": {"type": "string", "enum": ["pass", "fix", "reject"]},
                    "issues": {
                        "type": "array",
                        "items": {
                            "type": "object",
                            "properties": {
                                "severity": {"type": "string", "enum": SEVERITIES},
                                "field": {"type": "string"},
                                "claim": {"type": "string"},
                                "problem": {"type": "string"},
                                "suggestedFix": {"type": "string"},
                            },
                            "required": ["severity", "field", "claim", "problem", "suggestedFix"],
                            "additionalProperties": False,
                        },
                    },
                },
                "required": ["stubId", "verdict", "issues"],
                "additionalProperties": False,
            },
        },
    },
    "required": ["verdicts"],
    "additionalProperties": False,
}


def stage_schema(stage: str) -> dict:
    return VERDICT_SCHEMA if stage == "verify" else GEN_SCHEMA


def validate_result(result: dict, item: dict, stage: str) -> tuple[list[dict], list[str]]:
    """Checks one collected item against its stage schema and expected stubs; raises ResultError when unusable.

    Generation and repair: every expected stubId exactly once and no unknown stubId, else the item fails.
    Verify: entries for stubIds seen exactly once are returned; missing, duplicate or unknown ones are warnings and
    the caller marks those stubs unverified.
    """
    warnings: list[str] = []
    text = result.get("text")
    if result.get("status") != "ok":
        reason = classify_failure(result.get("problem"))
        if reason != "malformed-json" or text is None:
            raise ResultError(reason, f"item {result.get('id')}: {result.get('problem') or 'failed'}")
    if text is None:
        raise ResultError("no-result", f"item {result.get('id')}: no result file")
    body, fenced = strip_fence(text)
    if fenced:
        warnings.append(f"item {result.get('id')}: stripped a Markdown fence around the answer")
    try:
        payload = json.loads(body)
    except json.JSONDecodeError as error:
        raise ResultError("malformed-json", f"item {result.get('id')}: answer is not valid JSON ({error})") from None
    errors = schema_errors(payload, stage_schema(stage))
    if errors:
        raise ResultError("schema", f"item {result.get('id')}: " + "; ".join(errors[:5]))
    key = STAGE_KEYS[stage]
    entries = payload[key]
    expected = list(item["stubs"])
    counts: dict[str, int] = {}
    for entry in entries:
        counts[entry["stubId"]] = counts.get(entry["stubId"], 0) + 1
    missing = [s for s in expected if s not in counts]
    duplicate = [s for s, n in counts.items() if n > 1]
    unknown = [s for s in counts if s not in expected]
    problems = ([f"missing {', '.join(missing)}"] if missing else []) + \
               ([f"duplicate {', '.join(duplicate)}"] if duplicate else []) + \
               ([f"unknown {', '.join(unknown)}"] if unknown else [])
    if problems and stage != "verify":
        raise ResultError("stub-mismatch", f"item {result.get('id')}: " + "; ".join(problems))
    if problems:
        warnings.append(f"item {result.get('id')}: " + "; ".join(problems))
        entries = [e for e in entries if counts[e["stubId"]] == 1 and e["stubId"] in expected]
    out = []
    for entry in entries:
        entry = dict(entry)
        if "yaml" in entry:
            yaml, fenced = strip_fence(entry["yaml"])
            if fenced:
                warnings.append(f"{entry['stubId']}: stripped a Markdown fence around the YAML")
                entry["yaml"] = yaml
        out.append(entry)
    if stage != "verify":
        out = [{**e, "notes": [n["text"] for n in payload.get("notes", []) if n["stubId"] == e["stubId"]]} for e in out]
    return out, warnings


# ---------------------------------------------------------------- apply

TOP_LEVEL_RE = re.compile(r"^(slug|topic|level|type|stack|id)\s*:\s*(.*?)\s*$")


def top_level_scalars(body: str) -> dict[str, str]:
    values = {}
    for line in body.split("\n"):
        match = TOP_LEVEL_RE.match(line)
        if match and match.group(1) not in values:
            value = re.sub(r"\s+#.*$", "", match.group(2)).strip()
            if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
                value = value[1:-1]
            values[match.group(1)] = value
    return values


def prepare_body(yaml: str) -> str:
    """Model YAML without the schema comment; raises ResultError for an id line or a foreign schema comment."""
    text = normalise_text(yaml).lstrip("\n")
    lines = text.split("\n")
    if lines and lines[0].strip() == SCHEMA_COMMENT:
        lines = lines[1:]
    elif lines and lines[0].startswith("# yaml-language-server"):
        raise ResultError("format", f"wrong schema comment: {lines[0]}")
    body = "\n".join(lines).strip("\n") + "\n"
    if "id" in top_level_scalars(body):
        raise ResultError("id-line", "model YAML contains an id line")
    return body


def requested_change(record: dict, field: str, value: str) -> bool:
    """A level or type change is allowed in repair only when a verdict issue on that field requested it."""
    for verdict in record.get("verdicts", []):
        for issue in verdict.get("issues", []):
            if issue.get("field") == field and value in str(issue.get("suggestedFix", "")):
                return True
    return False


def check_stub_fields(body: str, record: dict, stage: str) -> dict:
    """Returns allowed changes {field: value}; raises ResultError on drift from the stub."""
    values = top_level_scalars(body)
    changes = {}
    for field in ("slug", "topic", "level", "type"):
        got = values.get(field)
        if got == record[field]:
            continue
        if stage == "repair" and field in ("level", "type") and got and requested_change(record, field, got):
            changes[field] = got
            continue
        raise ResultError("stub-drift", f"{field} {got!r} differs from the stub ({record[field]!r})")
    return changes


def retire_file(record: dict, worktree: Path, target: Path) -> Path | None:
    """Moves a question file out of content (kept byte for byte as `.yaml.txt`) so it is not published.

    An earlier copy with different bytes is kept: the new one gets `.2`, `.3`, ... before `.yaml.txt`.
    """
    source = worktree / record["path"]
    if not source.is_file():
        return None
    data = source.read_bytes()
    target.parent.mkdir(parents=True, exist_ok=True)
    stem = target.name[:-len(".yaml.txt")] if target.name.endswith(".yaml.txt") else target.name
    candidate, number = target, 1
    while candidate.exists() and candidate.read_bytes() != data:
        number += 1
        candidate = target.with_name(f"{stem}.{number}.yaml.txt")
    candidate.write_bytes(data)
    source.unlink()
    record.setdefault("retired", []).append({"file": candidate.as_posix(), "utc": utc_stamp()})
    return candidate


def fail_record(record: dict, stage: str, batch_id: str, item_id: str, reason: str, message: str,
                worktree: Path, wave_dir: Path, result_key: str | None = None) -> None:
    record.setdefault("failures", []).append({"stage": stage, "batchId": batch_id, "itemId": item_id,
                                              "resultKey": result_key, "reason": reason, "message": message,
                                              "utc": utc_stamp()})
    if retry_left(record, stage):
        record["status"] = "gen-failed"
    else:
        record["status"] = "shortfall"
        retire_file(record, worktree, wave_dir / "quarantine" / f"{record['stubId']}.yaml.txt")


def apply_batch(batch_path, stage: str, wave_dir: Path, worktree: Path, batch_id: str | None = None) -> dict:
    """Applies collected generation or repair results; idempotent on replay."""
    if stage not in ("gen", "repair"):
        raise WaveError(f"unknown stage {stage}")
    state = load_state(wave_dir / "state.json")
    registry = load_registry(wave_dir)
    batch = load_batch(batch_path, batch_id)
    unknown = [i for i in batch["index"] if i not in registry or registry[i]["stage"] != stage]
    if unknown:
        raise WaveError(f"items not registered as {stage} items: {', '.join(unknown)}")
    summary = {"written": [], "skipped": [], "failed": [], "warnings": []}
    summary["warnings"] += [f"registered item {i} of the same task is missing from index.json"
                            for i in missing_registered_items(batch, registry)]
    allowed = {"gen": {"planned", "gen-failed"},
               "repair": {"needs-repair", "quarantined", "gen-failed"}}[stage]
    new_status = "generated" if stage == "gen" else "repaired"
    for item_id in batch["index"]:
        item = registry[item_id]
        records = [state[s] for s in item["stubs"]]
        result = read_item_result(batch, item_id)
        key = result["key"]
        keys = result["keys"]
        result_file = _rel(result["file"], worktree) if result["file"] else None
        try:
            entries, warnings = validate_result(result, item, stage)
        except ResultError as error:
            for record in records:
                if any(f.get("resultKey") in keys and f["itemId"] == item_id for f in record.get("failures", [])):
                    summary["skipped"].append(record["stubId"])
                    continue
                if record["status"] not in allowed:
                    summary["warnings"].append(f"{record['stubId']}: status {record['status']} ignores failed "
                                               f"{stage} item {item_id}")
                    continue
                fail_record(record, stage, batch["batchId"], item_id, error.reason, str(error), worktree, wave_dir, key)
                summary["failed"].append(f"{record['stubId']}: {error.reason}: {error}")
            item["applied"] = True
            continue
        summary["warnings"].extend(warnings)
        for entry in entries:
            record = state[entry["stubId"]]
            raw_sha = sha256_bytes(entry["yaml"].encode("utf-8"))
            same = [h for h in record["yamlHistory"] if h["sha256"] == raw_sha]
            if any(h.get("resultKey") in keys and h["itemId"] == item_id for h in same):
                summary["skipped"].append(record["stubId"])
                continue
            history = {"stage": stage, "batchId": batch["batchId"], "itemId": item_id, "sha256": raw_sha,
                       "resultKey": key, "file": result_file}
            if record["status"] not in allowed:
                summary["warnings"].append(f"{record['stubId']}: status {record['status']} does not take {stage} output")
                continue
            if same and any(h.get("rejected") for h in same):
                # A retry that returns the same rejected text is a new failure, not a replay.
                reason = next(h["rejected"] for h in same if h.get("rejected"))
                record["yamlHistory"].append({**history, "rejected": f"repeated: {reason}"})
                fail_record(record, stage, batch["batchId"], item_id, "repeated-output", reason, worktree, wave_dir, key)
                summary["failed"].append(f"{record['stubId']}: repeated-output: {reason}")
                continue
            if same:
                record["yamlHistory"].append({**history, "unchanged": True})
                summary["warnings"].append(f"{record['stubId']}: {stage} output identical to an applied version")
                if stage == "repair":
                    fail_record(record, stage, batch["batchId"], item_id, "unchanged", "repair returned the same YAML",
                                worktree, wave_dir, key)
                    summary["failed"].append(f"{record['stubId']}: unchanged: repair returned the same YAML")
                continue
            try:
                body = set_verified(prepare_body(entry["yaml"]), VERIFIED_SENTINEL)
                changes = check_stub_fields(body, record, stage)
            except (ResultError, WaveError) as error:
                reason = getattr(error, "reason", "format")
                record["yamlHistory"].append({**history, "rejected": f"{reason}: {error}"})
                fail_record(record, stage, batch["batchId"], item_id, reason, str(error), worktree, wave_dir, key)
                summary["failed"].append(f"{record['stubId']}: {reason}: {error}")
                continue
            for field, value in changes.items():
                record["warnings"].append(f"{stage}: {field} changed from {record[field]} to {value} on request")
                record[field] = value
                record["stub"][field] = value
            text = f"{SCHEMA_COMMENT}\n{'id: ' + record['id']}\n{body}"
            write_text_lf(worktree / record["path"], text)
            record["yamlHistory"].append(history)
            record["contentHash"] = content_hash(text)
            record["verifiedDate"] = None
            record["status"] = new_status
            record["attempts"][stage] = record["attempts"].get(stage, 0) + 1
            record["notes"].extend({"stage": stage, "batchId": batch["batchId"], "text": n} for n in entry["notes"])
            summary["written"].append(record["stubId"])
        item["applied"] = True
    save_state(wave_dir / "state.json", state)
    save_registry(wave_dir, registry)
    return summary


# ---------------------------------------------------------------- CLI output parsing

ISSUE_HEAD_RE = re.compile(r"^(?P<path>[^\s:][^:]*?\.ya?ml): (?P<code>[a-z][a-z0-9-]*): (?P<rest>.*)$")
POINTER_TAIL_RE = re.compile(r" \[(?P<pointer>(?:[A-Za-z0-9_.$-]|\[\d+\])*)\]$")
SUMMARY_LINES = (re.compile(r"^Content is valid: \d+ question\(s\), hash [0-9a-f]+\.$"),
                 re.compile(r"^All runnable snippets match their declared results\.$"),
                 re.compile(r"^Checked \d+ URL\(s\): \d+ ok, \d+ dead, \d+ unknown\.$"),
                 # Trailers the CLI writes to stderr today; accepted on stdout too so a move does not abort mech.
                 re.compile(r"^\d+ (?:content|snippet) issue\(s\) found\.$"),
                 re.compile(r"^\d+ dead link\(s\) found\.$"))


def parse_issue_lines(output: str) -> dict[str, list[dict]]:
    """`path: code: message [pointer]` lines (a message may span lines) -> issues by `content/dotnet/...` path."""
    issues: dict[str, list[dict]] = {}
    current: dict | None = None
    lines = normalise_text(output).split("\n")
    for number, line in enumerate(lines, 1):
        if current is not None:
            current["message"] += "\n" + line
        else:
            if not line.strip() or any(p.match(line) for p in SUMMARY_LINES):
                continue
            head = ISSUE_HEAD_RE.match(line)
            if not head:
                raise WaveError(f"unparsed CLI line {number}: {line}")
            current = {"path": norm_content_path(head.group("path")), "code": head.group("code"),
                       "message": head.group("rest"), "pointer": None}
        tail = POINTER_TAIL_RE.search(current["message"])
        following = lines[number] if number < len(lines) else ""
        # A program output line may itself end with " [x]"; the issue ends only where a new line can start.
        if tail and following.strip() and not ISSUE_HEAD_RE.match(following) and \
                not any(p.match(following) for p in SUMMARY_LINES):
            tail = None
        if tail:
            current["pointer"] = tail.group("pointer") or None
            current["message"] = current["message"][:tail.start()]
            issues.setdefault(current["path"], []).append(current)
            current = None
    if current is not None:
        raise WaveError(f"unterminated CLI issue (no [pointer]): {current['path']}: {current['message'][:200]}")
    return issues


LINK_LINE_RE = re.compile(r"^(?P<status>dead|unknown): (?P<url>\S+) \((?P<detail>.*)\) cited by (?P<cited>.+)$")


def parse_link_lines(output: str) -> dict[str, list[dict]]:
    """`dead|unknown: <url> (<detail>) cited by <p1>, <p2>` -> link problems by `content/dotnet/...` path."""
    found: dict[str, list[dict]] = {}
    for number, line in enumerate(normalise_text(output).split("\n"), 1):
        if not line.strip() or any(p.match(line) for p in SUMMARY_LINES):
            continue
        match = LINK_LINE_RE.match(line)
        if not match:
            raise WaveError(f"unparsed check-links line {number}: {line}")
        for cited in match.group("cited").split(", "):
            path = norm_content_path(cited.strip())
            found.setdefault(path, []).append({"status": match.group("status"), "url": match.group("url"),
                                               "detail": match.group("detail")})
    return found


# ---------------------------------------------------------------- mechanical checks

CYRILLIC_RE = re.compile(r"[Ѐ-ӿ]")
CODE_SPAN_RE = re.compile(r"`([^`\n]+)`")
FENCED_BLOCK_RE = re.compile(r"```.*?```", re.S)
NUMBER_RE = re.compile(r"(?<![\w.])\d+(?:[.,]\d+)?(?![\w])")
LATIN_WORD_RE = re.compile(r"(?<![\w.#])[A-Za-z][A-Za-z0-9+#]*(?:\.[A-Za-z][A-Za-z0-9]*)*")
GLOSSARY_ALLOW = {"middleware", "deadlock", "code", "review", "net", ".net", "c#", "api", "ui", "id", "url", "http",
                  "https", "json", "yaml", "sql", "orm", "linq", "gc", "jit", "clr", "dto", "ci", "cd", "ok"}


def _strip_code(text: str) -> str:
    return CODE_SPAN_RE.sub(" ", FENCED_BLOCK_RE.sub(" ", text))


def _spans(text: str) -> list[str]:
    return sorted(CODE_SPAN_RE.findall(FENCED_BLOCK_RE.sub(" ", text)))


def _blocks(text: str) -> list[str]:
    return sorted(block.strip() for block in FENCED_BLOCK_RE.findall(text))


RU_THOUSANDS_RE = re.compile(r"(?<=\d)[ \u00a0\u202f](?=\d{3}(?!\d))")
EN_THOUSANDS_RE = re.compile(r"(?<=\d),(?=\d{3}(?!\d))")
# "2nd edition" is the number 2, as "2-е издание" is in ru.
EN_ORDINAL_RE = re.compile(r"(?<![\w.])(\d+)(?:st|nd|rd|th)\b")


def _numbers(text: str, locale: str) -> list[str]:
    """Numbers outside code, normalised across locale conventions: ru groups thousands with a
    (narrow) space and uses a decimal comma, en groups with a comma and uses a decimal point."""
    prose = _strip_code(text)
    if locale == "ru":
        prose = RU_THOUSANDS_RE.sub("", prose)
        return sorted(n.replace(",", ".") for n in NUMBER_RE.findall(prose))
    prose = EN_ORDINAL_RE.sub(r"\1", EN_THOUSANDS_RE.sub("", prose))
    return sorted(NUMBER_RE.findall(prose))


def locale_pairs(doc: dict) -> list[tuple[str, str | None, str | None]]:
    """(field, ru, en) for title, prompt, hint, answer, explanation, option texts and option explanations."""
    ru = (doc.get("locales") or {}).get("ru") or {}
    en = (doc.get("locales") or {}).get("en") or {}
    pairs = [(f, ru.get(f), en.get(f)) for f in ("title", "prompt", "hint", "answer", "explanation")]
    for kind in ("options", "optionExplanations"):
        keys = sorted(set((ru.get(kind) or {}).keys()) | set((en.get(kind) or {}).keys()))
        for key in keys:
            pairs.append((f"{kind}.{key}", (ru.get(kind) or {}).get(key), (en.get(kind) or {}).get(key)))
    return pairs


def parity(doc: dict) -> list[dict]:
    """Mechanical RU/EN parity. Severity `failure` blocks `ok`; `warning` goes to the verifier and the owner."""
    findings = []
    en_all = json.dumps((doc.get("locales") or {}).get("en") or {}, ensure_ascii=False)
    allow = GLOSSARY_ALLOW | {w.lower() for w in LATIN_WORD_RE.findall(en_all)}

    def add(field, severity, message):
        findings.append({"field": field, "severity": severity, "message": message})

    for field, ru, en in locale_pairs(doc):
        if (ru is None) != (en is None):
            add(field, "failure", f"present only in {'ru' if en is None else 'en'}")
            continue
        if ru is None:
            continue
        if _blocks(ru) != _blocks(en):
            add(field, "failure", "fenced code blocks differ between ru and en")
        if _spans(ru) != _spans(en):
            add(field, "failure", f"inline code spans differ: ru {_spans(ru)} vs en {_spans(en)}")
        # An identical value (program output such as "1,2") reads the same in both locales.
        if ru.strip() != en.strip() and _numbers(ru, "ru") != _numbers(en, "en"):
            add(field, "failure",
                f"numbers outside code differ: ru {_numbers(ru, 'ru')} vs en {_numbers(en, 'en')}")
        if CYRILLIC_RE.search(en):
            add(f"locales.en.{field}", "failure", "Cyrillic in en")
        # A short answer such as program output or "O(n)" is language-neutral: identical in both
        # locales and legitimately free of Cyrillic.
        neutral_answer = field == "answer" and ru.strip() == en.strip() and len(ru.strip()) <= 60
        if field in ("title", "prompt", "answer", "explanation") and not CYRILLIC_RE.search(ru)                 and not neutral_answer:
            add(f"locales.ru.{field}", "failure", "no Cyrillic in ru")
        stray = sorted({w for w in LATIN_WORD_RE.findall(_strip_code(ru)) if w.lower() not in allow})
        if stray:
            add(f"locales.ru.{field}", "warning", f"Latin words outside code: {', '.join(stray[:10])}")
    en = (doc.get("locales") or {}).get("en") or {}
    for index, title in enumerate(en.get("linkTitles") or []):
        if CYRILLIC_RE.search(title):
            add(f"locales.en.linkTitles[{index}]", "failure", "Cyrillic in en")
    return findings


def dash_scan(doc) -> list[str]:
    """Every U+2014 or U+2013 in any string field, reported as `<field path>:<line>`."""
    found = []

    def walk(value, path):
        if isinstance(value, str):
            for number, line in enumerate(value.split("\n"), 1):
                for char, name in (("—", "U+2014"), ("–", "U+2013")):
                    if char in line:
                        found.append(f"dash {name} at {path}:{number}")
        elif isinstance(value, dict):
            for key, item in value.items():
                walk(item, f"{path}.{key}" if path else key)
        elif isinstance(value, list):
            for index, item in enumerate(value):
                walk(item, f"{path}[{index}]")

    walk(doc, "")
    return found


def stub_mismatches(doc: dict, record: dict) -> list[str]:
    problems = []
    for field in ("id", "topic", "slug", "level", "type"):
        if doc.get(field) != record[field]:
            problems.append(f"stub mismatch: {field} {doc.get(field)!r} expected {record[field]!r}")
    if doc.get("stack") != "dotnet":
        problems.append(f"stub mismatch: stack {doc.get('stack')!r} expected 'dotnet'")
    return problems


ACTUAL_RE = re.compile(r"[Aa]ctual(?: result)?(?: is)? (?P<kind>[a-z-]+)(?: '(?P<value>.*?)')?(?=\.$| is not|\.\s|$)", re.S)


def snippet_result(doc: dict, issues: list[dict]) -> dict:
    code = doc.get("code") or {}
    meta = doc.get("meta") or {}
    declared = doc.get("expect") or doc.get("fixedExpect")
    if not code:
        return {"executed": False, "skipped": "no code", "declared": declared, "actual": None, "diagnostics": []}
    if code.get("harness") == "none":
        return {"executed": False, "skipped": meta.get("noRunReason") or "harness: none", "declared": declared,
                "actual": None, "diagnostics": []}
    has_expect = declared or any((o or {}).get("expect") for o in doc.get("options") or [])
    if not has_expect:
        return {"executed": False, "skipped": "no declared result", "declared": None, "actual": None, "diagnostics": []}
    actual = None
    for issue in issues:
        match = ACTUAL_RE.search(issue["message"])
        if match:
            actual = {"kind": match.group("kind"), "value": match.group("value")}
            break
    if actual is None and not issues:
        actual = declared
    return {"executed": True, "skipped": None, "declared": declared, "actual": actual,
            "matches": not issues, "diagnostics": [f"{i['code']}: {i['message']}" for i in issues]}


def _run_checked(cli, args: list[str]) -> subprocess.CompletedProcess:
    result = cli(args)
    if result.returncode == 3:
        raise WaveError(f"{args[0]}: sandbox failure (exit 3); fix Docker and re-run.\n{result.stderr}")
    if result.returncode not in (0, 1):
        raise WaveError(f"{args[0]} exited {result.returncode}:\n{result.stderr}\n{result.stdout[:2000]}")
    return result


SUMMARY_FOR = {"validate": SUMMARY_LINES[0], "run-snippets": SUMMARY_LINES[1], "check-links": SUMMARY_LINES[2]}
CHECKED_RE = re.compile(r"^Checked (\d+) URL\(s\): (\d+) ok, (\d+) dead, (\d+) unknown\.$", re.M)


def run_reconciled(cli, args: list[str]) -> dict[str, list[dict]]:
    """Runs validate, run-snippets or check-links and reconciles the exit code with the parsed output.

    Exit 0 needs the command's success summary and no parsed problem; exit 1 needs at least one parsed problem
    (check-links: at least one dead link, matching its totals line). Anything else stops the run, so a crash that
    exits 1 with the error on stderr never reads as "no issues".
    """
    command = args[0]
    result = _run_checked(cli, args)
    out = normalise_text(result.stdout)
    parsed = parse_link_lines(out) if command == "check-links" else parse_issue_lines(out)
    problems = [i for issues in parsed.values() for i in issues]
    detail = f"\nstderr: {result.stderr.strip()[:2000]}\nstdout: {out.strip()[:2000]}"
    if command == "check-links":
        totals = CHECKED_RE.search(out)
        if not totals:
            raise WaveError(f"check-links exited {result.returncode} without its totals line{detail}")
        dead_urls = {p["url"] for p in problems if p["status"] == "dead"}
        unknown_urls = {p["url"] for p in problems if p["status"] == "unknown"}
        if (len(dead_urls), len(unknown_urls)) != (int(totals.group(3)), int(totals.group(4))):
            raise WaveError(f"check-links totals do not match the listed links{detail}")
        if (result.returncode == 1) != bool(dead_urls):
            raise WaveError(f"check-links exit {result.returncode} disagrees with {len(dead_urls)} dead link(s){detail}")
        return parsed
    if result.returncode == 0:
        if problems or not SUMMARY_FOR[command].search(out) and not any(
                SUMMARY_FOR[command].match(line) for line in out.split("\n")):
            raise WaveError(f"{command} exited 0 without its success line or with issues{detail}")
    elif not problems:
        raise WaveError(f"{command} exited 1 but printed no issue line{detail}")
    return parsed


def quarantine(record: dict, worktree: Path, wave_dir: Path, issues: list[dict]) -> None:
    retire_file(record, worktree, wave_dir / "quarantine" / f"{record['stubId']}.yaml.txt")
    record["status"] = "quarantined"
    record["evidence"]["mech"] = {"hash": record.get("contentHash"), "ok": False,
                                  "issues": [f"{i['code']}: {i['message']}" for i in issues], "result": None}


def content_records(state: dict, worktree: Path) -> dict[str, dict]:
    return {r["path"]: r for r in state.values() if (worktree / r["path"]).is_file()}


def mech(state: dict, worktree: Path, wave_dir: Path, cli, run_links: bool = True,
         quarantine_read_issues: bool = True) -> dict:
    """Dump, quarantine, per-question checks, validate, run-snippets, check-links; writes `evidence/mech-<hash>.json`."""
    root = str(worktree / "content")
    dump = json.loads(_run_checked(cli, ["dump", root]).stdout)
    by_path = content_records(state, worktree)
    summary = {"quarantined": [], "ok": [], "failed": [], "seedWarnings": []}
    read_issues: dict[str, list[dict]] = {}
    for issue in dump.get("readIssues", []):
        read_issues.setdefault(norm_content_path(issue["path"]), []).append(issue)
    foreign = [p for p in read_issues if p not in by_path]
    if foreign:
        raise WaveError(f"read issues in files outside the wave: {', '.join(foreign)}")
    if read_issues and not quarantine_read_issues:
        raise WaveError(f"read issues: {', '.join(sorted(read_issues))}")
    for path, issues in read_issues.items():
        quarantine(by_path[path], worktree, wave_dir, issues)
        summary["quarantined"].append(by_path[path]["stubId"])
    if read_issues:
        dump = json.loads(_run_checked(cli, ["dump", root]).stdout)
        if dump.get("readIssues"):
            raise WaveError("read issues remain after quarantine")
        by_path = content_records(state, worktree)
    docs = {norm_content_path(q["path"]): q["document"] for q in dump["questions"]}
    validate = run_reconciled(cli, ["validate", root])
    snippets = run_reconciled(cli, ["run-snippets", root])
    links = run_reconciled(cli, ["check-links", "--content", root]) if run_links else {}
    for source in (validate, snippets):
        foreign = [p for p in source if p not in by_path]
        if foreign:
            raise WaveError(f"CLI issues in files outside the wave: {', '.join(foreign)}")
    summary["seedWarnings"] = [f"{p}: {l['status']} {l['url']}" for p, ls in links.items() if p not in by_path for l in ls]
    stamp = utc_stamp()
    for path, record in by_path.items():
        doc = docs.get(path)
        if doc is None:
            raise WaveError(f"{path}: not in dump output")
        failures = stub_mismatches(doc, record) + dash_scan(doc)
        findings = parity(doc)
        failures += [f"parity {f['field']}: {f['message']}" for f in findings if f["severity"] == "failure"]
        failures += [f"{i['code']}: {i['message']} [{i['pointer'] or ''}]" for i in validate.get(path, [])]
        failures += [f"{i['code']}: {i['message']} [{i['pointer'] or ''}]" for i in snippets.get(path, [])]
        link_problems = links.get(path, [])
        failures += [f"link dead: {l['url']} ({l['detail']})" for l in link_problems if l["status"] == "dead"]
        warnings = [f"parity {f['field']}: {f['message']}" for f in findings if f["severity"] == "warning"]
        warnings += [f"link unknown: {l['url']} ({l['detail']})" for l in link_problems if l["status"] == "unknown"]
        text = (worktree / path).read_text(encoding="utf-8")
        digest = content_hash(text)
        result = snippet_result(doc, snippets.get(path, []))
        evidence = {"stubId": record["stubId"], "path": path, "hash": digest, "utc": stamp, "ok": not failures,
                    "failures": failures, "warnings": warnings, "result": result, "parity": findings,
                    "linksChecked": run_links, "checkLinks": link_problems}
        file = wave_dir / "evidence" / f"mech-{digest}.json"
        write_json_atomic(file, evidence)
        record["contentHash"] = digest
        record["evidence"]["mech"] = {"hash": digest, "ok": not failures, "issues": failures, "warnings": warnings,
                                      "result": result, "utc": stamp, "linksChecked": run_links,
                                      "file": _rel(file, worktree)}
        record["evidence"]["parity"] = findings
        summary["ok" if not failures else "failed"].append(record["stubId"])
    return summary


# ---------------------------------------------------------------- links-fetch

USER_AGENT = "Mozilla/5.0 (compatible; content-link-evidence/1.0)"
EXCERPT_CHARS = 4000
DEAD_GAP = dt.timedelta(minutes=30)
UNKNOWN_CODES = {401, 403, 429}


class _TextExtractor(html.parser.HTMLParser):
    SKIP = {"script", "style", "noscript", "nav", "header", "footer", "svg", "template"}

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.title = ""
        self.parts: list[str] = []
        self._skip = 0
        self._in_title = False

    def handle_starttag(self, tag, attrs):
        if tag in self.SKIP:
            self._skip += 1
        # SVG icons carry their own <title> (devblogs adds "Your Privacy Choices Opt-Out Icon"); only the
        # document title names the page.
        if tag == "title" and not self._skip:
            self._in_title = True

    def handle_endtag(self, tag):
        if tag in self.SKIP and self._skip:
            self._skip -= 1
        if tag == "title":
            self._in_title = False

    def handle_data(self, data):
        if self._in_title:
            self.title += data
        elif not self._skip and data.strip():
            self.parts.append(data.strip())


def page_text(html: str) -> tuple[str, str]:
    parser = _TextExtractor()
    try:
        parser.feed(html)
    except Exception:  # noqa: BLE001 - malformed HTML still yields what was parsed
        pass
    return " ".join(parser.title.split()), re.sub(r"\s+", " ", " ".join(parser.parts))


def public_https(url: str, resolve_dns: bool = True) -> str | None:
    """None when `url` is https on a public host; otherwise the reason it must not be fetched."""
    import ipaddress
    import socket
    import urllib.parse
    parts = urllib.parse.urlsplit(url)
    if parts.scheme != "https" or not parts.hostname:
        return "not an https URL"
    if "@" in parts.netloc:
        return "URL carries userinfo"
    host = parts.hostname.lower()
    if host == "localhost" or host.endswith((".localhost", ".local", ".internal")):
        return f"local host {host}"
    try:
        addresses = [ipaddress.ip_address(host)]
    except ValueError:
        if not resolve_dns:
            return None
        try:
            addresses = [ipaddress.ip_address(info[4][0].split("%")[0])
                         for info in socket.getaddrinfo(host, parts.port or 443, proto=socket.IPPROTO_TCP)]
        except OSError:
            return None  # DNS failure is classified by the fetch itself
    if any(not a.is_global for a in addresses):
        return f"host {host} resolves to a non-public address"
    return None


DNS_UNRESOLVED = "dns: host did not resolve"


def resolve_public(host: str, port: int) -> tuple[list[str] | None, str | None]:
    """(addresses to try in order, None) when every address of `host` is public; else (None, reason).

    The name is resolved once and the caller connects to the returned address, so a second lookup cannot rebind
    the host to a private address between the check and the connection.
    """
    import ipaddress
    import socket
    try:
        literal = ipaddress.ip_address(host)
        return ([str(literal)], None) if literal.is_global else (None, f"host {host} is not a public address")
    except ValueError:
        pass
    try:
        infos = socket.getaddrinfo(host, port, proto=socket.IPPROTO_TCP)
    except OSError:
        return None, DNS_UNRESOLVED
    addresses = [ipaddress.ip_address(info[4][0].split("%")[0]) for info in infos]
    if not addresses:
        return None, DNS_UNRESOLVED
    if any(not a.is_global for a in addresses):
        return None, f"host {host} resolves to a non-public address"
    return list(dict.fromkeys(str(a) for a in addresses)), None


def _pinned_get(url: str, addresses: list[str], timeout: float = 20) -> tuple[int, dict, bytes]:
    """GET over TLS to the first reachable of the checked `addresses`, with SNI, certificate check and a Host
    header for the URL's host (never userinfo)."""
    import http.client
    import socket
    import ssl
    import urllib.parse
    parts = urllib.parse.urlsplit(url)
    host, port = parts.hostname, parts.port or 443
    context = ssl.create_default_context()

    class PinnedConnection(http.client.HTTPSConnection):
        def connect(self):
            last_error: OSError | None = None
            for address in addresses:
                try:
                    raw = socket.create_connection((address, port), timeout)
                except OSError as error:
                    last_error = error
                    continue
                try:
                    self.sock = context.wrap_socket(raw, server_hostname=host)
                except BaseException:
                    raw.close()
                    raise
                return
            raise last_error or OSError("no address to connect to")

    if "@" in parts.netloc:
        raise ValueError("URL carries userinfo")
    host_header = f"[{host}]" if ":" in host else host
    if parts.port and parts.port != 443:
        host_header += f":{parts.port}"
    connection = PinnedConnection(host, port, timeout=timeout, context=context)
    try:
        path = (parts.path or "/") + (f"?{parts.query}" if parts.query else "")
        connection.request("GET", path, headers={"User-Agent": USER_AGENT, "Accept": "text/html,*/*",
                                                 "Host": host_header})
        response = connection.getresponse()
        return response.status, {k.lower(): v for k, v in response.getheaders()}, response.read(3_000_000)
    finally:
        connection.close()


def http_fetch(url: str, max_redirects: int = 5) -> dict:
    """One GET with redirects followed; every hop must be public https and is connected to the checked address.

    Never raises. A host that does not resolve is reported as `unknown` (never fetched, never allowed).
    """
    import urllib.parse
    current = url
    for _ in range(max_redirects + 1):
        reason = public_https(current, resolve_dns=False)
        if reason:
            return {"status": None, "finalUrl": current, "html": "", "error": f"blocked: {reason}"}
        parts = urllib.parse.urlsplit(current)
        addresses, reason = resolve_public(parts.hostname, parts.port or 443)
        if reason:
            return {"status": None, "finalUrl": current, "html": "", "error": f"blocked: {reason}"}
        try:
            status, headers, body = _pinned_get(current, addresses)
        except Exception as error:  # noqa: BLE001 - TLS, timeouts, resets: classified by fetch_outcome
            return {"status": None, "finalUrl": current, "html": "", "error": f"{type(error).__name__}: {error}"}
        if status in (301, 302, 303, 307, 308) and headers.get("location"):
            current = urllib.parse.urljoin(current, headers["location"])
            continue
        if not 200 <= status < 300:
            return {"status": status, "finalUrl": current, "html": "", "error": f"HTTP {status}"}
        match = re.search(r"charset=([\w-]+)", headers.get("content-type", ""))
        charset = match.group(1) if match else "utf-8"
        try:
            text = body.decode(charset, "replace")
        except LookupError:
            text = body.decode("utf-8", "replace")
        return {"status": status, "finalUrl": current, "html": text, "error": None}
    return {"status": None, "finalUrl": current, "html": "", "error": "too many redirects"}


def fetch_outcome(fetched: dict) -> str:
    status = fetched.get("status")
    if status is not None and 200 <= status < 300:
        return "ok"
    if status in (404, 410):
        return "failed"
    error = (fetched.get("error") or "").lower()
    if status is None and ("getaddrinfo" in error or "name or service" in error or "nodename" in error
                           or "no address" in error or "11001" in error or "refused" in error):
        return "failed"
    return "unknown"


def link_verdict(history: list[dict]) -> str:
    """`dead` only after two failed fetches 30+ minutes apart, or a failed fetch plus a later check-links dead."""
    if not history:
        return "unknown"
    if history[-1]["outcome"] == "ok":
        return "ok"
    failures = [h for h in history if h["outcome"] == "failed" and h.get("source", "fetch") == "fetch"]
    for first in failures:
        for later in history:
            if parse_utc(later["utc"]) <= parse_utc(first["utc"]):
                continue
            if later.get("source") == "check-links" and later["outcome"] == "failed":
                return "dead"
            if later in failures and parse_utc(later["utc"]) - parse_utc(first["utc"]) >= DEAD_GAP:
                return "dead"
    if failures and history[-1]["outcome"] == "failed":
        return "suspect"
    return "unknown"


def key_terms(doc: dict) -> list[str]:
    en = (doc.get("locales") or {}).get("en") or {}
    terms = []
    for field in ("title", "prompt", "answer", "explanation"):
        terms.extend(CODE_SPAN_RE.findall(en.get(field) or ""))
    terms.extend(w for w in re.findall(r"[A-Za-z][A-Za-z0-9.<>]+", en.get("title") or "")
                 if len(w) > 3 and w.lower() not in STOPWORDS)
    seen = []
    for term in terms:
        term = term.strip()
        if term and term not in seen and len(term) <= 60:
            seen.append(term)
    return seen[:30]


def excerpt(text: str, terms: list[str], size: int = EXCERPT_CHARS) -> str:
    if len(text) <= size:
        return text
    lower = text.lower()
    positions = [(m.start(), t) for t in terms for m in re.finditer(re.escape(t.lower()), lower)]
    if not positions:
        return text[:size]
    best = max(positions, key=lambda p: len({t for pos, t in positions if abs(pos - p[0]) <= size // 2}))[0]
    start = max(0, min(best - size // 2, len(text) - size))
    return text[start:start + size]


def redirect_warning(url: str, final: str) -> str | None:
    import urllib.parse
    a, b = urllib.parse.urlsplit(url), urllib.parse.urlsplit(final)
    if (a.netloc.lower(), a.path.rstrip("/").lower()) == (b.netloc.lower(), b.path.rstrip("/").lower()):
        return None
    segments = [s for s in b.path.split("/") if s]
    if b.netloc.lower() == "learn.microsoft.com" and len(segments) <= 2:
        return f"redirected to a Learn landing page {final}"
    return f"redirected to a different page {final}"


def link_file(wave_dir: Path, url: str) -> Path:
    return wave_dir / "evidence" / "links" / f"{hashlib.sha1(url.encode('utf-8')).hexdigest()}.json"


# Fetched page text is third-party content: the repository is public and the content is CC BY-SA, so it must never be
# committed. The text lives under the git-ignored `<wave>/.local/`; committed evidence and task copies keep only its
# sha256, so the audit trail still binds each verdict to the exact text the model saw.
LOCAL_DIR = ".local"


def local_dir(wave_dir: Path) -> Path:
    return wave_dir / LOCAL_DIR


def excerpt_cache_file(wave_dir: Path, url: str) -> Path:
    return local_dir(wave_dir) / "excerpts" / f"{hashlib.sha1(url.encode('utf-8')).hexdigest()}.json"


def redaction_marker(text: str) -> str:
    return "[third-party text removed; sha256 " + sha256_bytes(text.encode("utf-8")) + "]"


EXCERPT_LINE_RE = re.compile(r"(?m)^(  excerpt: )(.*)$")


def redact_task(task: dict) -> dict:
    """The committed copy of a task: every excerpt line of every item carries the sha256 marker instead of the text."""
    copy = json.loads(json.dumps(task))
    for item in copy.get("items", []):
        if "text" in item:
            item["text"] = EXCERPT_LINE_RE.sub(lambda m: m.group(1) + redaction_marker(m.group(2)), item["text"])
    return copy


RATE_LIMIT_BACKOFF_SECONDS = (5.0, 15.0, 45.0)
HOST_MIN_INTERVAL_SECONDS = 1.0


def polite_fetch(fetch, sleep=time.sleep, clock=time.monotonic,
                 backoff: tuple[float, ...] = RATE_LIMIT_BACKOFF_SECONDS,
                 min_interval: float = HOST_MIN_INTERVAL_SECONDS):
    """Wraps `fetch` so requests to one host are serialised and spaced, and an HTTP 429 is retried after a
    back-off. Documentation hosts (learn.microsoft.com) rate-limit a burst of a few hundred parallel requests,
    which would otherwise turn most links into `unknown`."""
    import threading
    from urllib.parse import urlsplit
    locks: dict[str, threading.Lock] = {}
    last: dict[str, float] = {}
    guard = threading.Lock()

    def wrapped(url: str) -> dict:
        host = (urlsplit(url).hostname or "").lower()
        with guard:
            lock = locks.setdefault(host, threading.Lock())
        with lock:
            result = None
            for delay in (0.0, *backoff):
                if delay:
                    sleep(delay)
                wait = last.get(host, float("-inf")) + min_interval - clock()
                if wait > 0:
                    sleep(wait)
                result = fetch(url)
                last[host] = clock()
                if result.get("status") != 429:
                    break
            return result

    return wrapped


def links_fetch(state: dict, docs: dict[str, dict], wave_dir: Path, worktree: Path, fetch=http_fetch,
                now=utc_now, dead_from_check_links: dict[str, str] | None = None, workers: int = 4) -> dict:
    """Fetches every distinct link of every wave question and stores status history, title and excerpt hashes;
    the excerpt text goes to the local cache (see LOCAL_DIR)."""
    from concurrent.futures import ThreadPoolExecutor
    cited: dict[str, list[dict]] = {}
    for record in state.values():
        doc = docs.get(record["path"])
        if doc is None:
            continue
        current = {link["url"] for link in doc.get("links") or []}
        # A repair or an owner edit can replace a link; evidence for a URL the question no longer cites
        # would keep flagging it in the review sheet.
        record["evidence"]["links"] = [l for l in record["evidence"].get("links", []) if l["url"] in current]
        for url in current:
            cited.setdefault(url, []).append(record)
    urls = sorted(cited)

    paced = polite_fetch(fetch) if fetch is http_fetch else fetch

    def guarded(url: str) -> dict:
        reason = public_https(url, resolve_dns=False)
        if reason:
            return {"status": None, "finalUrl": url, "html": "", "error": f"blocked: {reason}"}
        return paced(url)

    with ThreadPoolExecutor(max_workers=workers) as pool:
        fetched = dict(zip(urls, pool.map(guarded, urls)))
    stamp = utc_stamp(now())
    summary = {"ok": [], "dead": [], "unknown": [], "suspect": [], "warnings": []}
    for url in urls:
        path = link_file(wave_dir, url)
        data = read_json(path, None) or {"url": url, "history": [], "title": None, "finalUrl": None}
        # Evidence written before the text moved to the local cache: drop the inline text, never carry it forward.
        data.pop("excerpts", None)
        data.setdefault("excerptSha256", {})
        result = fetched[url]
        outcome = fetch_outcome(result)
        data["history"].append({"utc": stamp, "source": "fetch", "status": result["status"], "outcome": outcome,
                                "finalUrl": result["finalUrl"], "error": result["error"]})
        dead_utc = (dead_from_check_links or {}).get(url)
        if dead_utc and not any(h.get("source") == "check-links" and h["utc"] == dead_utc for h in data["history"]):
            data["history"].append({"utc": dead_utc, "source": "check-links", "status": None, "outcome": "failed",
                                    "finalUrl": None, "error": "check-links dead"})
            data["history"].sort(key=lambda h: h["utc"])
        warning = None
        if outcome == "ok":
            title, text = page_text(result["html"])
            data["title"] = title
            data["finalUrl"] = result["finalUrl"]
            data["fetchedUtc"] = stamp
            cache_path = excerpt_cache_file(wave_dir, url)
            cache = read_json(cache_path, {}) or {}
            for record in cited[url]:
                piece = excerpt(text, key_terms(docs[record["path"]]))
                cache[record["stubId"]] = piece
                data["excerptSha256"][record["stubId"]] = sha256_bytes(piece.encode("utf-8"))
            write_json_atomic(cache_path, cache)
            warning = redirect_warning(url, result["finalUrl"] or url)
        data["warning"] = warning
        data["verdict"] = link_verdict(data["history"])
        write_json_atomic(path, data)
        summary[data["verdict"]].append(url)
        if warning:
            summary["warnings"].append(f"{url}: {warning}")
        for record in cited[url]:
            links = [l for l in record["evidence"].get("links", []) if l["url"] != url]
            links.append({"url": url, "status": data["verdict"], "title": data.get("title"),
                          "fetchedUtc": data.get("fetchedUtc"), "warning": warning, "file": _rel(path, worktree)})
            record["evidence"]["links"] = sorted(links, key=lambda l: l["url"])
    return summary


# ---------------------------------------------------------------- fact-check (verify) tasks

def verify_instructions(wave_dir: Path, worktree: Path) -> str:
    return (
        f"Follow {wave_rel(wave_dir, worktree, 'prompts', 'verify.md')}. Fact-check every question in the item against "
        "the evidence given there (sandbox result, numbered source, fetched link excerpts). "
        "Answer with exactly one verdict per stubId."
    )


def verify_shared(wave_dir: Path, worktree: Path) -> list[str]:
    return [wave_rel(wave_dir, worktree, "prompts", "verify.md"), "docs/content-guide.md"]


SURVIVOR_STATUSES = {"verify-pass", "owner-review", "final", "owner-edited"}


def file_hash(worktree: Path, record: dict) -> str | None:
    path = worktree / record["path"]
    return content_hash(path.read_text(encoding="utf-8")) if path.is_file() else None


def numbered_source(source: str) -> str:
    return "\n".join(f"{number:>3} | {line}" for number, line in enumerate(source.rstrip("\n").split("\n"), 1))


def _mech_lines(record: dict) -> list[str]:
    mech_evidence = record["evidence"].get("mech") or {}
    result = mech_evidence.get("result") or {}
    lines = ["Mechanical evidence:"]
    if result.get("executed"):
        lines.append(f"- executed in the sandbox; declared {json.dumps(result.get('declared'), ensure_ascii=False)}; "
                     f"actual {json.dumps(result.get('actual'), ensure_ascii=False)}")
    else:
        lines.append(f"- not executed ({result.get('skipped') or 'no runnable code'})")
    for diagnostic in result.get("diagnostics", []):
        lines.append(f"- sandbox: {diagnostic}")
    for failure in mech_evidence.get("issues", []):
        lines.append(f"- failure: {failure}")
    for warning in mech_evidence.get("warnings", []):
        lines.append(f"- warning: {warning}")
    if mech_evidence and not mech_evidence.get("linksChecked", True):
        lines.append("- check-links was skipped in this run (link status below comes from links-fetch only)")
    if len(lines) == 2 and mech_evidence.get("ok"):
        lines.append("- no mechanical failures")
    return lines


def _link_lines(record: dict, doc: dict | None, wave_dir: Path) -> list[str]:
    lines = ["Links:"]
    for link in (doc or {}).get("links") or []:
        data = read_json(link_file(wave_dir, link["url"]), None)
        if data is None:
            lines.append(f"- {link['url']}: not fetched")
            continue
        lines.append(f"- {link['url']}: status {data.get('verdict')}, title {data.get('title')!r}, fetched "
                     f"{data.get('fetchedUtc')}" + (f", warning: {data['warning']}" if data.get("warning") else ""))
        digest = (data.get("excerptSha256") or {}).get(record["stubId"])
        if digest:
            text = (read_json(excerpt_cache_file(wave_dir, link["url"]), {}) or {}).get(record["stubId"])
            if text is None or sha256_bytes(text.encode("utf-8")) != digest:
                raise WaveError(f"{record['stubId']}: the local excerpt of {link['url']} is missing or does not match "
                                f"its evidence; run links-fetch on this machine")
            lines.append(f"  excerpt: {text}")
    return lines


def current_verdict(record: dict, round_: int | None = None) -> dict | None:
    for verdict in reversed(record.get("verdicts", [])):
        if verdict.get("stale") or verdict.get("invalid"):
            continue
        if round_ is None or verdict["round"] == round_:
            return verdict
    return None


def verify_block(record: dict, doc: dict | None, wave_dir: Path, round_: int) -> str:
    lines = [f"## {record['stubId']}", f"path: {record['path']}",
             "stub: " + json.dumps(stub_payload(record), ensure_ascii=False)]
    gen_notes = [n["text"] for n in record.get("notes", []) if n.get("stage") == "gen"]
    lines.append("Generator notes: " + ("; ".join(gen_notes) if gen_notes else "none"))
    lines.extend(_mech_lines(record))
    code = (doc or {}).get("code") or {}
    if record["type"] == "find-bug" and code.get("source"):
        lines.append("Numbered code.source (quote every line listed in bugLineSets):")
        lines.append(numbered_source(code["source"]))
    lines.extend(_link_lines(record, doc, wave_dir))
    if round_ == 2:
        first = current_verdict(record, 1)
        lines.append("Round 1 issues: " + json.dumps((first or {}).get("issues", []), ensure_ascii=False))
        repair_notes = [n["text"] for n in record.get("notes", []) if n.get("stage") == "repair"]
        lines.append("Repair notes (rebuttals): " + ("; ".join(repair_notes) if repair_notes else "none"))
    return "\n".join(lines)


def verify_candidates(state: dict, round_: int, only: list[str] | None, registry: dict) -> list[dict]:
    pending = pending_stubs(registry, "verify")
    if round_ == 1:
        statuses = {"generated", "unverified"} if only else {"generated"}
    else:
        statuses = {"repaired", "unverified"}
    if not only and round_ == 2:
        # A round-1 unverified stub never saw a repair: retry it in round 1 (--only) or resolve it.
        return [r for r in state.values() if r["stubId"] not in pending and (
            r["status"] == "repaired" or (r["status"] == "unverified" and r.get("unverifiedRound") == 2))]
    if only:
        unknown = [s for s in only if s not in state]
        if unknown:
            raise WaveError(f"unknown stubIds: {', '.join(unknown)}")
        wrong = [s for s in only if state[s]["status"] not in statuses]
        if wrong:
            raise WaveError(f"not verifiable in round {round_}: " + ", ".join(f"{s} ({state[s]['status']})" for s in wrong))
        busy = [s for s in only if s in pending]
        if busy:
            raise WaveError(f"already in an unapplied verify item: {', '.join(busy)}")
        return [state[s] for s in only]
    return [r for r in state.values() if r["status"] in statuses and r["stubId"] not in pending]


def build_verify_tasks(state: dict, targets: dict, ref: str, worktree: Path, wave_dir: Path, registry: dict,
                       docs: dict[str, dict], round_: int, only: list[str] | None,
                       max_estimate: float | None) -> tuple[list[dict], dict]:
    records = verify_candidates(state, round_, only, registry)
    if not records:
        raise WaveError(f"no questions to verify in round {round_}")
    stale = [r["stubId"] for r in records
             if not (r["evidence"].get("mech") or {}).get("hash") or r["evidence"]["mech"]["hash"] != file_hash(worktree, r)]
    if stale:
        raise WaveError(f"mechanical evidence missing or stale (run mech): {', '.join(sorted(stale))}")
    size = int(targets.get("verifyChunk", 2))
    chunks: list[list[dict]] = []
    by_topic: dict[str, list[dict]] = {}
    for record in sorted(records, key=lambda r: r["stubId"]):
        by_topic.setdefault(record["topic"], []).append(record)
    for topic_records in by_topic.values():
        chunks.extend(topic_records[i:i + size] for i in range(0, len(topic_records), size))

    def text_for(chunk):
        header = (f"Fact-check round {round_}: {len(chunk)} question(s). The question files and the topic outline are "
                  "attached. Return exactly one verdict per stubId.\n\n")
        return header + "\n\n".join(verify_block(r, docs.get(r["path"]), wave_dir, round_) for r in chunk) + "\n"

    def files_for(chunk):
        return [r["path"] for r in chunk]

    def no_neighbours(worktree_, ref_, directory):
        return []

    task = base_task("claude-sonnet-5-5", verify_instructions(wave_dir, worktree), verify_shared(wave_dir, worktree),
                     VERDICT_SCHEMA, ref)
    items, entries = build_question_items(chunks, targets, worktree, wave_dir, ref, registry, "verify", text_for,
                                          files_for, lister=no_neighbours, id_prefix=f"verify-r{round_}",
                                          markers=wave_markers(state))
    for item_id, entry in entries.items():
        entry["round"] = round_
        entry["hashes"] = {s: state[s]["evidence"]["mech"]["hash"] for s in entry["stubs"]}
    tasks = [{**task, "items": chunk_items} for chunk_items in split_by_estimate(task, items, worktree, max_estimate)]
    return tasks, entries


# ---------------------------------------------------------------- verdicts

def set_verified(text: str, date: str) -> str:
    """Sets `meta.verified` (block or flow style); the content hash masks this value, so evidence stays valid."""
    lines = normalise_text(text).split("\n")
    in_meta = False
    for index, line in enumerate(lines):
        if re.match(r"^meta:\s*\{", line):
            if re.search(r"\bverified:", line):
                lines[index] = re.sub(r"(\bverified:\s*)[^,}\s]+", r"\g<1>" + date, line, count=1)
                return "\n".join(lines)
            break
        if re.match(r"^meta:\s*(#.*)?$", line):
            in_meta = True
            continue
        if in_meta:
            if line and not line[0].isspace() and not line.startswith("#"):
                break
            match = re.match(r"^(\s+)verified:", line)
            if match and len(match.group(1)) == _meta_indent(lines, index):
                lines[index] = f"{match.group(1)}verified: {date}"
                return "\n".join(lines)
    raise WaveError("no meta.verified line to set")


def mark_verified(record: dict, worktree: Path, date: str) -> None:
    path = worktree / record["path"]
    write_text_lf(path, set_verified(path.read_text(encoding="utf-8"), date))


def read_verified(text: str) -> str | None:
    """The `meta.verified` value of a question file (block or flow style), or None."""
    lines = normalise_text(text).split("\n")
    in_meta = False
    for index, line in enumerate(lines):
        flow = re.match(r"^meta:\s*\{.*?\bverified:\s*([^,}\s]+)", line)
        if flow:
            return flow.group(1).strip("\"'")
        if re.match(r"^meta:\s*(#.*)?$", line):
            in_meta = True
            continue
        if in_meta:
            if line and not line[0].isspace() and not line.startswith("#"):
                break
            match = re.match(r"^(\s+)verified:\s*(\S+)", line)
            if match and len(match.group(1)) == _meta_indent(lines, index):
                return match.group(2).strip("\"'")
    return None


def complete_file_ops(state: dict, worktree: Path, wave_dir: Path) -> list[str]:
    """Finishes file edits journalled in the state before a crash could interrupt them (idempotent)."""
    done = []
    for record in state.values():
        op = record.get("pendingFileOp")
        if not op:
            continue
        if op["op"] == "verify":
            passes = [v for v in record["verdicts"] if v.get("verdict") == "pass" and v.get("verifiedDate") == op["date"]]
            current = file_hash(worktree, record)
            if not passes or current != passes[-1]["hash"]:
                # The file changed (or vanished) between the pass and this completion: the pass no longer applies.
                record["status"] = "unverified"
                record["warnings"].append(f"pass of {op['date']} dropped: file changed before its date was written")
            else:
                try:
                    mark_verified(record, worktree, op["date"])
                    record["verifiedDate"] = op["date"]
                except WaveError as error:
                    record["status"] = "unverified"
                    record["warnings"].append(f"pass of {op['date']} dropped: {error}")
        elif op["op"] == "reject":
            retire_file(record, worktree, wave_dir / "rejected" / record["topic"] / f"{record['slug']}.yaml.txt")
        record.pop("pendingFileOp")
        done.append(record["stubId"])
    return done


def mech_ok(record: dict, current: str | None) -> bool:
    mech_evidence = record["evidence"].get("mech") or {}
    return bool(current) and mech_evidence.get("hash") == current and bool(mech_evidence.get("ok"))


VERIFY_INPUT_STATUSES = {1: {"generated", "unverified"}, 2: {"repaired", "unverified"}}


def apply_verdicts(batch_path, round_: int, wave_dir: Path, worktree: Path, batch_id: str | None = None,
                   today: str | None = None) -> dict:
    state = load_state(wave_dir / "state.json")
    registry = load_registry(wave_dir)
    batch = load_batch(batch_path, batch_id)
    unknown = [i for i in batch["index"] if i not in registry or registry[i]["stage"] != "verify"
               or registry[i].get("round") != round_]
    if unknown:
        raise WaveError(f"items not registered as round {round_} verify items: {', '.join(unknown)}")
    today = today or utc_now().strftime("%Y-%m-%d")
    complete_file_ops(state, worktree, wave_dir)
    summary: dict[str, list[str]] = {s: [] for s in ("verify-pass", "needs-repair", "unverified", "owner-review",
                                                     "rejected", "stale", "skipped", "warnings")}
    summary["warnings"] += [f"registered item {i} of the same task is missing from index.json"
                            for i in missing_registered_items(batch, registry)]
    for item_id in batch["index"]:
        item = registry[item_id]
        result = read_item_result(batch, item_id)
        key = result["key"]
        try:
            entries, warnings = validate_result(result, item, "verify")
            summary["warnings"].extend(warnings)
            item_problem = None
        except ResultError as error:
            entries, item_problem = [], f"{error.reason}: {error}"
        by_stub = {e["stubId"]: e for e in entries}
        for stub_id in item["stubs"]:
            record = state[stub_id]
            if any(v.get("resultKey") in result["keys"] and v.get("itemId") == item_id for v in record["verdicts"]):
                summary["skipped"].append(stub_id)
                continue
            if record["status"] not in VERIFY_INPUT_STATUSES[round_]:
                summary["warnings"].append(f"{stub_id}: status {record['status']} ignores round {round_} verdict "
                                           f"of item {item_id}")
                continue
            entry = by_stub.get(stub_id)
            base = {"round": round_, "hash": item["hashes"][stub_id], "batchId": batch["batchId"], "itemId": item_id,
                    "resultKey": key, "source": "batch", "utc": utc_stamp()}
            if entry is None:
                record["verdicts"].append({**base, "verdict": None, "issues": [], "invalid":
                                           item_problem or "missing or duplicate verdict"})
                _unverified(record, round_, summary)
                continue
            verdict = {**base, "verdict": entry["verdict"], "issues": entry["issues"]}
            current = file_hash(worktree, record)
            if current != verdict["hash"]:
                record["verdicts"].append({**verdict, "stale": True})
                summary["stale"].append(stub_id)
                continue
            blocking = [i for i in entry["issues"] if i["severity"] in ("blocker", "major", "unsure")]
            if entry["verdict"] == "pass" and blocking:
                record["verdicts"].append({**verdict, "invalid": "pass with a blocker, major or unsure issue"})
                _unverified(record, round_, summary)
                continue
            record["attempts"]["verify"] = record["attempts"].get("verify", 0) + 1
            if entry["verdict"] == "pass" and mech_ok(record, current):
                verdict["verifiedDate"] = today
                record["verdicts"].append(verdict)
                record["pendingFileOp"] = {"op": "verify", "date": today}
                record["status"] = "verify-pass"
                summary["verify-pass"].append(stub_id)
                continue
            record["verdicts"].append(verdict)
            if round_ == 1:
                record["status"] = "needs-repair"
            elif entry["verdict"] == "pass":
                # A pass on content that fails the mechanical checks is not a verdict on what ships; the
                # controller decides (resolve) instead of the driver discarding the question.
                _unverified(record, round_, summary)
                continue
            elif entry["verdict"] == "fix" and not entry["issues"]:
                # A fix without a stated issue is not a usable verdict.
                _unverified(record, round_, summary)
                continue
            elif entry["verdict"] == "fix" and all(i["severity"] in ("unsure", "minor") for i in entry["issues"]):
                # The verifier saw the rebuttals in this round; only open doubts go to the owner, never a
                # blocker or major issue the verifier still holds, and never a reject.
                record["status"] = "owner-review"
            else:
                record["status"] = "rejected"
                record["pendingFileOp"] = {"op": "reject"}
            summary[record["status"]].append(stub_id)
        item["applied"] = True
    # Journal first, then edit files: a crash in between is finished by the next complete_file_ops.
    save_state(wave_dir / "state.json", state)
    save_registry(wave_dir, registry)
    complete_file_ops(state, worktree, wave_dir)
    save_state(wave_dir / "state.json", state)
    return summary


def _unverified(record: dict, round_: int, summary: dict) -> None:
    record["status"] = "unverified"
    record["unverifiedRound"] = round_
    summary["unverified"].append(record["stubId"])


# ---------------------------------------------------------------- repair tasks

STUB_KEYS = ("slug", "level", "type", "angle", "answerHinge")


def revised_stub(record: dict) -> dict | None:
    """The revised stub of a `reject` verdict: JSON or `key: value` pairs in the first issue's suggestedFix."""
    verdict = current_verdict(record)
    if not verdict or verdict.get("verdict") != "reject" or not verdict.get("issues"):
        return None
    fix = verdict["issues"][0].get("suggestedFix", "")
    try:
        parsed = json.loads(fix)
        if isinstance(parsed, dict):
            return {k: str(v) for k, v in parsed.items() if k in STUB_KEYS}
    except json.JSONDecodeError:
        pass
    keys = "|".join(STUB_KEYS)
    pairs = re.findall(rf"\b({keys})\s*[:=]\s*(.+?)(?=\s*(?:[;\n]|,\s*(?:{keys})\s*[:=])|$)", fix)
    return {k: v.strip().strip("\"'") for k, v in pairs} or None


def apply_revised_stub(record: dict, revised: dict) -> list[str]:
    changes = []
    for key in ("angle", "answerHinge"):
        if revised.get(key):
            record["stub"][key] = revised[key]
            changes.append(key)
    if revised.get("level") in LEVELS and revised["level"] != record["level"]:
        record["level"] = record["stub"]["level"] = revised["level"]
        changes.append("level")
    if revised.get("type") in TYPES and revised["type"] != record["type"]:
        record["type"] = record["stub"]["type"] = revised["type"]
        changes.append("type")
    if changes:
        record["warnings"].append(f"stub revised for repair after reject: {', '.join(changes)}")
    return changes


def current_yaml(record: dict, worktree: Path, wave_dir: Path) -> str:
    path = worktree / record["path"]
    if path.is_file():
        return path.read_text(encoding="utf-8")
    saved = wave_dir / "quarantine" / f"{record['stubId']}.yaml.txt"
    if saved.is_file():
        return "(unreadable, from quarantine)\n" + saved.read_text(encoding="utf-8")
    reasons = "; ".join(f"{f['reason']}: {f['message']}" for f in record.get("failures", []))
    return f"(none: generation failed: {reasons or 'unknown'})"


def repair_block(record: dict, doc: dict | None, worktree: Path, wave_dir: Path) -> str:
    verdict = current_verdict(record)
    lines = [f"## {record['stubId']}", "stub: " + json.dumps(stub_payload(record), ensure_ascii=False),
             f"Reason: {record['status']}" + (f", verdict {verdict['verdict']} (round {verdict['round']})" if verdict else ""),
             "Current YAML:", "<<<", current_yaml(record, worktree, wave_dir).rstrip("\n"), ">>>"]
    lines.extend(_mech_lines(record))
    lines.append("Verifier issues: " + json.dumps((verdict or {}).get("issues", []), ensure_ascii=False))
    lines.extend(_link_lines(record, doc, wave_dir))
    return "\n".join(lines)


def repair_candidates(state: dict, registry: dict) -> list[dict]:
    pending = pending_stubs(registry, "gen") | pending_stubs(registry, "repair")
    return [r for r in state.values() if r["stubId"] not in pending and (
        r["status"] in ("needs-repair", "quarantined") or (r["status"] == "gen-failed" and retry_left(r)))]


def build_repair_tasks(state: dict, targets: dict, ref: str, worktree: Path, wave_dir: Path, registry: dict,
                       docs: dict[str, dict], max_estimate: float | None,
                       lister=committed_files, reader=git_show) -> tuple[list[dict], dict, list[str]]:
    records = repair_candidates(state, registry)
    if not records:
        raise WaveError("no questions to repair")
    revised = []
    for record in records:
        stub_change = revised_stub(record)
        if stub_change and apply_revised_stub(record, stub_change):
            revised.append(record["stubId"])
    chunks = chunk_records(records, targets)

    def text_for(chunk):
        header = (f"Repair {len(chunk)} question(s). Keep stubId, slug and topic. Return the complete file for every "
                  "question, without an id line.\n\n")
        return header + "\n\n".join(repair_block(r, docs.get(r["path"]), worktree, wave_dir) for r in chunk) + "\n"

    task = base_task("claude-opus-5-5", repair_instructions(wave_dir, worktree), repair_shared(wave_dir, worktree),
                     GEN_SCHEMA, ref)
    items, entries = build_question_items(chunks, targets, worktree, wave_dir, ref, registry, "repair", text_for,
                                          lister=lister, id_prefix="repair", markers=wave_markers(state),
                                          reader=reader)
    tasks = [{**task, "items": chunk_items} for chunk_items in split_by_estimate(task, items, worktree, max_estimate)]
    return tasks, entries, revised


def plan_counts(state: dict, targets: dict, seed_levels: dict, seed_types: dict) -> list[str]:
    """Level counts per topic and the wave type mix over records still in play, against the targets."""
    out = []
    alive = [r for r in state.values() if r["status"] not in ("rejected", "shortfall")]
    for topic, spec in targets["topics"].items():
        for level in LEVELS:
            want = spec["levels"].get(level, 0)
            have = seed_levels.get(topic, {}).get(level, 0) + sum(
                1 for r in alive if r["topic"] == topic and r["level"] == level)
            if have != want:
                out.append(f"{topic} {level}: target {want}, planned {have}")
    for type_name, wanted in targets["typeMix"].items():
        have = sum(t.get(type_name, 0) for t in seed_types.values()) + sum(1 for r in alive if r["type"] == type_name)
        if abs(have - wanted) > targets["typeTolerance"]:
            out.append(f"type mix {type_name}: target {wanted} +-{targets['typeTolerance']}, planned {have}")
    return out


# ---------------------------------------------------------------- shortfall by budget

SHORTFALL_LEVEL_ORDER = ("middle", "junior", "senior")
PLAN_FILE = "plan.json"


def load_planned_new(wave_dir: Path) -> dict[str, int]:
    return dict(read_json(wave_dir / PLAN_FILE, {}).get("plannedNew", {}))


def save_planned_new(wave_dir: Path, state: dict) -> None:
    """Stubs per topic as allocated, rejected and cut ones included: the fixed denominator of acceptance."""
    counts: dict[str, int] = {}
    for record in state.values():
        counts[record["topic"]] = counts.get(record["topic"], 0) + 1
    write_json_atomic(wave_dir / PLAN_FILE, {"plannedNew": dict(sorted(counts.items()))})


def pick_shortfall(state: dict, targets: dict, existing: dict, count: int, planned_new: dict) -> list[str]:
    """Stubs to drop when the budget cannot pay for all of them; fewer than `count` when fewer qualify.

    Only `planned` stubs are cut. After a cut the level cell must keep `min(target, floor)` and the topic must keep
    ceil(minShare x planned new) stubs; lead and architect are never cut. Levels go middle, junior, senior; inside a
    level the topic with the largest slack first, then by stubId. `existing` is per topic and level, as
    `existing_counts` returns it. `planned_new` is the per-topic count persisted at allocate: recomputing it from a
    fresh inventory would shrink the protected minimum whenever questions appear after planning. A topic without a
    persisted count is not cut.
    """
    acceptance_spec = targets.get("acceptance", {})
    share = float(acceptance_spec.get("minShare", 0.85))
    floors = acceptance_spec.get("levelFloors", {})
    alive = [r for r in state.values() if r["status"] not in ("rejected", "shortfall")]
    cells: dict[tuple[str, str], int] = {}
    kept: dict[str, int] = {}
    for topic in targets["topics"]:
        for level in LEVELS:
            cells[topic, level] = existing.get(topic, {}).get(level, 0) + sum(
                1 for r in alive if r["topic"] == topic and r["level"] == level)
        kept[topic] = sum(1 for r in alive if r["topic"] == topic)
    candidates = sorted((r for r in alive if r["status"] == "planned" and r["level"] in SHORTFALL_LEVEL_ORDER),
                        key=lambda r: r["stubId"])
    chosen: list[str] = []
    while len(chosen) < count:
        best = None
        for record in candidates:
            topic, level = record["topic"], record["level"]
            if topic not in planned_new:
                continue
            spec = targets["topics"][topic]
            slack = kept[topic] - math.ceil(share * planned_new[topic])
            if slack < 1 or cells[topic, level] - 1 < level_floor(spec, floors, level):
                continue
            key = (SHORTFALL_LEVEL_ORDER.index(level), -slack, record["stubId"])
            if best is None or key < best[0]:
                best = (key, record)
        if best is None:
            break
        record = best[1]
        chosen.append(record["stubId"])
        candidates.remove(record)
        cells[record["topic"], record["level"]] -= 1
        kept[record["topic"]] -= 1
    return chosen


def plan_counts_report(state: dict, targets: dict, dump: dict, outline_task: dict | None) -> list[str]:
    """Read-only planning sheet: existing / target / remaining per topic and level, type quotas, item sizes."""
    levels, types = existing_counts(dump, {r["id"] for r in state.values()})
    remaining = remaining_counts(targets, levels)
    lines = ["topic level: existing / target / remaining"]
    for topic, spec in targets["topics"].items():
        for level in LEVELS:
            lines.append(f"{topic} {level}: {levels.get(topic, {}).get(level, 0)} / "
                         f"{spec['levels'].get(level, 0)} / {remaining[topic][level]}")
    lines.append("")
    lines.append("type quotas per topic (new stubs):")
    try:
        quotas = type_quotas(targets, remaining, types)
        for topic, row in quotas.items():
            lines.append(f"{topic}: " + ", ".join(f"{k} {row[k]}" for k in TYPES))
    except WaveError as error:
        lines.append(f"type quotas unavailable: {error}")
    if outline_task is not None:
        lines.append("")
        lines.append("outline items (serialized bytes):")
        for item in outline_task["items"]:
            lines.append(f"{item['id']}: {_item_bytes(item)}")
    return lines


# ---------------------------------------------------------------- resolve, finalize, final gate

def resolve(state: dict, stub_id: str, as_status: str, reason: str, worktree: Path, wave_dir: Path) -> None:
    if as_status not in ("owner-review", "shortfall"):
        raise WaveError("resolve takes --as owner-review or --as shortfall")
    record = state.get(stub_id)
    if record is None:
        raise WaveError(f"unknown stubId {stub_id}")
    if record["status"] in TERMINAL_STATUSES or record["status"] in ("final", "owner-edited"):
        raise WaveError(f"{stub_id} is already {record['status']}")
    if not reason.strip():
        raise WaveError("a reason is required")
    if as_status == "owner-review" and not (worktree / record["path"]).is_file():
        raise WaveError(f"{stub_id} has no question file; resolve it as shortfall")
    record["notes"].append({"stage": "resolve", "text": f"resolved as {as_status}: {reason}", "utc": utc_stamp()})
    if as_status == "shortfall":
        retire_file(record, worktree, wave_dir / "quarantine" / f"{stub_id}.yaml.txt")
    record["status"] = as_status


def unresolved(state: dict) -> list[dict]:
    done = TERMINAL_STATUSES | {"final", "owner-edited"}
    return [r for r in state.values() if r["status"] not in done]


def level_floor(spec: dict, floors: dict, level: str) -> int:
    """Questions a level cell must hold: its floor, or its target when that is lower."""
    target = spec["levels"].get(level, 0)
    return min(target, floors[level]) if level in floors else target


def acceptance(state: dict, targets: dict, seed_levels: dict, seed_types: dict,
               planned_new: dict | None = None) -> tuple[list[str], list[str]]:
    """(failures, shortfall lines) on the survivors plus the seeds.

    With basis "new" the share is survivors / `planned_new[topic]`, the count persisted at allocate.
    """
    failures, shortfall = [], []
    share = targets["acceptance"]["minShare"]
    basis = targets["acceptance"].get("basis")
    floors = targets["acceptance"].get("levelFloors", {})
    policies = targets["policies"]
    for topic, spec in targets["topics"].items():
        survivors = [r for r in state.values() if r["topic"] == topic and r["status"] in SURVIVOR_STATUSES]
        levels = {lv: seed_levels.get(topic, {}).get(lv, 0) + sum(1 for r in survivors if r["level"] == lv)
                  for lv in LEVELS}
        types = {t: seed_types.get(topic, {}).get(t, 0) + sum(1 for r in survivors if r["type"] == t) for t in TYPES}
        target = sum(spec["levels"].values())
        total = sum(levels.values())
        if basis == "new":
            planned = (planned_new or {}).get(topic)
            owed = sum(max(0, spec["levels"].get(lv, 0) - seed_levels.get(topic, {}).get(lv, 0)) for lv in LEVELS)
            if planned is None and owed > 0:
                failures.append(f"{topic}: {owed} new question(s) are owed but no planned count is persisted "
                                f"({PLAN_FILE}); run allocate")
            elif planned and len(survivors) < math.ceil(share * planned):
                failures.append(f"{topic}: {len(survivors)}/{planned} new questions is below {share:.0%}")
            for level in LEVELS:
                needed = level_floor(spec, floors, level)
                if levels[level] < needed:
                    failures.append(f"{topic} {level}: {levels[level]} is below the floor {needed}")
        elif total < math.ceil(share * target):
            failures.append(f"{topic}: {total}/{target} is below {share:.0%}")
        for level in ("junior", "middle"):
            if spec["levels"].get(level, 0) > 0 and levels[level] == 0:
                failures.append(f"{topic}: no {level} question")
        if spec.get("peopleTopic"):
            if types["output"] + types["find-bug"]:
                failures.append(f"{topic}: people topic has output or find-bug questions")
            if total and types["situation"] < policies["peopleMinSituationShare"] * total:
                failures.append(f"{topic}: situation {types['situation']}/{total} below "
                                f"{policies['peopleMinSituationShare']:.0%}")
        if spec.get("codeHeavy") and total:
            code = types["output"] + types["find-bug"]
            if code < policies["codeHeavyMinOutputFindBugShare"] * total:
                failures.append(f"{topic}: output + find-bug {code}/{total} below "
                                f"{policies['codeHeavyMinOutputFindBugShare']:.0%}")
        for level in LEVELS:
            missing = spec["levels"].get(level, 0) - levels[level]
            if missing > 0:
                shortfall.append(f"{topic} {level}: {missing}")
    return failures, shortfall


def final_gate(state: dict, worktree: Path, wave_dir: Path, cli, seed_paths: set[str],
               accept_owner_review: str | None = None) -> tuple[list[str], list[str]]:
    """Re-runs the mechanical checks on the whole bank and binds every published file to its pass and evidence.

    Returns (problems, owner-review paths). Marks `final` only when there is no problem at all.
    """
    complete_file_ops(state, worktree, wave_dir)
    open_records = unresolved(state)
    if open_records:
        return [f"{r['stubId']}: {r['status']} (run finalize)" for r in open_records], []
    stamp_before = utc_stamp()
    mech(state, worktree, wave_dir, cli, quarantine_read_issues=False)
    dump = json.loads(_run_checked(cli, ["dump", str(worktree / "content")]).stdout)
    by_id = {r["id"]: r for r in state.values()}
    problems: list[str] = []
    owner_review: list[str] = []
    to_final: list[tuple[dict, str]] = []
    for question in dump["questions"]:
        path = norm_content_path(question["path"])
        record = by_id.get(question["document"].get("id"))
        if record is None:
            if path not in seed_paths:
                problems.append(f"{path}: not a seed and not a wave record")
            continue
        current = file_hash(worktree, record)
        verified = read_verified((worktree / path).read_text(encoding="utf-8"))
        parsed = str(((question["document"].get("meta") or {}).get("verified")) or "")
        if parsed != verified:
            # The C# reader is the authority; a disagreement means the text reader misread the file.
            problems.append(f"{path}: meta.verified reads {verified} as text but {parsed or 'nothing'} in dump")
            verified = parsed
        mech_evidence = record["evidence"].get("mech") or {}
        if not (mech_evidence.get("ok") and mech_evidence.get("hash") == current
                and mech_evidence.get("utc", "") >= stamp_before and mech_evidence.get("linksChecked", True)):
            problems.append(f"{path}: no ok mechanical evidence from this run for hash {current}")
        problems.extend(f"{path}: {p}" for p in link_evidence_problems(record, question["document"], wave_dir))
        if record["status"] == "owner-review":
            if verified != VERIFIED_SENTINEL:
                problems.append(f"{path}: owner-review file must carry meta.verified {VERIFIED_SENTINEL}, has {verified}")
            owner_review.append(path)
            continue
        if record["status"] == "owner-edited":
            if current != record.get("ownerEditedHash") or verified != record.get("verifiedDate"):
                problems.append(f"{path}: changed after owner-check/owner-approve (run owner-check)")
            continue
        if record["status"] not in ("verify-pass", "final"):
            problems.append(f"{path}: status {record['status']} is not verify-pass")
            continue
        passes = [v for v in record["verdicts"] if v.get("verdict") == "pass" and not v.get("stale")
                  and not v.get("invalid")]
        if not passes or passes[-1]["hash"] != current:
            problems.append(f"{path}: content changed after its pass verdict (hash {current})")
            continue
        pass_date = passes[-1].get("verifiedDate")
        if not pass_date or verified != pass_date or record.get("verifiedDate") != pass_date:
            problems.append(f"{path}: meta.verified {verified} is not the date of its pass ({pass_date})")
            continue
        to_final.append((record, current))
    published = {r["path"] for r in state.values() if (worktree / r["path"]).is_file()}
    for record in state.values():
        if record["status"] in ("verify-pass", "final", "owner-review", "owner-edited") and \
                record["path"] not in published:
            problems.append(f"{record['path']}: {record['status']} but the file is missing")
    if owner_review and not (accept_owner_review or "").strip():
        problems.append(f"{len(owner_review)} owner-review file(s) are not fact-check verified; resolve them "
                        "(owner-approve, owner-check, resolve) or pass --accept-owner-review \"<reason>\"")
    if not problems:
        for record, current in to_final:
            record["status"] = "final"
            record["finalHash"] = current
        if owner_review:
            stamp = utc_stamp()
            for record in state.values():
                if record["status"] == "owner-review":
                    record["ownerReviewAccepted"] = {"reason": accept_owner_review.strip(), "utc": stamp,
                                                     "hash": file_hash(worktree, record)}
    return problems, owner_review


def link_evidence_problems(record: dict, doc: dict, wave_dir: Path) -> list[str]:
    """Every link needs links-fetch evidence from two passes 30+ minutes apart and must not be dead or suspect."""
    problems = []
    for link in doc.get("links") or []:
        data = read_json(link_file(wave_dir, link["url"]), None)
        if data is None:
            problems.append(f"link {link['url']} has no links-fetch evidence")
            continue
        verdict = link_verdict(data["history"])
        if verdict in ("dead", "suspect"):
            problems.append(f"link {link['url']} is {verdict}")
        fetches = sorted(parse_utc(h["utc"]) for h in data["history"] if h.get("source", "fetch") == "fetch")
        if len(fetches) < 2 or fetches[-1] - fetches[0] < DEAD_GAP:
            problems.append(f"link {link['url']} lacks the second links-fetch pass")
    return problems


def owner_approve(state: dict, stub_id: str, reason: str, worktree: Path, today: str) -> None:
    """The owner read an owner-review question and accepts it unchanged: its date becomes the owner's date."""
    record = state.get(stub_id)
    if record is None or record["status"] != "owner-review":
        raise WaveError(f"{stub_id} is not owner-review")
    if not reason.strip():
        raise WaveError("a reason is required")
    current = file_hash(worktree, record)
    if not mech_ok(record, current):
        raise WaveError(f"{stub_id}: no ok mechanical evidence for the current file (run mech)")
    mark_verified(record, worktree, today)
    record["verifiedDate"] = today
    record["status"] = "owner-edited"
    record["ownerEditedHash"] = file_hash(worktree, record)
    record["notes"].append({"stage": "owner-approve", "text": reason, "utc": utc_stamp()})


def owner_check(state: dict, worktree: Path, wave_dir: Path, cli, today: str) -> tuple[list[str], list[str]]:
    """Files whose hash differs from their `final` hash: fresh mechanical evidence, `owner-edited`, new date."""
    changed = [r for r in state.values() if (r.get("finalHash") and r["status"] == "final"
                                             and file_hash(worktree, r) != r["finalHash"])
               or (r["status"] == "owner-edited" and file_hash(worktree, r) != r.get("ownerEditedHash"))]
    if not changed:
        return [], []
    mech(state, worktree, wave_dir, cli, quarantine_read_issues=False)
    failures, edited = [], []
    for record in changed:
        mech_evidence = record["evidence"].get("mech") or {}
        if not mech_evidence.get("ok"):
            failures.append(f"{record['path']}: " + "; ".join(mech_evidence.get("issues", [])))
            continue
        mark_verified(record, worktree, today)
        record["verifiedDate"] = today
        record["status"] = "owner-edited"
        record["ownerEditedHash"] = file_hash(worktree, record)
        edited.append(record["path"])
    return failures, edited


# ---------------------------------------------------------------- owner review sheets

REPORT_COLUMNS = ["path", "level", "type", "risk", "verified", "executed", "actual_result", "links", "verdict_rounds",
                  "warnings", "owner_read"]
RISK_PRIORITY = ["lead/architect", "situation", "owner-review", "repaired", "not executed", "link unknown",
                 "parity warning"]
CODE_BEARING_TYPES = ("output", "find-bug", "single", "multi")


def risk_flags(record: dict) -> list[str]:
    flags = []
    mech_evidence = record["evidence"].get("mech") or {}
    result = mech_evidence.get("result") or {}
    if record["level"] in ("lead", "architect"):
        flags.append("lead/architect")
    if record["type"] == "situation":
        flags.append("situation")
    if record["status"] == "owner-review":
        flags.append("owner-review")
    if any(h["stage"] == "repair" and not h.get("rejected") for h in record["yamlHistory"]):
        flags.append("repaired")
    if record["type"] in CODE_BEARING_TYPES and result and not result.get("executed") \
            and result.get("skipped") not in (None, "no code"):
        flags.append("not executed")
    if any(l.get("status") in ("unknown", "suspect") for l in record["evidence"].get("links", [])):
        flags.append("link unknown")
    if any(f["severity"] == "warning" for f in record["evidence"].get("parity", [])):
        flags.append("parity warning")
    return flags


def accepted_reason(record: dict, current_hash: str | None) -> str | None:
    """The final-gate acceptance of an owner-review file, only while the file is the content that was accepted."""
    accepted = record.get("ownerReviewAccepted")
    if accepted and accepted.get("hash") and accepted["hash"] == current_hash:
        return accepted["reason"]
    return None


def _acceptance_text(record: dict, current_hash: str | None) -> str:
    reason = accepted_reason(record, current_hash)
    if reason:
        return f" (accepted: {reason})"
    return " (acceptance stale: file changed after the gate)" if record.get("ownerReviewAccepted") else ""


def report_row(record: dict, current_hash: str | None = None) -> dict:
    mech_evidence = record["evidence"].get("mech") or {}
    result = mech_evidence.get("result") or {}
    actual = result.get("actual")
    rounds = ", ".join(f"r{v['round']} {v.get('verdict') or 'invalid'}" + (" (stale)" if v.get("stale") else "")
                       for v in record["verdicts"])
    links = "; ".join(f"{l.get('status')}: {l.get('title') or l['url']}" for l in record["evidence"].get("links", []))
    return {"path": record["path"], "level": record["level"], "type": record["type"],
            "risk": ", ".join(risk_flags(record)),
            "verified": ("UNVERIFIED" + _acceptance_text(record, current_hash))
            if record["status"] == "owner-review" else (record.get("verifiedDate") or ""),
            "executed": "yes" if result.get("executed") else "no",
            "actual_result": "" if not actual else f"{actual.get('kind')}: {actual.get('value')}",
            "links": links, "verdict_rounds": rounds,
            "warnings": "; ".join(_mech_warnings_for_report(mech_evidence, record) + record.get("warnings", [])),
            "owner_read": ""}


def _mech_warnings_for_report(mech_evidence: dict, record: dict) -> list[str]:
    """Mech warnings minus `link unknown` for URLs that links-fetch later resolved as ok.

    The content CLI's check-links runs once per mech pass and learn.microsoft.com answers it with 429;
    links-fetch paces per host and is the link evidence the final gate uses, so its `ok` supersedes the 429."""
    ok_urls = {l["url"] for l in record["evidence"].get("links", []) if l.get("status") == "ok"}
    return [w for w in mech_evidence.get("warnings", [])
            if not (w.startswith("link unknown: ") and w[len("link unknown: "):].split(" ", 1)[0] in ok_urls)]


def build_report(state: dict, targets: dict, group: str, seed_levels: dict,
                 seed_types: dict | None = None, worktree: Path | None = None) -> tuple[str, list[dict]]:
    import random

    def current(record):
        return file_hash(worktree, record) if worktree is not None else record.get("contentHash")

    seed_types = {k: sum(v.get(k, 0) for v in (seed_types or {}).values()) for k in TYPES}
    topics = [t for t, spec in targets["topics"].items() if spec.get("group") == group]
    if not topics:
        raise WaveError(f"unknown group {group}")
    records = [r for r in state.values() if r["topic"] in topics]
    shipped = [r for r in records if r["status"] in SURVIVOR_STATUSES]
    risky = [r for r in shipped if risk_flags(r)]
    others = sorted((r for r in shipped if not risk_flags(r)), key=lambda r: r["path"])
    sample = random.Random(group).sample(others, math.ceil(len(others) * 0.10)) if others else []
    def risk_key(record):
        flags = risk_flags(record)
        return [0 if flag in flags else 1 for flag in RISK_PRIORITY], record["path"]

    ordered = sorted(risky, key=risk_key) + sorted(sample, key=lambda r: r["path"])
    rows = [report_row(r, current(r)) for r in ordered]
    lines = [f"# Wave 1 review sheet: {group}", "",
             f"Questions shipped: {len(shipped)}; rows below: {len(risky)} flagged by risk plus a seeded 10% sample "
             f"({len(sample)}) of the other {len(others)}.", "",
             "| " + " | ".join(REPORT_COLUMNS) + " |", "|" + "---|" * len(REPORT_COLUMNS)]
    for row in rows:
        lines.append("| " + " | ".join(str(row[c]).replace("|", "\\|").replace("\n", " ") for c in REPORT_COLUMNS) + " |")
    lines += ["", "## Totals by topic and level (shipped + seeds / target)", "",
              "| topic | " + " | ".join(LEVELS) + " |", "|---|" + "---|" * len(LEVELS)]
    for topic in topics:
        cells = []
        for level in LEVELS:
            have = seed_levels.get(topic, {}).get(level, 0) + sum(
                1 for r in shipped if r["topic"] == topic and r["level"] == level)
            cells.append(f"{have}/{targets['topics'][topic]['levels'].get(level, 0)}")
        lines.append(f"| {topic} | " + " | ".join(cells) + " |")
    wave_shipped = [r for r in state.values() if r["status"] in SURVIVOR_STATUSES]
    lines += ["", "## Types (this group; whole wave + seeds vs the wave target)", "",
              "| type | group | wave | target |", "|---|---|---|---|"]
    for type_name in TYPES:
        lines.append(f"| {type_name} | {sum(1 for r in shipped if r['type'] == type_name)} | "
                     f"{seed_types.get(type_name, 0) + sum(1 for r in wave_shipped if r['type'] == type_name)} | "
                     f"{targets['typeMix'].get(type_name, 0)} +-{targets.get('typeTolerance', 0)} |")
    unverified = [r for r in shipped if r["status"] == "owner-review"]
    lines += ["", f"## Not fact-check verified (owner-review, meta.verified {VERIFIED_SENTINEL})", ""]
    lines += [f"- {r['path']}: accepted at the final gate: {accepted_reason(r, current(r))}"
              if accepted_reason(r, current(r)) else
              f"- {r['path']}: approve with `owner-approve` or edit and run `owner-check` before merge"
              + (" (earlier acceptance is stale: file changed)" if r.get("ownerReviewAccepted") else "")
              for r in unverified] or ["- none"]
    lines += ["", "## Shortfall", ""]
    lines += [f"- {r['stubId']} ({r['topic']}, {r['level']}, {r['type']})" for r in records
              if r["status"] == "shortfall"] or ["- none"]
    lines += ["", "## Rejected", ""]
    lines += [f"- {r['stubId']} {r['path']}" for r in records if r["status"] == "rejected"] or ["- none"]
    return "\n".join(lines) + "\n", rows


def write_report(wave_dir: Path, group: str, markdown: str, rows: list[dict]) -> tuple[Path, Path]:
    import csv
    import io
    md_path = wave_dir / "review" / f"{group}.md"
    csv_path = wave_dir / "review" / f"{group}.csv"
    write_text_lf(md_path, markdown)
    buffer = io.StringIO()
    writer = csv.DictWriter(buffer, fieldnames=REPORT_COLUMNS, lineterminator="\n")
    writer.writeheader()
    writer.writerows(rows)
    write_text_lf(csv_path, buffer.getvalue())
    return md_path, csv_path


# ---------------------------------------------------------------- CLI plumbing

_cli_built = False


def run_cli(args: list[str], worktree: Path = REPO_ROOT) -> subprocess.CompletedProcess:
    """Runs the content CLI from its build output so `dotnet run` build chatter never mixes with stdout."""
    global _cli_built
    if not _cli_built:
        build = subprocess.run(["dotnet", "build", CLI_PROJECT, "-nologo", "-v", "q", "-c", "Debug"],
                               cwd=worktree, capture_output=True, text=True)
        if build.returncode != 0:
            raise WaveError(f"content CLI build failed:\n{build.stdout}\n{build.stderr}")
        _cli_built = True
    dll = worktree / CLI_PROJECT / "bin" / "Debug" / "net10.0" / "Trainer.ContentCli.dll"
    # validate and run-snippets write through the console code page when redirected; never crash on decoding.
    return subprocess.run(["dotnet", str(dll), *args], cwd=worktree, capture_output=True, text=True,
                          encoding="utf-8", errors="replace")


def load_dump(worktree: Path, dump_file: str | None) -> dict:
    if dump_file:
        dump = json.loads(Path(dump_file).read_text(encoding="utf-8"))
    else:
        result = run_cli(["dump", str(worktree / "content")], worktree)
        if result.returncode != 0:
            raise WaveError(f"dump exited {result.returncode}: {result.stderr.strip()}")
        dump = json.loads(result.stdout)
    if dump.get("readIssues"):
        issues = "\n".join(f"{i['path']}: {i['code']}: {i['message']}" for i in dump["readIssues"])
        raise WaveError(f"content has read issues:\n{issues}")
    return dump


def load_targets(wave_dir: Path) -> dict:
    return read_json(wave_dir / "targets.json")


def load_ledger(wave_dir: Path) -> dict:
    return read_json(wave_dir / "ledger.json")


def write_task(task: dict, name: str, args, wave_dir: Path) -> Path:
    worktree = Path(args.worktree)
    problems = check_task_file(task, worktree)
    if not problems:
        problems = preflight(task["ref"], Path(args.server_repo), task_files(task, worktree), worktree)
    if problems:
        raise WaveError("task file refused:\n" + "\n".join(problems))
    # The submitted file holds the excerpts and stays local; the committed copy under tasks/ is redacted.
    path = local_dir(wave_dir) / "tasks" / f"{name}.json"
    audit = wave_dir / "tasks" / f"{name}.json"
    if (path.exists() or audit.exists()) and not args.overwrite:
        raise WaveError(f"{audit} exists; pass --overwrite to replace an unsubmitted task file")
    if path.exists() or audit.exists():
        refuse_submitted_overwrite(name, wave_dir)
    for target, body in ((path, task), (audit, redact_task(task))):
        target.parent.mkdir(parents=True, exist_ok=True)
        with open(target, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps(body, indent=2, ensure_ascii=False) + "\n")

    def rel(p: Path) -> str:
        return p.relative_to(worktree).as_posix() if p.is_relative_to(worktree) else str(p)

    ledger = load_ledger(wave_dir)
    ledger.setdefault("taskFiles", []).append({
        "name": name, "file": rel(path), "sha256": task_sha256(path), "auditFile": rel(audit),
        "auditSha256": task_sha256(audit), "ref": task["ref"], "items": len(task["items"]), "createdUtc": utc_stamp()})
    write_json_atomic(wave_dir / "ledger.json", ledger)
    shared = sum(_chars(worktree, f) for p in task["sharedFiles"] for f in _match(p, worktree))
    print(f"wrote {path} ({len(task['items'])} items, shared context {shared:,} characters, "
          f"sha256 {task_sha256(path)}); redacted copy {audit}")
    return path


def cmd_budget(args, wave_dir: Path) -> int:
    targets = load_targets(wave_dir)
    ledger = load_ledger(wave_dir)
    if args.record is not None:
        reading = {"utc": utc_stamp(), "usd": args.record,
                   "baselineUsd": args.record if args.baseline else _last_baseline(ledger),
                   "outstandingAtReading": [s["batchId"] for s in _outstanding(ledger)]}
        ledger.setdefault("readings", []).append(reading)
        write_json_atomic(wave_dir / "ledger.json", ledger)
        print(f"recorded reading {args.record:.2f} (baseline {reading['baselineUsd']:.2f}, "
              f"outstanding at reading: {reading['outstandingAtReading'] or 'none'})")
    budget = targets["budgetUsd"]
    floor = targets["budgetFloorUsd"]
    forecast = float(ledger.get("forecastRemainingUsd") or 0)
    estimate = args.estimate or 0.0
    ok, message = ledger_check(ledger, estimate, forecast, budget, floor, targets, args.stage)
    gate = submit_gate(targets, ledger, estimate, utc_now()) if args.estimate is not None else []
    print(message)
    for problem in gate:
        print(f"refused: {problem}")
    if args.estimate is not None:
        verdict = ok and not gate
        print("submit allowed" if verdict else "submit refused")
        return 0 if verdict else 1
    return 0 if ok or actual_spent(ledger) is not None else 1


def _last_baseline(ledger: dict) -> float:
    readings = ledger.get("readings", [])
    return float(readings[-1].get("baselineUsd", 0)) if readings else 0.0


def cmd_ledger_submit(args, wave_dir: Path) -> int:
    ledger = load_ledger(wave_dir)
    task_path = Path(args.task)
    sha = task_sha256(task_path)
    known = {t["sha256"] for t in ledger.get("taskFiles", [])}
    if sha not in known:
        raise WaveError(f"{task_path} (sha256 {sha}) is not a task file recorded by a *-tasks step")
    ledger.setdefault("submits", []).append({
        "name": args.name, "taskFile": task_path.as_posix(), "taskSha256": sha, "planId": args.plan_id,
        "batchId": args.batch_id, "estimateUsd": args.estimate, "ownerConfirmation": args.confirmation,
        "stage": args.stage, "submittedUtc": utc_stamp(), "dayUtc": utc_now().strftime("%Y-%m-%d"),
        "status": "submitted",
        "collectedUtc": None, "actualUsd": None})
    write_json_atomic(wave_dir / "ledger.json", ledger)
    print(f"recorded submit {args.name} batch {args.batch_id} estimate {args.estimate:.2f}")
    return 0


def cmd_shortfall(args, wave_dir: Path) -> int:
    worktree = Path(args.worktree)
    state = load_state(wave_dir / "state.json")
    dump = load_dump(worktree, args.dump_file)
    existing, _ = existing_counts(dump, {r["id"] for r in state.values()})
    planned_new = load_planned_new(wave_dir)
    if not planned_new:
        raise WaveError(f"{wave_dir / PLAN_FILE} is missing: run allocate first")
    picked = pick_shortfall(state, load_targets(wave_dir), existing, args.count, planned_new)
    verb = "cut" if args.apply else "would cut"
    for stub_id in picked:
        print(f"{verb}: {stub_id} ({state[stub_id]['topic']} {state[stub_id]['level']})")
    if len(picked) < args.count:
        print(f"only {len(picked)} of {args.count} stubs can be cut without breaking the floors or the topic share")
    if not args.apply:
        print("dry run: pass --apply to mark them")
    elif picked:
        for stub_id in picked:
            record = state[stub_id]
            record["status"] = "shortfall"
            record.setdefault("failures", []).append({
                "stage": "shortfall", "batchId": None, "itemId": None, "resultKey": None, "reason": "budget",
                "message": "cut to fit the budget", "utc": utc_stamp()})
        save_state(wave_dir / "state.json", state)
    return 0


def cmd_plan_counts(args, wave_dir: Path) -> int:
    worktree = Path(args.worktree)
    targets = load_targets(wave_dir)
    state = load_state(wave_dir / "state.json")
    dump = load_dump(worktree, args.dump_file)
    outline_task = None
    if (wave_dir / "prompts" / "outline.md").is_file():
        try:
            outline_task = build_outline_task(dump, targets, "HEAD", wave_dir, worktree)
        except WaveError as error:
            print(f"outline items unavailable: {error}")
    for line in plan_counts_report(state, targets, dump, outline_task):
        print(line)
    return 0


def cmd_ledger_collect(args, wave_dir: Path) -> int:
    ledger = load_ledger(wave_dir)
    for submit in ledger.get("submits", []):
        if submit.get("batchId") == args.batch_id:
            submit["status"] = "collected"
            submit["collectedUtc"] = utc_stamp()
            submit["actualUsd"] = args.actual
            write_json_atomic(wave_dir / "ledger.json", ledger)
            print(f"recorded collect of {args.batch_id}")
            return 0
    raise WaveError(f"unknown batch {args.batch_id}")


def cmd_preflight(args, wave_dir: Path) -> int:
    worktree = Path(args.worktree)
    task = read_json(Path(args.task))
    problems = check_task_file(task, worktree)
    problems += preflight(task["ref"], Path(args.server_repo), task_files(task, worktree), worktree)
    for problem in problems:
        print(problem)
    print("preflight ok" if not problems else f"{len(problems)} problem(s)")
    return 0 if not problems else 1


def resolve_ref(worktree: Path, ref: str) -> str:
    result = _git(worktree, "rev-parse", "--verify", f"{ref}^{{commit}}")
    if result.returncode != 0:
        raise WaveError(f"cannot resolve {ref}: {result.stderr.strip()}")
    return result.stdout.strip()


def cmd_outline_tasks(args, wave_dir: Path) -> int:
    worktree = Path(args.worktree)
    targets = load_targets(wave_dir)
    dump = load_dump(worktree, args.dump_file)
    task = build_outline_task(dump, targets, resolve_ref(worktree, args.ref), wave_dir, worktree)
    write_task(task, "outline", args, wave_dir)
    return 0


def _outline_context(worktree: Path, wave_dir: Path, args) -> tuple[dict, dict, dict, dict, dict]:
    targets = load_targets(wave_dir)
    dump = load_dump(worktree, args.dump_file)
    state = load_state(wave_dir / "state.json")
    wave_ids = {r["id"] for r in state.values()}
    levels, types = existing_counts(dump, wave_ids)
    remaining = remaining_counts(targets, levels)
    inventory = existing_inventory(
        {"questions": [q for q in dump["questions"] if q["document"]["id"] not in wave_ids]}, dump_redirects(dump))
    return targets, dump, remaining, inventory, types


def cmd_outline_apply(args, wave_dir: Path) -> int:
    targets = load_targets(wave_dir)
    written = outline_apply(Path(args.batch), wave_dir / "outline", targets)
    print(f"wrote outlines: {', '.join(written) or 'none'}")
    return 0 if written else 1


def cmd_dedupe_tasks(args, wave_dir: Path) -> int:
    worktree = Path(args.worktree)
    targets = load_targets(wave_dir)
    dump = load_dump(worktree, args.dump_file)
    outlines = load_outlines(wave_dir / "outline")
    missing = [t for t in targets["topics"] if t not in outlines]
    if missing:
        raise WaveError(f"outlines missing for: {', '.join(missing)}")
    task = build_dedupe_task(dump, targets, outlines, resolve_ref(worktree, args.ref), wave_dir, worktree)
    write_task(task, "dedupe", args, wave_dir)
    return 0


def cmd_outline_check(args, wave_dir: Path) -> int:
    targets, _, remaining, inventory, types = _outline_context(Path(args.worktree), wave_dir, args)
    failures = check_outline(load_outlines(wave_dir / "outline"), remaining, inventory, targets, types)
    for failure in failures:
        print(failure)
    print("outline ok" if not failures else f"{len(failures)} failure(s)")
    return 0 if not failures else 1


def cmd_allocate(args, wave_dir: Path) -> int:
    worktree = Path(args.worktree)
    targets, dump, remaining, inventory, types = _outline_context(worktree, wave_dir, args)
    outlines = load_outlines(wave_dir / "outline")
    failures = check_outline(outlines, remaining, inventory, targets, types)
    if failures:
        raise WaveError("outline-check fails; allocate refused:\n" + "\n".join(failures))
    state = load_state(wave_dir / "state.json")
    state = allocate(outlines, state, {q["document"]["id"] for q in dump["questions"]})
    save_state(wave_dir / "state.json", state)
    save_planned_new(wave_dir, state)
    planned = sum(1 for r in state.values() if r["status"] == "planned")
    print(f"{len(state)} records, {planned} planned")
    return 0


def _only(args) -> list[str] | None:
    if not getattr(args, "only", None):
        return None
    return [s.strip() for s in args.only.split(",") if s.strip()]


def cmd_gen_tasks(args, wave_dir: Path) -> int:
    worktree = Path(args.worktree)
    state = load_state(wave_dir / "state.json")
    tasks, entries = build_gen_tasks(state, load_targets(wave_dir), resolve_ref(worktree, args.ref), worktree,
                                     wave_dir, registry_for_build(args, args.name or "gen", wave_dir), _only(args),
                                     args.max_estimate, utc_now().strftime("%Y-%m-%d"))
    write_tasks(tasks, entries, args.name or "gen", "gen", args, wave_dir)
    return 0


def cmd_retry_plan(args, wave_dir: Path) -> int:
    worktree = Path(args.worktree)
    state = load_state(wave_dir / "state.json")
    shortfall = sorted(r["stubId"] for r in state.values() if r["status"] == "shortfall")
    print(f"shortfall (no retry left): {', '.join(shortfall) or 'none'}")
    if not any(r["status"] == "gen-failed" and last_failure_stage(r) == "gen" and retry_left(r, "gen")
               for r in state.values()):
        print("nothing to retry")
        return 0
    tasks, entries = build_gen_tasks(state, load_targets(wave_dir), resolve_ref(worktree, args.ref), worktree,
                                     wave_dir, load_registry(wave_dir), None, args.max_estimate,
                                     utc_now().strftime("%Y-%m-%d"), retry=True)
    existing = len(list((wave_dir / "tasks").glob("gen-retry-*.json")))
    write_tasks(tasks, entries, args.name or f"gen-retry-{existing + 1}", "gen", args, wave_dir)
    return 0


def cmd_apply(args, wave_dir: Path) -> int:
    summary = apply_batch(args.batch, args.stage, wave_dir, Path(args.worktree), args.batch_id)
    for warning in summary["warnings"]:
        print(f"warning: {warning}")
    for failure in summary["failed"]:
        print(f"failed: {failure}")
    print(f"written {len(summary['written'])}, skipped (already applied) {len(summary['skipped'])}, "
          f"failed {len(summary['failed'])}")
    return 0


def cmd_mech(args, wave_dir: Path) -> int:
    worktree = Path(args.worktree)
    state = load_state(wave_dir / "state.json")
    try:
        summary = mech(state, worktree, wave_dir, lambda a: run_cli(a, worktree), run_links=not args.no_links)
    finally:
        # Quarantine moves files before later steps can fail; the state must record it either way.
        save_state(wave_dir / "state.json", state)
    for warning in summary["seedWarnings"]:
        print(f"seed link: {warning}")
    print(f"quarantined: {', '.join(summary['quarantined']) or 'none'}")
    print(f"mechanical failures: {', '.join(summary['failed']) or 'none'}")
    print(f"ok {len(summary['ok'])}, failed {len(summary['failed'])}, quarantined {len(summary['quarantined'])}")
    return 0


def check_links_dead(state: dict) -> dict[str, str]:
    dead = {}
    for record in state.values():
        mech_evidence = record["evidence"].get("mech") or {}
        for issue in mech_evidence.get("issues", []):
            match = re.match(r"^link dead: (\S+) ", issue)
            if match and mech_evidence.get("utc"):
                dead[match.group(1)] = mech_evidence["utc"]
    return dead


def cmd_links_fetch(args, wave_dir: Path) -> int:
    worktree = Path(args.worktree)
    state = load_state(wave_dir / "state.json")
    dump = load_dump(worktree, args.dump_file)
    docs = {norm_content_path(q["path"]): q["document"] for q in dump["questions"]}
    summary = links_fetch(state, docs, wave_dir, worktree, dead_from_check_links=check_links_dead(state))
    save_state(wave_dir / "state.json", state)
    for warning in summary["warnings"]:
        print(f"warning: {warning}")
    for status in ("dead", "suspect", "unknown"):
        for url in summary[status]:
            print(f"{status}: {url}")
    print(f"ok {len(summary['ok'])}, suspect {len(summary['suspect'])}, unknown {len(summary['unknown'])}, "
          f"dead {len(summary['dead'])}")
    return 0


def _docs_and_seeds(worktree: Path, args, state: dict) -> tuple[dict, dict, dict]:
    dump = load_dump(worktree, args.dump_file)
    docs = {norm_content_path(q["path"]): q["document"] for q in dump["questions"]}
    seed_levels, seed_types = existing_counts(dump, {r["id"] for r in state.values()})
    return docs, seed_levels, seed_types


def cmd_verify_tasks(args, wave_dir: Path) -> int:
    worktree = Path(args.worktree)
    state = load_state(wave_dir / "state.json")
    docs, _, _ = _docs_and_seeds(worktree, args, state)
    tasks, entries = build_verify_tasks(state, load_targets(wave_dir), resolve_ref(worktree, args.ref), worktree,
                                        wave_dir,
                                        registry_for_build(args, args.name or f"verify-r{args.round}", wave_dir),
                                        docs, args.round, _only(args), args.max_estimate)
    write_tasks(tasks, entries, args.name or f"verify-r{args.round}", "verify", args, wave_dir)
    ledger = load_ledger(wave_dir)
    ledger.setdefault("verifyItems", {}).update({k: v["hashes"] for k, v in entries.items()})
    write_json_atomic(wave_dir / "ledger.json", ledger)
    return 0


def cmd_verdicts(args, wave_dir: Path) -> int:
    summary = apply_verdicts(args.batch, args.round, wave_dir, Path(args.worktree), args.batch_id)
    for warning in summary.pop("warnings"):
        print(f"warning: {warning}")
    for key, stubs in summary.items():
        print(f"{key}: {', '.join(stubs) or 'none'}")
    if summary["unverified"]:
        print("unverified: retry once with verify-tasks --only, then resolve what remains")
    return 0


def cmd_repair_tasks(args, wave_dir: Path) -> int:
    worktree = Path(args.worktree)
    state = load_state(wave_dir / "state.json")
    targets = load_targets(wave_dir)
    docs, seed_levels, seed_types = _docs_and_seeds(worktree, args, state)
    tasks, entries, revised = build_repair_tasks(state, targets, resolve_ref(worktree, args.ref), worktree, wave_dir,
                                                 registry_for_build(args, args.name or "repair", wave_dir), docs,
                                                 args.max_estimate)
    write_tasks(tasks, entries, args.name or "repair", "repair", args, wave_dir)
    save_state(wave_dir / "state.json", state)
    if revised:
        print(f"stubs revised after reject: {', '.join(revised)}")
    for line in plan_counts(state, targets, seed_levels, seed_types):
        print(f"count: {line}")
    return 0


def cmd_resolve(args, wave_dir: Path) -> int:
    state = load_state(wave_dir / "state.json")
    resolve(state, args.stub, args.as_status, args.reason, Path(args.worktree), wave_dir)
    save_state(wave_dir / "state.json", state)
    print(f"{args.stub}: {args.as_status}")
    return 0


def cmd_finalize(args, wave_dir: Path) -> int:
    state = load_state(wave_dir / "state.json")
    open_records = unresolved(state)
    if open_records:
        for record in sorted(open_records, key=lambda r: r["stubId"]):
            print(f"unresolved: {record['stubId']} {record['status']}")
        print(f"{len(open_records)} record(s) not terminal")
        return 1
    _, seed_levels, seed_types = _docs_and_seeds(Path(args.worktree), args, state)
    failures, shortfall = acceptance(state, load_targets(wave_dir), seed_levels, seed_types,
                                    load_planned_new(wave_dir))
    counts = {s: sum(1 for r in state.values() if r["status"] == s) for s in STATUSES}
    print("statuses: " + ", ".join(f"{k} {v}" for k, v in counts.items() if v))
    for line in shortfall:
        print(f"shortfall: {line}")
    for failure in failures:
        print(f"acceptance: {failure}")
    print("acceptance ok" if not failures else "acceptance FAILED: ask the owner")
    return 0 if not failures else 1


def cmd_final_gate(args, wave_dir: Path) -> int:
    worktree = Path(args.worktree)
    state = load_state(wave_dir / "state.json")
    wave_paths = {r["path"] for r in state.values()}
    seed_paths = {f for f in committed_files(worktree, args.seed_ref, "content/dotnet") if f not in wave_paths}
    try:
        problems, owner_review = final_gate(state, worktree, wave_dir, lambda a: run_cli(a, worktree), seed_paths,
                                            args.accept_owner_review)
    finally:
        save_state(wave_dir / "state.json", state)
    for path in owner_review:
        print(f"owner-review (published, not final): {path}")
    for problem in problems:
        print(f"gate: {problem}")
    print("final gate ok" if not problems else f"final gate FAILED: {len(problems)} problem(s)")
    return 0 if not problems else 1


def cmd_report(args, wave_dir: Path) -> int:
    state = load_state(wave_dir / "state.json")
    _, seed_levels, seed_types = _docs_and_seeds(Path(args.worktree), args, state)
    markdown, rows = build_report(state, load_targets(wave_dir), args.group, seed_levels, seed_types,
                                  Path(args.worktree))
    md_path, csv_path = write_report(wave_dir, args.group, markdown, rows)
    print(f"wrote {md_path} and {csv_path} ({len(rows)} rows)")
    return 0


def cmd_owner_approve(args, wave_dir: Path) -> int:
    state = load_state(wave_dir / "state.json")
    owner_approve(state, args.stub, args.reason, Path(args.worktree), utc_now().strftime("%Y-%m-%d"))
    save_state(wave_dir / "state.json", state)
    print(f"{args.stub}: owner-approved ({state[args.stub]['verifiedDate']})")
    return 0


def cmd_owner_check(args, wave_dir: Path) -> int:
    worktree = Path(args.worktree)
    state = load_state(wave_dir / "state.json")
    try:
        failures, edited = owner_check(state, worktree, wave_dir, lambda a: run_cli(a, worktree),
                                       utc_now().strftime("%Y-%m-%d"))
    finally:
        save_state(wave_dir / "state.json", state)
    for path in edited:
        print(f"owner-edited: {path}")
    for failure in failures:
        print(f"mechanical failure: {failure}")
    return 0 if not failures else 1


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="wave.py", description=__doc__)
    parser.add_argument("--wave-dir", required=True, help="wave directory, e.g. docs/content-wave-2")
    parser.add_argument("--worktree", default=str(REPO_ROOT))
    parser.add_argument("--server-repo", default=str(DEFAULT_SERVER_REPO))
    parser.add_argument("--dump-file", help="use a saved `dump` JSON instead of running the content CLI")
    sub = parser.add_subparsers(dest="command", required=True)

    budget = sub.add_parser("budget", help="record a review_budget reading and check the ledger rule")
    budget.add_argument("--record", type=float, help="spend today in USD as reported by review_budget")
    budget.add_argument("--baseline", action="store_true",
                        help="mark this reading as the pre-wave baseline (spend not attributed to the wave)")
    budget.add_argument("--estimate", type=float, help="estimate of the next submit; exit 1 if it may not go")
    budget.add_argument("--stage", choices=STAGES, help="stage of the next submit, for the completion forecast")

    ledger_submit = sub.add_parser("ledger-submit", help="record a tasks_submit in the ledger")
    ledger_submit.add_argument("--name", required=True)
    ledger_submit.add_argument("--task", required=True)
    ledger_submit.add_argument("--plan-id", required=True)
    ledger_submit.add_argument("--batch-id", required=True)
    ledger_submit.add_argument("--estimate", type=float, required=True)
    ledger_submit.add_argument("--confirmation", required=True)
    ledger_submit.add_argument("--stage", choices=STAGES, help="stage of the batch; the completion forecast needs it")

    ledger_collect = sub.add_parser("ledger-collect", help="record a tasks_collect and its actual cost")
    ledger_collect.add_argument("--batch-id", required=True)
    ledger_collect.add_argument("--actual", type=float)

    pre = sub.add_parser("preflight", help="check a task file and its ref")
    pre.add_argument("--task", required=True)

    for name in ("outline-tasks", "dedupe-tasks"):
        step = sub.add_parser(name)
        step.add_argument("--ref", required=True)
        step.add_argument("--overwrite", action="store_true")

    apply = sub.add_parser("outline-apply")
    apply.add_argument("--batch", required=True)
    sub.add_parser("outline-check")
    sub.add_parser("allocate")

    for name in ("gen-tasks", "retry-plan"):
        step = sub.add_parser(name)
        step.add_argument("--ref", required=True)
        step.add_argument("--max-estimate", type=float, help="split into several task files of at most this many USD")
        step.add_argument("--name", help="task file name (default gen / gen-retry-<n>)")
        step.add_argument("--overwrite", action="store_true")
        if name == "gen-tasks":
            step.add_argument("--only", help="comma-separated stubIds")

    apply_step = sub.add_parser("apply", help="apply collected generation or repair results")
    apply_step.add_argument("--batch", required=True, help="directory tasks_collect reported (holds index.json)")
    apply_step.add_argument("--stage", required=True, choices=["gen", "repair"])
    apply_step.add_argument("--batch-id", help="defaults to the msgbatch_* directory name")

    mech_step = sub.add_parser("mech", help="dump, quarantine, validate, run-snippets, check-links, evidence")
    mech_step.add_argument("--no-links", action="store_true", help="skip check-links (offline runs)")
    sub.add_parser("links-fetch", help="fetch every link of every wave question for fact-check evidence")

    verify = sub.add_parser("verify-tasks", help="build fact-check task files (Sonnet)")
    verify.add_argument("--ref", required=True)
    verify.add_argument("--round", type=int, required=True, choices=[1, 2])
    verify.add_argument("--only", help="comma-separated stubIds")
    verify.add_argument("--max-estimate", type=float)
    verify.add_argument("--name", help="task file name (default verify-r<round>)")
    verify.add_argument("--overwrite", action="store_true")

    verdicts = sub.add_parser("verdicts", help="apply collected fact-check verdicts")
    verdicts.add_argument("--batch", required=True)
    verdicts.add_argument("--round", type=int, required=True, choices=[1, 2])
    verdicts.add_argument("--batch-id")

    repair = sub.add_parser("repair-tasks", help="build repair task files (Opus)")
    repair.add_argument("--ref", required=True)
    repair.add_argument("--max-estimate", type=float)
    repair.add_argument("--name", help="task file name (default repair)")
    repair.add_argument("--overwrite", action="store_true")

    resolve_step = sub.add_parser("resolve", help="close an unresolved record by hand")
    resolve_step.add_argument("--stub", required=True)
    resolve_step.add_argument("--as", dest="as_status", required=True, choices=["owner-review", "shortfall"])
    resolve_step.add_argument("--reason", required=True)

    shortfall = sub.add_parser("shortfall", help="pick (and with --apply mark) stubs to drop for budget reasons")
    shortfall.add_argument("--count", type=int, required=True)
    shortfall.add_argument("--apply", action="store_true", help="mark the stubs shortfall; a dry run otherwise")
    sub.add_parser("plan-counts", help="read-only: counts, type quotas and outline item sizes")

    sub.add_parser("finalize", help="exit 1 while any record is open; check acceptance")
    gate = sub.add_parser("final-gate", help="whole-bank checks; bind every file to its pass; mark final")
    gate.add_argument("--seed-ref", default="main", help="ref whose content/dotnet files are the seeds")
    gate.add_argument("--accept-owner-review", metavar="REASON",
                      help="let owner-review files (meta.verified placeholder) pass the gate; the reason is recorded")
    report = sub.add_parser("report", help="owner review sheet for a topic group")
    report.add_argument("--group", required=True)
    sub.add_parser("owner-check", help="re-check files edited after the final gate")
    approve = sub.add_parser("owner-approve", help="owner accepts an owner-review question unchanged")
    approve.add_argument("--stub", required=True)
    approve.add_argument("--reason", required=True)
    return parser


COMMANDS = {
    "budget": cmd_budget,
    "ledger-submit": cmd_ledger_submit,
    "ledger-collect": cmd_ledger_collect,
    "preflight": cmd_preflight,
    "outline-tasks": cmd_outline_tasks,
    "outline-apply": cmd_outline_apply,
    "dedupe-tasks": cmd_dedupe_tasks,
    "outline-check": cmd_outline_check,
    "allocate": cmd_allocate,
    "gen-tasks": cmd_gen_tasks,
    "retry-plan": cmd_retry_plan,
    "apply": cmd_apply,
    "mech": cmd_mech,
    "links-fetch": cmd_links_fetch,
    "verify-tasks": cmd_verify_tasks,
    "verdicts": cmd_verdicts,
    "repair-tasks": cmd_repair_tasks,
    "resolve": cmd_resolve,
    "shortfall": cmd_shortfall,
    "plan-counts": cmd_plan_counts,
    "finalize": cmd_finalize,
    "final-gate": cmd_final_gate,
    "report": cmd_report,
    "owner-check": cmd_owner_check,
    "owner-approve": cmd_owner_approve,
}


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if sys.stdout.encoding and sys.stdout.encoding.lower() != "utf-8":
        sys.stdout.reconfigure(encoding="utf-8")
    try:
        return COMMANDS[args.command](args, Path(args.wave_dir).resolve())
    except WaveError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
