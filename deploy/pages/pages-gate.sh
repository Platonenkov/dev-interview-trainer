#!/usr/bin/env bash
# The deploy decisions of pages.yml, as one testable script (deploy/tests/pages-gate.test.sh).
#
# Usage:
#   pages-gate.sh site-url
#       Reads SITE_URL (the repository variable, may be empty) and REPOSITORY (owner/name) from the environment and prints
#       site_url=<address with a trailing slash>. A non-empty SITE_URL is a custom domain: it is validated and printed
#       unchanged. An empty one is derived from the repository: https://<owner>.github.io/<name>/, with the owner lowercased
#       (host names are) and the name kept as written (Pages paths are case-sensitive); a repository named
#       <owner>.github.io is a user site served at the root. Exit 1: an invalid SITE_URL or a malformed REPOSITORY.
#   pages-gate.sh provenance
#       Reads EVENT_NAME, REPOSITORY, REF_NAME, SHA, RUN_CONCLUSION, RUN_EVENT, RUN_BRANCH, RUN_REPOSITORY and
#       RUN_SHA from the environment (and GH_TOKEN for gh). Prints sha=<full commit>, or sha= when there is nothing to
#       publish. Exit 1: the run must fail (a manual run off main or without a green ci push run).
#   pages-gate.sh decide <site_url> <candidate_sha> <event>
#       Prints decision (deploy or skip), first (true only for the very first deployment), live_build_id and live_commit.
#   pages-gate.sh recheck <site_url> <candidate_sha> <event> <seen_first> <seen_live_build_id>
#       Decides again, just before deploying, with the same policy as decide against the live site as it is now, and
#       prints deploy=true or deploy=false. Another deployment that went live meanwhile does not stop a candidate that is
#       still newer than it (two close pushes must leave the newest commit live); the changed live build id only produces a
#       notice, because the build copied its immutable files from the build the gate saw (<seen_first> and
#       <seen_live_build_id> are what the gate saw).
#   pages-gate.sh wait-live <site_url> <build_id>
#       Run inside the deploy job after deploy-pages, so the "pages" lock is held until the new build is visible: the next
#       queued deploy job then decides against a build.json that already carries this build. Polls
#       <site_url>build.json?_r=<time> until its buildId is <build_id>, for PAGES_GATE_WAIT_SECONDS (default 300) with
#       PAGES_GATE_WAIT_INTERVAL seconds (default 10) between polls; an error status or a transport failure is just another
#       poll. Prints nothing on stdout. Exit 1 on timeout, with an error that names both build ids.
#
#   <site_url>       the public site address with a trailing slash (the output of site-url)
#   <candidate_sha>  the full commit the run would publish
#   <event>          workflow_run (automatic) or workflow_dispatch (manual); anything else exits 2
#
# stdout carries only key=value lines ready for $GITHUB_OUTPUT; everything else goes to stderr. decide and recheck run inside
# a checkout that has the history of main (fetch-depth: 0), because ordering is decided by commit ancestry.
#
# PAGES_GATE_ALLOW_UNKNOWN_LIVE=true lets a manual run (workflow_dispatch) deploy when the live commit is not in the history,
# for example after main was rewritten; an automatic run never does. PAGES_GATE_RETRY_DELAY (seconds, default 5) is the pause
# between attempts and PAGES_GATE_WAIT_SECONDS / PAGES_GATE_WAIT_INTERVAL bound wait-live; they exist for the tests.
#
# Exit codes: 0 a decision was made (or the build is live), 1 the live site or the history is inconsistent (nothing is guessed), 2 usage.
set -euo pipefail

usage() {
  echo "usage: pages-gate.sh site-url | provenance | decide <site_url> <candidate_sha> <event> | recheck <site_url> <candidate_sha> <event> <seen_first> <seen_live_build_id> | wait-live <site_url> <build_id>" >&2
  exit 2
}

provenance_skip() {
  echo "::notice::Nothing to publish: $1" >&2
  echo "sha="
  exit 0
}

