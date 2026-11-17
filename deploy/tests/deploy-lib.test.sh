#!/usr/bin/env bash
# Tests of the pure helpers in remote/deploy-lib.sh; no Docker involved.
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
# shellcheck source=../remote/lib.sh
. "$remote/lib.sh"
# shellcheck source=../remote/deploy-lib.sh
. "$remote/deploy-lib.sh"

status_of() {
  local status=0
  "$@" >/dev/null 2>&1 || status=$?
  echo "$status"
}

# shellcheck disable=SC2016 # the dollar sign is the literal under test
compose() { # <file> <image-line>
  printf 'services:\n  app:\n    image: ${APP_IMAGE}\n  umami:\n%s\n    restart: unless-stopped\n' "$2" >"$1"
}

# --- umami image -------------------------------------------------------------------------------------------------------
compose "$work/a.yaml" "    image: ghcr.io/umami/umami:1@sha256:aaa"
compose "$work/b.yaml" "    image: ghcr.io/umami/umami:2@sha256:bbb"
# shellcheck disable=SC2016 # the dollar sign is the literal under test
compose "$work/var.yaml" '    image: ${UMAMI_IMAGE}'
compose "$work/comment.yaml" "    image: ghcr.io/umami/umami:1 # pinned"
printf 'services:\n  app:\n    image: x\n' >"$work/none.yaml"

check "image is read" "ghcr.io/umami/umami:1@sha256:aaa" "$(dit_umami_image "$work/a.yaml")"
check "missing file is an error" 1 "$(status_of dit_umami_image "$work/missing.yaml")"
check "no umami service is an error" 1 "$(status_of dit_umami_image "$work/none.yaml")"
check "a variable is an error" 1 "$(status_of dit_umami_image "$work/var.yaml")"
check "trailing text is an error" 1 "$(status_of dit_umami_image "$work/comment.yaml")"
check "same image is unchanged" 0 "$(dit_umami_changed "$work/a.yaml" "$work/a.yaml")"
check "different image is changed" 1 "$(dit_umami_changed "$work/a.yaml" "$work/b.yaml")"
check "unparseable new image is an error, not unchanged" 1 "$(status_of dit_umami_changed "$work/a.yaml" "$work/var.yaml")"
check "unparseable old image is an error, not unchanged" 1 "$(status_of dit_umami_changed "$work/none.yaml" "$work/a.yaml")"
check "missing new compose file is an error" 1 "$(status_of dit_umami_changed "$work/a.yaml" "$work/missing.yaml")"

# --- bundle hash -------------------------------------------------------------------------------------------------------
mkdir -p "$work/bundle/remote"
printf 'one\n' >"$work/bundle/remote/x.sh"
first="$(dit_hash_bundle "$work/bundle")"
check "hash is stable" "$first" "$(dit_hash_bundle "$work/bundle")"
check "hash is a sha256" 64 "${#first}"
printf 'two\n' >"$work/bundle/remote/x.sh"
check "content changes the hash" 1 "$([ "$first" != "$(dit_hash_bundle "$work/bundle")" ] && echo 1 || echo 0)"
printf 'one\n' >"$work/bundle/remote/x.sh"
check "restored content restores the hash" "$first" "$(dit_hash_bundle "$work/bundle")"
ln -s x.sh "$work/bundle/remote/link"
check "a symlink changes the hash" 1 "$([ "$first" != "$(dit_hash_bundle "$work/bundle")" ] && echo 1 || echo 0)"
rm "$work/bundle/remote/link"
chmod 755 "$work/bundle/remote/x.sh"
with_mode="$(dit_hash_bundle "$work/bundle")"
chmod 700 "$work/bundle/remote/x.sh"
if [ "$(stat -c %a "$work/bundle/remote/x.sh")" = "700" ]; then
  check "a mode change changes the hash" 1 "$([ "$with_mode" != "$(dit_hash_bundle "$work/bundle")" ] && echo 1 || echo 0)"
else
  echo "SKIP: this file system ignores chmod, the mode hash was not exercised"
