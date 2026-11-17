#!/usr/bin/env bash
# Prints the server .env (NAME='value') from environment variables. Exits 2 naming the first missing or invalid
# variable, never its value. The caller writes the output atomically with mode 600 (umask 077, temp file, rename).
set -euo pipefail

fail() {
  printf 'render-env: %s\n' "$1" >&2
  exit 2
}

required=(
  APP_IMAGE APP_DIR PUBLIC_BASE_URL ADMIN_TELEGRAM_IDS
  TELEGRAM_OIDC_CLIENT_ID TELEGRAM_OIDC_CLIENT_SECRET
  DB_PASSWORD CONTENT_IMPORT_TOKEN UMAMI_DB_PASSWORD UMAMI_APP_SECRET
  BACKUP_S3_PROVIDER BACKUP_S3_ENDPOINT BACKUP_S3_BUCKET BACKUP_S3_ACCESS_KEY_ID BACKUP_S3_SECRET_ACCESS_KEY
)
optional=(
  UMAMI_WEBSITE_ID RATE_LIMIT_WRITES_PER_MINUTE RATE_LIMIT_READS_PER_MINUTE RATE_LIMIT_SEARCH_PER_MINUTE FEEDBACK_REPORTS_PER_DAY
)

# NUL cannot occur in an environment variable, so quote, CR and newline are the characters left to reject.
check_chars() {
  local name="$1" value="$2"
  case "$value" in
    *\'* | *$'\n'* | *$'\r'*) fail "$name contains a forbidden character (quote or line break)" ;;
  esac
}

check_format() {
  local name="$1" value="$2"
  case "$name" in
    # ":" and "$" would break the short volume syntax "${APP_DIR}/content:/content:ro".
    APP_DIR) [[ "$value" =~ ^/[A-Za-z0-9._/-]*$ ]] || fail "$name must be an absolute path of letters, digits and . _ / -" ;;
    APP_IMAGE) [[ "$value" =~ ^[^[:space:]]+$ ]] || fail "$name must not contain whitespace" ;;
    PUBLIC_BASE_URL) [[ "$value" =~ ^https://[^[:space:]]+$ ]] || fail "$name must be an https URL" ;;
    ADMIN_TELEGRAM_IDS)
      [ "${#value}" -le 1024 ] || fail "$name must be at most 1024 characters"
      [[ "$value" =~ ^[1-9][0-9]*(,[1-9][0-9]*)*$ ]] || fail "$name must be comma-separated positive integers"
      ;;
    CONTENT_IMPORT_TOKEN) [ "${#value}" -ge 32 ] || fail "$name must be at least 32 characters" ;;
    # These two are embedded in a connection string and a URL, so reserved characters would break them.
    DB_PASSWORD | UMAMI_DB_PASSWORD) [[ "$value" =~ ^[A-Za-z0-9._~-]+$ ]] || fail "$name may contain only letters, digits and . _ ~ -" ;;
    UMAMI_WEBSITE_ID) [[ "$value" =~ ^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$ ]] || fail "$name must be a GUID" ;;
    RATE_LIMIT_* | FEEDBACK_REPORTS_PER_DAY) [[ "$value" =~ ^[1-9][0-9]*$ ]] || fail "$name must be a positive integer" ;;
  esac
}

output=""
for name in "${required[@]}"; do
  value="${!name:-}"
  [ -n "$value" ] || fail "$name is required"
  check_chars "$name" "$value"
  check_format "$name" "$value"
  output+="$name='$value'"$'\n'
done

for name in "${optional[@]}"; do
  value="${!name:-}"
  [ -n "$value" ] || continue
  check_chars "$name" "$value"
  check_format "$name" "$value"
  # Numeric settings fail startup when bound from an empty string, so they are written under the application's own
  # names and compose passes them through only when present; an unset one keeps the application default.
  case "$name" in
    RATE_LIMIT_WRITES_PER_MINUTE) name="App__RateLimits__WritesPerMinute" ;;
    RATE_LIMIT_READS_PER_MINUTE) name="App__RateLimits__ReadsPerMinute" ;;
    RATE_LIMIT_SEARCH_PER_MINUTE) name="App__RateLimits__SearchPerMinute" ;;
    FEEDBACK_REPORTS_PER_DAY) name="App__Feedback__ReportsPerDay" ;;
  esac
  output+="$name='$value'"$'\n'
done

printf '%s' "$output"
