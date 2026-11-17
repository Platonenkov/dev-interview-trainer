# shellcheck shell=bash
# Sourced by every remote script from its own directory. It sets no shell options: each entry script owns
# "set -euo pipefail", and every function here reports failure through its return status.

APP_DIR="${APP_DIR:-/srv/dev-interview-trainer}"

dit_log() {
  # A dead log pipe must never abort a caller under "set -e" (a rollback in particular).
  printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >&2 || true
}

dit_compose() {
  docker compose --project-directory "$APP_DIR" -f "$APP_DIR/compose.yaml" --env-file "$APP_DIR/.env" "$@"
}

# The image's default exec user is root and "local all postgres peer" needs the OS user postgres; -U alone does not change that.
dit_psql() {
  local db="$1"
  shift
  dit_compose exec -T --user postgres postgres psql -v ON_ERROR_STOP=1 -d "$db" "$@"
}

dit_pg_dump() {
  local db="$1"
  shift
  dit_compose exec -T --user postgres postgres pg_dump -d "$db" "$@"
}

# Directories the Compose bind mounts need before the first "compose up"; otherwise Docker creates them as root.
# Idempotent: install -d leaves the owner and contents of an existing directory alone. Called by dit_apply_hba, which
# runs on every activation, so deploy-app.sh and bootstrap get the layout without a separate call.
dit_ensure_layout() {
  install -d -m 755 "$APP_DIR/postgres" "$APP_DIR/flags" "$APP_DIR/content"
}

