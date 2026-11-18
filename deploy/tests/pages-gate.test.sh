#!/usr/bin/env bash
# Behaviour of deploy/pages/pages-gate.sh (provenance, decide and recheck) against a local HTTP server standing in for the
# live site, a throw-away git history standing in for main and a fake gh.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
gate="$here/../pages/pages-gate.sh"
work="$(mktemp -d)"
server_pid=""
cleanup() {
  if [ -n "$server_pid" ]; then
    kill "$server_pid" 2>/dev/null || true
  fi
  rm -rf "$work"
}
trap cleanup EXIT
failures=0

check() {
  if [ "$2" != "$3" ]; then
    printf 'FAIL: %s\n  expected: %s\n  actual:   %s\n' "$1" "$2" "$3" >&2
    failures=$((failures + 1))
  fi
}

python="$(command -v python3 || command -v python || true)"
if [ -z "$python" ]; then
  echo "pages-gate.test.sh needs python3 for the stand-in site." >&2
  exit 1
fi

# The site answers from files under $root; a file named __fail turns every answer into a 503, __fail_root only the answer
# for the root, and __redirect_build answers build.json with a 302. The port is chosen by the
# operating system and written to a file, so parallel runs cannot collide.
cat >"$work/server.py" <<'PY'
import http.server
import os
import sys

root, port_file = sys.argv[1], sys.argv[2]


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        path = self.path.split("?", 1)[0]
        target = os.path.join(root, path.lstrip("/"))
        if path.endswith("/"):
            target = os.path.join(target, "index.html")
        if os.path.exists(os.path.join(root, "__fail")):
            status, body = 503, b"unavailable"
        elif path.endswith("build.json") and os.path.exists(os.path.join(root, "__redirect_build")):
            status, body = 302, b"moved"
        elif path.endswith("/") and os.path.exists(os.path.join(root, "__fail_root")):
            status, body = 503, b"unavailable"
        elif os.path.isfile(target):
            status, body = 200, open(target, "rb").read()
        else:
            status, body = 404, b"not found"
        self.send_response(status)
        if status == 302:
            self.send_header("Location", "/elsewhere")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
with open(port_file, "w") as handle:
    handle.write(str(server.server_port))
server.serve_forever()
PY

root="$work/root"
mkdir -p "$root/site"
"$python" "$work/server.py" "$root" "$work/port" &
server_pid=$!
for _ in $(seq 1 100); do
  [ -s "$work/port" ] && break
  sleep 0.1
done
[ -s "$work/port" ] || { echo "the stand-in site did not start" >&2; exit 1; }
site_url="http://127.0.0.1:$(cat "$work/port")/site/"

# c1 <- c2 <- c3 is the history of main; a side branch off c1 stands for a live commit that diverged from the candidate.
repo="$work/repo"
git init --quiet "$repo"
git -C "$repo" config user.email test@example.invalid
git -C "$repo" config user.name test
git -C "$repo" config commit.gpgsign false
git -C "$repo" config core.autocrlf false
commits=()
for n in 1 2 3; do
  echo "$n" >"$repo/f"
  git -C "$repo" add f
  git -C "$repo" commit --quiet -m "c$n"
  commits+=("$(git -C "$repo" rev-parse HEAD)")
done
c1="${commits[0]}"
c2="${commits[1]}"
c3="${commits[2]}"
git -C "$repo" checkout --quiet -b side "$c1"
echo side >"$repo/f"
git -C "$repo" commit --quiet -am side
side="$(git -C "$repo" rev-parse HEAD)"
git -C "$repo" checkout --quiet -
unknown="$(printf '%040d' 0 | tr 0 a)"

# A git that fails every merge-base with the status in FAKE_MERGE_BASE_STATUS and passes everything else through.
real_git="$(command -v git)"
mkdir -p "$work/fakebin"
cat >"$work/fakebin/git" <<SH
#!/usr/bin/env bash
if [ "\$1" = merge-base ]; then
  exit "\${FAKE_MERGE_BASE_STATUS:-128}"
fi
exec "$real_git" "\$@"
SH
chmod +x "$work/fakebin/git"

# A gh that answers "gh run list ... --jq length" with FAKE_GH_COUNT, or fails when FAKE_GH_FAIL is set.
cat >"$work/fakebin/gh" <<'SH'
#!/usr/bin/env bash
if [ -n "${FAKE_GH_FAIL:-}" ]; then
  exit 1
fi
echo "${FAKE_GH_COUNT:-0}"
SH
chmod +x "$work/fakebin/gh"

