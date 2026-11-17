#!/usr/bin/env bash
set -euo pipefail

script="$(dirname "${BASH_SOURCE[0]}")/../scripts/render-env.sh"
failures=0

full_env() {
  cat <<'EOF'
APP_IMAGE=ghcr.io/example/dit@sha256:abc
APP_DIR=/srv/dev-interview-trainer
PUBLIC_BASE_URL=https://trainer.example.com
ADMIN_TELEGRAM_IDS=12,345
TELEGRAM_OIDC_CLIENT_ID=client-id
TELEGRAM_OIDC_CLIENT_SECRET=client-secret
DB_PASSWORD=Db.Password-123
CONTENT_IMPORT_TOKEN=0123456789abcdef0123456789abcdef
UMAMI_DB_PASSWORD=Umami.Password-123
UMAMI_APP_SECRET=umami-app-secret
BACKUP_S3_PROVIDER=Other
BACKUP_S3_ENDPOINT=https://s3.example.com
BACKUP_S3_BUCKET=bucket
BACKUP_S3_ACCESS_KEY_ID=access-key
BACKUP_S3_SECRET_ACCESS_KEY=secret-key
EOF
}

# run_with "<overrides as NAME=value lines>" "<names to unset>": prints combined output, sets status.
status=0
output=""
run_with() {
  local overrides="$1" unset_names="${2:-}" line name skip
  local -a args=()
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    skip=0
    for name in $unset_names; do
      if [ "${line%%=*}" = "$name" ]; then skip=1; fi
    done
    if [ "$skip" -eq 0 ]; then args+=("$line"); fi
  done < <(full_env; printf '%s\n' "$overrides")
  status=0
  output="$(env -i PATH="$PATH" "${args[@]}" bash "$script" 2>&1)" || status=$?
}

check() {
  if [ "$2" != "$3" ]; then
    printf 'FAIL: %s (expected %s, got %s)\n' "$1" "$2" "$3" >&2
    failures=$((failures + 1))
  fi
}

check_contains() {
  case "$output" in
    *"$2"*) ;;
    *) printf 'FAIL: %s (output lacks %s)\n' "$1" "$2" >&2; failures=$((failures + 1)) ;;
  esac
}

check_lacks() {
  case "$output" in
    *"$2"*) printf 'FAIL: %s (output contains %s)\n' "$1" "$2" >&2; failures=$((failures + 1)) ;;
  esac
}

run_with ""
check "full set exits 0" 0 "$status"
check_contains "full set quotes values" "DB_PASSWORD='Db.Password-123'"
check_contains "full set renders ids" "ADMIN_TELEGRAM_IDS='12,345'"
check_lacks "optional unset is omitted" "RateLimits"
check_lacks "unset feedback limit is omitted" "ReportsPerDay"

run_with "RATE_LIMIT_WRITES_PER_MINUTE=90
FEEDBACK_REPORTS_PER_DAY=5
UMAMI_WEBSITE_ID=123e4567-e89b-12d3-a456-426614174000"
check "optional set exits 0" 0 "$status"
check_contains "optional rendered under the application name" "App__Feedback__ReportsPerDay='5'"
check_contains "rate limit rendered under the application name" "App__RateLimits__WritesPerMinute='90'"
check_contains "website id rendered" "UMAMI_WEBSITE_ID='123e4567-e89b-12d3-a456-426614174000'"

run_with "" "DB_PASSWORD"
check "missing DB_PASSWORD exits 2" 2 "$status"
check_contains "missing names the variable" "DB_PASSWORD"
check_lacks "missing prints no secret" "Umami.Password-123"

run_with "TELEGRAM_OIDC_CLIENT_SECRET=it'sbad"
check "quote rejected" 2 "$status"
check_lacks "quote rejection prints no value" "it'sbad"

run_with "" "BACKUP_S3_BUCKET"
check "missing backup variable rejected" 2 "$status"
check_contains "backup names the variable" "BACKUP_S3_BUCKET"

run_with "ADMIN_TELEGRAM_IDS=12,abc"
check "bad admin ids rejected" 2 "$status"

long_ids="$(printf '1234567890,%.0s' $(seq 1 100))1"
run_with "ADMIN_TELEGRAM_IDS=$long_ids"
check "oversized admin id list rejected" 2 "$status"
check_contains "oversized list names the variable" "ADMIN_TELEGRAM_IDS must be at most 1024"

run_with "PUBLIC_BASE_URL=http://x"
check "http url rejected" 2 "$status"

run_with "CONTENT_IMPORT_TOKEN=short"
check "short token rejected" 2 "$status"

run_with "DB_PASSWORD=a;b"
check "reserved password character rejected" 2 "$status"

run_with "RATE_LIMIT_READS_PER_MINUTE=0"
check "non-positive limit rejected" 2 "$status"

run_with "APP_DIR=/srv/with:colon"
check "APP_DIR with a colon rejected" 2 "$status"

# shellcheck disable=SC2016 # the dollar sign is the literal under test
run_with 'APP_DIR=/srv/$HOME'
check "APP_DIR with a dollar rejected" 2 "$status"

if [ "$failures" -ne 0 ]; then
  printf '%d failure(s)\n' "$failures" >&2
  exit 1
fi
echo "render-env tests passed"
