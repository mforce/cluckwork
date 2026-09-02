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
  [stock-pager-inert]="specs/readonly.spec.ts"
  [stock-summary-broken]="specs/owner.spec.ts"
  [report-range-bound-removed]="specs/reports-range.spec.ts"
  [refresh-always-fails]="specs/session-refresh.spec.ts"
  [logout-not-honoured]="specs/session-races.spec.ts"
  [nav-role-gate-bypassed]="specs/readonly.spec.ts"
  [payment-never-settles]="specs/sales.spec.ts"
  [export-returns-nothing]="specs/owner.spec.ts"
  [language-persist-dropped]="specs/i18n.spec.ts"
  [named-entity-picker-paging-broken]="specs/named-entity-picker.spec.ts"
  [a11y-inert-sweep-removed]="specs/a11y-live-regions.spec.ts"
  [a11y-announcer-duplicates-banner]="specs/a11y-live-regions.spec.ts"
  [a11y-announcer-renags-on-close]="specs/a11y-live-regions.spec.ts"
  [a11y-announcer-writes-transiently]="specs/a11y-live-regions.spec.ts"
  [a11y-announcer-writes-late]="specs/a11y-live-regions.spec.ts"
  [a11y-inert-never-lifted]="specs/a11y-live-regions.spec.ts"
  [a11y-dialog-hidden-from-tree]="specs/a11y-live-regions.spec.ts"
  [a11y-probe-live-off-ignored]="specs/a11y-live-regions.spec.ts"
  [a11y-probe-alert-control-broken]="specs/a11y-live-regions.spec.ts"
  [a11y-probe-alert-control-silenced]="specs/a11y-live-regions.spec.ts"
  [a11y-probe-off-role-dropped]="specs/a11y-live-regions.spec.ts"
)

# The third test in a11y-live-regions.spec.ts (recorded browser facts) has no
# mutant of its OWN, and that is correct: it records what Chromium does for two
# designs #501 has not taken, and no mutant of this app can change Chromium's
# mind. But it is not unmutated either — its FACT 2 opens with a precondition on
# product behaviour (the injected probe IS inerted by the sweep), so
# `a11y-inert-sweep-removed`'s GREP_FOR runs BOTH tests and both must go red.
#
# This note has now been wrong twice, in opposite directions, which is worth
# recording as the pattern rather than just fixing:
#   1. It claimed "no mutant of this app can break it" — false; the precondition
#      above breaks under the inert mutant.
#   2. Corrected to say it "goes red under that mutant" — also false at the
#      time, because GREP_FOR selected only the first test, so the harness never
#      executed it. A true statement about code that never runs is not coverage.
#   3. Corrected again by widening GREP_FOR so both tests run — still not
#      enough on its own, because one failure ends the run and the second test's
#      precondition could quietly stop failing (codex round 2).
# None of the three was caught by the harness. It is now: EXPECT_MSG_FOR lists
# the browser-facts precondition as one of the messages `a11y-inert-sweep-removed`
# must produce, so the claim in this comment is checked on every run instead of
# being trusted. Prose asserting coverage is not coverage — that is the whole
# lesson of this paragraph's three revisions.

declare -A GREP_FOR=(
  [audit-gate-removed]="direct link to /audit"
  [users-gate-removed]="direct link to /users"
  [flock-scope-removed]="is refused a daily entry"
  [stock-pager-inert]="pages a deep grade"
  [stock-summary-broken]="dashboard shows real production"
  [report-range-bound-removed]="refuses one day beyond"
  [refresh-always-fails]="forces a 401"
  [logout-not-honoured]="logout during an in-flight refresh"
  [nav-role-gate-bypassed]="is not offered the destinations"
  [payment-never-settles]="takes an order from new customer"
  [export-returns-nothing]="export downloads a real file"
  [language-persist-dropped]="renders that language across the shell"
  [named-entity-picker-paging-broken]="reaches and commits the page-two sentinel through paging"
  [a11y-inert-sweep-removed]="leave the accessibility tree|recorded browser facts"
  [a11y-announcer-duplicates-banner]="standing farm warning"
  [a11y-announcer-renags-on-close]="standing farm warning"
  [a11y-announcer-writes-transiently]="standing farm warning"
  [a11y-announcer-writes-late]="standing farm warning"
  [a11y-inert-never-lifted]="leave the accessibility tree"
  [a11y-dialog-hidden-from-tree]="leave the accessibility tree"
  [a11y-probe-live-off-ignored]="recorded browser facts"
  [a11y-probe-alert-control-broken]="recorded browser facts"
  [a11y-probe-alert-control-silenced]="recorded browser facts"
  [a11y-probe-off-role-dropped]="recorded browser facts"
)

