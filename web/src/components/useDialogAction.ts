import { errText } from "../lib/errText";
import { useDialogErrors, type DialogErrors } from "./useDialogErrors";
import { useDialogSession } from "./useDialogSession";
import { usePendingAction } from "./usePendingAction";

export interface DialogActionOptions {
  /**
   * Called as each attempt STARTS — after its slot is cleared and its session
   * claimed, before the action runs, and never for an attempt the in-flight
   * guard skipped. A page-level "Saved" message a screen clears on the next
   * attempt belongs here; it is the screen's state, so the hook does not own it.
   */
  onAttempt?: () => void;
}

export interface DialogAction {
  /** True while any action is in flight — screens inert every trigger on it. */
  busy: boolean;
  /** Whether THIS scope is the one in flight — the one control that should spin. */
  isPending: (scope: string) => boolean;
  /** The message slots — `errors.page`, `errors.forDialog(scope)` — for rendering. */
  errors: DialogErrors;
  /**
   * Runs one attempt under the in-flight guard, owning its message slot and
   * its session claim. `current()` tells the action whether the dialog session
   * that started it is still the one on screen; a scope that owns no dialog is
   * always current. Resolves the action's value, or `undefined` when the guard
   * skipped it or the action threw — never read `undefined` as success.
   */
  run: <T>(scope: string, action: (current: () => boolean) => Promise<T>) => Promise<T | undefined>;
  /** Called when a dialog OPENS: whatever session was on screen is over. */
  openDialog: (scope: string) => void;
  /**
   * Called when a dialog is DISMISSED: mutes the attempt still out, so its
   * failure lands nowhere, and ends the session, so its success cannot touch
   * whatever the user opens next.
   */
  dismissDialog: (scope: string) => void;
}

/**
 * #703 — one wrapper for every dialog write, lifted out of SalesPage after
 * #702 fixed the abandoned-success hijack there. It composes the three hooks
 * that each own one rule, in the order that rule needs:
 *
 *   • usePendingAction (#236) — the in-flight guard; a second press is skipped.
 *   • useDialogErrors (#479) — `beginAttempt` INSIDE the guarded action, so a
 *     skipped press cannot blank the verdict the dialog is showing; `report` in
 *     `catch`, which owns the "was this attempt abandoned?" decision.
 *   • useDialogSession (#477 part 2) — the session claimed BEFORE anything
 *     awaits, so it names the session the user was in when they asked, not
 *     whichever one is current when the network answers.
 *
 * What a superseded success must still do is the SCREEN's decision, per
 * statement, and it is rarely "nothing": releasing a spent idempotency key,
 * refreshing a list, confirming money — those are facts about the world and
 * run regardless. What must not run is anything that writes state the session
 * on screen now owns: closing its dialog, resetting its fields, swapping the
 * record it is about. The hook supplies `current()`; where to ask it is the
 * screen's job — the per-statement table is on #703 ("the superseded-safe question").
 *
 * `dialogScopes` names the scopes that own a dialog. A scope outside it — a
 * panel action, a row verb — routes its failure to the page and is never
 * superseded, exactly as before this hook existed. A dialog scope the screen
 * never `openDialog`s behaves the same way (#703 finding 3, deliberately open).
 */
export function useDialogAction(
  dialogScopes: readonly string[],
  options: DialogActionOptions = {},
): DialogAction {
  const { busy, isPending, run: runPending } = usePendingAction();
  const errors = useDialogErrors();
  const session = useDialogSession();

  const run = <T,>(scope: string, action: (current: () => boolean) => Promise<T>) =>
    runPending(scope, async () => {
      // The slot this attempt owns — its dialog's, or the page's. One lookup
      // decides both where the attempt clears and where its verdict lands.
      const slot = dialogScopes.includes(scope) ? scope : null;
      // Its own slot only, and un-muted: a dialog write must not wipe a page
      // failure the user has not seen, and abandoning one attempt must not
      // mute the next.
      errors.beginAttempt(slot);
      // Claimed BEFORE anything awaits. A non-dialog scope has no session to
      // be superseded by, so it is always current.
      const claimed = slot === null ? 0 : session.claim(slot);
      const current = () => slot === null || session.isCurrent(slot, claimed);
      options.onAttempt?.();
      try {
        return await action(current);
      } catch (err) {
        // Dropped outright if the user gave up on this one — `report` owns
        // that decision (#474, #479).
        errors.report(slot, errText(err));
        return undefined;
      }
    });

  const openDialog = (scope: string) => session.begin(scope);

  const dismissDialog = (scope: string) => {
    // #474 — empties the dialog's slot, so reopening shows no stale verdict,
    // and mutes the attempt still out, so its failure is not reported against
    // the session the user opens next.
    errors.abandon(scope);
    // Ends the session too: an attempt still out belongs to the one just
    // dismissed, so its success must not act on whatever replaces it (#477).
    session.begin(scope);
  };

  return { busy, isPending, errors, run, openDialog, dismissDialog };
}