fi

# --- list of good bundles ------------------------------------------------------------------------------------------------
id() { printf '%064d' "$1"; }
list="$work/good-bundles"
dit_remember_good_bundle "$list" "$(id 1)"
dit_remember_good_bundle "$list" "$(id 2)"
dit_remember_good_bundle "$list" "$(id 1)"
check "the newest entry is first and entries are unique" "$(printf '%s\n%s' "$(id 1)" "$(id 2)")" "$(cat "$list")"
dit_remember_good_bundle "$list" "$(id 3)"
dit_remember_good_bundle "$list" "$(id 4)"
check "three entries are kept" 3 "$(wc -l <"$list" | tr -d ' ')"
check "the oldest entry was dropped" 0 "$(grep -c "$(id 2)" "$list" || true)"
check "an invalid id is refused" 1 "$(status_of dit_remember_good_bundle "$list" "not-a-sha")"
before="$(cat "$list")"
mkdir "$work/list-dir"
printf 'x\n' >"$work/list-dir/file"
check "an unreadable list is an error" 1 "$(status_of dit_remember_good_bundle "$work/list-dir" "$(id 5)")"
check "a failed update leaves the list alone" "$before" "$(cat "$list")"
check "a failed update leaves no temp file" 0 "$(find "$work" -maxdepth 1 -name 'list-dir.*' | wc -l | tr -d ' ')"

# --- pruning fails closed -------------------------------------------------------------------------------------------------
setup_bundles() {
  rm -rf "$work/bundles"
  mkdir -p "$work/bundles/other-dir"
  for n in 1 2 3 4; do
    mkdir -p "$work/bundles/$(id "$n")"
  done
}
remaining() {
  find "$work/bundles" -mindepth 1 -maxdepth 1 -type d -printf '%f\n' | LC_ALL=C sort | tr '\n' ' '
}
everything="$(printf '%s %s %s %s other-dir ' "$(id 1)" "$(id 2)" "$(id 3)" "$(id 4)" | tr ' ' '\n' | LC_ALL=C sort | tr '\n' ' ')"

setup_bundles
check "a missing list prunes nothing and fails" 1 "$(status_of dit_prune_bundles "$work/bundles" "$work/absent" "$(id 1)")"
check "a missing list removed nothing" "$everything" "$(remaining)"
: >"$work/empty-list"
check "an empty list prunes nothing and fails" 1 "$(status_of dit_prune_bundles "$work/bundles" "$work/empty-list" "$(id 1)")"
check "an empty list removed nothing" "$everything" "$(remaining)"
printf 'garbage\n' >"$work/garbage-list"
check "a garbage list prunes nothing and fails" 1 "$(status_of dit_prune_bundles "$work/bundles" "$work/garbage-list" "$(id 1)")"
check "a garbage list removed nothing" "$everything" "$(remaining)"

printf '%s\n%s\n' "$(id 1)" "$(id 2)" >"$work/ok-list"
dit_prune_bundles "$work/bundles" "$work/ok-list" "$(id 3)" ""
check "unlisted, unprotected bundles are removed; protected, listed and foreign directories stay" \
  "$(printf '%s %s %s other-dir ' "$(id 1)" "$(id 2)" "$(id 3)" | tr ' ' '\n' | LC_ALL=C sort | tr '\n' ' ')" "$(remaining)"

# A protected bundle survives even when the list does not mention it.
setup_bundles
printf '%s\n' "$(id 9)" >"$work/other-list"
dit_prune_bundles "$work/bundles" "$work/other-list" "$(id 2)" "$(id 4)"
check "protected bundles survive a list that omits them" \
  "$(printf '%s %s other-dir ' "$(id 2)" "$(id 4)" | tr ' ' '\n' | LC_ALL=C sort | tr '\n' ' ')" "$(remaining)"

if [ "$failures" -ne 0 ]; then
  printf '%d failure(s)\n' "$failures" >&2
  exit 1
fi
echo "deploy-lib tests passed"
