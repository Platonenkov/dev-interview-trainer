#!/usr/bin/env bash
# Deploys one application release: backup, activate bundle and .env, migrate, start, wait for readiness, and roll back to
# the last state that passed readiness when anything after activation fails (or the run is signalled).
#
#   deploy-app.sh <staging-dir> <image-ref> <commit-sha>
#
# Exit codes: 0 deployed; 1 failed (rolled back where there was something to roll back to); 2 invalid input or host
# layout, nothing changed; 3 a guard file refuses the deployment, nothing changed.
#
# A pre-deploy backup (backup.sh predeploy) is required whenever last-good.env exists: when it fails the run exits 1
# before anything is touched. A first install has nothing to back up and skips it.
#
# Starting it (Task 13): under "setsid" so an SSH drop sends no SIGHUP, with stdout and stderr redirected to a file or
# /dev/null (nohup style). The script ignores SIGPIPE and writes everything to $APP_DIR/logs/deploy-app.log itself,
# mirroring to its stderr only while that is writable, so a dropped channel cannot kill a rollback half-way. The
# caller reads the result from the exit status and the log.
#
# Recovery when a failed deployment changed the Umami image: .umami-recovery blocks every deployment. If a rollback also
# failed to restore the records, .deploy-in-progress exists as well. Run "restore.sh --replace <dump> umami", which
# proceeds over both guards (dit_refuse_guards --allow umami-recovery interrupted-deploy). When the marker exists it first
# puts .env, current-bundle and pg_hba.conf back to last-good.env (dit_restore_last_good_records, the function the rollback
# uses), so the Umami it starts is the pre-upgrade image, and it clears .umami-recovery with dit_clear_umami_recovery only
# when that image runs (the function first resets UMAMI_CHANGED and UMAMI_STARTED in the marker, so the next run does not
# write the guard again). Then run deploy-app: it repairs the interrupted state first (restoring last-good.env) and deploys.
#
# A leftover .import-pending (receive-content.sh exit 6, or a kill after the switch) is settled first
# (dit_revert_import_pending): a release whose import committed stays current, otherwise content/current goes back to the
# previous release recorded in it, so the application starts on a release the import had accepted. Exit 2 when the file
# is malformed, the database cannot be read or the previous release directory is gone.
#
# Exit 2 after a repair: when the new bundle's Umami image cannot be compared, the interrupted run has already been
# repaired to last-good.env and only the new deployment is refused; the log says so.
set -euo pipefail
trap '' PIPE

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "$here/lib.sh"
# shellcheck source=deploy-lib.sh
. "$here/deploy-lib.sh"
export APP_DIR

# Shortened by the smoke test; production keeps the default.
wait_timeout="${DIT_WAIT_TIMEOUT:-180}"

if [ "$#" -ne 3 ]; then
  dit_log "usage: deploy-app.sh <staging-dir> <image-ref> <commit-sha>"
  exit 2
fi
staging="$1"
image="$2"
commit="$3"

env_file="$APP_DIR/.env"
good_file="$APP_DIR/last-good.env"
marker="$APP_DIR/.deploy-in-progress"
umami_guard="$APP_DIR/.umami-recovery"
good_list="$APP_DIR/good-bundles"
log_file="$APP_DIR/logs/deploy-app.log"

required_keys=(
  APP_IMAGE APP_DIR PUBLIC_BASE_URL ADMIN_TELEGRAM_IDS
  TELEGRAM_OIDC_CLIENT_ID TELEGRAM_OIDC_CLIENT_SECRET
  DB_PASSWORD CONTENT_IMPORT_TOKEN UMAMI_DB_PASSWORD UMAMI_APP_SECRET
  BACKUP_S3_PROVIDER BACKUP_S3_ENDPOINT BACKUP_S3_BUCKET BACKUP_S3_ACCESS_KEY_ID BACKUP_S3_SECRET_ACCESS_KEY
)

# prepare: nothing has changed yet; active: server state may differ from last-good.env, any failure rolls back; published:
# the deployment passed readiness and last-good.env records it, so a later failure (pruning) must never roll it back.
phase=prepare
publish_started=0
bundle_sha=""
previous_bundle=""
previous_image=""
umami_changed=0
umami_started=0
umami_dump="none"
pump_pid=""
repaired_interrupted_run=0

# --- logging ---------------------------------------------------------------------------------------------------------

