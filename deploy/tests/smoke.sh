#!/usr/bin/env bash
# Deployment smoke test: runs deploy-app.sh against the real Compose project in a temporary APP_DIR, through the same
# staging protocol the workflows use, and prints "ok <name>" for every check or stops at the first failure.
#
# Needs Linux with Docker (Compose plugin), bash, curl, flock, age, rclone, openssl, python3 and the repository checkout. IMAGE names the application
# image under test; without it the Dockerfile is built as dit:smoke. The application image is served from a throw-away
# local registry (127.0.0.1:5000) so "compose pull" runs for real. Ports 5000, 8080, 8081, 8082, 3001, 9000 and 9001 on loopback
# must be free. DIT_SMOKE_KEEP=1 keeps the temporary directory and containers after a failure.
#
# The stack runs as Compose project "dit-smoke" (COMPOSE_PROJECT_NAME overrides the name in compose.yaml), so its
# containers, volumes and network never collide with a real "dev-interview-trainer" stack; the script refuses to start
# while one exists, because the fixed subnet and loopback ports cannot be shared.
#
# DIT_SMOKE_MODE picks the tier. "full" (the default) runs every section. "quick" stops after the rollback section: first
# install, refusals and rollback exercise the scripts a deployment runs on every release, while concurrency, the random
# hard kills, backups, content delivery, the Umami guards and password rotation only matter when those scripts change.
set -euo pipefail

smoke_mode="${DIT_SMOKE_MODE:-full}"
case "$smoke_mode" in
  quick | full) ;;
  *)
    echo "DIT_SMOKE_MODE must be 'quick' or 'full', got '$smoke_mode'" >&2
    exit 2
    ;;
esac

if [ "$(uname -s)" != "Linux" ] || ! command -v docker >/dev/null 2>&1 || ! command -v flock >/dev/null 2>&1; then
  echo "smoke.sh needs Linux with Docker, curl and flock" >&2
  exit 1
fi
for tool in age age-keygen rclone openssl python3; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "smoke.sh needs $tool (the backup scripts run on this host, the import proxy is Python)" >&2
    exit 1
  fi
done

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
deploy_src="$repo_root/deploy"
content_src="$deploy_src/tests/content"
registry="127.0.0.1:5000"
registry_image="registry:2@sha256:a3d8aaa63ed8681a604f1dea0aa03f100d5895b6a58ace528858a7b332415373"
kill_iterations="${DIT_SMOKE_KILL_ITERATIONS:-12}"
export COMPOSE_PROJECT_NAME=dit-smoke
# The default of 180 s per attempt would make every failing deployment below take minutes.
export DIT_WAIT_TIMEOUT="${DIT_SMOKE_WAIT_TIMEOUT:-90}"
export DIT_LOCK_TIMEOUT=600

smoke_root="$(mktemp -d /tmp/dit-smoke.XXXXXX)"
# mktemp creates the directory with mode 700, and deploy-app.sh refuses a flags path nginx could not traverse.
chmod 755 "$smoke_root"
export APP_DIR="$smoke_root/app"
logs="$smoke_root/logs"
mkdir -p "$logs"

# shellcheck source=../remote/lib.sh
. "$deploy_src/remote/lib.sh"
# shellcheck source=../remote/deploy-lib.sh
. "$deploy_src/remote/deploy-lib.sh"

rand() {
  LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c "$1" || true
}

DB_PASSWORD="$(rand 24)"
UMAMI_DB_PASSWORD="$(rand 24)"
UMAMI_APP_SECRET="$(rand 32)"
CONTENT_IMPORT_TOKEN="$(rand 40)"
TELEGRAM_OIDC_CLIENT_SECRET="$(rand 32)"
SMOKE_S3_ACCESS_KEY="$(rand 16)"
SMOKE_S3_SECRET_KEY="$(rand 32)"
export SMOKE_S3_ACCESS_KEY SMOKE_S3_SECRET_KEY

last_log=""
deploy_status=0
bundle_hook=""
env_hook=""
kill_at=""
runs_pid=""
tls_pid=""
proxy_pid=""
s3_proxy_pid=""
backup_runs=0
restore_runs=0

pass() {
  printf 'ok %s\n' "$1"
}

fail() {
  printf 'FAIL: %s\n' "$*" >&2
  exit 1
}

expect_eq() {
  [ "$2" = "$3" ] || fail "$1: expected '$2', got '$3'"
}

# --- teardown --------------------------------------------------------------------------------------------------------

hard_reset_stack() {
  local ids
  ids="$(docker ps -aq --filter label=com.docker.compose.project=dit-smoke)"
  if [ -n "$ids" ]; then
    # shellcheck disable=SC2086 # one id per word
    docker rm -f $ids >/dev/null
  fi
  docker volume rm -f dit-smoke_pgdata dit-smoke_ogcache >/dev/null 2>&1 || true
  docker network rm dit-smoke_default >/dev/null 2>&1 || true
}

cleanup() {
  local status=$?
  trap - EXIT
  if [ "$status" -ne 0 ]; then
    echo "--- smoke failed (status $status); diagnostics ---" >&2
    if [ -n "$last_log" ] && [ -f "$last_log" ]; then
      echo "--- last deploy log: $last_log ---" >&2
      tail -n 60 "$last_log" >&2 || true
    fi
    docker ps -a --filter label=com.docker.compose.project=dit-smoke --format '{{.Names}} {{.Status}}' >&2 || true
    docker logs --tail 40 dit-smoke-app-1 >&2 2>&1 || true
  fi
  if [ "$status" -ne 0 ] && [ "${DIT_SMOKE_KEEP:-0}" = "1" ]; then
    echo "kept $smoke_root and the containers" >&2
    exit "$status"
  fi
  if [ -n "$tls_pid" ]; then
    kill "$tls_pid" 2>/dev/null || true
  fi
  if [ -n "$proxy_pid" ]; then
    kill "$proxy_pid" 2>/dev/null || true
  fi
  if [ -n "$s3_proxy_pid" ]; then
    kill "$s3_proxy_pid" 2>/dev/null || true
  fi
  hard_reset_stack
  docker compose -p dit-smoke-s3 -f "$deploy_src/tests/compose.smoke.yaml" down -v --timeout 2 >/dev/null 2>&1 || true
  docker rm -f dit-smoke-registry >/dev/null 2>&1 || true
  docker image ls --format '{{.Repository}}:{{.Tag}}' --filter "reference=$registry/*" | xargs -r docker image rm >/dev/null 2>&1 || true
  rm -rf "$smoke_root"
  exit "$status"
}
trap cleanup EXIT

# --- fixtures --------------------------------------------------------------------------------------------------------

if [ -z "${IMAGE:-}" ]; then
  IMAGE="dit:smoke"
  docker build -t "$IMAGE" "$repo_root"
elif ! docker image inspect "$IMAGE" >/dev/null 2>&1; then
  docker pull "$IMAGE"
fi
base_image="$IMAGE"

# The smoke stack shares the subnet and loopback ports with the real one; never touch a real stack, just refuse.
if [ -n "$(docker ps -aq --filter label=com.docker.compose.project=dev-interview-trainer)" ]; then
  echo "a dev-interview-trainer Compose project exists on this Docker host; stop it before running the smoke test" >&2
  exit 1
fi
for port in 5000 8080 8081 8082 3001 9000 9001; do
  if (exec 9<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then
    echo "loopback port $port is already in use" >&2
    exit 1
  fi
done

hard_reset_stack
docker rm -f dit-smoke-registry >/dev/null 2>&1 || true
docker run -d --name dit-smoke-registry -p 127.0.0.1:5000:5000 "$registry_image" >/dev/null
curl -fsS --retry 20 --retry-connrefused --retry-delay 1 "http://$registry/v2/" >/dev/null

docker compose -p dit-smoke-s3 -f "$deploy_src/tests/compose.smoke.yaml" up -d --wait >/dev/null
# The server's backup key may list and write but not read: the stand-in has one full-rights key, so this proxy on 9000 answers
# 403 to GET/HEAD of an object and to DELETE (the backup scripts use it), while smoke.sh reaches the stand-in itself on 9001.
s3_proxy_log="$smoke_root/s3-proxy.log"
: >"$s3_proxy_log"
python3 "$deploy_src/tests/s3-no-read-proxy.py" --port 9000 --upstream 127.0.0.1:9001 --log "$s3_proxy_log" >"$logs/s3-proxy.out" 2>&1 &
s3_proxy_pid=$!
for _ in $(seq 1 50); do
  (exec 9<>"/dev/tcp/127.0.0.1/9000") 2>/dev/null && break
  sleep 0.2
done
(exec 9<>"/dev/tcp/127.0.0.1/9000") 2>/dev/null || { echo "the S3 proxy did not start: $(cat "$logs/s3-proxy.out")" >&2; exit 1; }
# The proxy's own check: a read, a delete and a multi-object delete get 403 before they reach the stand-in.
python3 "$deploy_src/tests/s3-no-read-proxy.py" --self-test >/dev/null || { echo "the S3 proxy self-test failed" >&2; exit 1; }
s3_probe() { # <expected status> <label> <curl arguments...>
  local expected="$1" label="$2" got
  shift 2
  got="$(curl -s -o /dev/null -w '%{http_code}' "$@")"
  [ "$got" = "$expected" ] || { echo "the S3 proxy answered $got to $label, not $expected" >&2; exit 1; }
}
s3_probe 403 "a read" http://127.0.0.1:9000/dit-smoke/probe.dump.age
s3_probe 403 "a HEAD" -I http://127.0.0.1:9000/dit-smoke/probe.dump.age
s3_probe 403 "a delete" -X DELETE http://127.0.0.1:9000/dit-smoke/probe.dump.age
s3_probe 403 "a multi-object delete" -X POST http://127.0.0.1:9000/dit-smoke?delete
s3_probe 403 "a virtual-hosted object read" -H 'Host: dit-smoke.s3.example.test' http://127.0.0.1:9000/probe.dump.age
: >"$s3_proxy_log"

# One tag per distinct application image: the images differ by a label, so every deployment replaces the running image.
# Built here, pushed by upload(); a build failure inside the command substitution surfaces there as a failed push.
make_image() {
  local ref="$registry/dit:$1"
  if ! docker image inspect "$ref" >/dev/null 2>&1; then
    printf 'FROM %s\nLABEL dit.smoke.tag=%s\n' "$base_image" "$1" | docker build -q -t "$ref" - >/dev/null
  fi
}

commit_for() {
  printf '%040x' "$1"
}

commit_number() {
  local digits
  digits="$(printf '%s' "$1" | sed 's/^0*//')"
  printf '%d' "$((16#${digits:-0}))"
}

image_for() {
  make_image "c$1"
  printf '%s/dit:c%s' "$registry" "$1"
}

# --- the runner side of the upload protocol ----------------------------------------------------------------------------

render_env() {
  local out="$1" image="$2"
  (
    export APP_IMAGE="$image" PUBLIC_BASE_URL="https://smoke.example.test" ADMIN_TELEGRAM_IDS="1000001"
    export TELEGRAM_OIDC_CLIENT_ID="smoke-client" TELEGRAM_OIDC_CLIENT_SECRET DB_PASSWORD CONTENT_IMPORT_TOKEN
    export UMAMI_DB_PASSWORD UMAMI_APP_SECRET BACKUP_S3_PROVIDER="Other" BACKUP_S3_ENDPOINT="http://127.0.0.1:9000"
    export BACKUP_S3_BUCKET="dit-smoke" BACKUP_S3_ACCESS_KEY_ID="$SMOKE_S3_ACCESS_KEY" BACKUP_S3_SECRET_ACCESS_KEY="$SMOKE_S3_SECRET_KEY"
    umask 077
    bash "$deploy_src/scripts/render-env.sh" >"$out"
  )
}

# Streams the bundle and .env.new into the run's own staging directory and unpacks the bundle there; nothing else on the
# server is touched before the deploy script takes the lock. bundle_hook and env_hook edit the run's bundle or .env.new.
upload() {
  local run="$1" commit="$2" image="$3" src stage="$APP_DIR/staging/$1"
  src="$smoke_root/bundle-src/$run"
  rm -rf "$src"
  mkdir -p "$src" "$stage"
  docker push -q "$image" >/dev/null || fail "cannot push $image to the local registry"
  cp -a "$deploy_src/." "$src/"
  rm -rf "$src/tests"
  printf '%s\n' "$commit" >"$src/.smoke-id"
  if [ -n "$bundle_hook" ]; then
    "$bundle_hook" "$src"
  fi
  tar -czf "$stage/bundle.tgz" -C "$src" .
  render_env "$stage/.env.new" "$image"
  if [ -n "$env_hook" ]; then
    "$env_hook" "$stage/.env.new"
  fi
  mkdir -p "$stage/bundle"
  tar -xzf "$stage/bundle.tgz" -C "$stage/bundle"
  bundle_hook=""
  env_hook=""
}

# Foreground deploy; the exit status lands in deploy_status instead of ending the script.
run_deploy() {
  local run="$1" commit="$2" image="$3"
  local -a hooks=()
  if [ -n "$kill_at" ]; then
    hooks=(DIT_TEST_HOOKS=1 "DIT_TEST_KILL_AT=$kill_at")
  fi
  kill_at=""
  last_log="$logs/$run.log"
  deploy_status=0
  env "${hooks[@]}" bash "$APP_DIR/staging/$run/bundle/remote/deploy-app.sh" "$APP_DIR/staging/$run" "$image" "$commit" >"$last_log" 2>&1 || deploy_status=$?
}

start_deploy() {
  local run="$1" commit="$2" image="$3"
  last_log="$logs/$run.log"
  bash "$APP_DIR/staging/$run/bundle/remote/deploy-app.sh" "$APP_DIR/staging/$run" "$image" "$commit" >"$last_log" 2>&1 &
  runs_pid=$!
}

deploy() {
  upload "$@"
  run_deploy "$@"
}

# --- observations ----------------------------------------------------------------------------------------------------

ready() {
  curl -fsS --max-time 5 http://127.0.0.1:8080/health/ready >/dev/null 2>&1
}

wait_ready() {
  local i
  for i in $(seq 1 60); do
    ready && return 0
    sleep 1
  done
  fail "/health/ready did not answer 200 within 60 s"
}

running_services() {
  dit_compose ps --status running --services | sort | tr '\n' ' '
}

# Services restart on their own after a kill loop (Umami may sit in a restart back-off for a while), so a one-shot comparison
# right after it is a race. Polls for up to two minutes; on a timeout it shows what every container is doing.
wait_running_services() { # <label> <expected, as running_services prints it>
  local label="$1" expected="$2" i
  for i in $(seq 1 60); do
    [ "$(running_services)" = "$expected" ] && return 0
    sleep 2
  done
  {
    echo "--- $label: running services are '$(running_services)', expected '$expected' ---"
    dit_compose ps -a
    dit_compose logs --tail 60 umami
  } >&2 || true
  fail "$label: the services did not reach '$expected' within 120 s"
}

app_container() {
  dit_compose ps -q app
}

bundle_of_link() {
  basename "$(readlink "$APP_DIR/current-bundle")"
}

# Everything a refused or rolled-back deployment must leave alone.
snapshot() {
  {
    sha256sum "$APP_DIR/.env" "$APP_DIR/last-good.env"
    readlink "$APP_DIR/current-bundle"
    ls -1 "$APP_DIR/bundles"
    dit_compose ps -q | sort
  } | sha256sum
}

assert_rolled_back_to_last_good() {
  local label="$1" good_image good_bundle
  good_image="$(dit_env_get "$APP_DIR/last-good.env" APP_IMAGE)"
  good_bundle="$(dit_env_get "$APP_DIR/last-good.env" DIT_BUNDLE_SHA)"
  cmp -s "$APP_DIR/.env" "$APP_DIR/last-good.env" || fail "$label: .env differs from last-good.env"
  expect_eq "$label: current-bundle" "$good_bundle" "$(bundle_of_link)"
  expect_eq "$label: running app image" "$good_image" "$(docker inspect --format '{{.Config.Image}}' "$(app_container)")"
  cmp -s "$APP_DIR/bundles/$good_bundle/compose/postgres/pg_hba.conf" "$APP_DIR/postgres/pg_hba.conf" || fail "$label: pg_hba.conf is not the last good one"
  [ ! -e "$APP_DIR/.deploy-in-progress" ] || fail "$label: the in-progress marker was left behind"
  wait_ready
}

# One deployment per file: image tag, commit and the bundle's own marker must agree.
assert_record_consistent() {
  local label="$1" file="$2" image commit bundle
  image="$(dit_env_get "$file" APP_IMAGE)"
  commit="$(dit_env_get "$file" DIT_COMMIT)"
  bundle="$(dit_env_get "$file" DIT_BUNDLE_SHA)"
  expect_eq "$label: image of $(basename "$file")" "$registry/dit:c$(commit_number "$commit")" "$image"
  [ -d "$APP_DIR/bundles/$bundle" ] || fail "$label: bundle $bundle of $(basename "$file") is missing"
  expect_eq "$label: bundle marker of $(basename "$file")" "$commit" "$(cat "$APP_DIR/bundles/$bundle/.smoke-id")"
}

import_content() {
  local header="$smoke_root/import.header"
  (umask 077 && printf 'Authorization: Bearer %s\n' "$CONTENT_IMPORT_TOKEN" >"$header")
  curl -sS --max-time 120 -X POST -H "@$header" -o "$smoke_root/import.body" -w '%{http_code}' http://127.0.0.1:8081/internal/content/import
}

content_hash() {
  curl -fsS --max-time 10 http://127.0.0.1:8081/internal/version | sed -n 's/.*"activeContentHash":"\([^"]*\)".*/\1/p'
}

switch_content() {
  local name="$1" release="$APP_DIR/content/releases/$1"
  mkdir -p "$release"
  cp -a "$content_src/." "$release/"
  if [ -n "${2:-}" ]; then
    "$2" "$release"
  fi
  ln -sfn "releases/$name" "$APP_DIR/content/current.new"
  mv -T "$APP_DIR/content/current.new" "$APP_DIR/content/current"
}

retitle_question() {
  sed -i 's/What does the code print when lambdas capture a for loop variable?/Switched title for the smoke test/' "$1/dotnet/csharp/closure-for-loop.yaml"
}

# --- bundle and environment edits for the failing deployments ----------------------------------------------------------

# A change that differs from retitle_question, so a content release switched after a backup has a hash of its own.
retitle_question_again() {
  sed -i 's/What does the code print when lambdas capture a for loop variable?/Retitled once more for the rebuild check/' "$1/dotnet/csharp/closure-for-loop.yaml"
}

bad_public_url() {
  sed -i "s|^PUBLIC_BASE_URL=.*|PUBLIC_BASE_URL='http://bad'|" "$1"
}

stale_umami_password() {
  sed -i "s|^UMAMI_DB_PASSWORD=.*|UMAMI_DB_PASSWORD='not-the-running-password'|" "$1"
}

unparseable_umami_image() {
  sed -i "s|^    image: ghcr.io/umami-software/umami.*|    image: \${UMAMI_IMAGE}|" "$1/compose/compose.yaml"
}

stale_db_password() {
  sed -i "s|^DB_PASSWORD=.*|DB_PASSWORD='not-the-running-password'|" "$1"
}

changed_app_compose() {
  sed -i 's/mem_limit: 768m/mem_limit: 640m/' "$1/compose/compose.yaml"
}

reject_app_role_in_hba() {
  sed -i 's/^host trainer trainer .*/host trainer trainer 172.30.0.0\/24 reject/' "$1/compose/postgres/pg_hba.conf"
}

other_umami_image() {
  sed -i "s|^    image: ghcr.io/umami-software/umami.*|    image: $registry/umami:next|" "$1/compose/compose.yaml"
}

# === scenario 1: a clean server ==================================================================================

install -d -m 755 "$APP_DIR" "$APP_DIR/staging" "$APP_DIR/backup"
# The backup scripts encrypt to the recipient only; the identity stays outside the application directory, as it would on the
# administrator's machine.
identity_file="$smoke_root/age-identity.txt"
age-keygen -o "$identity_file" >/dev/null 2>&1
age-keygen -y "$identity_file" >"$APP_DIR/backup/recipients.txt"

# --- a failed first install keeps PostgreSQL, stops the rest and can be re-run ----------------------------------------
bundle_hook=""
env_hook=bad_public_url
deploy run-1 "$(commit_for 1)" "$(image_for 1)"
expect_eq "failed first install: exit status" 1 "$deploy_status"
[ ! -e "$APP_DIR/last-good.env" ] || fail "failed first install: last-good.env was published"
grep -q "first install incomplete" "$last_log" || fail "failed first install: the log does not say the install is incomplete"
expect_eq "failed first install: running services" "postgres " "$(running_services)"

# The re-run is also the first deployment without any content release: the site must be ready and empty.
deploy run-2 "$(commit_for 2)" "$(image_for 2)"
expect_eq "retry of the first install: exit status" 0 "$deploy_status"
wait_ready
[ ! -e "$APP_DIR/staging/run-2/.env.new" ] || fail "the staged .env.new (all secrets) was left behind after activation"
pass failed_first_install_can_be_retried

expect_eq "empty site: active content hash" "" "$(content_hash)"
[ -z "$(ls -A "$APP_DIR/content")" ] || fail "empty site: the content directory is not empty"
pass first_deploy_without_content_is_ready

# --- internal endpoints, headers and rendering ------------------------------------------------------------------------
expect_eq "internal port version" 200 "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8081/internal/version)"
expect_eq "public port internal path" 404 "$(curl -s -o "$smoke_root/public.body" -w '%{http_code}' http://127.0.0.1:8080/internal/version)"
expect_eq "public port internal path body size" 0 "$(wc -c <"$smoke_root/public.body" | tr -d ' ')"
pass internal_endpoints_only_on_internal_port

switch_content release-a
expect_eq "first content import: status" 200 "$(import_content)"
first_hash="$(content_hash)"
[ -n "$first_hash" ] || fail "first content import: no active content hash"

secure_cookie="$(curl -s -D - -o /dev/null -H 'X-Forwarded-Proto: https' 'http://127.0.0.1:8080/lang/en?return=/en' | tr -d '\r' | grep -i '^set-cookie:' || true)"
case "$secure_cookie" in
  *[Ss]ecure*) ;;
  *) fail "forwarded proto: the language cookie is not Secure: $secure_cookie" ;;
