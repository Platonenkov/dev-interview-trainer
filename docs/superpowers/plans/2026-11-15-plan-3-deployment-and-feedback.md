# Plan 3: Deployment, Operations and Question Feedback - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The site runs on the owner's VPS behind nginx with TLS, deployed and
reconfigured only through GitHub Actions (app image, content releases, password
rotation), provisioned once by an idempotent bootstrap script, backed up daily
(encrypted, off the server, Data Protection keys included) and checked daily; signed-in
users can like, dislike and report questions from the question page and the islands;
every question has a generated Open Graph image in Russian and English; page views are
counted by cookieless self-hosted analytics; and spec 8.2's phone targets are measured
on the deployed site.

**Architecture:** One Docker image (`Trainer.Web` published with the pinned
`wasm-tools` workload plus an EF Core migration bundle) runs in a Compose project with
PostgreSQL and Umami; every port is bound to `127.0.0.1` and nginx on the host is the
only public listener. Deploy logic lives in versioned bash scripts under
`deploy/remote/` that every workflow uploads before running, serialised on the server
by `flock`; workflows render secrets into `.env` on the runner and stream them over
SSH stdin. Feedback is a small Domain/Application/Infrastructure slice written through
`/api` from islands and through static SSR POST forms from the question page, which
renders them only for signed-in (never cached) responses, so anonymous pages stay
cookieless and output-cached.

**Tech Stack:** .NET 10, ASP.NET Core (static SSR + InteractiveWebAssembly islands),
EF Core + Npgsql, EF Core migration bundles, SkiaSharp (MIT) for Open Graph images,
Docker Engine + Compose plugin, PostgreSQL 17, Umami (MIT), nginx, certbot, `age`
(BSD-3) for backup encryption, `rclone` (MIT) for the off-site copy, GitHub Actions,
`gh` CLI, PowerShell 7 + Pester 5 (bootstrap), bash + shellcheck (server scripts),
xUnit v3 on Microsoft.Testing.Platform, Testcontainers.PostgreSql.