reset_site() {
  rm -f "$root/__fail" "$root/__fail_root" "$root/__redirect_build" "$root/site/build.json" "$root/site/index.html"
}

live() {
  reset_site
  printf '{\n  "buildId": "%s",\n  "commit": "%s",\n  "builtAt": "2026-11-18T00:00:00+00:00"\n}\n' "$1" "$2" >"$root/site/build.json"
}

# run_script <arguments...>: runs the script in the throw-away checkout; $status and $output hold the result. Variables
# set on the call line (PAGES_GATE_ALLOW_UNKNOWN_LIVE=true run_script ...) reach the script.
run_script() {
  status=0
  output="$(cd "$repo" && PAGES_GATE_RETRY_DELAY=0 bash "$gate" "$@" 2>"$work/stderr")" || status=$?
}

run_gate() {
  run_script decide "$site_url" "$1" "$2"
}

expected() {
  printf 'decision=%s\nfirst=%s\nlive_build_id=%s\nlive_commit=%s' "$1" "$2" "$3" "$4"
}

# live_equal_skips_on_workflow_run
live build-2 "$c2"
run_gate "$c2" workflow_run
check "live equal, workflow_run: exit" 0 "$status"
check "live equal, workflow_run: skip" "$(expected skip false build-2 "$c2")" "$output"

# live_equal_deploys_on_dispatch
run_gate "$c2" workflow_dispatch
check "live equal, workflow_dispatch: exit" 0 "$status"
check "live equal, workflow_dispatch: deploy" "$(expected deploy false build-2 "$c2")" "$output"

# live_newer_skips_on_both_events
live build-3 "$c3"
for event in workflow_run workflow_dispatch; do
  run_gate "$c2" "$event"
  check "live newer, $event: exit" 0 "$status"
  check "live newer, $event: skip" "$(expected skip false build-3 "$c3")" "$output"
done

# live_older_deploys
live build-1 "$c1"
for event in workflow_run workflow_dispatch; do
  run_gate "$c3" "$event"
  check "live older, $event: exit" 0 "$status"
  check "live older, $event: deploy" "$(expected deploy false build-1 "$c1")" "$output"
done

# divergent_live_commit_deploys: neither commit is an ancestor of the other
live build-side "$side"
for event in workflow_run workflow_dispatch; do
  run_gate "$c3" "$event"
  check "divergent live, $event: exit" 0 "$status"
  check "divergent live, $event: deploy" "$(expected deploy false build-side "$side")" "$output"
done

# empty_site_is_first_deployment: nothing at the manifest and nothing at the root
reset_site
run_gate "$c3" workflow_run
check "empty site: exit" 0 "$status"
check "empty site: first deployment" "$(expected deploy true "" "")" "$output"

# root_without_manifest_fails: something is live, but not a deployment of this workflow
reset_site
echo '<html>other site</html>' >"$root/site/index.html"
run_gate "$c3" workflow_run
check "root without manifest: exit" 1 "$status"
check "root without manifest: no decision" "" "$output"

# manifest_redirect_fails: a redirect is not an answer, and it is not followed
reset_site
touch "$root/__redirect_build"
run_gate "$c3" workflow_run
check "manifest redirect: exit" 1 "$status"
check "manifest redirect: no decision" "" "$output"

# root_server_error_after_missing_manifest_fails: a 404 manifest only proves an empty site when the root answers 404 too
reset_site
touch "$root/__fail_root"
run_gate "$c3" workflow_run
check "root 5xx after a missing manifest: exit" 1 "$status"
check "root 5xx after a missing manifest: no decision" "" "$output"
check "root 5xx after a missing manifest: three attempts" "1" "$(grep -c 'Attempt 3 of 3' "$work/stderr" || true)"

# unknown_live_commit_fails
live build-x "$unknown"
run_gate "$c3" workflow_run
check "unknown live commit: exit" 1 "$status"
check "unknown live commit: no decision" "" "$output"

# unknown_live_commit_needs_a_manual_run_and_the_flag
PAGES_GATE_ALLOW_UNKNOWN_LIVE=true run_gate "$c3" workflow_run
check "unknown live commit, automatic run with the flag: exit" 1 "$status"
check "unknown live commit, automatic run with the flag: no decision" "" "$output"
PAGES_GATE_ALLOW_UNKNOWN_LIVE=false run_gate "$c3" workflow_dispatch
check "unknown live commit, manual run with the flag off: exit" 1 "$status"
PAGES_GATE_ALLOW_UNKNOWN_LIVE=true run_gate "$c3" workflow_dispatch
check "unknown live commit, manual run with the flag: exit" 0 "$status"
check "unknown live commit, manual run with the flag: deploy" "$(expected deploy false build-x "$unknown")" "$output"

