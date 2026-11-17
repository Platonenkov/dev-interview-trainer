#!/usr/bin/env bash
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
remote="$here/../remote"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
failures=0

check() {
  if [ "$2" != "$3" ]; then
    printf 'FAIL: %s (expected %s, got %s)\n' "$1" "$2" "$3" >&2
    failures=$((failures + 1))
  fi
}

export APP_DIR="$work/app"
mkdir -p "$APP_DIR"

# lib.sh must not change the options of the shell that sources it.
before="$-"
# shellcheck source=../remote/lib.sh
. "$remote/lib.sh"
check "lib.sh leaves shell options alone" "$before" "$-"

env_file="$work/test.env"
printf "A='1'\nB='two words'\n" >"$env_file"

check "get reads a value" "two words" "$(dit_env_get "$env_file" B)"

status=0
message="$(dit_env_get "$env_file" MISSING 2>&1)" || status=$?
check "get of a missing key fails" 1 "$status"
case "$message" in
  *MISSING*) ;;
  *) printf 'FAIL: missing key message does not name the key: %s\n' "$message" >&2; failures=$((failures + 1)) ;;
esac

status=0
dit_env_get "$env_file" 'bad name' 2>/dev/null || status=$?
check "get rejects an invalid name" 2 "$status"

printf 'x\n' | dit_env_set "$env_file" B
check "set replaces in place" "x" "$(dit_env_get "$env_file" B)"
check "set keeps the other keys" "1" "$(dit_env_get "$env_file" A)"

printf 'new\n' | dit_env_set "$env_file" App__C
check "set appends a missing key" "new" "$(dit_env_get "$env_file" App__C)"

# Backslash, dollar and glob characters must survive untouched (single-quoted in the file, never expanded).
# shellcheck disable=SC2016 # the dollar sign is the literal under test
tricky='a\b$HOME*&\1'
printf '%s\n' "$tricky" | dit_env_set "$env_file" TRICKY
check "set keeps backslash and dollar" "$tricky" "$(dit_env_get "$env_file" TRICKY)"

status=0
printf "it's\n" | dit_env_set "$env_file" B 2>/dev/null || status=$?
check "set rejects a quote" 2 "$status"
check "rejected set leaves the old value" "x" "$(dit_env_get "$env_file" B)"

check "set leaves no temp files" 0 "$(find "$work" -maxdepth 1 -name 'test.env.*' | wc -l | tr -d ' ')"

# Layout helper is idempotent.
dit_ensure_layout
dit_ensure_layout
for dir in postgres flags content; do
  [ -d "$APP_DIR/$dir" ] || { printf 'FAIL: %s was not created\n' "$dir" >&2; failures=$((failures + 1)); }
done

# The flags directory must be reachable by nginx (www-data): every directory on the way needs o+x.
trav="$work/trav/a/flags"
mkdir -p "$trav"
# mktemp creates $work with mode 700, which would itself fail the check.
chmod 755 "$work"
status=0
dit_check_traversable "$trav" || status=$?
check "traversable path is accepted" 0 "$status"
chmod 750 "$work/trav/a"
if [ "$(stat -c %a "$work/trav/a")" = "750" ]; then
  status=0
  message="$(dit_check_traversable "$trav" 2>&1)" || status=$?
  check "directory without o+x is refused" 1 "$status"
  case "$message" in
    */trav/a\ has\ mode\ 750*) ;;
    *) printf 'FAIL: refusal does not name the directory and mode: %s
' "$message" >&2; failures=$((failures + 1)) ;;
  esac
  chmod 755 "$work/trav/a"
else
  echo "SKIP: this file system ignores chmod, the o+x refusal was not exercised"
fi
status=0
dit_check_traversable "$work/trav/missing" 2>/dev/null || status=$?
check "missing path is refused" 1 "$status"

