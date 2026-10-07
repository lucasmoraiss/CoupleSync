#!/usr/bin/env bash
# First step of the "App E2E" workflow: is there anything in this pull request for the screen tests to test?
# Writes `run=true` or `run=false` to $GITHUB_OUTPUT (to stdout when it is not set) and says why.
#
# A pull request is tested when it touches the app (mobile/), the API (backend/), or the workflow itself and
# its scripts (.github/workflows/app-e2e.yml, scripts/app-e2e-*). A manual run (workflow_dispatch) always tests.
# When in doubt the answer is "test".
#
# Needs the checkout of a pull request as GitHub Actions makes it (the merge commit of the PR into its base)
# with at least two commits of history: `actions/checkout` with `fetch-depth: 2`.
#
# Usage: EVENT_NAME=pull_request scripts/app-e2e-changes.sh
set -euo pipefail

RELEVANT='^(mobile/|backend/|scripts/app-e2e-|\.github/workflows/app-e2e\.yml$)'

decide() {
  echo "run=$1" >> "${GITHUB_OUTPUT:-/dev/stdout}"
  echo "$2"
}

if [ "${EVENT_NAME:-}" != "pull_request" ]; then
  decide true "Event '${EVENT_NAME:-unknown}' is not a pull request: the screen tests always run."
  exit 0
fi

# In a pull request HEAD is the merge commit: first parent = base branch, second parent = head of the PR.
# The difference to the first parent is exactly what the PR changes.
if ! git rev-parse --verify --quiet 'HEAD^2' >/dev/null; then
  decide true "HEAD is not a merge commit (or its parents were not fetched): the screen tests run."
  exit 0
fi

changed="$(git diff --name-only 'HEAD^1' HEAD)"
relevant="$(printf '%s\n' "$changed" | grep -E "$RELEVANT" || true)"

if [ -n "$relevant" ]; then
  echo "Files of this pull request that the screen tests cover:"
  printf '%s\n' "$relevant" | head -n 40 | sed 's/^/  /'
  decide true "The screen tests run."
else
  decide false "This pull request touches nothing under mobile/, backend/ or the App E2E workflow: nothing to test."
fi