**Spec:** `docs/superpowers/specs/2026-11-11-dev-interview-trainer-design.md` (read it
first; section numbers below refer to it). Plans 1 and 2
(`docs/superpowers/plans/2026-11-11-plan-1-foundation-content-public-site.md`,
`docs/superpowers/plans/2026-11-12-plan-2-sign-in-and-learning.md`) are done; their code
and conventions are the starting point. ADRs 0001-0014 in `docs/adr/` bind this plan,
especially 0004 (key ring unencrypted at rest, backups encrypted here), 0007 (no query
strings of `/{culture}/admin` in proxy logs), 0009 and 0012 (island pages: full page
loads, per-request nonce, never cached), 0013 (`wasm-tools` pinned in the image) and
0014 (phone targets are this plan's acceptance criterion).

**Scope added by the owner on 2026-11-13 (not yet in the spec except section 4's
feedback paragraph):** question feedback for signed-in users, cookieless self-hosted
analytics in the same Compose stack, per-question Open Graph images (ru and en), the
real-phone check of spec 8.2 as an owner step with a recorded result. Google Search
Console registration is excluded.

## Roadmap (all plans)

| Plan | Scope | Spec sections | Depends on |
|---|---|---|---|
| 1 (done) | Solution, content pipeline, public SSR site, CI, scheduled link checker | 3, 5.1, 5.2 (schema), 6, 7.1-7.5, 8.1-8.6 (public part), 9, 10 | - |
| 2 (done) | Sign-in, learning modes, islands, admin page, key store, ADRs 0001-0014 | 4, 5.1-5.4, 8.2, 8.3, 8.6, 8.7 (key store), 8.8, 9/9.1, 10, 11, 12 | 1 |
| **3 (this)** | Internal endpoints, fresh-install readiness, question feedback, Open Graph images, analytics, Dockerfile, migration bundle, Compose, nginx, remote scripts with rollback, encrypted off-site backups and daily ops check, `deploy-app`, `deploy-content`, `rotate-db-password`, bootstrap, link-check reporting, `docs/deployment.md`, deployed-phone acceptance | 4 (feedback), 7.3 (import endpoint), 7.4 (link checker), 8.2 (targets), 8.4 (Open Graph), 8.7, 10 | 1, 2 |
| 4 (running) | Content production in topic batches | 6, 7.6 | 1 |

Each plan is reviewed by Codex and Fable before execution.

## Global Constraints

Carried over from Plans 1 and 2 (unchanged):

- `net10.0`; `global.json` SDK `10.0.203`, `rollForward: latestFeature`, test runner Microsoft.Testing.Platform; nullable, warnings as errors, `AnalysisLevel=latest-recommended`, NuGet audit `all`/`low`; central package management.
- Explicit types instead of `var`; async with `CancellationToken` for IO; `TimeProvider` instead of `DateTime.Now`/`UtcNow`; `StringComparison.Ordinal*`; `System.Text.Json` with cached options or source-generated contexts; `CryptographicOperations.FixedTimeEquals` for every secret comparison.
- Comments, docs, commit messages in English; Conventional Commits; no AI attribution lines anywhere. No long dashes in docs (`DocsTests.Documentation_UsesNoLongDashes`).
- No FluentAssertions, no Moq, no bUnit; no package whose binaries carry a paid or fee-bearing licence (ImageSharp is excluded for its split licence; check NuGet licence and README before adding any package). xUnit v3 built-in `Assert`.
- Dependencies: Domain and Contracts reference nothing; Application → Domain, Contracts; Infrastructure → Application, Content; Web → Infrastructure, Application, Contracts, Web.Client; Web.Client → Domain, Contracts. **Web never references Snippets**; Web code does not use `Trainer.Content` types directly (it goes through Application interfaces).
- Public pages are static SSR, no `[StreamRendering]`, no `blazor.web.js`; anonymous responses carry no `Set-Cookie`, no POST form and no antiforgery token and are output-cached; signed-in responses bypass the cache, are `private, no-store` and uncompressed. Island pages follow ADRs 0009 and 0012.
- WCAG 2.2 AA (4.5:1 body text, 3:1 large text and UI colours), colours only through `tokens.css`; touch targets at least 44×44 px; widths 375, 768, 1280, 1920 px, no horizontal page scroll at 320 px. Every new string in RU and EN (`SharedResource`, `ClientResource`), key-parity tests cover both.
- Never log `Authorization`, `Cookie`, `Set-Cookie`, `X-CSRF-TOKEN`, token values, report comments, Telegram usernames, display names or client IPs.
- Every ADR (`docs/adr/NNNN-title.md`: Status, Context, Decision, Consequences) is written by the task that introduces it, numbered in task order without gaps, and linked from `docs/architecture.md` in the same task. `docs/architecture.md` (configuration table, API table, CI job list), `README.md` and `AGENTS.md` are updated in the task that changes what they describe; `DocsTests` enforce this.
- Test commands (unchanged, recorded in `AGENTS.md` and `ci.yml`): unit/integration `dotnet test --filter-not-trait "Category=E2E" --filter-not-trait "Category=Docker" --filter-not-trait "Category=Release" --ignore-exit-code 8`; Docker, E2E and Release by `--filter-trait`. The new `tests/Trainer.Deploy.Tests` has no trait and runs in the unit command. Server-side checks (shellcheck, Pester, the Compose smoke) run in `.github/workflows/deploy-smoke.yml`.

Plan 3 constraints (spec text quoted where it fixes a value):

- Hosting (8.7): "Docker Compose on the VPS: `app` (ASP.NET Core) + `postgres`, nginx on the host as reverse proxy with TLS (Let's Encrypt). No WebSocket upgrade needed." Umami joins the Compose project (owner scope). Every published Compose port is `127.0.0.1:`-bound (Docker's published ports bypass `ufw`).
- Data Protection: "fixed application name, included in backups" (application name `dev-interview-trainer`, table `data_protection_keys`, ADR 0004); backups are encrypted (ADR 0004).
- "Migrations are applied by an EF Core migration bundle run as a separate deploy step before the new container starts, never by `Migrate()` at startup." Migrations stay backward compatible with the previous image (expand only) because a failed readiness rolls the image back after they ran.
- "the readiness endpoint reports not-ready until import finishes, and nginx/compose health checks use it" (`/health/ready`).
- "**GitHub is the single source of truth for configuration**": secrets in GitHub Secrets, non-secret settings in GitHub Variables, both in the `production` environment; `deploy-app.yml` renders the server `.env` on every deploy; "Nothing is edited by hand on the server".
- Server layout (8.7.1): `/srv/dev-interview-trainer/` with `compose.yaml`, `.env` ("mode 600"), `content/releases/<git-sha>/`, `content/current -> releases/<sha>`, `backups/`; "The last 5 releases are kept".
- Workflows (8.7.2): `ci.yml`, `deploy-app.yml` (changes outside `content/` on `main`, or manual; it runs after a successful `ci` run, ADR 0020), `deploy-content.yml` (push to `main` changing `content/`, or manual), `rotate-db-password.yml` (manual only). All deploy workflows use environment `production` with a deployment branch rule (`main` only). Hardening, verbatim: "third-party actions pinned by full commit SHA; `permissions:` set per job to the minimum (`contents: read`, `packages: write` only where the image is pushed); workflows triggered by pull requests never receive secrets (GitHub default for forks) and never use `pull_request_target`; secrets are passed to remote commands through stdin or env, never on the command line, and never echoed (masked output is not relied upon)."
- Internal endpoint (8.7.2): "`POST /internal/content/import` is served on a separate port bound to `127.0.0.1` on the host and is not proxied by nginx ... requires a bearer token (`CONTENT_IMPORT_TOKEN`), compared with `CryptographicOperations.FixedTimeEquals` ... `GET /internal/version` on the same port returns the app version and supported content schema range."
- Bootstrap (8.7.3): run from the admin's PC, "PowerShell 7 (`pwsh`) ... the remote part is a bash script it uploads and runs", idempotent, Ubuntu LTS, firewall "22, 80, 443 only", unattended security upgrades, `deploy` user, secrets passed to `gh secret set` / `gh variable set` "through stdin, never as arguments", "Nothing is written to disk on the admin's PC except a temporary file that is deleted at the end", prints the BotFather redirect URI `https://<domain>/signin-telegram`.
- nginx (Ubuntu 24.04 package, 1.24): `gzip off` on the proxied location (BREACH guard of Plan 2); sends `X-Forwarded-For` and `X-Forwarded-Proto`; never logs client addresses or query strings (ADR 0007): HTTP error logging is off and only the site's access log, without address and query, is on; returns 404 for `/internal/`.
- Server state: workflows upload only into a per-run staging directory; every change of server state (activating a bundle, `compose.yaml` or `.env`, switching content, database roles, restore swap, password rotation, backups) happens under one `flock` on `$APP_DIR/.deploy.lock`; rollback goes to the last state that passed readiness (`last-good.env`); a state-changing script refuses to start while a guard (`flags/maintenance`, `.rotation-pending`, `.umami-recovery`) exists and names the manual recovery in `docs/deployment.md`; `flags/maintenance` also makes the app reject every public write.
- Image: built with SDK `10.0.303` and `dotnet workload install wasm-tools --version 10.0.303` (ADR 0013, `docs/performance.md`); runs as a non-root user.
- Feedback (spec 4, owner 2026-11-13): "like or dislike a question and report it with a reason chosen from a fixed list (wrong answer, unclear wording, outdated, broken link, other) plus an optional comment. Anonymous visitors see no feedback controls. One rating per user and question (changeable); reports are rate-limited per user and listed for the Admin role." Keys: ratings `like`, `dislike`; reasons `wrong-answer`, `unclear-wording`, `outdated`, `broken-link`, `other`.
- Phone acceptance (8.2, ADR 0014): "on a mid-range phone over 4G, Practice is interactive within 2 s with a warm cache and within 5 s cold", measured on the deployed site with `?perf=1`, 5 cold and 5 warm runs, RU and EN.
- Shell scripts: `#!/usr/bin/env bash` + `set -euo pipefail`, never `set -x`, shellcheck-clean; a secret is read from stdin or an environment variable, never from `$1..$n`, and never passed to a child process as an argument (use `-e NAME` without a value, `--password-stdin`, `curl -H @-`, psql `\getenv`). PostgreSQL administration runs as the container's `postgres` user (`docker compose exec --user postgres`).

## Review Focus

1. **Switching the `current` symlink while the app container runs.** A bind mount of a symlink is resolved once when the container starts, so a naive mount keeps serving the old release after `deploy-content`. Expect: the parent `content/` directory is mounted read-only at `/content`, `App:ContentRoot` is `/content/current`, and an import after the switch reads the new release without a restart. Test: Task 11 smoke `content_switch_is_seen_without_restart`; Task 9 `ComposeFileTests.App_MountsContentParentReadOnly`; Task 14 `same_sha_reupload_is_reused_while_current`.
2. **The first `deploy-app` on a fresh server, before any content release exists.** Expect: readiness turns Ready with an empty site when the database has no active import (so the first deploy is not rolled back), but Failed when the database has an active import and the content directory is missing. Tests: Task 2 `StartupImport_NoReleaseOnFreshDatabase_IsReadyAndEmpty`, `StartupImport_NoReleaseWithActiveImport_IsFailed`; Task 11 smoke `first_deploy_without_content_is_ready`.
3. **A failed or killed deploy after migrations already ran.** Expect: the last good image, `.env` and Compose manifest come back healthy on the new schema, and the workflow fails visibly. Tests: Task 8 `MigrationSafetyTests.NewMigrations_AreExpandOnly`; Task 11 `schema-compat.yml` (the deployed release's own `Release` suite on the migrated, non-empty database) and smoke `failed_readiness_rolls_back_to_last_good`, `killed_deploy_then_failed_deploy_rolls_back_to_last_good`, `killed_between_record_steps_never_mixes_deployments`.
4. **Two deploys at once or out of order** (one push changing app and content; a password rotation during a deploy; an older run finishing after a newer one). Expect: uploads and remote work never mix, production never moves backwards automatically, and content that needs a newer app is delivered after it. Tests: Task 11 smoke `concurrent_uploads_and_deploys_serialise`, `guard_files_block_deploys`; Task 15 `deploy_is_refused_while_rotation_pending`; Task 13 `DeployApp_ChecksWorkflowRunProvenance`; Task 13 `WorkflowHardeningTests.DeployWorkflows_UseWorkflowLevelConcurrencyAndProductionEnvironment`; Task 14 `DeployApp_DispatchesContentFollowUpWithoutRollback`.
5. **Secrets leaking through argv, logs or files** (`ps` on the server, `set -x`, workflow logs, a temp file left on the runner or the admin's PC). Expect: no secret value ever appears in a command line or a log, and temp files are removed in `always()` / `finally`. Tests: Task 13 `WorkflowHardeningTests.SecretsAppearOnlyInEnvBlocks`, `RunScripts_NeverInterpolateSecretsIntoSshArguments`; Task 9 `ShellScriptTests.NoScriptEnablesXtrace`; Task 16 Pester `Set-GitHubValue passes the value on stdin only`, `Bootstrap removes its temporary directory on failure`; Task 15 `rotate_new_password_never_in_process_list`; Task 10 `nginx-check.sh` sentinel requests.

---

## File Structure

```
Dockerfile, .dockerignore                                    image (Task 8)
deploy/
  compose/compose.yaml, compose/postgres/pg_hba.conf          Compose project (Tasks 9, 7)
  nginx/dev-interview-trainer.conf.template, .http.conf.template   host proxy (Task 10)
  systemd/dit-backup.service, dit-backup.timer                daily backup (Task 12)
  scripts/render-env.sh                                       runner side: GitHub env -> .env text (Task 9)
  remote/lib.sh, deploy-app.sh, ensure-databases.sh,          server side, uploaded into staging by every workflow
         receive-content.sh, rotate-db-password.sh,
         backup.sh, restore.sh, ops-check.sh
  bootstrap/Bootstrap.ps1, BootstrapLib.psm1,                 first-time provisioning (Task 16)
            remote/provision.sh, tests/BootstrapLib.Tests.ps1
  tests/smoke.sh, render-env.test.sh, nginx-check.sh, compose.smoke.yaml, content/   checks (Tasks 9-11)
  nginx/nginx.conf.template                                   http-level logging off (Task 10)
.github/actions/ssh-setup/action.yml                          local composite action (Task 13)
.github/workflows/deploy-smoke.yml, schema-compat.yml, deploy-app.yml, deploy-content.yml, content-checks.yml,
                  rotate-db-password.yml, ops-check.yml; ci.yml, link-check.yml (modified)
.github/dependabot.yml
src/Trainer.Domain/Feedback/                                  QuestionRating, ReportReason, ReportComment, ReportId
src/Trainer.Application/Content/IContentStatusQueries.cs, IContentSchemaInfo.cs
src/Trainer.Application/Feedback/                             service and query interfaces, options, results
src/Trainer.Contracts/FeedbackContracts.cs
src/Trainer.Infrastructure/Feedback/, Persistence/Entities/QuestionRatingEntity.cs, QuestionReportEntity.cs,
                          Persistence/ContractMigrationAttribute.cs, Migrations/<ts>_QuestionFeedback
src/Trainer.Web/Internal/                                     internal port, token, import and version endpoints
src/Trainer.Web/Api/FeedbackEndpoints.cs
src/Trainer.Web/OpenGraph/                                    card renderer, disk cache, endpoint, fonts (TTF, OFL)
src/Trainer.Web/Analytics/AnalyticsOptions.cs, Components/Shared/AnalyticsScript.razor
src/Trainer.Web/Components/Shared/QuestionFeedbackForms.razor, Pages/Admin.razor, Pages/QuestionPage.razor
src/Trainer.Web.Client/Components/QuestionFeedbackBar.razor
tests/Trainer.Deploy.Tests/                                   workflow, Compose, nginx and script checks (new)
docs/deployment.md (new); docs/adr/0015..0021; docs/architecture.md, docs/performance.md, README.md, AGENTS.md
```

---

### Task 1: Internal import and version endpoints

**Files:**
- Create: `src/Trainer.Web/Internal/InternalOptions.cs`, `InternalEndpoints.cs`, `InternalPortFilter.cs`, `ImportTokenFilter.cs`, `InternalStartupCheck.cs`, `InternalJsonContext.cs`, `AppVersion.cs`; `src/Trainer.Application/Content/IContentSchemaInfo.cs`, `IContentStatusQueries.cs`; `src/Trainer.Infrastructure/Content/ContentSchemaInfo.cs`, `src/Trainer.Infrastructure/Queries/ContentStatusQueries.cs`; `tests/Trainer.TestSupport/LocalPortFromHeaderStartupFilter.cs`; `docs/adr/0015-internal-endpoint-and-content-mount.md`
- Modify: `src/Trainer.Web/Program.cs`, `src/Trainer.Infrastructure/DependencyInjection.cs`, `tests/Trainer.TestSupport/TrainerWebFactory.cs`, `tests/Trainer.Web.Release/PublishedAppFixture.cs`, `tests/Trainer.Web.Tests/DocsTests.cs` (P6: extend the expected set of `Architecture_ConfigurationTableMatchesTheOptionClasses` with this task's keys, `App:Internal:Port` and `ContentImport:Token`), `docs/architecture.md` (new section "Internal endpoints" added to the Contents list, configuration table)
- Test: `tests/Trainer.Web.Tests/InternalEndpointTests.cs`, `InternalPortFilterTests.cs`, `InternalStartupCheckTests.cs`

**Interfaces:**
- Consumes: `IContentImportService.ImportAsync(string contentRoot, CancellationToken)`, `ContentImportResult` (Plan 1), `AppOptions.ContentRoot`.
- Produces:
  - `sealed class InternalOptions { const string SectionName = "App:Internal"; int Port { get; init; } }` (0 = internal endpoints not mapped); token key `ContentImport:Token` (env `ContentImport__Token`).
  - `static class InternalStartupCheck { void EnsureValid(InternalOptions options, string? token, IHostEnvironment env); }`: throws `InvalidOperationException` when `Port > 0` and the token is shorter than 32 characters, and in Production when `Port <= 0` or the token is missing.
  - `sealed class InternalPortFilter(IOptions<InternalOptions>) : IEndpointFilter`: 404 with an empty body unless `HttpContext.Connection.LocalPort == Port`; the filter sets `IStatusCodePagesFeature.Enabled = false`, otherwise `UseStatusCodePagesWithReExecute` would turn the 404 into the HTML not-found page (F14). The check is on the socket, not the `Host` header, so `Host: x:8081` through nginx cannot reach it.
  - `sealed class ImportTokenFilter(IConfiguration) : IEndpointFilter`: expects `Authorization: Bearer <token>`; compares UTF-8 bytes with `CryptographicOperations.FixedTimeEquals` (length mismatch is compared against a same-length dummy so timing does not reveal it); 401 with `WWW-Authenticate: Bearer` otherwise. Never logs the header.
  - `POST /internal/content/import` (no body) → imports `App:ContentRoot` with `IHostApplicationLifetime.ApplicationStopping` as the token, not `RequestAborted`, so a client that disconnects or times out never cancels an import half-way and a retry gets a definitive answer (the importer's advisory lock queues it behind the running one; R5); 200 for `Imported`/`Unchanged`, 422 for `Rejected` or a missing root (issue code `content-root-missing`), body `InternalImportDto(string Status, string Hash, int Imported, int Retired, IReadOnlyList<InternalImportIssueDto> Issues)`; `InternalImportIssueDto(string Path, string Code, string Message)`. `Status` is the lowercase enum name.
  - `GET /internal/version` → `InternalVersionDto(string AppVersion, int ContentSchemaMin, int ContentSchemaMax, int RenderVersion, string? ActiveContentHash)`; no token (the port is the boundary; the values are not secret). `ActiveContentHash` lets `receive-content.sh` tell whether a failed import call committed (C7).
  - `interface IContentStatusQueries { Task<string?> GetActiveHashAsync(CancellationToken ct); }` (the `content_imports` row with `is_active`; null when none).
  - `interface IContentSchemaInfo { int MinSupported { get; } int MaxSupported { get; } int RenderVersion { get; } }` implemented from `ContentSchema` in Infrastructure.
  - `static class AppVersion { string Current { get; } }`: the Web assembly's `AssemblyInformationalVersionAttribute` value, `"dev"` when absent.
  - `InternalJsonContext` (source-generated, camelCase) for both DTOs.
  - Test support: `TrainerWebFactory.InternalPort` (default 0) and `LocalPortFromHeaderStartupFilter`, registered only by the factory, which copies `X-Test-Local-Port` into `Connection.LocalPort` (TestServer has no sockets).
- ADR 0015 (designer's call, no spec text changes): separate Kestrel endpoint `http://+:8081` (`Kestrel:Endpoints:Internal`), port filter on the socket, content parent directory mounted read-only so symlink switches are seen (Review Focus 1), and `receive-content.sh` reads the token from the server `.env` (rendered from the GitHub secret) instead of receiving it from the workflow, so `deploy-content` needs no application secret.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public async Task Version_OnInternalPort_ReturnsAppVersionAndSchemaRange()
{ InternalVersionDto v = await GetVersion(port: 8081); Assert.Equal((1, 1), (v.ContentSchemaMin, v.ContentSchemaMax)); Assert.False(string.IsNullOrEmpty(v.AppVersion)); }
[Fact] public async Task Version_OnPublicPort_Returns404WithEmptyBody() { /* status 404, no body, no text/html */ }
[Fact] public async Task Version_ReportsActiveContentHashAfterImport() { }
[Fact] public async Task Import_SpoofedHostHeaderOnPublicPort_Returns404() { /* Host: localhost:8081, X-Test-Local-Port: 8080 */ }
[Fact] public async Task Import_WithoutToken_Returns401AndImportsNothing() { /* content_imports row count unchanged */ }
[Fact] public async Task Import_WrongTokenOfSameLength_Returns401() { }
[Fact] public async Task Import_ValidToken_ImportsAndEvictsOutputCache() { /* page cached, change test content title, import => 200 "imported", next GET shows new title */ }
[Fact] public async Task Import_SameContentTwice_SecondIsUnchanged() => Assert.Equal("unchanged", (await ImportOk()).Status);
[Fact] public async Task Import_RejectedContent_Returns422WithIssuesAndKeepsPreviousContent() { }
[Fact] public async Task Import_MissingRoot_Returns422ContentRootMissing() { }
[Fact] public async Task Import_ClientDisconnect_ImportStillCompletes() { /* cancel the HttpClient call right after sending; the import commits; a second call answers "unchanged" */ }
[Fact] public async Task Import_TokenNeverLogged() { /* CapturingLoggerProvider: no entry contains the token */ }
[Theory] [InlineData("Production", 8081, null)] [InlineData("Production", 0, "0123456789abcdef0123456789abcdef")] [InlineData("Testing", 8081, "short")]
public void StartupCheck_Throws(string env, int port, string? token) => Assert.Throws<InvalidOperationException>(() => InternalStartupCheck.EnsureValid(new() { Port = port }, token, Env(env)));
[Fact] public async Task PortFilter_UsesSocketPortNotHost() { /* DefaultHttpContext, LocalPort 8080, Host "x:8081" => 404 */ }
```

- [ ] **Step 2:** Run `dotnet test tests/Trainer.Web.Tests` → FAIL (types missing).
- [ ] **Step 3:** Implement; map the group in `Program.cs` only when `Port > 0`; call `InternalStartupCheck.EnsureValid` with the other pre-build checks; `PublishedAppFixture` sets `ContentImport__Token`, `App__Internal__Port` and a second Kestrel URL. Write ADR 0015 and the architecture section; add `App:Internal:Port` and `ContentImport:Token` to the configuration table.
- [ ] **Step 4:** Run `dotnet test tests/Trainer.Web.Tests` → PASS.
- [ ] **Step 5:** Commit `feat(web): add token-protected internal import and version endpoints`.

### Task 2: Readiness on a fresh install and the maintenance gate

**Files:**
- Create: `src/Trainer.Web/Startup/MaintenanceGateMiddleware.cs`
- Modify: `src/Trainer.Web/Startup/ContentStartupImporter.cs`, `src/Trainer.Web/Startup/AppOptions.cs` (`MaintenanceFlagPath`), `src/Trainer.Web/Program.cs`, `docs/architecture.md` ("Startup, readiness and health", configuration table)
- Test: `tests/Trainer.Web.Tests/StartupImportTests.cs`, `MaintenanceGateTests.cs`

**Interfaces:**
- Consumes: `IContentStatusQueries.GetActiveHashAsync` (Task 1).
- Rule (designer's call): before importing, when `<root>/schema-version` does not exist (root missing, or `current` not created yet), the importer asks `GetActiveHashAsync`: null ⇒ log a warning "No content release yet" and `MarkReady()` (the site serves an empty stack list until `deploy-content` runs); a hash ⇒ log an error and `MarkFailed()` after loading the stored snapshot (the previous content keeps serving). Transient database errors in this query follow the existing retry loop.
- Produces: `App:MaintenanceFlagPath` (string, default unset = gate off; Compose sets `/flags/maintenance`, the read-only mount of `${APP_DIR}/flags`) and `MaintenanceGateMiddleware`, registered right after `UseForwardedHeaders`: while the file exists, every request on the public port whose method is not `GET`, `HEAD` or `OPTIONS`, and every request under `TelegramAuth.CallbackPath` (`/signin-telegram`, a GET that creates or updates users), gets 503 with `Retry-After: 60` and an empty body, before routing, so no form post, `/api` write or sign-in callback can reach a handler (P9) (Q1: the write boundary is the application, not nginx, because nginx buffers a request body before proxying and a request admitted before the flag appeared can still arrive later). The file check is cached for 1 second (`TimeProvider`). The internal port is exempt, and the startup import is not an HTTP request, so content still loads during a restore.

- [ ] **Step 1:** Tests: `MaintenanceGate_FlagPresent_RejectsPostPutDeleteWith503`, `MaintenanceGate_FlagPresent_AllowsGet`, `MaintenanceGate_FlagPresent_RejectsSignInCallbackGet`, `MaintenanceGate_FlagRemoved_WritesWorkWithinTwoSeconds`, `MaintenanceGate_InternalPortExempt`, `MaintenanceGate_Unconfigured_IsOff`; `StartupImport_NoReleaseOnFreshDatabase_IsReadyAndEmpty` (`/health/ready` 200, `/en/dotnet` 404); `StartupImport_NoReleaseWithActiveImport_IsFailed` (import, then point `App:ContentRoot` at an empty directory, restart host ⇒ `/health/ready` 503 and the old question page still 200); `StartupImport_ReleaseAppearsLater_InternalImportMakesItServe` (fresh, then copy content and `POST /internal/content/import` ⇒ question page 200).
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement and document the rule in the readiness table. **Step 4:** Run `dotnet test tests/Trainer.Web.Tests` → PASS.
- [ ] **Step 5:** Commit `feat(web): report ready on a fresh install and add a file-based maintenance gate`.

### Task 3: Question feedback model and persistence

**Files:**
- Create: `src/Trainer.Domain/Feedback/QuestionRating.cs`, `QuestionRatingKeys.cs`, `ReportReason.cs`, `ReportReasonKeys.cs`, `ReportComment.cs`, `ReportId.cs`; `src/Trainer.Application/Feedback/IQuestionFeedbackService.cs`, `IQuestionFeedbackQueries.cs`, `FeedbackOptions.cs`, `FeedbackResults.cs`; `src/Trainer.Infrastructure/Feedback/QuestionFeedbackService.cs`, `QuestionFeedbackQueries.cs`; entities `QuestionRatingEntity`, `QuestionReportEntity` with configurations; migration `QuestionFeedback`; `docs/adr/0016-question-feedback.md`
- Modify: `tests/Trainer.Domain.Tests/PersistedEnumValuesTests.cs`, `src/Trainer.Infrastructure/DependencyInjection.cs`, `tests/Trainer.Web.Tests/DocsTests.cs` (P6: extend the expected set of `Architecture_ConfigurationTableMatchesTheOptionClasses` with this task's keys, `App:Feedback:ReportsPerDay`), `docs/architecture.md` (data model, new section "Question feedback" added to the Contents list)
- Test: `tests/Trainer.Domain.Tests/FeedbackKeysTests.cs`, `ReportCommentTests.cs`; `tests/Trainer.Infrastructure.Tests/QuestionFeedbackServiceTests.cs`

**Interfaces:**
- Produces:
  - `enum QuestionRating { Dislike = -1, Like = 1 }`; `static class QuestionRatingKeys { string ToKey(QuestionRating); bool TryParse(string? key, out QuestionRating rating); }` (`like`, `dislike`, case-sensitive).
  - `enum ReportReason { WrongAnswer = 1, UnclearWording = 2, Outdated = 3, BrokenLink = 4, Other = 5 }`; `static class ReportReasonKeys` (same members; keys from Global Constraints; `IReadOnlyList<ReportReason> All` in that order).
  - `static class ReportComment { const int MaxLength = 1000; bool TryNormalise(string? raw, out string? comment); }`: null or whitespace ⇒ true with null; CRLF/CR ⇒ LF; drops control characters except `\n` and `\t`; trims; false when longer than `MaxLength` after normalising.
  - `readonly record struct ReportId(string Value)` with `TryCreate` through `UlidText.IsValid`.
  - `sealed class FeedbackOptions { const string SectionName = "App:Feedback"; int ReportsPerDay { get; init; } = 10; }` (designer's call: 10 new reports per user per rolling 24 h; must be positive or startup fails).
  - `enum FeedbackOutcome { Saved, QuestionNotFound }`; `sealed record RatingResult(FeedbackOutcome Outcome, QuestionRating? Rating)`; `enum ReportOutcome { Created, Updated, QuestionNotFound, LimitReached }`; `sealed record ReportResult(ReportOutcome Outcome, DateTimeOffset? RetryAt)`.
  - `interface IQuestionFeedbackService { Task<RatingResult> SetRatingAsync(UserId user, QuestionId question, QuestionRating? rating, CancellationToken ct); Task<ReportResult> ReportAsync(UserId user, QuestionId question, CultureKey culture, ReportReason reason, string? comment, CancellationToken ct); Task<bool> ResolveReportAsync(ReportId report, UserId admin, CancellationToken ct); }`: `rating: null` clears. Retired or unknown question ⇒ `QuestionNotFound`. `comment` must already be normalised (`ReportComment`).
  - `interface IQuestionFeedbackQueries { Task<IReadOnlyDictionary<QuestionId, QuestionRating>> GetMyRatingsAsync(UserId user, IReadOnlyCollection<QuestionId> questions, CancellationToken ct); Task<ReportPage> GetReportsAsync(bool resolved, int page, CancellationToken ct); Task<IReadOnlyList<RatingTotals>> GetMostDislikedAsync(int count, CancellationToken ct); }`; `sealed record ReportRow(ReportId Id, QuestionId QuestionId, string Stack, string Slug, string Title, CultureKey Culture, ReportReason Reason, string? Comment, long ReporterTelegramId, string? ReporterName, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt)`; `sealed record ReportPage(IReadOnlyList<ReportRow> Rows, int Page, int PageCount)` (25 per page, newest first); `sealed record RatingTotals(QuestionId QuestionId, string Stack, string Slug, string Title, int Likes, int Dislikes)` (only questions with at least one dislike, ordered by dislikes desc).
  - Tables: `question_ratings(user_id char(26), question_id char(26), value smallint, updated_at timestamptz; pk(user_id, question_id); index(question_id))`; `question_reports(id char(26) pk, user_id, question_id, culture text, reason smallint, comment text null, created_at, resolved_at null, resolved_by char(26) null; index(user_id, created_at); index(resolved_at, created_at desc))`; foreign keys to `users` and `questions`.
- Rules: rating is an upsert (`INSERT ... ON CONFLICT (user_id, question_id) DO UPDATE`, interpolated `ExecuteSql`), so a double click stores one row. A report takes `pg_advisory_xact_lock(hashtextextended('feedback:' || user_id, 0))` (same pattern as `AnswerRecorder`), then: an open report by the same user for the same question and reason ⇒ update its comment and culture (`Updated`, does not count against the quota); else count the user's reports with `created_at > now - 24 h` (server `TimeProvider`); `>= ReportsPerDay` ⇒ `LimitReached` with `RetryAt` = oldest counted `created_at + 24 h`; else insert (`Created`). Comments are never logged.
- ADR 0016 (designer's call, no spec text changes): storage and quota above; feedback is not queued in the outbox (a failed write shows an error and can be retried; offline rating is not worth a second sync protocol); controls appear on the question page (Task 5) and in Practice and Review after answering (Task 4), not in Mock, whose summary links to the question pages; retired questions get no controls.

- [ ] **Step 1: Write failing tests**

```csharp
[Theory] [InlineData("like", QuestionRating.Like)] [InlineData("dislike", QuestionRating.Dislike)]
public void RatingKeys_Parse(string key, QuestionRating expected) { Assert.True(QuestionRatingKeys.TryParse(key, out QuestionRating r)); Assert.Equal(expected, r); }
[Fact] public void ReasonKeys_AreTheFixedList() => Assert.Equal(["wrong-answer", "unclear-wording", "outdated", "broken-link", "other"], ReportReasonKeys.All.Select(ReportReasonKeys.ToKey));
[Fact] public void Comment_StripsControlCharsKeepsNewlines() { Assert.True(ReportComment.TryNormalise(" a\r\nb\u0007 ", out string? c)); Assert.Equal("a\nb", c); }
[Fact] public void Comment_TooLong_Rejected() => Assert.False(ReportComment.TryNormalise(new string('x', 1001), out _));
[Fact] public void Comment_Blank_IsNull() { Assert.True(ReportComment.TryNormalise("  \n ", out string? c)); Assert.Null(c); }
// Infrastructure (own database per test)
[Fact] public async Task SetRating_ChangeAndClear_KeepsOneRow() { /* like -> dislike -> null => 0 rows; like twice concurrently => 1 row */ }
[Fact] public async Task SetRating_RetiredQuestion_IsNotFound() { }
[Fact] public async Task Report_SameReasonWhileOpen_UpdatesInsteadOfCreating() { }
[Fact] public async Task Report_EleventhWithin24h_LimitReachedWithRetryAt() { /* FakeTimeProvider; RetryAt = first + 24 h */ }
[Fact] public async Task Report_ConcurrentAtLimit_NeverExceedsQuota() { /* 9 stored, 5 parallel => exactly 1 Created */ }
[Fact] public async Task Report_AfterResolve_SameReasonCreatesNew() { }
[Fact] public async Task MostDisliked_OrdersByDislikesAndSkipsUndisliked() { }
[Fact] public async Task GetMyRatings_ReturnsOnlyRequestedIdsOfThatUser() { }
```

`PersistedEnumValuesTests`: pin `QuestionRating` (-1, 1) and `ReportReason` (1-5).

- [ ] **Step 2:** Run `dotnet test tests/Trainer.Domain.Tests` and `tests/Trainer.Infrastructure.Tests` → FAIL. **Step 3:** Implement; add the migration with `dotnet ef migrations add QuestionFeedback --project src/Trainer.Infrastructure`; register services; read and validate `FeedbackOptions` at startup; write ADR 0016, the data-model rows and `App:Feedback:ReportsPerDay` in the configuration table. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(feedback): store question ratings and rate-limited reports`.

### Task 4: Feedback API and island controls

**Files:**
- Create: `src/Trainer.Contracts/FeedbackContracts.cs`; `src/Trainer.Web/Api/FeedbackEndpoints.cs`; `src/Trainer.Web.Client/Components/QuestionFeedbackBar.razor`
- Modify: `src/Trainer.Contracts/SessionContracts.cs` (`SessionDto` gains `IReadOnlyDictionary<string, string>? MyRatings`), `ApiContracts.cs` (`ApiErrorCodes.QuestionNotFound = "question-not-found"`, `ReportLimit = "report-limit"`), `TrainerJsonContext.cs`, `src/Trainer.Web/Api/ApiEndpoints.cs`, `src/Trainer.Infrastructure/Sessions/SessionQueries.cs` (fill `MyRatings` for signed-in Practice and Review sessions), `src/Trainer.Web.Client/Api/TrainerApiClient.cs`, `Islands/PracticeIsland.razor`, `Islands/ReviewIsland.razor`, `Localization/ClientResource.{ru,en}.resx`, `wwwroot/css/islands.css`, `docs/architecture.md` (API table, "Question sessions and island UI")
- Test: `tests/Trainer.Web.Tests/FeedbackApiTests.cs`; `tests/Trainer.Web.Client.Tests/QuestionFeedbackBarTests.cs`; `tests/Trainer.Web.E2E/FeedbackIslandTests.cs`

**Interfaces:**
- Consumes: `IQuestionFeedbackService`, `IQuestionFeedbackQueries` (Task 3); `ApiEndpoints.AsWrite()`, `OwnerCheckFilter`, `TrainerApiClient.SendWriteAsync` (Plan 2).
- Produces:
  - `sealed record RatingRequest(string Value)`; `sealed record RatingDto(string? Value)`; `sealed record ReportRequest(string Culture, string Reason, string? Comment)`; `sealed record ReportResponse(string Outcome)` (`created` | `updated`).
  - `PUT /api/questions/{id}/rating` (200 `RatingDto`), `DELETE /api/questions/{id}/rating` (200 `RatingDto(null)`), `POST /api/questions/{id}/reports` (201 `created`, 200 `updated`, 429 `report-limit` with `Retry-After` in seconds until `RetryAt`, 404 `question-not-found`, 400 `validation` for an unknown key, a bad id, an unsupported culture or a comment over 1000 characters). All three are `.AsWrite()` (auth, write rate limit, antiforgery header, owner header).
  - `TrainerApiClient`: `Task<ApiResult<RatingDto>> SetRatingAsync(long owner, string questionId, string? value, CancellationToken ct)` (null ⇒ DELETE); `Task<ApiResult<ReportResponse>> ReportAsync(long owner, string questionId, ReportRequest request, CancellationToken ct)`.
  - `QuestionFeedbackBar` parameters: `QuestionDto Question`, `string? InitialRating`, `long Owner`, `string Culture`. Like and Dislike are `aria-pressed` toggle buttons (pressing the active one clears); "Report a problem" opens an inline `<details>` with five radio reasons (labels from resources), an optional `<textarea maxlength="1000">` and Send. States: idle, saving (buttons disabled), saved (status text), failed (retry text), limit (localised "You can send more reports later"). Rendered by Practice and Review below `FeedbackPanel` only when `Me is not null` and the attempt is done.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public async Task Rating_Anonymous_Returns401() { }
[Fact] public async Task Rating_WithoutAntiforgeryHeader_Returns400() { }
[Fact] public async Task Rating_OwnerHeaderOfOtherUser_Returns409AndStoresNothing() { }
[Fact] public async Task Rating_PutThenDelete_RoundTrips() { }
[Fact] public async Task Report_OverLimit_Returns429WithRetryAfter() { /* App:Feedback:ReportsPerDay = 1 in this host */ }
[Theory] [InlineData("like", "spam")] [InlineData("xx", "other")]
public async Task Report_UnknownReasonOrCulture_Returns400(string culture, string reason) { }
[Fact] public async Task PracticeSession_SignedIn_CarriesMyRatings_AnonymousCarriesNull() { }
// client
[Fact] public void Bar_PressingActiveRating_SendsClear() { }
[Fact] public void Bar_Limit_ShowsLaterText_NotError() { }
// E2E, Category=E2E, host.AssertNoCspViolations(tab) in each
[Fact] public async Task Practice_SignedIn_RatesAndReportsAfterAnswering() { }
[Fact] public async Task Practice_Anonymous_ShowsNoFeedbackControls() { }
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement; strings in RU and EN; no inline styles. **Step 4:** Run `dotnet test tests/Trainer.Web.Tests`, `tests/Trainer.Web.Client.Tests`, then the E2E command → PASS.
- [ ] **Step 5:** Commit `feat(feedback): rate and report questions from the islands`.

### Task 5: Feedback on the question page and on the admin page

**Files:**
- Create: `src/Trainer.Web/Components/Shared/QuestionFeedbackForms.razor`
- Modify: `src/Trainer.Web/Components/Pages/QuestionPage.razor`, `Components/Pages/Admin.razor`, `Localization/Resources/SharedResource.{ru,en}.resx`, `wwwroot/css/site.css`, `docs/architecture.md` ("Page rendering rules", "Question feedback", admin)
- Test: `tests/Trainer.Web.Tests/QuestionFeedbackPageTests.cs`, `AdminPageTests.cs`; `tests/Trainer.Web.E2E/FeedbackPageTests.cs`

**Interfaces:**
- Consumes: `IQuestionFeedbackService`, `IQuestionFeedbackQueries` (Task 3); the signed-in question-page branch that already loads statuses (Plan 2 Task 9); the admin page's POST form pattern (`@formname`, `<AntiforgeryToken />`, `ReadFormAsync`).
- Produces:
  - `QuestionFeedbackForms` parameters `QuestionId QuestionId`, `QuestionRating? Current`, `string ReturnPath`. Rendered in a `<section id="feedback">` only when the request is signed in and the question is not retired. Two static SSR POST forms (no JavaScript): `question-rating` (submit buttons `name="value"` with `like`, `dislike`, `clear`; the active one shows as pressed) and `question-report` inside `<details>` (radio `reason`, `<textarea name="comment" maxlength="1000">`). The handler calls the service and redirects (PRG) to `ReturnPath + "?feedback=<code>#feedback"` with codes `rated`, `cleared`, `reported`, `report-updated`, `report-limit`, `invalid`; the page shows the matching localised notice when signed in. `feedback` is not in the output-cache key allow-list and is ignored for anonymous requests.
  - Admin page sections: "Reports" (open by default, `?reports=resolved` shows resolved; 25 per page with `reportsPage`; columns date (formatted as the users table formats dates), question link in the report's culture, reason, comment HTML-encoded with `white-space: pre-wrap`, reporter name and Telegram id; a `resolve-{id}` POST form per open row) and "Most disliked" (top 20 `RatingTotals`).

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public async Task QuestionPage_Anonymous_HasNoFeedbackFormNoTokenNoSetCookie_AndIsCached() { }
[Fact] public async Task QuestionPage_SignedIn_RendersFormsWithAntiforgeryAndCurrentRating() { }
[Fact] public async Task QuestionPage_SignedIn_RetiredQuestion_HasNoFeedbackForms() { }
[Fact] public async Task RatingPost_Redirects302ToFeedbackAnchor_AndStores() { }
[Fact] public async Task RatingPost_WithoutAntiforgeryToken_Returns400() { }
[Fact] public async Task RatingPost_Anonymous_DoesNotStore() { }
[Fact] public async Task ReportPost_OverLimit_RedirectsWithReportLimitNotice() { }
[Fact] public async Task AnonymousPageAfterSignedInPost_StillServedFromCacheWithoutForms() { }
[Fact] public async Task Admin_ListsOpenReportsEncodesCommentAndResolves() { /* comment "<script>x</script>" appears encoded */ }
[Fact] public async Task Admin_NonAdminCannotResolve() { }
// E2E: FeedbackPage_SignedIn_ReportFormUsableAt375px (no horizontal overflow, 44 px targets)
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run `dotnet test tests/Trainer.Web.Tests` and E2E → PASS.
- [ ] **Step 5:** Commit `feat(feedback): rate and report from the question page, list reports for admins`.

### Task 6: Open Graph images

**Files:**
- Create: `src/Trainer.Web/OpenGraph/OgCardRenderer.cs`, `OgCard.cs`, `OgPalette.cs`, `OgFingerprint.cs`, `OgImageCache.cs`, `OgImageEndpoints.cs`, `OpenGraphOptions.cs`, `Fonts/Inter-SemiBold.ttf`, `Fonts/JetBrainsMono-Bold.ttf`, `Fonts/OFL.txt`; `src/Trainer.Web/Caching/CompositeContentCacheInvalidator.cs`; `docs/adr/0017-open-graph-images.md`
- Modify: `Directory.Packages.props` (`SkiaSharp`, `SkiaSharp.NativeAssets.Linux.NoDependencies`), `src/Trainer.Web/Trainer.Web.csproj` (fonts as embedded resources), `Program.cs`, `Components/Seo/SeoHead.razor`, `Components/Pages/QuestionPage.razor` and the other public pages (pass the image), `tests/Trainer.Web.Tests/DocsTests.cs` (P6: extend the expected set of `Architecture_ConfigurationTableMatchesTheOptionClasses` with this task's keys, `App:OpenGraph:CacheDirectory`), `docs/architecture.md` (SEO, configuration)
- Test: `tests/Trainer.Web.Tests/OgCardRendererTests.cs`, `OgImageEndpointTests.cs`, `SeoTests.cs`

**Interfaces:**
- Consumes: `IQuestionQueries` (title, level, topic title, retired flag), `SiteUrls`, `IContentCacheInvalidator` (Plan 1).
- Produces:
  - `sealed record OgCard(string Culture, string Title, string? LevelKey, string? TopicTitle, string Footer)`; `sealed class OgCardRenderer { byte[] RenderPng(OgCard card); }`: 1200×630 PNG, dark palette from `tokens.css` (`OgPalette` constants), monospace header `~/dotnet/<topic>`, level chip in the level colour, title in Inter SemiBold 60 px wrapped on spaces (a word wider than the line breaks by characters) to at most 4 lines with an ellipsis, footer with the localised site name and the public host. Deterministic: same card ⇒ same bytes. Fonts load once from embedded resources (`SKTypeface.FromStream`).
  - `static class OgFingerprint { const int Version = 1; string Of(OgCard card); }`: first 16 hex characters of SHA-256 over `Version`, culture and every card field, joined with `\n`.
  - `sealed class OgImageCache(IOptions<OpenGraphOptions>) : IContentCacheInvalidator`: files `<dir>/<culture>-<stack>-<slug>-<fingerprint>.png`, written to a temp name and moved; at most 2 renders at a time (`SemaphoreSlim`); `InvalidateAsync` deletes the directory contents. `OpenGraphOptions { const string SectionName = "App:OpenGraph"; string? CacheDirectory }` (default `Path.Combine(Path.GetTempPath(), "dit-og")`; Compose sets `/var/cache/dit/og`).
  - `CompositeContentCacheInvalidator(OutputCacheInvalidator, OgImageCache)` becomes the registered `IContentCacheInvalidator`; runs both and rethrows an `AggregateException` if any failed.
  - Routes: `GET /og/{lang}/{stackKey}/{slug}/{fingerprint}.png` and `GET /og/{lang}/site/{fingerprint}.png` (route values deliberately not named `culture` or `stack`, which `CultureAndStackMiddleware` would judge and turn into the HTML not-found page, P11) (site card: title = localised site tagline, used by home, stack, catalog, topic and about pages). 200 `image/png`, `Cache-Control: public, max-age=31536000, immutable`, no cookies; a stale fingerprint ⇒ 301 to the current URL with `Cache-Control: public, max-age=3600`; unknown slug, missing locale, retired question or unsupported culture ⇒ 404 with an empty body (status code pages disabled for these responses, as in Task 1).
  - `SeoHead` gains `OgImageUrl` (absolute, from `SiteUrls`) and renders `og:image`, `og:image:width` 1200, `og:image:height` 630, `og:image:alt` (the title), `twitter:card` `summary_large_image`.
- ADR 0017 (designer's call): SkiaSharp (MIT; ImageSharp excluded by licence), lazily rendered PNGs cached on a volume with fingerprinted URLs so crawlers get immutable responses and a changed title gets a new URL; the cache is cleared after each committed import.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Render_Is1200x630Png() { byte[] png = R.RenderPng(Card("en")); using SKBitmap b = SKBitmap.Decode(png); Assert.Equal((1200, 630), (b.Width, b.Height)); }
[Fact] public void Render_IsDeterministic() => Assert.Equal(R.RenderPng(Card("ru")), R.RenderPng(Card("ru")));
[Fact] public void Render_RuAndEnDiffer() => Assert.NotEqual(R.RenderPng(Card("ru")), R.RenderPng(Card("en")));
[Fact] public void Render_400CharacterTitleWithoutSpaces_DoesNotThrowAndStaysInFourLines() { }
[Fact] public void Fonts_CoverEveryCharacterOfEveryRepositoryTitle() { /* read content/ through IContentReader in the test, every title char: typeface.ContainsGlyph */ }
[Fact] public void Palette_MatchesDarkThemeTokens() { /* parse tokens.css [data-theme="dark"] --bg, --fg, level colours */ }
[Fact] public async Task Endpoint_CurrentFingerprint_Returns200ImmutableNoCookie() { }
[Fact] public async Task Endpoint_StaleFingerprint_Redirects301ToCurrent() { }
[Fact] public async Task Endpoint_RetiredOrMissingLocale_Returns404() { }
[Fact] public async Task Endpoint_UnsupportedCulture_Returns404WithEmptyBody() { /* /og/de/dotnet/...: 404, no body, no text/html (P11) */ }
[Fact] public async Task Endpoint_AfterImportChangingTitle_NewFingerprintAndCacheCleared() { }
[Fact] public async Task QuestionPage_HasAbsoluteOgImageWithDimensionsAndTwitterCard() { }
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Add packages (verify MIT licence and that `NoDependencies` runs on the Ubuntu image used in Task 8); download the TTF fonts from the Inter and JetBrains Mono release archives (same versions as the `woff2` files in `wwwroot/fonts`), commit them with their OFL text; implement; write ADR 0017 and the configuration row. **Step 4:** Run `dotnet test tests/Trainer.Web.Tests` → PASS.
- [ ] **Step 5:** Commit `feat(seo): generate cacheable Open Graph images per question and language`.

### Task 7: Cookieless analytics (application side)

**Files:**
- Create: `src/Trainer.Web/Analytics/AnalyticsOptions.cs`, `src/Trainer.Web/Components/Shared/AnalyticsScript.razor`; `docs/adr/0018-cookieless-analytics.md`
- Modify: `src/Trainer.Web/Components/App.razor`, `Program.cs`, `Components/Pages/About.razor` + resources (privacy paragraph in RU and EN: no cookies, page views counted without personal identifiers, feedback stored with the account), `tests/Trainer.Web.Tests/DocsTests.cs` (P6: extend the expected set of `Architecture_ConfigurationTableMatchesTheOptionClasses` with this task's keys, `App:Analytics:WebsiteId`), `docs/architecture.md` (security headers, configuration)
- Test: `tests/Trainer.Web.Tests/AnalyticsScriptTests.cs`

**Interfaces:**
- Produces: `sealed class AnalyticsOptions { const string SectionName = "App:Analytics"; string? WebsiteId { get; init; } }` (a GUID; empty ⇒ no script anywhere; an invalid GUID fails startup). `AnalyticsScript` renders, in `<head>` of every page except `/{culture}/admin` and `/{culture}/signin`, `<script defer src="/stats/script.js" data-website-id="{id}" data-host-url="{PublicBaseUrl}/stats" data-do-not-track="true" data-exclude-search="true" data-exclude-hash="true"></script>`. No CSP change: the script and its `POST /stats/api/send` are same-origin (`script-src 'self'`, `default-src 'self'`); nginx proxies exactly those two paths to Umami (Task 10). `data-host-url` is required because Umami's tracker posts to `<host>/api/send` and the app owns `/api`.
- Designer's call (one line, ADR 0018): **Umami**, because it runs on the PostgreSQL server already in the stack, while Plausible Community Edition needs ClickHouse, which roughly doubles the VPS memory. The dashboard is never proxied publicly; the owner reaches it through `ssh -L 3001:127.0.0.1:3001`. Verify the tracker attribute names against the pinned Umami release's tracker documentation when implementing.

- [ ] **Step 1:** Tests: `NoWebsiteId_RendersNoScript`; `PublicPage_RendersScriptWithExcludeSearchAndHostUrl`; `AnonymousPageWithScript_StillNoSetCookieAndCached`; `AdminAndSignIn_HaveNoScript`; `IslandPage_ScriptHasNoNonceAndCspUnchanged`; `InvalidWebsiteId_FailsStartup`.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement; write ADR 0018 and the About text. **Step 4:** Run `dotnet test tests/Trainer.Web.Tests` and the E2E suite (CSP violations) → PASS.
- [ ] **Step 5:** Commit `feat(web): add optional cookieless Umami page-view tracking`.

### Task 8: Docker image and migration bundle

**Files:**
- Create: `Dockerfile`, `.dockerignore`, `src/Trainer.Infrastructure/Persistence/ContractMigrationAttribute.cs`
- Modify: `AGENTS.md` (migrations rule), `docs/architecture.md` ("Migrations policy")
- Test: `tests/Trainer.Infrastructure.Tests/MigrationSafetyTests.cs`

**Interfaces:**
- Produces:
  - Image layout: `/app` (published `Trainer.Web`), `/app/migrate/efbundle` (framework-dependent EF Core bundle for `linux-x64`; it reads `TRAINER_DB` through `TrainerDbContextFactory`, so the connection string is never an argument), user `$APP_UID` (non-root, built into the .NET images), `EXPOSE 8080 8081`, `ENTRYPOINT ["dotnet", "/app/Trainer.Web.dll"]`, `curl` installed for the Compose health check. Before `USER $APP_UID`: `RUN install -d -o "$APP_UID" -g "$APP_UID" -m 0750 /var/cache/dit/og`; Docker copies a mount point's ownership into a new empty named volume, so the `ogcache` volume is writable by the app (C19/F9).
  - Build stage `mcr.microsoft.com/dotnet/sdk:10.0.303@sha256:<digest>`: install `python3` (emscripten in `wasm-tools` needs it), `dotnet workload install wasm-tools --version 10.0.303`, restore from the `*.csproj`/props layer, `dotnet publish src/Trainer.Web -c Release -o /out -p:InformationalVersion=$APP_VERSION -p:IncludeSourceRevisionInInformationalVersion=false`, `dotnet tool restore`, `dotnet ef migrations bundle --project src/Trainer.Infrastructure -r linux-x64 -o /out-migrate/efbundle`. A `RUN` check fails the build when the Brotli `dotnet.native.*.wasm.br` under `/out/wwwroot/_framework` is larger than 600 000 bytes (relinked ≈ 468 KB, prebuilt ≈ 977 KB; ADR 0013).
  - Final stage `mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:<digest>` (designer's call: not chiseled, because the health check needs `curl` and SkiaSharp needs the glibc user-space libraries).
  - `ARG APP_VERSION` (the git SHA); `.dockerignore` excludes `content/`, `tests/`, `docs/`, `**/bin`, `**/obj`, `.git`, `artifacts/`.
  - `[AttributeUsage(AttributeTargets.Class)] sealed class ContractMigrationAttribute(string reason) : Attribute`: marks a deliberate contract step. It only lifts the static check below; the runtime check (Task 11 `schema-compat.yml`: the deployed release's own `Release` suite on the migrated database) still runs for every deploy, so a contract step can ship only once the deployed release no longer uses what it removes.
  - `MigrationSafetyTests.NewMigrations_AreExpandOnly`: for every migration whose id is later than `20261115000000` and that lacks `[ContractMigration]`, `UpOperations` contain none of: `DropTableOperation`, `DropColumnOperation`, `RenameTableOperation`, `RenameColumnOperation`, `DropIndexOperation` on an existing table; an `AlterColumnOperation` that makes a column non-nullable, narrows its type or length, or removes a default; an `AddColumnOperation` with `IsNullable = false` and neither `DefaultValue` nor `DefaultValueSql` (the previous image's `INSERT` would fail); a `CreateIndexOperation` with `IsUnique = true`, an `AddForeignKeyOperation`, `AddCheckConstraintOperation` or `AddUniqueConstraintOperation` on a table that existed before the migration (determined from the previous migration's target model); any `SqlOperation` (raw SQL is allowed only with `[ContractMigration]`, so a reviewer reads it).
- AGENTS.md rule: "A migration must keep the previous release working (expand, then contract in a later release with `[ContractMigration]` after the release that stopped using the structure is deployed): a failed deploy rolls the image back after migrations ran, and every deploy runs the previous image against the migrated schema."

- [ ] **Step 1:** Write `MigrationSafetyTests` with one synthetic migration per forbidden case above (each must be reported, naming the migration and the operation) and one synthetic expand-only migration (new table, nullable column, non-unique index: not reported) → run → the Task 3 migration passes, every synthetic forbidden case is caught.
- [ ] **Step 2:** Write the Dockerfile and `.dockerignore`. **Step 3:** `docker build --build-arg APP_VERSION=local -t dit:local .` → succeeds, including the relink size check; `docker run --rm --entrypoint id dit:local -u` prints a non-zero uid; `docker run --rm --entrypoint stat dit:local -c %u /var/cache/dit/og` prints the same uid; `docker run --rm --entrypoint /app/migrate/efbundle dit:local --help` prints the bundle usage.
- [ ] **Step 4:** Commit `build: add production image with relinked WebAssembly runtime and migration bundle`.

### Task 9: Compose project, database roles and `.env` rendering

**Files:**
- Create: `deploy/compose/compose.yaml`, `deploy/compose/postgres/pg_hba.conf`, `deploy/remote/lib.sh`, `deploy/remote/ensure-databases.sh`, `deploy/scripts/render-env.sh`, `deploy/tests/render-env.test.sh`; `tests/Trainer.Deploy.Tests/Trainer.Deploy.Tests.csproj` (references `Trainer.TestSupport` for `ContentWorkspace.RepositoryRoot()`, YamlDotNet), `ComposeFileTests.cs`, `ShellScriptTests.cs`; `docs/adr/0019-database-roles.md`
- Modify: `DevInterviewTrainer.slnx`, `docs/architecture.md` (new section "Deployment topology" added to the Contents list, P6; ADR link)

**Interfaces:**
- Produces:
  - `compose.yaml` (project name `dev-interview-trainer`, read with `--env-file .env`): network `default` with fixed subnet `172.30.0.0/24`. Services:
    - `postgres`: `postgres:17-alpine@sha256:<same digest as ci.yml>`, `POSTGRES_PASSWORD: ${DB_PASSWORD}` (only used by the image's first init; network logins as `postgres` are rejected by `pg_hba.conf`), `command: ["postgres", "-c", "hba_file=/etc/postgresql/pg_hba.conf"]`, volume `pgdata`, the directory `${APP_DIR}/postgres` mounted read-only at `/etc/postgresql` (a directory, not the file or a path through the `current-bundle` symlink: a bind mount keeps the inode or symlink target it saw at container start; `dit_apply_hba <bundle-dir>` in `lib.sh` copies that bundle's `pg_hba.conf` there by temp file and rename and runs `SELECT pg_reload_conf()` when it changed; `deploy-app.sh` calls it on activation and again on rollback with the last good bundle, so the rules always belong to the running deployment, Q4), no `ports`, health check `pg_isready -U postgres`.
    - `app`: `${APP_IMAGE}`, `depends_on: postgres (service_healthy)`, ports `127.0.0.1:8080:8080` and `127.0.0.1:8081:8081`, `read_only: true`, `tmpfs: /tmp`, `cap_drop: [ALL]`, `security_opt: [no-new-privileges:true]`, `mem_limit: 768m`, volumes `${APP_DIR}/content:/content:ro`, `${APP_DIR}/flags:/flags:ro` (`App__MaintenanceFlagPath=/flags/maintenance`) and `ogcache:/var/cache/dit/og`, health check `curl -fsS http://127.0.0.1:8080/health/ready` (interval 10 s, start period 120 s). Environment: `ASPNETCORE_ENVIRONMENT=Production`, `Kestrel__Endpoints__Public__Url=http://+:8080`, `Kestrel__Endpoints__Internal__Url=http://+:8081`, `App__Internal__Port=8081`, `App__ContentRoot=/content/current`, `App__OpenGraph__CacheDirectory=/var/cache/dit/og`, `App__PublicBaseUrl`, `App__AdminTelegramIds`, `App__Analytics__WebsiteId`, `App__RateLimits__*`, `App__Feedback__ReportsPerDay`, `App__ForwardedHeaders__KnownProxies__0=172.30.0.1` (the gateway the userland proxy connects from), `TelegramOidc__ClientId`, `TelegramOidc__ClientSecret`, `ContentImport__Token`, `ConnectionStrings__Trainer` and `TRAINER_DB` (both `Host=postgres;Database=trainer;Username=trainer;Password=${DB_PASSWORD}`).
    - `umami`: the latest stable Umami PostgreSQL image pinned by digest, port `127.0.0.1:3001:3000`, `DATABASE_URL=postgresql://umami:${UMAMI_DB_PASSWORD}@postgres:5432/umami`, `APP_SECRET=${UMAMI_APP_SECRET}`, `DISABLE_TELEMETRY=1`, `cap_drop: [ALL]`, `no-new-privileges`, `mem_limit: 512m`.
    - Every service: `logging: json-file` with `max-size: 10m`, `max-file: 3`; `restart: unless-stopped`.
  - `pg_hba.conf`: `local all postgres peer`; `host trainer trainer 172.30.0.0/24 scram-sha-256`; `host umami umami 172.30.0.0/24 scram-sha-256`; nothing else.
  - Server layout used by every remote script: `$APP_DIR/bundles/<sha>/` (an unpacked `deploy/` bundle: `compose/`, `remote/`, `nginx/`, `systemd/`), `$APP_DIR/staging/<run-id>/` (uploads of one workflow run), `$APP_DIR/current-bundle -> bundles/<sha>` and `$APP_DIR/compose.yaml -> current-bundle/compose/compose.yaml` (both symlinks, switched only by `deploy-app.sh` under the lock), `$APP_DIR/last-good.env` (the last deployment that passed readiness, one file, Task 11), `$APP_DIR/postgres/pg_hba.conf` (copied from the active bundle, mounted as a directory).
  - `lib.sh` (sourced by every remote script from its own directory): `APP_DIR` (default `/srv/dev-interview-trainer`), `dit_compose` (wraps `docker compose --project-directory "$APP_DIR" -f "$APP_DIR/compose.yaml" --env-file "$APP_DIR/.env"`), `dit_psql DB` and `dit_pg_dump DB` (both `dit_compose exec -T --user postgres postgres ...`: the image's default exec user is root, and `local all postgres peer` requires the operating-system user `postgres`; `-U postgres` alone does not change that identity, C9/F10), `dit_lock` (`exec {fd}>"$APP_DIR/.deploy.lock"; flock -w 900 "$fd"`; exports `DIT_LOCKED=1` so nested scripts do not lock again), `dit_env_get FILE NAME` (reads one value from an env file without `source`), `dit_env_set FILE NAME` (replaces one line from stdin into a temp copy and renames it, mode 600), `dit_log` (timestamped stderr line, never values).
  - `ensure-databases.sh`: idempotent; for `trainer` and `umami` creates the role (`LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE`) with the password from `.env` **only when the role does not exist** (spec 8.7.2: passwords change only through `rotate-db-password`), and the database owned by it; passwords reach psql through `dit_compose exec -T --user postgres -e PW postgres psql -v ON_ERROR_STOP=1` with `\getenv pw PW`.
  - `render-env.sh`: reads environment variables and prints `.env` lines `NAME='value'`. Required: `APP_IMAGE`, `APP_DIR`, `PUBLIC_BASE_URL` (https), `ADMIN_TELEGRAM_IDS` (comma-separated positive integers), `TELEGRAM_OIDC_CLIENT_ID`, `TELEGRAM_OIDC_CLIENT_SECRET`, `DB_PASSWORD`, `CONTENT_IMPORT_TOKEN` (≥ 32), `UMAMI_DB_PASSWORD`, `UMAMI_APP_SECRET`, and the backup group `BACKUP_S3_PROVIDER`, `BACKUP_S3_ENDPOINT`, `BACKUP_S3_BUCKET`, `BACKUP_S3_ACCESS_KEY_ID`, `BACKUP_S3_SECRET_ACCESS_KEY` (required: a backup that lives only on the VPS is lost with it, C18). Optional: `UMAMI_WEBSITE_ID`, `RATE_LIMIT_WRITES_PER_MINUTE`, `RATE_LIMIT_READS_PER_MINUTE`, `RATE_LIMIT_SEARCH_PER_MINUTE`, `FEEDBACK_REPORTS_PER_DAY`. Exits 2 naming the first missing or invalid variable (never its value); rejects values containing `'`, a newline or a NUL.
- ADR 0019 (designer's call): least-privilege roles and `pg_hba`, superuser reachable only through `docker compose exec --user postgres` on the server.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void EveryPublishedPort_IsBoundToLoopback() { /* every services.*.ports entry starts with "127.0.0.1:" */ }
[Fact] public void Postgres_PublishesNoPort() { }
[Fact] public void Images_ArePinnedByDigest_ExceptAppImage() { }
[Fact] public void App_IsReadOnlyDropsCapabilitiesAndForbidsNewPrivileges() { }
[Fact] public void App_MountsContentParentReadOnly() => Assert.Contains("${APP_DIR}/content:/content:ro", AppVolumes());
[Fact] public void App_HealthCheckUsesReadiness() { }
[Fact] public void App_TrustsOnlyTheNetworkGateway() { /* KnownProxies__0 = gateway of the fixed subnet */ }
[Fact] public void EveryService_HasBoundedJsonLogs() { }
[Fact] public void PgHba_AllowsOnlyAppRolesOnTheComposeSubnet() { }
[Fact] public void PostgresAdminCommands_RunAsPostgresUser() { /* every "exec" of the postgres service in deploy/**/*.sh goes through dit_psql/dit_pg_dump or has "--user postgres" */ }
[Fact] public void NoScriptEnablesXtrace() { /* deploy/**/*.sh: no "set -x", no "set -o xtrace" */ }
[Fact] public void EveryScript_UsesStrictMode() { /* "set -euo pipefail" in every deploy/**/*.sh */ }
```

`render-env.test.sh` (plain bash asserts): full set renders every key quoted; a missing `DB_PASSWORD` exits 2 and the output names `DB_PASSWORD` without printing any value; a value with `'` is rejected; a missing backup variable is rejected; `ADMIN_TELEGRAM_IDS=12,abc` is rejected; `PUBLIC_BASE_URL=http://x` is rejected.

- [ ] **Step 2:** Run `dotnet test tests/Trainer.Deploy.Tests` and `bash deploy/tests/render-env.test.sh` → FAIL. **Step 3:** Implement; `shellcheck deploy/**/*.sh` clean; write ADR 0019. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(deploy): add compose project, database roles and env rendering`.

### Task 10: nginx reverse proxy

**Files:**
- Create: `deploy/nginx/nginx.conf.template`, `deploy/nginx/dev-interview-trainer.conf.template`, `deploy/nginx/dev-interview-trainer.http.conf.template`, `deploy/tests/nginx-check.sh`; `tests/Trainer.Deploy.Tests/NginxConfigTests.cs`
- Modify: `docs/architecture.md` ("Behind the reverse proxy")

**Interfaces:**
- Produces three templates with the placeholders `${DOMAIN}` and `${APP_DIR}` (filled by `envsubst '${DOMAIN} ${APP_DIR}'` in bootstrap). The target is the nginx that Ubuntu 24.04 ships (1.24): the `http2` directive appeared only in nginx 1.25.1 (nginx.org, `ngx_http_v2_module`), so the TLS server uses `listen 443 ssl http2;` (C10).
  - `nginx.conf.template`, installed as `/etc/nginx/nginx.conf` (R10): the Ubuntu default with two changes in the `http` block, `access_log off;` and `error_log /dev/null;`. Every HTTP server, including the default server that rejects other host names and the HTTP-only bootstrap server, therefore writes no error log and no access log unless it opts in; only the site's own `access_log` below is turned back on. Removing request-bearing error output entirely is simpler and safer than filtering it: nginx error lines at any level can carry the client address and the request line. Startup and configuration errors still reach the main-context error log and the journal (`nginx -t`, `systemctl status nginx`); the app's logs, `ops-check` and readiness cover runtime failures (designer's call).
  - `dev-interview-trainer.http.conf.template` (used by bootstrap before a certificate exists, F14): port 80 only, serves `/.well-known/acme-challenge/` from `/var/www/certbot`, everything else 404.
  - `dev-interview-trainer.conf.template` (the full site):
    - `log_format dit '[$time_local] "$request_method $uri $server_protocol" $status $body_bytes_sent $request_time';` and `access_log /var/log/nginx/dev-interview-trainer.access.log dit;`: no client address (Global Constraints) and no query string (`$uri`, ADR 0007) (C17, F14). No `error_log` in the site: it inherits `/dev/null` from `nginx.conf`.
    - Maintenance: in `location /` and `location = /stats/api/send`, `if (-f ${APP_DIR}/flags/maintenance) { return 503; }` (Task 12 restore, R2).
    - Port 80: `/.well-known/acme-challenge/` from `/var/www/certbot`, everything else `return 301 https://$host$request_uri;` (the target is not logged; only `$uri` reaches the access log).
    - Port 443 (`listen 443 ssl http2;`): certificate paths under `/etc/letsencrypt/live/${DOMAIN}/`, TLS 1.2 and 1.3 only, `add_header Strict-Transport-Security "max-age=31536000" always;` (no preload: designer's call, reversible), `client_max_body_size 1m`, `server_tokens off`.
    - `location ^~ /internal/ { return 404; }`.
    - `limit_req_zone $binary_remote_addr zone=dit_stats:1m rate=10r/s;` (memory only, never logged). `location = /stats/script.js` → `proxy_pass http://127.0.0.1:3001/script.js`; `location = /stats/api/send` → `limit_req zone=dit_stats burst=20 nodelay; client_max_body_size 16k; proxy_set_header X-Forwarded-For $remote_addr; proxy_pass http://127.0.0.1:3001/api/send;` (Umami derives the visitor hash from the forwarded address, F11); any other `/stats/` ⇒ 404.
    - `location /` → `proxy_pass http://127.0.0.1:8080; gzip off; proxy_set_header Host $host; proxy_set_header X-Forwarded-For $remote_addr; proxy_set_header X-Forwarded-Proto $scheme; proxy_http_version 1.1; proxy_set_header Connection "";` (`$remote_addr`, not `$proxy_add_x_forwarded_for`: the app trusts one hop and a client-supplied chain is dropped).
    - A `server` block with `default_server` on 80 and 443 (`ssl_reject_handshake on`, available since 1.19.4) so other host names never reach the app.
  - `limit_req_log_level` is not set: nginx.org lists only `info | notice | warn | error` (default `error`), so the earlier `crit` would fail `nginx -t` (R9); rejections are not logged anyway because the error log is `/dev/null`.
  - `nginx-check.sh`: in an `ubuntu:24.04` container, `apt-get install nginx` (the package production gets), render all three templates with a dummy domain and a self-signed certificate, `nginx -t`; start nginx with an unreachable upstream and send, each carrying `SENTINEL` in the query: a request to the site (upstream failure), a request with a foreign `Host` to the default server (rejected host), an oversized body to `/stats/api/send`, requests over the rate limit, and a request while `${APP_DIR}/flags/maintenance` exists (expects 503). Then assert that no file under `/var/log/nginx` contains `SENTINEL` or `127.0.0.1` (R10). Run by `deploy-smoke.yml` (Task 11).

- [ ] **Step 1:** Tests `NginxConfigTests` (inputs: all three templates, P10): `ProxiedLocation_HasGzipOff`; `ForwardedHeaders_OverwriteClientValues` (also on `/stats/api/send`); `AccessLog_NeverLogsQueryStringsOrAddresses` (no `$request`, `$args`, `$query_string`, `$request_uri`, `$remote_addr`, `$http_x_forwarded_for` in `log_format`); `HttpErrorAndAccessLogsOffByDefault`; `NoLimitReqLogLevel`; `MaintenanceFile_Returns503`; `Http2_UsesListenParameterFor124`; `Internal_Returns404`; `Stats_ProxiesExactlyTwoPathsAndLimitsSend`; `OnlyDomainAndAppDirPlaceholders`; `Hsts_IsSet`; `HttpOnlyTemplate_ServesOnlyAcmeChallenge`.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Write the templates and `nginx-check.sh`; run `bash deploy/tests/nginx-check.sh` → passes on the Ubuntu 24.04 package. **Step 4:** Run `dotnet test tests/Trainer.Deploy.Tests` → PASS.
- [ ] **Step 5:** Commit `feat(deploy): add nginx reverse proxy templates`.

### Task 11: Remote deploy script with rollback, the Compose smoke and the schema-compatibility check

**Files:**
- Create: `deploy/remote/deploy-app.sh`, `deploy/tests/smoke.sh`, `deploy/tests/compose.smoke.yaml`, `deploy/tests/content/` (copy of `tests/Trainer.Web.Tests/TestContent/web`), `.github/workflows/deploy-smoke.yml`, `.github/workflows/schema-compat.yml`; `docs/adr/0020-deploy-serialisation.md`
- Modify: `.github/workflows/ci.yml` (job `schema-compat`), `tests/Trainer.Web.Release/*` (re-runnable on a non-empty database), `docs/architecture.md` (ADR link, CI job list)

**Interfaces:**
- Consumes: `lib.sh`, `ensure-databases.sh`, `compose.yaml` (Task 9); image (Task 8); `/internal/*` (Task 1); `nginx-check.sh` (Task 10); the `Release` suite (Plan 2).
- Produces:
  - Upload protocol (every workflow, C4/F3): the runner creates `$APP_DIR/staging/<run-id>-<attempt>/` and streams into it only: the `deploy/` bundle (`bundle.tgz`) and, for `deploy-app`, `.env.new` (mode 600). Nothing outside the run's staging directory is written before the lock. The run unpacks `bundle.tgz` into `<staging>/bundle` and then executes `bash <staging>/bundle/remote/<script>` (P1). Staging directories older than 2 days are removed by `deploy-app.sh` under the lock.
  - Guard files that make every state-changing script refuse to start (exit 3) and print the `docs/deployment.md` section to follow: `$APP_DIR/flags/maintenance` (a restore is in progress or was interrupted, Task 12) and `$APP_DIR/.rotation-pending` (a password rotation is in progress or was interrupted, Task 15), `$APP_DIR/.umami-recovery` (a failed deployment changed the Umami image; below, Q3) and `$APP_DIR/.import-pending` (a content import has no definitive outcome yet, Task 14, Q5; only `receive-content.sh` refuses on it, and it resolves it itself). Refusing, not auto-repairing, is the recovery model (designer's call): on a single-owner VPS a documented manual step is safer than reconciliation code that guesses.
  - Deployment record (R4): the active `.env` carries two extra lines appended at activation, `DIT_BUNDLE_SHA` and `DIT_COMMIT` (`APP_IMAGE` is already there). The last state that passed readiness is one file, `$APP_DIR/last-good.env`, published by writing a temp file and `mv` (a single atomic rename: environment, image, bundle and commit can never come from different deployments).
  - `deploy-app.sh <staging-dir> <image-ref> <commit-sha>` (run as `deploy`, started with `setsid` so an SSH drop does not send SIGHUP; traps `TERM INT HUP` to the rollback path, F3):
    1. `dit_lock`; refuse when a guard file exists; `mkdir -p "$APP_DIR/flags" "$APP_DIR/postgres"` (mode 755) so Docker never creates them root-owned (P8).
    2. Validate `.env.new` (required keys non-empty); refuse with exit 2 when `DB_PASSWORD` or `UMAMI_DB_PASSWORD` in `.env.new` differs from the current `.env` (a configuration rendered with a stale or hand-edited database password would break the connection; passwords change only through `rotate-db-password`, C3).
    3. When `last-good.env` exists: `backup.sh predeploy` (Task 12; until Task 12 lands a missing script is skipped with a log line); a failure stops here with nothing changed. Without `last-good.env` the deployment is a first install and the backup is skipped (C5). The decision is made on `last-good.env`, never on whether a database exists, so a server whose database was renamed away by an interrupted restore is never treated as new (R2).
    4. Activate: `cp -a <staging>/bundle bundles/<sha>` (reused when identical), write `.env` from `.env.new` plus `DIT_BUNDLE_SHA`/`DIT_COMMIT` (temp file, rename), switch `current-bundle` with `ln -sfn bundles/<sha> current-bundle.new && mv -T current-bundle.new current-bundle`; `dit_apply_hba current-bundle`.
    5. `dit_compose pull`; `dit_compose up -d --wait postgres`; `ensure-databases.sh`; the bundle `dit_compose run --rm --no-deps --entrypoint /app/migrate/efbundle app`; `dit_compose up -d --wait --wait-timeout 180`; `curl -fsS http://127.0.0.1:8080/health/ready`.
    6. Success: publish `last-good.env` (copy of `.env`, temp file, rename); keep `bundles/` of the current and the last 2 good deployments; remove images of this repository other than the current and previous ones.
    7. Failure at any point after step 4 (or a trapped signal): copy `last-good.env` to `.env`, switch `current-bundle` to its `DIT_BUNDLE_SHA`, `dit_apply_hba current-bundle` (the failed deployment's authentication rules must not outlive it, Q4), `dit_compose up -d --wait --wait-timeout 180`, log `rolled back to <APP_IMAGE of last-good.env>`, exit 1. Umami is the exception (R12): when the failed deployment changed the Umami image, the rollback writes `$APP_DIR/.umami-recovery` (naming the pre-deploy `umami` dump of this run) **before** bringing the stack back, starts every service except `umami`, and prints the "Umami upgrade" recovery steps from `docs/deployment.md`. The guard is persistent (Q3): `deploy-app.sh` refuses (exit 3) while it exists, so no later `compose up` can start the older Umami image on a database the newer one may have migrated. The recovery is `restore.sh --replace <that umami dump> umami`, which is the one operation allowed while `.umami-recovery` exists: by then the rolled-back bundle selects the old image, so the restore swaps in the pre-upgrade database first and only then starts `umami` with that old image, and it removes the guard on success. With no `last-good.env` (a failed first install): `dit_compose stop app umami`, keep PostgreSQL and its data, log `first install incomplete; fix and re-run deploy-app`, exit 1.
  - `smoke.sh` (needs Docker, bash, curl, `age`; runs in a temporary `APP_DIR`; `IMAGE` env or builds `dit:smoke`): generates throw-away secrets, renders `.env` with `render-env.sh` (a local MinIO container stands in for the S3 bucket), uploads through the same staging protocol, layers `compose.smoke.yaml` (adds the MinIO service), then runs named checks, each printing `ok <name>` or failing the script:
    - `first_deploy_without_content_is_ready` (Review Focus 2)
    - `failed_first_install_can_be_retried`
    - `internal_endpoints_only_on_internal_port` (8081 `/internal/version` 200; 8080 404 with an empty body)
    - `content_switch_is_seen_without_restart` (Review Focus 1)
    - `forwarded_proto_is_trusted_from_gateway` (`curl -H 'X-Forwarded-Proto: https' 127.0.0.1:8080/lang/...` ⇒ `Set-Cookie` with `Secure`)
    - `anonymous_page_is_cookieless_and_cached`
    - `og_image_renders_in_container` (C19/F9)
    - `failed_readiness_rolls_back_to_last_good` (second deploy whose `.env.new` is edited after rendering to `PUBLIC_BASE_URL='http://bad'`, F14, with a changed `compose.yaml` in its bundle ⇒ exit 1, containers match `last-good.env` and its bundle) (Review Focus 3, F3)
    - `killed_deploy_then_failed_deploy_rolls_back_to_last_good` (TERM after step 4 ⇒ trap rolls back; a following failing deploy rolls back to the same state) (F3)
    - `killed_between_record_steps_never_mixes_deployments` (KILL at random points during steps 4-6 over 20 iterations; afterwards `.env`, `current-bundle` and `last-good.env` always describe one deployment or the rollback restores one) (R4)
    - `umami_image_change_is_not_rolled_back_automatically` (R12; then a further deploy exits 3 until `restore.sh --replace` of the named dump succeeds, after which Umami runs the old image on the restored database, Q3)
    - `failed_deploy_changing_hba_restores_previous_rules` (the failing deployment's bundle changes `pg_hba.conf` so the app role cannot connect; after rollback the rules are the last good ones and the app is ready, Q4)
    - `concurrent_uploads_and_deploys_serialise` (Review Focus 4, C4)
    - `guard_files_block_deploys` (`flags/maintenance` or `.rotation-pending` present ⇒ exit 3, nothing changed)
    - `stale_password_config_is_refused`
    - `image_runs_as_non_root_with_relinked_runtime`
  - `schema-compat.yml` (`on: workflow_call` with inputs `previous` and `current`, both commit SHAs; `permissions: contents: read`; PostgreSQL service as in the CI `release` job) proves that the previous release still reads and writes on the new schema (C6, R7), using the previous commit's own `Release` suite, which signs in through the fake provider and exercises users, answers, review states, mock sessions, profile reads and, once it exists in that commit, feedback: skip (success) when `git diff --quiet <previous> <current> -- src/Trainer.Infrastructure/Persistence/Migrations`; else check out both commits; migrate the database to `previous` (`dotnet ef database update` from `previous`); publish `previous` (same SDK pin and `wasm-tools` steps as the `release` job) and run its `Release` suite once (it leaves representative rows behind); apply `current`'s migrations; run the `previous` `Release` suite again against the migrated, non-empty database. The `Release` suite is made re-runnable on a non-empty database (unique ids per run, no assumption of empty tables) in this task, which also adds the marker file `tests/Trainer.Web.Release/RERUNNABLE`; when `previous` lacks that marker (only the first introduction, on this plan's own branch), the workflow skips the second run and logs `previous Release suite is not re-runnable; second run skipped` (P16).
  - `ci.yml` job `schema-compat` calls it with `previous` = `git merge-base origin/main HEAD` for pull requests and `github.event.before` for pushes to `main`, `current` = the tested SHA (job `permissions: contents: read`). Task 13 calls it with the commit actually running in production.
  - `deploy-smoke.yml`: `on: pull_request` with `paths: [deploy/**, Dockerfile, .dockerignore, src/**, .github/workflows/deploy-*.yml, .github/workflows/deploy-smoke.yml]`, `workflow_call` (input `image`, optional), `workflow_dispatch`; top-level `permissions: contents: read, packages: read` (declared statically: `permissions` takes no expressions, F7); job `checks` (shellcheck on `deploy/**/*.sh`, `render-env.test.sh`, `nginx-check.sh`, `Invoke-Pester deploy/bootstrap/tests` once Task 16 exists, guarded by `hashFiles`), job `smoke` (logs in to GHCR with `docker login --password-stdin` from `github.token` passed through `env:`, only when `image` is given; never `secrets.GITHUB_TOKEN`, so `PullRequestWorkflows_ReferenceNoSecrets` holds without an exemption, P14), runs `smoke.sh`. A caller must grant the same permissions on its calling job; a called workflow cannot raise them (GitHub docs, "Reusing workflow configurations": permissions "can be only downgraded (not elevated) by the called workflow").
- ADR 0020 (**needs owner approval**, see "Owner decisions needed"): deploy serialisation and ordering. Reviewers disagreed (F5: one workflow-level group per workflow; C15: a stale build can still deploy over a newer one, and a combined push can deliver content before its app). Decision: (1) each deploy workflow declares a **workflow-level** concurrency group (`production-app`, `production-content`, `production-rotate`, `production-ops`, `cancel-in-progress: false`), so within one workflow the build, smoke and deploy of an older run finish before a newer run starts, and GitHub's replacement of a pending run only ever drops an older run of the same workflow; (2) every remote state change takes the server `flock`, which keeps different workflows from overlapping; (3) automatic deploys refuse to go backwards: the deploy job compares its commit with `DIT_COMMIT` of `last-good.env` (app) or the `current` release SHA (content) and skips with a notice when its commit is a strict ancestor; an explicit rollback input is the only override (Tasks 13 and 14); (4) `deploy-app` runs after a successful `ci` run of a `push` to `main` in this repository (`workflow_run`, provenance checked, R1) instead of on the push itself, so nothing reaches production without the unit, integration, E2E, Release and schema-compatibility checks (F14), and after a successful deploy it dispatches `deploy-content` for the same commit when the live content release is behind that commit's `content/` tree (C15). This changes spec 8.7.2's "shared concurrency group" and the `deploy-app` trigger.

- [ ] **Step 1:** Write `smoke.sh` with all checks → run `bash deploy/tests/smoke.sh` → FAIL at `deploy-app.sh` missing.
- [ ] **Step 2:** Implement `deploy-app.sh` (shellcheck clean). **Step 3:** Run `bash deploy/tests/smoke.sh` locally (Linux or WSL with Docker) → every check `ok`.
- [ ] **Step 4:** Add `schema-compat.yml` and the `ci.yml` job; make the `Release` suite re-runnable; prove the check fails on a branch that adds a migration making an existing `answers` column `NOT NULL` without a default (the previous suite's answer writes fail), then drop that branch.
- [ ] **Step 5:** Add `deploy-smoke.yml`; push a branch; both workflows pass.
- [ ] **Step 6:** Commit `feat(deploy): deploy with migrations, readiness wait, rollback and schema-compatibility checks`.

### Task 12: Encrypted off-site backups, restore and the daily ops check

**Files:**
- Create: `deploy/remote/backup.sh`, `restore.sh`, `ops-check.sh`; `deploy/systemd/dit-backup.service`, `dit-backup.timer`; `docs/adr/0021-encrypted-offsite-backups.md`
- Modify: `deploy/remote/deploy-app.sh` (pre-deploy backup is now required when `last-good.env` exists), `deploy/tests/smoke.sh`, `docs/adr/0004-data-protection-keys-in-plan-2.md` (a "Notes added in Plan 3" line pointing to ADR 0021), `docs/architecture.md`

**Interfaces:**
- Produces:
  - `backup.sh daily|predeploy`: takes `dit_lock` unless `DIT_LOCKED=1` (called by `deploy-app.sh`), so a deploy cannot restart PostgreSQL or replace configuration in the middle of a dump (C18). Refuses (exit 4) when `$APP_DIR/backup/recipients.txt` is missing. For each database in `trainer umami`: `dit_pg_dump <db> -Fc | age -R "$APP_DIR/backup/recipients.txt" > <file>.partial` with `pipefail`, then `mv` to `$APP_DIR/backups/<db>-<UTC yyyyMMddTHHmmssZ>-<kind>.dump.age`, plus `<same name>.meta` (plain text, not secret), database-specific (R11): `trainer` records `APP_IMAGE`, `DIT_COMMIT`, the last row of `__EFMigrationsHistory`, the active content hash and the `current` release SHA; `umami` records the Umami image reference only. Off-site: `rclone copy` of every local file not yet marked uploaded to `offsite:${BACKUP_S3_BUCKET}/`, rclone configured only through `RCLONE_CONFIG_OFFSITE_*` environment variables read from `.env`; on success each file gets a `.uploaded` marker and `$APP_DIR/backups/last-offsite` is updated. Retention: 14 `daily` and 5 `predeploy` files per database, but a file without its `.uploaded` marker is never deleted (C18). A `daily` run fails when the dump or the upload fails; a `predeploy` run fails only when the dump fails (F12). `$APP_DIR/backups/last-success` holds the UTC time of the last run whose dumps succeeded.
  - `restore.sh <file.dump.age> <db>` (scratch) and `restore.sh --replace <file.dump.age> <db>` (the age identity is read from stdin, `age -d -i /dev/stdin`; C1/F2/R2):
    1. Preflight: refuse when a guard file exists (except `.umami-recovery` for `--replace ... umami`, Q3; `.import-pending` is ignored, because the restore's startup import re-reads `current` and settles it, P15), when a database named `<db>_restore_*` exists, or (for `--replace`) when `<db>` does not exist.
    2. Restore into `<db>_restore_<ts>` with `pg_restore --no-owner --role=<role> --exit-on-error`; on any error drop it and exit 1. The live database is untouched.
    3. Validate: every table listed by `pg_restore -l` exists with row counts printed; for `trainer`, `__EFMigrationsHistory` exists and `data_protection_keys` is not empty.
    4. Scratch mode stops here and prints the database name.
    5. `--replace`, under `dit_lock`: write `$APP_DIR/flags/maintenance` containing the three database names (live, `<db>_old_<ts>`, `<db>_restore_<ts>`); nginx answers 503 for new requests while the file exists (Task 10), and the app's maintenance gate rejects every write that nginx had already admitted (Task 2), so no public write reaches either database until the end (R2, Q1); stop the writers (`app` for `trainer`, `umami` for `umami`), which also ends every request still in flight against the old database; `ALTER DATABASE <db> RENAME TO <db>_old_<ts>`; `ALTER DATABASE <db>_restore_<ts> RENAME TO <db>`; for `trainer` run the migration bundle (a nightly dump is older than a deploy made later that day, F2); start the writers with `up -d --wait` (the startup import brings the database in line with the release on disk, C8); check readiness and the row counts again; remove `flags/maintenance` (for `umami`, also `.umami-recovery`).
    6. Any failure or interruption in step 5: `flags/maintenance` stays, so the site keeps answering 503 and every deploy, content delivery and rotation refuses; the script stops the writers and prints the "Interrupted restore" section of `docs/deployment.md`, which gives the exact `ALTER DATABASE ... RENAME` commands for each of the three states the names in `flags/maintenance` can be in (only the old name present, both swapped, migration failed), and then `rm flags/maintenance`. No automatic rename back: nothing has written to either database since step 5 began, so the operator's choice loses nothing. The old database is kept until `restore.sh --drop-old <db>_old_<ts>`.
  - `ops-check.sh`: prints one `ok|fail <check> <detail>` line per check and exits 1 if any failed: `readiness`, `backup-identity` (`recipients.txt` present), `backup-age` (`last-success` younger than 26 h), `offsite-age` (`last-offsite` younger than 26 h), `disk` (root filesystem below 85 %), `certificate` (`openssl s_client -connect 127.0.0.1:443 -servername "$DOMAIN"` end date more than 14 days ahead), `restarts` (no container restarted more than 3 times since the last deploy), `guards` (none of `flags/maintenance`, `.rotation-pending`, `.umami-recovery`, `.import-pending`; when present, names the documented recovery, P15).
  - `dit-backup.timer`: daily `03:30 UTC`, `Persistent=true`, `RandomizedDelaySec=10m`; the service runs `$APP_DIR/current-bundle/remote/backup.sh daily` as `deploy` with `ConditionPathExists=$APP_DIR/current-bundle/remote/backup.sh`, so bootstrap can enable it before the first deploy (F12).
- ADR 0021 (**off-site provider needs owner approval**): backups encrypted with `age` to a recipient whose identity is generated on the admin's PC by bootstrap and confirmed before the recipient is installed (Task 16); off-site copy to an S3-compatible bucket is mandatory, with an access key that can list and write but not delete (bucket lifecycle rules expire old objects, F12); the Data Protection key ring is inside the `trainer` dump (ADR 0004); a restore into the live database runs under site-wide maintenance and stops for the operator on any failure.

- [ ] **Step 1:** Extend `smoke.sh` → run → FAIL:
  - `backup_is_encrypted_and_contains_key_ring` (the file does not start with `PGDMP`; decrypted `pg_restore -l` lists `TABLE DATA public data_protection_keys`)
  - `both_dumps_have_database_specific_meta_and_restore` (R11: `trainer` and `umami` dumps, both `.meta` files with their own keys, both restore in scratch mode)
  - `backup_without_recipient_fails`
  - `restore_with_wrong_identity_leaves_live_database_untouched` (C1)
  - `restore_into_scratch_db_matches_row_counts`
  - `restore_replace_keeps_key_ring_and_runs_migrations`
  - `restore_admitted_slow_post_is_rejected_after_restart` (Q1: a POST whose body is still being sent when the flag is created reaches the restored app and gets 503; row counts unchanged)
    - `restore_replace_failure_keeps_maintenance_and_blocks_deploys` (a migration failure injected after the swap: `flags/maintenance` stays, `deploy-app.sh` exits 3, both databases exist, nothing written to either) (R2)
  - `rebuild_empty_server_from_older_backup` (C8/F13)
  - `offsite_outage_keeps_unuploaded_files_and_predeploy_succeeds`
  - `retention_keeps_14_daily`, `ops_check_flags_stale_backup_and_guards`
- [ ] **Step 2:** Implement the scripts and units; `deploy-app.sh` now aborts before touching the stack when the pre-deploy dump fails. **Step 3:** `bash deploy/tests/smoke.sh` → all `ok`; shellcheck clean.
- [ ] **Step 4:** Commit `feat(deploy): add encrypted off-site backups, guarded restore and daily ops check`.

### Task 13: `deploy-app.yml` and workflow hardening checks

**Files:**
- Create: `.github/actions/ssh-setup/action.yml`, `.github/workflows/deploy-app.yml`, `.github/workflows/ops-check.yml`, `.github/dependabot.yml`; `tests/Trainer.Deploy.Tests/WorkflowHardeningTests.cs`
- Modify: `docs/architecture.md` ("Tests and CI": workflow list)

**Interfaces:**
- Consumes: `render-env.sh` (Task 9), `deploy-app.sh`, the upload protocol, `deploy-smoke.yml` and `schema-compat.yml` (Task 11), `ops-check.sh` (Task 12).
- Produces:
  - `ssh-setup` composite action, inputs `ssh-key`, `known-hosts`, `host`, `user`: writes both to `$RUNNER_TEMP/ssh/` under `umask 077` from `env:` (never `${{ }}` inside `run:`), writes an `ssh_config` with `StrictHostKeyChecking yes`, `BatchMode yes`, `IdentitiesOnly yes`, `UserKnownHostsFile`, host alias `dit`; every calling job ends with an `if: always()` step `rm -rf "$RUNNER_TEMP/ssh" "$RUNNER_TEMP/env"`.
  - `deploy-app.yml`: `on: workflow_run: { workflows: [ci], types: [completed], branches: [main] }` and `workflow_dispatch` with inputs `ref` (a commit SHA on `main`, default empty) and `rollback` (boolean, default false); workflow-level `concurrency: { group: production-app, cancel-in-progress: false }`; top-level `permissions: {}`. Jobs:
    1. `resolve` (`contents: read`, `fetch-depth: 0`). Automatic path (R1): proceed only when, read through `env:`, `workflow_run.conclusion == 'success'`, `workflow_run.event == 'push'`, `workflow_run.head_branch == 'main'`, `workflow_run.head_repository.full_name == github.repository` and `git merge-base --is-ancestor "$SHA" origin/main` for `SHA = workflow_run.head_sha`. The `branches` filter alone matches a head branch name, which a fork's pull request can also call `main`; GitHub warns that `workflow_run` runs with secrets "even if the previous workflow was not" privileged. Manual path: the `ref` input through `env:`, validated against `^[0-9a-f]{40}$` and the same ancestry check. Output `sha`.
    2. `prepare` (`needs: resolve`, `environment: production`, `contents: read`): `ssh-setup`; read `DIT_COMMIT` and `APP_IMAGE` from `last-good.env` with `grep -E '^(DIT_COMMIT|APP_IMAGE)='` (non-secret lines only; R6: the server's record is the only reliable identity of what runs). Outputs `deployed_commit` (empty on the first deploy) and `deploy` = false only when (a) the trigger is `workflow_run` and `git diff --name-only <deployed_commit> <sha>` is non-empty and touches only `content/` (spec 8.7.2), or (b) `rollback` is false and `sha` is a strict ancestor of `deployed_commit` (stale guard, C15). A manual dispatch with `sha == deployed_commit` always deploys, so "edit a setting in GitHub, re-run deploy-app" re-renders `.env` (P2).
    3. `build` (`needs: prepare`, only when `deploy`, `contents: read`, `packages: write`): when the tag does not exist yet, build at `sha` with `APP_VERSION=<sha>` and push `ghcr.io/<lowercased owner>/dev-interview-trainer:<sha>`; output the digest.
    4. `compat` (`needs: prepare`, only when `deploy` and `deployed_commit` is not empty, `uses: ./.github/workflows/schema-compat.yml` with `previous: deployed_commit`, `current: sha`; job `permissions: contents: read`): the release that is actually in production must keep working on the new schema (R6, R7).
    5. `smoke` (`needs: build`, `uses: ./.github/workflows/deploy-smoke.yml` with `image` = the digest ref; job `permissions: contents: read, packages: read`, C13/F7).
    6. `deploy` (`needs: [prepare, smoke, compat]`, `if: ${{ !cancelled() && needs.prepare.outputs.deploy == 'true' && needs.smoke.result == 'success' && (needs.compat.result == 'success' || needs.compat.result == 'skipped') }}`, because GitHub otherwise skips a job whose `needs` job was skipped and the first deploy (no `compat`) would never run (P3), `environment: production`, `permissions: contents: read, packages: read`; Task 14 adds `actions: write`): checkout the workflow's own commit for the trusted `deploy/` scripts; `ssh-setup`; repeat the stale guard against `last-good.env` (cheap, closes the gap since `prepare`); render `.env` with `render-env.sh` (values via `env:` from `secrets.*`/`vars.*`, `APP_IMAGE` = digest ref) into `$RUNNER_TEMP/env`; upload bundle and `.env.new` into `staging/<run_id>-<run_attempt>/`; registry login on the server through stdin into a per-run `DOCKER_CONFIG` inside the staging directory; run `setsid bash <staging>/bundle/remote/deploy-app.sh <staging> <digest ref> <sha>`; `always()` cleanup.
  - `ops-check.yml`: `schedule: cron "0 7 * * *"` + `workflow_dispatch`, workflow-level concurrency `production-ops`, job `environment: production`, `permissions: contents: read`, uses `ssh-setup`, uploads the bundle into staging and runs `ops-check.sh` (Task 12) from there; a failure is the owner's daily alert.
  - `dependabot.yml`: weekly updates for `github-actions` (keeps the SHA pins current), `nuget`, `docker` (Dockerfile and `deploy/compose`), with `ignore` of `version-update:semver-major` for `postgres` (a new major needs a dump and restore) and of every update for the Umami image (pinned exact version; upgrades follow the manual "Umami upgrade" procedure in `docs/deployment.md`, R12).
  - `WorkflowHardeningTests` (YAML parsed with YamlDotNet over every `.github/workflows/*.yml` and `.github/actions/**/action.yml`):
    - `EveryRemoteAction_IsPinnedByFullSha`;
    - `EveryWorkflow_DeclaresTopLevelPermissions_AndNeverWriteAll`;
    - `PackagesWrite_OnlyInTheImageBuildJob`;
    - `ReusableWorkflowCallers_GrantWhatTheCalledWorkflowDeclares` (C13/F7);
    - `NoWorkflow_UsesPullRequestTarget`;
    - `PullRequestWorkflows_ReferenceNoSecrets`;
    - `SecretsAppearOnlyInEnvBlocks`;
    - `InputsAppearOnlyInEnvBlocks` (no `${{ inputs.` or `${{ github.event.` inside `run:`, F14);
    - `RunScripts_NeverInterpolateSecretsIntoSshArguments`;
    - `DeployWorkflows_UseWorkflowLevelConcurrencyAndProductionEnvironment`;
    - `DeployApp_ChecksWorkflowRunProvenance` (the `resolve` job reads `conclusion`, `event`, `head_branch`, `head_repository.full_name` and runs the ancestry check, R1);
    - `DeployApp_ReadsDeployedCommitFromServer` (no `gh run list` lookup of previous deployments, R6);
    - `DeployApp_DeployJobRunsWhenCompatSkipped` (the `deploy` job's `if:` contains `!cancelled()`, `needs.prepare.outputs.deploy == 'true'`, `needs.smoke.result == 'success'` and accepts `needs.compat.result` `success` or `skipped`, P3);
    - `DeployApp_ManualSameCommitRedeploys` (the `prepare` content-only rule is limited to `workflow_run` and a non-empty diff, P2);
    - `DeployJobs_UploadOnlyIntoStaging`;
    - `DeployJobs_CleanUpSshMaterialAlways`.

- [ ] **Step 1:** Write `WorkflowHardeningTests` → run `dotnet test tests/Trainer.Deploy.Tests` → FAIL (workflow missing; existing files must already pass the pin and permission checks; fix any that do not).
- [ ] **Step 2:** Write the action, workflows and Dependabot config; resolve each action's SHA with `gh api repos/<owner>/<repo>/git/ref/tags/<tag>` (dereference annotated tags). **Step 3:** Run → PASS. `actionlint` on the workflows → clean.
- [ ] **Step 4:** Commit `ci: add deploy-app workflow and workflow hardening checks`.

### Task 14: Content delivery workflow

**Files:**
- Create: `deploy/remote/receive-content.sh`, `deploy/tests/import-proxy.py`, `.github/workflows/content-checks.yml`, `.github/workflows/deploy-content.yml`
- Modify: `.github/workflows/ci.yml` (the `content` job calls `content-checks.yml`; job name `content` unchanged), `.github/workflows/deploy-app.yml` (content follow-up below), `deploy/tests/smoke.sh`, `tests/Trainer.Deploy.Tests/WorkflowHardeningTests.cs`, `tests/Trainer.Web.Tests/DocsTests.cs` (P5: `WorkflowCommands` also reads `content-checks.yml`, so `Agents_TestCommandsMatchCi` still finds `validate` and the Docker-trait command after they move there), `docs/architecture.md` (CI jobs, content flow)

**Interfaces:**
- Consumes: `/internal/version`, `/internal/content/import` (Task 1: an import, once started, runs to its end even when the client disconnects, and a committed import answers 200); `lib.sh` and the upload protocol (Tasks 9, 11); `ssh-setup` (Task 13).
- Produces:
  - `content-checks.yml`: `on: workflow_call` with input `ref` (default empty = the calling commit); `permissions: contents: read`; one job that checks out `ref` and runs the current CI `content` steps; no secrets.
  - Import outcome rule (R5, replacing the earlier before/after hash comparison): the import endpoint is definitive. `Imported` and `Unchanged` (200) mean the release on disk is in the database; `Rejected` (422) never commits. A transport failure or a 5xx is **not** an answer: the script calls the endpoint once more. The second call waits on the importer's advisory lock until any in-flight import has finished, then imports the same `current` and answers definitively (200 or 422). Before the first call the script writes `$APP_DIR/.import-pending` (the release SHA) and removes it only after a definitive answer. A second non-answer ends the script with exit 6, `current` left on the new release and the guard in place (Q5): an import may still be reading `/content/current`, so nothing may switch it. The next `receive-content.sh` run, before extracting or switching anything, sees the guard and calls the import endpoint for the release `current` points to; that call queues behind any in-flight import on the advisory lock and its definitive answer settles the earlier delivery (200 clears the guard; 422 switches back as in step 7 and clears it); only then does the new delivery proceed. `deploy-app.sh` does not refuse on this guard: an app restart aborts an in-flight import (its transaction rolls back) and the startup import re-reads `current`, which is consistent.
  - `receive-content.sh <staging-dir> <sha>` (stdin: `tar.gz` whose top directory is `content/`):
    1. `dit_lock`; refuse when a guard file other than `.import-pending` exists; when `.import-pending` exists, settle it first as described in the import outcome rule.
    2. Extract into `<staging>/content` with `--strip-components=1`, refusing absolute paths, `..` entries and links; compute a manifest (`sha256sum` of every file, sorted).
    3. `releases/<sha>` already exists: reuse it when its stored `.manifest` equals the new one, else fail (immutable releases, C20). Otherwise move the tree to `releases/<sha>.tmp`, write `.manifest`, rename to `releases/<sha>`.
    4. Read `schema-version`; `GET http://127.0.0.1:8081/internal/version`; outside `[ContentSchemaMin, ContentSchemaMax]` ⇒ exit 5 before switching ("deploy-app will deliver this content after the app that supports it").
    5. Remember the old `current` target (none on a first delivery); switch `ln -sfn "releases/<sha>" current.new && mv -T current.new current`.
    6. `curl -sS -X POST -o "<staging>/import.json" -w '%{http_code}' -H @- http://127.0.0.1:8081/internal/content/import` (no `-f`, which would turn a 422 into exit 22 without a body; no client timeout; P4); 200 and 422 are definitive, any other code or a curl failure is a non-answer; the `Authorization` header line built from `dit_env_get .env CONTENT_IMPORT_TOKEN` and piped on stdin; apply the import outcome rule.
    7. 422: switch `current` back to the old target; with no old target (first delivery, R5) remove `current`, which returns the app to the fresh-install state of Task 2 (the database has no active import because a rejection never commits). Print the issues; exit 1.
    8. 200: keep the 5 newest releases plus whatever `current` points to.
  - Deterministic fault injection for the smoke (Q5): `DIT_TEST_IMPORT_URL` (honoured only when `DIT_TEST=1`) points import calls at `deploy/tests/import-proxy.py` (stdlib only), which forwards the request to `127.0.0.1:8081`, then polls `pg_locks` through `dit_psql` until the importer's advisory lock is held, and only then closes the client connection: the real import has started when the script sees the transport failure. A variant flag applies it to the first call only.
  - `deploy-content.yml`: `on: push: branches: [main], paths: ['content/**']` and `workflow_dispatch` with inputs `ref` (default empty) and `rollback` (boolean, default false; R8); workflow-level `concurrency: { group: production-content, cancel-in-progress: false }`; top-level `permissions: {}`. Jobs:
    1. `resolve` (`contents: read`): one immutable SHA (C16): the push SHA, or the input through `env:` validated against `^[0-9a-f]{40}$` and `git merge-base --is-ancestor <ref> origin/main`.
    2. `checks` (`needs: resolve`, `uses: ./.github/workflows/content-checks.yml` with `ref: sha`, job `permissions: contents: read`).
    3. `deploy` (`needs: checks`, `environment: production`, `permissions: contents: read`): checkout the **workflow's own commit** (trusted scripts), `fetch-depth: 0`; `ssh-setup`; stale guard: when `rollback` is false and `sha` is a strict ancestor of the live release SHA (`readlink "$APP_DIR/content/current"`), end with a notice. Only `rollback: true` overrides it, so an app follow-up that passes `ref` can never move content backwards (R8). Upload the bundle into staging; `git archive --format=tar.gz <sha> content | ssh dit "bash <staging>/bundle/remote/receive-content.sh <staging> <sha>"`; `always()` cleanup.
  - `deploy-app.yml` follow-up (C15): the `deploy` job gains `actions: write` and a last step: when `git rev-parse <live release sha>:content` differs from `git rev-parse $SHA:content`, `gh workflow run deploy-content.yml -f ref=$SHA` (without `rollback`) with `GITHUB_TOKEN`. `WorkflowHardeningTests.DeployApp_DispatchesContentFollowUpWithoutRollback` pins it.
- The freshness check runs on the runner, not under the server lock (R8, partly rejected): runs of `deploy-content` are serialised by its workflow-level concurrency group, and no other workflow switches `current`, so nothing can change the live release between the check and the delivery.

- [ ] **Step 1:** Extend `smoke.sh` → run → FAIL: `content_out_of_schema_range_is_refused_before_switch`, `rejected_import_switches_back`, `rejected_first_delivery_returns_to_fresh_state`, `lost_response_is_retried_and_decided_by_second_call` (proxy on the first call), `unresolved_import_blocks_switch_until_settled` (proxy on both calls: exit 6 and `.import-pending` kept; a second delivery of another release first settles the pending one, then switches; `current` never changes while the started import runs), `same_sha_with_different_content_is_refused`, `same_sha_reupload_is_reused_while_current`, `tar_with_parent_paths_is_refused`, `keeps_five_releases_and_current`.
- [ ] **Step 2:** Implement; refactor `ci.yml`. **Step 3:** `smoke.sh` all `ok`; `dotnet test tests/Trainer.Deploy.Tests` → PASS; the CI run on a branch shows the `content` job green through the reusable workflow.
- [ ] **Step 4:** Commit `ci: deliver immutable content releases with schema check and definitive import outcome`.

### Task 15: Database password rotation

**Files:**
- Create: `deploy/remote/rotate-db-password.sh`, `.github/workflows/rotate-db-password.yml`
- Modify: `deploy/tests/smoke.sh`, `docs/architecture.md` ("Tests and CI": describe `rotate-db-password.yml`, so `Architecture_ListsEveryCiJob` stays green, P7)

**Interfaces:**
- Produces (R3: roll-forward only; no revert, no reconciliation of which value is current):
  - `rotate-db-password.sh <staging-dir> <trainer|umami> begin|finish|commit`. Every mode takes `dit_lock`.
    - `begin` (stdin: the new password): refuse when any guard file exists; write `$APP_DIR/.rotation-pending` (role and the new password, mode 600, temp file and rename) **before** changing anything; then run `finish`.
    - `finish <role>`: read the role and new password from `.rotation-pending`; when the given role differs from the journal's, exit 2 "pending rotation is for <journal role>" before any change (Q2); `ALTER ROLE <role> PASSWORD :'pw'` through `dit_psql` with `\getenv` (idempotent); `dit_env_set` the one line (`DB_PASSWORD` or `UMAMI_DB_PASSWORD`) in `.env` and in `last-good.env`; `dit_compose up -d --wait` for the affected service; readiness (`trainer`) or Umami's `/api/heartbeat` must pass, else exit 1 with the journal kept. Every step sets the state to "new password everywhere on the server", so running it again after a crash at any point converges.
    - `commit <role>`: the same role check, then delete `.rotation-pending`. Only called after GitHub confirmed the new value.
    - `pending-role`: print the journal's role (no secret), for the workflow.
    - With `.rotation-pending` present, `deploy-app.sh` and `receive-content.sh` refuse (Task 11) and `ops-check` fails `guards`.
  - `rotate-db-password.yml`: `workflow_dispatch` with inputs `role` (`trainer` | `umami`) and `mode` (`rotate` | `finish`); workflow-level concurrency `production-rotate`; top-level `permissions: {}`; job `rotate` (`environment: production`, `permissions: contents: read, actions: write`):
    1. Token check before anything changes (C14/F6): with `GH_TOKEN` from `secrets.SECRETS_ADMIN_TOKEN` (fine-grained, this repository, **Environments: read and write**), `gh secret set DIT_TOKEN_CHECK --env production` then `gh secret delete DIT_TOKEN_CHECK --env production`; failure ends the run with nothing changed.
    2. `mode: rotate`: generate 48 hex characters with `openssl rand -hex 24` (never printed); upload the bundle into staging; `begin` with the value on stdin.
    3. `mode: finish` (after an interrupted run): upload the bundle; run `pending-role` and fail the run when it differs from the `role` input; run `finish <role>`, then read the pending password back over the SSH channel's stdout into a step-local variable (never printed; the value is already on the server in `.env`, so this is the same exposure as rendering it).
    4. Both modes: the secret name is derived from the verified role only (`trainer` ⇒ `DB_PASSWORD`, `umami` ⇒ `UMAMI_DB_PASSWORD`); `gh secret set <that name> --env production --repo "$GITHUB_REPOSITORY"` from a `umask 077` file, 3 attempts; only on success `commit`, then `gh workflow run deploy-app.yml -f ref=<current main SHA>` with `GITHUB_TOKEN`. An ambiguous or failed GitHub update keeps the journal (R3); deploys stay blocked until the owner runs `mode: finish`, which is safe to repeat.

- [ ] **Step 1:** Extend `smoke.sh` → run → FAIL: `rotate_begin_then_commit_keeps_app_ready`, `crash_after_alter_role_then_finish_converges` (kill `finish` right after `ALTER ROLE`, before `.env` changes: the app loses new connections, `deploy-app.sh` refuses, `finish` restores service with the new password everywhere; R3's case), `crash_before_alter_role_then_finish_converges`, `deploy_is_refused_while_rotation_pending`, `finish_with_other_role_fails_without_change` (journal for `trainer`, `finish umami` and `commit umami` exit 2, journal kept, nothing altered; Q2), `rotate_new_password_never_in_process_list`.
- [ ] **Step 2:** Implement. **Step 3:** `smoke.sh` all `ok`; hardening tests PASS.
- [ ] **Step 4:** Commit `feat(deploy): rotate database passwords through a roll-forward workflow`.

### Task 16: Bootstrap (first-time provisioning)

**Files:**
- Create: `deploy/bootstrap/Bootstrap.ps1`, `deploy/bootstrap/BootstrapLib.psm1`, `deploy/bootstrap/remote/provision.sh`, `deploy/bootstrap/tests/BootstrapLib.Tests.ps1`
- Modify: `.github/workflows/deploy-smoke.yml` (Pester step now has files)

**Interfaces:**
- Produces:
  - Prerequisites on the admin's PC: PowerShell 7.2+, OpenSSH client, `age` and `age-keygen` (winget `FiloSottile.age`, Homebrew or apt), optional `gh`.
  - `Bootstrap.ps1 -HostName <string> -AdminUser <string> -Domain <string> -AdminTelegramIds <long[]> -LetsEncryptEmail <string> [-SshPort <int>] [-TelegramClientId <string>] [-Repository <owner/name>] [-RotateDeployKey] [-RotateAgeIdentity] [-RotateSecret CONTENT_IMPORT_TOKEN|UMAMI_APP_SECRET]` (`#Requires -Version 7.2`). The OIDC client secret and the S3 access key pair are asked with `Read-Host -AsSecureString`, never parameters. Steps follow spec 8.7.3 1-7:
    1. Validate inputs (`Test-DomainName`, ids positive, e-mail shape); check tools; `gh auth status` if present.
    2. Create one temp directory (`New-TemporaryDirectory`, ACL restricted to the current user); everything below lives there; `try/finally` removes it on success, failure and Ctrl+C.
    3. Package the host assets into `host-assets.tgz` (`provision.sh`, `deploy/nginx/*.template`, `deploy/systemd/*`; C11; `pg_hba.conf` travels with the deploy bundle instead, Task 9) and upload it with a non-secret vars file (domain, e-mail, admin user, SSH port, app dir) by `scp` to `/tmp/dit-bootstrap-<random>/`.
    4. Deploy key, only when needed (C12): `provision.sh --has-deploy-key` (non-PTY, read-only) says whether a `dit-deploy-*` key is installed, and `gh secret list --env production` whether `DEPLOY_SSH_KEY` exists. Both present and no `-RotateDeployKey` ⇒ keep them. Otherwise generate a pair locally (`ssh-keygen -t ed25519 -N "" -C dit-deploy-<yyyyMMddTHHmmssZ>-<8 random hex>`; the comment is informational only) and pass the public key in the vars file.
    5. Phase 1, `ssh -t <admin>@<host> sudo bash /tmp/dit-bootstrap-<random>/provision.sh --base <vars>` (interactive sudo prompt visible; output is shown, never captured): packages, `deploy` user and its key, `/srv` layout, host assets, nginx HTTP-only site, certificate, full site, backup timer, unattended upgrades. sshd and the firewall are not touched yet.
    6. Reconnect check (C12/F4): a new non-interactive connection `ssh -o PasswordAuthentication=no -o BatchMode=yes <admin>@<host> -p <port> true` must succeed (the admin's key works), and when a deploy key was generated, `ssh -i <new key> deploy@<host> true` must succeed. Failure stops before hardening.
    7. Phase 2, `provision.sh --harden <vars>` (with `-t`): sshd drop-in `/etc/ssh/sshd_config.d/10-dit.conf` (`PasswordAuthentication no`, `KbdInteractiveAuthentication no`, `PermitRootLogin prohibit-password`, or `no` when the admin user is not root); the `10-` prefix sorts before cloud-init's `50-cloud-init.conf`, and sshd keeps the first value it reads (F4); `sshd -t` must pass, then `sshd -T` must report the three values, then `systemctl reload ssh`; `ufw` allows the SSH port taken from `$SSH_CONNECTION` (and `-SshPort` when given) before `ufw default deny incoming`, 80, 443, `ufw --force enable` (F4). Then one more reconnect check as in step 6; a failure prints the recovery instructions (provider console) and stops.
    8. Backup identity (C2/F1): `provision.sh --has-age-recipient` (non-PTY). Missing, or `-RotateAgeIdentity`: run `age-keygen` **locally** into the temp directory; show the identity once with "store it in your password manager now"; ask the owner to paste it back (`Read-Host -AsSecureString`); encrypt a random challenge to the recipient with `age -r` and decrypt it with the pasted identity; only on a match upload the recipient (public, non-secret) with `provision.sh --install-age-recipient`. Until then `backup.sh` refuses to run and `ops-check` fails `backup-identity`, so a lost identity can never silently protect backups. With `-RotateAgeIdentity` the old recipient is replaced and older backups stay readable only with the old identity (the docs say to keep both).
    9. Generate missing secrets locally with `New-RandomSecret -Length 40` (alphanumeric, `RandomNumberGenerator.GetInt32` per character, no modulo bias): `DB_PASSWORD`, `CONTENT_IMPORT_TOKEN`, `UMAMI_DB_PASSWORD`, `UMAMI_APP_SECRET`, **only for names that do not exist yet** in the environment, so a re-run never breaks a live database.
    10. With `gh`: create or update environment `production` with a custom deployment branch policy `main` (`gh api -X PUT repos/{repo}/environments/production` + `deployment-branch-policies`), show the list of names to be written and ask for confirmation, then `Set-GitHubValue` for each (including `DEPLOY_SSH_KEY` and `DEPLOY_KNOWN_HOSTS` only when step 4 generated a key or they are missing). After `DEPLOY_SSH_KEY` is set, `provision.sh --remove-deploy-keys-except <new public key file>` (non-PTY as admin, or as `deploy` with the new key) removes older automation keys. Without `gh`: print the checklist (each secret shown once) and the variables, and leave old keys in place.
    11. Print the BotFather redirect URI `https://<domain>/signin-telegram` and the summary from both provisioning phases.
  - `BootstrapLib.psm1` exported functions: `Test-DomainName([string]) -> bool`, `New-RandomSecret([int]$Length) -> string`, `Get-ProductionSettingNames -> @{ Secrets = string[]; Variables = string[] }` (the spec 8.7.3 lists plus `UMAMI_DB_PASSWORD`, `UMAMI_APP_SECRET`, `BACKUP_S3_ACCESS_KEY_ID`, `BACKUP_S3_SECRET_ACCESS_KEY`, `SECRETS_ADMIN_TOKEN` as secrets and `UMAMI_WEBSITE_ID`, `BACKUP_S3_PROVIDER`, `BACKUP_S3_ENDPOINT`, `BACKUP_S3_BUCKET`, `RATE_LIMIT_WRITES_PER_MINUTE`, `RATE_LIMIT_READS_PER_MINUTE`, `RATE_LIMIT_SEARCH_PER_MINUTE`, `FEEDBACK_REPORTS_PER_DAY` as variables, P13), `Get-MissingSecretNames([string[]]$Existing) -> string[]`, `Set-GitHubValue([ValidateSet('secret','variable')]$Kind, [string]$Name, [securestring]$Value, [string]$Repository)` (value written to `gh`'s stdin; never in the argument list), `ConvertTo-KnownHostsLine([string]$HostName, [int]$Port) -> string` (from the admin's own `known_hosts` via `ssh-keygen -F`, not `ssh-keyscan`, so the value is the key the admin already trusted), `Test-AgeIdentity([string]$Recipient, [securestring]$Identity) -> bool`, `Get-DeployKeyPlan([bool]$ServerHasKey, [bool]$GitHubHasKey, [bool]$Rotate) -> 'keep'|'create'`.
  - `-RotateDeployKey`: new key pair, `provision.sh --add-deploy-key` appends the public key with `restrict`, `ssh -i <new> deploy@host true` must succeed, `Set-GitHubValue secret DEPLOY_SSH_KEY`, then remove the older keys (the old key keeps working until the new one is proven and stored).
  - `-RotateSecret`: regenerate the named stateless secret, `Set-GitHubValue`, print "re-run deploy-app"; database passwords are refused with a pointer to `rotate-db-password.yml`.
  - `provision.sh --base|--harden <vars> | --has-deploy-key | --add-deploy-key <file> | --remove-deploy-keys-except <public-key-file> | --has-age-recipient | --install-age-recipient <file>` (root, idempotent, prints `changed <item>` / `unchanged <item>` per step and a final count). Deploy keys are identified by their key material, never by comment (R13): `--remove-deploy-keys-except` keeps exactly the `authorized_keys` lines whose key type and base64 blob equal the given public key's and that carry the `dit-deploy-` prefix, and removes every other `dit-deploy-` line; `--has-deploy-key` compares the blob too. There is no installed-version stamp and no drift check (R14, simplified): host assets change only through a bootstrap re-run, which is cheap and idempotent, and `AGENTS.md` and `docs/deployment.md` say that a change under `deploy/nginx`, `deploy/systemd` or `deploy/bootstrap` needs one.
    - `--base`: apt packages (Docker Engine and compose plugin from Docker's apt repository with its key fingerprint checked, nginx, certbot, ufw, unattended-upgrades, age, rclone, curl); unattended security upgrades enabled; `deploy` user in group `docker`, no sudo; `authorized_keys` line `restrict <key> dit-deploy-<date>` added when given; `/srv/dev-interview-trainer/{content/releases,backups,backup,bundles,staging,flags,postgres}` owned by `deploy` (`flags`, `postgres` and the app directory itself mode 755, so nginx can stat `flags/maintenance`; `last-good.env` is a file written by `deploy-app.sh`, not a directory; P8); nginx: install `nginx.conf`; when no certificate exists, install the HTTP-only template, `nginx -t && systemctl reload nginx`, `certbot certonly --webroot -w /var/www/certbot -d <domain> -m <email> --agree-tos --non-interactive`; then the full template, `nginx -t && systemctl reload nginx`; certbot deploy hook `systemctl reload nginx`; `dit-backup.service`/`.timer` copied from the assets and enabled at once (`ConditionPathExists` keeps it idle until the first deploy, F12). Re-running `--base` with a newer `host-assets.tgz` is how nginx and unit changes reach the host (C11).
    - `--harden`: as in step 7.

- [ ] **Step 1: Write failing Pester tests**

```powershell
Describe 'New-RandomSecret' { It 'returns 40 alphanumeric characters' { New-RandomSecret -Length 40 | Should -Match '^[A-Za-z0-9]{40}$' } }
Describe 'Get-MissingSecretNames' { It 'skips names that exist' { Get-MissingSecretNames -Existing @('DB_PASSWORD') | Should -Not -Contain 'DB_PASSWORD' } }
Describe 'Get-DeployKeyPlan' { It 'keeps an established key on a re-run' { Get-DeployKeyPlan $true $true $false | Should -Be 'keep' } ; It 'creates when either side is missing' { Get-DeployKeyPlan $true $false $false | Should -Be 'create' } }
Describe 'Set-GitHubValue' { It 'passes the value on stdin only' { Mock gh { $script:args = $args; $script:stdin = $input | Out-String } -ModuleName BootstrapLib; Set-GitHubValue secret DB_PASSWORD (ConvertTo-SecureString 's3cr3t' -AsPlainText -Force) 'o/r'; $script:args -join ' ' | Should -Not -Match 's3cr3t'; $script:stdin | Should -Match 's3cr3t' } }
Describe 'Remove deploy keys' { It 'keeps only the line with the new key blob even when two keys share a comment' { <# run provision.sh --remove-deploy-keys-except on a fixture authorized_keys inside the Pester test through bash #> } }
Describe 'Test-AgeIdentity' { It 'rejects an identity that does not match the recipient' { } }
Describe 'Get-ProductionSettingNames' { It 'matches render-env.sh' { <# parse required and optional names from deploy/scripts/render-env.sh; deploy-only names DEPLOY_*, APP_DIR, SECRETS_ADMIN_TOKEN and APP_IMAGE (supplied by the workflow) excepted, P13 #> } }
Describe 'Test-DomainName' { It 'rejects a URL' { Test-DomainName 'https://x.dev' | Should -BeFalse } }
Describe 'Bootstrap' {
  It 'removes its temporary directory on failure' { <# mocked ssh throws #> }
  It 'does not run --harden when the reconnect check fails' { }
  It 'does not install the age recipient when the pasted identity does not decrypt the challenge' { }
}
```

`provision.sh` checks in `deploy-smoke.yml`: shellcheck, `bash -n`, and an `ubuntu:24.04` container run of `--harden`'s sshd part against a copy of `/etc/ssh` that contains a `50-cloud-init.conf` with `PasswordAuthentication yes`: `sshd -T` reports `passwordauthentication no`.

- [ ] **Step 2:** Run `pwsh -c "Invoke-Pester deploy/bootstrap/tests -CI"` → FAIL. **Step 3:** Implement. **Step 4:** Pester → PASS; `deploy-smoke.yml` runs Pester and the sshd container check on a branch → green.
- [ ] **Step 5:** Commit `feat(deploy): add idempotent two-phase bootstrap for first-time VPS provisioning`.

### Task 17: Link checker reporting

**Files:**
- Modify: `tools/Trainer.ContentCli/Commands/CheckLinksCommand.cs` (option `--report <file>`), `tools/Trainer.ContentCli/Program.cs`, `.github/workflows/link-check.yml`, `docs/content-guide.md` (link check section)
- Test: `tests/Trainer.ContentCli.Tests/CheckLinksCommandTests.cs`

**Interfaces:**
- Consumes: the existing `check-links` command (weekly schedule, exit 1 on dead links, step summary).
- Produces:
  - `check-links [--content <dir>] [--report <file>]`: `--report` writes the same Markdown as the step summary (totals line and the problem table) to the file, also when there are no problems; options in any order; a missing value is a usage error (exit 2).
  - `link-check.yml` job permissions `contents: read`, `issues: write`: run the CLI with `--report "$RUNNER_TEMP/links.md"`, keep its exit code; `gh label create dead-links --force`; when dead links were found: update the open issue labelled `dead-links` (`gh issue edit --body-file`) or create "Dead source links" with that body; when none: close the open issue with a comment "All source links resolve as of <date>". The step fails the job on exit 1 after updating the issue, so the owner also gets the failure notification. `GH_TOKEN` is the job's `GITHUB_TOKEN`, through `env:`.

- [ ] **Step 1:** Tests `CheckLinks_ReportOption_WritesTotalsAndTable`, `CheckLinks_ReportOption_NoProblems_WritesTotalsOnly`, `CheckLinks_ReportWithoutValue_IsUsageError`, `CheckLinks_OptionsInEitherOrder` (local Kestrel server as in the existing tests) → FAIL.
- [ ] **Step 2:** Implement; update the workflow and the content guide. **Step 3:** `dotnet test tests/Trainer.ContentCli.Tests` and `tests/Trainer.Deploy.Tests` → PASS; `workflow_dispatch` run on a branch creates or closes the issue as expected.
- [ ] **Step 4:** Commit `ci: track dead source links in a GitHub issue`.

### Task 18: Deployment documentation and repository docs

**Files:**
- Create: `docs/deployment.md`
- Modify: `README.md` (link to deployment, feedback, Open Graph and analytics in the feature list), `AGENTS.md` (layout: `deploy/` exists, drop "Not created yet"; rules below), `docs/architecture.md` (contents list, every new section linked, every ADR 0015-0021 linked), `tests/Trainer.Web.Tests/DocsTests.cs`

**Interfaces:**
- Produces:
  - `docs/deployment.md` (English, numbered procedures, commands in code font), sections in order: Overview and topology (diagram in text: nginx → 127.0.0.1:8080 app / 3001 Umami; 8081 internal; postgres internal); Prerequisites (Ubuntu 24.04 LTS VPS, DNS A/AAAA record, production bot in BotFather (separate from the dev bot, spec 11.3), PowerShell 7, OpenSSH, `gh` authenticated, an S3-compatible bucket); First-time setup (install `age` locally, run bootstrap, store the age identity and paste it back for the check, register the redirect URI); First deployment (dispatch `deploy-app`, run the backup-identity drill, run `deploy-content`, open Umami through `ssh -L 3001:127.0.0.1:3001`, change the default admin password at once, create the website, set `UMAMI_WEBSITE_ID`, re-run `deploy-app`); Updating host assets (nginx, systemd units, sshd and firewall change only through a bootstrap re-run; a pull request that touches `deploy/nginx`, `deploy/systemd` or `deploy/bootstrap` says so in its description); Interrupted restore (the three states named in `flags/maintenance` and the exact rename commands for each, then removing `flags/maintenance`); Interrupted rotation (run `rotate-db-password` with `mode: finish`); Umami upgrade (pinned exact version: take an `umami` backup, change the pin in a pull request, deploy; if the deploy fails, `.umami-recovery` names the pre-deploy dump and blocks deploys until `restore.sh --replace <dump> umami` succeeds; then revert the pin); Configuration reference (every secret and variable: purpose, who sets it, how to change it); Changing a setting (edit in GitHub, re-run `deploy-app`); Rotating credentials (deploy key, import token, Umami app secret, both database passwords including `mode: finish` after an interrupted run, the S3 key, the age identity with `-RotateAgeIdentity`, keeping the old identity for older backups); Backups and restore (schedule, retention, off-site upload rules, restore drill into a scratch database, full restore with `--replace` and dropping the kept `_old_` database); Rebuilding a lost server, in this order (C8/F13): bootstrap on a new VPS, update `DEPLOY_HOST` and `DEPLOY_KNOWN_HOSTS`, dispatch `deploy-app`, run `deploy-content` for the commit named in the backup's `.meta` file or current `main`, then `restore.sh --replace` of the latest off-site backup (it migrates and re-imports before waiting for readiness); Rolling back (app: revert the commit on `main`, or dispatch `deploy-app` with `ref` and `rollback: true`; the inputs are named as in the workflows, P12); Daily ops check (what each check means, what to do); Hardening checklist before the repository goes public (the four spec bullets, environment branch rule, Dependabot, secret scanning and push protection enabled in repository settings); Troubleshooting (readiness stuck, import rejected, certificate renewal, disk full).
  - `AGENTS.md` rules added (future-facing, owner rule): a new configuration value is added to `render-env.sh`, `compose.yaml`, `Get-ProductionSettingNames`, `docs/deployment.md` and the architecture configuration table in one change; a new published port binds to `127.0.0.1`; a change under `deploy/nginx`, `deploy/systemd` or `deploy/bootstrap` needs a bootstrap re-run after merge; Umami is upgraded only through the documented procedure; migrations are expand-only (Task 8); a new workflow pins actions by SHA, declares minimal permissions, passes secrets and inputs only through `env:`, uploads only into staging and, when it calls a reusable workflow, grants that workflow's permissions on the calling job; a new page that should not be counted by analytics opts out in `AnalyticsScript`; never `set -x` in deploy scripts.
  - `DocsTests` additions: `Deployment_DocumentsEverySecretAndVariable` (each name from `render-env.sh` and from `Get-ProductionSettingNames` in `BootstrapLib.psm1` appears in `deployment.md`); `Deployment_HasRequiredSections` (the headings above, in order); `Agents_DescribesDeployDirectory` (no "Not created yet" block mentioning Plan 3); `Deployment_DocumentedWorkflowInputsExist` (every `<input>: ` named next to `deploy-app`, `deploy-content` or `rotate-db-password` in `deployment.md` is an input of that workflow, P12); `Architecture_ReferencesEveryAdr` and `Architecture_ConfigurationTableMatchesTheOptionClasses` stay green with the new options (`InternalOptions`, `FeedbackOptions`, `OpenGraphOptions`, `AnalyticsOptions`).

- [ ] **Step 1:** Add the `DocsTests` → FAIL. **Step 2:** Write `docs/deployment.md` and update the other docs (no long dashes). **Step 3:** Full check: `dotnet build -warnaserror`, the unit/integration command, the Docker, E2E and Release suites in CI, `deploy-smoke` green.
- [ ] **Step 4:** Commit `docs: add deployment guide and reconcile docs with plan 3`.

### Task 19: First production deployment and acceptance (owner steps with recorded results)

**Files:**
- Modify: `docs/performance.md` (Results rows "phone, deployed"; "Open decisions" updated with the outcome), `docs/deployment.md` (section "Acceptance log": date, commit, each step's result), `tests/Trainer.Web.Tests/DocsTests.cs`

**Interfaces:**
- Consumes: everything above; ADR 0014; the "Measure on the deployed site" procedure in `docs/performance.md`.
- Produces:
  - `DocsTests.Performance_RecordsAcceptedDeployedPhoneRuns`: the Results table has at least one row whose Runner cell contains `phone, deployed` for `/ru` and one for `/en`, each with 5 cold and 5 warm values listed below the table, and both medians under 5 000 ms cold and 2 000 ms warm. The test is added in this task and must pass before Plan 3 is accepted.
  - `DocsTests.Deployment_AcceptanceLogComplete`: the "Acceptance log" lists every step below with a date and `ok`.

Owner steps (the implementer prepares the commands and records the results the owner reports):

- [ ] **Step 1:** Owner: approve or amend the decisions in "Owner decisions needed"; create the production bot, the VPS, the DNS record, the S3 bucket with a list-and-write key without delete rights and a lifecycle rule, and the fine-grained token `SECRETS_ADMIN_TOKEN` for this repository with **Environments: read and write**. Check the token by writing and deleting a throw-away environment secret with it (`gh secret set DIT_TOKEN_CHECK --env production` then `gh secret delete ...`); a list call proves only read access (C14/F6).
- [ ] **Step 2:** Owner: run `pwsh deploy/bootstrap/Bootstrap.ps1 ...`; store the age identity when asked and paste it back for the decryption check; register the redirect URI. Re-run it immediately: the summary reports `0 changed` apart from package upgrades, no new deploy key and no new identity. Record both summaries.
- [ ] **Step 3:** Run `deploy-app` by dispatch (expected: ready with an empty site; the stack has no data yet). Then the first backup-identity drill, before any data exists (F1): start the backup once with `ssh -t <admin>@<host> sudo systemctl start dit-backup.service`, fetch the newest `trainer` dump from the **off-site** bucket and run `restore.sh <file> trainer` (scratch mode) with the identity from the password manager; it must succeed. Record the result.
- [ ] **Step 4:** Run `deploy-content` (expected: questions served), then the Umami steps from `docs/deployment.md` and `deploy-app` again. Record run URLs.
- [ ] **Step 5:** Verify on the live site: `curl -sI https://<domain>/en` has no `Set-Cookie`, has HSTS and the CSP; `https://<domain>/internal/version` is 404; `ssh deploy@<host> curl -s 127.0.0.1:8081/internal/version` shows the deployed SHA; an Open Graph image renders in a link preview (Telegram) in RU and EN; sign-in with the production bot works; rating and reporting from the question page and from Practice work and the report appears on `/{culture}/admin`; Umami shows the page views and the browser shows no cookie besides `.dit.session`/`.dit.af` when signed in; the nginx access and error logs hold no client address and no query string after a request to `/en/dotnet/questions?q=sentinel`. Record each.
- [ ] **Step 6:** Run `ops-check` manually (green, including `backup-identity` and `guards`), wait for the first nightly backup and off-site copy, then the restore drill with real data into a scratch database from the **off-site** copy; record row counts.
- [ ] **Step 7:** Owner: phone measurement per `docs/performance.md` "Measure on the deployed site": a mid-range Android phone over 4G, `?perf=1`, 5 cold and 5 warm runs, RU and EN; record phone model, Android and Chrome versions, network, all values and medians. If a median misses 2 s warm or 5 s cold, stop: the remedy is an owner decision (see the last section) and Plan 3 is not accepted until a later measurement passes.
- [ ] **Step 8:** Add the two `DocsTests`, fill the tables → tests PASS. Commit `docs: record plan 3 deployment acceptance and deployed phone measurement`.

---

## Designer's calls (spec gaps and new scope)

| Call | Choice | Reason |
|---|---|---|
| Analytics engine | Umami, same PostgreSQL, tracker and send endpoint proxied same-origin under `/stats/`, dashboard only through an SSH tunnel | Runs on the database already in the stack (Plausible CE needs ClickHouse); no CSP change; no public admin surface |
| Analytics privacy | `data-exclude-search`, `data-exclude-hash`, `data-do-not-track`; no script on admin and sign-in; `/stats/api/send` rate-limited per address in nginx memory, never logged | Search terms and admin queries never leave the app; ADR 0007 spirit; unauthenticated writes cannot fill the disk |
| Feedback surfaces | Question page (static SSR POST forms, signed-in only) and Practice/Review after answering; not Mock | Works without JavaScript on public pages, keeps anonymous HTML cookieless and cached; Mock stays "no feedback until the end" |
| Report quota | 10 new reports per user per rolling 24 h in the database under a per-user advisory lock; a repeat of an open report updates it | Survives restarts, exact under concurrency, does not punish corrections |
| Comment | Optional, plain text, ≤ 1000 characters, control characters removed, HTML-encoded on display, never logged | Untrusted input shown to admins |
| Feedback offline | Not queued; failure shows a retry message | A second sync protocol for a low-value write is not worth it |
| Internal port check | `Connection.LocalPort`, not the `Host` header; status code pages off for `/internal` and `/og` 404s | Host headers are client-controlled; machine endpoints answer with empty bodies |
| Import token on the server | Read from the server `.env` by `receive-content.sh` | `deploy-content` handles no application secret; GitHub stays the source because `deploy-app` renders `.env` |
| Content mount | Parent `content/` directory, relative symlink, immutable release directories | A bind-mounted symlink is resolved once at container start; a live release must never change under the app |
| Content import failure | The import runs to its end regardless of the client; a transport failure is retried once and the second answer decides; a rejection never commits | Removes the need to infer from hashes whether a lost call committed |
| Fresh install | Ready with an empty site when no release exists and the database has no active import | First `deploy-app` must not roll itself back |
| Base image | `aspnet:10.0-noble` with `curl`, non-root `$APP_UID`, OG cache directory created owned by it | Health check and SkiaSharp need user-space tools and libraries; a new named volume inherits the mount point's ownership |
| Database roles | `trainer`, `umami` without superuser; `postgres` only over the local socket, every admin `exec` as `--user postgres` | Least privilege; peer authentication matches the operating-system user |
| Registry auth on the server | Per-run `DOCKER_CONFIG` from the job's `GITHUB_TOKEN` over stdin | No long-lived registry credential on the server |
| Image flow | Build and push by SHA, smoke-test the pushed digest, run the deployed release's own `Release` suite on the migrated schema, then deploy that digest | The deployed bytes are the tested bytes; rollback safety is exercised with real reads and writes, not assumed |
| Deploy trigger | `deploy-app` after a successful `ci` run of a `push` to `main` in this repository (provenance checked); automatic deploys never go backwards; an explicit `rollback` input is the only override | Nothing untested or foreign reaches production; an older run cannot overwrite a newer one |
| Server state changes | Uploads only into per-run staging; activation, rollback, restore swap, rotation and backups under one `flock` | Two runs can never mix scripts, manifests or environments |
| Rollback target | `last-good.env`, one file carrying environment, image, bundle and commit, published by a single rename after readiness passed | A killed or half-finished deploy is never the rollback target, and the record can never mix deployments |
| Interrupted operations | Guard files (`flags/maintenance`, `.rotation-pending`, `.umami-recovery`, and `.import-pending` for content only) make every state-changing script refuse and point at a documented manual step; no automatic reconciliation | On a single-owner VPS a refusal plus a precise procedure is safer than recovery code that guesses |
| Password rotation | Roll forward only: the new password is journalled before any change and `finish` re-applies it idempotently | One recovery path that converges from any crash point |
| Umami version | Pinned exact image; upgrades are a manual documented step with a backup; a failed deploy never starts an older Umami on a possibly migrated database | Umami migrates its own schema at start; its rollback compatibility is not something this project can prove |
| Pre-deploy backup | Encrypted dump before every deploy, 5 kept; skipped only on an incomplete first install | Expand-only migrations make rollback safe; the dump covers the rest |
| Backup encryption | `age`; identity generated on the admin's PC and proven by a decryption challenge before the recipient is installed | A stolen server or bucket cannot read backups (ADR 0004); a lost identity is detected before it protects anything |
| Off-site copy | Mandatory; key without delete rights; local files kept until uploaded; predeploy upload failures non-fatal | A backup that dies with the VPS is not a backup; a storage outage must not block deploys |
| Restore | Into a scratch database, validated, then renamed in under the lock and site-wide maintenance (nginx 503 for new requests, the app's gate for admitted ones, writers stopped before the swap) with the old database kept; migrations and the startup import follow; any failure stops for the operator | A wrong identity or a corrupt dump can never cost the live database, and no write can land in a database that may be swapped back |
| Monitoring | Daily `ops-check` workflow (readiness, backup identity, backup and off-site age, disk, certificate, restarts, guard files) | Failed scheduled runs notify the owner; no extra service |
| HSTS | One year, no `preload` | Reversible; preload is a separate owner choice later |
| nginx logs | HTTP error log off (`/dev/null`) and access log off by default; the site's access log without address and query | Global Constraints and ADR 0007; nginx error lines at any level can carry both |
| nginx version | Ubuntu 24.04's 1.24 with `listen ... http2` | The `http2` directive needs 1.25.1 |
| Open Graph rendering | SkiaSharp, lazy, disk cache, fingerprinted immutable URLs, site card for non-question pages | Licence-clean, cheap, crawler-friendly |
| Link-check reporting | One issue labelled `dead-links`, updated or closed weekly | A report that does not disappear with the run log |
| `DEPLOY_KNOWN_HOSTS` | From the admin's `known_hosts`, not `ssh-keyscan` | Uses the host key the admin already verified |
| Deploy key | Created once, kept on re-runs, rotated only on request with the old key kept until the new one is proven; identified by key material, not comment; `restrict` in `authorized_keys` | Re-runs must not desynchronise GitHub and the server; no forwarding or PTY for the automation key |
| sshd and firewall | Applied in a second phase after a successful key-only reconnect; `10-dit.conf` drop-in verified with `sshd -T`; SSH port from the live connection | No lockout; cloud-init's drop-in cannot silently win |
| Dependabot | PostgreSQL majors and every Umami update ignored | A PostgreSQL major needs a dump and restore, not a tag bump |

## Owner decisions needed

Owner approved the plan with all four recommended defaults on 2026-11-15. Decision 3 (provider, domain) is supplied at Task 19.

| # | Decision | Recommended default |
|---|---|---|
| 1 | ADR 0020: instead of spec 8.7.2's single shared concurrency group and push-triggered `deploy-app`: a workflow-level concurrency group per deploy workflow, a server-side `flock` for every state change, automatic deploys that never go backwards (an explicit `rollback` input overrides), `deploy-app` triggered by a successful `ci` run of a push to `main` in this repository, and `deploy-app` dispatching `deploy-content` when content is behind (Tasks 11, 13, 14). | Approve. One shared group lets GitHub cancel a queued deploy of the other workflow; job-level groups let an older run deploy after a newer one; a push trigger deploys before the E2E and Release suites pass. |
| 2 | ADR 0021: off-site backup provider, region and retention; the off-site copy is now mandatory (Task 12). | Backblaze B2 (S3 API), private bucket, 30-day lifecycle rule, application key scoped to that bucket with list and write but no delete; age identity kept in the owner's password manager. |
| 3 | VPS provider and size, and the production domain (needed before Task 19; the domain is `PUBLIC_BASE_URL`). | Ubuntu 24.04 LTS, 2 vCPU, 4 GB RAM, 40 GB SSD (app 768 MB, Umami 512 MB, PostgreSQL and the host fit with headroom); domain of the owner's choice. |
| 4 | If the deployed-phone medians miss 2 s warm or 5 s cold (Task 19 Step 7), which remedy from ADR 0014 to plan. | Reduce work before the first render first (no payload growth); AOT only if the warm target alone is missed, since AOT enlarges the cold download. |

## Self-review notes

- Coverage: spec 4 feedback paragraph (Tasks 3-5); 7.3 on-demand import through the internal endpoint with cache eviction and sitemap regeneration (Task 1, existing invalidator, Task 6 composite); 7.4 scheduled link checker (existing `link-check.yml`, reporting in Task 17); 8.2 phone targets (Task 19, ADR 0014); 8.4 Open Graph tags gain images (Task 6); 8.7 Compose, nginx, Data Protection keys in backups, migration bundle, readiness, configuration from GitHub, logging (Tasks 8-12); 8.7.1 layout and 5 releases (Tasks 9, 14); 8.7.2 four workflows, environment, hardening, internal endpoint (Tasks 1, 11, 13-15); 8.7.3 bootstrap steps 1-7 and rotations (Task 16, docs in Task 18); 10 `docs/deployment.md` (Task 18); owner scope: analytics (Tasks 7, 9, 10), Open Graph (Task 6), phone check (Task 19); Search Console excluded.
- Interfaces checked across tasks: `IQuestionFeedbackService`/`IQuestionFeedbackQueries` (Task 3) are used by Tasks 4 and 5 with the same names; `SessionDto.MyRatings` (Task 4) is filled by `SessionQueries` and read by `QuestionFeedbackBar`; `IContentStatusQueries.GetActiveHashAsync` (Task 1) is used by Task 2 and surfaces as `InternalVersionDto.ActiveContentHash` (operator visibility; `receive-content.sh` relies on the definitive import outcome instead); `InternalVersionDto.ContentSchemaMin/Max` (Task 1) are read by `receive-content.sh`; `ContentImport__Token` (Task 1) is rendered from `CONTENT_IMPORT_TOKEN` (Task 9) and read by `dit_env_get .env CONTENT_IMPORT_TOKEN` (Task 14); `App__Internal__Port=8081` and the Kestrel `Internal` endpoint (Task 9) match `InternalOptions` (Task 1); `/stats/script.js` and `/stats/api/send` (Task 7) match the nginx locations (Task 10) and Umami's port 3001 (Task 9); `OgImageCache` directory `/var/cache/dit/og` (Task 6) is created in the image (Task 8) and is the `ogcache` volume (Task 9); `lib.sh` functions and the server layout (Task 9) are used by Tasks 11, 12, 14, 15; the staging protocol, the guard files and `last-good.env` (Task 11) are used by Tasks 12-15; `schema-compat.yml` (Task 11) is called by `ci.yml` (Task 11) and `deploy-app.yml` (Task 13); `.rotation-pending` (Task 15) and `flags/maintenance` (Task 12) are honoured by `deploy-app.sh` (Task 11, which refuses whenever either exists, before the later tasks create them) and reported by `ops-check` (Task 12); the nginx maintenance rule (Task 10) reads the same `flags/maintenance` file; concurrency group names (ADR 0020, Task 11) are asserted in Task 13; `Get-ProductionSettingNames` (Task 16) is cross-checked against `render-env.sh` (Task 9) by Pester and by `DocsTests` (Task 18).
- Order: application changes (Tasks 1-7) precede the image (Task 8); Task 11's `deploy-app.sh` tolerates the missing `backup.sh` until Task 12 makes it mandatory; the content follow-up in `deploy-app.yml` is added by Task 14, when `deploy-content.yml` exists; Pester runs in `deploy-smoke.yml` only once Task 16 adds files. No task depends on a later one.
- ADR numbering follows task order: 0015 (Task 1), 0016 (Task 3), 0017 (Task 6), 0018 (Task 7), 0019 (Task 9), 0020 (Task 11, approval), 0021 (Task 12, provider approval).

## Review resolution

Two independent reviews (Codex: C1-C20; Fable: F1-F14) were checked against the code, the spec and, where the claim was about third-party behaviour, the vendor documentation: nginx.org `ngx_http_v2_module` ("This directive appeared in version 1.25.1"), GitHub "Reusing workflow configurations" (caller permissions "can be only downgraded (not elevated) by the called workflow"; `jobs.<id>.permissions` is allowed on a calling job), and GitHub "Permissions required for fine-grained personal access tokens" (environment secrets are written with the repository permission "Environments: write"; "Secrets" covers repository secrets only). Every finding was accepted, in some cases with a narrower fix than the reviewer proposed; none was rejected.

| Id | Finding | Resolution |
|---|---|---|
| C1 | Destructive restore before validation; no lock; stops only `app` | Accepted. Task 12 `restore.sh`: scratch database, validation, swap by rename under `dit_lock`, writers of that database stopped, old database kept; smoke `restore_with_wrong_identity_leaves_live_database_untouched`. |
| C2 | Backup identity can be lost permanently | Accepted. Task 16 step 8: identity generated on the admin's PC, shown, pasted back and proven by a decryption challenge before the recipient is installed; `backup.sh` refuses without a recipient; `ops-check backup-identity`; Task 19 Step 3 drill before data. |
| C3 | Rotation is not one transaction | Accepted; mechanism replaced in round 2 by roll-forward (R3). Task 15: lock in every mode, durable journal `.rotation-pending`, `deploy-app.sh` refuses while it exists, `reconcile` mode against GitHub's current value, one-line revert; `deploy-app.sh` refuses `.env.new` whose database passwords differ from the server's (obsolete credential generation). |
| C4 | Uploads bypass serialisation | Accepted. Task 11 upload protocol: per-run staging only; activation of bundle, `compose.yaml` and `.env` under the lock; smoke `concurrent_uploads_and_deploys_serialise`; `WorkflowHardeningTests.DeployJobs_UploadOnlyIntoStaging`. |
| C5 | Rollback restores only part of the deployment; first-install failure | Accepted; the record became one file, `last-good.env`, in round 2 (R4). Task 11: `last-good/` holds `.env`, bundle (with `compose.yaml`), image and commit; rollback restores all; explicit incomplete-first-install path; backup skipped when no database exists; smoke `failed_first_install_can_be_retried`. |
| C6 | Rollback safety not established by a blacklist | Accepted; the image-level check was replaced in round 2 by `schema-compat.yml` (R6, R7). Task 8 blacklist extended and raw SQL requires the attribute; Task 11 smoke `previous_image_runs_on_migrated_schema` runs the actually deployed previous image against the migrated database for every deploy (Task 13 passes `previous_image`), and the attribute does not bypass it. |
| C7 | HTTP failure does not imply import failure | Accepted; replaced in round 2 by the definitive-outcome rule (R5). Task 1 adds `ActiveContentHash`; Task 14 compares it before and after and re-imports the previous release when the failed call committed; smoke `import_response_lost_after_commit_is_reconciled`. |
| C8 | Recovery ordering broken | Accepted. Task 12: dumps carry `.meta` (image, migration, content hash, release); `restore --replace` runs the migration bundle and re-imports the release on disk before waiting for readiness; Task 18 rebuild order is bootstrap, `deploy-app`, `deploy-content`, then restore; smoke `rebuild_empty_server_from_older_backup`. |
| C9 | Peer authentication fails as root | Accepted (verified: the `postgres` image's default exec user is root; peer maps the OS user). Task 9 `dit_psql`/`dit_pg_dump` use `--user postgres`; `ComposeFileTests.PostgresAdminCommands_RunAsPostgresUser`. |
| C10 | `http2 on` unsupported on Ubuntu 24.04 nginx | Accepted (nginx.org: directive since 1.25.1; Ubuntu 24.04 ships 1.24). Task 10 uses `listen 443 ssl http2;` and tests against the Ubuntu 24.04 package in `nginx-check.sh`. |
| C11 | Provisioning assets never arrive; host updates impossible | Accepted; the drift check was dropped in round 2 (R14). Task 16 uploads `host-assets.tgz` (provision script, nginx templates, units, `pg_hba.conf`); re-running bootstrap updates them; `ops-check host-assets` reports drift. |
| C12 | Bootstrap reruns rotate deploy keys; no reconnect before hardening | Accepted. Task 16: `Get-DeployKeyPlan` keeps established keys; rotation keeps the old key until the new one is proven; hardening only after a key-only reconnect, then `sshd -t`, `sshd -T`, and a second reconnect. |
| C13 | Reusable smoke lacks caller permissions | Accepted (GitHub docs quoted above). Task 13 calling job grants `contents: read, packages: read`; `ReusableWorkflowCallers_GrantWhatTheCalledWorkflowDeclares`. |
| C14 | Rotation token has the wrong permission | Accepted (GitHub fine-grained permission table). Task 15 and Task 19 require "Environments: read and write" and prove it by writing and deleting a throw-away environment secret before PostgreSQL is touched. |
| C15 | Serialisation does not prevent stale deployment; combined push | Accepted; decided together with F5 (see F5). Stale guard on both deploy workflows with a manual `ref` override; `deploy-app` dispatches `deploy-content` when content is behind (Task 14); `receive-content.sh` exit 5 explains the deferral. |
| C16 | Manual rollback validates different content | Accepted. Task 14 `resolve` job yields one immutable SHA used by `content-checks` (`ref` input) and delivery; deploy scripts come from the workflow's own commit. |
| C17 | Logging violates privacy | Accepted. Task 10: no client address or query in `log_format`, error log at `crit`; `nginx-check.sh` sends sentinel requests to a dead upstream and asserts neither log contains them or the address; Task 19 Step 5 repeats it on the live host. |
| C18 | Backup consistency overstated; off-site optional | Accepted. Task 12: daily backups take the lock (predeploy runs inside the deploy's lock); off-site mandatory (`render-env.sh` requires the group); files are kept until uploaded. |
| C19 | OG cache volume unwritable | Accepted. Task 8 creates `/var/cache/dit/og` owned by `$APP_UID` (Docker copies the mount point's ownership into a new named volume); smoke `og_image_renders_in_container`. |
| C20 | Replacing an existing SHA breaks atomic delivery | Accepted. Task 14: release directories are immutable; a re-upload is reused when its manifest matches and refused otherwise; smoke tests both. |
| F1 | Age identity lost; PTY capture conflict | Accepted; same fix as C2. Generating locally removes the captured-PTY problem entirely (no secret output is read from a `ssh -t` session); `-RotateAgeIdentity` added; drill moved before data (Task 19 Step 3). |
| F2 | `restore --replace` drops first, skips migrations, no lock | Accepted; same fix as C1 plus the migration bundle and content re-import after the swap (C8). |
| F3 | `.env.prev` is not last good; HUP/TERM; `compose.yaml` outside rollback and lock | Accepted. Last-good record (C5, R4), `setsid` plus traps, staging-only uploads (C4); smoke `killed_deploy_then_failed_deploy_rolls_back_to_last_good` and the changed-manifest case in `failed_readiness_rolls_back_to_last_good`. |
| F4 | sshd lockout or ineffective hardening; hard-coded port 22 | Accepted. Task 16 phase 2: `10-dit.conf`, `sshd -t`, `sshd -T`, port from `$SSH_CONNECTION`, reconnect checks; container check with a cloud-init drop-in. |
| F5 | Job-level concurrency does not order deploys | Accepted, and decided against C15's narrower reading: workflow-level groups order runs of one workflow (an older run's build, smoke and deploy finish before a newer run starts); C15's stale guard is still added because re-runs, manual dispatches and cross-workflow follow-ups can carry older commits. Owner decision 1 updated. |
| F6 | Token permission; whole-file revert wipes a deploy | Accepted; C14 for the permission, Task 15 reverts only the one line and deploys are blocked during a rotation (C3). |
| F7 | `deploy-app.yml` cannot start: caller permissions; expression in `permissions` | Accepted; same as C13; `deploy-smoke.yml` declares `packages: read` statically. |
| F8 | Migration check misses common violations | Accepted. Task 8 adds NOT NULL without default, new unique index, foreign key, check and unique constraint on existing tables, dropped index, removed default, and raw SQL. |
| F9 | `ogcache` not writable | Accepted; same as C19. |
| F10 | `exec -U postgres` runs as root | Accepted; same as C9. |
| F11 | Umami without client address; unlimited public writes | Accepted. Task 10 forwards `X-Forwarded-For` to Umami and adds `limit_req` (memory zone, `crit` log level) and a 16 KB body cap on `/stats/api/send`. |
| F12 | Timer enabled late; units never updated; predeploy blocked by off-site; deleting S3 key | Accepted. Task 16 installs and enables units at once with `ConditionPathExists`; host assets update through bootstrap re-runs with drift detection (C11); predeploy upload failures are non-fatal; the key has no delete right (ADR 0021, owner decision 2). |
| F13 | Rebuild procedure ends in Failed readiness | Accepted; same fix as C8 (content delivered before the restore, re-import after the swap). |
| F14 | Small gaps | Accepted, each: smoke breaks `.env.new` after rendering instead of passing a value `render-env.sh` rejects (Task 11); HTTP-only nginx template for the pre-certificate phase (Task 10); `ref` through `env:` with a SHA regex and the same SHA checked and delivered (Task 14, `InputsAppearOnlyInEnvBlocks`); Dependabot ignores PostgreSQL and Umami majors (Task 13); no `$remote_addr` in logs (Task 10); `/internal` 404s keep an empty body by disabling status code pages in the filter (Task 1); `deploy-app` runs only after a successful `ci` run (Task 13, ADR 0020). |

### Round 2 (Codex re-review, R1-R14)

Checked against nginx.org (`limit_req_log_level`: "info | notice | warn | error", default `error`), GitHub "Events that trigger workflows" (`workflow_run` runs with access to secrets and write tokens "even if the previous workflow was not"; its `branches` filter names the head branch) and the importer code (`ContentImportService` already swallows cache-invalidation failures after commit, so the earlier "invalidation throws" scenario cannot occur, but a cancelled request token could still abort an import). Simplifications that remove a whole class of findings: guard files with documented manual recovery (R2, R3), one-file deployment record (R4), a definitive import outcome instead of hash inference (R5), running the deployed release's own test suite instead of inferring compatibility (R6, R7), nginx HTTP logging off instead of filtered (R9, R10), Umami pinned with a manual upgrade (R12), and no host-asset drift check (R14).

| Id | Finding | Resolution |
|---|---|---|
| R1 | `workflow_run` provenance not verified | Accepted (mechanism, small). Task 13 `resolve` requires `conclusion == success`, `event == push`, `head_branch == main`, `head_repository.full_name == github.repository` and ancestry on `origin/main`; `DeployApp_ChecksWorkflowRunProvenance`. |
| R2 | Restore starts writers before validation; interrupted swap unrecoverable | Accepted (simplified). Task 12: site-wide maintenance through a `flags/maintenance` file that nginx answers with 503 and every script refuses on; validation before the file is removed; on any failure the script stops and `docs/deployment.md` "Interrupted restore" gives the exact commands for each state. No swap journal, no automatic rename back. First-install detection now uses `last-good.env`, so a renamed-away database is never mistaken for a new server. |
| R3 | Rotation reconcile commits wrongly after a crash | Accepted (simplified). Task 15 is roll-forward only: the new password is journalled before any change, `finish` re-applies it idempotently and GitHub is updated last; `revert` and `reconcile` are removed. Smoke covers crashes before and after `ALTER ROLE`. |
| R4 | `last-good` files not published atomically | Accepted (simplified). One file, `last-good.env`, carrying `APP_IMAGE`, `DIT_BUNDLE_SHA` and `DIT_COMMIT`, published by one rename; smoke `killed_between_record_steps_never_mixes_deployments`. |
| R5 | Timeout does not prove the import finished; no empty-state policy; timing-dependent test | Accepted (simplified). Task 1 runs the import on the application-stopping token, not the request's; Task 14 uses no client timeout, retries once on a transport failure and takes the second answer as definitive (the advisory lock orders it after any in-flight import); a rejected first delivery removes `current` (fresh-install state); fault injection by a connection-closing listener. |
| R6 | Previous deployment taken from workflow history | Accepted (simplified). Task 13 `prepare` reads `DIT_COMMIT` from the server's `last-good.env`; no `gh run list`, so `actions: read` is not needed; `DeployApp_ReadsDeployedCommitFromServer`. |
| R7 | Compatibility smoke exercises no user writes | Accepted (simplified). Task 11 `schema-compat.yml` runs the deployed (or merge-base) commit's own `Release` suite, which signs in and writes answers, review states, mock sessions and feedback, before and after applying the new migrations; called from CI and from `deploy-app`. The image-level previous-image smoke is removed. |
| R8 | Follow-up dispatch is treated as a rollback | Accepted. Separate `rollback` input (default false) on both deploy workflows; the follow-up never sets it; `DeployApp_DispatchesContentFollowUpWithoutRollback`. Partly rejected: the freshness check stays on the runner, because `deploy-content` runs are serialised by their workflow-level group and no other workflow switches `current`. |
| R9 | `limit_req_log_level crit` is invalid | Accepted (verified on nginx.org). Directive removed; `NoLimitReqLogLevel`. |
| R10 | `crit` error log still carries request context; other servers inherit logging | Accepted (simplified). Task 10 installs an `nginx.conf` with HTTP `error_log /dev/null` and `access_log off`; only the site's address-free, query-free access log is on; `nginx-check.sh` exercises upstream failure, rejected host, oversized body, rate limit and maintenance with sentinels across all log files. |
| R11 | Metadata queries EF tables in the Umami dump | Accepted. Task 12 `.meta` is database-specific; smoke `both_dumps_have_database_specific_meta_and_restore`. |
| R12 | Umami rollback after its own migration | Accepted (simplified). Umami pinned to an exact image, Dependabot ignores it, upgrades follow a documented manual procedure with a backup; a failed deploy that changed Umami leaves it stopped instead of starting the older image; smoke `umami_image_change_is_not_rolled_back_automatically`. |
| R13 | Keys identified by a date-only comment | Accepted. Task 16 identifies keys by key type and blob; comments get a timestamp and random suffix for humans only; Pester fixture with two keys sharing a comment. |
| R14 | Host-asset drift comparison undefined | Accepted (simplified by dropping the feature). No version stamp and no `ops-check host-assets`; host assets change only through a bootstrap re-run, required by `AGENTS.md` and `docs/deployment.md` for changes under `deploy/nginx`, `deploy/systemd`, `deploy/bootstrap`; `pg_hba.conf` moved into the deploy bundle with a directory mount and reload. |

### Round 3 (Codex scoped check, Q1-Q5)

Verified against nginx.org (`proxy_request_buffering on` by default: the whole body is read before the request is passed upstream, so a request admitted before the flag appeared can reach the app after it) and the existing code (`ContentImportService` holds a transaction-scoped advisory lock, so a queued import call waits for an in-flight one; Compose bind mounts and `pg_reload_conf()` behave as Task 9 now relies on). No finding was rejected.

| Id | Finding | Resolution |
|---|---|---|
| Q1 | Maintenance does not stop requests already admitted by nginx | Accepted. Task 2 adds a file-based maintenance gate in the app (503 for every non-GET on the public port, before routing); the flag lives in `${APP_DIR}/flags`, mounted read-only into the app and read by nginx; Task 12 creates it, stops the writers before the swap (draining in-flight requests against the old database) and removes it only after validation; smoke `restore_admitted_slow_post_is_rejected_after_restart`. |
| Q2 | Recovery can update the wrong GitHub secret | Accepted. Task 15 binds everything to the journal's role: `finish` and `commit` exit 2 on a mismatched role, the workflow checks `pending-role` first and derives the secret name from the verified role; smoke `finish_with_other_role_fails_without_change`. |
| Q3 | Stopping Umami is not persistent | Accepted. Task 11 writes `.umami-recovery` before rolling back; `deploy-app.sh` refuses while it exists; the only allowed operation is `restore.sh --replace` of the named pre-deploy dump, which runs under the rolled-back bundle (old image), starts Umami after the swap and removes the guard. |
| Q4 | `pg_hba.conf` escapes rollback | Accepted. `dit_apply_hba` copies the active bundle's file and reloads PostgreSQL on activation and on rollback; smoke `failed_deploy_changing_hba_restores_previous_rules`. |
| Q5 | An unresolved import can outlive serialisation | Accepted. Task 14 `.import-pending` guard, kept after two non-answers and settled by the next delivery (one more import call, queued behind the in-flight one on the advisory lock) before anything is extracted or switched; the fault proxy forwards the request and disconnects only after the importer holds its advisory lock; smoke `unresolved_import_blocks_switch_until_settled`. |

## Pre-flight rulings

Applied before execution (owner-approved plan; internal consistency only, design unchanged). Source: `.superpowers/sdd/2026-11-15-plan-3-deployment-and-feedback/preflight.md`, conflicts C1-C16, recorded here as P1-P16.

| Id | Ruling | Tasks changed |
|---|---|---|
| P1 | Unpack the bundle into `<staging>/bundle`; scripts run from `<staging>/bundle/remote/` | 11, 13, 14, 15 |
| P2 | Content-only skip only for `workflow_run` with a non-empty diff; manual same-commit dispatch redeploys | 13 |
| P3 | Explicit `if:` on the `deploy` job tolerating a skipped `compat`; pinned by a hardening test | 13 |
| P4 | `receive-content.sh` calls curl without `-f`, reads the status code; 200 and 422 definitive | 14 |
| P5 | `DocsTests.WorkflowCommands` also reads `content-checks.yml` | 14 |
| P6 | Each task that adds options extends `DocsTests`' expected set; new architecture sections go into the Contents list in the same task | 1, 3, 6, 7, 9 |
| P7 | Task 15 documents `rotate-db-password.yml` under "Tests and CI" | 15 |
| P8 | Bootstrap creates `flags` and `postgres` (owner `deploy`, 755), no `last-good` directory; `deploy-app.sh` also `mkdir -p`s them | 16, 11 |
| P9 | Maintenance gate also blocks `GET /signin-telegram`; test added | 2 |
| P10 | `nginx.conf.template` in Task 10's Create list and test inputs | 10 |
| P11 | `/og` route values renamed to `lang`, `stackKey`; empty-body 404 test for an unsupported culture | 6 |
| P12 | Docs use the `rollback` input; `DocsTests` checks documented inputs exist | 18 |
| P13 | Optional rate-limit and feedback names in `Get-ProductionSettingNames`; `APP_IMAGE` excepted | 16 |
| P14 | `deploy-smoke.yml` logs in with `github.token` through `env:` | 11 |
| P15 | `ops-check guards` lists all four guards; `restore.sh` ignores `.import-pending` | 12 |
| P16 | `schema-compat` skips its second run, with a log line, when `previous` lacks the re-runnable `Release` suite marker | 11 |