# server_error_fails: three attempts, then the run fails instead of guessing
reset_site
touch "$root/__fail"
run_gate "$c3" workflow_run
check "server error: exit" 1 "$status"
check "server error: no decision" "" "$output"

# a manifest that does not name a commit is not trusted
live build-1 "not-a-commit"
run_gate "$c3" workflow_run
check "malformed commit: exit" 1 "$status"

# a candidate that is not in the checkout cannot be ordered against the live commit
live build-1 "$c1"
run_gate "$unknown" workflow_run
check "unknown candidate: exit" 1 "$status"

# merge_base_exit_codes: 0 is an ancestor, 1 is not, anything else is an error and never a decision
live build-3 "$c3"
FAKE_MERGE_BASE_STATUS=0 PATH="$work/fakebin:$PATH" run_gate "$c2" workflow_run
check "merge-base 0: exit" 0 "$status"
check "merge-base 0: skip" "$(expected skip false build-3 "$c3")" "$output"
FAKE_MERGE_BASE_STATUS=1 PATH="$work/fakebin:$PATH" run_gate "$c2" workflow_run
check "merge-base 1: exit" 0 "$status"
check "merge-base 1: deploy" "$(expected deploy false build-3 "$c3")" "$output"
for code in 2 128; do
  FAKE_MERGE_BASE_STATUS=$code PATH="$work/fakebin:$PATH" run_gate "$c2" workflow_run
  check "merge-base $code: exit" 1 "$status"
  check "merge-base $code: no decision" "" "$output"
done

# usage errors
live build-1 "$c1"
run_gate "$c3" push
check "unknown event: exit" 2 "$status"
run_gate "not-a-sha" workflow_run
check "malformed candidate: exit" 2 "$status"
run_script
check "no arguments: exit" 2 "$status"
run_script unknown-mode "$site_url" "$c3" workflow_run
check "unknown mode: exit" 2 "$status"

# recheck: the same policy as decide against the live site as it is now; another deployment that went live meanwhile does not
# stop a candidate that is still newer, and only changes the notice
recheck() {
  run_script recheck "$site_url" "$c3" workflow_run "$1" "$2"
}

live build-1 "$c1"
recheck false build-1
check "recheck, same live build: exit" 0 "$status"
check "recheck, same live build: deploy" "deploy=true" "$output"
check "recheck, same live build: no notice" "0" "$(grep -c 'went live while the build ran' "$work/stderr" || true)"
recheck false build-other
check "recheck, another older build went live: exit" 0 "$status"
check "recheck, another older build went live: deploy" "deploy=true" "$output"
check "recheck, another older build went live: notice" "1" "$(grep -c '::notice::Another deployment went live while the build ran' "$work/stderr" || true)"
recheck true ""
check "recheck, first deployment is no longer first, candidate still newer: deploy" "deploy=true" "$output"
check "recheck, first deployment is no longer first: notice" "1" "$(grep -c '::notice::Another deployment' "$work/stderr" || true)"

# a divergent live commit is replaced, like in decide
live build-side "$side"
recheck false build-1
check "recheck, divergent live commit: deploy" "deploy=true" "$output"

live build-3 "$c3"
recheck false build-3
check "recheck, the candidate went live meanwhile: no deploy" "deploy=false" "$output"
recheck false build-1
check "recheck, the candidate went live meanwhile under another build id: no deploy" "deploy=false" "$output"
run_script recheck "$site_url" "$c2" workflow_run false build-1
check "recheck, a newer commit went live meanwhile: exit" 0 "$status"
check "recheck, a newer commit went live meanwhile: no deploy" "deploy=false" "$output"
run_script recheck "$site_url" "$c3" workflow_dispatch false build-3
check "recheck, manual run of the live commit: deploy" "deploy=true" "$output"

live build-9 "$unknown"
recheck false build-9
check "recheck, the live commit became unknown: exit" 1 "$status"
check "recheck, the live commit became unknown: no output" "" "$output"
PAGES_GATE_ALLOW_UNKNOWN_LIVE=true recheck false build-9
check "recheck, unknown live commit, automatic run with the flag: exit" 1 "$status"
check "recheck, unknown live commit, automatic run with the flag: no output" "" "$output"
PAGES_GATE_ALLOW_UNKNOWN_LIVE=false run_script recheck "$site_url" "$c3" workflow_dispatch false build-9
check "recheck, unknown live commit, manual run without the flag: exit" 1 "$status"
PAGES_GATE_ALLOW_UNKNOWN_LIVE=true run_script recheck "$site_url" "$c3" workflow_dispatch false build-9
check "recheck, unknown live commit, manual run with the flag: exit" 0 "$status"
check "recheck, unknown live commit, manual run with the flag: deploy" "deploy=true" "$output"
PAGES_GATE_ALLOW_UNKNOWN_LIVE=true run_script recheck "$site_url" "$c3" workflow_dispatch false build-other
check "recheck, unknown live commit, manual run with the flag, another build id: deploy" "deploy=true" "$output"

