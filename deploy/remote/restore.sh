#!/usr/bin/env bash
# Restores an encrypted dump. The age identity (the private key) is read from stdin and never from a file or argv.
#
#   restore.sh <file.dump.age> <db>              restore into a scratch database <db>_restore_<ts>; prints its name
#   restore.sh --replace <file.dump.age> <db>    swap the restored database in for the live one (site-wide maintenance)
#   restore.sh --drop-old <db>_old_<ts>          drop the database a --replace kept
#   restore.sh --drop-scratch <db>_restore_<ts>  drop a scratch database
#
# <db> is trainer or umami. The restore always goes into a scratch database first and is validated there (every table of
# the dump exists with its rows counted; for trainer the migration history is present and the Data Protection key ring is
# not empty), so a wrong identity, a damaged dump or a failing restore leaves the live database untouched.
#
# --replace, under the deploy lock: flags/maintenance (nginx answers 503, the application's maintenance gate rejects what
# nginx had already admitted), stop the writers, rename the live database to <db>_old_<ts>, rename the scratch database
# to <db>, run the migration bundle (trainer: a nightly dump is older than a later deployment), start the writers, check
# readiness, remove flags/maintenance. Any failure or interruption after the flag was written leaves the flag in place: the
# site keeps answering 503 and every deployment refuses (exit 3) until the operator has chosen what to do. There is no
# automatic rename back, because nothing has written to either database since the flag appeared; the script prints the
# exact commands ("Interrupted restore" in docs/deployment.md) and the operator removes the flag by hand.
#
# .import-pending is not a guard here, but it is settled before the application is restarted: --replace trainer keeps a release
# whose import committed and otherwise switches content/current back to the previous release (dit_revert_import_pending), because the startup import would
# otherwise read a release the import may reject and leave the restore in maintenance. restore.sh --replace <dump> umami
# proceeds over .umami-recovery and a leftover .deploy-in-progress; with the marker present it first puts .env,
# current-bundle and pg_hba.conf back to last-good.env (dit_restore_last_good_records), so Umami starts on the pre-upgrade
# image and not on the image of an interrupted deployment, and the guard is cleared only when that image runs.
#
# Exit codes: 0 done; 1 failed; 2 invalid input; 3 a guard file refuses the run.
set -euo pipefail
umask 077

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "$here/lib.sh"
# shellcheck source=deploy-lib.sh
. "$here/deploy-lib.sh"
# shellcheck source=backup-lib.sh
. "$here/backup-lib.sh"
export APP_DIR

# Shortened by the smoke test; production keeps the default.
wait_timeout="${DIT_WAIT_TIMEOUT:-180}"
flag="$APP_DIR/flags/maintenance"

usage() {
  dit_log "usage: restore.sh [--replace] <file.dump.age> <trainer|umami> | --drop-old <db>_old_<ts> | --drop-scratch <db>_restore_<ts>"
  exit 2
}

mode=scratch
file=""
db=""
drop_name=""
case "${1:-}" in
  --replace)
    [ "$#" -eq 3 ] || usage
    mode=replace
    file="$2"
    db="$3"
    ;;
  --drop-old | --drop-scratch)
    [ "$#" -eq 2 ] || usage
    mode="${1#--}"
    drop_name="$2"
    ;;
  -*) usage ;;
  *)
    [ "$#" -eq 2 ] || usage
    file="$1"
    db="$2"
    ;;
esac

case "$mode" in
  drop-old) [[ "$drop_name" =~ ^(trainer|umami)_old_[0-9]{8}T[0-9]{6}Z$ ]] || usage ;;
  drop-scratch) [[ "$drop_name" =~ ^(trainer|umami)_restore_[0-9]{8}T[0-9]{6}Z$ ]] || usage ;;
  *)
    case "$db" in trainer | umami) ;; *) usage ;; esac
    if [ ! -f "$file" ]; then
      dit_log "$file is not a file"
      exit 2
    fi
    case "$file" in *.dump.age) ;; *)
      dit_log "$file is not an encrypted dump (*.dump.age)"
      exit 2
      ;;
    esac
    ;;
esac

# --- state -----------------------------------------------------------------------------------------------------------

# scratch: a scratch database may exist and is dropped on failure; replacing: the flag was written, so the operator decides;
# settled: nothing to undo.
phase=none
ts="$(date -u +%Y%m%dT%H%M%SZ)"
scratch="${db}_restore_${ts}"
old="${db}_old_${ts}"
writer=""
case "$db" in trainer) writer=app ;; umami) writer=umami ;; esac
identity=""
tables=""
validated_counts=""