# Mutants whose RED is known not to prove the guarantee they name. See the header.
declare -A FALSE_KILLS=(
  [nav-role-gate-bypassed]="the server rejects the forged token, so sign-in fails before the nav assertion runs"
)

# The assertion each mutant must die ON. One substring per line; EVERY line must
# appear in the run's log or the kill does not count.
#
# **Required, not optional.** The first version made this opt-in and populated
# only the a11y mutants. That still counted the other twelve as coverage on the
# strength of "some assertion failed" — the exact thing this table exists to
# stop — while the headline presented them as verified (codex round 2 on #504).
# A mutant with no entry is now reported UNVERIFIED and kept out of the killed
# count.
#
# Every line below was COPIED FROM AN OBSERVED RUN, never guessed. Three mutants
# trip assertions that carry no custom message, so the distinctive part is
# Playwright's locator line instead; that is weaker, and it is the honest limit
# of this technique rather than a reason to skip them.
#
# Multi-line entries exist because one mutant can be required to break several
# assertions: the two inert mutants must fail for BOTH announcers (they are
# judged with expect.soft precisely so both reach the log), and
# a11y-inert-sweep-removed must additionally fail the browser-facts precondition
# its GREP_FOR now runs.
declare -A EXPECT_MSG_FOR=(
  [audit-gate-removed]="/audit rendered no error for a ReadOnly user"
  [users-gate-removed]="/users rendered no error for a ReadOnly user"
  [flock-scope-removed]="the unassigned-flock write was NOT refused"
  [stock-pager-inert]="getByRole('button', { name: 'history', exact: true })"
  [stock-summary-broken]="fell back to \"—\" (its fetch failed)"
  [report-range-bound-removed]="getByRole('button', { name: 'retry' })"
  [refresh-always-fails]="the silent refresh itself failed"
  [logout-not-honoured]="a live refresh cookie survived the logout"
  [nav-role-gate-bypassed]="getByRole('complementary')"
  [payment-never-settles]="so the payment did not settle the balance"
  [export-returns-nothing]="the export downloaded 0 bytes"
  [language-persist-dropped]="the es preference did not survive a reload"
  [named-entity-picker-paging-broken]="the flock page-two sentinel never appeared after keyboard-paging to the loaded end"
  [a11y-inert-sweep-removed]="main.content > p.sr-only[aria-live=\"assertive\"] is still exposed to assistive technology with a dialog open
#root > p.sr-only[aria-live=\"polite\"] is still exposed to assistive technology with a dialog open
the injected probe is a body child but the modal sweep did not inert it"
  [a11y-inert-never-lifted]="main.content > p.sr-only[aria-live=\"assertive\"] never returned to the accessibility tree
#root > p.sr-only[aria-live=\"polite\"] never returned to the accessibility tree"
  [a11y-announcer-duplicates-banner]="duplicated a warning the visible banner already made"
  [a11y-announcer-renags-on-close]="re-announced a standing warning after dialog cycle"
  [a11y-announcer-writes-transiently]="was written to during the dialog cycles"
  [a11y-announcer-writes-late]="was written to during the dialog cycles"
  [a11y-dialog-hidden-from-tree]="the dialog's own controls are not exposed either"
  [a11y-probe-live-off-ignored]="SIDE 4 — aria-live=\"off\" no longer suppresses"
  [a11y-probe-alert-control-broken]="SIDE 1 — an explicit role=alert stopped resolving to alert
SIDE 2 — role=alert no longer carries implicit assertive politeness"
  [a11y-probe-alert-control-silenced]="SIDE 2 — role=alert no longer carries implicit assertive politeness"
  [a11y-probe-off-role-dropped]="SIDE 3 — the off probe stopped resolving to alert"
)