# File first, then the original stderr for as long as it accepts writes.
pump_log() {
  local line mirror=1
  # A signal to the whole process group must not end logging before the rollback has finished; EOF ends this loop.
  trap '' TERM INT HUP
  while IFS= read -r line || [ -n "$line" ]; do
    printf '%s\n' "$line" >>"$log_file" 2>/dev/null || true
    if [ "$mirror" = "1" ] && ! printf '%s\n' "$line" >&3 2>/dev/null; then
      mirror=0
    fi
  done
}

# Directory 700, file 600, rotated once it passes 5 MiB (one generation is kept).
setup_logging() {
  install -d -m 700 "$APP_DIR/logs" || return 1
  if [ -f "$log_file" ] && [ "$(stat -c %s "$log_file")" -gt 5242880 ]; then
    mv -f "$log_file" "$log_file.1" || return 1
  fi
  (umask 077 && : >>"$log_file") || return 1
  exec 3>&2
  exec > >(pump_log) 2>&1
  pump_pid=$!
}

finish_logging() {
  if [ -n "$pump_pid" ]; then
    exec >&- 2>&-
    wait "$pump_pid" 2>/dev/null || true
  fi
}

# --- test hooks ------------------------------------------------------------------------------------------------------

# Used by smoke.sh to stop the run at a named step boundary. Both variables must be set, so a stray DIT_TEST_KILL_AT in
# a production environment does nothing.
kill_point() {
  if [ "${DIT_TEST_HOOKS:-}" = "1" ] && [ "${DIT_TEST_KILL_AT:-}" = "$1" ]; then
    kill -KILL $$
  fi
}

# --- input -----------------------------------------------------------------------------------------------------------

