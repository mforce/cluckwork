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
#   * tools/simulation/out/canary-vitals/<screen>.json — read by run-baseline.sh
#     and folded into the findings doc's "Browser experience" section, so the
#     browser numbers and the server percentiles from the same window share a
#     page. ONE FILE PER SCREEN, because at CANARY_BROWSERS=2 the tests run in
#     two worker processes and a single shared file would be last-writer-wins.

set -euo pipefail

UI_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SIM_DIR="$(cd "$UI_DIR/.." && pwd)"
K6_SHELL_NIX="$SIM_DIR/k6/shell.nix"
K6_SCRIPT="$SIM_DIR/k6/baseline.js"
# Kept in step with run-baseline.sh's constant of the same name, on purpose.
# See that script's own comment for the bump history/evidence.
EXPECTED_K6_VERSION="v2.1.0"

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

# Clear previous samples ONLY once we are actually about to run. Doing it at the
# top destroyed a prior good run's data whenever a precondition below failed
# (missing node_modules, no nix-shell, a k6 version mismatch) — run-baseline.sh
# then reported "no browser canary was run" for a run that had one
# (PR #391 review round 2). Done here, once, rather than from a test: a test
# clearing shared output would race the other worker.
clear_samples() { rm -rf "$SIM_DIR/out/canary-vitals"; }

if [[ $WITH_LOAD -eq 0 ]]; then
  echo "== canary: QUIET baseline (no k6). This is the number to compare against, not the finding. =="
  clear_samples
  exec npx playwright test --config playwright.canary.config.ts
fi

# --- concurrent mode -------------------------------------------------------
for f in "$K6_SHELL_NIX" "$K6_SCRIPT"; do
  [[ -f "$f" ]] || { echo "canary: required file missing: $f" >&2; exit 1; }
done

# Same nix-shell-preferred, PATH-k6-fallback resolution as run-baseline.sh —
# see that script's own comment for why. Kept in step here on purpose.
if command -v nix-shell >/dev/null 2>&1; then
  K6_MODE="nix"
  echo "[preflight] verifying pinned k6 version via nix-shell (expect k6 ${EXPECTED_K6_VERSION})..."
  k6_version_line="$(nix-shell "$K6_SHELL_NIX" --run 'k6 version' 2>/dev/null || true)"
elif command -v k6 >/dev/null 2>&1; then
  K6_MODE="path"
  echo "canary: nix-shell not found — falling back to bare 'k6' on PATH (no pinned-build guarantee, EXPECTED_K6_VERSION is still enforced)." >&2
  echo "[preflight] verifying k6 on PATH (expect k6 ${EXPECTED_K6_VERSION})..."
  k6_version_line="$(k6 version 2>/dev/null || true)"
else
  echo "canary: neither nix-shell nor a bare 'k6' found on PATH — needed for --with-load." >&2
  exit 1
fi
# Exact equality on the version TOKEN, not a prefix match — same reasoning
# as run-baseline.sh's preflight (codex review, PR #430): a prefix glob lets
# an unverified prerelease/local build (e.g. `v2.1.0-rc.1`) pass silently.
k6_version_token="$(awk '{print $2}' <<<"$k6_version_line")"
if [[ "$k6_version_token" != "$EXPECTED_K6_VERSION" ]]; then
  echo "canary: k6 version mismatch (${K6_MODE}) — resolved '${k6_version_line:-<none>}', expected 'k6 ${EXPECTED_K6_VERSION}'." >&2
  echo "  Same rule as run-baseline.sh: baseline.js's VU scheduling was only probed against that build." >&2
  exit 1
fi

K6_LOG="$(mktemp -t canary-k6-XXXXXX.log)"
echo "== canary: starting k6 via ${K6_MODE} (log: $K6_LOG) =="
if [[ "$K6_MODE" == "nix" ]]; then
  nix-shell "$K6_SHELL_NIX" --run "k6 run '$K6_SCRIPT'" > "$K6_LOG" 2>&1 &
else
  k6 run "$K6_SCRIPT" > "$K6_LOG" 2>&1 &
fi
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
clear_samples
set +e
CLUCKWORK_E2E_UNDER_LOAD=1 npx playwright test --config playwright.canary.config.ts
CANARY_STATUS=$?
set -e

echo
echo "== canary: done (playwright exit ${CANARY_STATUS}) =="
echo "   vitals JSON:      $SIM_DIR/out/canary-vitals/*.json"
echo "   playwright report: $UI_DIR/playwright-report-canary/"
echo "   k6 log:            $K6_LOG"
echo
echo "   Fold the vitals into a findings doc with:"
echo "     bash tools/simulation/run-baseline.sh --render-only <RUN_ID>"
exit "$CANARY_STATUS"
