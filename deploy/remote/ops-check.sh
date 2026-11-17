#!/usr/bin/env bash
# The daily operations check: prints one "ok|fail <check> <detail>" line per check on stdout and exits 1 when any failed.
# It changes nothing and takes no lock, so it is safe to run at any time, including while a deployment is running.
#
# Checks: readiness, backup-identity, backup-age, offsite-age, disk, certificate, restarts, guards, deploy-hold.
#
# DIT_OPS_TLS_ADDR (default 127.0.0.1:443) is where the certificate is read from; the host name comes from
# PUBLIC_BASE_URL in .env. DIT_OPS_MAX_BACKUP_AGE (seconds, default 93600 = 26 h) and DIT_OPS_MAX_DISK_PERCENT (default 85)
# exist for tests.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "$here/lib.sh"
# shellcheck source=backup-lib.sh
. "$here/backup-lib.sh"
export APP_DIR

env_file="$APP_DIR/.env"
backups="$APP_DIR/backups"
max_age="${DIT_OPS_MAX_BACKUP_AGE:-93600}"
tls_addr="${DIT_OPS_TLS_ADDR:-127.0.0.1:443}"
max_disk="${DIT_OPS_MAX_DISK_PERCENT:-85}"
failed=0

report() { # <ok|fail> <check> <detail>
  printf '%s %s %s\n' "$1" "$2" "$3"
  if [ "$1" = "fail" ]; then
    failed=1
  fi
}

check_readiness() {
  if curl -fsS --max-time 10 http://127.0.0.1:8080/health/ready >/dev/null 2>&1; then
    report ok readiness "/health/ready answers 200"
  else
    report fail readiness "/health/ready does not answer 200"
  fi
}

check_backup_identity() {
  if [ -s "$APP_DIR/backup/recipients.txt" ]; then
    report ok backup-identity "recipients.txt is installed"
  else
    report fail backup-identity "$APP_DIR/backup/recipients.txt is missing; backups are refused"
  fi
}

check_record_age() { # <check> <file> <what>
  if dit_age_within "$2" "$max_age"; then
    report ok "$1" "$3 at $(head -n 1 "$2")"
  elif [ -s "$2" ]; then
    report fail "$1" "$3 is older than $((max_age / 3600)) h (last: $(head -n 1 "$2"))"
  else
    report fail "$1" "$3 was never recorded"
  fi
}

check_disk() {
  local used
  used="$(df --output=pcent / | tail -n 1 | tr -dc '0-9')"
  if [ -n "$used" ] && [ "$used" -lt "$max_disk" ]; then
    report ok disk "root filesystem ${used}% used"
  else
    report fail disk "root filesystem ${used:-unknown}% used (limit ${max_disk}%)"
  fi
}

check_certificate() {
  local url host
  url="$(dit_env_get "$env_file" PUBLIC_BASE_URL 2>/dev/null)" || url=""
  host="${url#*://}"
  host="${host%%[/:]*}"
  if [ -z "$host" ]; then
    report fail certificate "PUBLIC_BASE_URL is not set in .env"
    return 0
  fi
  # -checkend exits 0 while the certificate is still valid that many seconds from now.
  if openssl s_client -connect "$tls_addr" -servername "$host" </dev/null 2>/dev/null | openssl x509 -noout -checkend $((14 * 86400)) >/dev/null 2>&1; then
    report ok certificate "the certificate of $host is valid for more than 14 days"
  else
    report fail certificate "the certificate of $host at $tls_addr is missing, unreadable or expires within 14 days"
  fi
}

check_restarts() {
  local ids id lines="" over
  ids="$(dit_compose ps -q 2>/dev/null)" || ids=""
  for id in $ids; do
    lines+="$(docker inspect --format '{{.Name}} {{.RestartCount}}' "$id" 2>/dev/null || true)"$'\n'
  done
  # The restart count belongs to the container, and every deployment that changes a service recreates it, so the count
  # is "since the last deployment" without any bookkeeping.
  over="$(printf '%s' "$lines" | dit_restarts_over 3 | tr '\n' ' ')"
  if [ -z "$ids" ]; then
    report fail restarts "no containers of the stack were found"
  elif [ -n "$over" ]; then
    report fail restarts "restarted more than 3 times since the last deployment: ${over% }"
  else
    report ok restarts "no container restarted more than 3 times"
  fi
}

check_guards() {
  local present="" entry file name
  for entry in \
    "flags/maintenance|Interrupted restore" \
    ".rotation-pending|Interrupted rotation" \
    ".umami-recovery|Umami upgrade" \
    ".import-pending|Content import" \
    ".deploy-in-progress|Interrupted deployment"; do
    file="${entry%%|*}"
    name="${entry#*|}"
    if [ -e "$APP_DIR/$file" ]; then
      present+="$file (docs/deployment.md \"$name\"); "
    fi
  done
  if [ -z "$present" ]; then
    report ok guards "no guard file exists"
  else
    report fail guards "${present%; }"
  fi
}

# flags/deploy-hold is written after a rollback by dispatch and stops automatic deployments. It is not a guard for the scripts, but
# a hold nobody remembers keeps the site on the rolled-back release for good, so it fails the daily run until it is released
# on purpose (a manual deploy-app run without rollback). It is its own check: the other checks stay as they are.
check_deploy_hold() {
  local file="$APP_DIR/flags/deploy-hold"
  if [ -e "$file" ]; then
    report fail deploy-hold "automatic deployments are held since a rollback (commit $(head -c 40 "$file" 2>/dev/null | tr -cd '0-9a-f')); release it with a manual deploy-app run without rollback (docs/deployment.md, \"Rolling back\")"
  else
    report ok deploy-hold "no rollback hold"
  fi
}

check_readiness
check_backup_identity
check_record_age backup-age "$backups/last-success" "the last backup run without a failed dump"
check_record_age offsite-age "$backups/last-offsite" "the last complete off-site copy"
check_disk
check_certificate
check_restarts
check_guards
check_deploy_hold

exit "$failed"