validate_input() {
  local real_app real_staging name value
  real_app="$(realpath -e "$APP_DIR")" || exit 2
  if ! real_staging="$(realpath -e "$staging" 2>/dev/null)"; then
    dit_log "the staging directory does not exist"
    exit 2
  fi
  case "$real_staging" in
    "$real_app"/staging/*) staging="$real_staging" ;;
    *)
      dit_log "the staging directory must be inside $APP_DIR/staging"
      exit 2
      ;;
  esac
  if [ ! -d "$staging/bundle/remote" ] || [ ! -f "$staging/.env.new" ]; then
    dit_log "the staging directory lacks bundle/remote or .env.new"
    exit 2
  fi
  if ! [[ "$commit" =~ ^[0-9a-f]{7,64}$ ]]; then
    dit_log "the commit must be 7 to 64 lowercase hex digits"
    exit 2
  fi

  for name in "${required_keys[@]}"; do
    value="$(dit_env_get "$staging/.env.new" "$name")" || exit 2
    if [ -z "$value" ]; then
      dit_log "$name is empty in .env.new"
      exit 2
    fi
  done
  if [ "$(dit_env_get "$staging/.env.new" APP_IMAGE)" != "$image" ]; then
    dit_log "APP_IMAGE in .env.new differs from the image argument"
    exit 2
  fi
  if [ "$(dit_env_get "$staging/.env.new" APP_DIR)" != "$APP_DIR" ]; then
    dit_log "APP_DIR in .env.new differs from the directory the script runs for"
    exit 2
  fi

  # A configuration rendered with a stale or hand-edited password would break the database connection; passwords change
  # only through rotate-db-password (spec 8.7.2). Values are never printed.
  if [ -f "$env_file" ]; then
    for name in DB_PASSWORD UMAMI_DB_PASSWORD; do
      if [ "$(dit_env_get "$staging/.env.new" "$name")" != "$(dit_env_get "$env_file" "$name")" ]; then
        dit_log "$name in .env.new differs from the running configuration; passwords change only through rotate-db-password"
        exit 2
      fi
    done
  fi
}

# Decided before anything changes, from the two bundles' compose files, so a rollback never has to guess from a possibly
# half-copied directory. A first install has no rollback target and no Umami data worth protecting.
decide_umami_change() {
  local good_bundle
  if [ ! -f "$good_file" ]; then
    umami_changed=0
    return 0
  fi
  good_bundle="$(dit_env_get "$good_file" DIT_BUNDLE_SHA)" || exit 2
  if ! umami_changed="$(dit_umami_changed "$APP_DIR/bundles/$good_bundle/compose/compose.yaml" "$staging/bundle/compose/compose.yaml")"; then
    if [ "$repaired_interrupted_run" = "1" ]; then
      dit_log "cannot compare the Umami images of the last good bundle and the new one; the interrupted run was repaired to last-good.env and this deployment was refused"
    else
      dit_log "cannot compare the Umami images of the last good bundle and the new one; nothing was changed"
    fi
    exit 2
  fi
}

# --- records ---------------------------------------------------------------------------------------------------------

link_compose_file() {
  if [ "$(readlink "$APP_DIR/compose.yaml" 2>/dev/null || true)" != "current-bundle/compose/compose.yaml" ]; then
    ln -sfn current-bundle/compose/compose.yaml "$APP_DIR/compose.yaml.new" && mv -T "$APP_DIR/compose.yaml.new" "$APP_DIR/compose.yaml"
  fi
}

# The marker says "state may mix two releases" and carries what a repair run needs; it is rewritten (atomically) when Umami
# is about to start.
write_marker() {
  local tmp
  tmp="$(mktemp "$marker.XXXXXX")" || return 1
  if ! printf "DIT_BUNDLE_SHA='%s'\nUMAMI_DUMP='%s'\nUMAMI_CHANGED='%s'\nUMAMI_STARTED='%s'\n" \
    "$bundle_sha" "$umami_dump" "$umami_changed" "$umami_started" >"$tmp" || ! mv -f "$tmp" "$marker"; then
    rm -f "$tmp"
    return 1
  fi
}

write_umami_guard() {
  local tmp
  tmp="$(mktemp "$umami_guard.XXXXXX")" || return 1
  if ! printf 'UMAMI_DUMP=%s\n' "$umami_dump" >"$tmp" || ! mv -f "$tmp" "$umami_guard"; then
    rm -f "$tmp"
    return 1
  fi
}

# --- rollback --------------------------------------------------------------------------------------------------------

# Returns non-zero when the records (.env, current-bundle, pg_hba.conf) or the Umami guard could not be restored: then the
# caller keeps the repair marker and nothing is started. Starting the stack is best effort, because the records are what
# the next run needs to repair from.
rollback() {
  local good_image failed=0

  if [ ! -f "$good_file" ]; then
    dit_log "first install incomplete; fix and re-run deploy-app"
    dit_compose stop app umami || dit_log "could not stop app and umami"
    return 0
  fi

  good_image="$(dit_env_get "$good_file" APP_IMAGE)" || return 1
  dit_env_get "$good_file" DIT_BUNDLE_SHA >/dev/null || return 1

  # The guard goes first, so no later "compose up" can run the old Umami image on a database the new one may have migrated.
  if [ "$umami_started" = "1" ] && [ "$umami_changed" = "1" ]; then
    if ! write_umami_guard; then
      dit_log "could not write $umami_guard; umami stays stopped"
      dit_compose stop umami || dit_log "could not stop umami"
      return 1
    fi
  fi

  dit_restore_last_good_records || failed=1
  if [ "$failed" -ne 0 ]; then
    dit_log "the records were not restored; nothing was started"
    return 1
  fi

  if [ -e "$umami_guard" ]; then
    dit_compose stop umami || dit_log "could not stop umami"
    dit_compose up -d --wait --wait-timeout "$wait_timeout" --remove-orphans postgres app || dit_log "the rolled-back stack is not healthy"
    dit_log "rolled back to $good_image; umami was left stopped because the failed deployment changed its image"
    dit_log "Umami upgrade recovery (docs/deployment.md, \"Umami upgrade\"): restore.sh --replace ${umami_dump} umami"
  else
    dit_compose up -d --wait --wait-timeout "$wait_timeout" --remove-orphans || dit_log "the rolled-back stack is not healthy"
    dit_log "rolled back to $good_image"
  fi
}

# The EXIT trap, not ERR, decides: it runs for "set -e" exits from inside functions and is not inherited by command
# substitutions, so a failing "$(...)" cannot start a second rollback. A signal handler only has to exit.
on_exit() {
  local code=$?
  trap '' TERM INT HUP
  trap - EXIT
  if [ "$code" -ne 0 ] && [ "$phase" = "active" ]; then
    if [ "$publish_started" = "1" ] && cmp -s "$env_file" "$good_file"; then
      dit_log "the deployment was published before the run ended; keeping it"
      rm -f "$marker"
    else
      dit_log "deployment failed; rolling back"
      if rollback; then
        rm -f "$marker"
      else
        dit_log "rollback did not complete; the marker stays and the next run repairs first"
      fi
    fi
    code=1
  fi
  finish_logging
  exit "$code"
}

trap on_exit EXIT
trap 'dit_log "interrupted by a signal"; exit 1' TERM INT HUP

# A previous run that was killed without a chance to roll back leaves its marker; the state it left can be a mix of two
# deployments, and last-good.env is the only description that is complete.
recover_interrupted_run() {
  if [ ! -e "$marker" ]; then
    return 0
  fi
  dit_log "a previous deployment was interrupted; restoring the last good state first"
  bundle_sha="$(dit_env_get "$marker" DIT_BUNDLE_SHA || true)"
  umami_dump="$(dit_env_get "$marker" UMAMI_DUMP || echo none)"
  # A field that is missing (a marker from before these fields existed) or unreadable counts as "Umami may have been
  # changed and started": a needless restore is recoverable, a missed one corrupts the Umami database.
  umami_changed="$(dit_env_get "$marker" UMAMI_CHANGED || echo 1)"
  umami_started="$(dit_env_get "$marker" UMAMI_STARTED || echo 1)"
  # A run killed after publish_last_good but before it removed the marker did succeed: last-good.env already names the
  # marker's bundle, so the Umami image it started is the published one and nothing needs the restore of an older dump.
  if [ -f "$good_file" ] && [ "$bundle_sha" = "$(dit_env_get "$good_file" DIT_BUNDLE_SHA || true)" ]; then
    dit_log "the interrupted deployment was published before it ended; keeping it"
    umami_changed=0
    umami_started=0
  fi
  if ! rollback; then
    dit_log "could not restore the last good state; the marker stays"
    exit 1
  fi
  # The state is restored either way; a guard written by the rollback keeps blocking on its own.
  rm -f "$marker"
  repaired_interrupted_run=1
  if [ -e "$umami_guard" ]; then
    dit_log "the interrupted deployment changed the Umami image; deploys stay refused until it is recovered"
    exit 3
  fi
  bundle_sha=""
  umami_changed=0
  umami_started=0
  umami_dump="none"
}

# --- steps -----------------------------------------------------------------------------------------------------------

dit_lock || exit 1
setup_logging || {
  echo "cannot open the deploy log under $APP_DIR/logs" >&2
  exit 1
}
dit_log "deploy-app started for commit $commit"
dit_refuse_guards --allow interrupted-deploy
dit_ensure_layout
# nginx treats an unreadable flags directory as "no maintenance"; refuse rather than run unprotected.
dit_check_traversable "$APP_DIR/flags" || exit 2
validate_input
# Before anything restarts the application: the startup import reads whatever content/current points to, and a delivery that
# never learned its outcome may have left it on a release the import rejects.
dit_revert_import_pending || exit 2
# After the repair of an interrupted run, which restores its own Umami values: this run decides last, and for itself.
recover_interrupted_run
decide_umami_change

# Leftovers of runs that were killed mid-write, and staging directories no workflow will use again.
rm -rf "$APP_DIR"/bundles/.new-*
rm -f "$APP_DIR"/.env.tmp.* "$APP_DIR"/last-good.env.tmp.* "$APP_DIR"/.deploy-in-progress.* "$APP_DIR"/good-bundles.* \
  "$APP_DIR"/.umami-recovery.*
rm -f "$APP_DIR/compose.yaml.new" "$APP_DIR/current-bundle.new"
find "$APP_DIR/staging" -mindepth 1 -maxdepth 1 -type d -mmin +2880 ! -path "$staging" -exec rm -rf {} +

if [ -f "$good_file" ]; then
  previous_image="$(dit_env_get "$good_file" APP_IMAGE)"
  previous_bundle="$(dit_env_get "$good_file" DIT_BUNDLE_SHA)"
  # The pre-deploy backup is required: it is the only way back from a failed Umami upgrade. backup.sh inherits the lock held
  # here, passes no guard (none can exist at this point) and prints the path of the umami dump it wrote as the last line of
  # stdout; a failure stops here with nothing changed.
  if ! backup_output="$(bash "$here/backup.sh" predeploy)"; then
    dit_log "the pre-deploy backup failed; nothing was changed"
    exit 1
  fi
  umami_dump="$(printf '%s\n' "$backup_output" | tail -n 1)"
  if [ ! -f "$umami_dump" ]; then
    dit_log "backup.sh did not name the umami dump it wrote; nothing was changed"
    exit 1
  fi
else
  dit_log "no last-good.env: first install, skipping the pre-deploy backup"
fi

bundle_sha="$(dit_hash_bundle "$staging/bundle")"

dit_log "activating bundle $bundle_sha for commit $commit"
phase=active
write_marker
kill_point after-marker
install -d -m 755 "$APP_DIR/bundles"
if [ ! -d "$APP_DIR/bundles/$bundle_sha" ]; then
  cp -a "$staging/bundle" "$APP_DIR/bundles/.new-$bundle_sha"
  mv -T "$APP_DIR/bundles/.new-$bundle_sha" "$APP_DIR/bundles/$bundle_sha"
fi
kill_point after-bundle-copy

new_env="$(umask 077 && mktemp "$APP_DIR/.env.tmp.XXXXXX")"
cp "$staging/.env.new" "$new_env"
printf '%s\n' "$bundle_sha" | dit_env_set "$new_env" DIT_BUNDLE_SHA
printf '%s\n' "$commit" | dit_env_set "$new_env" DIT_COMMIT
mv -f "$new_env" "$env_file"
# Every secret of the release is in .env now; the staging copy has no further use.
rm -f "$staging/.env.new"
kill_point after-env

dit_switch_bundle "$bundle_sha"
link_compose_file
kill_point after-switch
dit_apply_hba "$APP_DIR/current-bundle"
kill_point after-hba

dit_compose pull
dit_compose up -d --wait --wait-timeout "$wait_timeout" postgres
bash "$here/ensure-databases.sh"
dit_compose run --rm -T --no-deps --entrypoint /app/migrate/efbundle app
# An image that never started cannot have migrated the Umami database, so the recovery guard applies from here on.
umami_started=1
write_marker
dit_compose up -d --wait --wait-timeout "$wait_timeout" --remove-orphans
curl -fsS --max-time 10 --retry 3 --retry-connrefused http://127.0.0.1:8080/health/ready >/dev/null
kill_point after-ready

# --- success ---------------------------------------------------------------------------------------------------------

publish_last_good() {
  local tmp
  tmp="$(umask 077 && mktemp "$APP_DIR/last-good.env.tmp.XXXXXX")"
  cp "$env_file" "$tmp"
  publish_started=1
  mv -f "$tmp" "$good_file"
}

# Images of this repository other than the one running and the previous good one. Removal is by reference, because an
# image id with several tags cannot be removed by id. Fails closed: when the running image cannot be resolved, nothing is
# removed.
prune_images() {
  local repo ref keep="" id repository tag digest target
  repo="${image%%@*}"
  case "${repo##*/}" in
    *:*) repo="${repo%:*}" ;;
  esac
  for ref in "$image" "$previous_image"; do
    [ -n "$ref" ] || continue
    id="$(docker image inspect --format '{{.Id}}' "$ref" 2>/dev/null || true)"
    if [ -z "$id" ] && [ "$ref" = "$image" ]; then
      dit_log "the running image cannot be inspected; no image was pruned"
      return 1
    fi
    keep+="$id"$'\n'
  done
  while read -r repository tag digest id; do
    [ -n "$id" ] || continue
    if grep -qxF "$id" <<<"$keep"; then
      continue
    fi
    if [ "$tag" != "<none>" ]; then
      target="$repository:$tag"
    else
      target="$repository@$digest"
    fi
    docker image rm "$target" >/dev/null 2>&1 || true
  done < <(docker image ls --digests --no-trunc --filter "reference=$repo" --format '{{.Repository}} {{.Tag}} {{.Digest}} {{.ID}}')
}

publish_last_good
kill_point after-last-good
phase=published
rm -f "$marker"
kill_point after-marker-removed
if dit_remember_good_bundle "$good_list" "$bundle_sha"; then
  dit_prune_bundles "$APP_DIR/bundles" "$good_list" "$bundle_sha" "$previous_bundle" "$(dit_env_get "$good_file" DIT_BUNDLE_SHA)" \
    "$(basename "$(readlink "$APP_DIR/current-bundle")")" || dit_log "could not prune old bundles"
else
  dit_log "could not update the list of good bundles; no bundle was pruned"
fi
prune_images || dit_log "could not prune old images"
dit_log "deployed $image at commit $commit"