MUTANTS=("$@")
if [ ${#MUTANTS[@]} -eq 0 ]; then
  MUTANTS=(audit-gate-removed users-gate-removed flock-scope-removed
           stock-pager-inert stock-summary-broken report-range-bound-removed
           refresh-always-fails logout-not-honoured
           nav-role-gate-bypassed payment-never-settles export-returns-nothing
           language-persist-dropped named-entity-picker-paging-broken
           a11y-inert-sweep-removed a11y-announcer-duplicates-banner
           a11y-announcer-renags-on-close a11y-announcer-writes-transiently
           a11y-announcer-writes-late a11y-inert-never-lifted
           a11y-dialog-hidden-from-tree a11y-probe-live-off-ignored
           a11y-probe-alert-control-broken a11y-probe-alert-control-silenced
           a11y-probe-off-role-dropped)
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

killed=(); survived=(); false_killed=(); unverified=()
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
      # An assertion failed — but WHICH one? Three times running, a mutant on
      # PR #504 died at an assertion EARLIER than the one it names, leaving the
      # assertion it was written for uncovered while the run printed a clean
      # kill: an `inert` poll used as a settling signal, an announcer mutant
      # that fired before the loop below it, and a precondition the `-g` filter
      # never executed. Reviewers caught all three; the harness caught none,
      # because "something failed" was the only question it asked.
      #
      # So every mutant DECLARES the text it must die on, and the kill counts
      # only if the log contains all of it. A first version made this opt-in and
      # filled in the a11y mutants alone; that still counted the other twelve on
      # "something failed", and the headline still called them coverage (codex
      # round 2). A mutant with no declaration is now UNVERIFIED, not killed.
      want="${EXPECT_MSG_FOR[$name]:-}"
      missing=""
      if [ -n "$want" ]; then
        while IFS= read -r line; do
          [ -z "$line" ] && continue
          grep -qF -- "$line" "$log" || missing+="                     - ${line}"$'\n'
        done <<< "$want"
      fi
      if [ -z "$want" ]; then
        echo "     UNVERIFIED — no expected assertion declared, so this red is not evidence."
        echo "                   Run it, read the failure, add it to EXPECT_MSG_FOR."
        unverified+=("$name")
      elif [ -n "$missing" ]; then
        echo "     WRONG ASSERTION — it died, but not on every assertion it names."
        echo "                   never appeared in the log:"
        printf '%s' "$missing"
        echo "                   The guarantee in its name is NOT covered. See $log"
        survived+=("$name (killed at the wrong assertion)")
      elif [ -n "${FALSE_KILLS[$name]:-}" ]; then
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
if [ ${#unverified[@]} -gt 0 ]; then
  echo "  UNVERIFIED  : ${#unverified[@]}  ${unverified[*]}"
  echo "                red, but no declared assertion — NOT counted as coverage."
fi
if [ ${#false_killed[@]} -ne 0 ]; then
  echo "  FALSE kills : ${#false_killed[@]}  ${false_killed[*]}"
  echo "                red, but for the wrong reason — NOT counted as coverage."
  for name in "${false_killed[@]}"; do
    echo "                  - $name: ${FALSE_KILLS[$name]}"
  done
fi
echo "  survived    : ${#survived[@]}  ${survived[*]:-}"
echo "  restore     : $restore"

if [ ${#survived[@]} -ne 0 ] || [ ${#unverified[@]} -ne 0 ] || [ "$restore" != "GREEN" ]; then
  echo
  echo "NOT CLEAN. A surviving mutant means that spec does not test what it claims;"
  echo "an UNVERIFIED one means nobody knows WHICH assertion it killed, which is the same"
  echo "problem wearing a friendlier word; a red restore means a mutant left state behind."
  echo "Report it, do not delete it."
  # UNVERIFIED belongs in this condition (codex round 4 on #504). Without it the
  # run printed the warning and exited 0, which made the required-declaration
  # contract advisory: a newly added mutant could contribute no verified
  # coverage at all while `npm run mutation` stayed green. A guard that reports
  # a problem and then passes is precisely the failure this script exists to
  # prevent, and it had just been reintroduced one section above.
  exit 1
fi
echo
if [ ${#false_killed[@]} -ne 0 ]; then
  echo "No survivors; baseline and restore both green — but ${#false_killed[@]} of the reds above is a"
  echo "FALSE kill and proves nothing. Real coverage is ${#killed[@]} guarantee(s), not $(( ${#killed[@]} + ${#false_killed[@]} ))."
else
  echo "All mutants killed, baseline and restore both green."
fi