esac
plain_cookie="$(curl -s -D - -o /dev/null 'http://127.0.0.1:8080/lang/en?return=/en' | tr -d '\r' | grep -i '^set-cookie:' || true)"
case "$plain_cookie" in
  *lang=en*) ;;
  *) fail "forwarded proto: the control request set no language cookie" ;;
esac
case "$plain_cookie" in
  *[Ss]ecure*) fail "forwarded proto: the cookie is Secure without X-Forwarded-Proto" ;;
esac
pass forwarded_proto_is_trusted_from_gateway

curl -s -D "$smoke_root/page1.headers" -o /dev/null http://127.0.0.1:8080/en/dotnet
curl -s -D "$smoke_root/page2.headers" -o "$smoke_root/page2.body" http://127.0.0.1:8080/en/dotnet
head -n 1 "$smoke_root/page2.headers" | grep -q ' 200' || fail "anonymous page: not 200"
if grep -qi '^set-cookie:' "$smoke_root/page1.headers" "$smoke_root/page2.headers"; then
  fail "anonymous page: sets a cookie"
fi
grep -qi '^age:' "$smoke_root/page2.headers" || fail "anonymous page: the second response is not served from the output cache (no Age header)"
pass anonymous_page_is_cookieless_and_cached

question_url=http://127.0.0.1:8080/en/dotnet/questions/closure-for-loop
og_path="$(curl -fsS "$question_url" | grep -o 'property="og:image" content="[^"]*"' | head -n 1 | sed 's/.*content="https:\/\/smoke.example.test//; s/"$//')"
[ -n "$og_path" ] || fail "og image: the question page has no og:image"
curl -s -D "$smoke_root/og.headers" -o "$smoke_root/og.png" "http://127.0.0.1:8080$og_path"
head -n 1 "$smoke_root/og.headers" | grep -q ' 200' || fail "og image: $og_path is not 200"
grep -qi '^content-type: image/png' "$smoke_root/og.headers" || fail "og image: not image/png"
expect_eq "og image: PNG signature" "89504e47" "$(head -c 4 "$smoke_root/og.png" | od -An -tx1 | tr -d ' \n')"
[ "$(wc -c <"$smoke_root/og.png")" -gt 2000 ] || fail "og image: suspiciously small"
pass og_image_renders_in_container

# --- a content switch is visible without restarting the container -----------------------------------------------------
app_before="$(app_container)"
started_before="$(docker inspect --format '{{.State.StartedAt}}' "$app_before")"
page="$(curl -fsS "$question_url")"
case "$page" in
  *'What does the code print when lambdas capture a for loop variable?'*) ;;
  *) fail "content switch: original title missing" ;;
esac
switch_content release-b retitle_question
expect_eq "second content import: status" 200 "$(import_content)"
[ "$(content_hash)" != "$first_hash" ] || fail "content switch: the active content hash did not change"
page="$(curl -fsS "$question_url")"
case "$page" in
  *'Switched title for the smoke test'*) ;;
  *) fail "content switch: the new release is not served" ;;
esac
expect_eq "content switch: same container" "$app_before" "$(app_container)"
expect_eq "content switch: not restarted" "$started_before" "$(docker inspect --format '{{.State.StartedAt}}' "$app_before")"
pass content_switch_is_seen_without_restart

# --- the image ---------------------------------------------------------------------------------------------------------
image_uid="$(docker run --rm --entrypoint id "$base_image" -u)"
[ "$image_uid" != "0" ] || fail "image: runs as root"
running_uid="$(docker exec "$(app_container)" id -u)"
expect_eq "image: running uid" "$image_uid" "$running_uid"
wasm_size="$(docker run --rm --entrypoint sh "$base_image" -c 'stat -c %s /app/wwwroot/_framework/dotnet.native.*.wasm.br')"
[ "$wasm_size" -le 600000 ] || fail "image: the WebAssembly runtime was not relinked ($wasm_size bytes)"
docker run --rm --entrypoint test "$base_image" -x /app/migrate/efbundle || fail "image: the migration bundle is missing or not executable"
pass image_runs_as_non_root_with_relinked_runtime

# --- refusals leave everything alone ------------------------------------------------------------------------------------
before="$(snapshot)"
for guard in flags/maintenance .rotation-pending .umami-recovery; do
  touch "$APP_DIR/$guard"
  deploy "guard-$RANDOM" "$(commit_for 3)" "$(image_for 3)"
  expect_eq "guard $guard: exit status" 3 "$deploy_status"
  expect_eq "guard $guard: state unchanged" "$before" "$(snapshot)"
  rm -f "$APP_DIR/$guard"
done
grep -q "Umami upgrade" "$last_log" || fail "guard .umami-recovery: the log does not name the recovery section"
pass guard_files_block_deploys

env_hook=stale_db_password
deploy "stale-$RANDOM" "$(commit_for 3)" "$(image_for 3)"
expect_eq "stale password: exit status" 2 "$deploy_status"
expect_eq "stale password: state unchanged" "$before" "$(snapshot)"
env_hook=stale_umami_password
deploy "stale-umami-$RANDOM" "$(commit_for 3)" "$(image_for 3)"
expect_eq "stale umami password: exit status" 2 "$deploy_status"
expect_eq "stale umami password: state unchanged" "$before" "$(snapshot)"
pass stale_password_config_is_refused

bundle_hook=unparseable_umami_image
deploy "umami-parse-$RANDOM" "$(commit_for 3)" "$(image_for 3)"
expect_eq "unparseable umami image: exit status" 2 "$deploy_status"
grep -q "cannot determine the umami image" "$last_log" || fail "unparseable umami image: the log does not say why"
expect_eq "unparseable umami image: state unchanged" "$before" "$(snapshot)"
pass unparseable_umami_image_is_refused_before_activation

chmod 750 "$smoke_root"
deploy "traverse-$RANDOM" "$(commit_for 3)" "$(image_for 3)"
chmod 755 "$smoke_root"
expect_eq "untraversable flags path: exit status" 2 "$deploy_status"
grep -q "cannot traverse" "$last_log" || fail "untraversable flags path: the log does not explain the refusal"
expect_eq "untraversable flags path: state unchanged" "$before" "$(snapshot)"
pass flags_path_without_o_x_is_refused

# --- rollback ----------------------------------------------------------------------------------------------------------
good_before="$(sha256sum "$APP_DIR/last-good.env")"
bundle_hook=changed_app_compose
env_hook=bad_public_url
deploy run-fail "$(commit_for 4)" "$(image_for 4)"
expect_eq "failed readiness: exit status" 1 "$deploy_status"
grep -q "rolled back to" "$last_log" || fail "failed readiness: the log does not say it rolled back"
expect_eq "failed readiness: last-good.env untouched" "$good_before" "$(sha256sum "$APP_DIR/last-good.env")"
assert_rolled_back_to_last_good "failed readiness"
expect_eq "failed readiness: restored memory limit" "$((768 * 1024 * 1024))" "$(docker inspect --format '{{.HostConfig.Memory}}' "$(app_container)")"
pass failed_readiness_rolls_back_to_last_good

upload run-term "$(commit_for 5)" "$(image_for 5)"
start_deploy run-term "$(commit_for 5)" "$(image_for 5)"
good_bundle="$(dit_env_get "$APP_DIR/last-good.env" DIT_BUNDLE_SHA)"
for _ in $(seq 1 600); do
  [ "$(bundle_of_link)" != "$good_bundle" ] && break
  kill -0 "$runs_pid" 2>/dev/null || break
  sleep 0.1
done
[ "$(bundle_of_link)" != "$good_bundle" ] || fail "terminated deploy: the new bundle was never activated"
kill -TERM "$runs_pid"
term_status=0
wait "$runs_pid" || term_status=$?
expect_eq "terminated deploy: exit status" 1 "$term_status"
grep -q "rolled back to" "$last_log" || fail "terminated deploy: the log does not say it rolled back"
assert_rolled_back_to_last_good "terminated deploy"
bundle_hook=""
env_hook=bad_public_url
deploy run-fail-2 "$(commit_for 6)" "$(image_for 6)"
expect_eq "failed deploy after a terminated one: exit status" 1 "$deploy_status"
assert_rolled_back_to_last_good "failed deploy after a terminated one"
pass killed_deploy_then_failed_deploy_rolls_back_to_last_good

bundle_hook=reject_app_role_in_hba
deploy run-hba "$(commit_for 7)" "$(image_for 7)"
expect_eq "changed pg_hba.conf: exit status" 1 "$deploy_status"
grep -qi "pg_hba.conf" "$last_log" || fail "changed pg_hba.conf: the failure was not caused by the rejecting rule"
assert_rolled_back_to_last_good "changed pg_hba.conf"
pass failed_deploy_changing_hba_restores_previous_rules

# The pre-deploy backup in the sections above needs the S3 stand-in and its proxy, so they stay up in this tier too.
if [ "$smoke_mode" = "quick" ]; then
  echo "smoke passed (quick mode; skipped: serialisation, hard kills, backups and restore, content delivery, Umami guards, password rotation)"
  exit 0
fi

# --- serialisation ------------------------------------------------------------------------------------------------------
upload run-a "$(commit_for 8)" "$(image_for 8)"
upload run-b "$(commit_for 9)" "$(image_for 9)"
start_deploy run-a "$(commit_for 8)" "$(image_for 8)"
pid_a=$runs_pid
start_deploy run-b "$(commit_for 9)" "$(image_for 9)"
pid_b=$runs_pid
# An upload while two deploys run lands in its own staging directory and disturbs neither.
upload run-c "$(commit_for 10)" "$(image_for 10)"
tgz_c="$(sha256sum "$APP_DIR/staging/run-c/bundle.tgz")"
status_a=0
status_b=0
wait "$pid_a" || status_a=$?
wait "$pid_b" || status_b=$?
expect_eq "concurrent deploy a: exit status" 0 "$status_a"
expect_eq "concurrent deploy b: exit status" 0 "$status_b"
expect_eq "upload during deploys: staging intact" "$tgz_c" "$(sha256sum "$APP_DIR/staging/run-c/bundle.tgz")"

window() {
  local first last
  first="$(head -n 1 "$1" | cut -d' ' -f1)"
  last="$(tail -n 1 "$1" | cut -d' ' -f1)"
  printf '%s %s\n' "$(date -u -d "$first" +%s)" "$(date -u -d "$last" +%s)"
}
read -r start_a end_a < <(window "$logs/run-a.log")
read -r start_b end_b < <(window "$logs/run-b.log")
if [ "$start_a" -le "$start_b" ]; then
  [ "$start_b" -ge "$end_a" ] || fail "concurrent deploys overlapped (a: $start_a-$end_a, b: $start_b-$end_b)"
else
  [ "$start_a" -ge "$end_b" ] || fail "concurrent deploys overlapped (a: $start_a-$end_a, b: $start_b-$end_b)"
fi
cmp -s "$APP_DIR/.env" "$APP_DIR/last-good.env" || fail "concurrent deploys: .env differs from last-good.env"
assert_record_consistent "concurrent deploys" "$APP_DIR/last-good.env"
wait_ready
pass concurrent_uploads_and_deploys_serialise

# --- hard kills never mix two deployments -----------------------------------------------------------------------------
# DIT_TEST_KILL_AT stops deploy-app.sh with SIGKILL at a named step boundary (only with DIT_TEST_HOOKS=1). The record of a
# deployment lives in .env, current-bundle and last-good.env: right after the kill each must describe one deployment, and a
# mixture is allowed only while the in-progress marker tells the next run to repair. The next run must then restore one state.
state_commit() { # <old|new> <old-commit> <new-commit>
  if [ "$1" = "old" ]; then printf '%s' "$2"; else printf '%s' "$3"; fi
}

