# Deployment and operations

How the Dev Interview Trainer runs in production, how to set it up from nothing, and what to do when
something stops half-way. The design reasons are in `docs/architecture.md` ("Deployment topology") and in
ADR 0015 to 0021 under `docs/adr/`. Every command here was taken from the scripts and workflows in this
repository; when a script and this page disagree, the script is right, and this page has a bug to fix.

The server edition is not deployed while the site runs on GitHub Pages: everything below applies when a server is set up,
and only "Publishing the static site" applies now. The static edition's own guide is `docs/static-edition.md`; the checks
for making the repository public are added to it with the pre-public audit (Task 20 of Plan 5).

Conventions used below:

- `<admin>`, `<host>`, `<port>`, `<domain>` and `<owner>/<repo>` are yours to fill in. `<admin>` is the account you
  log in with to provision the server (key login, `sudo` or root); it is not the `deploy` user. `<port>` is the SSH
  port of the server (22 unless you bootstrapped with `-SshPort`). Every `ssh` and `scp` command below shows
  `-p <port>` (`-P <port>` for `scp`); leave it out when the port is 22, or put the host and port in a `Host` entry
  of `~/.ssh/config` and drop the option everywhere.
- Server commands run on the VPS. The scripts in `current-bundle/remote/` run as the `deploy` user, which
  owns `/srv/dev-interview-trainer` and holds the deploy lock, so they are started with `sudo -u deploy`.
  Define these once per shell session:

  ```bash
  export APP_DIR=/srv/dev-interview-trainer
  R="$APP_DIR/current-bundle/remote"
  dc() { sudo -u deploy docker compose --project-directory "$APP_DIR" -f "$APP_DIR/compose.yaml" --env-file "$APP_DIR/.env" "$@"; }
  ```

- Workflow names are the files in `.github/workflows/`. Start one with Actions, the workflow, "Run
  workflow" on `main`, or with `gh workflow run <file> --ref main -f <input>=<value>`.
- Never run any script of `deploy/` with `bash -x` or `set -x`: they handle secrets, and the repository
  forbids tracing in them.

## Overview and topology

```
Internet
   |  80, 443
   v
nginx (on the host, TLS from Let's Encrypt, certbot renews it)
   |-- /stats/script.js, /stats/api/send  -> 127.0.0.1:3001  Umami (analytics, cookieless)
   |-- /internal/...                      -> 404, never proxied
   `-- everything else                    -> 127.0.0.1:8080  app (public endpoints)

127.0.0.1:8081   app (internal endpoints: content import, version); no proxy, not reachable from outside
postgres         only on the Compose network 172.30.0.0/24; publishes no port
```

Docker Compose (project `dev-interview-trainer`) runs three services: `postgres`, `app` and `umami`. Every
published port is bound to `127.0.0.1`. The Umami admin interface is not exposed: you reach it through an SSH
tunnel. The deploy user has no `sudo`; it is in the `docker` group, which is root-equivalent on the host and is
the accepted price of automated deployments. The deploy key in its `authorized_keys` carries `restrict`.

What changes the server, and who starts it:

| Workflow | Starts | What it does |
|---|---|---|
| `ci` | pull requests, pushes to `main`, by hand | Build, tests, content checks, browser checks, the Release suite. Never touches the server. |
| `deploy-app` | after `ci` succeeds on a push to `main`, or by hand | Deploys the newest commit on `main` whose `ci` push run succeeded (an explicit `ref` by hand names its own commit), after the image build, the quick smoke tier and the schema-compatibility checks. |
| `deploy-content` | a push to `main` that changes `content/`, or by hand | Delivers the `content/` tree as an immutable release and asks the running app to import it. No restart. |
| `rotate-db-password` | by hand | Rolls the password of one database role forward. |
| `ops-check` | daily at 07:00 UTC, or by hand | Runs `ops-check.sh` on the server. The job failing is the alert. |
| `link-check` | weekly on Monday at 06:00 UTC, or by hand | Checks the source links of the question bank. Never touches the server. |
| `deploy-smoke`, `schema-compat`, `content-checks` | called by the workflows above; `deploy-smoke` also on pull requests that touch `deploy/`, `Dockerfile`, `.dockerignore`, `src/` or the `deploy-*.yml` and `deploy-smoke.yml` workflows, and by hand | Checks of the bundle, of the previous release against the new schema, and of the content. `deploy-smoke` has two tiers, described below. |

`deploy-smoke` runs `deploy/tests/smoke.sh` in one of two tiers, chosen by `DIT_SMOKE_MODE`:

- `quick` stops after the rollback section: first install, a failed and re-run first install, the internal endpoints, headers
  and rendering, a content switch, the image, the refusals and the rollbacks. It is much shorter than `full`; it has no random kills and no backup checks.
- `full` runs every section, including serialisation, 4 random kills (`DIT_SMOKE_KILL_ITERATIONS`), backups and restores,
  content delivery, the Umami guards and password rotation. The smoke step measured about 16 minutes on a hosted runner with
  20 kills, so it is shorter now.

A pull request runs `full` when it changes anything under `deploy/`, `.github/workflows/deploy-*.yml` or `.github/actions/`,
the `Dockerfile` or `.dockerignore`, and `quick` otherwise (a change under `src/` alone). The job prints the mode it chose and
the file that decided it. `deploy-app` calls the workflow with `quick`: the deployment scripts on `main` already passed `full`
on their pull request, so a production deployment only re-checks them against the new image. A manual run takes the tier as
the `mode` input (default `full`). Nothing runs the smoke on a schedule.

All workflows that touch the server use the GitHub environment `production`, whose deployment branch rule
allows only `main`; that rule is the access boundary. Do not add required reviewers to the environment: the scheduled
`ops-check`, every content delivery and the follow-up that `deploy-app` dispatches would all wait for an approval, and
because GitHub keeps one pending run per concurrency group, a waiting `ops-check` is replaced by the next day's run, so
the daily alert would stop without any run turning red. Their concurrency groups (`production-app`, `production-content`, `production-rotate`,
`production-ops`) never cancel a run, and every state change on the server takes the lock `$APP_DIR/.deploy.lock`
([ADR 0020](adr/0020-deploy-serialisation.md)).

Files on the server under `/srv/dev-interview-trainer`:

| Path | Meaning |
|---|---|
| `.env`, `last-good.env` | The rendered configuration (mode 600) and the copy that last passed readiness. `last-good.env` is the only record of what runs. |
| `compose.yaml`, `current-bundle` | Symlinks into `bundles/<hash>/`, the unpacked `deploy/` directory of the active release. |
| `content/releases/<sha>`, `content/current` | Immutable content releases (the five newest are kept) and the symlink to the live one. |
| `backups/`, `backup/recipients.txt` | Encrypted dumps with their `.meta` and `.uploaded` files, and the public age key they are encrypted to. |
| `logs/deploy-app.log` | The log of the deployment script (one 5 MiB generation is kept). |
| `staging/<run>-<attempt>/` | Per-run uploads; removed by the workflow, and swept after 48 hours. |
| `flags/maintenance`, `.rotation-pending`, `.umami-recovery`, `.import-pending`, `.deploy-in-progress` | Guard files. While one exists, the operation it protects refuses to start; each has a section below. |
| `flags/deploy-hold` | Written by `deploy-app` after a rollback by dispatch: automatic deployments end with a notice while it exists. See "Rolling back". It is not a guard file for the scripts, but `ops-check` fails `deploy-hold` every day while it exists. |

## Prerequisites

1. A VPS with Ubuntu 24.04 LTS, a public IPv4 address and root or `sudo` access with an SSH key. Bootstrap
   installs Docker Engine with the Compose plugin, nginx, certbot, `ufw`, `age` and `rclone`, so nothing needs to be
   installed by hand.
2. A DNS A record (and AAAA if the VPS has IPv6) for `<domain>` pointing at the VPS. The certificate is requested
   during bootstrap, so the record must resolve first.
3. A production Telegram bot in `@BotFather` with OIDC enabled, separate from the development bot (design
   section 11.3; the README shows the development setup). Its Client ID and Client Secret are asked for by
   bootstrap. Never reuse the development bot here.
4. On your PC: PowerShell 7.2 or newer (`pwsh`), the OpenSSH client (`ssh`, `scp`, `ssh-keygen`), `tar` and `age`
   with `age-keygen` (`winget install FiloSottile.age`, `brew install age` or `apt install age`).
5. `gh` authenticated for the repository (`gh auth login`). Without it bootstrap still works but prints a
   checklist instead of writing to GitHub (see "First-time setup").
6. An S3-compatible bucket for the off-site backups ([ADR 0021](adr/0021-encrypted-offsite-backups.md) uses
   Backblaze B2), and two application keys restricted to that bucket:
   - the server's key: list and write, no delete, no bucket creation. It goes into GitHub (`BACKUP_S3_*`) and onto
     the server, and it cannot read anything back;
   - a read key: list and read only. It exists to restore, it is kept in your password manager next to the backup
     identity, and it is never stored on the server or in GitHub ("Fetching a dump from the bucket").

   The provider value for `BACKUP_S3_PROVIDER` is `Other`: rclone's S3 provider list has no Backblaze entry (the
   smoke test uses `Other` too). The endpoint for B2 looks like `https://s3.us-west-004.backblazeb2.com` (the region
   is shown with the bucket). Give the bucket a lifecycle rule that expires objects after 30 days.
