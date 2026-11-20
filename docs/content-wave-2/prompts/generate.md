# Generation instructions

You write complete bilingual (Russian and English) interview questions for the
.NET stack of Dev Interview Trainer. Each question is one YAML file that must
pass the strict reader and validator described in `docs/content-guide.md`
(attached). Follow that guide exactly: field reference, rules per question
type, the snippet sandbox, Markdown rules and the quality bar.

## Input

The item text gives one or two stubs as JSON (stubId, topic, slug, level,
type, subtopic, angle, answerHinge, sources). The attached files are the outline of the topic and existing
questions of the topic and its neighbours: use them as the style reference and
never duplicate their facts.

## Output

For every stub, one entry `{stubId, yaml}` in `questions`. `yaml` is the raw
file text:

- the first line is exactly
  `# yaml-language-server: $schema=../../schema/question.schema.json`;
- **no `id` line**: the driver inserts the allocated id; a model-written `id`
  is rejected;
- raw YAML, no Markdown fences around it;
- keep the stub's `slug`, `level`, `type` and `topic` exactly; `stack: dotnet`;
- `meta.authors: [platonenkov]`; `meta.verified: 1970-01-01`, a placeholder: the
  question is not verified yet, and only a fact-check pass sets the real date
  (the driver overwrites any other value).

Use `notes` for every claim you hold with less than full certainty, one entry
per claim with the stubId. Notes are read by the fact-checker and the owner.

## Content rules (verbatim from the plan's global constraints)

- One question per file at `content/dotnet/{topic}/{slug}.yaml`, first line `# yaml-language-server: $schema=../../schema/question.schema.json`, LF, UTF-8 without BOM.
- `id`: ULID allocated by `wave.py` for every stub before generation, never written by a model, never changed. `slug`: lowercase kebab-case, at most 80 characters, unique in the stack, not equal to any slug of `content/dotnet/redirects.yaml` (`from` and `to` are `topic/slug`; compare the slug part).
- `ru` and `en` complete and equivalent. Natural technical Russian following the glossary in `prompts/generate.md` and the seeds as style reference. Plain hyphen `-` only: a U+2014 or U+2013 anywhere is a mechanical failure reported with its field and line, never rewritten silently. No time-relative wording ("latest", "current", "new"): always name the .NET or C# version.
- Code is not translated; comments in code are English.
- Every question has at least one link, target two: Microsoft Learn (`https://learn.microsoft.com/en-us/...`, `lang: en`), the C# specification, `dotnet/runtime` or other official repositories, RFCs, OWASP, then recognised authors (Stephen Toub, Eric Lippert, Stephen Cleary, Andrew Lock, Martin Fowler, Jon Skeet). No URL fragments unless certain. No invented APIs, overloads, flags or links.
- Executable code: `output` questions are C#, BCL only (no NuGet, no ASP.NET Core or EF Core types, no network), deterministic, short output; no timing, thread ids, hash codes, addresses or unordered-collection ordering; `harness: none` forbidden. `find-bug` on BCL code is runnable and states the observable failure; `find-bug` and code-bearing `single`/`multi` about ASP.NET Core or EF Core use `harness: none` with `meta.noRunReason` and no `fixedSource`, and are listed as "not executed".
- Executable code by dependency, not by topic: a snippet that needs only the BCL is runnable whatever its topic; `output` questions are BCL-only, always; a snippet that needs ASP.NET Core, EF Core, Blazor, MAUI, gRPC, SignalR, Docker or a cloud SDK appears only in `find-bug`, `single` or `multi`, with `harness: none`, `meta.noRunReason` and no `fixedSource`. No `output` or `find-bug` on lead or architect stubs.
- One defensible answer per type: `single` exactly one correct option; `multi` exactly one correct set; `output` exactly one real result; `find-bug` the accepted line sets; `open`/`situation` a reference answer that experts would accept as a strong answer.
- `meta.authors: [platonenkov]` (owner may change before merge). `meta.verified` = UTC date of the fact-check pass against fetched excerpts, set by `wave.py`; owner edits reset it to the owner's review date. `meta.dotnetVersion: "10"` whenever behaviour depends on a version, and both locales say so.

## Levels

- junior: definitions and correct everyday use;
- middle: practical pitfalls and choosing between options;
- senior: internals, diagnosis, trade-offs under load;
- lead: technical decisions with team, process and risk;
- architect: system-wide trade-offs across services and years.

## Russian glossary

Write natural technical Russian, the way a Russian-speaking senior .NET
developer talks, not a word-by-word translation.

| English | Russian |
|---|---|
| value type | тип значения |
| reference type | ссылочный тип |
| thread pool | пул потоков |
| garbage collector | сборщик мусора |
| race condition | состояние гонки |

Keep as is: `middleware`, deadlock, code review. API names, type names and
keywords stay in code font and in English.

## Checklist before you answer

1. Every API, member, overload and flag exists in .NET 10 with that exact name.
2. `output`: you traced the program line by line; `expect` and every option's
   `expect` are the real result; the output is short and deterministic.
3. `find-bug`: every number in `bugLineSets` is the 1-based line of the buggy
   statement in `code.source`; recount the lines.
4. `ru` and `en` state the same facts, numbers, code spans, options and hint
   strength.
5. The hint nudges without giving the answer.
6. Every incorrect option has an explanation in both locales.
7. `linkTitles` has one title per link in both locales.
8. Strings that look like numbers are quoted (`dotnetVersion: "10"`).
9. No U+2014 or U+2013 characters anywhere, including code comments.
