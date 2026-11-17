#!/usr/bin/env bash
# Encrypted backups of both databases, copied off-site.
#
#   backup.sh daily|predeploy
#
# For each database (trainer, umami): pg_dump -Fc, encrypted with age to the recipients in $APP_DIR/backup/recipients.txt
# (only the public key is on the server), written as $APP_DIR/backups/<db>-<UTC yyyyMMddTHHmmssZ>-<kind>.dump.age with a
# plain-text <name>.meta beside it, then every file without a ".uploaded" marker is copied to the bucket. Retention keeps
# 14 daily and 5 predeploy files per database and never deletes a file that was not confirmed uploaded.
#
# While .umami-recovery exists the umami database is not dumped (the log says so): it is the database a failed upgrade may have
# migrated, and a dump of it would become the "newest" file the recovery must not pick. The trainer database is dumped
# regardless. A skipped dump is not a failure.
#
# "daily" (the systemd timer) fails when a dump or the upload fails. "predeploy" (deploy-app.sh, before it touches the
# stack) fails only when a dump fails: an off-site outage must not stop a deployment, and the file stays pending. It
# prints the path of the umami dump as the LAST line of stdout; everything else goes to stderr.
#
# Exit codes: 0 done; 1 a dump (or, for daily, the upload) failed; 2 invalid input; 3 a guard file refuses the run;
# 4 no recipients file.
#
# Locking: an entry script takes the deploy lock itself. When deploy-app.sh starts this script it has already inherited
# the open lock descriptor, which dit_lock recognises, so the lock is not taken twice.
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

if [ "$#" -ne 1 ]; then
  dit_log "usage: backup.sh daily|predeploy"
  exit 2
fi
kind="$1"
case "$kind" in daily | predeploy) ;; *)
  dit_log "usage: backup.sh daily|predeploy"
  exit 2
  ;;
esac

backups="$APP_DIR/backups"
recipients="$APP_DIR/backup/recipients.txt"
env_file="$APP_DIR/.env"

dit_lock || exit 1
# A backup of a state that mixes two releases, or of a database in the middle of a restore, would be worse than none, so
# flags/maintenance and .deploy-in-progress refuse. A pending rotation and an Umami recovery leave the trainer database
# consistent while the site keeps taking writes, so they must not stop its backup for days.
dit_refuse_guards --allow rotation umami-recovery
if [ ! -s "$recipients" ]; then
  dit_log "$recipients is missing or empty; backups are refused until a recipient is installed"
  exit 4
fi
install -d -m 700 "$backups"
# Under the lock nothing can be writing one, so any ".partial" is the leftover of a killed run.
rm -f "$backups"/*.partial "$backups"/.tmp.*

trap 'rm -f "$backups"/*.partial' EXIT

write_file() { # <path>; content from stdin, replaced by rename
  local path="$1" tmp
  tmp="$(mktemp "$backups/.tmp.XXXXXX")" || return 1
  if ! cat >"$tmp" || ! mv -f "$tmp" "$path"; then
    rm -f "$tmp"
    return 1
  fi
}

utc_now() {
  date -u +%Y-%m-%dT%H:%M:%SZ
}

# --- sidecar ---------------------------------------------------------------------------------------------------------

env_or_unknown() { # <name>
  local value
  value="$(dit_env_get "$env_file" "$1" 2>/dev/null)" || value=""
  printf '%s\n' "${value:-unknown}"
}

trainer_meta() { # <ts> -> sidecar text
  local migration hash release version
  migration="$(dit_psql trainer -tA -c 'SELECT migration_id FROM ef_migrations_history ORDER BY migration_id DESC LIMIT 1' 2>/dev/null </dev/null | tr -d '[:space:]')" || migration=""
  # An empty site has no active content: "none" says so, "unknown" says the application did not answer.
  if version="$(curl -fsS --max-time 5 http://127.0.0.1:8081/internal/version 2>/dev/null)"; then
    hash="$(printf '%s' "$version" | sed -n 's/.*"activeContentHash":"\([^"]*\)".*/\1/p')"
    hash="${hash:-none}"
  else
    hash="unknown"
  fi
  release="$(basename "$(readlink "$APP_DIR/content/current" 2>/dev/null || echo none)")"
  dit_backup_meta_trainer "$(env_or_unknown APP_IMAGE)" "$(env_or_unknown DIT_COMMIT)" "${migration:-unknown}" "$hash" "$release" "$1" "$kind"
}