7. Admin access to the repository settings, and a fine-grained personal access token for
   `SECRETS_ADMIN_TOKEN` (see the configuration reference).

## First-time setup

Run this once from your PC, in a checkout of the repository.

1. Connect to the server once with `ssh -p <port> <admin>@<host>` and accept the host key. Bootstrap reads the pinned host
   key from your own `known_hosts` and refuses to continue without an entry.
2. Run the bootstrap. Parameters in brackets are optional.

   ```powershell
   pwsh deploy/bootstrap/Bootstrap.ps1 -HostName <host> -AdminUser <admin> -Domain <domain> `
     -AdminTelegramIds <id>[,<id>] -LetsEncryptEmail <email> [-SshPort <port>] [-TelegramClientId <id>] [-Repository <owner>/<repo>]
   ```

   `-SshPort` defaults to what `ssh -G <host>` reports; it is stored as `DEPLOY_SSH_PORT`. `-Repository` is detected
   through `gh` when omitted. The OIDC client secret and the S3 key pair are read as secure prompts and are never
   parameters.
3. Bootstrap works in two phases with a reconnect check between them: phase 1 installs the packages, creates the
   `deploy` user, the directory layout, the nginx sites, the certificate with its renewal hook, and the daily
   backup timer; phase 2 hardens `sshd` (password login off, key login only) and enables the firewall
   for the SSH port(s), 80 and 443. If the reconnect check after phase 2 fails, the error message gives
   the recovery steps for the provider's web console.
4. Store the backup identity. Bootstrap generates an age key pair on your PC, prints the identity (the line
   starting with `AGE-SECRET-KEY-`) once, and asks you to paste it back. Save it in your password manager first. The
   paste is proven by encrypting and decrypting a test message; three failed attempts end the run without
   installing the recipient, and backups stay refused. Losing this identity makes every backup unreadable.
5. Enter the values it asks for (press Enter to skip any of them and set it by hand later):
   `TELEGRAM_OIDC_CLIENT_SECRET`, `BACKUP_S3_ACCESS_KEY_ID`, `BACKUP_S3_SECRET_ACCESS_KEY`, `BACKUP_S3_PROVIDER`,
   `BACKUP_S3_ENDPOINT`, `BACKUP_S3_BUCKET` and `TELEGRAM_OIDC_CLIENT_ID`. A name that already exists in GitHub is
   not asked for again.
6. Bootstrap lists the names it will write and waits for you to type `yes`. It then creates the `production`
   environment (a custom branch policy that allows only `main`) and writes the secrets and variables into it.
   Database passwords, `CONTENT_IMPORT_TOKEN` and `UMAMI_APP_SECRET` are generated locally (40 random letters and
   digits) and only when GitHub does not hold them yet. Answering anything other than `yes` writes nothing and
   withdraws a newly installed deploy key from the server.
7. Set the two values bootstrap cannot: `SECRETS_ADMIN_TOKEN` (needed only by `rotate-db-password`) and, after
   Umami first runs, `UMAMI_WEBSITE_ID` (see "First deployment"). To store a secret by hand, run
   `gh secret set <NAME> --env production --repo <owner>/<repo>`; `gh` reads the value from the terminal, never
   from an argument.
8. Register the redirect URI that bootstrap prints, `https://<domain>/signin-telegram`, with the production bot in
   `@BotFather`.

Bootstrap is idempotent: running it again repairs or updates the host and prints a `changed` or `unchanged` line
per item and a summary.

Without an authenticated `gh`, bootstrap prints the secrets once and the variables as a checklist and you enter
them yourself in Settings, Environments, `production`. On a server that was deployed before (it has `.env`), it
never generates database passwords, `CONTENT_IMPORT_TOKEN` or `UMAMI_APP_SECRET`, because a new database password
would lock the application out of its database, and it keeps the deploy key already installed: the key you stored
in the first run is still the one in use. On a server that was never deployed it issues a new deploy key, because
an installed key may be the leftover of a run whose private half is gone, and removes the older ones once it is
proven. Pass `-RotateDeployKey` to force a new key; see "Rotating credentials".

## First deployment

Do these in order. The first run starts an empty site: the stack has no content and no analytics yet.

1. Dispatch `deploy-app` on `main` and leave the `ref` input empty, which means the newest commit on `main` whose `ci`
   run succeeded. Expect the job to
   end green and `/health/ready` to answer 200 with an empty list of stacks. A first install skips the pre-deploy
   backup because there is nothing to back up.
2. Run the backup-identity drill before any data exists, so a lost or mistyped identity or read key shows up while
   it costs nothing:
   1. The `trainer` dump must contain a Data Protection key, and `restore.sh` rejects a dump whose key ring is
      empty. Check it: `dc exec -T --user postgres postgres psql -d trainer -tAc 'select count(*) from data_protection_keys'`.
      If it prints `0`, start a Telegram sign-in once from the site's sign-in page (the sign-in state is protected
      with the key ring) and check again.
   2. Start a backup: `ssh -t -p <port> <admin>@<host> sudo systemctl start dit-backup.service`.
   3. Fetch the newest `trainer-<timestamp>-daily.dump.age` from the bucket with the read key, as described in
      "Fetching a dump from the bucket", not from the server's disk, so the off-site copy is what you prove. Copy it
      to the server and place it where `deploy` can read it (the same section shows how).
   4. Restore it into a scratch database with the identity on stdin (see "Backups and restore"):
      `sudo -u deploy bash "$R/restore.sh" "$APP_DIR/staging/<file>" trainer`. It must print row counts and end with
      the scratch database name.
   5. Drop the scratch database with `--drop-scratch` and delete the downloaded file. Record that the drill passed.
3. Check that the content went out. The last step of `deploy-app` dispatches `deploy-content` itself when the live
   release lacks the content of the deployed commit, so on a new server a run is already queued or finished: open
   Actions, wait for `deploy-content` to go green and confirm that the site lists the questions. Dispatch it by hand
   (no inputs) only when that run is missing or failed.
4. Open Umami through a tunnel; it is not reachable from the internet:

   ```bash
   ssh -p <port> -L 3001:127.0.0.1:3001 <admin>@<host>
   ```

   Browse to `http://localhost:3001` and sign in with the default account `admin` and password `umami`. Change that
   password immediately, before anything else. Create a website with your domain and copy its website id (a GUID).
5. Store the id: `gh variable set UMAMI_WEBSITE_ID --env production --repo <owner>/<repo>` and paste it.
6. Dispatch `deploy-app` again with `ref` set to the deployed commit (a manual run always deploys, which re-renders
   `.env`; see "Changing a setting" for reading the commit). The pages now carry
   `/stats/script.js`, and the admin, sign-in and profile pages stay uncounted.
7. Run `ops-check` by hand. Every line must be `ok`. Sign in with the production bot to confirm that
   authentication works end to end.

