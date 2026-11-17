#!/usr/bin/env bash
# Exit codes of rotate-db-password.sh against a stand-in for docker and curl: every failing step after the journal exists is
# exit 1 whatever the failing command returned, and the password never appears on a command line the stand-in saw.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
script="$here/../remote/rotate-db-password.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
failures=0

check() {
  if [ "$2" != "$3" ]; then
    printf 'FAIL: %s (expected %s, got %s)\n' "$1" "$2" "$3" >&2
    failures=$((failures + 1))
  fi
}

bin="$work/bin"
mkdir -p "$bin"
# docker: "exec ... sh" is the login proof, any other exec is ALTER ROLE, "up" is the recreation. Codes come from FAKE_* variables.
cat >"$bin/docker" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"$FAKE_ARGS_LOG"
case " $* " in
  *" exec "*)
    if [[ " $* " == *" sh -c "* ]]; then
      exit "${FAKE_LOGIN_EXIT:-0}"
    fi
    cat >/dev/null
    exit "${FAKE_EXEC_EXIT:-0}"
    ;;
  *" up "*) exit "${FAKE_UP_EXIT:-0}" ;;
esac
exit 0
STUB
cat >"$bin/curl" <<'STUB'
#!/usr/bin/env bash
exit "${FAKE_CURL_EXIT:-0}"
STUB
chmod +x "$bin/docker" "$bin/curl"

old='oldoldoldoldoldoldoldoldoldoldoldoldoldoldoldoldoldoldold1'
new='0123456789abcdef0123456789abcdef0123456789abcdef'

new_server() {
  export APP_DIR="$work/app"
  rm -rf "$APP_DIR"
  mkdir -p "$APP_DIR/staging/s1"
  printf "DB_PASSWORD='%s'\nUMAMI_DB_PASSWORD='%s'\n" "$old" "$old" >"$APP_DIR/.env"
  cp "$APP_DIR/.env" "$APP_DIR/last-good.env"
  : >"$work/args.log"
  export FAKE_ARGS_LOG="$work/args.log" DIT_WAIT_TIMEOUT=2
  unset FAKE_EXEC_EXIT FAKE_UP_EXIT FAKE_LOGIN_EXIT FAKE_CURL_EXIT
}

run() { # <mode> [role]; stdin of begin is the new password; sets status
  local mode="$1" role="${2:-trainer}"
  status=0
  if [ "$mode" = begin ]; then
    printf '%s\n' "$new" | PATH="$bin:$PATH" bash "$script" "$APP_DIR/staging/s1" "$role" begin >/dev/null 2>"$work/err.log" || status=$?
  else
    PATH="$bin:$PATH" bash "$script" "$APP_DIR/staging/s1" "$role" "$mode" >/dev/null 2>"$work/err.log" </dev/null || status=$?
  fi
}

journal_exists() {
  if [ -f "$APP_DIR/.rotation-pending" ]; then echo yes; else echo no; fi
}

# psql reports 3 for a failed statement, compose has its own codes: all of them are exit 1 with the journal kept.
new_server
FAKE_EXEC_EXIT=3 run begin
check "ALTER ROLE failing with 3: exit status" 1 "$status"
check "ALTER ROLE failing with 3: journal kept" yes "$(journal_exists)"
check "ALTER ROLE failing with 3: .env untouched" "$old" "$(grep -o "'.*'" "$APP_DIR/.env" | head -n 1 | tr -d "'")"

new_server
FAKE_EXEC_EXIT=2 run begin
check "ALTER ROLE failing with 2: exit status" 1 "$status"

new_server
FAKE_UP_EXIT=2 run begin
check "compose up failing with 2: exit status" 1 "$status"
check "compose up failing with 2: journal kept" yes "$(journal_exists)"

new_server
FAKE_LOGIN_EXIT=2 run begin
check "login proof failing with 2: exit status" 1 "$status"
check "login proof failing with 2: journal kept" yes "$(journal_exists)"

new_server
FAKE_CURL_EXIT=22 run begin
check "readiness failing: exit status" 1 "$status"
check "readiness failing: journal kept" yes "$(journal_exists)"

# finish repeats the work and succeeds once the failing command works; commit then removes the journal.
FAKE_CURL_EXIT=0 run finish
check "finish after a failed readiness: exit status" 0 "$status"
check "finish: .env holds the new value" "$new" "$(sed -n "s/^DB_PASSWORD='\(.*\)'$/\1/p" "$APP_DIR/.env")"
check "finish: last-good.env holds the new value" "$new" "$(sed -n "s/^DB_PASSWORD='\(.*\)'$/\1/p" "$APP_DIR/last-good.env")"
check "finish: the other password is untouched" "$old" "$(sed -n "s/^UMAMI_DB_PASSWORD='\(.*\)'$/\1/p" "$APP_DIR/.env")"
run commit
check "commit: exit status" 0 "$status"
check "commit: journal removed" no "$(journal_exists)"

# What the stand-in saw on its command lines never contains the value.
if grep -qF -- "$new" "$FAKE_ARGS_LOG"; then
  printf 'FAIL: the new password reached a command line\n' >&2
  failures=$((failures + 1))
fi
if ! grep -q ' exec ' "$FAKE_ARGS_LOG"; then
  printf 'FAIL: the stand-in saw no exec call, so the argument check proves nothing\n' >&2
  failures=$((failures + 1))
fi

# The documented codes that are not step failures.
new_server
run finish umami
check "finish without a journal: exit status" 2 "$status"
status=0
printf '%s\n' "$new" | PATH="$bin:$PATH" bash "$script" "$APP_DIR/staging/s1" nobody begin >/dev/null 2>&1 || status=$?
check "begin for an unknown role: exit status" 2 "$status"
status=0
printf 'short\n' | PATH="$bin:$PATH" bash "$script" "$APP_DIR/staging/s1" trainer begin >/dev/null 2>&1 || status=$?
check "begin with an invalid password: exit status" 2 "$status"
check "begin with an invalid password: nothing journaled" no "$(journal_exists)"
touch "$APP_DIR/.rotation-pending"
status=0
printf '%s\n' "$new" | PATH="$bin:$PATH" bash "$script" "$APP_DIR/staging/s1" trainer begin >/dev/null 2>&1 || status=$?
check "begin over a guard: exit status" 3 "$status"

if [ "$failures" -ne 0 ]; then
  printf '%d failure(s)\n' "$failures" >&2
  exit 1
fi
echo "rotate tests passed"
