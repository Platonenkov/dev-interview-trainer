#!/usr/bin/env bash
# Tests of the pure helpers in remote/backup-lib.sh (naming, retention, sidecar content, record age); no Docker involved.
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
# shellcheck source=../remote/backup-lib.sh
. "$remote/backup-lib.sh"

status_of() {
  local status=0
  "$@" >/dev/null 2>&1 || status=$?
  echo "$status"
}

# --- names -------------------------------------------------------------------------------------------------------------
check "dump name" "trainer-20261116T030000Z-daily.dump.age" "$(dit_backup_name trainer 20261116T030000Z daily)"
check "predeploy name" "umami-20261116T030000Z-predeploy.dump.age" "$(dit_backup_name umami 20261116T030000Z predeploy)"
check "unknown database is refused" 1 "$(status_of dit_backup_name postgres 20261116T030000Z daily)"
check "malformed timestamp is refused" 1 "$(status_of dit_backup_name trainer 2026-11-16 daily)"
check "unknown kind is refused" 1 "$(status_of dit_backup_name trainer 20261116T030000Z weekly)"
check "daily keeps 14" 14 "$(dit_backup_keep_count daily)"
check "predeploy keeps 5" 5 "$(dit_backup_keep_count predeploy)"
check "unknown kind has no retention" 1 "$(status_of dit_backup_keep_count weekly)"

# --- retention ---------------------------------------------------------------------------------------------------------
dir="$work/backups"
mkdir -p "$dir"
for day in $(seq 1 20); do
  stamp="202611$(printf '%02d' "$day")T030000Z"
  for db in trainer umami; do
    : >"$dir/$(dit_backup_name "$db" "$stamp" daily)"
    : >"$dir/$(dit_backup_name "$db" "$stamp" daily).meta"
    : >"$dir/$(dit_backup_name "$db" "$stamp" daily).uploaded"
  done
done
: >"$dir/$(dit_backup_name trainer 20261105T120000Z predeploy)"
: >"$dir/$(dit_backup_name trainer 20261105T120000Z predeploy).uploaded"

candidates="$(dit_backup_prune_candidates "$dir" trainer daily 14 | xargs -r -n1 basename)"
check "retention lists the six oldest trainer dailies" 6 "$(printf '%s\n' "$candidates" | wc -l | tr -d ' ')"
check "retention starts with the newest of the surplus" "trainer-20261106T030000Z-daily.dump.age" "$(printf '%s\n' "$candidates" | head -n 1)"
check "retention ends with the oldest" "trainer-20261101T030000Z-daily.dump.age" "$(printf '%s\n' "$candidates" | tail -n 1)"
case "$candidates" in
  *umami* | *predeploy*)
    printf 'FAIL: retention crossed a database or kind\n' >&2
    failures=$((failures + 1))
    ;;
esac
check "nothing is listed within the keep count" 0 "$(dit_backup_prune_candidates "$dir" trainer predeploy 5 | wc -l | tr -d ' ')"

# An upload outage: the oldest files never got their marker. They are the only copy and must survive.
rm -f "$dir/trainer-20261101T030000Z-daily.dump.age.uploaded" "$dir/trainer-20261103T030000Z-daily.dump.age.uploaded"
candidates="$(dit_backup_prune_candidates "$dir" trainer daily 14 | xargs -r -n1 basename)"
check "an un-uploaded file is never a candidate" 0 "$(printf '%s\n' "$candidates" | grep -c -e 20261101T -e 20261103T || true)"
check "the uploaded surplus is still listed" 4 "$(printf '%s\n' "$candidates" | wc -l | tr -d ' ')"

# Nothing is uploaded at all: nothing may be deleted, however many files there are.
find "$dir" -name '*.uploaded' -delete
check "no marker, no candidate" 0 "$(dit_backup_prune_candidates "$dir" trainer daily 14 | wc -l | tr -d ' ')"
check "a non-numeric keep count is refused" 1 "$(status_of dit_backup_prune_candidates "$dir" trainer daily many)"

check "pending lists every dump without a marker" 41 "$(dit_backup_pending "$dir" | wc -l | tr -d ' ')"
: >"$dir/trainer-20261101T030000Z-daily.dump.age.uploaded"
check "a marked file is not pending" 40 "$(dit_backup_pending "$dir" | wc -l | tr -d ' ')"
check "pending is oldest first" "trainer-20261102T030000Z-daily.dump.age" "$(dit_backup_pending "$dir" | head -n 1 | xargs basename)"
check "the sidecar is never pending" 0 "$(dit_backup_pending "$dir" | grep -c '\.meta$' || true)"

# --- sidecar content ---------------------------------------------------------------------------------------------------
meta="$(dit_backup_meta_trainer ghcr.io/x/dit:1 abc123 20261112131645_StackRetiredAt deadbeef release-7 20261116T030000Z daily)"
for line in DB=trainer KIND=daily CREATED_UTC=20261116T030000Z APP_IMAGE=ghcr.io/x/dit:1 DIT_COMMIT=abc123 \
  EF_LAST_MIGRATION=20261112131645_StackRetiredAt ACTIVE_CONTENT_HASH=deadbeef CONTENT_RELEASE=release-7; do
  check "trainer meta has $line" 1 "$(printf '%s\n' "$meta" | grep -cx -- "$line")"
