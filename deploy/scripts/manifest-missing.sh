#!/usr/bin/env bash
# Reads the output of "docker manifest inspect" from stdin and exits 0 when it says the image is not there yet (the caller
# then builds and pushes it), 1 for anything else (a network error, a daemon problem), which the caller treats as a failure.
#
# GHCR answers "denied" instead of "not found" for a package that has never been pushed. A real authentication problem
# is not hidden by that: the push that follows fails with it. "permission denied" (the Docker socket) is not a missing image.
set -euo pipefail

text="$(cat)"
text="${text,,}"
text="${text//permission denied/}"

case "$text" in
  *"manifest unknown"* | *"no such manifest"* | *"not found"* | *denied*) exit 0 ;;
esac
exit 1
