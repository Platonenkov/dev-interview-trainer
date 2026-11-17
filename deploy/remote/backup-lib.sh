# shellcheck shell=bash
# Sourced by backup.sh, restore.sh and ops-check.sh after lib.sh. Pure helpers (files and text in, text and status out)
# so the rules that protect backups can be tested without Docker; like lib.sh it sets no shell options.

# File name of one dump: <db>-<UTC yyyyMMddTHHmmssZ>-<daily|predeploy>.dump.age. The timestamp sorts lexically.
dit_backup_name() {
  local db="$1" ts="$2" kind="$3"
  case "$db" in trainer | umami) ;; *) return 1 ;; esac
  [[ "$ts" =~ ^[0-9]{8}T[0-9]{6}Z$ ]] || return 1
  case "$kind" in daily | predeploy) ;; *) return 1 ;; esac
  printf '%s-%s-%s.dump.age\n' "$db" "$ts" "$kind"
}

# How many files of a kind survive retention (per database).
dit_backup_keep_count() {
  case "$1" in
    daily) echo 14 ;;
    predeploy) echo 5 ;;
    *) return 1 ;;
  esac
}

# Dump files in <dir> of one database and kind, newest first.
dit_backup_list() {
  local dir="$1" db="$2" kind="$3" file
  for file in "$dir/$db"-*-"$kind".dump.age; do
    [ -f "$file" ] || continue
    printf '%s\n' "$file"
  done | LC_ALL=C sort -r
}

# Dump files that were never confirmed uploaded, oldest first. A file counts as uploaded only through its marker.
dit_backup_pending() {
  local dir="$1" file
  for file in "$dir"/*.dump.age; do
    [ -f "$file" ] || continue
    [ -e "$file.uploaded" ] || printf '%s\n' "$file"
  done | LC_ALL=C sort
}

# Files retention may delete: everything past the newest <keep> of a database and kind that carries an upload marker. A
# file without its marker is never listed, however old: during an off-site outage it is the only copy (C18).
dit_backup_prune_candidates() {
  local dir="$1" db="$2" kind="$3" keep="$4" file n=0
  [[ "$keep" =~ ^[0-9]+$ ]] || return 1
  while IFS= read -r file; do
    n=$((n + 1))
    if [ "$n" -gt "$keep" ] && [ -e "$file.uploaded" ]; then
      printf '%s\n' "$file"
    fi
  done < <(dit_backup_list "$dir" "$db" "$kind")
}

# The plain-text sidecar of a trainer dump: what is needed to pick the right application image and migrations when the
# dump is restored. Not secret. <image> <commit> <last migration id> <content hash> <content release> <ts> <kind>
dit_backup_meta_trainer() {
  local value
  for value in "$@"; do
    case "$value" in *$'\n'* | *$'\r'*) return 1 ;; esac
  done
  [ "$#" -eq 7 ] || return 1
  printf 'DB=trainer\nKIND=%s\nCREATED_UTC=%s\nAPP_IMAGE=%s\nDIT_COMMIT=%s\nEF_LAST_MIGRATION=%s\nACTIVE_CONTENT_HASH=%s\nCONTENT_RELEASE=%s\n' \
    "$7" "$6" "$1" "$2" "$3" "$4" "$5"
}

# The sidecar of an umami dump records only the Umami image: it is the one thing that decides which schema the dump has.
# <image> <ts> <kind>
dit_backup_meta_umami() {
  local value
  for value in "$@"; do
    case "$value" in *$'\n'* | *$'\r'*) return 1 ;; esac
  done
  [ "$#" -eq 3 ] || return 1
  printf 'DB=umami\nKIND=%s\nCREATED_UTC=%s\nUMAMI_IMAGE=%s\n' "$3" "$2" "$1"
}

# 0 when the file holds an ISO UTC time younger than <max-seconds>; a missing, empty, unparsable or future-dated file
# is a failure, so a stale or absent record can never read as healthy. [now-epoch] exists for tests.
dit_age_within() {
  local file="$1" max="$2" now="${3:-}" recorded
  [ -s "$file" ] || return 1
  [[ "$max" =~ ^[0-9]+$ ]] || return 1
  recorded="$(date -u -d "$(head -n 1 "$file")" +%s 2>/dev/null)" || return 1
  now="${now:-$(date -u +%s)}"
  [ "$recorded" -le "$now" ] && [ $((now - recorded)) -lt "$max" ]
}

# Names from "<name> <restart count>" lines whose count exceeds <limit>.
dit_restarts_over() {
  local limit="$1" name count
  [[ "$limit" =~ ^[0-9]+$ ]] || return 1
  while read -r name count; do
    [[ "$count" =~ ^[0-9]+$ ]] || continue
    if [ "$count" -gt "$limit" ]; then
      printf '%s\n' "$name"
    fi
  done
}

# 0 when last-offsite may move: no dump of any age is still waiting for its upload (a predeploy run uploads only its own
# files, so an older pending daily dump must keep the record stale), and the run either uploaded something or wrote its
# dumps ("nothing happened" is not a fresh copy). <backups dir> <files uploaded this run> <1 when a dump failed>
dit_offsite_current() {
  local dir="$1" uploaded="$2" dump_failed="$3"
  [[ "$uploaded" =~ ^[0-9]+$ ]] && [[ "$dump_failed" =~ ^[01]$ ]] || return 1
  [ -z "$(dit_backup_pending "$dir")" ] || return 1
  [ "$uploaded" -gt 0 ] || [ "$dump_failed" -eq 0 ]
}

# 0 when <final> is an integer not smaller than <validated> (both must be integers): "the key ring did not shrink".
dit_count_not_below() {
  [[ "$1" =~ ^[0-9]+$ ]] && [[ "$2" =~ ^[0-9]+$ ]] && [ "$2" -ge "$1" ]
}

# "schema.table" of every table in the table of contents printed by "pg_restore -l" (stdin). "TABLE DATA" lines and every
# other object kind are skipped; the entry format is "<id>; <catalog> <oid> TABLE <schema> <name> <owner>".
dit_toc_tables() {
  awk '$4 == "TABLE" && $5 != "DATA" && NF >= 7 { print $5 "." $6 }'
}