site_url() {
  local explicit="${SITE_URL:-}" repository="${REPOSITORY:-}" owner name
  if [ -n "$explicit" ]; then
    if ! [[ "$explicit" =~ ^https://[^/?#@]+/([^?#]*/)?$ ]]; then
      echo "::error::The SITE_URL repository variable must be an https address with a trailing slash, without a query, a fragment or credentials; clear it to use the address derived from the repository." >&2
      exit 1
    fi
    echo "site_url=$explicit"
    return
  fi
  if ! [[ "$repository" =~ ^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$ ]]; then
    echo "::error::The repository '$repository' is not in the owner/name form, so the site address cannot be derived." >&2
    exit 1
  fi
  owner="${repository%%/*}"
  name="${repository#*/}"
  owner="${owner,,}"
  if [ "${name,,}" = "$owner.github.io" ]; then
    echo "site_url=https://$owner.github.io/"
  else
    echo "site_url=https://$owner.github.io/$name/"
  fi
}

provenance() {
  local candidate green
  case "${EVENT_NAME:-}" in
    workflow_run)
      # workflow_run carries the repository's permissions even when the triggering run was not privileged, and its "branches"
      # filter matches a head branch name that a fork's pull request can also call "main", so every property is checked again.
      if [ "${RUN_CONCLUSION:-}" != "success" ]; then
        provenance_skip "ci concluded with '${RUN_CONCLUSION:-}'."
      fi
      if [ "${RUN_EVENT:-}" != "push" ]; then
        provenance_skip "ci was started by '${RUN_EVENT:-}', not by a push."
      fi
      if [ "${RUN_BRANCH:-}" != "main" ]; then
        provenance_skip "ci ran for branch '${RUN_BRANCH:-}'."
      fi
      if [ "${RUN_REPOSITORY:-}" != "${REPOSITORY:-}" ]; then
        provenance_skip "ci ran for the repository '${RUN_REPOSITORY:-}'."
      fi
      candidate="${RUN_SHA:-}"
      ;;
    workflow_dispatch)
      if [ "${REF_NAME:-}" != "refs/heads/main" ]; then
        echo "::error::Run this workflow from main." >&2
        exit 1
      fi
      candidate="${SHA:-}"
      green="$(gh run list --repo "${REPOSITORY:-}" --workflow ci.yml --commit "$candidate" --event push --status success --json databaseId --jq 'length')"
      if ! [[ "$green" =~ ^[0-9]+$ ]] || [ "$green" -lt 1 ]; then
        echo "::error::The tip of main ($candidate) has no successful ci push run; wait for ci, or fix it, before publishing." >&2
        exit 1
      fi
      ;;
    *)
      echo "::error::Unsupported event '${EVENT_NAME:-}'; provenance is checked for workflow_run and workflow_dispatch only." >&2
      exit 2
      ;;
  esac
  if ! [[ "$candidate" =~ ^[0-9a-f]{40}$ ]]; then
    echo "::error::The candidate is not a full commit SHA." >&2
    exit 1
  fi
  echo "sha=$candidate"
}

