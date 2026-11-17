#!/usr/bin/env bash
# Delivers one immutable content release and asks the running application to import it.
#
#   receive-content.sh <staging-dir> <commit-sha>      (stdin: a tar.gz whose top directory is content/)
#
# Exit codes: 0 delivered; 1 failed or the import was rejected or refused (current is back on the previous release);
# 2 invalid input or an unsafe archive, nothing changed; 3 a guard file refuses the delivery; 4 the deployment lock was
# not obtained in time, nothing changed; 5 the content's schema-version is newer than the running application supports,
# nothing switched (deploy-app delivers it after the application that supports it); 6 the import gave no definitive
# answer twice; 7 the content's schema-version is older than the running application supports, nothing switched (no
# follow-up can deliver it).
#
# The import endpoint is the only source of truth for the outcome. 200 (imported or unchanged) means the release on
# disk is in the database, 422 means it was rejected and never committed. A transport failure or any other status is
# not an answer, so the call is made once more: the second call queues on the importer's advisory lock behind an import
# that is still running and then answers definitively. $APP_DIR/.import-pending (release sha, then the release current
# pointed to before, then the active content hash) exists from before the switch until a definitive answer. 401, 403 and 404 mean the endpoint refused
# the call, so no import was started: the switch is undone like after a 422. After two other non-answers the script exits 6
# and leaves current and the guard as they are, because an import may still be reading /content/current. The next run
# settles the guard first, by importing what current points to, and only then proceeds. deploy-app.sh and restore.sh --replace
# trainer settle it differently, because they are about to restart the application: they keep current when the active content
# hash moved (the import committed) and otherwise switch it back to the previous target recorded on the second line
# (dit_revert_import_pending in lib.sh), then remove the file, so the startup import reads a release the import accepted.
#
# DIT_TEST_IMPORT_URL redirects the import calls to a fault-injecting proxy and DIT_TEST_KILL_AT stops the run at a named
# point (after-pending, after-switch, after-import); both are honoured only with DIT_TEST_HOOKS=1.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "$here/lib.sh"
export APP_DIR

content_dir="$APP_DIR/content"
releases="$content_dir/releases"
pending="$APP_DIR/.import-pending"
env_file="$APP_DIR/.env"
version_url="http://127.0.0.1:8081/internal/version"
import_url="http://127.0.0.1:8081/internal/content/import"
keep_releases=5
max_archive_bytes=67108864
max_unpacked_bytes=268435456
# The repository content tree has about 430 entries; the cap leaves room for growth and stops an archive of millions of empty files.
max_archive_entries=20000
sha_pattern='^[0-9a-f]{40}$'
target_pattern="$dit_content_target_pattern"

if [ "${DIT_TEST_HOOKS:-}" = "1" ] && [ -n "${DIT_TEST_IMPORT_URL:-}" ]; then
  import_url="$DIT_TEST_IMPORT_URL"
fi

if [ "$#" -ne 2 ]; then
  dit_log "usage: receive-content.sh <staging-dir> <commit-sha> (the archive on stdin)"
  exit 2
fi
staging="$1"
sha="$2"

import_status=""
import_body=""
import_token=""

# Used by smoke.sh to stop the run at a named point. Both variables must be set, so a stray DIT_TEST_KILL_AT in a
# production environment does nothing.
kill_point() {
  if [ "${DIT_TEST_HOOKS:-}" = "1" ] && [ "${DIT_TEST_KILL_AT:-}" = "$1" ]; then
    kill -KILL $$
  fi
}

# --- input -----------------------------------------------------------------------------------------------------------

validate_input() {
  local real_app real_staging
  if ! [[ "$sha" =~ $sha_pattern ]]; then
    dit_log "the commit must be a full 40-digit lowercase SHA"
    exit 2
  fi
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
  import_body="$staging/import.json"
}

# The stream is spooled before the lock is taken: the sender never blocks on a refusal, and the lock is held only for
# the work on the server.
spool_archive() {
  local archive="$1" size
  head -c $((max_archive_bytes + 1)) >"$archive"
  size="$(stat -c %s "$archive")"
  if [ "$size" -eq 0 ] || [ "$size" -gt "$max_archive_bytes" ]; then
    dit_log "the archive is empty or larger than $max_archive_bytes bytes"
    exit 2
  fi
}