# ensure-databases.sh must stop on an empty password before it touches the database: docker is a stub that records calls.
stub="$work/bin"
mkdir -p "$stub"
printf '#!/bin/sh\necho called >> "%s/docker.calls"\nexit 1\n' "$work" >"$stub/docker"
chmod +x "$stub/docker"
printf "DB_PASSWORD=''\nUMAMI_DB_PASSWORD='p'\n" >"$APP_DIR/.env"
status=0
PATH="$stub:$PATH" bash "$remote/ensure-databases.sh" 2>/dev/null || status=$?
check "empty password is rejected" 1 "$status"
check "empty password never reaches docker" "absent" "$([ -e "$work/docker.calls" ] && echo present || echo absent)"

printf "UMAMI_DB_PASSWORD='p'\n" >"$APP_DIR/.env"
status=0
PATH="$stub:$PATH" bash "$remote/ensure-databases.sh" 2>/dev/null || status=$?
check "missing password key is rejected" 1 "$status"
check "missing password key never reaches docker" "absent" "$([ -e "$work/docker.calls" ] && echo present || echo absent)"

# dit_lock needs flock (util-linux); the check is skipped where it is absent.
if command -v flock >/dev/null 2>&1; then
  # The holder reports that it owns the lock, then keeps it until the test releases it (30 s safety limit).
  holder_ready="$work/holder.ready"
  holder_release="$work/holder.release"
  (
    unset DIT_LOCKED
    dit_lock
    : >"$holder_ready"
    for _ in $(seq 1 300); do
      [ -e "$holder_release" ] && break
      sleep 0.1
    done
  ) &
  holder_pid=$!
  for _ in $(seq 1 300); do
    [ -e "$holder_ready" ] && break
    sleep 0.1
  done
  check "holder acquired the lock" "present" "$([ -e "$holder_ready" ] && echo present || echo absent)"

  status=0
  (
    unset DIT_LOCKED
    DIT_LOCK_TIMEOUT=1 dit_lock
  ) 2>/dev/null || status=$?
  check "second locker times out while the lock is held" 1 "$status"

  # A DIT_LOCKED inherited without the lock holder's open descriptor proves nothing: an entry script still waits.
  status=0
  (
    export DIT_LOCKED=1 DIT_LOCK_FD=99
    DIT_LOCK_TIMEOUT=1 dit_lock
  ) 2>/dev/null || status=$?
  check "a bare inherited DIT_LOCKED does not skip the lock" 1 "$status"

  : >"$holder_release"
  wait "$holder_pid"
  status=0
  (
    unset DIT_LOCKED
    DIT_LOCK_TIMEOUT=5 dit_lock
  ) || status=$?
  check "lock is free after the holder exits" 0 "$status"
  # A script started by the holder inherits the open descriptor and skips locking.
  status=0
  (
    unset DIT_LOCKED DIT_LOCK_FD
    dit_lock
    DIT_LOCK_TIMEOUT=1 bash -c '. "$1"; dit_lock' _ "$remote/lib.sh"
  ) || status=$?
  check "a script started by the lock holder skips locking" 0 "$status"
else
  echo "SKIP: flock is not installed, dit_lock was not exercised"
fi

# Guard files: every state-changing script refuses (exit 3) and names the section to follow. The helper ends the shell with
# "exit 3", so each call runs in a subshell.
saved_app_dir="$APP_DIR"
APP_DIR="$work/guards"
mkdir -p "$APP_DIR/flags"
status=0
(dit_refuse_guards) 2>/dev/null || status=$?
check "no guard file means no refusal" 0 "$status"
for guard in flags/maintenance .rotation-pending .umami-recovery .deploy-in-progress; do
  : >"$APP_DIR/$guard"
  status=0
  message="$( (dit_refuse_guards) 2>&1)" || status=$?
  check "guard $guard refuses with exit 3" 3 "$status"
  case "$message" in
    *"$guard"*docs/deployment.md*) ;;
    *) printf 'FAIL: refusal for %s does not point at docs/deployment.md: %s\n' "$guard" "$message" >&2; failures=$((failures + 1)) ;;
  esac
  status=0
  (dit_refuse_guards --allow interrupted-deploy) 2>/dev/null || status=$?
  if [ "$guard" = ".deploy-in-progress" ]; then
    check "deploy-app may start over an interrupted deployment" 0 "$status"
  else
    check "guard $guard also refuses deploy-app" 3 "$status"
  fi
  rm -f "$APP_DIR/$guard"