kill_point_case() { # <point> <n> <last-good: old|new> <.env: old|new> <current-bundle: old|new> <marker: yes|no>
  local point="$1" n="$2" run="kp-$1" old_commit new_commit
  old_commit="$(dit_env_get "$APP_DIR/last-good.env" DIT_COMMIT)"
  new_commit="$(commit_for "$n")"
  upload "$run" "$new_commit" "$(image_for "$n")"
  kill_at="$point"
  run_deploy "$run" "$new_commit" "$(image_for "$n")"
  expect_eq "$point: the run was killed" 137 "$deploy_status"
  assert_record_consistent "$point" "$APP_DIR/last-good.env"
  assert_record_consistent "$point" "$APP_DIR/.env"
  expect_eq "$point: last-good.env" "$(state_commit "$3" "$old_commit" "$new_commit")" "$(dit_env_get "$APP_DIR/last-good.env" DIT_COMMIT)"
  expect_eq "$point: .env" "$(state_commit "$4" "$old_commit" "$new_commit")" "$(dit_env_get "$APP_DIR/.env" DIT_COMMIT)"
  expect_eq "$point: current-bundle" "$(state_commit "$5" "$old_commit" "$new_commit")" "$(cat "$APP_DIR/current-bundle/.smoke-id")"
  if [ "$6" = "yes" ]; then
    [ -e "$APP_DIR/.deploy-in-progress" ] || fail "$point: no marker tells the next run to repair"
  else
    [ ! -e "$APP_DIR/.deploy-in-progress" ] || fail "$point: the marker outlived the publication"
  fi

  deploy "$run-next" "$(commit_for $((n + 500)))" "$(image_for $((n + 500)))"
  expect_eq "$point: the next run's exit status" 0 "$deploy_status"
  if [ "$6" = "yes" ]; then
    grep -q "a previous deployment was interrupted" "$last_log" || fail "$point: the next run did not repair first"
  fi
  cmp -s "$APP_DIR/.env" "$APP_DIR/last-good.env" || fail "$point: after the repair .env differs from last-good.env"
  assert_record_consistent "$point (repaired)" "$APP_DIR/last-good.env"
  expect_eq "$point: after the repair current-bundle" "$(dit_env_get "$APP_DIR/.env" DIT_BUNDLE_SHA)" "$(bundle_of_link)"
  [ ! -e "$APP_DIR/.deploy-in-progress" ] || fail "$point: the marker was left behind by the repair"
  wait_ready
}

kill_point_case after-marker 400 old old old yes
kill_point_case after-bundle-copy 401 old old old yes
kill_point_case after-env 402 old new old yes
kill_point_case after-switch 403 old new new yes
kill_point_case after-hba 404 old new new yes
kill_point_case after-ready 405 old new new yes
kill_point_case after-last-good 406 new new new yes
kill_point_case after-marker-removed 407 new new new no

# On top of the named points, kills at random moments (mostly inside Compose calls). The delay is drawn from 0 to
# max_tenths tenths of a second. A deploy on a fast runner can finish before its kill, so every miss halves the range
# (never below 0.5 s) and, when every planned iteration missed, up to four more run with the narrowed range. Only a run
# that was really interrupted counts, so the check stays meaningful and cannot fail by chance.
interrupted=0
max_tenths=259
kill_total=0
for i in $(seq 1 $((kill_iterations + 4))); do
  if [ "$i" -gt "$kill_iterations" ] && [ "$interrupted" -ge 1 ]; then
    break
  fi
  kill_total=$i
  n=$((100 + i))
  run="kill-$i"
  upload "$run" "$(commit_for "$n")" "$(image_for "$n")"
  start_deploy "$run" "$(commit_for "$n")" "$(image_for "$n")"
  pid="$runs_pid"
  # The earlier killed run may still be finishing a compose call that holds the lock, and a recovery runs first; the
  # random delay counts from the moment this run starts changing server state.
  for _ in $(seq 1 3000); do
    grep -q "activating bundle" "$logs/$run.log" 2>/dev/null && break
    kill -0 "$pid" 2>/dev/null || break
    sleep 0.1
  done
  sleep "$((RANDOM % max_tenths / 10)).$((RANDOM % 10))"
  if kill -KILL "$pid" 2>/dev/null; then
    interrupted=$((interrupted + 1))
  else
    max_tenths=$((max_tenths / 2))
    [ "$max_tenths" -ge 5 ] || max_tenths=5
  fi
  { wait "$pid"; } 2>/dev/null || true
  assert_record_consistent "kill iteration $i" "$APP_DIR/last-good.env"
  assert_record_consistent "kill iteration $i" "$APP_DIR/.env"
  link_commit="$(cat "$APP_DIR/current-bundle/.smoke-id")"
  env_commit="$(dit_env_get "$APP_DIR/.env" DIT_COMMIT)"
  if [ "$link_commit" != "$env_commit" ] && [ ! -e "$APP_DIR/.deploy-in-progress" ]; then
    fail "kill iteration $i: .env and current-bundle describe different deployments and nothing marks the run as interrupted"
  fi
done
# A run that finished before its random kill proves nothing, so the check needs at least one real interruption.
[ "$interrupted" -ge 1 ] || fail "none of the $kill_total runs was still running when it was killed"
printf 'killed %d of %d runs at random moments; %d later runs recovered an interrupted one\n' "$interrupted" "$kill_total" \
  "$(grep -l 'a previous deployment was interrupted' "$logs"/kill-*.log 2>/dev/null | wc -l | tr -d ' ')"
# The next run recovers an interrupted one before it deploys, so afterwards everything describes one deployment again.
final_n=$((100 + kill_total + 1))
previous_good_image="$(dit_env_get "$APP_DIR/last-good.env" APP_IMAGE)"
previous_good_bundle="$(dit_env_get "$APP_DIR/last-good.env" DIT_BUNDLE_SHA)"
deploy run-after-kills "$(commit_for "$final_n")" "$(image_for "$final_n")"
expect_eq "deploy after the kills: exit status" 0 "$deploy_status"
cmp -s "$APP_DIR/.env" "$APP_DIR/last-good.env" || fail "after the kills: .env differs from last-good.env"
assert_record_consistent "after the kills" "$APP_DIR/last-good.env"
expect_eq "after the kills: current-bundle" "$(dit_env_get "$APP_DIR/.env" DIT_BUNDLE_SHA)" "$(bundle_of_link)"
[ ! -e "$APP_DIR/.deploy-in-progress" ] || fail "after the kills: the in-progress marker was left behind"
wait_ready
pass killed_between_record_steps_never_mixes_deployments

# Retention: the running and the previous good deployment are kept, nothing else but at most one older bundle.
current_bundle="$(dit_env_get "$APP_DIR/last-good.env" DIT_BUNDLE_SHA)"
bundle_count="$(find "$APP_DIR/bundles" -mindepth 1 -maxdepth 1 -type d | wc -l | tr -d ' ')"
[ "$bundle_count" -le 3 ] || fail "retention: $bundle_count bundles kept"
[ -d "$APP_DIR/bundles/$current_bundle" ] || fail "retention: the current bundle was pruned"
[ -d "$APP_DIR/bundles/$previous_good_bundle" ] || fail "retention: the previous good bundle was pruned"
image_count="$(docker image ls --filter "reference=$registry/dit" --format '{{.ID}}' | sort -u | wc -l | tr -d ' ')"
[ "$image_count" -le 2 ] || fail "retention: $image_count application images kept"
docker image inspect "$(dit_env_get "$APP_DIR/last-good.env" APP_IMAGE)" >/dev/null || fail "retention: the current image was pruned"
docker image inspect "$previous_good_image" >/dev/null || fail "retention: the previous good image was pruned"
pass old_images_and_bundles_are_pruned

# --- backups, restore and the daily check ---------------------------------------------------------------------------------
# Everything below runs the scripts the way the server does, from $APP_DIR/current-bundle/remote, against the live stack
# and the rclone stand-in for the bucket.
backup_script="$APP_DIR/current-bundle/remote/backup.sh"
restore_script="$APP_DIR/current-bundle/remote/restore.sh"

run_backup() { # <daily|predeploy>; sets backup_status, backup_out (stdout) and backup_err (the log file)
  backup_runs=$((backup_runs + 1))
  backup_err="$logs/backup-$backup_runs.log"
  backup_status=0
  backup_out="$(bash "$backup_script" "$1" 2>"$backup_err")" || backup_status=$?
}

run_restore() { # <identity file> <restore.sh arguments...>; sets restore_status, restore_out and restore_err
  local identity="$1"
  shift
  restore_runs=$((restore_runs + 1))
  restore_err="$logs/restore-$restore_runs.log"
  restore_status=0
  restore_out="$(bash "$restore_script" "$@" <"$identity" 2>"$restore_err")" || restore_status=$?
}

latest_dump() { # <db> <kind>
  find "$APP_DIR/backups" -maxdepth 1 -name "$1-*-$2.dump.age" | LC_ALL=C sort | tail -n 1
}

database_names() {
  dit_psql postgres -tA -c "select datname from pg_database where datname like 'trainer%' or datname like 'umami%' order by 1" </dev/null | LC_ALL=C sort | tr '\n' ' '
}

table_counts() { # <database>: "table|rows" for every public table
  local table
  for table in $(dit_psql "$1" -tA -c "select table_name from information_schema.tables where table_schema = 'public' and table_type = 'BASE TABLE' order by 1" </dev/null); do
    printf '%s|%s\n' "$table" "$(dit_psql "$1" -tA -c "select count(*) from \"$table\"" </dev/null)"
  done
}

key_ring_fingerprint() {
  dit_psql trainer -tA -c "select count(*) || ':' || coalesce(md5(string_agg(k::text, '|' order by k::text)), '') from data_protection_keys k" </dev/null
}

offsite() { # rclone against the stand-in bucket directly (full rights), for what only a reader could do
  (
    export RCLONE_CONFIG=/dev/null RCLONE_CONFIG_OFFSITE_TYPE=s3 RCLONE_CONFIG_OFFSITE_PROVIDER=Other
    export RCLONE_CONFIG_OFFSITE_ENDPOINT="${offsite_endpoint:-http://127.0.0.1:9001}" RCLONE_CONFIG_OFFSITE_NO_CHECK_BUCKET=true
    export RCLONE_CONFIG_OFFSITE_ACCESS_KEY_ID="$SMOKE_S3_ACCESS_KEY" RCLONE_CONFIG_OFFSITE_SECRET_ACCESS_KEY="$SMOKE_S3_SECRET_KEY"
    rclone "$@" </dev/null
  )
}

stop_s3() {
  docker compose -p dit-smoke-s3 -f "$deploy_src/tests/compose.smoke.yaml" stop --timeout 2 s3 >/dev/null
}

start_s3() {
  docker compose -p dit-smoke-s3 -f "$deploy_src/tests/compose.smoke.yaml" up -d --wait s3 >/dev/null
}

assert_no_secrets() { # <label> <file>...
  local label="$1" secret
  shift
  for secret in 'AGE-SECRET-KEY-' "$SMOKE_S3_SECRET_KEY" "$DB_PASSWORD" "$UMAMI_DB_PASSWORD"; do
    if grep -qF -- "$secret" "$@"; then
      fail "$label: a log contains a secret"
    fi
  done
}

# The key ring must be there before anything is backed up, or "the dump contains the key ring" proves nothing.
[ "$(dit_psql trainer -tA -c 'select count(*) from data_protection_keys' </dev/null)" -gt 0 ] || fail "backups: the application has not created a Data Protection key yet"

# --- no recipient: the backup refuses, and so does a deployment that would depend on it -------------------------------
mv "$APP_DIR/backup/recipients.txt" "$smoke_root/recipients.aside"
run_backup daily
expect_eq "backup without a recipient: exit status" 4 "$backup_status"
[ -z "$(find "$APP_DIR/backups" -name '*.partial')" ] || fail "backup without a recipient: a partial file was left"
before="$(snapshot)"
deploy "no-recipient-$RANDOM" "$(commit_for 3)" "$(image_for 3)"
expect_eq "deploy without a recipient: exit status" 1 "$deploy_status"
grep -q "the pre-deploy backup failed; nothing was changed" "$last_log" || fail "deploy without a recipient: the log does not say the backup failed"
expect_eq "deploy without a recipient: state unchanged" "$before" "$(snapshot)"
mv "$smoke_root/recipients.aside" "$APP_DIR/backup/recipients.txt"
pass backup_without_recipient_fails

# --- an encrypted dump with the key ring, a sidecar per database, and the off-site copy --------------------------------
run_backup daily
expect_eq "daily backup: exit status" 0 "$backup_status"
trainer_dump="$(latest_dump trainer daily)"
umami_dump_file="$(latest_dump umami daily)"
if [ ! -f "$trainer_dump" ] || [ ! -f "$umami_dump_file" ]; then
  fail "daily backup: a dump is missing"
fi
[ "$(head -c 5 "$trainer_dump")" != "PGDMP" ] || fail "daily backup: the trainer dump is not encrypted"
expect_eq "daily backup: dump mode" 600 "$(stat -c %a "$trainer_dump")"
toc="$(age -d -i "$identity_file" "$trainer_dump" | dit_compose exec -T --user postgres postgres pg_restore -l)"
grep -q 'TABLE DATA public data_protection_keys' <<<"$toc" || fail "daily backup: the decrypted dump does not list the key ring data"
for dump in "$trainer_dump" "$umami_dump_file"; do
  [ -e "$dump.uploaded" ] || fail "daily backup: $(basename "$dump") has no upload marker"
  offsite lsf offsite:dit-smoke | grep -qx "$(basename "$dump")" || fail "daily backup: $(basename "$dump") is not in the bucket"
  offsite lsf offsite:dit-smoke | grep -qx "$(basename "$dump").meta" || fail "daily backup: the sidecar of $(basename "$dump") is not in the bucket"
done
# The server's key may list and write but not read: everything the backup scripts did so far (predeploy runs included) went
# through the proxy that refuses reads, so no read was attempted and every write was accepted.
[ "$(grep -c '^PUT /dit-smoke/.*\.dump\.age 200$' "$s3_proxy_log")" -ge 2 ] || fail "off-site key: no dump upload reached the bucket through the restricted endpoint: $(cat "$s3_proxy_log")"
if grep -Eq '^(GET|HEAD|DELETE) /dit-smoke/[^ ]+|^[A-Z]+ .* (403|501|502)$' "$s3_proxy_log"; then
  fail "off-site key: the backup scripts read, deleted or were refused: $(grep -E '^(GET|HEAD|DELETE) /dit-smoke/[^ ]+|^[A-Z]+ .* (403|501|502)$' "$s3_proxy_log" | head -n 5 | tr '\n' ';')"
fi
# The restriction itself is real: the same key reads nothing through the same endpoint.
# rclone does not fail on every refused read, so the proof is the content: nothing may arrive.
rm -f "$smoke_root/denied.meta"
offsite_endpoint=http://127.0.0.1:9000 offsite copyto "offsite:dit-smoke/$(basename "$trainer_dump").meta" "$smoke_root/denied.meta" >/dev/null 2>&1 || true
[ ! -s "$smoke_root/denied.meta" ] || fail "off-site key: the restricted endpoint let the key read an object"
[ -s "$trainer_dump.meta" ] || fail "off-site key: the local sidecar is empty, so the read check proves nothing"
offsite copyto "offsite:dit-smoke/$(basename "$trainer_dump").meta" "$smoke_root/allowed.meta" >/dev/null 2>&1 || true
[ -s "$smoke_root/allowed.meta" ] || fail "off-site key: the full-rights helper cannot read the sidecar, so the read check proves nothing"
grep -Eq '^(GET|HEAD) /dit-smoke/[^ ]+ 403$' "$s3_proxy_log" || fail "off-site key: the refusal of a read was not recorded"
offsite_endpoint=http://127.0.0.1:9000 offsite lsf offsite:dit-smoke >/dev/null || fail "off-site key: listing through the restricted endpoint failed"
: >"$s3_proxy_log"
if [ ! -s "$APP_DIR/backups/last-success" ] || [ ! -s "$APP_DIR/backups/last-offsite" ]; then
  fail "daily backup: the success records were not written"
fi
[ -z "$(find "$APP_DIR/backups" \( -name '*.partial' -o -name '.tmp.*' \))" ] || fail "daily backup: temporary files were left"
assert_no_secrets "daily backup" "$backup_err"
pass backup_is_encrypted_and_contains_key_ring

# --- database-specific sidecars, and both dumps restore in scratch mode ----------------------------------------------------
trainer_meta_text="$(cat "$trainer_dump.meta")"
umami_meta_text="$(cat "$umami_dump_file.meta")"
for key in DB=trainer KIND=daily APP_IMAGE= DIT_COMMIT= EF_LAST_MIGRATION= ACTIVE_CONTENT_HASH= CONTENT_RELEASE=; do
  grep -q "^$key" <<<"$trainer_meta_text" || fail "trainer sidecar: no $key"
done
expect_eq "trainer sidecar: image" "$(dit_env_get "$APP_DIR/.env" APP_IMAGE)" "$(sed -n 's/^APP_IMAGE=//p' "$trainer_dump.meta")"
expect_eq "trainer sidecar: commit" "$(dit_env_get "$APP_DIR/.env" DIT_COMMIT)" "$(sed -n 's/^DIT_COMMIT=//p' "$trainer_dump.meta")"
expect_eq "trainer sidecar: content hash" "$(content_hash)" "$(sed -n 's/^ACTIVE_CONTENT_HASH=//p' "$trainer_dump.meta")"
expect_eq "trainer sidecar: content release" "$(basename "$(readlink "$APP_DIR/content/current")")" "$(sed -n 's/^CONTENT_RELEASE=//p' "$trainer_dump.meta")"
expect_eq "trainer sidecar: last migration" "$(dit_psql trainer -tA -c 'select migration_id from ef_migrations_history order by migration_id desc limit 1' </dev/null)" \
  "$(sed -n 's/^EF_LAST_MIGRATION=//p' "$trainer_dump.meta")"
