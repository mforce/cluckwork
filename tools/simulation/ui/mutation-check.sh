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
# ================== TWO PROJECTS, SO A MUTANT NAMES ITS WIDTH ==================
#
# The suite runs in two Playwright projects (#814): `chromium` at 1280 and
# `chromium-phone` at 390, partitioned by the `@phone` tag. A mutant's kill run
# therefore has to say WHICH, and `PROJECT_FOR` is that table.
#
# **Required, not defaulted, and the reason is this file's own history.**
# `EXPECT_MSG_FOR` and `FALSE_KILLS` both started out advisory here, and both
# let the score overstate itself until a review round made them mandatory. A
# defaulted project would do it a third time, and more quietly: a future
# phone mutant with no entry would run at 1280, where its spec does not even
# exist, find nothing, and be reported as a survivor — an accusation against a
# spec that was never executed. So a mutant with no `PROJECT_FOR` entry is
# skipped and counted as a survivor, exactly as an unmapped `SPEC_FOR` already
# is.
#
# `MUST_STAY_GREEN_ON` asks the second question, which the kill run cannot.
# Killing a phone spec at 390 proves the spec noticed something; it does not
# prove the something was WIDTH-SPECIFIC. A mutant that broke the app at every
# width would kill it just as dead, and the phone gate would be credited with
# coverage it has not got. So after a killed verdict, the OTHER project's whole
# suite is re-run under the same mutant and must come back GREEN. A red there is
# reported as a survivor with its own message and its own log, because the
# mutant is real but the evidence is not about phone width.
#
# Phases 1 and 3 stay UNSCOPED on purpose — no `--project` — so "the suite must
# be green" keeps meaning both of them.
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
  [phone-action-bar-under-tabbar]="specs/phone.spec.ts"
  [phone-tabbar-removed]="specs/phone.spec.ts"
  [phone-table-overflow-unclipped]="specs/phone.spec.ts"
  [phone-tabs-inert]="specs/phone.spec.ts"
  [phone-action-label-wrapped]="specs/phone.spec.ts"
)

# --- the mutant -> project map ---------------------------------------------
# Which of the two viewports the kill run happens at. REQUIRED — see the header
# for why a default here would let a phone mutant run at 1280, find nothing, and
# be reported as a survivor of a spec that never executed.
declare -A PROJECT_FOR=(
  [audit-gate-removed]="chromium"
  [users-gate-removed]="chromium"
  [flock-scope-removed]="chromium"
  [stock-pager-inert]="chromium"
  [stock-summary-broken]="chromium"
  [report-range-bound-removed]="chromium"
  [refresh-always-fails]="chromium"
  [logout-not-honoured]="chromium"
  [nav-role-gate-bypassed]="chromium"
  [payment-never-settles]="chromium"
  [export-returns-nothing]="chromium"
  [language-persist-dropped]="chromium"
  [named-entity-picker-paging-broken]="chromium"
  [a11y-inert-sweep-removed]="chromium"
  [a11y-announcer-duplicates-banner]="chromium"
  [a11y-announcer-renags-on-close]="chromium"
  [a11y-announcer-writes-transiently]="chromium"
  [a11y-announcer-writes-late]="chromium"
  [a11y-inert-never-lifted]="chromium"
  [a11y-dialog-hidden-from-tree]="chromium"
  [a11y-probe-live-off-ignored]="chromium"
  [a11y-probe-alert-control-broken]="chromium"
  [a11y-probe-alert-control-silenced]="chromium"
  [a11y-probe-off-role-dropped]="chromium"
  [phone-action-bar-under-tabbar]="chromium-phone"
  [phone-tabbar-removed]="chromium-phone"
  [phone-table-overflow-unclipped]="chromium-phone"
  [phone-tabs-inert]="chromium-phone"
  [phone-action-label-wrapped]="chromium-phone"
)