# Shared checks of decide and recheck. Sets site_url, candidate and event.
read_decision_arguments() {
  site_url="$1"
  candidate="$2"
  event="$3"
  case "$event" in
    workflow_run | workflow_dispatch) ;;
    *)
      echo "::error::Unsupported event '$event'; the gate decides for workflow_run and workflow_dispatch only." >&2
      exit 2
      ;;
  esac
  if ! [[ "$candidate" =~ ^[0-9a-f]{40}$ ]]; then
    echo "::error::The candidate must be a full lowercase commit SHA." >&2
    exit 2
  fi
  if ! [[ "$site_url" =~ ^https?://[^?#]+/$ ]]; then
    echo "::error::The site URL must be an absolute http(s) URL with a trailing slash and no query or fragment." >&2
    exit 2
  fi
  # An ancestry decision against a commit git does not know would silently read as "not an ancestor".
  if ! git cat-file -e "${candidate}^{commit}" 2>/dev/null; then
    echo "::error::The candidate $candidate is not in this checkout's history; fetch the full history of main." >&2
    exit 1
  fi
}

attempts=3
retry_delay="${PAGES_GATE_RETRY_DELAY:-5}"
body="$(mktemp)"
trap 'rm -f "$body"' EXIT

# Sets http_status and leaves the response body in $body. Redirects are not followed: the live site answers these two
# URLs directly, and a redirect would hide which site was asked. Only a transport failure or a 5xx is retried; a 404 is
# an answer. The query defeats the CDN's ten-minute page cache, so the decision is made on what is live now.
fetch() {
  local url attempt code
  url="$1?_r=$(date +%s%N)"
  for attempt in $(seq 1 "$attempts"); do
    code="$(curl --silent --show-error --max-time 20 --output "$body" --write-out '%{http_code}' "$url" || true)"
    if [[ "$code" =~ ^[0-9]{3}$ ]] && [ "$code" -lt 500 ] && [ "$code" -ne 0 ]; then
      http_status="$code"
      return 0
    fi
    echo "Attempt $attempt of $attempts for $1 gave '${code:-no response}'." >&2
    if [ "$attempt" -lt "$attempts" ]; then
      sleep "$retry_delay"
    fi
  done
  echo "::error::$1 did not answer after $attempts attempts." >&2
  exit 1
}

# Both fields are flat strings in build.json, so no JSON tool is needed; the values are validated before use.
json_string() {
  sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" "$body" | head -n 1
}

# Sets decision, first, live_build_id and live_commit from the live site and the candidate.
decide_now() {
  local ancestor_status
  decision=""
  first=false
  live_build_id=""
  live_commit=""
  fetch "${site_url}build.json"
  case "$http_status" in
    200)
      live_build_id="$(json_string buildId)"
      live_commit="$(json_string commit)"
      if ! [[ "$live_build_id" =~ ^[A-Za-z0-9._-]+$ ]] || ! [[ "$live_commit" =~ ^[0-9a-f]{40}$ ]]; then
        echo "::error::${site_url}build.json does not carry a valid buildId and commit." >&2
        exit 1
      fi
      if ! git cat-file -e "${live_commit}^{commit}" 2>/dev/null; then
        if [ "$event" = "workflow_dispatch" ] && [ "${PAGES_GATE_ALLOW_UNKNOWN_LIVE:-}" = "true" ]; then
          echo "::warning::The live commit $live_commit is not in this checkout's history; publishing anyway because allow_unknown_live was set on a manual run." >&2
          decision=deploy
          return 0
        fi
        echo "::error::The live commit $live_commit is not in this checkout's history (main was rewritten, or the live site was not built from this repository). After checking that, run the workflow by hand with allow_unknown_live set." >&2
        exit 1
      fi
      if [ "$candidate" = "$live_commit" ]; then
        if [ "$event" = "workflow_run" ]; then
          echo "::notice::The candidate is already live: $live_commit." >&2
          decision=skip
        else
          echo "The candidate is live; a manual run republishes it with a new build id." >&2
          decision=deploy
        fi
        return 0
      fi
      # 0 is an ancestor, 1 is not; any other status is an error in git itself and must not read as "not an ancestor".
      ancestor_status=0
      git merge-base --is-ancestor "$candidate" "$live_commit" || ancestor_status=$?
      case "$ancestor_status" in
        0)
          echo "::notice::The live site ($live_commit) is newer than the candidate; nothing goes backwards." >&2
          decision=skip
          ;;
        1)
          decision=deploy
          ;;
        *)
          echo "::error::git merge-base --is-ancestor failed with status $ancestor_status; the order of $candidate and $live_commit is unknown." >&2
          exit 1
          ;;
      esac
      ;;
    404)
      # No manifest is either a site that was never deployed or somebody else's site; only an empty root proves the first.
      fetch "$site_url"
      if [ "$http_status" = "404" ]; then
        echo "Nothing is live at $site_url yet: the first deployment." >&2
        decision=deploy
        first=true
      else
        echo "::error::${site_url}build.json is missing but the root answers $http_status: something else is published here." >&2
        exit 1
      fi
      ;;
    *)
      echo "::error::${site_url}build.json answered $http_status." >&2
      exit 1
      ;;
  esac
}