umami_meta() { # <ts> -> sidecar text
  local image
  image="$(dit_umami_image "$APP_DIR/compose.yaml" 2>/dev/null)" || image="unknown"
  dit_backup_meta_umami "$image" "$1" "$kind"
}

# --- dump ------------------------------------------------------------------------------------------------------------

# Prints the final path on success. The meta is written before the dump is renamed into place: a dump file never exists
# without its sidecar, and a leftover sidecar of a failed run is harmless.
dump_database() { # <db> <ts>
  local db="$1" ts="$2" name final partial header
  local -a statuses
  name="$(dit_backup_name "$db" "$ts" "$kind")" || return 1
  final="$backups/$name"
  partial="$final.partial"
  # Both stages are checked by their own status, whatever the caller's errexit context is.
  set +e
  dit_pg_dump "$db" -Fc </dev/null | age -R "$recipients" >"$partial"
  statuses=("${PIPESTATUS[@]}")
  set -e
  if [ "${statuses[0]}" -ne 0 ] || [ "${statuses[1]}" -ne 0 ]; then
    rm -f "$partial"
    dit_log "the dump of $db failed (pg_dump ${statuses[0]}, age ${statuses[1]})"
    return 1
  fi
  # age writes its header even for empty input, so size proves nothing; the header proves that age produced the file.
  header="$(head -n 1 "$partial" | tr -d '\000')"
  if [ "$header" != "age-encryption.org/v1" ]; then
    rm -f "$partial"
    dit_log "the dump of $db is not an age file"
    return 1
  fi
  if ! "${db}_meta" "$ts" | write_file "$final.meta"; then
    rm -f "$partial" "$final.meta"
    dit_log "could not write the sidecar of $db"
    return 1
  fi
  if ! mv -f "$partial" "$final"; then
    rm -f "$partial"
    return 1
  fi
  dit_log "wrote $name"
  printf '%s\n' "$final"
}

# What an upload pass considers: a daily run everything not yet marked, a predeploy run only its own files, so a long
# off-site outage cannot make a deployment wait for a backlog while it holds the lock.
upload_candidates() {
  if [ "$kind" = "predeploy" ]; then
    if [ "${#written[@]}" -gt 0 ]; then
      printf '%s\n' "${written[@]}"
    fi
  else
    dit_backup_pending "$backups"
  fi
}

unique_timestamp() {
  local ts
  while :; do
    ts="$(date -u +%Y%m%dT%H%M%SZ)"
    if [ ! -e "$backups/$(dit_backup_name trainer "$ts" "$kind")" ] && [ ! -e "$backups/$(dit_backup_name umami "$ts" "$kind")" ]; then
      printf '%s\n' "$ts"
      return 0
    fi
    sleep 1
  done
}

# --- off-site --------------------------------------------------------------------------------------------------------

