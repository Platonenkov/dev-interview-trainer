#!/usr/bin/env bash
# Creates the trainer and umami roles and databases when missing. Passwords are set only at role creation
# (spec 8.7.2: they change through rotate-db-password), so a later .env change never silently rewrites a role.
set -euo pipefail

# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

ensure_one() {
  local name="$1" password_var="$2" password exists
  password="$(dit_env_get "$APP_DIR/.env" "$password_var")"
  if [ -z "$password" ]; then
    dit_log "$password_var is empty; refusing to create role $name"
    exit 1
  fi

  exists="$(dit_psql postgres -tA -c "SELECT 1 FROM pg_roles WHERE rolname = '$name'")"
  if [ "$exists" != "1" ]; then
    dit_log "creating role $name"
    # The password travels through the exec environment, never argv or the script text. psql substitutes :'pw' itself, so the
    # server receives the plaintext statement and would log it if it failed (log_min_error_statement defaults to "error"):
    # both settings are switched off in the same session first, as rotate-db-password.sh does.
    printf '%s\n' 'SET log_min_error_statement = panic;' "SET log_statement = 'none';" '\getenv pw PW' \
      "CREATE ROLE $name LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD :'pw';" |
      PW="$password" dit_compose exec -T --user postgres -e PW postgres psql -v ON_ERROR_STOP=1 -d postgres
  fi

  exists="$(dit_psql postgres -tA -c "SELECT 1 FROM pg_database WHERE datname = '$name'")"
  if [ "$exists" != "1" ]; then
    dit_log "creating database $name"
    dit_psql postgres -c "CREATE DATABASE $name OWNER $name"
  fi
}

ensure_one trainer DB_PASSWORD
ensure_one umami UMAMI_DB_PASSWORD