# Only regular files and directories under content/ are accepted: an absolute path, a ".." component, a link or a device
# would let the archive write (or later serve) something outside the release. Backslashes are refused outright, so the
# escaped listing below cannot hide a newline in a name.
check_archive() {
  local archive="$1" names listing types unpacked name
  if ! names="$(tar -tzf "$archive" --quoting-style=escape 2>/dev/null)" ||
    ! listing="$(tar -tzvf "$archive" --quoting-style=escape 2>/dev/null)"; then
    dit_log "the upload is not a readable tar.gz archive"
    exit 2
  fi
  types="$(cut -c1 <<<"$listing")"
  # The third column of the verbose listing is the size; the compressed cap alone does not bound what a bomb unpacks to.
  unpacked="$(awk '{ total += $3 } END { print total + 0 }' <<<"$listing")"
  if [ "$(wc -l <<<"$names")" -gt "$max_archive_entries" ]; then
    dit_log "the archive has more than $max_archive_entries entries"
    exit 2
  fi
  if [ "$unpacked" -gt "$max_unpacked_bytes" ]; then
    dit_log "the archive unpacks to more than $max_unpacked_bytes bytes"
    exit 2
  fi
  if [ "$(printf '%s\n' "$names" | wc -l)" -ne "$(printf '%s\n' "$types" | wc -l)" ] || [ -n "${types//[-d$'\n']/}" ]; then
    dit_log "the archive contains entries that are neither regular files nor directories"
    exit 2
  fi
  while IFS= read -r name; do
    case "$name" in
      *\\* | /* | .. | ../* | */../* | */.. | content/.manifest)
        dit_log "the archive contains an unsafe path"
        exit 2
        ;;
      content | content/*) ;;
      *)
        dit_log "every archive entry must be under content/"
        exit 2
        ;;
    esac
  done <<<"$names"
}

extract_archive() {
  local archive="$1" tree="$2"
  rm -rf "$tree"
  install -d -m 755 "$tree"
  tar -xzf "$archive" -C "$tree" --strip-components=1 --no-same-owner --no-same-permissions
  # The application runs as another user and must read what the deploy user extracted.
  chmod -R u+rwX,go+rX,go-w "$tree"
  if [ ! -f "$tree/schema-version" ]; then
    dit_log "the archive has no content/schema-version"
    exit 2
  fi
}

# One "sha256  ./path" line per file, in byte order of the path; the release's own .manifest is not part of the tree.
write_manifest() {
  local tree="$1" out="$2"
  (cd "$tree" && find . -type f ! -path ./.manifest -print0 | LC_ALL=C sort -z | xargs -0 -r sha256sum) >"$out"
}

# An interrupted run leaves <sha>.tmp (a release being built) or <sha>.del.<pid> (one being pruned); neither is a release.
sweep_leftovers() {
  if [ -d "$releases" ]; then
    find "$releases" -mindepth 1 -maxdepth 1 -type d \( -name '*.tmp' -o -name '*.del.*' \) -exec rm -rf {} +
  fi
}

# --- releases --------------------------------------------------------------------------------------------------------

# A release is immutable: an existing directory for this sha is reused only when its recorded manifest matches.
install_release() {
  local tree="$1" manifest="$2" release="$releases/$sha" on_disk
  install -d -m 755 "$content_dir" "$releases"
  if [ -e "$release" ]; then
    # The stored manifest alone says nothing about a tree that lost a file; the files on disk must match it too.
    on_disk="$staging/release.manifest"
    write_manifest "$release" "$on_disk"
    if [ -f "$release/.manifest" ] && cmp -s "$release/.manifest" "$manifest" && cmp -s "$on_disk" "$manifest"; then
      dit_log "release $sha already exists with the same content; reusing it"
      touch "$release"
      return 0
    fi
    dit_log "release $sha already exists and its files or manifest differ from this delivery; releases are immutable"
    exit 1
  fi
  mv "$tree" "$release.tmp"
  cp "$manifest" "$release.tmp/.manifest"
  chmod 644 "$release.tmp/.manifest"
  mv -T "$release.tmp" "$release"
}

check_schema_range() {
  local version body min max
  version="$(tr -d ' \t\r\n' <"$releases/$sha/schema-version")"
  if ! [[ "$version" =~ ^[0-9]{1,9}$ ]]; then
    dit_log "content/schema-version must contain a single non-negative integer"
    exit 2
  fi
  if ! body="$(curl -fsS --max-time 10 "$version_url")"; then
    dit_log "the application did not answer $version_url, so the schema range is unknown"
    exit 1
  fi
  min="$(sed -n 's/.*"contentSchemaMin":\([0-9]*\).*/\1/p' <<<"$body")"
  max="$(sed -n 's/.*"contentSchemaMax":\([0-9]*\).*/\1/p' <<<"$body")"
  if [ -z "$min" ] || [ -z "$max" ]; then
    dit_log "the version endpoint did not report a schema range"
    exit 1
  fi
  if [ $((10#$version)) -gt "$max" ]; then
    dit_log "content schema $version is newer than the supported range $min..$max; deploy-app will deliver this content after the app that supports it"
    exit 5
  fi
  if [ $((10#$version)) -lt "$min" ]; then
    dit_log "content schema $version is older than the supported range $min..$max; no later deployment can deliver it"
    exit 7
  fi
}

# A rejected release is removed through the same atomic rename as a pruned one, unless it is where current points after the
# switch-back (the previous target). A release held back by a schema skip (exit 5) is not rejected and stays.
discard_rejected_release() { # <sha>
  local current_target
  current_target="$(readlink "$content_dir/current" 2>/dev/null || true)"
  if [ "$current_target" = "releases/$1" ] || [ ! -d "$releases/$1" ]; then
    return 0
  fi
  mv -T "${releases:?}/$1" "${releases:?}/$1.del.$$"
  rm -rf "${releases:?}/$1.del.$$"
}

# The five newest releases and whatever current points to stay; only directories named like a sha are touched.
prune_releases() {
  local current_name rank=0 name
  current_name="$(readlink "$content_dir/current")"
  current_name="${current_name#releases/}"
  while read -r _ name; do
    if [[ "$name" =~ $sha_pattern ]]; then
      rank=$((rank + 1))
      if [ "$rank" -gt "$keep_releases" ] && [ "$name" != "$current_name" ]; then
        # The rename is atomic: a kill during the removal leaves a *.del.* directory (swept next run), never a release
        # with missing files that a later delivery of the same sha could reuse.
        mv -T "${releases:?}/$name" "${releases:?}/$name.del.$$"
        rm -rf "${releases:?}/$name.del.$$"
      fi
    fi
  done < <(find "$releases" -mindepth 1 -maxdepth 1 -type d -printf '%T@ %f\n' | LC_ALL=C sort -rn)
}

# --- import ----------------------------------------------------------------------------------------------------------

# Prints the HTTP status ("000" when curl failed). No -f (a 422 would lose its body) and no client timeout: the import
# runs to its end on the server, and a client that waits less would only create the ambiguity this script resolves.
import_once() {
  local code
  code="$(printf 'Authorization: Bearer %s\n' "$import_token" | curl -sS -X POST -o "$import_body" -w '%{http_code}' -H @- "$import_url")" || code=000
  printf '%s\n' "$code"
}

# The token is read once, before anything is switched: a missing token must not leave current on an unaccepted release.
load_token() {
  if ! import_token="$(dit_env_get "$env_file" CONTENT_IMPORT_TOKEN)" || [ -z "$import_token" ]; then
    dit_log "CONTENT_IMPORT_TOKEN is not available in $env_file"
    exit 2
  fi
}

# Sets import_status to 200, 422 or, when the endpoint refused the call (nothing was started), 401, 403 or 404. Returns 1
# after two calls without any of these.
import_until_definitive() {
  local attempt code
  for attempt in 1 2; do
    : >"$import_body"
    code="$(import_once)"
    case "$code" in
      200 | 422 | 401 | 403 | 404)
        import_status="$code"
        return 0
        ;;
    esac
    dit_log "the import call $attempt gave no definitive answer (HTTP $code)"
    if [ "$attempt" -eq 1 ]; then
      sleep 2
    fi
  done
  return 1
}

print_issues() {
  dit_log "the import was rejected:"
  head -c 20000 "$import_body" >&2 || true
  printf '\n' >&2
}

refused() { # true when the endpoint refused the call, so no import was started
  [ "$import_status" = "401" ] || [ "$import_status" = "403" ] || [ "$import_status" = "404" ]
}

# The third line is the active content hash before the switch ("none" for an empty database, empty when the application did
# not say): deploy-app.sh and restore.sh use it to tell the lost answer of an import that committed from one that did not.
write_pending() { # <sha> <previous target> <active hash before>
  printf '%s\n%s\n%s\n' "$1" "$2" "$3" >"$pending.tmp"
  mv -f "$pending.tmp" "$pending"
}

fail_unresolved() {
  dit_log "no definitive answer from the import endpoint twice; current and $pending are left as they are because an import may still be reading the release. The next delivery settles it first."
  exit 6
}

# The guard means an earlier delivery never learned its outcome. The import of what current points to queues behind any
# import still running, and its definitive answer settles the earlier delivery.
settle_pending() {
  local pending_sha pending_previous current_target
  if [ ! -e "$pending" ]; then
    return 0
  fi
  pending_sha=""
  pending_previous=""
  {
    IFS= read -r pending_sha || true
    IFS= read -r pending_previous || true
  } <"$pending"
  if ! [[ "$pending_sha" =~ $sha_pattern ]] || { [ -n "$pending_previous" ] && ! [[ "$pending_previous" =~ $target_pattern ]]; }; then
    dit_log "$pending is malformed; resolve it by hand (docs/deployment.md)"
    exit 2
  fi
  dit_log "settling the unresolved import of $pending_sha before this delivery"
  if ! import_until_definitive; then
    fail_unresolved
  fi
  if [ "$import_status" = "200" ]; then
    rm -f "$pending"
    dit_log "the earlier delivery of $pending_sha is in the database"
    return 0
  fi
  if refused; then
    dit_log "the import endpoint refused the call (HTTP $import_status); check CONTENT_IMPORT_TOKEN. $pending is kept and nothing was switched."
    exit 1
  fi
  current_target="$(readlink "$content_dir/current" 2>/dev/null || true)"
  if [ "$current_target" = "releases/$pending_sha" ]; then
    dit_restore_content_current "$pending_previous"
    dit_log "the earlier delivery of $pending_sha was rejected; current is back on ${pending_previous:-the fresh-install state}"
  else
    dit_log "the earlier delivery of $pending_sha was rejected and current no longer points to it; left alone"
  fi
  rm -f "$pending"
  discard_rejected_release "$pending_sha"
}

# --- run -------------------------------------------------------------------------------------------------------------

validate_input
archive="$staging/content.tar.gz"
tree="$staging/content"
manifest="$staging/content.manifest"
spool_archive "$archive"

if ! dit_lock; then
  exit 4
fi
# .import-pending is not one of the guards: this script settles it itself, right below.
# shellcheck disable=SC2119 # no argument means "allow no guard"
dit_refuse_guards
load_token
sweep_leftovers
settle_pending

check_archive "$archive"
extract_archive "$archive" "$tree"
write_manifest "$tree" "$manifest"
install_release "$tree" "$manifest"
check_schema_range

old_target="$(readlink "$content_dir/current" 2>/dev/null || true)"
if [ -n "$old_target" ] && ! [[ "$old_target" =~ $target_pattern ]]; then
  dit_log "current points to '$old_target', which is not a release path; resolve it by hand before delivering"
  exit 2
fi
# Written before the switch: a run killed between the two steps leaves a guard whose settlement is harmless.
active_hash=""
if version_body="$(curl -fsS --max-time 5 "$version_url" 2>/dev/null)"; then
  active_hash="$(sed -n 's/.*"activeContentHash":"\([^"]*\)".*/\1/p' <<<"$version_body")"
  active_hash="${active_hash:-none}"
fi
write_pending "$sha" "$old_target" "$active_hash"
kill_point after-pending
dit_switch_content_current "releases/$sha"
kill_point after-switch
if ! import_until_definitive; then
  fail_unresolved
fi
kill_point after-import
if [ "$import_status" != "200" ]; then
  dit_restore_content_current "$old_target"
  rm -f "$pending"
  if refused; then
    dit_log "the import endpoint refused the call (HTTP $import_status); check CONTENT_IMPORT_TOKEN"
  else
    print_issues
    discard_rejected_release "$sha"
  fi
  dit_log "current is back on ${old_target:-the fresh-install state}"
  exit 1
fi
rm -f "$pending"
prune_releases
dit_log "content $sha is imported and current"