for key in DB=umami KIND=daily UMAMI_IMAGE=; do
  grep -q "^$key" <<<"$umami_meta_text" || fail "umami sidecar: no $key"
done
if grep -q -e APP_IMAGE -e DIT_COMMIT -e EF_ -e CONTENT <<<"$umami_meta_text"; then
  fail "umami sidecar: carries trainer keys"
fi
expect_eq "umami sidecar: image" "$(dit_umami_image "$APP_DIR/compose.yaml")" "$(sed -n 's/^UMAMI_IMAGE=//p' "$umami_dump_file.meta")"
assert_no_secrets "sidecars" "$trainer_dump.meta" "$umami_dump_file.meta"

live_counts="$(table_counts trainer)"
run_restore "$identity_file" "$trainer_dump" trainer
expect_eq "scratch restore of trainer: exit status" 0 "$restore_status"
scratch_trainer="$restore_out"
case "$scratch_trainer" in trainer_restore_*) ;; *) fail "scratch restore of trainer: stdout is '$restore_out', not a database name" ;; esac
expect_eq "scratch restore of trainer: row counts" "$live_counts" "$(table_counts "$scratch_trainer")"
[ "$(dit_psql "$scratch_trainer" -tA -c 'select count(*) from data_protection_keys' </dev/null)" -gt 0 ] || fail "scratch restore of trainer: the key ring is empty"
expect_eq "scratch restore of trainer: the live database is untouched" "$live_counts" "$(table_counts trainer)"
run_restore "$identity_file" "$umami_dump_file" umami
expect_eq "scratch restore of umami: exit status" 0 "$restore_status"
scratch_umami="$restore_out"
case "$scratch_umami" in umami_restore_*) ;; *) fail "scratch restore of umami: stdout is '$restore_out', not a database name" ;; esac
expect_eq "scratch restore of umami: row counts" "$(table_counts umami)" "$(table_counts "$scratch_umami")"
pass both_dumps_have_database_specific_meta_and_restore

# A leftover scratch database blocks the next restore; --drop-scratch removes it.
run_restore "$identity_file" "$trainer_dump" trainer
expect_eq "restore while a scratch database exists: exit status" 1 "$restore_status"
grep -q "a scratch database" "$restore_err" || fail "restore while a scratch database exists: the log does not say why"
run_restore "$identity_file" --drop-scratch "$scratch_trainer"
expect_eq "drop of the trainer scratch database: exit status" 0 "$restore_status"
run_restore "$identity_file" --drop-scratch "$scratch_umami"
expect_eq "drop of the umami scratch database: exit status" 0 "$restore_status"
expect_eq "no scratch database is left" "trainer umami " "$(database_names)"

# An identity copied through a Windows password manager has CRLF line ends.
sed 's/$/\r/' "$identity_file" >"$smoke_root/identity-crlf.txt"
run_restore "$smoke_root/identity-crlf.txt" "$trainer_dump" trainer
expect_eq "scratch restore with a CRLF identity: exit status" 0 "$restore_status"
run_restore "$identity_file" --drop-scratch "$restore_out"
# "bash -x" must not print the key, neither on the assignment nor in the decryption.
xtrace_err="$logs/restore-xtrace.log"
xtrace_out="$(bash -x "$restore_script" "$trainer_dump" trainer <"$identity_file" 2>"$xtrace_err")"
assert_no_secrets "restore under xtrace" "$xtrace_err"
[ -s "$xtrace_err" ] || fail "restore under xtrace: no trace was produced, so the check proves nothing"
run_restore "$identity_file" --drop-scratch "$xtrace_out"
expect_eq "no scratch database is left after the CRLF and xtrace runs" "trainer umami " "$(database_names)"
pass restore_into_scratch_db_matches_row_counts

# --- a wrong identity changes nothing ----------------------------------------------------------------------------------------
age-keygen -o "$smoke_root/wrong-identity.txt" >/dev/null 2>&1
names_before="$(database_names)"
# A restore with the wrong identity must not touch the live application at all: same container, no restart.
app_id_before="$(app_container)"
app_restarts_before="$(docker inspect --format '{{.RestartCount}}' "$app_id_before")"
for mode_args in "" "--replace"; do
  # shellcheck disable=SC2086 # the mode is one optional word
  run_restore "$smoke_root/wrong-identity.txt" $mode_args "$trainer_dump" trainer
  expect_eq "wrong identity ($mode_args): exit status" 1 "$restore_status"
  expect_eq "wrong identity ($mode_args): databases" "$names_before" "$(database_names)"
  expect_eq "wrong identity ($mode_args): row counts" "$live_counts" "$(table_counts trainer)"
  [ ! -e "$APP_DIR/flags/maintenance" ] || fail "wrong identity ($mode_args): the maintenance flag was written"
  assert_no_secrets "wrong identity" "$restore_err"
done
expect_eq "wrong identity: the application container" "$app_id_before" "$(app_container)"
expect_eq "wrong identity: the application was not restarted" "$app_restarts_before" "$(docker inspect --format '{{.RestartCount}}' "$app_id_before")"
# Umami may still be in a restart back-off from the kill loop, so the full service list is awaited, not compared once.
wait_running_services "wrong identity: the application still runs" "app postgres umami "
pass restore_with_wrong_identity_leaves_live_database_untouched

# --- replace: an older schema is migrated, the key ring survives -------------------------------------------------------------
# Make the live database look like the one a nightly dump of an earlier release would hold: drop what the newest migration
# created and forget it. The dump taken now is then older than the application's schema.
newest_migration="$(dit_psql trainer -tA -c 'select migration_id from ef_migrations_history order by migration_id desc limit 1' </dev/null)"
case "$newest_migration" in
  *_QuestionFeedback) ;;
  *) fail "replace: the newest migration is $newest_migration; update the downgrade step of this check" ;;
esac
dit_psql trainer -c "drop table question_ratings, question_reports; delete from ef_migrations_history where migration_id = '$newest_migration'" </dev/null >/dev/null
run_backup daily
expect_eq "backup of the older schema: exit status" 0 "$backup_status"
old_schema_dump="$(latest_dump trainer daily)"
ring_before="$(key_ring_fingerprint)"
run_restore "$identity_file" --replace "$old_schema_dump" trainer
expect_eq "replace of trainer: exit status" 0 "$restore_status"
[ ! -e "$APP_DIR/flags/maintenance" ] || fail "replace of trainer: the maintenance flag is still there"
wait_ready
expect_eq "replace of trainer: the migration was applied again" "$newest_migration" "$(dit_psql trainer -tA -c 'select migration_id from ef_migrations_history order by migration_id desc limit 1' </dev/null)"
expect_eq "replace of trainer: the feedback tables exist again" 2 "$(dit_psql trainer -tA -c "select count(*) from information_schema.tables where table_name in ('question_ratings', 'question_reports')" </dev/null)"
expect_eq "replace of trainer: the key ring is the dump's" "$ring_before" "$(key_ring_fingerprint)"
old_database="$(sed -n 's/.*the previous database is kept as \([^ ]*\) .*/\1/p' "$restore_err" | tail -n 1)"
case "$old_database" in trainer_old_*) ;; *) fail "replace of trainer: the log does not name the kept database" ;; esac
expect_eq "replace of trainer: databases" "$(printf '%s\n' trainer "$old_database" umami | LC_ALL=C sort | tr '\n' ' ')" "$(database_names)"
expect_eq "replace of trainer: running services" "app postgres umami " "$(running_services)"
[ -n "$(content_hash)" ] || fail "replace of trainer: the startup import did not bring the content back"
assert_no_secrets "replace of trainer" "$restore_err"
run_restore "$identity_file" --drop-old "$old_database"
expect_eq "drop of the previous database: exit status" 0 "$restore_status"
expect_eq "databases after the drop" "trainer umami " "$(database_names)"
pass restore_replace_keeps_key_ring_and_runs_migrations

# --- a POST that nginx admitted before the flag meets the restored application -----------------------------------------------
dit_psql trainer -c 'create table smoke_dropped_later (id int)' </dev/null >/dev/null
run_backup daily
expect_eq "backup before the maintenance check: exit status" 0 "$backup_status"
fresh_dump="$(latest_dump trainer daily)"
pause_file="$smoke_root/restore.pause"
: >"$pause_file"
restore_runs=$((restore_runs + 1))
restore_err="$logs/restore-$restore_runs.log"
DIT_TEST_HOOKS=1 DIT_TEST_PAUSE_FILE="$pause_file" bash "$restore_script" --replace "$fresh_dump" trainer <"$identity_file" >/dev/null 2>"$restore_err" &
restore_pid=$!
for _ in $(seq 1 600); do
  grep -q "the maintenance flag is still in place" "$restore_err" 2>/dev/null && break
  kill -0 "$restore_pid" 2>/dev/null || break
  sleep 0.5
done
grep -q "the maintenance flag is still in place" "$restore_err" || fail "maintenance gate: the restore did not reach the restored application"
[ -e "$APP_DIR/flags/maintenance" ] || fail "maintenance gate: the flag is missing while the restore waits"
counts_before_post="$(table_counts trainer)"
# The request line and headers arrive, the body does not: the state of a request nginx is still buffering when the flag appears.
exec 7<>/dev/tcp/127.0.0.1/8080
printf 'POST /api/answers/batch HTTP/1.1\r\nHost: smoke.example.test\r\nContent-Type: application/json\r\nContent-Length: 1000\r\n\r\n{' >&7
status_line=""
IFS= read -r -t 20 status_line <&7 || true
exec 7>&- 7<&-
case "$status_line" in
  'HTTP/1.1 503'*) ;;
  *) fail "maintenance gate: a half-sent POST got '$status_line', not 503" ;;
esac
expect_eq "maintenance gate: a complete POST" 503 "$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' -d '{}' http://127.0.0.1:8080/api/answers/batch)"
expect_eq "maintenance gate: a GET is still served" 200 "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8080/en/dotnet)"
expect_eq "maintenance gate: row counts" "$counts_before_post" "$(table_counts trainer)"
# A migration newer than the dump may drop a table the dump contains; the check after the start must not take that for a failure.
dit_psql trainer -c 'drop table smoke_dropped_later' </dev/null >/dev/null
rm -f "$pause_file"
restore_status=0
wait "$restore_pid" || restore_status=$?
expect_eq "restore after the pause: exit status" 0 "$restore_status"
expect_eq "restore after the pause: the dropped table stays dropped" 0 "$(dit_psql trainer -tA -c "select count(*) from information_schema.tables where table_name = 'smoke_dropped_later'" </dev/null)"
[ ! -e "$APP_DIR/flags/maintenance" ] || fail "maintenance gate: the flag outlived the restore"
wait_ready
after_flag_status="$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' -d '{}' http://127.0.0.1:8080/api/answers/batch)"
[ "$after_flag_status" != "503" ] || fail "maintenance gate: the POST is still rejected after the flag was removed"
old_database="$(sed -n 's/.*the previous database is kept as \([^ ]*\) .*/\1/p' "$restore_err" | tail -n 1)"
run_restore "$identity_file" --drop-old "$old_database"
expect_eq "drop of the previous database: exit status" 0 "$restore_status"
pass restore_admitted_slow_post_is_rejected_after_restart

# --- a failure after the swap stops everything for the operator ------------------------------------------------------------------
run_backup daily
expect_eq "backup before the failing restore: exit status" 0 "$backup_status"
failing_dump="$(latest_dump trainer daily)"
counts_live_before="$(table_counts trainer)"
cp -p "$APP_DIR/.env" "$smoke_root/env.keep"
sed -i "s|^DB_PASSWORD=.*|DB_PASSWORD='not-the-running-password'|" "$APP_DIR/.env"
run_restore "$identity_file" --replace "$failing_dump" trainer
failing_restore_err="$restore_err"
cp -p "$smoke_root/env.keep" "$APP_DIR/.env"
expect_eq "failing restore: exit status" 1 "$restore_status"
[ -f "$APP_DIR/flags/maintenance" ] || fail "failing restore: the maintenance flag was removed"
expect_eq "failing restore: the flag names three databases" 3 "$(wc -l <"$APP_DIR/flags/maintenance" | tr -d ' ')"
mapfile -t flag_names <"$APP_DIR/flags/maintenance"
expect_eq "failing restore: the live name" trainer "${flag_names[0]}"
failed_old="${flag_names[1]}"
failed_scratch="${flag_names[2]}"
expect_eq "failing restore: databases" "$(printf '%s\n' trainer "$failed_old" umami | LC_ALL=C sort | tr '\n' ' ')" "$(database_names)"
grep -q "ALTER DATABASE" "$failing_restore_err" || fail "failing restore: the log gives no rename commands"
grep -q "Interrupted restore" "$failing_restore_err" || fail "failing restore: the log does not name the recovery section"
expect_eq "failing restore: the previous database was not written" "$counts_live_before" "$(table_counts "$failed_old")"
expect_eq "failing restore: the swapped database was not written" "$counts_live_before" "$(table_counts trainer)"
expect_eq "failing restore: running services" "postgres umami " "$(running_services)"
before="$(snapshot)"
deploy "after-failed-restore-$RANDOM" "$(commit_for 3)" "$(image_for 3)"
expect_eq "deploy after a failed restore: exit status" 3 "$deploy_status"
expect_eq "deploy after a failed restore: state unchanged" "$before" "$(snapshot)"
run_restore "$identity_file" "$failing_dump" trainer
expect_eq "restore while the flag exists: exit status" 3 "$restore_status"
run_backup daily
expect_eq "backup while the flag exists: exit status" 3 "$backup_status"
assert_no_secrets "failing restore" "$failing_restore_err"
# The operator's way out, with the commands the script printed (the swapped state, going back).
dit_psql postgres -c "ALTER DATABASE \"trainer\" RENAME TO \"$failed_scratch\"" </dev/null >/dev/null
dit_psql postgres -c "ALTER DATABASE \"$failed_old\" RENAME TO \"trainer\"" </dev/null >/dev/null
dit_compose up -d --wait --wait-timeout "$DIT_WAIT_TIMEOUT" app >/dev/null 2>&1
rm -f "$APP_DIR/flags/maintenance"
wait_ready
run_restore "$identity_file" --drop-scratch "$failed_scratch"
expect_eq "drop of the abandoned restore: exit status" 0 "$restore_status"
expect_eq "databases after the operator's recovery" "trainer umami " "$(database_names)"
expect_eq "row counts after the operator's recovery" "$counts_live_before" "$(table_counts trainer)"
pass restore_replace_failure_keeps_maintenance_and_blocks_deploys

# --- an empty server rebuilt from an older backup, with newer content on disk ----------------------------------------------------
run_backup daily
expect_eq "backup of the old state: exit status" 0 "$backup_status"
rebuild_trainer_dump="$(latest_dump trainer daily)"
rebuild_umami_dump="$(latest_dump umami daily)"
hash_in_backup="$(content_hash)"
switch_content release-rebuild retitle_question_again
expect_eq "newer content import: status" 200 "$(import_content)"
hash_after_backup="$(content_hash)"
[ "$hash_after_backup" != "$hash_in_backup" ] || fail "rebuild: the content did not change after the backup"
ring_expected="$(key_ring_fingerprint)"
# What the owner would have: the encrypted files from the bucket and the identity, nothing else.
rm -rf "$smoke_root/offsite-copy"
mkdir -p "$smoke_root/offsite-copy"
offsite copy "offsite:dit-smoke/$(basename "$rebuild_trainer_dump")" "$smoke_root/offsite-copy"
offsite copy "offsite:dit-smoke/$(basename "$rebuild_umami_dump")" "$smoke_root/offsite-copy"
dit_compose down -v --remove-orphans --timeout 5 >/dev/null 2>&1
expect_eq "rebuild: nothing runs" "" "$(running_services | tr -d ' ')"
dit_compose up -d --wait --wait-timeout "$DIT_WAIT_TIMEOUT" postgres >/dev/null 2>&1
bash "$APP_DIR/current-bundle/remote/ensure-databases.sh" >/dev/null 2>&1
run_restore "$identity_file" --replace "$smoke_root/offsite-copy/$(basename "$rebuild_trainer_dump")" trainer
expect_eq "rebuild: restore of trainer: exit status" 0 "$restore_status"
run_restore "$identity_file" --replace "$smoke_root/offsite-copy/$(basename "$rebuild_umami_dump")" umami
expect_eq "rebuild: restore of umami: exit status" 0 "$restore_status"
wait_ready
expect_eq "rebuild: the key ring is the backup's" "$ring_expected" "$(key_ring_fingerprint)"
expect_eq "rebuild: the content on disk is newer than the backup and wins" "$hash_after_backup" "$(content_hash)"
expect_eq "rebuild: running services" "app postgres umami " "$(running_services)"
expect_eq "rebuild: Umami answers" 200 "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:3001/api/heartbeat)"
for kept in $(database_names); do
  case "$kept" in *_old_*) run_restore "$identity_file" --drop-old "$kept" ;; esac