# Polls until the live manifest carries <build_id>. Unlike fetch, a failed poll is not fatal: Pages is expected to answer 404 or
# the previous build for a while after a deploy, and only the deadline decides.
wait_live() {
  local url build_id wait_seconds interval deadline code live last
  site_url="$1"
  build_id="$2"
  if ! [[ "$site_url" =~ ^https?://[^?#]+/$ ]]; then
    echo "::error::The site URL must be an absolute http(s) URL with a trailing slash and no query or fragment." >&2
    exit 2
  fi
  if ! [[ "$build_id" =~ ^[A-Za-z0-9._-]+$ ]]; then
    echo "::error::The build id must contain only letters, digits, dots, underscores and hyphens." >&2
    exit 2
  fi
  wait_seconds="${PAGES_GATE_WAIT_SECONDS:-300}"
  interval="${PAGES_GATE_WAIT_INTERVAL:-10}"
  deadline=$(($(date +%s) + wait_seconds))
  last="no answer"
  while true; do
    url="${site_url}build.json?_r=$(date +%s%N)"
    code="$(curl --silent --show-error --max-time 20 --output "$body" --write-out '%{http_code}' "$url" || true)"
    if [ "$code" = "200" ]; then
      live="$(json_string buildId)"
      if [ "$live" = "$build_id" ]; then
        echo "The build $build_id is live." >&2
        return 0
      fi
      last="build '${live:-unreadable}'"
    else
      last="status '${code:-none}'"
    fi
    if [ "$(date +%s)" -ge "$deadline" ]; then
      break
    fi
    echo "Waiting for $build_id: the site shows $last." >&2
    sleep "$interval"
  done
  echo "::error::${site_url}build.json did not show the build $build_id within ${wait_seconds} seconds (it shows $last). The deployment may still be going live; check the site and run the workflow again if it does not." >&2
  exit 1
}

if [ "$#" -lt 1 ]; then
  usage
fi
mode="$1"
shift

case "$mode" in
  site-url)
    if [ "$#" -ne 0 ]; then
      usage
    fi
    site_url
    ;;
  provenance)
    if [ "$#" -ne 0 ]; then
      usage
    fi
    provenance
    ;;
  decide)
    if [ "$#" -ne 3 ]; then
      usage
    fi
    read_decision_arguments "$@"
    decide_now
    printf 'decision=%s\nfirst=%s\nlive_build_id=%s\nlive_commit=%s\n' "$decision" "$first" "$live_build_id" "$live_commit"
    ;;
  recheck)
    if [ "$#" -ne 5 ]; then
      usage
    fi
    seen_first="$4"
    seen_live_build_id="$5"
    case "$seen_first" in
      true | false) ;;
      *)
        echo "::error::seen_first must be true or false." >&2
        exit 2
        ;;
    esac
    if [ "$seen_first" = "false" ] && [ -z "$seen_live_build_id" ]; then
      echo "::error::A deployment that is not the first needs the live build id the gate saw." >&2
      exit 2
    fi
    read_decision_arguments "$1" "$2" "$3"
    decide_now
    deploy=false
    if [ "$decision" = "deploy" ]; then
      deploy=true
      if [ "$first" != "$seen_first" ] || [ "$live_build_id" != "$seen_live_build_id" ]; then
        echo "::notice::Another deployment went live while the build ran (build id '${live_build_id:-none}', the gate saw '${seen_live_build_id:-none}'), but the candidate is still newer, so it is published. Its retained files were copied from the older build, so a client still on the replaced build may find some of its hashed files missing until the recovery banner reloads it." >&2
      fi
    else
      echo "::notice::The candidate is already live or older than the live site now; this run does not deploy." >&2
    fi
    echo "deploy=$deploy"
    ;;
  wait-live)
    if [ "$#" -ne 2 ]; then
      usage
    fi
    wait_live "$@"
    ;;
  *)
    usage
    ;;
esac
