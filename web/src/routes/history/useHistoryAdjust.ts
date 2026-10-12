import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { adjustDailyEntry, getDailyEntry } from "../../api/cluckwork";
import type { DailyEntry, EggGrade } from "../../api/cluckwork";
import { ApiError } from "../../api/client";
import type { DialogAction } from "../../components/useDialogAction";
import { armedState, gradingState } from "../../lib/grading";
import i18n from "../../i18n";
import type { PagedEntries } from "./historyUi";

export function useHistoryAdjust({
  grades, entries, keyFor, settleKey, setMessage, setPageError, dialog, correctable,
}: {
  grades: EggGrade[];
  entries: PagedEntries;
  keyFor: (scope: string) => string;
  settleKey: (scope: string, err?: unknown) => void;
  setMessage: (message: string | null) => void;
  setPageError: (error: string | null) => void;
  dialog: DialogAction<"adjust">;
  correctable: (e: DailyEntry) => boolean;
}) {
  const { busy, run, errors, openDialog, dismissDialog } = dialog;

  // adjust panel: one entry at a time; the version it was loaded with rides
  // along so a concurrent correction surfaces as a 409, not an overwrite.
  const [adjusting, setAdjusting] = useState<DailyEntry | null>(null);
  const [total, setTotal] = useState(0);
  const [cracked, setCracked] = useState(0);
  const [dirty, setDirty] = useState(0);
  const [discarded, setDiscarded] = useState(0);
  const [mortality, setMortality] = useState(0);
  const [reason, setReason] = useState("");
  const [lineQty, setLineQty] = useState<Record<string, number>>({});
  // #443 — see DailyEntryPage's identical `gradeQtyRef` comment: setLine reads
  // this instead of the `lineQty` closure so a hold-to-repeat burst (each
  // tick against the SAME setLine closure captured at press-time) sees every
  // earlier tick's write.
  const lineQtyRef = useRef(lineQty);
  lineQtyRef.current = lineQty;
  // F134's remainder gesture, mirrored here: hand everything still unaccounted
  // for to one grade. A recount is lopsided the same way a capture is.
  const [assigning, setAssigning] = useState(false);

  // Dismissal is one of the two session edges (#703): it mutes the attempt
  // still out, so a late failure is not reported against a session the user
  // reopened, and ends the session, so a late success cannot act on it.
  const closeAdjust = () => { setAdjusting(null); dismissDialog("adjust"); };

  function startAdjust(e: DailyEntry) {
    // Opening is the other edge. A fresh open, or a different entry's adjust
    // DISPLACING this session without onClose — reachable behind the backdrop
    // via a screen reader's virtual cursor (#480; pi review of #491) — ends
    // whatever session was on screen. Same-id re-entry is deliberately spared:
    // the session is still about this entry, so its failed save's verdict must
    // survive the reseed (pinned by "keeps a save failure visible after
    // re-entering the same entry's adjust"). The 409 rebind also re-enters
    // same-id, on purpose: it reopens the SAME session with the winner's
    // values and re-arms its slot itself before reporting.
    if (adjusting === null || adjusting.id !== e.id) openDialog("adjust");
    setAdjusting(e);
    setTotal(e.totalEggs);
    setCracked(e.crackedEggs);
    setDirty(e.dirtyEggs);
    setDiscarded(e.discardedEggs);
    setMortality(e.mortalityCount);
    setReason("");
    setLineQty(Object.fromEntries(e.grades.map((g) => [g.eggGradeId, g.quantity])));
    // A different entry (or the server's newer copy after a 409) is a different
    // day: staying armed over it would be a gesture the user never aimed there.
    setAssigning(false);
  }

  // The entry's own lines (possibly deactivated grades — still correctable)
  // plus the active saleable catalog for adding a missed grade.
  //
  // #396 — a counter-fed grade is never offered for adding. It is excluded from
  // the CATALOG half only, not from the entry's own lines: an existing line must
  // stay correctable whatever it names, and a condition grade cannot legitimately
  // be on an entry anyway (ConditionGradeGuard refuses it server-side). Adding
  // one here would ask the server for a second lot on a grade its counter
  // already produced, and be rejected.
  function panelGrades(e: DailyEntry): EggGrade[] {
    const onEntry = new Set(e.grades.map((g) => g.eggGradeId));
    return grades.filter((g) =>
      onEntry.has(g.id) || (g.active && g.isSaleable && g.dailyEntryKind === "Manual"));
  }

  // #394: an adjustment has no draft state of its own — it replaces the
  // entry's official numbers outright, so it is held to the same exact
  // reconciliation Daily Entry's submit uses. Both screens read that rule out
  // of lib/grading, so the Save button's disabled state, the readouts in the
  // dialog and the submit-time guard below can never disagree.
  const gradesSum = Object.values(lineQty).reduce((sum, q) => sum + (q || 0), 0);
  const grading = gradingState({ totalEggs: total, cracked, dirty, discarded, gradesSum });
  const { losses, sellable, remaining, lossesExceedTotal } = grading;
  const gradesReconciled = grading.reconciled;
  const canAssign = remaining > 0;
  // DERIVED, not the raw flag: there is nothing left to hand out the instant
  // the day reconciles, and that can happen by typing a grade rather than by
  // using the gesture — which self-disarms. Reading `assigning` directly left
  // the rows armed (and Save enabled) for the render between that keystroke
  // and the effect below, so "armed" and "saveable" overlapped by one frame
  // instead of being mutually exclusive. Derived, they cannot (codex round 2).
  // The derivation itself lives in lib/grading, where it is asserted directly.
  const armed = armedState(assigning, canAssign);
  // The effect still clears the stale flag, so typing back DOWN to a remainder
  // does not silently re-arm rows the user disarmed by walking away from it.
  useEffect(() => {
    if (!canAssign && assigning) setAssigning(false);
  }, [canAssign, assigning]);

  // #443 — mirrors DailyEntryPage's setGrade: a grade edit that would push
  // the graded sum past what the total currently allows raises the total to
  // fit instead of being capped (the removed `max=` did that).
  //
  // Gated on `newSum > prevSum` (codex review of #449) — not just "still over
  // the total" — so that correcting a grade back DOWN with − after the total
  // was trimmed on step 1 doesn't ratchet the total back up on every
  // decrement; only an edit that itself increases the graded sum bumps it.
  const setLine = (gradeId: string) => (next: number | ((prev: number) => number)) => {
    const current = lineQtyRef.current;
    const prevSum = Object.values(current).reduce((a, b) => a + (b || 0), 0);
    const updated = {
      ...current,
      [gradeId]: typeof next === "function" ? next(current[gradeId] ?? 0) : next,
    };
    lineQtyRef.current = updated;
    setLineQty(updated);
    const newSum = Object.values(updated).reduce((a, b) => a + (b || 0), 0);
    if (newSum > prevSum) setTotal((t) => Math.max(t, newSum + losses));
  };

  // Hand the whole remainder to one grade line.
  function assignRest(gradeId: string) {
    if (remaining <= 0) return;
    setLine(gradeId)((prev) => prev + remaining);
    setAssigning(false);
  }

  // On a 409 the correction lost a race. Reload, then re-bind the panel to
  // the fresh entry with the OTHER admin's values in every field — keeping
  // this admin's typed numbers could silently clobber a grade line the
  // winner just added (pi review of PR #81). Only the reason survives; if
  // the entry is no longer correctable (voided meanwhile), close the panel.
  async function rebindAfterConflict(entryId: string) {
    // This path can REOPEN a panel the user dismissed while the 409's re-read
    // was still out, and that dismissal muted the scope — so both messages
    // below would be dropped and the panel would spring back open showing
    // another admin's numbers with no word of why. Found independently by two
    // reviewers of #491.
    //
    // Un-muting is not a hole in #474. That rule drops the verdict of an
    // attempt the USER walked away from. This is the app reopening the dialog
    // uninvited and saying something new — that the entry was rebound — which
    // is the one thing that makes the reappearance intelligible.
    //
    // It has to happen AFTER the re-read, not before: the dismissal that mutes
    // the scope arrives while that request is out, so un-muting up here would
    // simply be undone. Each un-mute therefore sits with the report it enables.
    try {
      // No reload here either: this runs from the adjust submit's 409 catch,
      // by which point runWrite has already re-read. A duplicate replacement
      // read that failed would clear the rows that one just loaded (#469).
      const fresh = await getDailyEntry(entryId);
      if (correctable(fresh)) {
        const keptReason = reason;
        startAdjust(fresh);
        setReason(keptReason);
        // The dialog is open — either still, or because startAdjust just put it
        // back — and rebound to the winner's numbers, so this is its message.
        errors.beginAttempt("adjust");
        errors.report("adjust", i18n.t("history:conflictRebindMessage"));
      } else {
        // The panel closes here, not through Cancel — nothing left to abandon,
        // and the message belongs to the page it lands on.
        setAdjusting(null);
        // .toLowerCase() on the raw wire status is locale-fragile (it only ever
        // reads correctly in English) — tracked as a native-pass deferral
        // (#182); interpolating the lowered value keeps this task
        // text-preserving without solving the shared-component lowercase
        // problem here.
        setPageError(i18n.t("history:nothingToAdjustMessage", { status: fresh.status.toLowerCase() }));
      }
    } catch {
      // The re-read itself failed. Unlike the success path this does NOT
      // reopen the panel, so there is no un-mute here: if the user kept the
      // panel open no mute exists (the submit cleared it) and this report
      // lands on the stale numbers it explains; if they dismissed, the mute
      // is exactly what drops a message that would otherwise park in the
      // closed slot and replay in the next adjust dialog (pi review of #491).
      // A displacing open of a DIFFERENT entry's adjust is not a live
      // alternative here — every adjust trigger is `disabled={busy}`, and
      // `busy` covers this whole call (it runs inside the submit's own
      // `run()`), so the only action reachable while this is out is a
      // dismissal (adversarial review of #491).
      errors.report("adjust", i18n.t("history:conflictRebindFailedMessage"));
    }
  }

  async function onAdjustSubmit(ev: FormEvent) {
    ev.preventDefault();
    if (!adjusting || busy) return;
    errors.beginAttempt("adjust");
    setMessage(null);
    const scope = `adjust:${adjusting.id}`;
    const lines = Object.entries(lineQty)
      .filter(([, q]) => q > 0)
      .map(([eggGradeId, quantity]) => ({ eggGradeId, quantity }));
    // Mirror the server's exact-reconciliation rule (#394) for an instant
    // message; the API remains the authority. The Save button is already
    // disabled while this is false (see gradesReconciled above), so this is
    // defense in depth rather than the primary gate. Validated before the
    // flight opens: a rejected form never reads as busy.
    if (!gradesReconciled) {
      errors.report("adjust", i18n.t("history:gradesMustReconcileMessage"));
      return;
    }
    // The run scope is the dialog's; the idempotency key stays per entry.
    await run("adjust", async (current) => {
      try {
        // The list ticket is claimed before the PUT, so a filter change made
        // while it is in flight keeps the view and this refresh stands down
        // rather than repainting the old filter's rows (#469).
        await entries.runWrite(async () => {
          await adjustDailyEntry(adjusting.id, {
            version: adjusting.version,
            totalEggs: total,
            crackedEggs: cracked,
            dirtyEggs: dirty,
            discardedEggs: discarded,
            mortalityCount: mortality,
            reason: reason.trim(),
            grades: lines, // [] explicitly clears all lines
          }, keyFor(scope));
          settleKey(scope);
          // The adjust's superseded case is unreachable through the UI (the
          // row's adjust button is `disabled={busy}`); the gate stays for
          // INV-1 and against a future change that enables the button — pinned
          // by the wiring test "a successful adjust closes its dialog and
          // refreshes the list". The write, the key settle above and the
          // refresh runWrite does next are facts about the world (#703).
          if (!current()) return;
          setAdjusting(null);
          setMessage(i18n.t("history:entryAdjustedMessage"));
        });
      } catch (err) {
        settleKey(scope, err);
        if (err instanceof ApiError && err.status === 409) {
          await rebindAfterConflict(adjusting.id);
        } else {
          // Rethrown for the hook to report into the "adjust" slot — it owns
          // the "was this attempt abandoned?" decision (#474/#479).
          throw err;
        }
      }
    });
  }

  return {
    adjusting, setAdjusting, closeAdjust, startAdjust, onAdjustSubmit, panelGrades,
    setLine, assignRest, lineQty, total, setTotal, cracked, setCracked, dirty, setDirty,
    discarded, setDiscarded, mortality, setMortality, reason, setReason, armed, canAssign,
    remaining, grading, gradesSum, gradesReconciled, lossesExceedTotal, sellable, setAssigning,
  };
}
