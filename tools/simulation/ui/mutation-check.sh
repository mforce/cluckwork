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
# ================== A RED IS NOT ALWAYS A PROOF ==================
#
# One mutant is KNOWN to go red for a reason unrelated to the guarantee it names.
# `nav-role-gate-bypassed` forges the role claim, and the SERVER rejects the
# forged token, so the spec dies inside `signIn` before it ever looks at a nav
# link. src/mutants.ts has said so since PR #390 review round 2 — but this script
# still counted it in the headline, so the run printed "10 killed" while only 9
# of those kills proved anything. A score that overstates itself is exactly the
# failure this whole harness exists to prevent, so the false kill is now named
# in the output and subtracted from the real count (PR #390 review round 3).
#
# Adding to FALSE_KILLS is a confession, not a silencer: it keeps the mutant
# running and still fails the run if it SURVIVES. It only stops its red from
# being counted as evidence.
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
  [nav-role-gate-bypassed]="specs/readonly.spec.ts"
  [payment-never-settles]="specs/sales.spec.ts"
  [export-returns-nothing]="specs/owner.spec.ts"
)

declare -A GREP_FOR=(
  [audit-gate-removed]="direct link to /audit"
  [users-gate-removed]="direct link to /users"
  [flock-scope-removed]="is refused a daily entry"
  [stock-summary-broken]="dashboard shows real production"
  [report-range-bound-removed]="refuses one day beyond"
  [refresh-always-fails]="forces a 401"
  [logout-not-honoured]="logout during an in-flight refresh"
  [nav-role-gate-bypassed]="is not offered the destinations"
  [payment-never-settles]="takes an order from new customer"
  [export-returns-nothing]="export downloads a real file"
)

# Mutants whose RED is known not to prove the guarantee they name. See the header.
declare -A FALSE_KILLS=(
  [nav-role-gate-bypassed]="the server rejects the forged token, so sign-in fails before the nav assertion runs"
)

MUTANTS=("$@")
if [ ${#MUTANTS[@]} -eq 0 ]; then
  MUTANTS=(audit-gate-removed users-gate-removed flock-scope-removed
           stock-summary-broken report-range-bound-removed
           refresh-always-fails logout-not-honoured
           nav-role-gate-bypassed payment-never-settles export-returns-nothing)
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

killed=(); survived=(); false_killed=()
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
    # Distinguish "an ASSERTION caught it" from "the run fell over".
    #
    # A mutant that merely crashes the spec is NOT a kill — it proves the harness
    # broke, not that the spec noticed the regression. An earlier version only
    # grepped for Playwright's "N failed" line and claimed in a comment that this
    # excluded crashes. It does not: an uncaught TypeError in a spec body reports
    # as "1 failed" exactly like a failed expectation, so a crash was recorded as
    # KILLED (PR #390 review).
    #
    # The real distinction is Playwright's MATCHER SUMMARY line, which it prints
    # for an assertion failure and only for an assertion failure:
    #
    #     expect(received).toBeGreaterThan(expected)
    #     expect(locator).toBeVisible() failed
    #
    # Two earlier versions of this check were both wrong, and in the same way —
    # each let a crash read as a proven kill:
    #   1. grepping only for "N failed": an uncaught TypeError prints that too.
    #   2. grepping for `expect(` ANYWHERE in the log: Playwright prints a source
    #      CODE FRAME around every failure, crash included, and these specs call
    #      `expect` every few lines — so a crash next to an unrelated assertion
    #      matched. Reproduced by a reviewer with a TypeError two lines after a
    #      passing `expect(true).toBe(true)`.
    #
    # Anchoring at line start separates the two by construction: a code frame is
    # always prefixed with its line number (`> 89 |`), so it can never match.
    #
    # The optional `Error: ` prefix is load-bearing. Playwright prints the matcher
    # summary on its OWN line when the assertion carried a custom message, and
    # INLINE after `Error: ` when it did not:
    #
    #     Error: my custom message          |    Error: expect(locator).toBeVisible() failed
    #     expect(received).toBeGreaterThan  |
    #
    # A first attempt matched only the first form and demoted three genuine kills
    # to INCONCLUSIVE — the mirror-image mistake, and one the harness caught on
    # itself by reporting them as survivors rather than quietly passing.
    #
    # Matching the FORM rather than a list of matcher names also fixes the
    # opposite error the list version had: a genuine kill using a matcher nobody
    # remembered to add (toThrow, toHaveAttribute, resolves) was silently
    # demoted to INCONCLUSIVE, quietly deflating the score.
    log="/tmp/mutant-$name.log"
    if ! grep -qE "^ *[0-9]+ failed" "$log"; then
      echo "     INCONCLUSIVE — the run errored without a test failure (see $log)"
      survived+=("$name (no test failure)")
    elif grep -qE "^[[:space:]]*(Error: )?expect\((received|locator)\)" "$log"; then
      if [ -n "${FALSE_KILLS[$name]:-}" ]; then
        echo "     KILLED, BUT FALSE — ${FALSE_KILLS[$name]}."
        echo "                   Counted separately; it is NOT evidence for that guarantee."
        false_killed+=("$name")
      else
        echo "     KILLED — an assertion failed, as it should."
        killed+=("$name")
      fi
    else
      echo "     INCONCLUSIVE — the spec failed, but NOT on an assertion (crash/timeout). See $log"
      survived+=("$name (crashed, not asserted)")
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
echo "  baseline    : GREEN"
echo "  killed      : ${#killed[@]}  ${killed[*]:-}"
if [ ${#false_killed[@]} -ne 0 ]; then
  echo "  FALSE kills : ${#false_killed[@]}  ${false_killed[*]}"
  echo "                red, but for the wrong reason — NOT counted as coverage."
  for name in "${false_killed[@]}"; do
    echo "                  - $name: ${FALSE_KILLS[$name]}"
  done
fi
echo "  survived    : ${#survived[@]}  ${survived[*]:-}"
echo "  restore     : $restore"

if [ ${#survived[@]} -ne 0 ] || [ "$restore" != "GREEN" ]; then
  echo
  echo "NOT CLEAN. A surviving mutant means that spec does not test what it claims;"
  echo "a red restore means a mutant left state behind. Report it, do not delete it."
  exit 1
fi
echo
if [ ${#false_killed[@]} -ne 0 ]; then
  echo "No survivors; baseline and restore both green — but ${#false_killed[@]} of the reds above is a"
  echo "FALSE kill and proves nothing. Real coverage is ${#killed[@]} guarantee(s), not $(( ${#killed[@]} + ${#false_killed[@]} ))."
else
  echo "All mutants killed, baseline and restore both green."
fi
