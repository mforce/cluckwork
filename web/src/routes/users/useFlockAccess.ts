import { useState } from "react";
import type { Dispatch, SetStateAction } from "react";
import { assignFlock as apiAssignFlock, listFlockAssignments, unassignFlock } from "../../api/cluckwork";
import type { Flock, FlockAssignment } from "../../api/cluckwork";
import { stepUp } from "../../api/client";
import type { PickerSnapshot } from "../../components/NamedEntityPicker";
import type { DialogAction } from "../../components/useDialogAction";
import { errText } from "../../lib/errText";

// The flock-access dialog's state and handlers, moved out of UsersPage
// (#1215): the per-worker open/close edges and the step-up-guarded
// assign/remove. The flight guard, the message slots and the idempotency
// keys stay on the page (#703) and are passed in, as do the controlled
// picker state (the page's load effect seeds it) and the proof password the
// page clears on logout (#308).
export function useFlockAccess({
  dialog,
  keyFor,
  clearKey,
  assignFlock,
  setAssignFlock,
  setAssignFlockGen,
  assignFlockSnapshot,
  setAssignPickerOpen,
  flockStepUpPassword,
  setFlockStepUpPassword,
}: {
  dialog: DialogAction<"flock-access">;
  keyFor: (scope: string) => string;
  clearKey: (scope: string) => void;
  assignFlock: Flock | null;
  setAssignFlock: Dispatch<SetStateAction<Flock | null>>;
  setAssignFlockGen: Dispatch<SetStateAction<number>>;
  assignFlockSnapshot: PickerSnapshot<Flock>;
  setAssignPickerOpen: Dispatch<SetStateAction<boolean>>;
  flockStepUpPassword: string;
  setFlockStepUpPassword: Dispatch<SetStateAction<string>>;
}) {
  const { busy, errors, run, startLoad, openDialog, dismissDialog } = dialog;

  const [openUser, setOpenUser] = useState<string | null>(null);
  const [assignments, setAssignments] = useState<FlockAssignment[]>([]);

  // #609 review — narrower than `busy`: true only while the actual
  // assign/unassign request and its post-write refresh are in flight, NOT
  // during the earlier step-up issuance wait (closing/reopening THEN is
  // already safe — see the stale-continuation tests below, which close mid
  // step-up on purpose). Gates the dialog's own closability so a write in
  // this window cannot be orphaned by a close+reopen of the SAME worker.
  const [flockWriteInFlight, setFlockWriteInFlight] = useState(false);

  // F133: flock scoping is a per-worker action, so it opens in the shared dialog
  // like the other per-row surfaces (#131) — the row button opens it, the dialog
  // closes it. Load the assignments before opening so the panel is never empty
  // mid-flight; a load failure surfaces on the page and the dialog stays shut.
  async function openAssignments(userId: string) {
    // The latest click wins (#703): a new session for this dialog now, so a
    // load still out — or an attempt from the dialog on screen, a reopen of
    // the SAME worker included (#606) — is superseded. The slot is left alone
    // until the load lands: a load is not an attempt, and a load that fails
    // reports to the page and changes nothing else. The try covers the load
    // alone: once the dialog rebinds below, this load's `current()` is over.
    const current = startLoad("flock-access");
    let list: FlockAssignment[];
    try {
      list = await listFlockAssignments(userId);
    } catch (err) {
      if (!current()) return;
      errors.setPage(errText(err)); // load failed → no dialog; surface on the page
      return;
    }
    if (!current()) return; // superseded by another open/close
    setAssignments(list);
    // Start every worker's dialog on a FRESH controlled generation, never
    // retaining the previous open's exploration or selection — open A, pick
    // fl2, close, open B, and B would otherwise still show fl2, so a
    // distracted admin could assign the wrong flock. The default is the
    // first active flock when one is loaded; until the load resolves (or
    // the account has none) it is a fresh BLANK (account-wide) — the
    // optional picker admits the blank, so Assign is only ever armed once
    // a real default exists.
    // #646 — blank, for the reason above: a role grant should not carry a
    // flock the admin never picked.
    setAssignFlock(null);
    setAssignFlockGen((g) => g + 1);
    // The SLOT's edge (the mute and the clear that `openDialog` carries)
    // only once the load actually succeeds and the dialog is about to
    // rebind: a failed load opens nothing, so nothing is cleared for it — the
    // session itself began at `startLoad` above. (Another worker's row is
    // inert behind an open dialog since #480, so a displacement reaches here
    // only after a dismissal; the test `keeps worker A's dialog and its
    // message when worker B's load fails` drives the row directly.)
    // Rebind: this ends the displaced dialog's session and drops its verdict
    // — a same-worker re-entry included, exactly as every other dialog on
    // this screen (#703).
    openDialog("flock-access");
    setOpenUser(userId);
    setFlockStepUpPassword(""); // fresh re-entry per open, including same-worker reopen
  }

  function closeAssignments() {
    setOpenUser(null);
    setFlockStepUpPassword(""); // #308 — never leave a typed proof password behind
    // A closed dialog must not keep a picker armed for a write that no dialog
    // can present: the next open issues its own fresh generation (above), but
    // until then the engine would still report canSubmit for whatever the
    // last open committed.
    setAssignPickerOpen(false);
    setAssignFlock(null);
    setAssignFlockGen((g) => g + 1);
    dismissDialog("flock-access");
  }

  async function onAssign() {
    const target = openUser;
    const selectedFlock = assignFlock;
    // US2 (T028) — canSubmit is the write-safety boundary, not the button:
    // a disabled button alone is bypassable (a stale click, a suppressed
    // render), so the handler refuses an uncommitted/exploring/unavailable
    // selection itself.
    if (!target || !selectedFlock || !assignFlockSnapshot.canSubmit || busy) return;
    // One string serves as both the pending scope and the idempotency-key
    // scope here — payload-bound either way; the dialog is named for the
    // slot and the session (#703).
    const scope = `assign:${target}:${selectedFlock.id}`;
    await run(scope, async (current) => {
      // #606/#308 — read then clear before awaiting issuance; the grant is
      // spent on this write only, never cached across assign/remove.
      const enteredPassword = flockStepUpPassword;
      setFlockStepUpPassword("");
      const stepUpToken = (await stepUp(enteredPassword)).token;
      if (!current()) return;

      setFlockWriteInFlight(true);
      try {
        await apiAssignFlock(target, selectedFlock.id, keyFor(scope), stepUpToken);
        const fresh = await listFlockAssignments(target);
        clearKey(scope);
        if (current()) setAssignments(fresh);
      } finally {
        setFlockWriteInFlight(false);
      }
    }, { dialog: "flock-access" });
  }

  async function onUnassign(a: FlockAssignment) {
    const target = openUser;
    if (!target || busy) return;
    // The KEY scope stays bound to the assignment id (the exact write being
    // retried); the PENDING scope is user:flock so the row's spinner matches
    // what the admin sees themselves removing.
    const keyScope = `unassign:${a.id}`;
    await run(`unassign:${target}:${a.flockId}`, async (current) => {
      const enteredPassword = flockStepUpPassword;
      setFlockStepUpPassword("");
      const stepUpToken = (await stepUp(enteredPassword)).token;
      if (!current()) return;

      setFlockWriteInFlight(true);
      try {
        await unassignFlock(target, a.id, keyFor(keyScope), stepUpToken);
        const fresh = await listFlockAssignments(target);
        clearKey(keyScope);
        if (current()) setAssignments(fresh);
      } finally {
        setFlockWriteInFlight(false);
      }
    }, { dialog: "flock-access" });
  }


  return {
    openUser, assignments, flockWriteInFlight,
    openAssignments, closeAssignments, onAssign, onUnassign,
  };
}