## Updating host assets

nginx, the systemd units, `sshd` and the firewall are installed by bootstrap only. `deploy-app` uploads the
`deploy/` directory but applies only the scripts, the Compose file and `pg_hba.conf` from it; it never touches
`/etc`. A change under `deploy/nginx`, `deploy/systemd` or `deploy/bootstrap` therefore takes effect only
after a bootstrap re-run.

1. A pull request that touches `deploy/nginx`, `deploy/systemd` or `deploy/bootstrap` says so in its description,
   with the sentence "Needs a bootstrap re-run after merge".
2. After the merge, pull `main` and run the same bootstrap command as in "First-time setup". Existing secrets,
   the deploy key and the backup recipient are kept; the output lists what changed.
3. nginx is tested with `nginx -t` before a reload; a configuration nginx rejects is replaced by the previous
   one and the step fails, so a bad template cannot leave the server without a working site.
4. If a deployed Compose or script change needs no host asset, nothing else is required: `deploy-app` delivers it.

## Interrupted restore

`flags/maintenance` exists. nginx answers 503 for the site and for analytics hits, and the application's maintenance
gate rejects what nginx had already admitted; `deploy-app`, `deploy-content`, `rotate-db-password` and the backups
refuse (exit 3), and `ops-check` fails `guards`. The flag is written by `restore.sh --replace` once the scratch
restore has been validated (so a scratch database always exists when the flag does), right before the writer is
stopped, and it is removed only by a restore that finished. A failure or a kill after it was written leaves it
in place, stops the writer service (`app` for `trainer`, `umami` for `umami`) and prints the commands below
with the real names filled in. There is no automatic rename back: nothing wrote to either database since the flag
appeared, so you can choose without losing data.

1. Read the three names. The file has three lines: the live database `<db>`, the previous one `<db>_old_<ts>` (the
   live database moved aside) and the restored one `<db>_restore_<ts>` (the scratch database):

   ```bash
   cat "$APP_DIR/flags/maintenance"
   ```

   In the commands below, `db`, `old` and `scratch` are those three names, and `psql_postgres` runs one statement
   as the PostgreSQL superuser inside the container, exactly as the script prints it:

   ```bash
   psql_postgres() { sudo -u deploy docker compose --project-directory "$APP_DIR" -f "$APP_DIR/compose.yaml" --env-file "$APP_DIR/.env" exec -T --user postgres postgres psql -d postgres -c "$1"; }
   ```

2. List what exists:

   ```bash
   psql_postgres "SELECT datname FROM pg_database WHERE datname LIKE '<db>%'"
   ```

3. Pick the line that matches the list and run its commands:
   - Only `<db>` and `<scratch>` exist (nothing was swapped): drop the scratch database.

     ```bash
     psql_postgres 'DROP DATABASE "<scratch>"'
     ```

   - Only `<old>` and `<scratch>` exist (the live name was moved aside): go forward by naming the restored
     database back into place,

     ```bash
     psql_postgres 'ALTER DATABASE "<scratch>" RENAME TO "<db>"'
     ```

     or go back by naming the previous one back into place:

     ```bash
     psql_postgres 'ALTER DATABASE "<old>" RENAME TO "<db>"'
     ```

     After going forward with `trainer`, run the migration bundle before the writer starts, as `restore.sh` does
     (a nightly dump is older than a later deployment):
     `dc run --rm -T --no-deps --entrypoint /app/migrate/efbundle app`.

   - `<db>` and `<old>` exist (swapped; the migration or the start failed): go back.

     ```bash
     psql_postgres 'ALTER DATABASE "<db>" RENAME TO "<scratch>"'
     psql_postgres 'ALTER DATABASE "<old>" RENAME TO "<db>"'
     ```

4. Start the writer and remove the flag. The writer is `app` for `trainer` and `umami` for `umami`:

   ```bash
   dc up -d --wait <writer>
   sudo -u deploy rm "$APP_DIR/flags/maintenance"
   ```

5. Clean up the leftover with `restore.sh`, which refuses while the flag exists and so runs only now. A leftover
   `<scratch>` (after going back) is dropped with `--drop-scratch`, a leftover `<old>` (after going forward) with
   `--drop-old`:

   ```bash
   sudo -u deploy bash "$R/restore.sh" --drop-scratch <scratch>
   sudo -u deploy bash "$R/restore.sh" --drop-old <old>
   ```

   A leftover `<db>_restore_*` database makes the next restore refuse until it is dropped.

6. If the interrupted restore was `--replace <dump> umami` over a failed Umami upgrade, finishing by hand is not
   enough: `.umami-recovery` (and a leftover `.deploy-in-progress`) still exist and every `deploy-app` still exits 3,
   because only a completed `restore.sh --replace ... umami` clears them. Once the flag is gone and no
   `umami_restore_*` database is left, run the same restore again to completion with the dump named in
   `.umami-recovery`: `sudo -u deploy bash "$R/restore.sh" --replace <dump> umami`, identity on stdin. It repeats the
   swap (the previous database stays as another `umami_old_*`; drop the extras with `--drop-old`) and clears the guards
   at its end. A `trainer` restore has no such follow-up.
