#!/usr/bin/env bash
#
# tools/simulation/ui/run-canary.sh — #386, the canary-under-load probe.
#
# Runs one or two real browsers through the SPA and records Core Web Vitals,
# either on a quiet system (the baseline you compare against) or WHILE k6 is
# loading the same backend (the measurement).
#
#   bash tools/simulation/ui/run-canary.sh              # quiet baseline
#   bash tools/simulation/ui/run-canary.sh --with-load  # concurrent with k6
#
# ================== THE CANARY IS NOT THE LOAD ==================
#
# `CANARY_BROWSERS` is 1 or 2 and the config REFUSES anything higher. Past two
# browsers this stops being a canary and becomes load, which would perturb the
# very number it exists to record. k6 stays the crowd (#243).
#
# ================== WHY IT DRIVES k6 ITSELF ==================
#
# The measurement only means something if the browser is on the glass while the
# backend is actually saturated. Starting them by hand in two terminals and
# hoping the windows overlap is how you get a "under load" figure taken during
# k6's ramp-down. So this script owns both processes and starts the canary only
# after k6 has been running long enough to be past warmup.
#
# It uses the SAME pinned k6 as run-baseline.sh (k6/shell.nix) and asserts the
# same version, for the reason recorded in that file's header: baseline.js's
# drain-gap behaviour was live-probed against exactly this build.
#
# ================== WHERE THE NUMBERS GO ==================
#
# Both places, deliberately:
#   * playwright-report-canary/ — traces and per-screen attachments, for
#     debugging one bad run.
#   * tools/simulation/out/canary-vitals.json — read by run-baseline.sh and
#     folded into the findings doc's "Browser experience" section, so the browser
#     numbers and the server percentiles from the same window share a page.

set -euo pipefail

UI_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SIM_DIR="$(cd "$UI_DIR/.." && pwd)"
K6_SHELL_NIX="$SIM_DIR/k6/shell.nix"
K6_SCRIPT="$SIM_DIR/k6/baseline.js"
# Kept in step with run-baseline.sh's constant of the same name, on purpose.
EXPECTED_K6_VERSION="v2.0.0"

# How long to let k6 run before the browser starts. baseline.js opens with a
# warmup phase; measuring during it would describe a system that is not yet busy.
WARMUP_GRACE_SECONDS="${WARMUP_GRACE_SECONDS:-45}"

WITH_LOAD=0
[[ "${1:-}" == "--with-load" ]] && WITH_LOAD=1

cd "$UI_DIR"

if [[ ! -d node_modules ]]; then
  echo "canary: dependencies are not installed."
  echo "  On NixOS:  PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 npm install   (a system chromium is used)"
  echo "  Elsewhere: npm install && npx playwright install chromium"
  exit 1
fi

if [[ $WITH_LOAD -eq 0 ]]; then
  echo "== canary: QUIET baseline (no k6). This is the number to compare against, not the finding. =="
  exec npx playwright test --config playwright.canary.config.ts
fi

# --- concurrent mode -------------------------------------------------------
command -v nix-shell >/dev/null 2>&1 || {
  echo "canary: nix-shell not found — needed to run the pinned k6." >&2
  exit 1
}
for f in "$K6_SHELL_NIX" "$K6_SCRIPT"; do
  [[ -f "$f" ]] || { echo "canary: required file missing: $f" >&2; exit 1; }
done

echo "[preflight] verifying pinned k6 version (expect k6 ${EXPECTED_K6_VERSION})..."
k6_version_line="$(nix-shell "$K6_SHELL_NIX" --run 'k6 version' 2>/dev/null || true)"
if [[ "$k6_version_line" != "k6 ${EXPECTED_K6_VERSION}"* ]]; then
  echo "canary: k6 version mismatch — pinned shell resolved to '${k6_version_line:-<none>}', expected 'k6 ${EXPECTED_K6_VERSION}'." >&2
  echo "  Same rule as run-baseline.sh: baseline.js's VU scheduling was only probed against that build." >&2
  exit 1
fi

K6_LOG="$(mktemp -t canary-k6-XXXXXX.log)"
echo "== canary: starting k6 (log: $K6_LOG) =="
nix-shell "$K6_SHELL_NIX" --run "k6 run '$K6_SCRIPT'" > "$K6_LOG" 2>&1 &
K6_PID=$!

# Always reap k6, including on Ctrl-C or a failing canary — a k6 left hammering
# the sim stack would poison every later run on this box.
cleanup() {
  if kill -0 "$K6_PID" 2>/dev/null; then
    echo "== canary: stopping k6 (pid $K6_PID) =="
    kill "$K6_PID" 2>/dev/null || true
    wait "$K6_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

echo "== canary: letting k6 get past warmup (${WARMUP_GRACE_SECONDS}s) =="
for _ in $(seq "$WARMUP_GRACE_SECONDS"); do
  if ! kill -0 "$K6_PID" 2>/dev/null; then
    echo "canary: k6 exited during warmup — measuring now would record an idle system." >&2
    echo "  Its log:" >&2
    tail -20 "$K6_LOG" >&2
    exit 1
  fi
  sleep 1
done

echo "== canary: running browsers against a LOADED backend =="
set +e
CLUCKWORK_E2E_UNDER_LOAD=1 npx playwright test --config playwright.canary.config.ts
CANARY_STATUS=$?
set -e

echo
echo "== canary: done (playwright exit ${CANARY_STATUS}) =="
echo "   vitals JSON:      $SIM_DIR/out/canary-vitals.json"
echo "   playwright report: $UI_DIR/playwright-report-canary/"
echo "   k6 log:            $K6_LOG"
echo
echo "   Fold the vitals into a findings doc with:"
echo "     bash tools/simulation/run-baseline.sh --render-only <RUN_ID>"
exit "$CANARY_STATUS"
