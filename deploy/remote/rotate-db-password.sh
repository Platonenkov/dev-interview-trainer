#!/usr/bin/env bash
# Rolls the password of one database role (trainer or umami) forward. There is no revert and no attempt to work out which
# value is current: the new value is journaled first, and every later step moves the server towards "new password
# everywhere", so running finish again after a crash at any point converges.
#
#   rotate-db-password.sh <staging-dir> <trainer|umami> begin     (new password on stdin)
#   rotate-db-password.sh <staging-dir> <trainer|umami> finish
#   rotate-db-password.sh <staging-dir> <trainer|umami> commit
#   rotate-db-password.sh <staging-dir> pending-role
#
# begin refuses over any guard file, writes .rotation-pending (role and new password, mode 600, temp file and rename) and
# then runs finish. finish sets the role's password, writes the one .env line in .env and last-good.env, recreates the
# affected service, proves that the role logs in with the new password and waits until the service answers (the application's
# /health/ready, Umami's /api/heartbeat, both liveness only); any failing step exits 1 and keeps the journal. commit deletes the journal and is called only after GitHub holds the new value. Each mode takes
# the deploy lock. While .rotation-pending exists deploy-app.sh, receive-content.sh and backup.sh refuse and ops-check
# fails "guards".
#
# Exit codes: 0 done; 1 failed (any step after the journal exists, whatever the failing command returned: the journal stays and
# finish repeats the work; before the journal exists nothing changed); 2 invalid input, or the pending rotation is for another
# role, nothing changed; 3 a guard file refuses begin.
set -euo pipefail
# The password is held in shell variables below; no trace may ever print it, even when the script is started with "bash -x".
{ set +x; } 2>/dev/null

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "$here/lib.sh"
export APP_DIR

# Shortened by the smoke test; production keeps the default.
wait_timeout="${DIT_WAIT_TIMEOUT:-180}"
env_file="$APP_DIR/.env"
good_file="$APP_DIR/last-good.env"
journal="$APP_DIR/.rotation-pending"

role=""
journal_role=""
journal_password=""

# Used by smoke.sh to stop the run at a named step boundary. Both variables must be set, so a stray DIT_TEST_KILL_AT in
# a production environment does nothing.
kill_point() {
  if [ "${DIT_TEST_HOOKS:-}" = "1" ] && [ "${DIT_TEST_KILL_AT:-}" = "$1" ]; then
    kill -KILL $$
  fi
}

usage() {
  dit_log "usage: rotate-db-password.sh <staging-dir> <trainer|umami> begin|finish|commit"
  dit_log "       rotate-db-password.sh <staging-dir> pending-role"
  exit 2
}

