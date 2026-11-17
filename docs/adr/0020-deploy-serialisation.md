# ADR 0020: Deploy serialisation and ordering

## Status

Accepted by the owner on 2026-11-15. A design decision of Plan 3. It changes spec 8.7.2 in
two places: the "shared concurrency group" of the deploy workflows and the trigger of
`deploy-app`.

## Context

Four workflows change the production server: `deploy-app`, `deploy-content`,
`rotate-db-password` and the operations workflow. Reviewers disagreed on how to keep them
from interfering. One workflow-level group per workflow stops two runs of the same
workflow, but not an older build that finishes after a newer one has deployed, and not a
push that changes both the application and the content and delivers the content before
the application that needs it.

## Decision

1. Each deploy workflow declares a workflow-level concurrency group (`production-app`,
   `production-content`, `production-rotate`, `production-ops`) with
   `cancel-in-progress: false`. Within one workflow the build, smoke test and deployment of
   an older run finish before a newer run starts. GitHub keeps one pending run per group and
   drops the others, and the dropped run is not always the older commit: a late `ci` of an
   older commit can replace the pending run of a newer one. For that reason `deploy-app` does
   not deploy the commit that triggered it. Its resolve step deploys the newest commit on
   `main` whose `ci` push run concluded successfully (read with `gh api` through the workflow's
   `GITHUB_TOKEN` and `actions: read`), never one without a green `ci` run and, because
   `prepare` keeps the no-backwards rule, never one older than the deployed commit. A
   `workflow_run` trigger from an older commit therefore deploys the newest green commit or
   nothing, and a manual run with an empty `ref` resolves the same way (an explicit `ref` is
   deployed as named). Every replaced run loses nothing, as for `deploy-content`.
2. Every remote state change takes the server lock (`flock` on `$APP_DIR/.deploy.lock`),
   which keeps different workflows from overlapping. Uploads go to a per-run staging
   directory and touch nothing else before the lock is held.
3. Automatic deployments refuse to go backwards. The deploy job compares its commit with
   `DIT_COMMIT` of `last-good.env` (application) or the `current` release SHA (content) and
   skips with a notice when its commit is a strict ancestor. An explicit rollback input is
   the only override (Tasks 13 and 14 of the plan). A rollback by dispatch also holds: after it
   succeeds the workflow writes `flags/deploy-hold` on the server over ssh, and every automatic
   (`workflow_run`) deployment ends with a notice while the file exists. Only a manual run
   without rollback clears it. The hold is a file and not a GitHub environment variable
   because `GITHUB_TOKEN` cannot write variables; `rotate-db-password` redeploys the commit
   that runs, not the tip of `main`, and keeps the hold.
4. `deploy-app` runs after a successful `ci` run of a `push` to `main` in this repository
   (`workflow_run`, with the provenance of the run checked) instead of on the push itself,
   so nothing reaches production without the unit, integration, end-to-end, Release and
   schema-compatibility checks. After a successful deployment it dispatches `deploy-content`
   for the tip of `main` when the live content release is behind the tip's `content/` tree,
   so content that needs a newer application, and content whose own delivery failed on the
   lock, is always delivered after it.

## Consequences

- A deployment starts a few minutes after the merge, when `ci` finishes, not at the push.
- A merge whose `ci` run fails never deploys; the next green push deploys everything up to it.
- Required reviewers must not be added to the `production` environment: they would gate the
  scheduled `ops-check`, content deliveries and the follow-up dispatch, and a waiting run is
  replaced by the next one, so the daily alert would stop without a red run. The branch
  policy (`main` only) is the access boundary.
- A stale run cannot move production backwards, but a deliberate rollback stays possible.
- The server lock is the single point of mutual exclusion, so every script that changes
  server state (deploy, content switch, restore, rotation, backup) must take it, or inherit
  it through `DIT_LOCKED`.
- Because the guards are files (`flags/maintenance`, `.rotation-pending`, `.umami-recovery`)
  and not locks, an interrupted operation blocks later ones until an operator clears it
  through the documented procedure.