7. Find the cause (the script's stderr and the container logs) before you try the restore again.

`restore.sh` switches `xtrace` off before it reads the age identity and does not switch it on again, so
`bash -x restore.sh` shows nothing of the replace phase. Read the script's own log lines on stderr instead.

## Interrupted rotation

`.rotation-pending` exists: a database password rotation started and did not finish. Until it is finished,
`deploy-app` and `deploy-content` refuse (exit 3), the backups go on, and `ops-check` fails `guards`. The server journals
the new password first and every later step moves towards "new password everywhere", so finishing is always safe
to repeat.

1. Find the pending role without printing the password:

   ```bash
   sudo -u deploy sed -n "s/^ROTATION_ROLE='\([a-z]*\)'\$/\1/p" "$APP_DIR/.rotation-pending"
   ```

2. Dispatch `rotate-db-password` with `role: <that role>` and `mode: finish`. The workflow first checks that the
   role you chose is the pending one and stops with "Nothing was changed" when it is not. Then it converges the
   server, writes the secret (`DB_PASSWORD` for `trainer`, `UMAMI_DB_PASSWORD` for `umami`), commits the journal and
   dispatches `deploy-app` for the commit that runs (`DIT_COMMIT` of `last-good.env`, never the tip of `main`, whose
   `ci` may have failed), without releasing a rollback hold.
3. If the run ends with "The rotation is committed; only dispatching deploy-app failed" (or "only reading the deployed
   commit failed"), dispatch `deploy-app` by hand with `ref` set to the deployed commit, as in "Changing a setting".
   If a rollback hold exists, add `keep_hold: true` to that run: a run without `keep_hold` releases a rollback hold (see "Rolling back"), after which the next push ships the tip that was rolled back.

See "Rotating credentials" for a normal rotation and for what each message of the workflow means.

## Interrupted deployment

`.deploy-in-progress` exists: `deploy-app.sh` was killed after it began to change the server, so the state can mix
two releases. The backups and restores refuse, and `ops-check` fails `guards` once no deployment process is running.

1. Dispatch `deploy-app` with `ref` empty (the newest green commit on `main`) or set to the deployed commit, as in
   "Changing a setting" (a run without `keep_hold` releases a rollback hold (see "Rolling back"), after which the next push ships the tip that was rolled back; with a hold in place pass `keep_hold: true` and the deployed commit as `ref`). The script notices the marker first and restores
   the last good state from `last-good.env` (its configuration, bundle and `pg_hba.conf`), then deploys.
2. If the log says "could not restore the last good state; the marker stays", the repair itself failed. Read
   `logs/deploy-app.log` on the server, fix the cause it names and dispatch again.
3. If the interrupted run had changed the Umami image, the repair leaves Umami stopped, writes `.umami-recovery`
   and exits 3: continue with "Umami upgrade".

## Content import

Content is delivered by `deploy-content` as an immutable release under `content/releases/<sha>`; `content/current`
is switched to it, and the application imports it. The server's answer decides: 200 (imported or unchanged) means
the release is in the database, 422 means the import rejected it and committed nothing.

A run without `rollback` delivers the tip of `main` as fetched at the start of the run: the `ref` input only has to
be a commit on `main` and is a lower bound. A run with `rollback: true` and a `ref` delivers exactly that commit, even
if it is older than the live release; without `rollback`, a tip that is older than the live release is skipped with
a notice. A newer queued run replaces a pending one, which loses nothing because every run delivers the newest
content.

What the server exit code means (the job log prints the message):

| Exit | Meaning | What to do |
|---|---|---|
| 0 | Imported and current. | Nothing. |
| 5 | The content's `schema-version` is newer than the running application supports. The release is kept on disk, `current` is not switched. A run without `rollback` ends green with a warning; a `rollback` run fails. | Nothing for a normal run: `deploy-app` dispatches `deploy-content` after the application that supports it. For a failed `rollback` run, deploy an application that supports that content first. |
| 7 | The content's schema is older than the application supports. Nothing switched. | Fail on purpose: no later deployment can deliver it. Bring the content to a supported schema in a new commit on `main`. |
| 1 with a 422 in the log | The import rejected the release; the issues are printed. `current` is back on the previous release and the rejected release is removed from disk. | Fix the content on `main` and let the next run deliver it. |
| 1 with 401, 403 or 404 in the log | The endpoint refused the call, so no import started; `current` is switched back. | Check that `CONTENT_IMPORT_TOKEN` in `.env` is the token the application runs with (re-run `deploy-app` after changing it). |
| 6 | No definitive answer twice. `current` stays on the new release and `.import-pending` is set, because an import may still be reading it. | Run `deploy-content` again; see below. |
| 4 | The deployment lock was not obtained in time (a deployment is still running); nothing changed. The job is red on purpose. | Run the workflow again. If you do not, the next `deploy-app` delivers it: its last step compares the live content with the tip of `main` and dispatches this workflow when they differ. |
| 3 | A guard file refuses the delivery. | Follow the section the message names. |
| 2 | Invalid input or an unsafe archive; nothing changed. | Read the message in the log. |

`.import-pending` holds three lines: the release that was being delivered, the target `current` pointed to before,
and the active content hash the application reported before the switch (`none` for an empty database, empty when the
application did not answer). It exists from before the switch until a definitive answer. `ops-check` reports it as a
guard. The next delivery settles it before it does anything else, by importing what `current` points to; the answer
decides the earlier delivery (kept on 200, switched back and removed on 422). `deploy-app` and
`restore.sh --replace <dump> trainer`, which restart the application, settle it differently, before they start the
application, by comparing the third line with the active hash in `content_imports`:

- The hash changed: the earlier import committed although its answer was lost. `current` stays on that release and the
  file is removed.
- The hash is unchanged, or the file has no third line: the import did not commit. While `current` still names the
  pending release, it is switched back to the previous target on the second line (or removed when that line is empty);
  the file is removed and the log says which. The content is then delivered again by the follow-up of `deploy-app` or by
  the next `deploy-content`.
- The previous release directory no longer exists, or the database cannot be read: the script stops with exit 2 and
  leaves `current` and the file as they are.

Whenever one of these stops with exit 2, or the file is malformed (a delivery, a `deploy-app` and a trainer restore all
stop with exit 2): check that `readlink "$APP_DIR/content/current"` names the release you want live, remove the file
with `sudo -u deploy rm "$APP_DIR/.import-pending"` and dispatch `deploy-content`.

## Umami upgrade

Umami is pinned to an exact image digest in `deploy/compose/compose.yaml` (the comment names the version).
Dependabot is told to ignore it, and the `deploy-app` workflow compares the Umami image of the last good bundle
with the new one: an upgrade is a deliberate procedure, because the new image migrates the Umami database when it
starts and there is no way back except a dump taken just before.

1. Take a fresh `umami` backup and check that it arrived off-site:
   `ssh -t -p <port> <admin>@<host> sudo systemctl start dit-backup.service`. (`deploy-app` also takes a pre-deploy backup
   of both databases automatically; this one is the manual safety net.)
2. In a pull request, change the `umami` image reference in `deploy/compose/compose.yaml` to the new release and its
   digest, and the version named in the comment above it. Describe the upgrade in the pull request. The
   `deploy-smoke` workflow runs on it.
3. Merge it. `ci` succeeds and `deploy-app` deploys. The script notes that the Umami image changes, takes the
   pre-deploy backup whose `umami` dump it remembers, and watches the deployment.
4. If the deployment succeeds, sign in to Umami through the tunnel and confirm that the statistics are intact.
5. If it fails after Umami started on the new image, the script rolls back the application and leaves Umami
   stopped. `.umami-recovery` names the pre-deploy dump (`UMAMI_DUMP=<path>`) and every deployment refuses with exit 3
   until the dump is restored:

   ```bash
   cat "$APP_DIR/.umami-recovery"
   sudo -u deploy bash "$R/restore.sh" --replace <dump from .umami-recovery> umami
   ```

   Give the age identity on stdin (see "Backups and restore"). `restore.sh --replace <dump> umami` is the only restore
   that proceeds over `.umami-recovery` (and over a leftover `.deploy-in-progress`). If the marker exists it first puts
   `.env`, `current-bundle` and `pg_hba.conf` back to `last-good.env`, so Umami starts on the pre-upgrade image and not
   on the image of the interrupted deployment; after a verified restore it clears the guard, and only when that image
   runs. Drop the kept database with `--drop-old` once Umami works.

   If the file says `UMAMI_DUMP=none`, the repair of an interrupted run could not read which dump belongs to it. The
   restore does not care which `umami` dump it gets, so choose one: the newest pre-deploy dump taken before the failed
   deployment is the right one (`sudo -u deploy sh -c 'ls -1 /srv/dev-interview-trainer/backups/umami-*-predeploy.dump.age | sort | tail -n 1'`;
   an older file from the bucket also works, see "Fetching a dump from the bucket"). Run the same
   `restore.sh --replace <that file> umami`: the restore clears `.umami-recovery` whatever dump it used.
6. Revert the pin in a pull request and merge it, so the next deployment does not try the upgrade again. If the
   failure happened before Umami started on the new image, no guard exists and the deployment simply rolled back.

## Configuration reference

Where the table says "re-run `deploy-app`", dispatch it with `ref` set to the deployed commit as in "Changing a
setting"; a run without `keep_hold` releases a rollback hold (see "Rolling back"), after which the next push ships the tip that was rolled back (pass `keep_hold: true` while `ops-check` reports `deploy-hold`).

Every name below is stored in the `production` environment of the repository (bootstrap writes them there). The
workflows read variables through `vars.*` and secrets through `secrets.*`. `deploy/scripts/render-env.sh` turns the
values the `deploy` job passes into the server `.env`, exits 2 naming the first missing or invalid variable (never
its value) and rejects any value with a quote or a line break.

| Name | Kind | Purpose | Set by | How to change |
|---|---|---|---|---|
| `APP_IMAGE` | rendered, not stored | The application image, as a digest reference of `ghcr.io/<owner>/dev-interview-trainer`. | `deploy-app` (the build job) | Deploy another commit. |
| `APP_DIR` | variable | Absolute directory of the deployment on the server (letters, digits and `. _ / -`). Default `/srv/dev-interview-trainer`. | Bootstrap | Do not change it on a live server. |
| `PUBLIC_BASE_URL` | variable | Public `https` origin of the site, for canonical URLs and the certificate check. | Bootstrap (`https://<domain>`) | Edit it, then see "Changing a setting". |
| `ADMIN_TELEGRAM_IDS` | variable | Comma-separated Telegram user ids that get the Admin role (at most 1024 characters). | Bootstrap | Edit it, then re-run `deploy-app`; read once at startup. |
| `TELEGRAM_OIDC_CLIENT_ID` | variable | Client ID (the bot id) of the production bot. | Bootstrap | Edit it, then re-run `deploy-app`. |
| `TELEGRAM_OIDC_CLIENT_SECRET` | secret | Client Secret of the production bot from `@BotFather`. | You, at the bootstrap prompt | Reissue it in `@BotFather`, store it, re-run `deploy-app`. |
| `DB_PASSWORD` | secret | Password of the `trainer` database role. | Bootstrap (generated) | Only through `rotate-db-password` with `role: trainer`. |
| `UMAMI_DB_PASSWORD` | secret | Password of the `umami` database role. | Bootstrap (generated) | Only through `rotate-db-password` with `role: umami`. |
| `CONTENT_IMPORT_TOKEN` | secret | Bearer token of the internal import endpoint (at least 32 characters). | Bootstrap (generated) | `-RotateSecret CONTENT_IMPORT_TOKEN`, then re-run `deploy-app`. |
| `UMAMI_APP_SECRET` | secret | Application secret of Umami. | Bootstrap (generated) | `-RotateSecret UMAMI_APP_SECRET`, then re-run `deploy-app`. |
| `UMAMI_WEBSITE_ID` | variable, optional | The Umami website id (a GUID). Without it no analytics script is rendered. | You, after Umami first runs | Edit it, then re-run `deploy-app`. |
| `RATE_LIMIT_WRITES_PER_MINUTE` | variable, optional | Writes per signed-in user per minute; becomes `App__RateLimits__WritesPerMinute`. Unset keeps the default. | You | Edit it, then re-run `deploy-app`. |
| `RATE_LIMIT_READS_PER_MINUTE` | variable, optional | Reads per minute; becomes `App__RateLimits__ReadsPerMinute`. | You | As above. |
| `RATE_LIMIT_SEARCH_PER_MINUTE` | variable, optional | Catalog searches per client address per minute; becomes `App__RateLimits__SearchPerMinute`. | You | As above. |
| `FEEDBACK_REPORTS_PER_DAY` | variable, optional | Question reports one user may file per 24 hours; becomes `App__Feedback__ReportsPerDay`. | You | As above. |
| `BACKUP_S3_PROVIDER` | variable | The `rclone` S3 provider name of the bucket: `Other` for Backblaze B2. | You, at the bootstrap prompt | Edit it, then re-run `deploy-app`. |
| `BACKUP_S3_ENDPOINT` | variable | Endpoint URL of the S3-compatible API, for example `https://s3.us-west-004.backblazeb2.com`. | You, at the bootstrap prompt | As above. |
| `BACKUP_S3_BUCKET` | variable | Bucket that receives the encrypted dumps. | You, at the bootstrap prompt | As above. |
| `BACKUP_S3_ACCESS_KEY_ID` | secret | Application key id: list and write, no delete. | You, at the bootstrap prompt | See "Rotating credentials". |
| `BACKUP_S3_SECRET_ACCESS_KEY` | secret | The matching application key. | You, at the bootstrap prompt | See "Rotating credentials". |
| `DEPLOY_HOST` | variable | Host name or address the workflows connect to (the real host, not an `~/.ssh/config` alias). | Bootstrap | Edit it after a server move; see "Rebuilding a lost server". |
| `DEPLOY_USER` | variable | Login user on the server, `deploy`. | Bootstrap | Do not change it. |
| `DEPLOY_SSH_PORT` | variable | Port of the server's `sshd`; the workflows pass it to the `ssh-setup` action and use 22 when it is unset. | Bootstrap (`-SshPort`) | Edit it together with the server's `sshd` configuration, through a bootstrap re-run. |
| `DEPLOY_SSH_KEY` | secret | Private half of the deploy key. | Bootstrap | `-RotateDeployKey`. |
| `DEPLOY_SSH_PUBLIC_KEY` | variable | Public half of the deploy key. Lets a later bootstrap compare key material with what the server holds, instead of names. | Bootstrap | Written together with `DEPLOY_SSH_KEY`. |
| `DEPLOY_KNOWN_HOSTS` | variable | The pinned host key line(s) of the server; the workflows never learn a host key. | Bootstrap | Rewritten by bootstrap after a server move. |
| `SECRETS_ADMIN_TOKEN` | secret | A fine-grained personal access token for this repository with the Environments permission "read and write". `rotate-db-password` uses it to write `DB_PASSWORD` and `UMAMI_DB_PASSWORD`, which `GITHUB_TOKEN` cannot do. | You, by hand | Create a new token, `gh secret set SECRETS_ADMIN_TOKEN --env production`, revoke the old one. |

`DEPLOY_KNOWN_HOSTS` is a variable, not a secret: a host key is public, and the `ssh-setup` action and every
workflow read it through `vars.DEPLOY_KNOWN_HOSTS`. Design section 8.7.3 lists it among the secrets; the code is
authoritative. That section also lists fewer names than the table above, because the S3 settings, Umami and the
SSH port were added by Plan 3 ([ADR 0020](adr/0020-deploy-serialisation.md),
[ADR 0021](adr/0021-encrypted-offsite-backups.md)).

## Changing a setting

1. Edit the variable or secret in GitHub: Settings, Environments, `production`, or
   `gh variable set <NAME> --env production --repo <owner>/<repo>` and
   `gh secret set <NAME> --env production --repo <owner>/<repo>` (the value is read from the terminal).
2. Read the commit that runs and dispatch `deploy-app` with that SHA as `ref`:

   ```bash
   ssh -p <port> <admin>@<host> sudo grep '^DIT_COMMIT=' /srv/dev-interview-trainer/last-good.env
   ```

   A manual run of the deployed commit always deploys, so it re-renders `.env` and recreates the services whose
   configuration changed, and nothing else moves. With `ref` empty the run deploys the newest commit on `main` whose
   `ci` succeeded, which may carry code changes besides the setting. The log of `deploy-app.sh` is on the server in
   `logs/deploy-app.log`.

   Warning: a run without `keep_hold` releases a rollback hold (see "Rolling back"), after which the next push ships the tip that was rolled back. While a hold exists (`ops-check` reports it as `deploy-hold`), dispatch with
   `keep_hold: true` and `ref` set to the deployed commit; the run fails at once when `ref` is empty or is another
   commit.
3. Do not change `DB_PASSWORD` or `UMAMI_DB_PASSWORD` this way. A rendered password that differs from the running
   configuration makes `deploy-app.sh` exit 2 with "passwords change only through rotate-db-password", because
   changing the secret in GitHub does not change the database.
4. If `render-env.sh` exits 2, the job log names the variable that is missing or invalid; fix that one and
   dispatch again.

## Rotating credentials

1. **Deploy key.** Run the bootstrap command from "First-time setup" with `-RotateDeployKey`. It creates a new key
   pair, appends the public key to the server, proves a login with it, stores `DEPLOY_SSH_KEY` and
   `DEPLOY_SSH_PUBLIC_KEY` in GitHub after your `yes`, and only then removes the older keys from the server. If you
   answer anything but `yes`, the new key is withdrawn again. Without an authenticated `gh` the new key is printed
   and the previous one stays valid until you store the new one and remove its `dit-deploy-` line from
   `/home/deploy/.ssh/authorized_keys`.
2. **Import token and Umami application secret.** These two are regenerated in GitHub by bootstrap and then take
   effect through a deployment. `-RotateSecret` needs an authenticated `gh`, and the other mandatory bootstrap
   parameters are still required:

   ```powershell
   pwsh deploy/bootstrap/Bootstrap.ps1 -HostName <host> -AdminUser <admin> -Domain <domain> `
     -AdminTelegramIds <id> -LetsEncryptEmail <email> -Repository <owner>/<repo> -RotateSecret CONTENT_IMPORT_TOKEN
   ```

   Then dispatch `deploy-app` as in "Changing a setting". `-RotateSecret UMAMI_APP_SECRET` works the same way, and Umami restarts with the new
   secret. `-RotateSecret DB_PASSWORD` and `-RotateSecret UMAMI_DB_PASSWORD` are refused: use the next item.
3. **Database passwords.** Dispatch `rotate-db-password` with `role: trainer` (`DB_PASSWORD`) or `role: umami`
   (`UMAMI_DB_PASSWORD`) and `mode: rotate`. The workflow checks `SECRETS_ADMIN_TOKEN` first, generates a 48-character
   value, journals it on the server (`.rotation-pending`), sets the role's password, rewrites `.env` and
   `last-good.env`, recreates the service, proves that the role logs in with the new password, writes the GitHub
   secret (three attempts), commits the journal and dispatches `deploy-app` for the commit that runs (read from
   `DIT_COMMIT` of `last-good.env`, so a commit whose `ci` failed never reaches production this way). Only that running
   commit is redeployed: a newer green commit whose automatic deployment was refused while the journal existed waits for
   the next `ci` completion or a manual run. A rollback hold stays in place (`keep_hold`). It only rolls forward; there is no revert.

   After a run that did not finish, dispatch it again with `mode: finish` and the same role. What the messages mean:

   | Message | Meaning and next step |
   |---|---|
   | The first step fails on the secrets token | `SECRETS_ADMIN_TOKEN` cannot write environment secrets. Nothing was changed; fix the token. |
   | "The rotation was refused before anything was journaled (exit 2)" | Nothing changed. Fix the cause and run `mode: rotate` again. |
   | "A guard file refuses the rotation" (exit 3) | Another guard exists. If `.rotation-pending` exists, run `mode: finish` for the pending role; otherwise follow the section the server message names. |
   | "The connection to the server failed (ssh exit 255)" | The state is unknown. Run it again; if a rotation was journaled meanwhile, the server refuses and points to `mode: finish`. |
   | "The rotation did not finish on the server" | Run `mode: finish` with the same role. If it reports that no rotation is pending, nothing was journaled and `mode: rotate` can be run again. |
   | "The pending rotation is for X, not Y. Nothing was changed." | The role you chose does not match the journal. Run `mode: finish` for X. |
   | "`DB_PASSWORD` was not confirmed" (or the Umami name) | The server keeps the pending rotation and refuses deployments. Run `mode: finish` with the same role. |
   | "The rotation is committed; only dispatching deploy-app failed" (or "only reading the deployed commit failed") | The rotation is complete. Dispatch `deploy-app` by hand with `ref` set to the deployed commit ("Changing a setting"). |

4. **S3 key.** Create a new application key at the provider with the same restrictions (the bucket only, list and
   write, no delete). Store `BACKUP_S3_ACCESS_KEY_ID` and `BACKUP_S3_SECRET_ACCESS_KEY` with `gh secret set ... --env
   production`, dispatch `deploy-app` as in "Changing a setting", start a backup (`sudo systemctl start dit-backup.service`) and confirm in the
   bucket that the new files arrived; then delete the old key at the provider.
5. **Backup identity.** Run bootstrap with `-RotateAgeIdentity`. It generates a new identity, shows it once, proves
   the paste and replaces the recipient on the server. New backups are encrypted to the new key; **older backups still
   need the previous identity, so keep both in the password manager** until the last backup made with the old one has
   expired from the bucket.
6. **Telegram client secret.** Reissue it in `@BotFather`, store `TELEGRAM_OIDC_CLIENT_SECRET`, dispatch `deploy-app` as in "Changing a setting".
7. **`SECRETS_ADMIN_TOKEN`.** Create a new fine-grained token, store it, revoke the old one. `rotate-db-password` proves
   the token works before it changes anything.

## Backups and restore

**What is backed up.** `dit-backup.timer` starts `dit-backup.service` daily at 03:30 UTC (with up to 10 minutes of
random delay; a run missed while the server was off starts when it is up again). It runs `backup.sh daily`, and `deploy-app.sh` runs
`backup.sh predeploy` before it changes anything. Each run writes `pg_dump -Fc` of the `trainer` and `umami`
databases, encrypted with `age` to the public key in `backup/recipients.txt`, as
`backups/<db>-<UTC timestamp>-<daily|predeploy>.dump.age` with a plain-text `.meta` sidecar (for `trainer`: the
image, the commit, the last migration, the active content hash and the content release). The `trainer` dump
contains the Data Protection key ring, so a restore brings the sessions back.

**Off-site copy.** Every dump without an `.uploaded` marker is copied to the bucket with `rclone`, configured through
environment variables so the keys never reach a command line. A file gets its marker only after both the dump and
its `.meta` arrived. The daily run fails when a dump or the upload fails; the pre-deploy run fails only when a dump
fails, so an off-site outage never stops a deployment (the file stays pending and the next daily run uploads it).
The server's key cannot read, so the upload never reads an object: `rclone copyto --no-check-dest` skips the lookup of the
destination before it, and `RCLONE_CONFIG_OFFSITE_NO_HEAD=true` skips the check after it (dump names are unique). The
smoke test pins this against a stand-in that answers 403 to reads; the capability set of a real Backblaze key is not
proven by it, so check the first upload on the real bucket (`offsite-age` stays `ok` after the first daily run).

**Guards and backups.** `flags/maintenance` and `.deploy-in-progress` refuse the backup (the databases are in the middle
of a change). A pending rotation and `.umami-recovery` do not: the `trainer` database is dumped regardless. While
`.umami-recovery` exists the `umami` dump is skipped and the log says so, so the newest pre-deploy `umami` dump stays the
one the recovery names.

**Retention.** The server keeps the newest 14 daily and 5 pre-deploy files per database and never deletes a file
that has no `.uploaded` marker. The provider's lifecycle rule expires objects after 30 days. `backups/last-success`
and `backups/last-offsite` are the records the daily check reads.

Start a backup on demand with `ssh -t -p <port> <admin>@<host> sudo systemctl start dit-backup.service` and read its result
with `sudo journalctl -u dit-backup.service -n 50`.

**Fetching a dump from the bucket.** The server's key cannot read, so restoring needs either the provider's web console
(no key: download the file and its `.meta`) or the read key from your password manager. The bucket holds a flat list of
objects in its root: `<db>-<UTC yyyyMMddTHHmmssZ>-<daily|predeploy>.dump.age` and, beside each, the same name plus
`.meta` (for example `trainer-20261115T033000Z-daily.dump.age` and `trainer-20261115T033000Z-daily.dump.age.meta`). The
timestamp sorts like the name, so the newest dump of a database is the last name of its listing. From your PC, with
`rclone`, configured through environment variables so that neither key appears in a file, in the
command line or in your shell history (`Read-Host` input is not recorded; the bucket name and the endpoint are not
secret and may appear):

```powershell
$env:RCLONE_CONFIG_OFFSITE_TYPE = 's3'
$env:RCLONE_CONFIG_OFFSITE_PROVIDER = 'Other'
$env:RCLONE_CONFIG_OFFSITE_ENDPOINT = '<endpoint>'
$env:RCLONE_CONFIG_OFFSITE_NO_CHECK_BUCKET = 'true'
$env:RCLONE_CONFIG_OFFSITE_ACCESS_KEY_ID = Read-Host 'Read key id'
$env:RCLONE_CONFIG_OFFSITE_SECRET_ACCESS_KEY = Read-Host 'Read key' -MaskInput
rclone lsf offsite:<bucket> --include "trainer-*.dump.age"
rclone copyto offsite:<bucket>/<file> ./<file>
rclone copyto offsite:<bucket>/<file>.meta ./<file>.meta
Remove-Item Env:RCLONE_CONFIG_OFFSITE_*
```

On Linux or macOS use `export` and `read -rs` for the two key variables. Then copy the files to the server and place
them for `deploy`:

```bash
scp -P <port> <file> <file>.meta <admin>@<host>:/tmp/
ssh -t -p <port> <admin>@<host> 'sudo install -o deploy -g deploy -m 600 /tmp/<file> /tmp/<file>.meta /srv/dev-interview-trainer/staging/ && rm /tmp/<file> /tmp/<file>.meta'
```

The `.meta` shows what the dump belongs to (`APP_IMAGE`, `DIT_COMMIT`, `CONTENT_RELEASE`, `CREATED_UTC`).

**Restore drill into a scratch database.** A restore always goes into a scratch database first and is validated
there (every table of the dump exists with its rows counted; for `trainer`, the migration history is present and the
key ring is not empty), so a wrong identity or a damaged file changes nothing live. The age identity is given on
stdin, never as a file or an argument; the script strips the carriage returns that a Windows clipboard adds.

1. Put the dump on the server, readable by `deploy` ("Fetching a dump from the bucket").
2. Run it with the identity on stdin. Over `ssh -t`, so that `sudo` can ask for its password:

   ```bash
   ssh -t -p <port> <admin>@<host> 'sudo -u deploy bash /srv/dev-interview-trainer/current-bundle/remote/restore.sh /srv/dev-interview-trainer/staging/<file> trainer'
   ```

   Paste the identity, press Enter, then Ctrl-D. The terminal shows the identity while you paste it: do it where
   nobody watches, and clear the screen afterwards. Only when the admin has passwordless `sudo` (no terminal is
   needed then) can the identity come from the clipboard without being shown:
   `Get-Clipboard | ssh -p <port> <admin>@<host> "sudo -u deploy bash /srv/dev-interview-trainer/current-bundle/remote/restore.sh /srv/dev-interview-trainer/staging/<file> trainer"`.
   Without a terminal `sudo` fails with "a terminal is required"; do not feed its password on stdin.
3. It prints the row counts and, as its last line, the scratch database `<db>_restore_<timestamp>`. Compare the counts
   with what you expect.
4. Drop it: `sudo -u deploy bash "$R/restore.sh" --drop-scratch <db>_restore_<timestamp>`.

**Full restore with `--replace`.** Use it to bring back lost or damaged data. It runs under the deploy lock and
under `flags/maintenance`, so the site answers 503 while it works.

1. Take the dump (the newest `daily` or `predeploy` file for the database, from the server or from the bucket) and
   put it on the server.
2. Run `sudo -u deploy bash "$R/restore.sh" --replace <file> trainer` with the identity on stdin. It restores into
   the scratch database and validates it, writes the flag, stops the writer, renames the live database to
   `<db>_old_<timestamp>` and the scratch database to `<db>`, checks that the row counts equal the validated ones,
   runs the migration bundle (a nightly dump is older than a later deployment), starts the writer, waits for
   readiness, checks that the key ring did not shrink and removes the flag.
3. Do the same with `umami` for the analytics database when it is lost too.
4. The previous database stays as `<db>_old_<timestamp>`. After you have checked the restored site, drop it:
   `sudo -u deploy bash "$R/restore.sh" --drop-old <db>_old_<timestamp>`.
5. If it stops half-way, the flag stays and the script prints the commands; continue with "Interrupted restore".

## Rebuilding a lost server

Do these in this order. You need the DNS name, the repository, GitHub's production environment (it keeps the
database passwords and every other secret), the off-site bucket and the backup identity from your password manager.

1. Create a new VPS and point the DNS record for `<domain>` at it. If `<host>` is the same name or address as before,
   your `known_hosts` still holds the old server's key, ssh refuses with "REMOTE HOST IDENTIFICATION HAS CHANGED", and
   bootstrap would pin that old key into `DEPLOY_KNOWN_HOSTS`, which makes every workflow fail its host key check.
   Remove the old entry first: `ssh-keygen -R <host>` (and `ssh-keygen -R "[<host>]:<port>"` for a port other
   than 22). Then connect once with `ssh -p <port> <admin>@<host>` and compare the fingerprint it shows with the one in the
   provider's console before you accept it. `DEPLOY_KNOWN_HOSTS` must contain only the new server's key afterwards.
2. Run the bootstrap command from "First-time setup" against the new host. The secrets that GitHub holds are kept
   (no database password is regenerated). The new server has no deploy key and no backup recipient, so bootstrap
   issues a new deploy key and a new backup identity: save the new identity next to the old one, because backups
   made on the old server still need the old identity.
3. Make sure `DEPLOY_HOST` and `DEPLOY_KNOWN_HOSTS` name the new server. Bootstrap writes both when you confirm with
   `yes`; without `gh`, set them by hand.
4. Dispatch `deploy-app`. It starts an empty stack and skips the pre-deploy backup, because the new server has no
   previous state.
5. `deploy-app` dispatches `deploy-content` as its last step, so a run is normally already queued or finished: wait
   for it to go green. Dispatch `deploy-content` by hand only when it is missing or failed. Either way the run delivers
   the current `main`, which is what you normally want, because the restore re-imports whatever is on disk. The commit
   a backup was made at is named in its `.meta` file (`CONTENT_RELEASE` for the content, `DIT_COMMIT` for the
   application); to deliver exactly that commit, dispatch by hand with `ref: <sha>` and `rollback: true`.
6. Fetch the dumps with the read key ("Fetching a dump from the bucket"). Pick the newest `trainer` and `umami` dump
   that the old server made: the bucket now also receives dumps of the new, empty server, encrypted to the new
   identity (the daily timer runs at 03:30 UTC, and a pre-deploy backup runs on the next `deploy-app`). Tell them apart
   by the timestamp (before the loss) and by the `.meta`: the old server's `APP_IMAGE` and `DIT_COMMIT` are the old
   release, and `ACTIVE_CONTENT_HASH` is not `none`. A dump of the new server fails to decrypt with the old identity
   ("nothing was changed") and would restore an empty site with the new one.
7. Restore with `restore.sh --replace`, `trainer` first, then `umami`, with the old identity on stdin (the dumps were
   encrypted to it). The script runs the migration bundle and starts the writer, whose startup import re-reads the
   content on disk before the application answers ready, then removes the flag.
8. Check: dispatch `ops-check`, open the site, sign in (the key ring came with the dump, so existing sessions work),
   and start one backup to confirm that the new identity and the bucket work together.

## Rolling back

**Application, the normal way.** Revert the commit on `main` with a pull request. `ci` runs, `deploy-app` deploys the
reverted tree (the newest commit with a green `ci` run), and the schema-compatibility check runs as for any deployment.
If a rollback by dispatch is in place (see the hold below), the automatic run ends with a notice; dispatch `deploy-app`
by hand without `rollback` once the revert is merged.

**Application, a quick rollback by dispatch.** Dispatch `deploy-app` on `main` with `ref: <sha>` (the full 40-character
SHA of the older commit) and `rollback: true`. Without `rollback: true` an older commit is skipped with a notice. Things
to know:

1. A manual run with an explicit `ref` does not check that `ci` succeeded for the commit. The boundary is the
   `production` environment, which only `main` may use; the image build, the smoke test and the compatibility check
   still gate the run.
2. The `deploy/` bundle (Compose file, scripts, the Umami pin) is the one at the tip of `main`, not the one at `ref`:
   the infrastructure moves forward while the application image moves back to the one built from `ref`.
3. Migrations are expand-only and are never reversed, so the older release runs on the newer schema. The
   compatibility check proves it before anything is deployed: for a rollback it runs the older release (`previous` is
   the rollback commit) on the schema of the deployed commit (`current` is the deployed commit).
4. The job then dispatches `deploy-content` without `rollback`, which skips the delivery when the live content is
   newer, so an application rollback never moves content backwards.
5. GitHub keeps one pending run per concurrency group: while a deployment runs, a rollback queued behind it is
   dropped silently when another run is queued after it (for example when a push to `main` finishes `ci`). Check the
   Actions list and dispatch it again.
6. **A rollback holds.** After a successful rollback run the workflow writes `flags/deploy-hold` on the server, and
   every automatic run (after `ci` on a push) ends with the notice "A rollback holds automatic deployments" instead of
   shipping the tip that was rolled back. The hold is a file written over `ssh` because `GITHUB_TOKEN` cannot write
   environment variables. Only a manual run without `rollback` clears it, after its deployment succeeds: merge the revert
   pull request first, then dispatch `deploy-app` with `ref` empty (the newest green commit, which is the revert) or with
   the commit you want. A failed rollback does not set the hold. If the rollback deployed but writing the hold failed,
   the job is red and says "NO HOLD EXISTS": create it by hand (`echo <sha> | sudo -u deploy tee
   /srv/dev-interview-trainer/flags/deploy-hold`) before the next push to `main`. While a hold exists, `ops-check` fails
   `deploy-hold` every day as a reminder. `keep_hold: true` is accepted only with `ref` set to the deployed commit
   (`DIT_COMMIT`); any other combination fails before anything is built. Every manual run without `keep_hold` releases
   the hold, so re-running `deploy-app` for a setting while a rollback is in place needs `keep_hold: true`. To release the hold without deploying, delete the file:
   `sudo -u deploy rm /srv/dev-interview-trainer/flags/deploy-hold`. `rotate-db-password` redeploys the commit that runs
   and leaves the hold alone. Content deliveries are not held.
7. Check the result: `ssh -p <port> <admin>@<host> sudo grep -E '^(DIT_COMMIT|APP_IMAGE)=' /srv/dev-interview-trainer/last-good.env`
   shows the commit and the image that run, and `ops-check` must stay green.

A failed deployment rolls itself back: `deploy-app.sh` restores `last-good.env`, its bundle and `pg_hba.conf` and starts
the previous stack. The job is red, nothing else is required, and `logs/deploy-app.log` says what happened.

**Content.** Dispatch `deploy-content` with `ref: <sha of the older commit>` and `rollback: true`. With `rollback` the
content is exactly that commit, even if it is older than the live release; without it a tip older than the live
release is skipped.

**Data.** There are no down migrations. Damaged data is restored from a backup (see "Backups and restore").

## Daily ops check

`ops-check` runs `ops-check.sh` on the server every day at 07:00 UTC and prints one `ok` or `fail` line per check; the
job fails when any check fails. That failure is the alert, so read the job log. It changes nothing and takes no lock.
It waits up to 20 minutes while `deploy-app.sh`, `receive-content.sh` or `rotate-db-password.sh` is running (the job
timeout is 30 minutes), because readiness and the guard files (`.deploy-in-progress`, `.import-pending`,
`.rotation-pending`) change during one. A marker file without a live process still fails the check.

| Check | `fail` means | What to do |
|---|---|---|
| `readiness` | `/health/ready` does not answer 200. | Run `dc ps` and `dc logs --tail 100 app`; see "Troubleshooting". |
| `backup-identity` | `backup/recipients.txt` is missing, so backups are refused. | Run the bootstrap command again; it installs a recipient after the identity paste is proven. |
| `backup-age` | The last backup run without a failed dump is older than 26 hours or never happened. | Read `sudo journalctl -u dit-backup.service -n 50`; look for `flags/maintenance` or `.deploy-in-progress` (they refuse the backup) or a full disk. |
| `offsite-age` | The last complete off-site copy is older than 26 hours. | The upload fails: check the `BACKUP_S3_*` values and the provider; files without an `.uploaded` marker stay pending and the next daily run retries them. |
| `disk` | The root filesystem is at 85% or more. | See "Troubleshooting", disk full. |
| `certificate` | The certificate of the domain is missing or expires within 14 days. | See "Troubleshooting", certificate renewal. |
| `restarts` | A container restarted more than 3 times since the last deployment. | Read `dc logs --tail 200 <service>`; a crash loop is usually a bad setting or a database problem. |
| `deploy-hold` | `flags/deploy-hold` exists: automatic deployments have been held since a rollback by dispatch. | Merge the revert and run `deploy-app` by hand without `rollback` to release it, or delete the file; see "Rolling back". |
| `guards` | A guard file exists; the line names it and the section to follow. | `flags/maintenance`: "Interrupted restore". `.rotation-pending`: "Interrupted rotation". `.umami-recovery`: "Umami upgrade". `.import-pending`: "Content import". `.deploy-in-progress`: "Interrupted deployment". |

### Weekly link check

`link-check` fetches every source link of the question bank on Mondays at 06:00 UTC. It keeps one open GitHub issue
labelled `dead-links` ("Dead source links") in step with the result: with dead links it creates the issue or updates the
oldest open one (any further open `dead-links` issues are closed as duplicates) and fails the job, so you get the
workflow notification as well; when every link resolves it closes the issue; when the checker itself crashes it fails
without touching the issue. Fix a dead link in the question files the issue lists. The next weekly run, or a manual
run, closes the issue.

## Publishing the static site

`pages` publishes the static site to GitHub Pages after `ci` succeeds on `main`. It decides what to do from the live
`build.json`, so it needs the full history of `main`: it skips a commit that is already live or older than the live one, and
it fails when the live commit is not in that history. That happens after `main` was rewritten (a force-push, or a history
that was squashed), or when the site at the published address (derived from the repository, or `SITE_URL` for a custom domain) was not built from this repository.

To recover, check that the live site is the one you expect, then run the `pages` workflow by hand from `main` with
`allow_unknown_live` ticked. The run publishes the tip of `main` (which needs a green `ci` push run) even though the live
commit is unknown, and the new `build.json` makes later automatic runs work again. Automatic runs ignore the input and keep
failing until then. A manual run without the input is still the way to republish the same commit after changing the custom-domain `SITE_URL`,
`GOATCOUNTER_CODE`, `GOOGLE_SITE_VERIFICATION` or `YANDEX_VERIFICATION`.

## Hardening checklist before the repository goes public

Check each item in the repository settings and in the workflows before the repository becomes public:

1. Third-party actions are pinned by full commit SHA (`.github/dependabot.yml` keeps the pins current, and the
   hardening tests fail on an unpinned action).
2. Every workflow and job sets `permissions:` to the minimum (`contents: read`; `packages: write` only in the job that
   pushes the image).
3. Workflows triggered by pull requests never receive secrets and none uses `pull_request_target`.
4. Secrets reach remote commands through stdin or `env:`, never on a command line, and are never echoed.
5. The `production` environment allows deployments from `main` only. Confirm it in Settings, Environments. Do not add
   required reviewers: they would also gate the scheduled `ops-check` (whose waiting run the next day's run replaces, so
   the alert goes silent), every content delivery and the follow-up dispatched by `deploy-app`. The branch policy is the
   boundary.
6. Dependabot is enabled: version updates through `dependabot.yml`, and alerts and security updates in Settings, Code
   security.
7. Secret scanning and push protection are enabled in Settings, Code security.
8. `SECRETS_ADMIN_TOKEN` is a fine-grained token for this repository only, with the Environments permission and
   nothing else; no personal access token with wider scope is stored anywhere.
9. No `.env`, key, token or connection string is in the git history; the age identity exists only in the password
   manager.

## Troubleshooting

**Readiness stuck.** `deploy-app.sh` waits up to 180 seconds for the services and then for `/health/ready`, and rolls
back when it does not answer.
1. Read the end of the log: `sudo tail -n 100 "$APP_DIR/logs/deploy-app.log"`, and the application's output:
   `dc logs --tail 200 app`.
2. A database connection error usually means a password mismatch between `.env` and the role (see "Changing a
   setting"), or PostgreSQL still starting.
3. A fresh install with no content is ready with an empty site; run `deploy-content` to load the question bank.
4. The job's final message names the exit code: 1 failed and rolled back where possible, 2 invalid input, 3 a guard file
   refuses deployments (follow the section it names).

**Import rejected.** See "Content import": a 422 prints the validation issues in the log of `deploy-content`; the
release is discarded and the previous one stays live. Fix the content on `main`.

**Certificate renewal.** certbot renews through a systemd timer and the deploy hook
`/etc/letsencrypt/renewal-hooks/deploy/dit-reload-nginx.sh` reloads nginx.
1. Check the timer and a dry run: `systemctl list-timers | grep certbot` and `sudo certbot renew --dry-run`.
2. Renewal needs port 80 open and the nginx site serving `/.well-known/acme-challenge/` from `/var/www/certbot`;
   the firewall rule and the site come from bootstrap, so a bootstrap re-run repairs both.
3. After a renewal, reload nginx by hand if the hook did not run: `sudo systemctl reload nginx`.

**Disk full.** `ops-check` fails `disk` at 85%.
1. Find what is large: `df -h /`, `sudo du -sh /var/lib/docker /srv/dev-interview-trainer/* | sort -h`, `docker system df`.
2. Databases kept by a restore (`<db>_old_<timestamp>`) are removed with `restore.sh --drop-old`.
3. `deploy-app` already prunes old bundles and images of this repository; `docker image prune` removes unused
   third-party layers. Container logs are capped at 3 files of 10 MB each.
4. A backup killed by a full disk leaves `.partial` files, which the next run removes. Free space, then start a
   backup by hand and confirm that `ops-check` is green again.