done
expect_eq "rebuild: databases" "trainer umami " "$(database_names)"
pass rebuild_empty_server_from_older_backup

# --- off-site outage and retention -------------------------------------------------------------------------------------------------
# 20 old daily files per database; the three oldest were never uploaded. The marker is the only thing that makes a file
# deletable.
for day in $(seq 1 20); do
  for seeded_db in trainer umami; do
    seeded="$APP_DIR/backups/$seeded_db-202601$(printf '%02d' "$day")T030000Z-daily.dump.age"
    : >"$seeded"
    [ "$day" -le 3 ] || : >"$seeded.uploaded"
  done
done
stop_s3
offsite_before="$(cat "$APP_DIR/backups/last-offsite")"
success_before="$(cat "$APP_DIR/backups/last-success")"
sleep 1
run_backup daily
expect_eq "daily backup during an outage: exit status" 1 "$backup_status"
[ "$(cat "$APP_DIR/backups/last-offsite")" = "$offsite_before" ] || fail "outage: last-offsite moved although nothing was uploaded"
[ "$(cat "$APP_DIR/backups/last-success")" != "$success_before" ] || fail "outage: last-success did not move although both dumps succeeded"
outage_dump="$(latest_dump trainer daily)"
if [ ! -f "$outage_dump" ] || [ -e "$outage_dump.uploaded" ]; then
  fail "outage: the new dump is missing or marked uploaded"
fi
for day in 01 02 03; do
  for seeded_db in trainer umami; do
    [ -e "$APP_DIR/backups/$seeded_db-202601${day}T030000Z-daily.dump.age" ] || fail "outage: retention deleted the un-uploaded $seeded_db file of day $day"
  done
done
assert_no_secrets "outage" "$backup_err"
run_backup predeploy
expect_eq "predeploy backup during an outage: exit status" 0 "$backup_status"
case "$(printf '%s\n' "$backup_out" | tail -n 1)" in
  "$APP_DIR"/backups/umami-*-predeploy.dump.age) ;;
  *) fail "predeploy backup: the last stdout line is '$(printf '%s\n' "$backup_out" | tail -n 1)', not the umami dump path" ;;
esac
if grep -q 20260101T030000Z "$backup_err"; then
  fail "predeploy backup: it tried the backlog of older files"
fi
expect_eq "predeploy backup: stdout is only the path" 1 "$(printf '%s\n' "$backup_out" | wc -l | tr -d ' ')"
[ ! -e "$(printf '%s\n' "$backup_out" | tail -n 1).uploaded" ] || fail "predeploy backup: marked uploaded during an outage"
start_s3
run_backup daily
expect_eq "daily backup after the outage: exit status" 0 "$backup_status"
expect_eq "everything is uploaded after the outage" 0 "$(find "$APP_DIR/backups" -name '*.dump.age' | while read -r f; do [ -e "$f.uploaded" ] || echo "$f"; done | wc -l | tr -d ' ')"
[ "$(cat "$APP_DIR/backups/last-offsite")" != "$offsite_before" ] || fail "after the outage: last-offsite did not move"
pass offsite_outage_keeps_unuploaded_files_and_predeploy_succeeds

for seeded_db in trainer umami; do
  expect_eq "retention: $seeded_db daily files" 14 "$(find "$APP_DIR/backups" -maxdepth 1 -name "$seeded_db-*-daily.dump.age" | wc -l | tr -d ' ')"
  [ ! -e "$APP_DIR/backups/$seeded_db-20260101T030000Z-daily.dump.age" ] || fail "retention: the oldest $seeded_db file survived"
  [ "$(find "$APP_DIR/backups" -maxdepth 1 -name "$seeded_db-*-predeploy.dump.age" | wc -l | tr -d ' ')" -le 5 ] || fail "retention: more than 5 predeploy files of $seeded_db"
done
[ -z "$(find "$APP_DIR/backups" -name '*.meta' | while read -r meta; do [ -e "${meta%.meta}" ] || echo "$meta"; done)" ] || fail "retention: a sidecar outlived its dump"
pass retention_keeps_14_daily

# With PostgreSQL down both dumps fail and nothing is pending: that is not a fresh off-site copy.
offsite_before_down="$(cat "$APP_DIR/backups/last-offsite")"
success_before_down="$(cat "$APP_DIR/backups/last-success")"
dit_compose stop postgres >/dev/null 2>&1
sleep 1
run_backup daily
expect_eq "daily backup with PostgreSQL down: exit status" 1 "$backup_status"
grep -q "the dump of trainer failed" "$backup_err" || fail "daily backup with PostgreSQL down: the log does not name the failed dump"
expect_eq "daily backup with PostgreSQL down: last-offsite" "$offsite_before_down" "$(cat "$APP_DIR/backups/last-offsite")"
expect_eq "daily backup with PostgreSQL down: last-success" "$success_before_down" "$(cat "$APP_DIR/backups/last-success")"
[ -z "$(find "$APP_DIR/backups" -name '*.partial')" ] || fail "daily backup with PostgreSQL down: a partial file was left"
dit_compose up -d --wait --wait-timeout "$DIT_WAIT_TIMEOUT" postgres >/dev/null 2>&1
wait_ready

# --- the daily check ------------------------------------------------------------------------------------------------------------------
ops_check() {
  ops_status=0
  ops_out="$(DIT_OPS_TLS_ADDR=127.0.0.1:8443 DIT_OPS_MAX_DISK_PERCENT="${ops_disk_limit:-100}" bash "$APP_DIR/current-bundle/remote/ops-check.sh" 2>"$logs/ops-check.err")" || ops_status=$?
}
ops_line() { # <check> -> "ok" or "fail"
  printf '%s\n' "$ops_out" | awk -v check="$1" '$2 == check { print $1 }'
}

openssl req -x509 -newkey rsa:2048 -nodes -days 30 -subj "/CN=smoke.example.test" -keyout "$smoke_root/tls.key" -out "$smoke_root/tls.crt" >/dev/null 2>&1
openssl s_server -accept 8443 -cert "$smoke_root/tls.crt" -key "$smoke_root/tls.key" -quiet </dev/null >/dev/null 2>&1 &
tls_pid=$!
sleep 1
ops_check
expect_eq "ops-check on a healthy server: exit status" 0 "$ops_status"
for check in readiness backup-identity backup-age offsite-age disk certificate restarts guards deploy-hold; do
  expect_eq "ops-check on a healthy server: $check" ok "$(ops_line "$check")"
done

printf '2020-01-01T00:00:00Z\n' >"$APP_DIR/backups/last-success"
printf '2020-01-01T00:00:00Z\n' >"$APP_DIR/backups/last-offsite"
printf '%s\n' "$(commit_for 9)" >"$APP_DIR/flags/deploy-hold"
touch "$APP_DIR/.rotation-pending" "$APP_DIR/.import-pending" "$APP_DIR/.deploy-in-progress"
ops_disk_limit=1
ops_check
unset ops_disk_limit
expect_eq "ops-check with problems: exit status" 1 "$ops_status"
for check in backup-age offsite-age disk guards deploy-hold; do
  expect_eq "ops-check with problems: $check" fail "$(ops_line "$check")"
done
for check in readiness backup-identity certificate restarts; do
  expect_eq "ops-check with problems: $check" ok "$(ops_line "$check")"
done
printf '%s\n' "$ops_out" | grep '^fail guards' | grep -q '.rotation-pending' || fail "ops-check: the guards line does not name .rotation-pending"
printf '%s\n' "$ops_out" | grep '^fail guards' | grep -q '.import-pending' || fail "ops-check: the guards line does not name .import-pending"
printf '%s\n' "$ops_out" | grep '^fail guards' | grep -q '.deploy-in-progress' || fail "ops-check: the guards line does not name .deploy-in-progress"
printf '%s\n' "$ops_out" | grep '^fail deploy-hold' | grep -q "$(commit_for 9)" || fail "ops-check: the deploy-hold line does not name the held commit"
if printf '%s\n' "$ops_out" | grep '^fail guards' | grep -q deploy-hold; then
  fail "ops-check: the hold must be its own check, not part of guards"
fi
rm -f "$APP_DIR/.rotation-pending" "$APP_DIR/.import-pending" "$APP_DIR/.deploy-in-progress" "$APP_DIR/flags/deploy-hold"
mv "$APP_DIR/backup/recipients.txt" "$smoke_root/recipients.aside"
ops_check
expect_eq "ops-check without recipients: backup-identity" fail "$(ops_line backup-identity)"
mv "$smoke_root/recipients.aside" "$APP_DIR/backup/recipients.txt"
kill "$tls_pid" 2>/dev/null || true
ops_check
expect_eq "ops-check without a certificate: certificate" fail "$(ops_line certificate)"
run_backup daily
expect_eq "backup after the ops-check: exit status" 0 "$backup_status"
pass ops_check_flags_stale_backup_and_guards

# --- guards that leave the trainer database consistent do not stop its backup -----------------------------------------------
# A pending rotation and an Umami recovery can last days while the site keeps taking writes; a restore in progress and an
# interrupted deployment still refuse, because the databases are then in the middle of a change.
trainer_before_guard="$(latest_dump trainer daily)"
umami_before_guard="$(latest_dump umami daily)"
touch "$APP_DIR/.rotation-pending"
run_backup daily
expect_eq "daily backup over .rotation-pending: exit status" 0 "$backup_status"
[ "$(latest_dump trainer daily)" != "$trainer_before_guard" ] || fail "daily backup over .rotation-pending: no trainer dump was written"
[ "$(latest_dump umami daily)" != "$umami_before_guard" ] || fail "daily backup over .rotation-pending: no umami dump was written"
rm -f "$APP_DIR/.rotation-pending"
trainer_before_guard="$(latest_dump trainer daily)"
umami_before_guard="$(latest_dump umami daily)"
printf 'UMAMI_DUMP=none\n' >"$APP_DIR/.umami-recovery"
run_backup daily
expect_eq "daily backup over .umami-recovery: exit status" 0 "$backup_status"
[ "$(latest_dump trainer daily)" != "$trainer_before_guard" ] || fail "daily backup over .umami-recovery: no trainer dump was written"
expect_eq "daily backup over .umami-recovery: the umami dump is skipped" "$umami_before_guard" "$(latest_dump umami daily)"
grep -q "skipping the umami dump" "$backup_err" || fail "daily backup over .umami-recovery: the log does not say the umami dump was skipped"
[ -e "$(latest_dump trainer daily).uploaded" ] || fail "daily backup over .umami-recovery: the trainer dump was not uploaded"
rm -f "$APP_DIR/.umami-recovery"
for guard in flags/maintenance .deploy-in-progress; do
  touch "$APP_DIR/$guard"
  run_backup daily
  expect_eq "daily backup over $guard: exit status" 3 "$backup_status"
  rm -f "$APP_DIR/$guard"
done
run_backup daily
expect_eq "daily backup after the guards are gone: exit status" 0 "$backup_status"
pass backup_proceeds_over_rotation_and_umami_recovery_guards

# --- content delivery ----------------------------------------------------------------------------------------------------
# receive-content.sh through the staging protocol of the deploy-content workflow: the archive on stdin, the bundle in the
# run's staging directory. content_hook edits the tree before it is packed; receive_env adds environment variables (the
# import proxy) for one delivery.
content_status=0
content_hook=""
receive_env=()
title_text=""
proxy_log="$smoke_root/import-proxy.log"
proxy_url="http://127.0.0.1:8082/internal/content/import"
releases_dir="$APP_DIR/content/releases"

retitle_to() {
  sed -i "s|What does the code print when lambdas capture a for loop variable?|$title_text|" "$1/dotnet/csharp/closure-for-loop.yaml"
}

schema_two() {
  printf '2\n' >"$1/schema-version"
}

drop_stacks_file() {
  rm "$1/stacks.yaml"
}

current_target() {
  readlink "$APP_DIR/content/current" || true
}

release_names() {
  find "$releases_dir" -mindepth 1 -maxdepth 1 -type d -printf '%f
' | grep -E '^[0-9a-f]{40}$' || true
}

deliver_archive() { # <run> <commit number> <archive>
  local run="$1" commit stage
  commit="$(commit_for "$2")"
  stage="$APP_DIR/staging/$run"
  mkdir -m 700 "$stage"
  mkdir "$stage/bundle"
  cp -a "$deploy_src/remote" "$stage/bundle/remote"
  last_log="$logs/$run.log"
  content_status=0
  env "${receive_env[@]}" bash "$stage/bundle/remote/receive-content.sh" "$stage" "$commit" <"$3" >"$last_log" 2>&1 || content_status=$?
  receive_env=()
}

deliver_content() { # <run> <commit number>
  local run="$1" tree="$smoke_root/content-src/$1"
  rm -rf "$tree"
  mkdir -p "$tree/content" "$smoke_root/uploads"
  cp -a "$content_src/." "$tree/content/"
  if [ -n "$content_hook" ]; then
    "$content_hook" "$tree/content"
  fi
  content_hook=""
  tar -czf "$smoke_root/uploads/$run.tgz" -C "$tree" content
  deliver_archive "$run" "$2" "$smoke_root/uploads/$run.tgz"
}

# The proxy forwards import calls to the real application and drops the client connection of the calls it is told to
# ("first" or "all") once the import holds its advisory lock.
start_import_proxy() { # first|all
  local i
  : >"$proxy_log"
  python3 "$deploy_src/tests/import-proxy.py" --port 8082 --upstream 127.0.0.1:8081 --lib "$deploy_src/remote/lib.sh" \
    --log "$proxy_log" --drop "$1" >"$logs/import-proxy.out" 2>&1 &
  proxy_pid=$!
  for i in $(seq 1 100); do
    if (exec 9<>/dev/tcp/127.0.0.1/8082) 2>/dev/null; then
      return 0
    fi
    sleep 0.1
  done
  fail "the import proxy did not start: $(cat "$logs/import-proxy.out")"
}

stop_import_proxy() {
  kill "$proxy_pid" 2>/dev/null || true
  wait "$proxy_pid" 2>/dev/null || true
  proxy_pid=""
}

page_has() {
  case "$(curl -fsS "$question_url")" in
    *"$1"*) return 0 ;;
    *) return 1 ;;
  esac
}

# A started import runs to its end on the server even though its client is gone.
wait_for_title() {
  local i
  for i in $(seq 1 60); do
    if page_has "$1"; then
      return 0
    fi
    sleep 1
  done
  fail "the title '$1' was never served"
}

before_target="$(current_target)"
before_hash="$(content_hash)"
content_hook=schema_two
deliver_content content-schema 401
expect_eq "schema out of range: exit status" 5 "$content_status"
expect_eq "schema out of range: current" "$before_target" "$(current_target)"
expect_eq "schema out of range: active content hash" "$before_hash" "$(content_hash)"
[ ! -e "$APP_DIR/.import-pending" ] || fail "schema out of range: the import guard was written"
grep -q "deploy-app will deliver this content" "$last_log" || fail "schema out of range: the log does not name the follow-up"
pass content_out_of_schema_range_is_refused_before_switch

title_text="First delivered release"
content_hook=retitle_to
deliver_content content-good 402
expect_eq "first delivery: exit status" 0 "$content_status"
expect_eq "first delivery: current" "releases/$(commit_for 402)" "$(current_target)"
page_has "First delivered release" || fail "first delivery: the new title is not served"
[ ! -e "$APP_DIR/.import-pending" ] || fail "first delivery: the import guard was left behind"
good_hash="$(content_hash)"
content_hook=drop_stacks_file
deliver_content content-rejected 403
expect_eq "rejected import: exit status" 1 "$content_status"
expect_eq "rejected import: current is back on the previous release" "releases/$(commit_for 402)" "$(current_target)"
expect_eq "rejected import: active content hash" "$good_hash" "$(content_hash)"
[ ! -e "$APP_DIR/.import-pending" ] || fail "rejected import: the import guard was left behind"
grep -q "the import was rejected" "$last_log" || fail "rejected import: the log does not print the issues"
[ ! -e "$releases_dir/$(commit_for 403)" ] || fail "rejected import: the rejected release was kept"
[ -d "$releases_dir/$(commit_for 402)" ] || fail "rejected import: the previous release is gone"
page_has "First delivered release" || fail "rejected import: the previous release is not served"
pass rejected_import_switches_back

rm -f "$APP_DIR/content/current"
content_hook=drop_stacks_file
deliver_content content-rejected-first 404
expect_eq "rejected first delivery: exit status" 1 "$content_status"
[ ! -e "$APP_DIR/content/current" ] && [ ! -L "$APP_DIR/content/current" ] || fail "rejected first delivery: current was left behind"
expect_eq "rejected first delivery: active content hash" "$good_hash" "$(content_hash)"
[ ! -e "$APP_DIR/.import-pending" ] || fail "rejected first delivery: the import guard was left behind"
[ ! -e "$releases_dir/$(commit_for 404)" ] || fail "rejected first delivery: the rejected release was kept"
pass rejected_first_delivery_returns_to_fresh_state