done
check "trainer meta has no Umami key" 0 "$(printf '%s\n' "$meta" | grep -c UMAMI || true)"
meta="$(dit_backup_meta_umami ghcr.io/umami/umami:1@sha256:aaa 20261116T030000Z predeploy)"
for line in DB=umami KIND=predeploy CREATED_UTC=20261116T030000Z UMAMI_IMAGE=ghcr.io/umami/umami:1@sha256:aaa; do
  check "umami meta has $line" 1 "$(printf '%s\n' "$meta" | grep -cx -- "$line")"
done
check "umami meta has nothing of the trainer" 0 "$(printf '%s\n' "$meta" | grep -c -e APP_IMAGE -e DIT_COMMIT -e EF_ -e CONTENT || true)"
check "a value with a newline is refused" 1 "$(status_of dit_backup_meta_umami $'a\nb' 20261116T030000Z daily)"
check "the wrong number of values is refused" 1 "$(status_of dit_backup_meta_trainer a b c)"

# --- age of a record ---------------------------------------------------------------------------------------------------
printf '2026-11-16T03:00:00Z\n' >"$work/stamp"
now="$(date -u -d 2026-11-16T12:00:00Z +%s)"
check "nine hours is within 26" 0 "$(status_of dit_age_within "$work/stamp" 93600 "$now")"
now="$(date -u -d 2026-11-17T05:00:01Z +%s)"
check "twenty-six hours and a second is stale" 1 "$(status_of dit_age_within "$work/stamp" 93600 "$now")"
check "a missing record is stale" 1 "$(status_of dit_age_within "$work/missing" 93600 "$now")"
: >"$work/empty"
check "an empty record is stale" 1 "$(status_of dit_age_within "$work/empty" 93600 "$now")"
printf 'yesterday-ish\n' >"$work/garbage"
check "an unparsable record is stale" 1 "$(status_of dit_age_within "$work/garbage" 93600 "$now")"
printf '2027-01-01T00:00:00Z\n' >"$work/future"
check "a future record is stale" 1 "$(status_of dit_age_within "$work/future" 93600 "$now")"

# --- when last-offsite may move ----------------------------------------------------------------------------------------
offsite="$work/offsite-backups"
mkdir -p "$offsite"
: >"$offsite/trainer-20261101T030000Z-daily.dump.age"
# A predeploy run uploaded its own two files while the older daily dump is still pending.
: >"$offsite/trainer-20261116T030000Z-predeploy.dump.age"
: >"$offsite/trainer-20261116T030000Z-predeploy.dump.age.uploaded"
check "an older pending dump keeps last-offsite stale after a predeploy upload" 1 "$(status_of dit_offsite_current "$offsite" 2 0)"
: >"$offsite/trainer-20261101T030000Z-daily.dump.age.uploaded"
check "nothing pending and files uploaded moves it" 0 "$(status_of dit_offsite_current "$offsite" 2 0)"
check "nothing pending and the dumps written moves it" 0 "$(status_of dit_offsite_current "$offsite" 0 0)"
check "nothing pending, nothing uploaded and a failed dump keeps it stale" 1 "$(status_of dit_offsite_current "$offsite" 0 1)"
check "a malformed argument keeps it stale" 1 "$(status_of dit_offsite_current "$offsite" x 0)"

# --- the key ring after a start -----------------------------------------------------------------------------------------
check "an equal key ring count is fine" 0 "$(status_of dit_count_not_below 3 3)"
check "a larger key ring count is fine" 0 "$(status_of dit_count_not_below 3 5)"
check "a smaller key ring count fails" 1 "$(status_of dit_count_not_below 3 2)"
check "a non-integer answer fails" 1 "$(status_of dit_count_not_below 3 'ERROR: relation does not exist')"
check "an empty answer fails" 1 "$(status_of dit_count_not_below 3 '')"

# --- restarts and the table of contents --------------------------------------------------------------------------------
check "restarts over the limit" "app" "$(printf 'app 4\npostgres 3\numami 0\nbroken x\n' | dit_restarts_over 3)"
check "no restarts over the limit" "" "$(printf 'app 3\n' | dit_restarts_over 3)"

toc='; Archive created at 2026-11-16 03:00:00 UTC
218; 1259 16401 TABLE public data_protection_keys trainer
219; 1259 16405 TABLE public ef_migrations_history trainer
4350; 0 16401 TABLE DATA public data_protection_keys trainer
3200; 2606 16410 CONSTRAINT public data_protection_keys pk_data_protection_keys trainer
220; 1259 16420 SEQUENCE public some_seq trainer'
check "toc tables" "public.data_protection_keys public.ef_migrations_history" "$(printf '%s\n' "$toc" | dit_toc_tables | tr '\n' ' ' | sed 's/ $//')"
check "an empty toc has no tables" "" "$(printf '' | dit_toc_tables)"

if [ "$failures" -ne 0 ]; then
  printf '%d check(s) failed\n' "$failures" >&2
  exit 1
fi
echo "backup-lib tests passed"
