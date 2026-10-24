#!/usr/bin/env bash
#
# tools/simulation/ui/in-playwright-image.sh — run a command from this
# directory inside the Playwright image that e2e-smoke.yml pins as
# PLAYWRIGHT_IMAGE (its header says why CI does this).
#
#   PLAYWRIGHT_IMAGE=<image> bash tools/simulation/ui/in-playwright-image.sh npm test

set -euo pipefail

: "${PLAYWRIGHT_IMAGE:?set PLAYWRIGHT_IMAGE to the pinned image in .github/workflows/e2e-smoke.yml}"

UI_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$UI_DIR/../../.." && pwd)"

# --network host: the sim stack listens on the host's 127.0.0.1:8081.
# The whole repo at its own path: src/i18n.ts imports web/src, cast.ts reads ../.sim-cast.json.
# --user: files the run writes stay removable by the runner user.
# --init and --ipc=host: Playwright's Docker guide asks for both for Chromium.
exec docker run --rm --init --ipc=host --network host \
  --user "$(id -u):$(id -g)" \
  -v "$REPO_ROOT:$REPO_ROOT" -w "$UI_DIR" \
  -e CI -e BASE_URL -e CANARY_BROWSERS \
  -e CLUCKWORK_E2E_BUNDLED_BROWSER -e CLUCKWORK_E2E_CHROMIUM \
  -e CLUCKWORK_E2E_SLOW -e CLUCKWORK_E2E_UNDER_LOAD -e CLUCKWORK_E2E_MUTANT \
  "$PLAYWRIGHT_IMAGE" "$@"
