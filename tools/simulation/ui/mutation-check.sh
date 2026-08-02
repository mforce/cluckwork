#!/usr/bin/env bash
#
# tools/simulation/ui/mutation-check.sh — proves the E2E suite can actually fail.
#
# ================== THE SHAPE, AND WHY IT IS THIS SHAPE ==================
#
# Three phases, and ALL THREE ARE PRINTED:
#
#   1. BASELINE   — the suite must be GREEN before anything is mutated.
#   2. MUTANTS    — each mutant breaks one guarantee; the spec that claims to
#                   cover it must go RED.
#   3. RESTORE    — the suite must be GREEN again, proving the mutants left
#                   nothing behind.
#
# Phase 1 is not ceremony. **A mutation run whose baseline is already red proves
# nothing and reads exactly like success** — every mutant "fails as expected",
# the script prints all-clear, and the suite was broken the whole time. So a red
# baseline aborts here rather than continuing.
#
# Phase 3 is the mirror: these mutants are route interceptions inside a browser
# context, so they cannot in principle leak — but the specs also WRITE to a shared
# fixture, and a mutant that made a write succeed when it should have been refused
# can leave a row behind. Re-running the suite clean is the cheap way to find out.
#
# ================== WHAT A SURVIVING MUTANT MEANS ==================
#
# If a mutant does NOT turn its spec red, the spec is not testing what it claims.
# This script reports that as a FAILURE, loudly, and exits non-zero. Do not
# "fix" it by deleting the mutant.
#
# Usage:  bash tools/simulation/ui/mutation-check.sh [mutant-name ...]
#         (no arguments = every mutant)

set -uo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

# --- the mutant -> spec map ------------------------------------------------
# Kept here rather than derived from src/mutants.ts so this script stays a plain
# shell tool with no build step. `caughtBy` in that file is the same mapping in
# prose; if they disagree, one of them is wrong and the run will say so by
# reporting a survivor.
declare -A SPEC_FOR=(
  [audit-gate-removed]="specs/readonly.spec.ts"
  [users-gate-removed]="specs/readonly.spec.ts"
  [flock-scope-removed]="specs/worker.spec.ts"
  [stock-summary-broken]="specs/owner.spec.ts"
  [report-range-bound-removed]="specs/reports-range.spec.ts"
  [refresh-always-fails]="specs/session-refresh.spec.ts"
  [logout-not-honoured]="specs/session-races.spec.ts"
)

declare -A GREP_FOR=(
  [audit-gate-removed]="direct link to /audit"
  [users-gate-removed]="direct link to /users"
  [flock-scope-removed]="is refused a daily entry"
  [stock-summary-broken]="dashboard shows real production"
  [report-range-bound-removed]="refuses one day beyond"
  [refresh-always-fails]="forces a 401"
  [logout-not-honoured]="logout during an in-flight refresh"
)

MUTANTS=("$@")
if [ ${#MUTANTS[@]} -eq 0 ]; then
  MUTANTS=(audit-gate-removed users-gate-removed flock-scope-removed
           stock-summary-broken report-range-bound-removed
           refresh-always-fails logout-not-honoured)
fi

rule() { printf '\n%s\n' "────────────────────────────────────────────────────────────────────────"; }

# --- phase 1: baseline -----------------------------------------------------
rule
echo "PHASE 1/3 — BASELINE (the suite must be GREEN before anything is mutated)"
rule
if npx playwright test --reporter=line; then
  echo "BASELINE: GREEN"
else
  echo
  echo "BASELINE: RED — ABORTING."
  echo "A mutation run on a already-failing suite proves nothing: every mutant would"
  echo "'fail as expected' for the wrong reason. Fix the suite, then re-run."
  exit 1
fi

# --- phase 2: mutants ------------------------------------------------------
rule
echo "PHASE 2/3 — MUTANTS (each must turn its spec RED)"
rule

killed=(); survived=()
for name in "${MUTANTS[@]}"; do
  spec="${SPEC_FOR[$name]:-}"
  pattern="${GREP_FOR[$name]:-}"
  if [ -z "$spec" ]; then
    echo "  ?? $name — no spec mapped in this script; skipping (fix SPEC_FOR)"
    survived+=("$name (unmapped)")
    continue
  fi

  printf '  .. %-28s -> %s\n' "$name" "$spec"
  if CLUCKWORK_E2E_MUTANT="$name" npx playwright test "$spec" -g "$pattern" \
       --reporter=line > "/tmp/mutant-$name.log" 2>&1; then
    echo "     SURVIVED — the spec still passed with this guarantee broken."
    survived+=("$name")
  else
    # Distinguish "the assertion caught it" from "the run fell over". A mutant
    # that crashes the runner is not a kill: it proves the harness broke, not
    # that the spec noticed. Playwright prints a failed-test count on a real
    # assertion failure and does not when it dies during setup.
    if grep -qE "^ *[0-9]+ failed" "/tmp/mutant-$name.log"; then
      echo "     KILLED — the spec failed, as it should."
      killed+=("$name")
    else
      echo "     INCONCLUSIVE — the run errored without a test failure (see /tmp/mutant-$name.log)"
      survived+=("$name (inconclusive)")
    fi
  fi
done

# --- phase 3: restore ------------------------------------------------------
rule
echo "PHASE 3/3 — RESTORE (the suite must be GREEN again)"
rule
if npx playwright test --reporter=line; then
  restore="GREEN"
else
  restore="RED"
fi
echo "RESTORE: $restore"

# --- verdict ---------------------------------------------------------------
rule
echo "RESULT"
rule
echo "  baseline : GREEN"
echo "  killed   : ${#killed[@]}  ${killed[*]:-}"
echo "  survived : ${#survived[@]}  ${survived[*]:-}"
echo "  restore  : $restore"

if [ ${#survived[@]} -ne 0 ] || [ "$restore" != "GREEN" ]; then
  echo
  echo "NOT CLEAN. A surviving mutant means that spec does not test what it claims;"
  echo "a red restore means a mutant left state behind. Report it, do not delete it."
  exit 1
fi
echo
echo "All mutants killed, baseline and restore both green."