# nginx runs as www-data and treats EACCES on the maintenance flag as "file absent", so a directory without the o+x bit
# between / and the flags directory would turn the maintenance check into a silent no-op. This compares mode bits (not
# access(2)) so it also catches the problem when run as root.
dit_check_traversable() {
  local dir mode
  dir="$(readlink -f "$1")" || return 1
  while :; do
    mode="$(stat -L -c %a "$dir")" || return 1
    if [ $((8#$mode & 1)) -eq 0 ]; then
      dit_log "$dir has mode $mode: nginx (www-data) cannot traverse it, so the maintenance flag would be ignored"
      return 1
    fi
    if [ "$dir" = "/" ]; then
      return 0
    fi
    dir="$(dirname "$dir")"
  done
}

# The descriptor stays open for the life of the shell and is inherited by the scripts it starts. A nested script skips
# locking only when it inherited the open descriptor of this very lock file (DIT_LOCK_FD): a bare DIT_LOCKED left in an
# administrator's environment proves nothing and is ignored, so an entry script always takes the lock itself.
dit_lock() {
  local lock="$APP_DIR/.deploy.lock" fd
  if [ "${DIT_LOCKED:-}" = "1" ] && [ -n "${DIT_LOCK_FD:-}" ] && [ -e "/proc/self/fd/$DIT_LOCK_FD" ] &&
    [ "$(stat -L -c %i "/proc/self/fd/$DIT_LOCK_FD" 2>/dev/null)" = "$(stat -c %i "$lock" 2>/dev/null)" ]; then
    return 0
  fi
  unset DIT_LOCKED DIT_LOCK_FD
  exec {fd}>"$lock" || return 1
  if ! flock -w "${DIT_LOCK_TIMEOUT:-900}" "$fd"; then
    dit_log "timed out waiting for the deploy lock"
    return 1
  fi
  export DIT_LOCKED=1 DIT_LOCK_FD="$fd"
}

# State-changing scripts refuse to start while a guard file exists and name the docs/deployment.md section to follow
# (exit 3). "--allow <name>..." lists the guards the caller may proceed over because it is the operation that resolves
# them: "interrupted-deploy" (.deploy-in-progress; deploy-app.sh repairs it itself), "umami-recovery" (.umami-recovery;
# restore.sh --replace <dump> umami resolves it), "restore" (flags/maintenance) and "rotation" (.rotation-pending).
dit_refuse_guards() {
  local refused=0 name
  local -a allowed=()
  if [ "$#" -gt 0 ]; then
    if [ "$1" != "--allow" ]; then
      dit_log "dit_refuse_guards: unknown argument \"$1\" (expected --allow <name>...)"
      return 2
    fi
    shift
    for name in "$@"; do
      case "$name" in
        restore | rotation | umami-recovery | interrupted-deploy) ;;
        *)
          dit_log "dit_refuse_guards: unknown guard name \"$name\""
          return 2
          ;;
      esac
    done
    allowed=("$@")
  fi
  dit_guard_check "$APP_DIR/flags/maintenance" "Interrupted restore" "a restore is in progress or was interrupted" restore "${allowed[@]}" || refused=1
  dit_guard_check "$APP_DIR/.rotation-pending" "Interrupted rotation" "a database password rotation is in progress or was interrupted" rotation "${allowed[@]}" || refused=1
  dit_guard_check "$APP_DIR/.umami-recovery" "Umami upgrade" "a failed deployment changed the Umami image and the database was not restored" umami-recovery "${allowed[@]}" || refused=1
  dit_guard_check "$APP_DIR/.deploy-in-progress" "Interrupted deployment" "a deployment was interrupted and the state may mix two releases; run deploy-app first" interrupted-deploy "${allowed[@]}" || refused=1
  if [ "$refused" -ne 0 ]; then
    exit 3
  fi
}

# The image the running umami container was created from; empty when there is none.
dit_running_umami_image() {
  local id
  id="$(dit_compose ps -q umami 2>/dev/null </dev/null)" || return 0
  if [ -n "$id" ]; then
    docker inspect --format '{{.Config.Image}}' "$id" 2>/dev/null || true
  fi
}

# Resolves .umami-recovery after the Umami database was restored. The guard exists so that the old Umami image never runs
# on a database a newer one migrated, so it is cleared only when the records and the container agree with the restored
# (pre-upgrade) database: current-bundle is the last good bundle and the running Umami image is the one that bundle pins.
# Needs deploy-lib.sh (dit_umami_image). A leftover .deploy-in-progress still says "Umami changed and started", which would
# make the next deploy-app write the guard again and refuse with exit 3, so the marker is rewritten first (each field by
# temp file + rename); the guard is removed only after that succeeded. Called by restore.sh --replace.
dit_clear_umami_recovery() {
  local marker="$APP_DIR/.deploy-in-progress" good_file="$APP_DIR/last-good.env" good_bundle expected running
  if [ -f "$good_file" ]; then
    good_bundle="$(dit_env_get "$good_file" DIT_BUNDLE_SHA)" || return 1
    if [ "$(basename "$(readlink "$APP_DIR/current-bundle" 2>/dev/null || true)")" != "$good_bundle" ]; then
      dit_log "current-bundle is not the last good bundle, so the running Umami image may not match the restored database; the guard stays"
      return 1
    fi
    expected="$(dit_umami_image "$APP_DIR/current-bundle/compose/compose.yaml")" || return 1
    running="$(dit_running_umami_image)"
    if [ "$running" != "$expected" ]; then
      dit_log "the running Umami image is '${running:-none}', not the pinned $expected; the guard stays"
      return 1
    fi
  fi
  if [ -e "$marker" ]; then
    printf '0\n' | dit_env_set "$marker" UMAMI_STARTED || return 1
    printf '0\n' | dit_env_set "$marker" UMAMI_CHANGED || return 1
  fi
  rm -f "$APP_DIR/.umami-recovery"
}

# <file> <section> <meaning> <guard-name> [allowed-name...]
dit_guard_check() {
  local file="$1" section="$2" meaning="$3" name="$4" allowed
  shift 4
  if [ ! -e "$file" ]; then
    return 0
  fi
  for allowed in "$@"; do
    if [ "$allowed" = "$name" ]; then
      return 0
    fi
  done
  dit_log "refusing to continue: $file exists ($meaning). Follow \"$section\" in docs/deployment.md."
  return 1
}

# --- records of a deployment: shared by deploy-app.sh (rollback and repair) and restore.sh ---------------------------------

dit_install_env_from() { # <env file>
  local source="$1" tmp
  tmp="$(umask 077 && mktemp "$APP_DIR/.env.tmp.XXXXXX")" || return 1
  if ! cp "$source" "$tmp" || ! mv -f "$tmp" "$APP_DIR/.env"; then
    rm -f "$tmp"
    return 1
  fi
}

dit_switch_bundle() { # <bundle sha>
  ln -sfn "bundles/$1" "$APP_DIR/current-bundle.new" && mv -T "$APP_DIR/current-bundle.new" "$APP_DIR/current-bundle"
}

# Puts .env, current-bundle and pg_hba.conf back to what last-good.env describes (the failed deployment's authentication rules
# must not outlive it). Every step is attempted and logged; non-zero when any failed. Starts nothing.
dit_restore_last_good_records() {
  local good_file="$APP_DIR/last-good.env" good_bundle failed=0
  good_bundle="$(dit_env_get "$good_file" DIT_BUNDLE_SHA)" || return 1
  dit_install_env_from "$good_file" || { dit_log "could not restore .env"; failed=1; }
  dit_switch_bundle "$good_bundle" || { dit_log "could not switch current-bundle"; failed=1; }
  dit_apply_hba "$APP_DIR/current-bundle" || { dit_log "could not restore pg_hba.conf"; failed=1; }
  return "$failed"
}

# --- content current and .import-pending: shared by receive-content.sh, deploy-app.sh and restore.sh -----------------------

# What .import-pending may record: a release path under content/, whatever name an earlier bootstrap gave it.
dit_content_target_pattern='^releases/[A-Za-z0-9_][A-Za-z0-9._-]*$'

dit_switch_content_current() { # <relative target>
  ln -sfn "$1" "$APP_DIR/content/current.new"
  mv -T "$APP_DIR/content/current.new" "$APP_DIR/content/current"
}

# An empty target is the first delivery: no current returns the application to the fresh-install state, and a rejected
# import never commits, so the database has nothing to undo.
dit_restore_content_current() { # <relative target or empty>
  if [ -n "$1" ] && [ -d "$APP_DIR/content/$1" ]; then
    dit_switch_content_current "$1"
  else
    rm -f "$APP_DIR/content/current"
  fi
}

# The hash of the active import ("none" for an empty database), straight from the table the importer writes; non-zero when
# the database cannot be asked.
dit_active_content_hash() {
  local hash
  hash="$(dit_psql trainer -tA -c 'SELECT hash FROM content_imports WHERE is_active' </dev/null 2>/dev/null)" || return 1
  printf '%s\n' "${hash:-none}"
}

# For a script that is about to start or restart the application: an earlier delivery never learned its outcome, and the
# startup import would read whatever current points to. .import-pending holds the release, the previous target and (third
# line, written by receive-content.sh) the active content hash before the switch. If the active hash differs now, the import
# of the pending release committed (nothing else imports while the lock is held): current stays on it and only the guard is
# cleared. Otherwise current goes back to the previous target, but only while it still names the pending release, and never
# by deleting it when the previous release directory has vanished (that returns 1 and keeps the guard). An older file
# without the third line cannot be verified and is treated as not imported. Returns 1 when the file is malformed, the
# database cannot be asked, or the previous release is gone.
dit_revert_import_pending() {
  local pending="$APP_DIR/.import-pending" pending_sha="" pending_previous="" hash_before="" hash_now current_target
  if [ ! -e "$pending" ]; then
    return 0
  fi
  {
    IFS= read -r pending_sha || true
    IFS= read -r pending_previous || true
    IFS= read -r hash_before || true
  } <"$pending"
  if ! [[ "$pending_sha" =~ ^[0-9a-f]{40}$ ]] || { [ -n "$pending_previous" ] && ! [[ "$pending_previous" =~ $dit_content_target_pattern ]]; } ||
    { [ -n "$hash_before" ] && ! [[ "$hash_before" =~ ^([0-9a-f]{64}|none)$ ]]; }; then
    dit_log "$pending is malformed; resolve it by hand (docs/deployment.md, \"Content import\")"
    return 1
  fi
  current_target="$(readlink "$APP_DIR/content/current" 2>/dev/null || true)"
  if [ -n "$hash_before" ]; then
    if ! hash_now="$(dit_active_content_hash)"; then
      dit_log "cannot read the active content hash from the database, so it is unknown whether $pending_sha was imported; $pending stays (docs/deployment.md, \"Content import\")"
      return 1
    fi
    if [ "$hash_now" != "$hash_before" ]; then
      rm -f "$pending"
      dit_log "the delivery of $pending_sha was imported although its answer was lost (the active content hash changed); current stays on ${current_target:-the fresh-install state} and the guard is cleared"
      return 0
    fi
  fi
  if [ "$current_target" != "releases/$pending_sha" ]; then
    rm -f "$pending"
    dit_log "the delivery of $pending_sha never learned its outcome and current no longer points to it; current is left on ${current_target:-the fresh-install state}"
    return 0
  fi
  if [ -n "$pending_previous" ] && [ ! -d "$APP_DIR/content/$pending_previous" ]; then
    dit_log "the delivery of $pending_sha was not imported, but the previous release $pending_previous no longer exists, so current is left on it and $pending stays; follow \"Content import\" in docs/deployment.md"
    return 1
  fi
  dit_restore_content_current "$pending_previous"
  rm -f "$pending"
  if [ -n "$pending_previous" ]; then
    dit_log "the delivery of $pending_sha was not imported; current is back on $pending_previous"
  else
    dit_log "the delivery of $pending_sha was not imported; current was removed (the fresh-install state)"
  fi
}

dit_env_check_name() {
  case "$1" in
    '' | [0-9]* | *[!A-Za-z0-9_]*)
      dit_log "invalid env name"
      return 2
      ;;
  esac
}