reset_site
recheck true ""
check "recheck, still the first deployment: exit" 0 "$status"
check "recheck, still the first deployment: deploy" "deploy=true" "$output"
check "recheck, still the first deployment: no notice" "0" "$(grep -c 'went live while the build ran' "$work/stderr" || true)"
recheck false build-1
check "recheck, the site was emptied: deploy" "deploy=true" "$output"

reset_site
touch "$root/__fail"
recheck false build-1
check "recheck, server error: exit" 1 "$status"
check "recheck, server error: no output" "" "$output"

live build-1 "$c1"
recheck maybe build-1
check "recheck, bad first flag: exit" 2 "$status"
recheck false ""
check "recheck, retention without a live build id: exit" 2 "$status"
run_script recheck "$site_url" "$c3" workflow_run false
check "recheck, missing argument: exit" 2 "$status"

# wait-live: the deploy job holds the pages lock until the live build.json carries its own build id
wait_live() {
  PAGES_GATE_WAIT_SECONDS="${PAGES_GATE_WAIT_SECONDS:-2}" PAGES_GATE_WAIT_INTERVAL="${PAGES_GATE_WAIT_INTERVAL:-1}" run_script wait-live "$site_url" "$1"
}

live build-new "$c3"
wait_live build-new
check "wait-live, the build is already live: exit" 0 "$status"
check "wait-live, the build is already live: no stdout" "" "$output"

live build-old "$c2"
wait_live build-new
check "wait-live, another build stays live: exit" 1 "$status"
check "wait-live, another build stays live: error names the build" "1" "$(grep -c "::error::.*build-new" "$work/stderr" || true)"
check "wait-live, another build stays live: error says what was live" "1" "$(grep -c "::error::.*build-old" "$work/stderr" || true)"

# the new build shows up while the script is waiting (Pages serves the old files for a while after a deploy)
live build-old "$c2"
(sleep 1; live build-new "$c3") &
appear_pid=$!
PAGES_GATE_WAIT_SECONDS=10 wait_live build-new
wait "$appear_pid"
check "wait-live, the build appears while waiting: exit" 0 "$status"

reset_site
wait_live build-new
check "wait-live, nothing is live: exit" 1 "$status"

touch "$root/__fail"
wait_live build-new
check "wait-live, server errors until the end: exit" 1 "$status"
check "wait-live, server errors until the end: error" "1" "$(grep -c "::error::" "$work/stderr" || true)"

# a transient server error does not end the wait
live build-new "$c3"
touch "$root/__fail"
(sleep 1; rm -f "$root/__fail") &
clear_pid=$!
PAGES_GATE_WAIT_SECONDS=10 wait_live build-new
wait "$clear_pid"
check "wait-live, a transient server error: exit" 0 "$status"

live build-new "$c3"
run_script wait-live "$site_url"
check "wait-live, missing build id: exit" 2 "$status"
run_script wait-live "$site_url" "bad id!"
check "wait-live, malformed build id: exit" 2 "$status"
run_script wait-live "not a url" build-new
check "wait-live, malformed site url: exit" 2 "$status"

# provenance: which commit may be published, from the event and the triggering run
base_env() {
  export SITE_URL="$site_url" EVENT_NAME=workflow_run REPOSITORY=owner/repo REF_NAME=refs/heads/main SHA="$c3"
  export RUN_CONCLUSION=success RUN_EVENT=push RUN_BRANCH=main RUN_REPOSITORY=owner/repo RUN_SHA="$c2"
  unset FAKE_GH_COUNT FAKE_GH_FAIL
}

provenance() {
  PATH="$work/fakebin:$PATH" run_script provenance
}

base_env
provenance
check "provenance, a green push run of this repository: exit" 0 "$status"
check "provenance, a green push run of this repository: candidate is the run commit" "sha=$c2" "$output"

