# ADR 0019: Least-privilege database roles; the superuser is reachable only on the server

## Status

Accepted. A design decision of Plan 3, taken by the designer inside the scope of the
deployment section of the spec; no spec text changes.

## Context

One PostgreSQL server holds two databases: the application's `trainer` and Umami's `umami`
(ADR 0018). The application must not hold superuser rights, Umami must not see the
trainer data, and a leaked application password must not give access to the server.

## Decision

- Two roles, `trainer` and `umami`, each `LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE` and
  the owner of its own database. `ensure-databases.sh` creates a role only when it does not
  exist, so a changed `.env` value never rewrites a password; passwords change only through
  the rotation script (spec 8.7.2).
- `pg_hba.conf` has three rules: `local all postgres peer`, and `scram-sha-256` for
  `trainer` on `trainer` and for `umami` on `umami`, both from the Compose subnet
  `172.30.0.0/24` only. Network logins as `postgres` are rejected.
- The superuser is reached only with `docker compose exec --user postgres` on the server.
  The image's default exec user is root, and `peer` needs the operating-system user
  `postgres`, so every administrative call goes through `dit_psql` or `dit_pg_dump`.
- PostgreSQL publishes no port. The rules directory is mounted read-only as a directory,
  because a file bind mount keeps the inode it saw at start; each deployment copies its
  bundle's `pg_hba.conf` there by rename and reloads the configuration when it changed.
- Passwords of the two roles are limited to letters, digits and `. _ ~ -`, because they are
  embedded in a connection string and a URL.

## Consequences

- A compromised application can read and write only `trainer`; Umami cannot read it.
- Administration needs a shell on the server; there is no remote superuser path.
- A rollback restores the previous bundle's rules together with its containers.
- The first start needs `pg_hba.conf` in place before PostgreSQL starts.