q() { # a database name that passed the name checks, as an SQL identifier
  printf '"%s"' "$1"
}

sql_postgres() {
  dit_psql postgres -tA -c "$1"
}

database_exists() {
  [ "$(sql_postgres "SELECT 1 FROM pg_database WHERE datname = '$1'")" = "1" ]
}

# The commands of docs/deployment.md "Interrupted restore", with this restore's names filled in.
print_interrupted_help() {
  local psql_cmd="docker compose --project-directory $APP_DIR -f $APP_DIR/compose.yaml --env-file $APP_DIR/.env exec -T --user postgres postgres psql -d postgres -c"
  {
    echo "The restore of $db was interrupted. Follow \"Interrupted restore\" in docs/deployment.md."
    echo "$flag stays: the site answers 503 and every deployment, content delivery and rotation refuses until you remove it."
    echo "Nothing has written to either database since the restore began. The names: live $db, previous $old, restored $scratch."
    echo "List what exists:  $psql_cmd \"SELECT datname FROM pg_database WHERE datname LIKE '${db}%'\""
    echo "  only $db and $scratch exist (nothing was swapped):  $psql_cmd 'DROP DATABASE \"$scratch\"'"
    echo "  only $old and $scratch exist (the live name was moved aside):  $psql_cmd 'ALTER DATABASE \"$scratch\" RENAME TO \"$db\"'  (forward)"
    echo "      or $psql_cmd 'ALTER DATABASE \"$old\" RENAME TO \"$db\"'  (back)"
    echo "  $db and $old exist (swapped; the migration or the start failed):  back: $psql_cmd 'ALTER DATABASE \"$db\" RENAME TO \"$scratch\"'"
    echo "      then $psql_cmd 'ALTER DATABASE \"$old\" RENAME TO \"$db\"'"
    echo "Then start the writers (docker compose ... up -d --wait $writer) and remove the flag: rm $flag"
  } >&2
}

on_exit() {
  local code=$?
  trap - EXIT
  trap '' TERM INT HUP
  case "$phase" in
    scratch)
      if [ "$code" -ne 0 ] && database_exists "$scratch" 2>/dev/null; then
        # "docker compose exec" does not forward a signal to pg_restore inside the container, which may still hold a connection.
        dit_psql postgres -tA -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$scratch'" >/dev/null 2>&1 || true
        dit_psql postgres -c "DROP DATABASE $(q "$scratch")" >/dev/null 2>&1 || dit_log "could not drop $scratch"
      fi
      ;;
    replacing)
      if [ "$code" -ne 0 ]; then
        dit_compose stop "$writer" >/dev/null 2>&1 || dit_log "could not stop $writer"
        print_interrupted_help
        code=1
      fi
      ;;
  esac
  exit "$code"
}
trap on_exit EXIT
trap 'dit_log "interrupted by a signal"; exit 1' TERM INT HUP

# --- helpers ---------------------------------------------------------------------------------------------------------

# The identity is held only in this shell's memory; the process substitution hands it to age without a file or argv.
decrypt() {
  { set +x; } 2>/dev/null
  age -d -i <(printf '%s\n' "$identity") "$file"
}

pg_restore_in_container() {
  dit_compose exec -T --user postgres postgres pg_restore "$@"
}

# "schema.table|rows" per table, one psql session.
row_counts() { # <database> <schema.table>...
  local database="$1" table
  shift
  for table in "$@"; do
    case "$table" in *\"* | *\'*)
      dit_log "refusing the table name $table"
      return 1
      ;;
    esac
    printf "SELECT '%s' || '|' || count(*) FROM \"%s\".\"%s\";\n" "$table" "${table%%.*}" "${table#*.}"
  done | dit_psql "$database" -tA
}

count_of() { # <counts> <schema.table>
  printf '%s\n' "$1" | sed -n "s/^$2|//p"
}

# Test hook, like deploy-app.sh's kill points: with DIT_TEST_HOOKS=1 the script waits while DIT_TEST_PAUSE_FILE exists.
test_pause() {
  local waited=0
  [ "${DIT_TEST_HOOKS:-}" = "1" ] && [ -n "${DIT_TEST_PAUSE_FILE:-}" ] || return 0
  while [ -e "$DIT_TEST_PAUSE_FILE" ] && [ "$waited" -lt 1200 ]; do
    sleep 0.5
    waited=$((waited + 1))
  done
}