done
# A restore of the Umami database may proceed over both the Umami guard and an interrupted deployment, and over nothing else.
: >"$APP_DIR/.umami-recovery"
: >"$APP_DIR/.deploy-in-progress"
status=0
(dit_refuse_guards --allow umami-recovery interrupted-deploy) 2>/dev/null || status=$?
check "named guards are allowed together" 0 "$status"
status=0
(dit_refuse_guards --allow umami-recovery) 2>/dev/null || status=$?
check "a guard that is not named still refuses" 3 "$status"
: >"$APP_DIR/flags/maintenance"
status=0
(dit_refuse_guards --allow umami-recovery interrupted-deploy) 2>/dev/null || status=$?
check "an allowance does not cover another guard" 3 "$status"
rm -f "$APP_DIR/.umami-recovery" "$APP_DIR/.deploy-in-progress" "$APP_DIR/flags/maintenance"
APP_DIR="$saved_app_dir"

# Misuse of the helper is an error of its own (2), not a refusal and not silent acceptance.
status=0
message="$( (dit_refuse_guards --allow no-such-guard) 2>&1)" || status=$?
check "an unknown guard name is an error" 2 "$status"
case "$message" in
  *no-such-guard*) ;;
  *) printf 'FAIL: the message does not name the unknown guard: %s\n' "$message" >&2; failures=$((failures + 1)) ;;
esac
status=0
(dit_refuse_guards interrupted-deploy) 2>/dev/null || status=$?
check "a first argument other than --allow is an error" 2 "$status"
status=0
(dit_refuse_guards --allow umami-recovery bogus) 2>/dev/null || status=$?
check "one unknown name among valid ones is an error" 2 "$status"

# Clearing the Umami guard first resets the marker, so the next deploy-app does not write the guard again.
APP_DIR="$work/guards"
printf "DIT_BUNDLE_SHA='abc'\nUMAMI_DUMP='/d/umami.age'\nUMAMI_CHANGED='1'\nUMAMI_STARTED='1'\n" >"$APP_DIR/.deploy-in-progress"
: >"$APP_DIR/.umami-recovery"
dit_clear_umami_recovery
check "the guard is cleared" absent "$([ -e "$APP_DIR/.umami-recovery" ] && echo present || echo absent)"
check "the marker stays for the repair" present "$([ -e "$APP_DIR/.deploy-in-progress" ] && echo present || echo absent)"
check "the marker no longer says Umami changed" 0 "$(dit_env_get "$APP_DIR/.deploy-in-progress" UMAMI_CHANGED)"
check "the marker no longer says Umami started" 0 "$(dit_env_get "$APP_DIR/.deploy-in-progress" UMAMI_STARTED)"
check "the marker keeps the bundle" abc "$(dit_env_get "$APP_DIR/.deploy-in-progress" DIT_BUNDLE_SHA)"
check "the marker keeps the dump" /d/umami.age "$(dit_env_get "$APP_DIR/.deploy-in-progress" UMAMI_DUMP)"
status=0
(dit_refuse_guards --allow interrupted-deploy) 2>/dev/null || status=$?
check "deploy-app is no longer refused after the clear" 0 "$status"
rm -f "$APP_DIR/.deploy-in-progress"
: >"$APP_DIR/.umami-recovery"
dit_clear_umami_recovery
check "the guard is cleared without a marker too" absent "$([ -e "$APP_DIR/.umami-recovery" ] && echo present || echo absent)"
APP_DIR="$saved_app_dir"