# The first call imports and its response is lost; the second call waits on the importer's advisory lock behind it and
# answers definitively.
start_import_proxy first
receive_env=(DIT_TEST_HOOKS=1 "DIT_TEST_IMPORT_URL=$proxy_url")
title_text="Release with a lost response"
content_hook=retitle_to
deliver_content content-lost-response 405
stop_import_proxy
expect_eq "lost response: exit status" 0 "$content_status"
expect_eq "lost response: current" "releases/$(commit_for 405)" "$(current_target)"
page_has "Release with a lost response" || fail "lost response: the content is not imported"
[ ! -e "$APP_DIR/.import-pending" ] || fail "lost response: the import guard was left behind"
expect_eq "lost response: proxy calls" 2 "$(wc -l <"$proxy_log" | tr -d ' ')"
grep -q '^1 dropped held$' "$proxy_log" || fail "lost response: the first call was not dropped while the importer held its lock: $(cat "$proxy_log")"
grep -q '^2 forwarded$' "$proxy_log" || fail "lost response: the second call was not forwarded: $(cat "$proxy_log")"
expect_eq "lost response: calls without an answer" 1 "$(grep -c 'no definitive answer' "$last_log" | tr -d ' ')"
pass lost_response_is_retried_and_decided_by_second_call

# Both calls lose their answer: the script gives up with exit 6 and leaves current and the guard alone. The next delivery
# settles the guard first.
start_import_proxy all
receive_env=(DIT_TEST_HOOKS=1 "DIT_TEST_IMPORT_URL=$proxy_url")
title_text="Release with an unresolved import"
content_hook=retitle_to
deliver_content content-unresolved 406
stop_import_proxy
expect_eq "unresolved import: exit status" 6 "$content_status"
[ -f "$APP_DIR/.import-pending" ] || fail "unresolved import: the guard was removed"
expect_eq "unresolved import: guard names the release" "$(commit_for 406)" "$(head -n 1 "$APP_DIR/.import-pending")"
expect_eq "unresolved import: current stays on the release being imported" "releases/$(commit_for 406)" "$(current_target)"
expect_eq "unresolved import: proxy calls" 2 "$(wc -l <"$proxy_log" | tr -d ' ')"
[ "$(grep -Ec '^[12] dropped (held|completed)$' "$proxy_log")" = "2" ] || fail "unresolved import: a call was not dropped in flight: $(cat "$proxy_log")"
grep -q '^1 dropped held$' "$proxy_log" || fail "unresolved import: the first call was not dropped while the importer held its lock: $(cat "$proxy_log")"
wait_for_title "Release with an unresolved import"
expect_eq "unresolved import: current while the import ran" "releases/$(commit_for 406)" "$(current_target)"
title_text="Release after the settlement"
content_hook=retitle_to
deliver_content content-settled 407
expect_eq "settled delivery: exit status" 0 "$content_status"
grep -q "settling the unresolved import of $(commit_for 406)" "$last_log" || fail "settled delivery: the log does not show the settlement"
grep -q "the earlier delivery of $(commit_for 406) is in the database" "$last_log" || fail "settled delivery: the earlier delivery was not settled by a definitive answer"
[ ! -e "$APP_DIR/.import-pending" ] || fail "settled delivery: the import guard was left behind"
expect_eq "settled delivery: current" "releases/$(commit_for 407)" "$(current_target)"
page_has "Release after the settlement" || fail "settled delivery: the new content is not served"
pass unresolved_import_blocks_switch_until_settled

title_text="Immutable release"
content_hook=retitle_to
deliver_content content-immutable 408
expect_eq "immutable release: first upload" 0 "$content_status"
manifest_inode="$(stat -c %i "$releases_dir/$(commit_for 408)/.manifest")"
title_text="Same sha, other content"
content_hook=retitle_to
deliver_content content-immutable-changed 408
expect_eq "same sha, different content: exit status" 1 "$content_status"
grep -q "releases are immutable" "$last_log" || fail "same sha, different content: the log does not say why"
expect_eq "same sha, different content: current" "releases/$(commit_for 408)" "$(current_target)"
grep -q "Immutable release" "$releases_dir/$(commit_for 408)/dotnet/csharp/closure-for-loop.yaml" || fail "same sha, different content: the stored release changed"
pass same_sha_with_different_content_is_refused

title_text="Immutable release"
content_hook=retitle_to
deliver_content content-immutable-again 408
expect_eq "same sha, same content: exit status" 0 "$content_status"
grep -q "already exists with the same content" "$last_log" || fail "same sha, same content: the release was not reused"
expect_eq "same sha, same content: the release was not rewritten" "$manifest_inode" "$(stat -c %i "$releases_dir/$(commit_for 408)/.manifest")"
expect_eq "same sha, same content: current" "releases/$(commit_for 408)" "$(current_target)"
[ -z "$(find "$releases_dir" -name '*.tmp')" ] || fail "same sha, same content: a temporary release was left behind"
pass same_sha_reupload_is_reused_while_current

# Entries that could write outside the release or serve something else are refused before anything is extracted.
unsafe="$smoke_root/unsafe"
rm -rf "$unsafe"
mkdir -p "$unsafe/base/content" "$unsafe/uploads"
cp -a "$content_src/." "$unsafe/base/content/"
unsafe_case() { # <name> <archive creator>
  local archive="$unsafe/uploads/$1.tgz" run="unsafe-$1" target_before
  target_before="$(current_target)"
  "$2" "$archive"
  deliver_archive "$run" 409 "$archive"
  expect_eq "unsafe archive $1: exit status" 2 "$content_status"
  expect_eq "unsafe archive $1: current" "$target_before" "$(current_target)"
  [ ! -e "$releases_dir/$(commit_for 409).tmp" ] || fail "unsafe archive $1: a temporary release was created"
  [ ! -e "$APP_DIR/staging/escape" ] && [ ! -e /dit-smoke-absolute ] || fail "unsafe archive $1: a file escaped the release"
  [ ! -e "$APP_DIR/staging/$run/content" ] || fail "unsafe archive $1: something was extracted"
}
make_parent_paths() {
  printf 'x\n' >"$unsafe/x"
  tar -czf "$1" -C "$unsafe" --transform='s|^x$|content/../escape|' x
}
make_absolute_path() {
  printf 'x\n' >"$unsafe/x"
  tar -czPf "$1" -C "$unsafe" --transform='s|^x$|/dit-smoke-absolute|' x
}
make_symlink() {
  ln -sfn /etc/passwd "$unsafe/base/content/link"
  tar -czf "$1" -C "$unsafe/base" content
  rm -f "$unsafe/base/content/link"
}
make_hardlink() {
  ln -f "$unsafe/base/content/schema-version" "$unsafe/base/content/schema-version-copy"
  tar -czf "$1" -C "$unsafe/base" content
  rm -f "$unsafe/base/content/schema-version-copy"
}
make_outside_content() {
  mkdir -p "$unsafe/other"
  printf 'x\n' >"$unsafe/other/file"
  tar -czf "$1" -C "$unsafe" other
}
make_fifo() {
  mkfifo "$unsafe/base/content/pipe"
  tar -czf "$1" -C "$unsafe/base" content
  rm -f "$unsafe/base/content/pipe"
}
make_manifest_name() {
  printf 'x\n' >"$unsafe/base/content/.manifest"
  tar -czf "$1" -C "$unsafe/base" content
  rm -f "$unsafe/base/content/.manifest"
}
# 300 MB of zeros pack into a few hundred kilobytes: far below the compressed cap, far above the unpacked one.
make_bomb() {
  head -c 300000000 /dev/zero >"$unsafe/base/content/big.bin"
  tar -czf "$1" -C "$unsafe/base" content
  rm -f "$unsafe/base/content/big.bin"
}
# 20001 empty files: tiny to pack, and over the entry cap.
make_many_entries() {
  mkdir "$unsafe/base/content/many"
  (cd "$unsafe/base/content/many" && seq 1 20001 | xargs touch)
  tar -czf "$1" -C "$unsafe/base" content
  rm -rf "$unsafe/base/content/many"
}
unsafe_case parent make_parent_paths
unsafe_case absolute make_absolute_path
unsafe_case symlink make_symlink
unsafe_case hardlink make_hardlink
unsafe_case outside make_outside_content
unsafe_case fifo make_fifo
unsafe_case manifest make_manifest_name
unsafe_case bomb make_bomb
unsafe_case many make_many_entries
pass tar_with_parent_paths_is_refused

for n in 421 422 423 424 425 426 427; do
  deliver_content "content-keep-$n" "$n"
  expect_eq "keep five: delivery $n" 0 "$content_status"
done
expect_eq "keep five: releases left" 5 "$(release_names | wc -l | tr -d ' ')"
expect_eq "keep five: the newest five" "$(for n in 423 424 425 426 427; do commit_for "$n"; echo; done | sort)" "$(release_names | sort)"
expect_eq "keep five: current" "releases/$(commit_for 427)" "$(current_target)"
[ -d "$releases_dir/$(commit_for 427)" ] || fail "keep five: the release current points to is gone"
[ -d "$releases_dir/release-b" ] || fail "keep five: a directory that is not a release was touched"
pass keeps_five_releases_and_current

# A release whose files no longer match its manifest is never reused, even when its stored .manifest is intact; leftovers
# of an interrupted build or prune are swept at the start of the next run.
truncated="$releases_dir/$(commit_for 427)"
truncated_target="$(current_target)"
rm "$truncated/dotnet/async/to-be-retired.yaml"
deliver_content content-truncated 427
expect_eq "truncated release: exit status" 1 "$content_status"
grep -q "its files or manifest differ" "$last_log" || fail "truncated release: the log does not say why"
expect_eq "truncated release: current" "$truncated_target" "$(current_target)"
mkdir -p "$releases_dir/$(commit_for 428).tmp" "$releases_dir/$(commit_for 429).del.123"
cp -a "$content_src/dotnet/async/to-be-retired.yaml" "$truncated/dotnet/async/to-be-retired.yaml"
deliver_content content-restored 427
expect_eq "restored release: exit status" 0 "$content_status"
[ -z "$(find "$releases_dir" -name '*.tmp' -o -name '*.del.*')" ] || fail "restored release: leftovers of an interrupted run were not swept"
pass truncated_release_is_not_reused

# The earlier delivery was rejected but its answer was lost: the next delivery settles it by switching back to the release
# recorded in the guard.
title_text="Settlement base release"
content_hook=retitle_to
deliver_content content-settle-base 431
expect_eq "settlement base: exit status" 0 "$content_status"
start_import_proxy all
receive_env=(DIT_TEST_HOOKS=1 "DIT_TEST_IMPORT_URL=$proxy_url")
content_hook=drop_stacks_file
deliver_content content-settle-bad 432
stop_import_proxy
expect_eq "unanswered rejection: exit status" 6 "$content_status"
expect_eq "unanswered rejection: guard release" "$(commit_for 432)" "$(sed -n 1p "$APP_DIR/.import-pending")"
expect_eq "unanswered rejection: guard previous target" "releases/$(commit_for 431)" "$(sed -n 2p "$APP_DIR/.import-pending")"
expect_eq "unanswered rejection: current" "releases/$(commit_for 432)" "$(current_target)"
deliver_content content-settle-next 433
expect_eq "settled rejection: exit status" 0 "$content_status"
grep -q "the earlier delivery of $(commit_for 432) was rejected; current is back on releases/$(commit_for 431)" "$last_log" || fail "settled rejection: the log does not show the switch back"
[ ! -e "$APP_DIR/.import-pending" ] || fail "settled rejection: the guard was left behind"
expect_eq "settled rejection: current" "releases/$(commit_for 433)" "$(current_target)"
[ ! -e "$releases_dir/$(commit_for 432)" ] || fail "settled rejection: the rejected release was kept"
[ -d "$releases_dir/$(commit_for 431)" ] || fail "settled rejection: the previous release is gone"
pass settled_rejection_switches_back

# A delivery that never learned its outcome left current on a release the import rejects (the application would mark its
# startup import Failed and never become ready). deploy-app settles .import-pending before it restarts anything, by switching
# current back to the previous release recorded in it.
expect_eq "pending before deploy: current" "releases/$(commit_for 433)" "$(current_target)"
start_import_proxy all
receive_env=(DIT_TEST_HOOKS=1 "DIT_TEST_IMPORT_URL=$proxy_url")
content_hook=drop_stacks_file
deliver_content content-pending-bad 436
stop_import_proxy
expect_eq "pending before deploy: delivery exit status" 6 "$content_status"
expect_eq "pending before deploy: current is on the rejected release" "releases/$(commit_for 436)" "$(current_target)"
[ -f "$APP_DIR/.import-pending" ] || fail "pending before deploy: no guard was left"
deploy "pending-deploy-$RANDOM" "$(commit_for 470)" "$(image_for 470)"
expect_eq "deploy over .import-pending: exit status" 0 "$deploy_status"
grep -q "the delivery of $(commit_for 436) was not imported; current is back on releases/$(commit_for 433)" "$last_log" || fail "deploy over .import-pending: the log does not show the switch back"
[ ! -e "$APP_DIR/.import-pending" ] || fail "deploy over .import-pending: the guard was left behind"
expect_eq "deploy over .import-pending: current" "releases/$(commit_for 433)" "$(current_target)"
wait_ready
pass deploy_settles_import_pending_before_starting_the_app

# The same left-over for a restore of the trainer database: the app restarted by the restore would read the rejected release,
# never become ready and leave the maintenance flag in place. The restore settles .import-pending before it writes the flag.
start_import_proxy all
receive_env=(DIT_TEST_HOOKS=1 "DIT_TEST_IMPORT_URL=$proxy_url")
content_hook=drop_stacks_file
deliver_content content-pending-restore 437
stop_import_proxy
expect_eq "pending before restore: delivery exit status" 6 "$content_status"
expect_eq "pending before restore: current is on the rejected release" "releases/$(commit_for 437)" "$(current_target)"
run_restore "$identity_file" --replace "$(latest_dump trainer daily)" trainer
expect_eq "restore of trainer over .import-pending: exit status" 0 "$restore_status"
grep -q "the delivery of $(commit_for 437) was not imported; current is back on releases/$(commit_for 433)" "$restore_err" || fail "restore over .import-pending: the log does not show the switch back"
[ ! -e "$APP_DIR/flags/maintenance" ] || fail "restore over .import-pending: the maintenance flag was left"
[ ! -e "$APP_DIR/.import-pending" ] || fail "restore over .import-pending: the guard was left behind"
expect_eq "restore over .import-pending: current" "releases/$(commit_for 433)" "$(current_target)"
wait_ready
for kept in $(database_names); do
  case "$kept" in *_old_*) run_restore "$identity_file" --drop-old "$kept" ;; esac
done
pass restore_settles_import_pending_before_restarting_the_app

# The answer of a delivery was lost although its import committed: deploy-app must keep the new release (the active content hash
# differs from the one recorded in the guard) and only clear the guard; reverting would import the previous release over it.
title_text="Committed but unanswered"
start_import_proxy all
receive_env=(DIT_TEST_HOOKS=1 "DIT_TEST_IMPORT_URL=$proxy_url")
content_hook=retitle_to
hash_before_commit="$(content_hash)"
deliver_content content-committed 438
stop_import_proxy
expect_eq "committed import: delivery exit status" 6 "$content_status"
expect_eq "committed import: the guard records the hash before" "$hash_before_commit" "$(sed -n 3p "$APP_DIR/.import-pending")"
wait_for_title "$title_text"
committed_hash="$(content_hash)"
[ "$committed_hash" != "$hash_before_commit" ] || fail "committed import: the active hash did not change"
deploy "committed-deploy-$RANDOM" "$(commit_for 471)" "$(image_for 471)"
expect_eq "deploy over a committed pending import: exit status" 0 "$deploy_status"
grep -q "was imported although its answer was lost" "$last_log" || fail "deploy over a committed pending import: the log does not say it was imported"
[ ! -e "$APP_DIR/.import-pending" ] || fail "deploy over a committed pending import: the guard was left behind"
expect_eq "deploy over a committed pending import: current stays on the new release" "releases/$(commit_for 438)" "$(current_target)"
expect_eq "deploy over a committed pending import: content hash" "$committed_hash" "$(content_hash)"
wait_ready
page_has "$title_text" || fail "deploy over a committed pending import: the new title is not served"
pass deploy_keeps_a_release_whose_import_committed