for mutation in "RUN_CONCLUSION=failure" "RUN_CONCLUSION=cancelled" "RUN_CONCLUSION=" "RUN_EVENT=pull_request" "RUN_EVENT=workflow_dispatch" "RUN_BRANCH=feature" "RUN_REPOSITORY=fork/repo"; do
  base_env
  export "${mutation?}"
  provenance
  check "provenance, $mutation: exit" 0 "$status"
  check "provenance, $mutation: nothing to publish" "sha=" "$output"
done

base_env
export RUN_SHA="not-a-sha"
provenance
check "provenance, malformed run commit: exit" 1 "$status"

base_env
export SITE_URL=""
provenance
check "provenance, no SITE_URL (the address is derived by site-url): exit" 0 "$status"
check "provenance, no SITE_URL (the address is derived by site-url): candidate is the run commit" "sha=$c2" "$output"

# a manual run publishes the tip of main, and only when ci has a successful push run for it
base_env
export EVENT_NAME=workflow_dispatch FAKE_GH_COUNT=1
provenance
check "provenance, manual run with a green ci push run: exit" 0 "$status"
check "provenance, manual run with a green ci push run: candidate is the tip" "sha=$c3" "$output"

export FAKE_GH_COUNT=0
provenance
check "provenance, manual run without a green ci push run: exit" 1 "$status"
check "provenance, manual run without a green ci push run: no output" "" "$output"

export FAKE_GH_COUNT=1 REF_NAME=refs/heads/feature
provenance
check "provenance, manual run from another branch: exit" 1 "$status"

export FAKE_GH_COUNT=many
provenance
check "provenance, a non-numeric gh count: exit" 1 "$status"
check "provenance, a non-numeric gh count: no output" "" "$output"

unset FAKE_GH_COUNT
export REF_NAME=refs/heads/main FAKE_GH_FAIL=1
provenance
check "provenance, gh failing: exit" 1 "$status"

# site-url: the explicit custom domain, or the address derived from the repository
site_url_of() {
  SITE_URL="$1" REPOSITORY="$2" run_script site-url
}

site_url_of "https://example.org/trainer/" "Owner/Repo"
check "site-url, explicit custom domain: exit" 0 "$status"
check "site-url, explicit custom domain: printed unchanged" "site_url=https://example.org/trainer/" "$output"

site_url_of "https://example.org/" "Owner/Repo"
check "site-url, explicit root domain: exit" 0 "$status"
check "site-url, explicit root domain: printed unchanged" "site_url=https://example.org/" "$output"

for bad in "http://example.org/" "https://example.org" "https://example.org/x" "https://example.org/?q=1" "https://example.org/#f" "https://user@example.org/" "not a url"; do
  site_url_of "$bad" "owner/repo"
  check "site-url, invalid explicit '$bad': exit" 1 "$status"
  check "site-url, invalid explicit '$bad': no output" "" "$output"
  case "$(cat "$work/stderr")" in
    *SITE_URL*) ;;
    *) check "site-url, invalid explicit '$bad': the error names SITE_URL" "names SITE_URL" "does not" ;;
  esac
done

site_url_of "" "Owner/Dev-Interview-Trainer"
check "site-url, derived project site: exit" 0 "$status"
check "site-url, derived project site: owner lowercased, repository case kept" "site_url=https://owner.github.io/Dev-Interview-Trainer/" "$output"

# Mutation row: every spelling of the owner must reach the same lowercase host, so a derivation that forgets to lowercase fails here.
for owner in "Owner" "OWNER" "oWnEr"; do
  site_url_of "" "$owner/repo"
  check "site-url, owner spelled '$owner' gives a lowercase host" "site_url=https://owner.github.io/repo/" "$output"
done

for user_site in "Owner/owner.github.io" "owner/OWNER.GITHUB.IO" "OWNER/Owner.GitHub.io"; do
  site_url_of "" "$user_site"
  check "site-url, user site '$user_site': exit" 0 "$status"
  check "site-url, user site '$user_site': served at the root" "site_url=https://owner.github.io/" "$output"
done

site_url_of "" "owner/other.github.io"
check "site-url, a repository named after another owner's user site: exit" 0 "$status"
check "site-url, a repository named after another owner's user site is a project site" "site_url=https://owner.github.io/other.github.io/" "$output"

for repository in "" "owner" "owner/" "/repo" "owner/repo/extra" "own er/repo" "owner/re po" "owner/repo?x"; do
  site_url_of "" "$repository"
  check "site-url, malformed repository '$repository': exit" 1 "$status"
  check "site-url, malformed repository '$repository': no output" "" "$output"
done

if [ "$failures" -ne 0 ]; then
  printf '%s check(s) failed\n' "$failures" >&2
  exit 1
fi
echo "pages-gate tests passed"