# Git Bash on Windows copies instead of linking; the tests below read links, so they run only where "ln -s" makes one.
mkdir -p "$work/linkprobe/target"
ln -s target "$work/linkprobe/link" 2>/dev/null || true
if [ -L "$work/linkprobe/link" ]; then
# The guard is cleared only when the records and the running Umami image agree with the restored database, so the old image
# never runs on a database the new one migrated.
# shellcheck source=../remote/deploy-lib.sh
. "$remote/deploy-lib.sh"
APP_DIR="$work/umami-clear"
mkdir -p "$APP_DIR/bundles/good/compose" "$APP_DIR/bundles/failed/compose"
printf 'services:\n  umami:\n    image: umami:old\n' >"$APP_DIR/bundles/good/compose/compose.yaml"
printf 'services:\n  umami:\n    image: umami:new\n' >"$APP_DIR/bundles/failed/compose/compose.yaml"
printf "DIT_BUNDLE_SHA='good'\n" >"$APP_DIR/last-good.env"
printf "DIT_BUNDLE_SHA='failed'\nUMAMI_DUMP='/d'\nUMAMI_CHANGED='1'\nUMAMI_STARTED='1'\n" >"$APP_DIR/.deploy-in-progress"
: >"$APP_DIR/.umami-recovery"
ln -sfn bundles/failed "$APP_DIR/current-bundle"
fake_running="umami:old"
dit_running_umami_image() { printf '%s\n' "$fake_running"; }
status=0
dit_clear_umami_recovery 2>/dev/null || status=$?
check "clear refuses while current-bundle is on the failed bundle" 1 "$status"
check "the guard stays when current-bundle is on the failed bundle" present "$([ -e "$APP_DIR/.umami-recovery" ] && echo present || echo absent)"
check "the marker is untouched when the clear is refused" 1 "$(dit_env_get "$APP_DIR/.deploy-in-progress" UMAMI_CHANGED)"
ln -sfn bundles/good "$APP_DIR/current-bundle"
fake_running="umami:new"
status=0
dit_clear_umami_recovery 2>/dev/null || status=$?
check "clear refuses while the new Umami image runs" 1 "$status"
check "the guard stays while the new Umami image runs" present "$([ -e "$APP_DIR/.umami-recovery" ] && echo present || echo absent)"
fake_running=""
status=0
dit_clear_umami_recovery 2>/dev/null || status=$?
check "clear refuses while no Umami container runs" 1 "$status"
fake_running="umami:old"
status=0
dit_clear_umami_recovery 2>/dev/null || status=$?
check "clear succeeds on the last good bundle with its Umami image running" 0 "$status"
check "the guard is gone after a matching clear" absent "$([ -e "$APP_DIR/.umami-recovery" ] && echo present || echo absent)"
check "the marker was reset after a matching clear" 0 "$(dit_env_get "$APP_DIR/.deploy-in-progress" UMAMI_CHANGED)"

