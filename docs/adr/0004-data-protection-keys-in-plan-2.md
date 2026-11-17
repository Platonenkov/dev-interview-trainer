# ADR 0004: The data protection key ring lives in PostgreSQL from Plan 2

## Status

Accepted. A planner decision: spec 8.7 already requires persistent keys, so only the order of the plans changes; it was not on the owner approval list.

## Context

Spec section 8.7 puts key persistence with hosting, which is Plan 3. Plan 2
already issues two protected cookies, the session ticket (`.dit.session`) and the
antiforgery cookie (`.dit.af`). Without a persistent key ring every restart, and
every deploy restarts the app, would sign everyone out and invalidate every token
in flight. The default store (a folder under the user profile, or memory in a
container) is lost with the container.

## Decision

`AddDataProtection().SetApplicationName("dev-interview-trainer").PersistKeysToDbContext<TrainerDbContext>()`.
`TrainerDbContext` implements `IDataProtectionKeyContext`; the table
`data_protection_keys` comes from the migration `DataProtectionKeys`. A fixed
application name keeps the ring identical across hosts and builds.

The keys are stored unencrypted in that table. This is accepted for a
single-instance app whose database is not exposed outside the host. Plan 3
encrypts database backups, which include the table.

## Consequences

- Sessions and antiforgery tokens survive restarts and deploys, and a second
  instance on the same database reads the same cookies.
- Anyone with read access to the database or an unencrypted backup can forge a
  session cookie. Database access and backups are therefore handled as secrets.
- Rotating the ring (deleting rows) signs every user out; keys expire and roll on
  their own every 90 days.
- If the app ever runs on several hosts or the database becomes reachable from
  elsewhere, add a key-encryption certificate (`ProtectKeysWithCertificate`) and
  revisit this decision.

## Notes added in Plan 3

Backups are encrypted with `age` and copied off-site, so the key ring in the `trainer` dump is no
longer readable without the owner's identity: see [ADR 0021](0021-encrypted-offsite-backups.md).