# --- guards and lock -------------------------------------------------------------------------------------------------

# The identity is all of stdin and is read before anything else: "docker compose exec" forwards its own stdin to the
# container and would swallow it.
if [ "$mode" != "drop-old" ] && [ "$mode" != "drop-scratch" ]; then
  # Under "bash -x" the assignment would print the key, so tracing is switched off before the key is read and stays off:
  # the repository forbids enabling it in a script, and later lines have no use for it. A key pasted from a Windows
  # password manager arrives with CRLF line ends, which age rejects.
  { set +x; } 2>/dev/null
  identity="$(cat)"
  identity="${identity//$'\r'/}"
  if [ -z "$identity" ]; then
    dit_log "the age identity must be given on stdin"
    exit 2
  fi
fi
exec </dev/null

dit_lock || exit 1
if [ "$mode" = "replace" ] && [ "$db" = "umami" ]; then
  dit_refuse_guards --allow umami-recovery interrupted-deploy
else
  dit_refuse_guards
fi

# --- settle what the restart would trip over -------------------------------------------------------------------------

# An interrupted deployment may have left current-bundle on a release whose Umami image is newer than the dump; starting
# Umami from it would migrate the restored database forward. The records go back to last-good.env first, by the function
# deploy-app.sh's rollback uses.
repair_interrupted_records() {
  if [ ! -f "$APP_DIR/last-good.env" ]; then
    dit_log "an interrupted deployment left its marker and there is no last-good.env to repair to; run deploy-app first"
    exit 1
  fi
  dit_log "an interrupted deployment left its marker; putting .env, current-bundle and pg_hba.conf back to last-good.env before Umami starts"
  if ! dit_restore_last_good_records; then
    dit_log "the records were not restored; nothing was started"
    exit 1
  fi
}

if [ "$mode" = "replace" ] && [ "$db" = "umami" ] && [ -e "$APP_DIR/.deploy-in-progress" ]; then
  repair_interrupted_records
fi
if [ "$mode" = "replace" ] && [ "$db" = "trainer" ]; then
  dit_revert_import_pending || exit 2
fi

# --- drop ------------------------------------------------------------------------------------------------------------

if [ "$mode" = "drop-old" ] || [ "$mode" = "drop-scratch" ]; then
  if ! database_exists "$drop_name"; then
    dit_log "database $drop_name does not exist"
    exit 1
  fi
  dit_psql postgres -c "DROP DATABASE $(q "$drop_name")" >/dev/null
  dit_log "dropped $drop_name"
  exit 0
fi

# --- preflight -------------------------------------------------------------------------------------------------------

if [ -n "$(sql_postgres "SELECT datname FROM pg_database WHERE datname LIKE '${db}\\_restore\\_%'")" ]; then
  dit_log "a scratch database ${db}_restore_* exists; inspect it or remove it with restore.sh --drop-scratch <name>"
  exit 1
fi
if [ "$mode" = "replace" ]; then
  # nginx treats an unreadable flags directory as "no maintenance", and umami has no gate of its own.
  dit_ensure_layout
  dit_check_traversable "$APP_DIR/flags" || exit 2
fi
if [ "$mode" = "replace" ] && ! database_exists "$db"; then
  dit_log "database $db does not exist; there is nothing to replace"
  exit 1
fi
if database_exists "$old"; then
  dit_log "database $old already exists; try again in a second"
  exit 1
fi

# --- scratch restore and validation ----------------------------------------------------------------------------------

# Decrypting and listing first proves the identity and the dump's table of contents before any database is created.
toc="$(decrypt | pg_restore_in_container -l)" || {
  dit_log "the dump cannot be decrypted or read (wrong identity, or a damaged file); nothing was changed"
  exit 1
}
tables="$(printf '%s\n' "$toc" | dit_toc_tables)"
if [ -z "$tables" ]; then
  dit_log "the dump lists no tables; nothing was changed"
  exit 1
fi
if [ "$db" = "trainer" ]; then
  for required in public.ef_migrations_history public.data_protection_keys; do
    if ! grep -qx "$required" <<<"$tables"; then
      dit_log "the trainer dump has no table $required; nothing was changed"
      exit 1
    fi
  done
fi