# The project whose WHOLE suite must still be GREEN under this mutant, checked
# after a killed verdict. Only the width-scoped mutants have an entry: each of
# the three claims to change nothing above 900px, and this is what asks the
# claim instead of believing the comment.
declare -A MUST_STAY_GREEN_ON=(
  [phone-action-bar-under-tabbar]="chromium"
  [phone-tabbar-removed]="chromium"
  [phone-table-overflow-unclipped]="chromium"
  [phone-tabs-inert]="chromium"
  [phone-action-label-wrapped]="chromium"
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
  [phone-action-bar-under-tabbar]="action bar stays clear of the tab bar"
  [phone-tabbar-removed]="the tab bar is the navigation at this width"
  [phone-table-overflow-unclipped]="no walked screen overflows"
  [phone-tabs-inert]="the tab bar is the navigation at this width"
  [phone-action-label-wrapped]="no action control is taller than it is wide"
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
#
# `nav-role-gate-bypassed`'s entry was RE-OBSERVED for #814, not translated by
# hand. It used to declare `getByRole('complementary')` — the sidebar landmark
# `signIn` asserted on. `signIn` now asserts `main#main-content`, so the old
# string can no longer appear anywhere, and leaving it would have turned a known
# false kill into an unexplained WRONG ASSERTION. Run, read, paste: the observed
# line is `locator('main#main-content')`, which says the same thing the header
# does — this mutant still dies inside sign-in, still proves nothing about the
# nav gate, and is still counted as a false kill rather than as coverage.
#
# The three phone entries were observed the same way. Two of them carry a custom
# message; `phone-table-overflow-unclipped` declares FOUR lines, one per route it
# breaks, because the softness of that walk is itself the claim — a hard
# assertion would stop at /sales and report a quarter of the damage, so requiring
# all four is what keeps `expect.soft` there honest. /daily-entry and /stock are
# deliberately absent: neither renders a wide data table, and both stayed at
# exactly 390 under the mutant.
#
# `phone-action-label-wrapped` declares ONE line where that walk declares four,
# and the difference is not laziness. The route walk's softness is its claim —
# a regression hits some screens and not others, so requiring every route keeps
# it honest. The two save buttons share a single flex track, so whatever
# reshapes one reshapes the other; there is no per-control claim to pin. The
# declared fragment carries no measurements on purpose: the observed line names
# 170.6x217.2, and pinning that would turn a font-metric shift into a WRONG
# ASSERTION against a mutant that worked.
declare -A EXPECT_MSG_FOR=(
  [audit-gate-removed]="/audit rendered no error for a ReadOnly user"
  [users-gate-removed]="/users rendered no error for a ReadOnly user"
  [flock-scope-removed]="the unassigned-flock write was NOT refused"
  [stock-pager-inert]="getByRole('button', { name: 'history', exact: true })"
  [stock-summary-broken]="getByText('Could not load.')"
  [report-range-bound-removed]="getByRole('button', { name: 'retry' })"
  [refresh-always-fails]="the silent refresh itself failed"
  [logout-not-honoured]="a live refresh cookie survived the logout"
  [nav-role-gate-bypassed]="locator('main#main-content')"
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
  [phone-action-bar-under-tabbar]="the daily-entry action bar overlaps the tab bar — its Submit and Save buttons are under it"
  [phone-tabbar-removed]="there is no tab bar at phone width, so nothing can be navigated to"
  [phone-tabs-inert]="a tap at the centre of the Sales tab does not land on it"
  [phone-action-label-wrapped]="at phone width — taller than it is wide, so its pill clamps into an ellipse and the label leaves its background"
  [phone-table-overflow-unclipped]="/sales scrolls sideways at phone width
/customers scrolls sideways at phone width
/flocks scrolls sideways at phone width
/history scrolls sideways at phone width"
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
           a11y-probe-off-role-dropped
           phone-action-bar-under-tabbar phone-tabbar-removed
           phone-table-overflow-unclipped phone-action-label-wrapped
           phone-tabs-inert)
fi

rule() { printf '\n%s\n' "────────────────────────────────────────────────────────────────────────"; }

# --- phase 1: baseline -----------------------------------------------------
rule
echo "PHASE 1/3 — BASELINE (the suite must be GREEN before anything is mutated)"
rule
# UNSCOPED — no `--project`, so this runs BOTH the desktop and the phone
# project. A baseline scoped to one width would leave the other one's specs
# unproven before the run that is about to accuse them.
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
  project="${PROJECT_FOR[$name]:-}"
  if [ -z "$spec" ]; then
    echo "  ?? $name — no spec mapped in this script; skipping (fix SPEC_FOR)"
    survived+=("$name (unmapped)")
    continue
  fi
  # A width-scoped mutant MUST declare the other width's cross-check. Without
  # this, deleting a MUST_STAY_GREEN_ON row silently downgrades the run to an
  # ordinary kill — the verification becomes optional exactly the way
  # EXPECT_MSG_FOR and FALSE_KILLS each did before a review round made them
  # mandatory, and the header above claims this file no longer does that.
  # Found by an adversarial pass on #814, which traced the missing-entry branch
  # straight into `killed+=`.
  if [ "${PROJECT_FOR[$name]:-}" = "chromium-phone" ] && [ -z "${MUST_STAY_GREEN_ON[$name]:-}" ]; then
    echo "  ?? $name — runs at chromium-phone but declares no MUST_STAY_GREEN_ON;"
    echo "     skipping (a phone mutant with no cross-check cannot show it is width-specific)"
    survived+=("$name (no width cross-check)")
    continue
  fi
  if [ -z "$project" ]; then
    # Same treatment as an unmapped spec, and for a sharper reason: guessing a
    # project would run a phone mutant at 1280 against a spec the grep does not
    # even select there, then blame the spec. See the header.
    echo "  ?? $name — no project mapped in this script; skipping (fix PROJECT_FOR)"
    survived+=("$name (no project)")
    continue
  fi

  printf '  .. %-30s -> %s [%s]\n' "$name" "$spec" "$project"
  if CLUCKWORK_E2E_MUTANT="$name" npx playwright test "$spec" -g "$pattern" \
       --project "$project" --reporter=line > "/tmp/mutant-$name.log" 2>&1; then
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
        # The kill is real. One question remains, and only for a mutant that
        # claims a WIDTH: did it break the other project too? If it did, its red
        # says "this mutant breaks the app" rather than "this mutant breaks the
        # app at 390 and the phone spec is what notices" — and the phone gate
        # gets credited with coverage it has not earned. See the header.
        other="${MUST_STAY_GREEN_ON[$name]:-}"
        if [ -n "$other" ]; then
          cross_log="/tmp/mutant-$name.must-stay-green-on-$other.log"
          echo "     .. checking it is width-specific: whole $other suite must stay GREEN"
          if CLUCKWORK_E2E_MUTANT="$name" npx playwright test --project "$other" \
               --reporter=line > "$cross_log" 2>&1; then
            echo "     KILLED — an assertion failed, as it should, and $other stayed green."
            killed+=("$name")
          else
            echo "     NOT WIDTH-SPECIFIC — it killed its spec, but it also turned $other RED."
            echo "                   The kill proves the app broke, not that the phone gate"
            echo "                   noticed something only phone width can show. See $cross_log"
            survived+=("$name (not width-specific)")
          fi
        else
          echo "     KILLED — an assertion failed, as it should."
          killed+=("$name")
        fi
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
# UNSCOPED, same as the baseline — the two have to measure the same thing or
# the comparison between them means nothing.
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