# A leftover .import-pending is settled by switching content/current back to the recorded previous target.
APP_DIR="$work/pending"
sha_new="$(printf 'b%.0s' $(seq 1 40))"
sha_old="$(printf 'a%.0s' $(seq 1 40))"
mkdir -p "$APP_DIR/content/releases/$sha_new" "$APP_DIR/content/releases/$sha_old"
ln -sfn "releases/$sha_new" "$APP_DIR/content/current"
printf '%s\nreleases/%s\n' "$sha_new" "$sha_old" >"$APP_DIR/.import-pending"
status=0
dit_revert_import_pending 2>/dev/null || status=$?
check "revert of a pending import succeeds" 0 "$status"
check "current is back on the previous release" "releases/$sha_old" "$(readlink "$APP_DIR/content/current")"
check "the pending file is removed" absent "$([ -e "$APP_DIR/.import-pending" ] && echo present || echo absent)"
status=0
dit_revert_import_pending 2>/dev/null || status=$?
check "no pending file is a no-op" 0 "$status"
# Current already moved on: the file goes, current stays.
printf '%s\nreleases/%s\n' "$sha_new" "$sha_old" >"$APP_DIR/.import-pending"
ln -sfn "releases/$sha_old" "$APP_DIR/content/current"
mkdir -p "$APP_DIR/content/releases/other"
ln -sfn "releases/other" "$APP_DIR/content/current"
dit_revert_import_pending 2>/dev/null
check "current that names another release is left alone" "releases/other" "$(readlink "$APP_DIR/content/current")"
check "the pending file is removed when current moved on" absent "$([ -e "$APP_DIR/.import-pending" ] && echo present || echo absent)"
# First delivery: no previous target returns to the fresh-install state.
ln -sfn "releases/$sha_new" "$APP_DIR/content/current"
printf '%s\n\n' "$sha_new" >"$APP_DIR/.import-pending"
dit_revert_import_pending 2>/dev/null
check "an empty previous target removes current" absent "$([ -e "$APP_DIR/content/current" ] || [ -L "$APP_DIR/content/current" ] && echo present || echo absent)"
# A malformed file is an error and is kept.
printf 'not-a-sha\n' >"$APP_DIR/.import-pending"
status=0
dit_revert_import_pending 2>/dev/null || status=$?
check "a malformed pending file is an error" 1 "$status"
check "a malformed pending file is kept" present "$([ -e "$APP_DIR/.import-pending" ] && echo present || echo absent)"
# The third line is the active content hash before the switch: a different hash now means the import committed.
hash_a="$(printf 'c%.0s' $(seq 1 64))"
hash_b="$(printf 'd%.0s' $(seq 1 64))"
fake_hash="$hash_b"
dit_active_content_hash() { [ -n "$fake_hash" ] && printf '%s\n' "$fake_hash"; }
write_pending_file() { printf '%s\nreleases/%s\n%s\n' "$sha_new" "$sha_old" "$1" >"$APP_DIR/.import-pending"; ln -sfn "releases/$sha_new" "$APP_DIR/content/current"; }
write_pending_file "$hash_a"
status=0
dit_revert_import_pending 2>/dev/null || status=$?
check "an import that committed is accepted" 0 "$status"
check "an import that committed keeps current on the new release" "releases/$sha_new" "$(readlink "$APP_DIR/content/current")"
check "an import that committed clears the guard" absent "$([ -e "$APP_DIR/.import-pending" ] && echo present || echo absent)"
write_pending_file "$hash_b"
dit_revert_import_pending 2>/dev/null
check "an import that did not commit is reverted" "releases/$sha_old" "$(readlink "$APP_DIR/content/current")"
check "the reverted guard is gone" absent "$([ -e "$APP_DIR/.import-pending" ] && echo present || echo absent)"
write_pending_file "none"
fake_hash="$hash_a"
dit_revert_import_pending 2>/dev/null
check "a first import that committed keeps current" "releases/$sha_new" "$(readlink "$APP_DIR/content/current")"
write_pending_file "$hash_a"
fake_hash="$hash_a"
mv "$APP_DIR/content/releases/$sha_old" "$APP_DIR/content/releases/gone"
status=0
message="$(dit_revert_import_pending 2>&1)" || status=$?
check "a missing previous release refuses" 1 "$status"
check "a missing previous release leaves current alone" "releases/$sha_new" "$(readlink "$APP_DIR/content/current")"
check "a missing previous release keeps the guard" present "$([ -e "$APP_DIR/.import-pending" ] && echo present || echo absent)"
case "$message" in
  *"no longer exists"*"Content import"*) ;;
  *) printf 'FAIL: the missing-previous message is not clear: %s\n' "$message" >&2; failures=$((failures + 1)) ;;
esac
mv "$APP_DIR/content/releases/gone" "$APP_DIR/content/releases/$sha_old"
fake_hash=""
status=0
dit_revert_import_pending 2>/dev/null || status=$?
check "an unreadable database refuses" 1 "$status"
check "an unreadable database keeps the guard" present "$([ -e "$APP_DIR/.import-pending" ] && echo present || echo absent)"
check "an unreadable database leaves current alone" "releases/$sha_new" "$(readlink "$APP_DIR/content/current")"
rm -f "$APP_DIR/.import-pending"
APP_DIR="$saved_app_dir"

else
  echo "SKIP: symbolic links are not available, the current-bundle and content/current tests were not run"
fi

# A closed log pipe must not make dit_log fail, or a rollback under "set -e" would stop half-way.
status=0
dit_log "to a closed stderr" 2>&- || status=$?
check "dit_log survives a closed stderr" 0 "$status"

if [ "$failures" -ne 0 ]; then
  printf '%d failure(s)\n' "$failures" >&2
  exit 1
fi
echo "lib tests passed"