validate_staging() {
  local real_app real_staging
  real_app="$(realpath -e "$APP_DIR")" || exit 2
  if ! real_staging="$(realpath -e "$1" 2>/dev/null)"; then
    dit_log "the staging directory does not exist"
    exit 2
  fi
  case "$real_staging" in
    "$real_app"/staging/*) ;;
    *)
      dit_log "the staging directory must be inside $APP_DIR/staging"
      exit 2
      ;;
  esac
}

check_role() {
  case "$1" in
    trainer | umami) ;;
    *)
      dit_log "the role must be trainer or umami"
      exit 2
      ;;
  esac
}

# The value ends up in .env between single quotes, in a Compose environment entry, in a connection string and in a URL
# (Umami's DATABASE_URL), so only characters that need no quoting in any of them are accepted.
check_password() {
  [[ "$1" =~ ^[A-Za-z0-9._~-]{32,128}$ ]]
}

env_name_of() {
  case "$1" in
    trainer) echo DB_PASSWORD ;;
    umami) echo UMAMI_DB_PASSWORD ;;
  esac
}

service_of() {
  case "$1" in
    trainer) echo app ;;
    umami) echo umami ;;
  esac
}

read_journal() {
  if [ ! -f "$journal" ]; then
    dit_log "no rotation is pending"
    exit 2
  fi
  journal_role="$(dit_env_get "$journal" ROTATION_ROLE)" || exit 1
  journal_password="$(dit_env_get "$journal" ROTATION_PASSWORD)" || exit 1
  check_role "$journal_role"
  if ! check_password "$journal_password"; then
    dit_log "the password in $journal is not a valid rotation password"
    exit 1
  fi
}

# Before any change: a journal for one role must never be finished or committed under the name of the other, which would
# publish the wrong secret.
require_pending_role() {
  read_journal
  if [ "$journal_role" != "$role" ]; then
    dit_log "pending rotation is for $journal_role"
    exit 2
  fi
}

write_journal() {
  local password="$1" tmp
  rm -f "$journal".*
  tmp="$(umask 077 && mktemp "$journal.XXXXXX")" || return 1
  if ! printf "ROTATION_ROLE='%s'\nROTATION_PASSWORD='%s'\n" "$role" "$password" >"$tmp" || ! chmod 600 "$tmp" || ! mv -f "$tmp" "$journal"; then
    rm -f "$tmp"
    return 1
  fi
}

# The password travels through the exec environment and psql's \getenv, never argv or a script text. psql substitutes :'pw'
# itself, so the server receives the plaintext statement and would write it to its log on any error (log_min_error_statement
# defaults to "error"); both settings are switched off in the same session, before the statement. They are superuser-only,
# which is what this session is.
alter_role() {
  printf '%s\n' "SET log_min_error_statement = panic;" "SET log_statement = 'none';" '\getenv pw ROTATION_PW' "ALTER ROLE $role PASSWORD :'pw';" |
    ROTATION_PW="$journal_password" dit_compose exec -T --user postgres -e ROTATION_PW postgres psql -v ON_ERROR_STOP=1 -d postgres >/dev/null
}

# Proves the role itself accepts the journaled password, from inside the postgres container to its own address in the Compose
# subnet (the only place pg_hba.conf admits the application roles). A heartbeat cannot prove this: it only shows the process runs.
verify_login() {
  # shellcheck disable=SC2016 # the script text is for the shell inside the container
  PGPASSWORD="$journal_password" dit_compose exec -T --user postgres -e PGPASSWORD postgres \
    sh -c 'exec psql -h "$(hostname -i)" -U "$1" -d "$1" -tAc "select 1"' _ "$role" >/dev/null </dev/null
}

# Liveness only: the service answers. The login proof above is what shows the new password works.
wait_for_readiness() {
  local url deadline=$((SECONDS + wait_timeout))
  case "$role" in
    trainer) url="http://127.0.0.1:8080/health/ready" ;;
    umami) url="http://127.0.0.1:3001/api/heartbeat" ;;
  esac
  until curl -fsS --max-time 10 "$url" >/dev/null 2>&1; do
    if [ "$SECONDS" -ge "$deadline" ]; then
      dit_log "$url did not answer 200 within ${wait_timeout}s"
      return 1
    fi
    sleep 2
  done
}

# A step failure after the journal exists is exit 1 whatever the failing command returned (psql says 3, dit_env_set 2, compose
# its own codes): 2 and 3 mean "nothing was changed" to the caller.
fail_step() {
  dit_log "$1 failed; the journal stays, run finish again"
  exit 1
}

# Every step leaves the server closer to "new password everywhere", so this is safe to run again from any crash point.
run_finish() {
  local name service
  name="$(env_name_of "$role")"
  service="$(service_of "$role")"
  if [ ! -f "$env_file" ]; then
    dit_log "$env_file does not exist; nothing to rotate"
    exit 1
  fi

  dit_log "setting the password of role $role"
  alter_role || fail_step "setting the password of role $role"
  kill_point after-alter-role
  printf '%s\n' "$journal_password" | dit_env_set "$env_file" "$name" || fail_step "writing $name to .env"
  kill_point after-env
  if [ -f "$good_file" ]; then
    printf '%s\n' "$journal_password" | dit_env_set "$good_file" "$name" || fail_step "writing $name to last-good.env"
  fi
  kill_point after-last-good

  # --no-deps: the postgres service renders DB_PASSWORD too, and recreating the database server is no part of a rotation.
  dit_log "recreating $service"
  dit_compose up -d --wait --wait-timeout "$wait_timeout" --no-deps "$service" || fail_step "recreating $service"
  verify_login || fail_step "logging in as $role with the new password"
  wait_for_readiness || fail_step "waiting for $service"
  dit_log "role $role has the new password everywhere on the server; the journal stays until commit"
}

do_begin() {
  local password
  dit_refuse_guards
  IFS= read -r password || true
  if ! check_password "$password"; then
    dit_log "the new password must be 32 to 128 characters of A-Z a-z 0-9 . _ ~ -"
    exit 2
  fi
  if [ ! -f "$env_file" ]; then
    dit_log "$env_file does not exist; nothing to rotate"
    exit 1
  fi
  # Written before anything changes: from here on a crash is resolved by finish.
  write_journal "$password" || exit 1
  journal_role="$role"
  journal_password="$password"
  dit_log "rotation of role $role is journaled"
  kill_point after-journal
  run_finish
}

do_finish() {
  dit_refuse_guards --allow rotation
  require_pending_role
  run_finish
}

# Removing the guard while the server still holds the old value would unblock deployments that render the new secret over
# a database that rejects it; the check makes commit safe to call only after a finish that completed.
do_commit() {
  require_pending_role
  if [ "$(dit_env_get "$env_file" "$(env_name_of "$role")")" != "$journal_password" ]; then
    dit_log "the rotation of $role has not finished on this server; run finish first"
    exit 1
  fi
  rm -f "$journal"
  dit_log "rotation of role $role is committed"
}

do_pending_role() {
  read_journal
  printf '%s\n' "$journal_role"
}

if [ "$#" -eq 2 ] && [ "$2" = "pending-role" ]; then
  mode="pending-role"
elif [ "$#" -eq 3 ]; then
  role="$2"
  mode="$3"
  check_role "$role"
else
  usage
fi
validate_staging "$1"
dit_lock || exit 1

case "$mode" in
  begin) do_begin ;;
  finish) do_finish ;;
  commit) do_commit ;;
  pending-role) do_pending_role ;;
  *) usage ;;
esac