# Reads NAME='value' lines without sourcing the file; values never contain a single quote (render-env rejects them).
dit_env_get() {
  local file="$1" name="$2" line
  dit_env_check_name "$name" || return 2
  if ! line="$(grep -m1 "^${name}='" "$file")"; then
    dit_log "$name is not set in $file"
    return 1
  fi
  line="${line#"${name}='"}"
  printf '%s\n' "${line%\'}"
}

# New value from stdin; replaced in place or appended, written to a temp copy and renamed, mode 600.
dit_env_set() {
  local file="$1" name="$2" value tmp
  dit_env_check_name "$name" || return 2
  IFS= read -r value
  case "$value" in
    *\'* | *$'\r'*)
      dit_log "refusing a value with a quote or CR for $name"
      return 2
      ;;
  esac
  tmp="$(umask 077 && mktemp "${file}.XXXXXX")" || return 1
  if ! NEW_VALUE="$value" awk -v n="$name" '
      BEGIN { done = 0; line = n "=\047" ENVIRON["NEW_VALUE"] "\047" }
      index($0, n "=") == 1 { if (!done) { print line; done = 1 } ; next }
      { print }
      END { if (!done) print line }
    ' "$file" >"$tmp" || ! chmod 600 "$tmp" || ! mv -f "$tmp" "$file"; then
    rm -f "$tmp"
    return 1
  fi
}

# Rules belong to the running deployment: copy by temp file and rename (the directory is mounted, so the new inode is seen), reload only when changed.
dit_apply_hba() {
  local bundle="$1" src dest tmp running
  src="$bundle/compose/postgres/pg_hba.conf"
  dest="$APP_DIR/postgres/pg_hba.conf"
  dit_ensure_layout || return 1
  if [ -f "$dest" ] && cmp -s "$src" "$dest"; then
    return 0
  fi
  tmp="$(mktemp "$dest.XXXXXX")" || return 1
  if ! cp "$src" "$tmp" || ! chmod 644 "$tmp" || ! mv -f "$tmp" "$dest"; then
    rm -f "$tmp"
    return 1
  fi
  # Captured first: "ps | grep -q" can end the pipeline early (SIGPIPE) and silently skip the reload.
  running="$(dit_compose ps --status running --services)" || return 1
  if grep -qx postgres <<<"$running"; then
    dit_psql postgres -c 'SELECT pg_reload_conf()' >/dev/null
  fi
}
