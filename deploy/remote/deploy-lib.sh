# shellcheck shell=bash
# Sourced by deploy-app.sh after lib.sh. Pure helpers (files in, status out) so they can be tested without Docker; like
# lib.sh it sets no shell options and installs no traps. Every function fails closed: when it cannot be sure, it reports
# an error and changes nothing.

# Image of the umami service in a compose file. The compose file declares it as a literal one level below "  umami:";
# anything else (no match, a variable, trailing text) is an error, because a guessed value could hide a real change.
dit_umami_image() {
  local file="$1" image
  if [ ! -f "$file" ]; then
    dit_log "no compose file at $file"
    return 1
  fi
  image="$(awk '
    /^  umami:[[:space:]]*$/ { in_service = 1; next }
    in_service && /^  [^ ]/ { in_service = 0 }
    in_service && /^    image:/ { sub(/^    image:[[:space:]]*/, ""); print; exit }
  ' "$file")" || return 1
  case "$image" in
    '' | *[[:space:]]* | *'$'*)
      dit_log "cannot determine the umami image in $file"
      return 1
      ;;
  esac
  printf '%s\n' "$image"
}

# Prints 1 when the umami image differs between two compose files, 0 when it does not; fails when either is unparseable.
dit_umami_changed() {
  local old new
  old="$(dit_umami_image "$1")" || return 1
  new="$(dit_umami_image "$2")" || return 1
  if [ "$old" = "$new" ]; then
    echo 0
  else
    echo 1
  fi
}

# Content, modes and symlinks of a bundle: two bundles that differ only in a mode or a link get different directories.
dit_hash_bundle() {
  local dir="$1"
  (
    cd "$dir" || exit 1
    {
      find . -type f -print0 | LC_ALL=C sort -z | xargs -0 -r sha256sum
      find . -printf '%m %y %p %l\n' | LC_ALL=C sort
    } | sha256sum | cut -d' ' -f1
  )
}

dit_is_bundle_id() {
  [[ "$1" =~ ^[0-9a-f]{64}$ ]]
}

# Puts <sha> first in the list of good bundles and keeps three entries. Written by temp file and rename; on any error the
# old list stays as it was and the status is non-zero.
dit_remember_good_bundle() {
  local list="$1" sha="$2" old="" rc=0 tmp
  dit_is_bundle_id "$sha" || return 1
  if [ -e "$list" ] && [ ! -f "$list" ]; then
    dit_log "$list is not a regular file"
    return 1
  fi
  if [ -s "$list" ]; then
    old="$(grep -vx -- "$sha" "$list")" || rc=$?
    if [ "$rc" -gt 1 ]; then
      return 1
    fi
  fi
  tmp="$(mktemp "$list.XXXXXX")" || return 1
  if ! printf '%s\n%s\n' "$sha" "$old" |
    awk 'length($0) == 64 && $0 ~ /^[0-9a-f]+$/ && !seen[$0]++ && n++ < 3' >"$tmp" ||
    [ "$(sed -n 1p "$tmp")" != "$sha" ] || ! mv -f "$tmp" "$list"; then
    rm -f "$tmp"
    return 1
  fi
}

# Removes bundle directories that are neither in the list nor protected. Fails closed: a missing, unreadable or empty list
# removes nothing, and the protected ids (the running, the previous good and the last good bundle) are never removed even
# when the list is wrong. Only directories named like a bundle id are ever touched.
dit_prune_bundles() {
  local bundles="$1" list="$2" dir name protected rc entry
  shift 2
  if [ ! -r "$list" ] || [ ! -s "$list" ] || ! grep -qE '^[0-9a-f]{64}$' "$list"; then
    dit_log "the list of good bundles is missing or empty; no bundle was pruned"
    return 1
  fi
  for dir in "$bundles"/*/; do
    [ -d "$dir" ] || continue
    name="$(basename "$dir")"
    dit_is_bundle_id "$name" || continue
    rc=0
    grep -qx -- "$name" "$list" || rc=$?
    if [ "$rc" -eq 0 ]; then
      continue
    elif [ "$rc" -ne 1 ]; then
      dit_log "could not read the list of good bundles; pruning stopped"
      return 1
    fi
    protected=0
    for entry in "$@"; do
      if [ "$entry" = "$name" ]; then
        protected=1
      fi
    done
    if [ "$protected" -eq 0 ]; then
      rm -rf -- "$dir"
    fi
  done
}
