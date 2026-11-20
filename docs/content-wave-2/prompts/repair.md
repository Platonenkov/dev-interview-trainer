# Repair instructions

You rewrite interview questions of Dev Interview Trainer that a fact-checker
or the mechanical checks flagged. All rules of `prompts/generate.md` (attached
as a shared file) apply unchanged: file format, no `id` line, schema comment
first, raw YAML without fences, the content rules, levels, glossary and the
checklist.

## Input

For each question the item gives: stubId, the stub, the current YAML (or the
unreadable text from quarantine), the mechanical evidence (sandbox result,
failures, parity findings), the verifier's issues with their severity, and the
fetched link excerpts.

Keep `meta.verified: 1970-01-01`: it is a pipeline placeholder, and an issue
about it needs no change and no rebuttal.

## Executable code rule

Executable code by dependency, not by topic: a snippet that needs only the BCL is runnable whatever its topic; `output` questions are BCL-only, always; a snippet that needs ASP.NET Core, EF Core, Blazor, MAUI, gRPC, SignalR, Docker or a cloud SDK appears only in `find-bug`, `single` or `multi`, with `harness: none`, `meta.noRunReason` and no `fixedSource`. No `output` or `find-bug` on lead or architect stubs.

## Rules

1. Rewrite only the listed questions.
2. Fix every issue, or rebut it in `notes` with evidence from the excerpts or
   the sandbox result. A rebuttal without evidence is not accepted.
3. Keep stubId, slug and topic.
4. Change `level` or `type` only when a `fix` issue on that field requests it,
   and say so in `notes`.
5. For a `reject`, write a new question for the revised stub given in the item
   (same topic and slug unless the item gives a new slug).
6. Fix mechanical failures first: a snippet whose real result differs from
   `expect` means the expectation or the code is wrong; the sandbox result is
   ground truth.
7. Return the complete file for every listed question, not a diff.