# The previous release recorded in the guard has vanished and the pending one was not imported: nothing may be deleted, the
# run refuses (exit 2) and the guard stays, until the directory is back.
start_import_proxy all
receive_env=(DIT_TEST_HOOKS=1 "DIT_TEST_IMPORT_URL=$proxy_url")
content_hook=drop_stacks_file
deliver_content content-missing-previous 439
stop_import_proxy
expect_eq "missing previous release: delivery exit status" 6 "$content_status"
expect_eq "missing previous release: the guard names the previous target" "releases/$(commit_for 438)" "$(sed -n 2p "$APP_DIR/.import-pending")"
mv "$APP_DIR/content/releases/$(commit_for 438)" "$APP_DIR/content/releases/$(commit_for 438).aside"
deploy "missing-previous-$RANDOM" "$(commit_for 472)" "$(image_for 472)"
mv "$APP_DIR/content/releases/$(commit_for 438).aside" "$APP_DIR/content/releases/$(commit_for 438)"
expect_eq "missing previous release: deploy exit status" 2 "$deploy_status"
grep -q "no longer exists" "$last_log" || fail "missing previous release: the log does not name the problem"
grep -q "Content import" "$last_log" || fail "missing previous release: the log does not name the docs section"
[ -f "$APP_DIR/.import-pending" ] || fail "missing previous release: the guard was removed"
expect_eq "missing previous release: current is not removed" "releases/$(commit_for 439)" "$(current_target)"
deploy "missing-previous-fixed-$RANDOM" "$(commit_for 473)" "$(image_for 473)"
expect_eq "deploy once the previous release is back: exit status" 0 "$deploy_status"
expect_eq "deploy once the previous release is back: current" "releases/$(commit_for 438)" "$(current_target)"
[ ! -e "$APP_DIR/.import-pending" ] || fail "deploy once the previous release is back: the guard was left behind"
wait_ready
pass missing_previous_release_is_refused_without_deleting_current

# A run killed at any point after the guard was written is settled by the next delivery.
kill_number=440
for point in after-pending after-switch after-import; do
  kill_number=$((kill_number + 2))
  before_target="$(current_target)"
  receive_env=(DIT_TEST_HOOKS=1 "DIT_TEST_KILL_AT=$point")
  deliver_content "content-kill-$point" "$kill_number"
  expect_eq "killed at $point: exit status" 137 "$content_status"
  [ -f "$APP_DIR/.import-pending" ] || fail "killed at $point: no guard was left"
  if [ "$point" = "after-pending" ]; then
    expect_eq "killed at $point: current" "$before_target" "$(current_target)"
  else
    expect_eq "killed at $point: current" "releases/$(commit_for "$kill_number")" "$(current_target)"
  fi
  deliver_content "content-after-$point" "$((kill_number + 1))"
  expect_eq "delivery after a kill at $point: exit status" 0 "$content_status"
  grep -q "settling the unresolved import of $(commit_for "$kill_number")" "$last_log" || fail "delivery after a kill at $point: the guard was not settled"
  [ ! -e "$APP_DIR/.import-pending" ] || fail "delivery after a kill at $point: the guard was left behind"
  expect_eq "delivery after a kill at $point: current" "releases/$(commit_for "$((kill_number + 1))")" "$(current_target)"
done
pass killed_delivery_is_settled_by_the_next_delivery

# The test hooks need the gate: without DIT_TEST_HOOKS=1 neither the import URL nor the kill point is honoured.
receive_env=("DIT_TEST_IMPORT_URL=http://127.0.0.1:1/internal/content/import" "DIT_TEST_KILL_AT=after-switch")
deliver_content content-no-gate 451
expect_eq "hooks without the gate: exit status" 0 "$content_status"
pass import_url_hook_is_ignored_without_the_gate

# 401, 403 and 404 say that no import was started, so the switch is undone instead of waiting for an answer that never comes.
before_target="$(current_target)"
receive_env=(DIT_TEST_HOOKS=1 "DIT_TEST_IMPORT_URL=http://127.0.0.1:8081/internal/content/missing")
deliver_content content-refused 452
expect_eq "refused import: exit status" 1 "$content_status"
grep -q "refused the call (HTTP 404)" "$last_log" || fail "refused import: the log does not name the status"
expect_eq "refused import: current" "$before_target" "$(current_target)"
[ ! -e "$APP_DIR/.import-pending" ] || fail "refused import: the guard was left behind"
pass refused_import_switches_back

# Without the token nothing may be switched: the delivery stops before the guard is written.
before_target="$(current_target)"
mv "$APP_DIR/.env" "$smoke_root/env.aside"
deliver_content content-no-token 453
mv "$smoke_root/env.aside" "$APP_DIR/.env"
expect_eq "missing token: exit status" 2 "$content_status"
grep -q "CONTENT_IMPORT_TOKEN" "$last_log" || fail "missing token: the log does not name the variable"
expect_eq "missing token: current" "$before_target" "$(current_target)"
[ ! -e "$APP_DIR/.import-pending" ] || fail "missing token: a guard was written"
pass missing_token_is_refused_before_switch

# A guard records the previous target, so a target that is not a release path is refused before anything is switched.
before_target="$(current_target)"
ln -sfn /etc "$APP_DIR/content/current.new"
mv -T "$APP_DIR/content/current.new" "$APP_DIR/content/current"
deliver_content content-odd-current 454
expect_eq "current outside releases: exit status" 2 "$content_status"
expect_eq "current outside releases: current" /etc "$(current_target)"
[ ! -e "$APP_DIR/.import-pending" ] || fail "current outside releases: a guard was written"
ln -sfn "$before_target" "$APP_DIR/content/current.new"
mv -T "$APP_DIR/content/current.new" "$APP_DIR/content/current"
pass current_outside_releases_is_refused

# Waiting for the deployment lock is not a failed delivery: nothing changed and the exit status says so.
before_target="$(current_target)"
(
  exec 8>"$APP_DIR/.deploy.lock"
  flock 8
  sleep 6
) &
lock_holder=$!
sleep 1
receive_env=(DIT_LOCK_TIMEOUT=1)
deliver_content content-lock 455
wait "$lock_holder"
expect_eq "lock timeout: exit status" 4 "$content_status"
expect_eq "lock timeout: current" "$before_target" "$(current_target)"
pass lock_timeout_is_reported_distinctly

# --- Umami is never rolled back automatically -------------------------------------------------------------------------
umami_pinned="$(awk '/^  umami:/{f=1} f&&/^    image:/{sub(/^    image:[[:space:]]*/,""); print; exit}' "$deploy_src/compose/compose.yaml")"
docker pull -q "$umami_pinned" >/dev/null
docker tag "$umami_pinned" "$registry/umami:next"
docker push -q "$registry/umami:next" >/dev/null
# A run that repairs an interrupted one first must still apply its own Umami decision: kill at after-marker, then a
# deployment that changes the Umami image and fails readiness has to leave the guard behind.
upload run-umami-killed "$(commit_for 310)" "$(image_for 310)"
kill_at=after-marker
run_deploy run-umami-killed "$(commit_for 310)" "$(image_for 310)"
expect_eq "interrupted run before the Umami change: killed" 137 "$deploy_status"
bundle_hook=other_umami_image
env_hook=bad_public_url
deploy run-umami-after-kill "$(commit_for 311)" "$(image_for 311)"
expect_eq "Umami change after an interrupted run: exit status" 1 "$deploy_status"
grep -q "a previous deployment was interrupted" "$last_log" || fail "Umami change after an interrupted run: no repair happened first"
[ -e "$APP_DIR/.umami-recovery" ] || fail "Umami change after an interrupted run: the recovery guard was not written"
expect_eq "Umami change after an interrupted run: running services" "app postgres " "$(running_services)"
assert_rolled_back_to_last_good "Umami change after an interrupted run"
rm -f "$APP_DIR/.umami-recovery"
deploy run-umami-restore "$(commit_for 312)" "$(image_for 312)"
expect_eq "restoring the stack after the guard was cleared: exit status" 0 "$deploy_status"
wait_ready
pass interrupted_run_then_umami_change_keeps_the_guard

# A kill after publish_last_good, before the marker is removed, in a deployment that changed the Umami image: the deployment
# succeeded, so the repair keeps it. The marker says "Umami changed and started", but last-good.env already names its bundle.
bundle_hook=other_umami_image
upload run-umami-published "$(commit_for 313)" "$(image_for 313)"
kill_at=after-last-good
run_deploy run-umami-published "$(commit_for 313)" "$(image_for 313)"
expect_eq "published Umami change: the run was killed" 137 "$deploy_status"
[ -e "$APP_DIR/.deploy-in-progress" ] || fail "published Umami change: no marker was left"
expect_eq "published Umami change: marker UMAMI_CHANGED" 1 "$(dit_env_get "$APP_DIR/.deploy-in-progress" UMAMI_CHANGED)"
assert_record_consistent "published Umami change" "$APP_DIR/last-good.env"
expect_eq "published Umami change: last-good.env names the killed run" "$(commit_for 313)" "$(dit_env_get "$APP_DIR/last-good.env" DIT_COMMIT)"
deploy run-umami-published-next "$(commit_for 314)" "$(image_for 314)"
expect_eq "repair of a published Umami change: exit status" 0 "$deploy_status"
grep -q "a previous deployment was interrupted" "$last_log" || fail "repair of a published Umami change: no repair happened first"
grep -q "the interrupted deployment was published before it ended" "$last_log" || fail "repair of a published Umami change: the log does not say the deployment was kept"
[ ! -e "$APP_DIR/.umami-recovery" ] || fail "repair of a published Umami change: a recovery guard was written for a successful upgrade"
wait_ready
expect_eq "repair of a published Umami change: running services" "app postgres umami " "$(running_services)"
pass killed_after_publish_keeps_a_successful_umami_upgrade

bundle_hook=other_umami_image
env_hook=bad_public_url
deploy run-umami "$(commit_for 300)" "$(image_for 300)"
expect_eq "umami image change: exit status" 1 "$deploy_status"
[ -e "$APP_DIR/.umami-recovery" ] || fail "umami image change: the recovery guard was not written"
grep -q "restore.sh --replace" "$last_log" || fail "umami image change: the log does not give the recovery command"
grep -q "Umami upgrade" "$last_log" || fail "umami image change: the log does not name the recovery section"
expect_eq "umami image change: running services" "app postgres " "$(running_services)"
assert_rolled_back_to_last_good "umami image change"
before="$(snapshot)"
deploy run-after-umami "$(commit_for 301)" "$(image_for 301)"
expect_eq "deploy while .umami-recovery exists: exit status" 3 "$deploy_status"
expect_eq "deploy while .umami-recovery exists: state unchanged" "$before" "$(snapshot)"
expect_eq "deploy while .umami-recovery exists: running services" "app postgres " "$(running_services)"
# The second half: "restore.sh --replace <the dump named in .umami-recovery> umami" proceeds over the guard, puts the database
# back as it was before the upgrade, and a further deployment succeeds.
recovery_dump="$(sed -n 's/^UMAMI_DUMP=//p' "$APP_DIR/.umami-recovery")"
case "$recovery_dump" in
  "$APP_DIR"/backups/umami-*-predeploy.dump.age) ;;
  *) fail "umami recovery: the guard names '$recovery_dump', not a pre-deploy umami dump" ;;
