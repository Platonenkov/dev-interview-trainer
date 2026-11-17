# ADR 0021: Encrypted off-site backups and a guarded restore

## Status

Accepted by the owner on 2026-11-15. A design decision of Plan 3 that adds a third-party service
(the off-site provider) and so needed the owner's approval. It completes ADR 0004, which left the
Data Protection key ring unencrypted at rest until backups were encrypted.

## Context

The `trainer` database holds every account, answer and the Data Protection key ring, and the `umami`
database holds the analytics. Both live in one PostgreSQL volume on one VPS, so a lost disk, a bad
migration or a mistaken `DROP` ends the site unless a copy exists elsewhere. A copy that is readable
by its custodian is a copy of every session key (ADR 0004), so the copy must be encrypted with a key
the server cannot use. A deployment that replaces the Umami image can corrupt the Umami database
without any way back except a dump taken just before it.

## Decision

1. `backup.sh` runs `pg_dump -Fc` for `trainer` and `umami` and encrypts each dump with `age` to the
   recipients in `$APP_DIR/backup/recipients.txt`. Only public keys are on the server. The identity
   is generated on the administrator's PC by the bootstrap script and confirmed there before the
   recipient is installed (Task 16), and it is kept in the owner's password manager. `restore.sh`
   receives it on stdin and never writes it anywhere. A missing recipients file refuses the backup
   (exit 4).
2. Each dump gets a plain-text sidecar that is not secret: for `trainer` the application image, the
   commit, the newest row of `ef_migrations_history`, the active content hash and the live content
   release; for `umami` the Umami image reference only.
3. The off-site copy is mandatory. The provider is Backblaze B2 through its S3-compatible API,
   addressed with `rclone` configured only by `RCLONE_CONFIG_OFFSITE_*` environment variables read
   from `.env`. The application key can list and write but not delete, so a compromised server cannot
   erase its own history; the bucket's lifecycle rule expires objects after 30 days. A file carries a
   `.uploaded` marker once it has arrived, and local retention (14 daily and 5 pre-deploy files per
   database) never deletes a file without the marker, so an outage never leaves the only copy
   unprotected.
4. `deploy-app.sh` takes a pre-deploy backup whenever `last-good.env` exists and aborts before it
   touches the stack when the dump fails. An unreachable bucket does not stop a deployment (the file
   stays pending); a failed dump does. The daily timer fails when either the dump or the upload fails.
   `ops-check.sh` reports stale backups, a missing recipient, the guard files and the other signs of
   an unhealthy host once a day.
5. The Data Protection key ring is part of the `trainer` dump (ADR 0004), so restoring a dump brings
   the sessions back with it.
6. `restore.sh` always restores into a scratch database first and validates it (every table of the
   dump present with its rows counted, the migration history and a non-empty key ring for `trainer`),
   so a wrong identity or a damaged file changes nothing. `--replace` then runs under site-wide
   maintenance: `flags/maintenance` makes nginx answer 503 and the application's maintenance gate
   reject what nginx had already admitted, the writers stop, the live database is renamed aside, the
   restored one takes its name, migrations run and the writers start. A failure or an interruption at
   any point leaves the flag in place, so the site keeps answering 503 and every deployment refuses,
   and the script prints the exact commands for the operator. There is no automatic rename back:
   nothing has written to either database since the flag appeared, so the operator's choice loses
   nothing. The old database stays until `restore.sh --drop-old`.

## Consequences

- Recovery depends on the identity in the owner's password manager. Losing it makes every backup
  unreadable, and the bootstrap confirmation step exists to catch a lost copy before it matters.
- A Backblaze B2 account and an application key without delete rights are a standing dependency.
  Thirty-day expiry bounds both the cost and how far back a restore can reach.
- Restoring an old dump into a newer release is safe because the migration bundle runs after the
  swap and the startup import settles the content on disk.
- Decrypting needs the identity twice (once to list the dump, once to restore it), so the script keeps
  it in memory for the run instead of passing it through a file.
- Restoring reads the bucket with a key the owner holds, never with the server's key: that key cannot read, and a
  read key stored on the server would let a compromised server read its own history. The owner keeps a list-and-read
  application key beside the age identity and uses it from the administrator's PC (`docs/deployment.md`, "Fetching a
  dump from the bucket").
- The restore script is the only code that renames databases, which keeps the dangerous step in one
  place that the smoke test exercises, including the failure path.