# rclone is configured only through RCLONE_CONFIG_OFFSITE_* variables of a subshell: the keys never reach a command line
# or a file, and "rclone copyto" at its default log level does not print them. The application key may list and write but
# neither read nor delete, and cannot create buckets, so rclone must never read an object: no_check_bucket skips the bucket
# lookup, no_head skips the HEAD after each upload, and --no-check-dest skips the lookup of the destination object before it
# (dump names are unique, so there is nothing to compare with).
rclone_offsite() {
  (
    export RCLONE_CONFIG=/dev/null
    RCLONE_CONFIG_OFFSITE_TYPE=s3
    RCLONE_CONFIG_OFFSITE_PROVIDER="$(dit_env_get "$env_file" BACKUP_S3_PROVIDER)" || exit 1
    RCLONE_CONFIG_OFFSITE_ENDPOINT="$(dit_env_get "$env_file" BACKUP_S3_ENDPOINT)" || exit 1
    RCLONE_CONFIG_OFFSITE_ACCESS_KEY_ID="$(dit_env_get "$env_file" BACKUP_S3_ACCESS_KEY_ID)" || exit 1
    RCLONE_CONFIG_OFFSITE_SECRET_ACCESS_KEY="$(dit_env_get "$env_file" BACKUP_S3_SECRET_ACCESS_KEY)" || exit 1
    RCLONE_CONFIG_OFFSITE_NO_CHECK_BUCKET=true
    RCLONE_CONFIG_OFFSITE_NO_HEAD=true
    export RCLONE_CONFIG_OFFSITE_TYPE RCLONE_CONFIG_OFFSITE_PROVIDER RCLONE_CONFIG_OFFSITE_ENDPOINT \
      RCLONE_CONFIG_OFFSITE_ACCESS_KEY_ID RCLONE_CONFIG_OFFSITE_SECRET_ACCESS_KEY RCLONE_CONFIG_OFFSITE_NO_CHECK_BUCKET \
      RCLONE_CONFIG_OFFSITE_NO_HEAD
    # An unreachable endpoint must fail fast: the default is ten low-level retries with growing pauses per file.
    exec rclone --retries 2 --low-level-retries 2 --contimeout 15s --timeout 120s "$@" </dev/null >&2
  )
}

# Uploads every dump without a marker, oldest first, with its sidecar; the marker is written only after both arrived. A file
# that fails does not hold back the newer ones; it returns non-zero, and whatever is not marked stays pending for the next run.
upload_pending() {
  local bucket file failed=0
  bucket="$(dit_env_get "$env_file" BACKUP_S3_BUCKET)" || return 1
  while IFS= read -r file; do
    if rclone_offsite copyto --no-check-dest "$file" "offsite:$bucket/$(basename "$file")" &&
      { [ ! -f "$file.meta" ] || rclone_offsite copyto --no-check-dest "$file.meta" "offsite:$bucket/$(basename "$file").meta"; }; then
      : >"$file.uploaded"
      uploaded_now=$((uploaded_now + 1))
      dit_log "uploaded $(basename "$file")"
    else
      dit_log "the upload of $(basename "$file") failed; it stays pending"
      failed=1
    fi
  done < <(upload_candidates)
  return "$failed"
}

# --- retention -------------------------------------------------------------------------------------------------------

prune_local() {
  local db k file
  for db in trainer umami; do
    for k in daily predeploy; do
      while IFS= read -r file; do
        rm -f -- "$file" "$file.meta" "$file.uploaded"
        dit_log "removed $(basename "$file") by retention"
      done < <(dit_backup_prune_candidates "$backups" "$db" "$k" "$(dit_backup_keep_count "$k")")
    done
  done
}

# --- run -------------------------------------------------------------------------------------------------------------

ts="$(unique_timestamp)"
dump_failed=0
umami_dump=""
uploaded_now=0
written=()
for db in trainer umami; do
  if [ "$db" = "umami" ] && [ -e "$APP_DIR/.umami-recovery" ]; then
    dit_log "skipping the umami dump: .umami-recovery exists, so the database may belong to the upgrade that failed"
    continue
  fi
  if path="$(dump_database "$db" "$ts")"; then
    written+=("$path")
    if [ "$db" = "umami" ]; then
      umami_dump="$path"
    fi
  else
    dump_failed=1
  fi
done

if [ "$dump_failed" -eq 0 ]; then
  utc_now | write_file "$backups/last-success"
fi

upload_failed=0
if upload_pending; then
  if dit_offsite_current "$backups" "$uploaded_now" "$dump_failed"; then
    utc_now | write_file "$backups/last-offsite"
  fi
else
  upload_failed=1
  dit_log "the off-site copy is incomplete; the files stay on this host until an upload succeeds"
fi

prune_local || dit_log "retention could not remove every old file"

if [ "$dump_failed" -ne 0 ]; then
  exit 1
fi
if [ "$kind" = "daily" ] && [ "$upload_failed" -ne 0 ]; then
  exit 1
fi
if [ "$kind" = "predeploy" ]; then
  printf '%s\n' "$umami_dump"
fi