esac
[ -f "$recovery_dump" ] || fail "umami recovery: the named dump does not exist"
# What the new image did to the database after the dump was taken.
dit_psql umami -c 'create table smoke_changed_by_new_image (id int)' </dev/null >/dev/null
# The double failure: the rollback wrote the guard and then could not restore the records, so the marker exists, current-bundle
# is still on the failed bundle and the new Umami image is the one that is running.
good_bundle="$(dit_env_get "$APP_DIR/last-good.env" DIT_BUNDLE_SHA)"
failed_bundle=""
for candidate in "$APP_DIR"/bundles/*/; do
  candidate="$(basename "$candidate")"
  if [ "$(cat "$APP_DIR/bundles/$candidate/.smoke-id")" = "$(commit_for 300)" ]; then
    failed_bundle="$candidate"
  fi
done
[ -n "$failed_bundle" ] && [ "$failed_bundle" != "$good_bundle" ] || fail "umami recovery: the failed bundle is not on disk"
printf "DIT_BUNDLE_SHA='%s'\nUMAMI_DUMP='%s'\nUMAMI_CHANGED='1'\nUMAMI_STARTED='1'\n" "$failed_bundle" "$recovery_dump" >"$APP_DIR/.deploy-in-progress"
dit_switch_bundle "$failed_bundle"
dit_compose up -d --wait --wait-timeout "$DIT_WAIT_TIMEOUT" umami >/dev/null 2>&1
expect_eq "umami recovery: the failed deployment's Umami image runs before the restore" "$registry/umami:next" \
  "$(docker inspect --format '{{.Config.Image}}' "$(dit_compose ps -q umami)")"
# The guard is cleared by the restore of umami only: restoring trainer must still refuse over it.
run_restore "$identity_file" --replace "$(latest_dump trainer daily)" trainer
expect_eq "umami recovery: restore of trainer over the guard" 3 "$restore_status"
run_restore "$identity_file" --replace "$recovery_dump" umami
expect_eq "umami recovery: restore of umami: exit status" 0 "$restore_status"
[ ! -e "$APP_DIR/.umami-recovery" ] || fail "umami recovery: the guard was not cleared"
[ ! -e "$APP_DIR/flags/maintenance" ] || fail "umami recovery: the maintenance flag was left"
expect_eq "umami recovery: marker UMAMI_CHANGED" 0 "$(dit_env_get "$APP_DIR/.deploy-in-progress" UMAMI_CHANGED)"
expect_eq "umami recovery: marker UMAMI_STARTED" 0 "$(dit_env_get "$APP_DIR/.deploy-in-progress" UMAMI_STARTED)"
expect_eq "umami recovery: the database is the pre-upgrade one" 0 "$(dit_psql umami -tA -c "select count(*) from information_schema.tables where table_name = 'smoke_changed_by_new_image'" </dev/null)"
expect_eq "umami recovery: current-bundle is the last good bundle again" "$good_bundle" "$(bundle_of_link)"
expect_eq "umami recovery: Umami runs the pre-upgrade image" "$umami_pinned" \
  "$(docker inspect --format '{{.Config.Image}}' "$(dit_compose ps -q umami)")"
expect_eq "umami recovery: running services" "app postgres umami " "$(running_services)"
expect_eq "umami recovery: Umami answers" 200 "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:3001/api/heartbeat)"
for kept in $(database_names); do
  case "$kept" in *_old_*) run_restore "$identity_file" --drop-old "$kept" ;; esac
done
deploy run-after-umami-restore "$(commit_for 302)" "$(image_for 302)"
expect_eq "deploy after the Umami restore: exit status" 0 "$deploy_status"
grep -q "a previous deployment was interrupted" "$last_log" || fail "deploy after the Umami restore: the leftover marker was not repaired first"
[ ! -e "$APP_DIR/.umami-recovery" ] || fail "deploy after the Umami restore: the guard came back"
wait_ready
expect_eq "deploy after the Umami restore: running services" "app postgres umami " "$(running_services)"
assert_no_secrets "the whole run" "$logs"/*.log
pass umami_image_change_is_not_rolled_back_automatically

# --- database password rotation ----------------------------------------------------------------------------------------
# The script runs the way the server runs it, from current-bundle. Every role below is rotated for real: the role's password,
# .env, last-good.env and the recreated service are all observed. The shell variables DB_PASSWORD and UMAMI_DB_PASSWORD stand
# for the GitHub secrets and move to the new value once a rotation is committed, as the workflow does.
rotate_script="$APP_DIR/current-bundle/remote/rotate-db-password.sh"
rotate_runs=0
rotate_kill=""
rotate_psql_hold=""
rotate_bash=(bash)
rotate_status=0
rotate_out=""
rotate_err=""
rotation_secrets=()

rotate() { # <role> <begin|finish|commit|pending-role> [new password for begin]; sets rotate_status, rotate_out, rotate_err
  local role="$1" mode="$2" stage
  local -a hooks=() args
  rotate_runs=$((rotate_runs + 1))
  stage="$APP_DIR/staging/rotate-$rotate_runs"
  install -d -m 700 "$stage"
  if [ -n "$rotate_kill" ]; then
    hooks=(DIT_TEST_HOOKS=1 "DIT_TEST_KILL_AT=$rotate_kill")
  fi
  if [ -n "$rotate_psql_hold" ]; then
    hooks+=(DIT_TEST_HOOKS=1 "DIT_TEST_PSQL_HOLD=$rotate_psql_hold")
  fi
  rotate_kill=""
  rotate_psql_hold=""
  if [ "$mode" = pending-role ]; then
    args=("$stage" pending-role)
  else
    args=("$stage" "$role" "$mode")
  fi
  rotate_err="$logs/rotate-$rotate_runs.log"
  rotate_status=0
  rotate_out="$(printf '%s\n' "${3:-}" | env "${hooks[@]}" "${rotate_bash[@]}" "$rotate_script" "${args[@]}" 2>"$rotate_err")" || rotate_status=$?
}

# Whether the role can log in over the network with the password: from inside the postgres container to its own address in the
# Compose subnet, the only place pg_hba.conf lets the application roles in. The password travels through the exec environment.
db_login() { # <role> <password>
  # shellcheck disable=SC2016 # the script text is for the shell inside the container
  PGPASSWORD="$2" dit_compose exec -T --user postgres -e PGPASSWORD postgres \
    sh -c 'exec psql -h "$(hostname -i)" -U "$1" -d "$1" -tAc "select 1"' _ "$1" >/dev/null 2>&1 </dev/null
}

umami_answers() {
  curl -fsS --max-time 5 http://127.0.0.1:3001/api/heartbeat >/dev/null 2>&1
}

assert_password_state() { # <label> <env name> <role> <password the server must hold> <password it must have replaced>
  local label="$1" name="$2" role="$3" current="$4" replaced="$5"
  expect_eq "$label: .env" "$current" "$(dit_env_get "$APP_DIR/.env" "$name")"
  expect_eq "$label: last-good.env" "$current" "$(dit_env_get "$APP_DIR/last-good.env" "$name")"
  db_login "$role" "$current" || fail "$label: the role does not accept the new password"
  if db_login "$role" "$replaced"; then
    fail "$label: the role still accepts the old password"
  fi
}

assert_journal() { # <label> <role> <password>
  [ -f "$APP_DIR/.rotation-pending" ] || fail "$1: .rotation-pending is missing"
  expect_eq "$1: journal mode" 600 "$(stat -c %a "$APP_DIR/.rotation-pending")"
  expect_eq "$1: journal role" "$2" "$(dit_env_get "$APP_DIR/.rotation-pending" ROTATION_ROLE)"
  expect_eq "$1: journal password" "$3" "$(dit_env_get "$APP_DIR/.rotation-pending" ROTATION_PASSWORD)"
}

assert_no_rotation_secret() { # <label> <file>...
  local label="$1" secret
  shift
  for secret in "${rotation_secrets[@]}"; do
    if grep -qF -- "$secret" "$@"; then
      fail "$label: a log contains a rotation password"
    fi
  done
}

# Process list of the runner and of the postgres container, sampled until the stop file appears. Every pass writes a marker
# line, so a sampler that silently did nothing cannot make the check below pass.
sample_processes() { # <output> <stop file> <postgres container>
  while [ ! -e "$2" ]; do
    printf -- '--sample--\n' >>"$1"
    ps -eww -o args= >>"$1" 2>/dev/null || true
    docker top "$3" -eo args >>"$1" 2>/dev/null || true
    sleep 0.1
  done
}

# A rotation that completes, observed from the process list, the files and the running stack.
old_trainer_password="$DB_PASSWORD"
new_trainer_password="$(openssl rand -hex 24)"
rotation_secrets+=("$new_trainer_password")
app_before="$(app_container)"
postgres_before="$(dit_compose ps -q postgres)"
samples="$smoke_root/rotate-process-samples.txt"
sampler_stop="$smoke_root/rotate-sampler.stop"
canary="rotate-sampler-canary-$RANDOM$RANDOM"
rm -f "$sampler_stop"
: >"$samples"
sample_processes "$samples" "$sampler_stop" "$postgres_before" &
sampler_pid=$!
bash -c 'sleep 3; :' "$canary" &
# The hook keeps the ALTER ROLE psql session open for 3 s, far longer than one sampling pass.
rotate_psql_hold=3
rotate trainer begin "$new_trainer_password"
touch "$sampler_stop"
wait "$sampler_pid"
expect_eq "rotate trainer: exit status" 0 "$rotate_status"
assert_journal "rotate trainer" trainer "$new_trainer_password"
assert_password_state "rotate trainer" DB_PASSWORD trainer "$new_trainer_password" "$old_trainer_password"
wait_ready
[ "$(app_container)" != "$app_before" ] || fail "rotate trainer: the application was not recreated"
expect_eq "rotate trainer: postgres container" "$postgres_before" "$(dit_compose ps -q postgres)"
expect_eq "rotate trainer: running services" "app postgres umami " "$(running_services)"
rotate trainer pending-role
expect_eq "rotate trainer: pending-role" trainer "$rotate_out"
rotate trainer commit
expect_eq "rotate trainer: commit exit status" 0 "$rotate_status"
[ ! -e "$APP_DIR/.rotation-pending" ] || fail "rotate trainer: the journal survived commit"
DB_PASSWORD="$new_trainer_password"

old_umami_password="$UMAMI_DB_PASSWORD"
new_umami_password="$(openssl rand -hex 24)"
rotation_secrets+=("$new_umami_password")
rotate umami begin "$new_umami_password"
expect_eq "rotate umami: exit status" 0 "$rotate_status"
assert_journal "rotate umami" umami "$new_umami_password"
assert_password_state "rotate umami" UMAMI_DB_PASSWORD umami "$new_umami_password" "$old_umami_password"
umami_answers || fail "rotate umami: Umami does not answer"
expect_eq "rotate umami: running services" "app postgres umami " "$(running_services)"
rotate umami commit
expect_eq "rotate umami: commit exit status" 0 "$rotate_status"
[ ! -e "$APP_DIR/.rotation-pending" ] || fail "rotate umami: the journal survived commit"
UMAMI_DB_PASSWORD="$new_umami_password"
pass rotate_begin_then_commit_keeps_app_ready

[ "$(grep -c -x -e '--sample--' "$samples")" -ge 5 ] || fail "process list: the sampler took fewer than five samples"
grep -qF -- "$canary" "$samples" || fail "process list: the sampler did not see a known argument, so it proves nothing"
grep -q 'rotate-db-password.sh' "$samples" || fail "process list: the sampler never saw the rotation script"
grep -q 'psql -v ON_ERROR_STOP=1' "$samples" || fail "process list: the sampler never saw the psql session that sets the password"
if grep -qF -e "$new_trainer_password" -e "$old_trainer_password" "$samples"; then
  fail "process list: a password appeared on a command line"
fi
assert_no_rotation_secret "rotation logs" "$logs"/rotate-*.log
pass rotate_new_password_never_in_process_list

# A crash right after the journal was written: nothing but the journal has changed, and everything refuses meanwhile.
old_umami_password="$UMAMI_DB_PASSWORD"
new_umami_password="$(openssl rand -hex 24)"
rotation_secrets+=("$new_umami_password")
before="$(snapshot)"
rotate_kill=after-journal
rotate umami begin "$new_umami_password"
expect_eq "crash before ALTER ROLE: exit status" 137 "$rotate_status"
assert_journal "crash before ALTER ROLE" umami "$new_umami_password"
expect_eq "crash before ALTER ROLE: state unchanged" "$before" "$(snapshot)"
db_login umami "$old_umami_password" || fail "crash before ALTER ROLE: the old password stopped working"
if db_login umami "$new_umami_password"; then
  fail "crash before ALTER ROLE: the role already has the new password"
fi
rotate umami pending-role
expect_eq "crash before ALTER ROLE: pending-role" umami "$rotate_out"

deploy "rotation-refused-$RANDOM" "$(commit_for 600)" "$(image_for 600)"
expect_eq "deploy while a rotation is pending: exit status" 3 "$deploy_status"
grep -q "Interrupted rotation" "$last_log" || fail "deploy while a rotation is pending: the log does not name the section"
expect_eq "deploy while a rotation is pending: state unchanged" "$before" "$(snapshot)"
deliver_content content-rotation-pending 610
expect_eq "content delivery while a rotation is pending: exit status" 3 "$content_status"
ops_check
expect_eq "ops-check while a rotation is pending: guards" fail "$(ops_line guards)"
printf '%s\n' "$ops_out" | grep '^fail guards' | grep -q '.rotation-pending' || fail "ops-check: the guards line does not name .rotation-pending"
pass deploy_is_refused_while_rotation_pending

journal_before="$(sha256sum "$APP_DIR/.rotation-pending")"
for other_mode in finish commit; do
  rotate trainer "$other_mode"
  expect_eq "$other_mode for the other role: exit status" 2 "$rotate_status"
  grep -q "pending rotation is for umami" "$rotate_err" || fail "$other_mode for the other role: the log does not name the pending role"
done
rotate trainer begin "$(openssl rand -hex 24)"
expect_eq "begin over a pending rotation: exit status" 3 "$rotate_status"
expect_eq "other role: journal unchanged" "$journal_before" "$(sha256sum "$APP_DIR/.rotation-pending")"
expect_eq "other role: state unchanged" "$before" "$(snapshot)"
db_login umami "$old_umami_password" || fail "other role: the old password stopped working"
db_login trainer "$DB_PASSWORD" || fail "other role: the trainer password stopped working"
pass finish_with_other_role_fails_without_change

rotate umami finish
expect_eq "finish after a crash before ALTER ROLE: exit status" 0 "$rotate_status"
assert_password_state "finish after a crash before ALTER ROLE" UMAMI_DB_PASSWORD umami "$new_umami_password" "$old_umami_password"
umami_answers || fail "finish after a crash before ALTER ROLE: Umami does not answer"
assert_journal "finish after a crash before ALTER ROLE" umami "$new_umami_password"
rotate umami commit
expect_eq "commit after a crash before ALTER ROLE: exit status" 0 "$rotate_status"
UMAMI_DB_PASSWORD="$new_umami_password"
pass crash_before_alter_role_then_finish_converges

# A crash right after ALTER ROLE, before .env changes: the password in .env no longer works, which is the case this design
# exists for. finish moves the server to the new value, and a run under xtrace shows nothing.
old_trainer_password="$DB_PASSWORD"
new_trainer_password="$(openssl rand -hex 24)"
rotation_secrets+=("$new_trainer_password")
rotate_kill=after-alter-role
rotate trainer begin "$new_trainer_password"
expect_eq "crash after ALTER ROLE: exit status" 137 "$rotate_status"
assert_journal "crash after ALTER ROLE" trainer "$new_trainer_password"
expect_eq "crash after ALTER ROLE: .env still holds the old password" "$old_trainer_password" "$(dit_env_get "$APP_DIR/.env" DB_PASSWORD)"
db_login trainer "$new_trainer_password" || fail "crash after ALTER ROLE: the role does not have the new password"
if db_login trainer "$(dit_env_get "$APP_DIR/.env" DB_PASSWORD)"; then
  fail "crash after ALTER ROLE: the password in .env still logs in"
fi
# A restarted application re-reads its environment, which still holds the old password: it cannot connect. Waits (bounded) for
# the cause itself, the authentication failure in its log or an unhealthy status, instead of a fixed time.
docker restart "$(app_container)" >/dev/null
rejected=0
for _ in $(seq 1 45); do
  # Captured first: "docker logs | grep -q" under pipefail loses the match when grep exits early and docker gets SIGPIPE.
  app_log="$(docker logs "$(app_container)" 2>&1 || true)"
  if grep -q "password authentication failed" <<<"$app_log"; then
    rejected=1
    break
  fi
  if [ "$(docker inspect --format '{{.State.Health.Status}}' "$(app_container)")" = unhealthy ]; then
    rejected=1
    break
  fi
  sleep 2
done
[ "$rejected" = 1 ] || fail "crash after ALTER ROLE: the restarted application neither logged an authentication failure nor became unhealthy within 90 s"
if ready; then
  fail "crash after ALTER ROLE: a restarted application is ready with a password the database rejects"
fi
before="$(snapshot)"
deploy "rotation-refused-$RANDOM" "$(commit_for 601)" "$(image_for 601)"
expect_eq "crash after ALTER ROLE: deploy exit status" 3 "$deploy_status"
expect_eq "crash after ALTER ROLE: deploy left the state alone" "$before" "$(snapshot)"
rotate trainer commit
expect_eq "commit before finish: exit status" 1 "$rotate_status"
[ -f "$APP_DIR/.rotation-pending" ] || fail "commit before finish: the journal was removed"
rotate_bash=(bash -x)
rotate trainer finish
rotate_bash=(bash)
expect_eq "finish after a crash after ALTER ROLE: exit status" 0 "$rotate_status"
[ -s "$rotate_err" ] || fail "finish under xtrace: no trace was produced, so the check proves nothing"
assert_no_rotation_secret "finish under xtrace" "$rotate_err"
assert_password_state "finish after a crash after ALTER ROLE" DB_PASSWORD trainer "$new_trainer_password" "$old_trainer_password"
wait_ready
rotate trainer commit
expect_eq "commit after a crash after ALTER ROLE: exit status" 0 "$rotate_status"
DB_PASSWORD="$new_trainer_password"
pass crash_after_alter_role_then_finish_converges

# A crash between the two records: .env is new and last-good.env is old, and finish writes the second one.
old_umami_password="$UMAMI_DB_PASSWORD"
new_umami_password="$(openssl rand -hex 24)"
rotation_secrets+=("$new_umami_password")
rotate_kill=after-env
rotate umami begin "$new_umami_password"
expect_eq "crash after .env: exit status" 137 "$rotate_status"
expect_eq "crash after .env: .env" "$new_umami_password" "$(dit_env_get "$APP_DIR/.env" UMAMI_DB_PASSWORD)"
expect_eq "crash after .env: last-good.env" "$old_umami_password" "$(dit_env_get "$APP_DIR/last-good.env" UMAMI_DB_PASSWORD)"
rotate umami finish
expect_eq "finish after a crash after .env: exit status" 0 "$rotate_status"
assert_password_state "finish after a crash after .env" UMAMI_DB_PASSWORD umami "$new_umami_password" "$old_umami_password"
umami_answers || fail "finish after a crash after .env: Umami does not answer"
rotate umami commit
expect_eq "commit after a crash after .env: exit status" 0 "$rotate_status"
UMAMI_DB_PASSWORD="$new_umami_password"
pass crash_after_env_update_then_finish_converges

# A crash after both records are written but before the service was recreated: the running container still has the old
# password in its environment, and finish has to recreate it purely from the .env that already changed.
old_umami_password="$UMAMI_DB_PASSWORD"
new_umami_password="$(openssl rand -hex 24)"
rotation_secrets+=("$new_umami_password")
umami_before="$(dit_compose ps -q umami)"
rotate_kill=after-last-good
rotate umami begin "$new_umami_password"
expect_eq "crash after last-good.env: exit status" 137 "$rotate_status"
expect_eq "crash after last-good.env: .env" "$new_umami_password" "$(dit_env_get "$APP_DIR/.env" UMAMI_DB_PASSWORD)"
expect_eq "crash after last-good.env: last-good.env" "$new_umami_password" "$(dit_env_get "$APP_DIR/last-good.env" UMAMI_DB_PASSWORD)"
expect_eq "crash after last-good.env: the container was not touched" "$umami_before" "$(dit_compose ps -q umami)"
rotate umami finish
expect_eq "finish after a crash after last-good.env: exit status" 0 "$rotate_status"
[ "$(dit_compose ps -q umami)" != "$umami_before" ] || fail "finish after a crash after last-good.env: Umami was not recreated"
assert_password_state "finish after a crash after last-good.env" UMAMI_DB_PASSWORD umami "$new_umami_password" "$old_umami_password"
umami_answers || fail "finish after a crash after last-good.env: Umami does not answer"
rotate umami commit
expect_eq "commit after a crash after last-good.env: exit status" 0 "$rotate_status"
UMAMI_DB_PASSWORD="$new_umami_password"
pass crash_after_last_good_then_finish_recreates_service

# A step that fails after the journal exists exits 1 whatever psql returned (3), and the statement that carried the password
# does not reach PostgreSQL's log. The role is renamed away so that ALTER ROLE fails, then renamed back.
old_umami_password="$UMAMI_DB_PASSWORD"
new_umami_password="$(openssl rand -hex 24)"
rotation_secrets+=("$new_umami_password")
postgres_container="$(dit_compose ps -q postgres)"
dit_psql postgres -c 'ALTER ROLE umami RENAME TO umami_renamed' </dev/null >/dev/null
rotate umami begin "$new_umami_password"
dit_psql postgres -c 'ALTER ROLE umami_renamed RENAME TO umami' </dev/null >/dev/null
expect_eq "failing ALTER ROLE: exit status" 1 "$rotate_status"
assert_journal "failing ALTER ROLE" umami "$new_umami_password"
expect_eq "failing ALTER ROLE: .env unchanged" "$old_umami_password" "$(dit_env_get "$APP_DIR/.env" UMAMI_DB_PASSWORD)"
postgres_log="$smoke_root/postgres-after-failed-alter.log"
docker logs "$postgres_container" >"$postgres_log" 2>&1
grep -q 'role "umami" does not exist' "$postgres_log" || fail "failing ALTER ROLE: the server did not log the error, so the check proves nothing"
if grep -qF -- "$new_umami_password" "$postgres_log"; then
  fail "failing ALTER ROLE: the new password reached the PostgreSQL log"
fi
rotate umami finish
expect_eq "finish after a failing ALTER ROLE: exit status" 0 "$rotate_status"
assert_password_state "finish after a failing ALTER ROLE" UMAMI_DB_PASSWORD umami "$new_umami_password" "$old_umami_password"
rotate umami commit
expect_eq "commit after a failing ALTER ROLE: exit status" 0 "$rotate_status"
UMAMI_DB_PASSWORD="$new_umami_password"
pass failed_alter_role_exits_one_and_never_logs_the_password

# With the journal gone, the secrets the workflow wrote are what a deployment renders: it must be accepted.
deploy run-after-rotation "$(commit_for 602)" "$(image_for 602)"
expect_eq "deploy after the rotations: exit status" 0 "$deploy_status"
wait_ready
umami_answers || fail "deploy after the rotations: Umami does not answer"
expect_eq "deploy after the rotations: running services" "app postgres umami " "$(running_services)"
assert_no_rotation_secret "the whole run" "$logs"/*.log

# Every later predeploy and daily upload went through the endpoint that refuses reads and deletes as well.
if grep -Eq '^(GET|HEAD|DELETE) /dit-smoke/[^ ]+ ' "$s3_proxy_log"; then
  fail "off-site key: a backup script read or deleted an object: $(grep -E '^(GET|HEAD|DELETE) /dit-smoke/[^ ]+ ' "$s3_proxy_log" | head -n 5 | tr '\n' ';')"
fi
pass offsite_key_never_reads_or_deletes

echo "smoke passed"