phase=scratch
dit_psql postgres -c "CREATE DATABASE $(q "$scratch") OWNER $db" >/dev/null
# --no-owner with --role: the objects belong to the application's role, whatever role owned them when the dump was made.
decrypt | pg_restore_in_container -d "$scratch" --no-owner --role="$db" --exit-on-error
# Both decryptions are done; the key has no further use in memory.
unset identity
# shellcheck disable=SC2086 # one table per word; the names were validated by row_counts
validated_counts="$(row_counts "$scratch" $tables)"
dit_log "restored into $scratch; rows per table:"
while IFS= read -r line; do
  dit_log "  $line"
done <<<"$validated_counts"
if [ "$db" = "trainer" ] && [ "$(count_of "$validated_counts" public.data_protection_keys)" = "0" ]; then
  dit_log "the key ring (data_protection_keys) in the dump is empty"
  exit 1
fi

if [ "$mode" = "scratch" ]; then
  phase=settled
  dit_log "validated; drop it with restore.sh --drop-scratch $scratch when it is no longer needed"
  printf '%s\n' "$scratch"
  exit 0
fi

# --- replace ---------------------------------------------------------------------------------------------------------

# The phase changes only once the flag exists: until then a failure has stopped nothing and must not claim a maintenance state.
flag_tmp="$flag.new"
rm -f "$flag_tmp"
# The application runs as another user and reads the flag through a read-only mount; a mode 600 file would be invisible to
# its gate, which treats an unreadable flag as absent. Every step is inside the condition, so a failed write (a full disk
# truncates the file) also removes the partial temp file.
if ! printf '%s\n%s\n%s\n' "$db" "$old" "$scratch" >"$flag_tmp" || ! chmod 644 "$flag_tmp" || ! mv -f "$flag_tmp" "$flag"; then
  rm -f "$flag_tmp"
  dit_log "could not write $flag; nothing was stopped or renamed"
  exit 1
fi
phase=replacing
dit_log "maintenance flag written ($db, $old, $scratch); stopping $writer"

dit_compose stop "$writer"
# Renaming needs the database idle; the writers were the only clients, this only catches stragglers.
dit_psql postgres -tA -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$db' AND pid <> pg_backend_pid()" >/dev/null
dit_psql postgres -c "ALTER DATABASE $(q "$db") RENAME TO $(q "$old")" >/dev/null
dit_psql postgres -c "ALTER DATABASE $(q "$scratch") RENAME TO $(q "$db")" >/dev/null
dit_log "swapped: $db is the restored database, $old the previous one"

# Nothing has run against the swapped database yet, so its counts must be exactly the validated ones.
# shellcheck disable=SC2086 # one table per word
swapped_counts="$(row_counts "$db" $tables)"
if [ "$swapped_counts" != "$validated_counts" ]; then
  dit_log "the row counts after the swap differ from the validated ones"
  exit 1
fi

if [ "$db" = "trainer" ]; then
  # A nightly dump is older than a deployment made later that day.
  dit_compose run --rm -T --no-deps --entrypoint /app/migrate/efbundle app
fi

dit_compose up -d --wait --wait-timeout "$wait_timeout" "$writer"
if [ "$db" = "trainer" ]; then
  curl -fsS --max-time 10 --retry 10 --retry-delay 2 --retry-connrefused --retry-all-errors http://127.0.0.1:8080/health/ready >/dev/null
else
  curl -fsS --max-time 10 --retry 10 --retry-delay 2 --retry-connrefused --retry-all-errors http://127.0.0.1:3001/api/heartbeat >/dev/null
fi
dit_log "$writer is up on the restored database; the maintenance flag is still in place"
test_pause

# A migration newer than the dump may add, rename or drop tables, and the startup import changes rows, so the dump's table list
# means nothing after the start. Only the key ring is checked: sessions depend on it, and it never shrinks.
if [ "$db" = "trainer" ]; then
  ring_now="$(dit_psql trainer -tA -c 'SELECT count(*) FROM data_protection_keys')"
  if ! dit_count_not_below "$(count_of "$validated_counts" public.data_protection_keys)" "$ring_now"; then
    dit_log "the key ring has $ring_now rows after the start, the dump had $(count_of "$validated_counts" public.data_protection_keys)"
    exit 1
  fi
fi

rm -f "$flag"
phase=settled
if [ "$db" = "umami" ] && ! dit_clear_umami_recovery; then
  dit_log "$db was restored and verified and the maintenance flag is gone, but the guard could not be cleared: fix the cause and run dit_clear_umami_recovery (or remove $APP_DIR/.umami-recovery) before the next deployment"
  exit 1
fi
dit_log "restored $db from $(basename "$file"); the previous database is kept as $old (restore.sh --drop-old $old removes it)"
