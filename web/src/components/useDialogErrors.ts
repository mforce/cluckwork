import { useCallback, useRef, useState } from "react";

export interface DialogErrors {
  /** The screen's own failure — a read, or a write not behind a dialog. */
  page: string | null;
  setPage: (text: string | null) => void;
  /** The message this dialog raised, if any. */
  forDialog: (scope: string) => string | undefined;
  clearDialog: (scope: string) => void;
  /**
   * Called when an attempt starts: clears the slot that attempt will write to
   * and nothing else, and un-mutes that scope so this attempt can report.
   * `null` means the page's.
   *
   * Call it BEFORE any client-side validation, not just before the network
   * call. A form that rejects its own input never reaches the write, so a mute
   * left behind by an earlier dismissal would still be set and `report` would
   * silently drop the message — the user presses Save and nothing at all
   * happens. Four screens hit this independently while #479 rolled out
   * (Products, Users, Stock, Inventory), which is why it is written here
   * rather than in four screen comments.
   */
  beginAttempt: (scope: string | null) => void;
  /**
   * Called when a dialog is dismissed: empties its slot, so reopening the form
   * shows no stale verdict, and mutes whatever attempt was still out, so its
   * failure is not reported against the session the user opens next.
   */
  abandon: (scope: string) => void;
  /**
   * The settle path, and the only way a screen should write a dialog's slot:
   * routes a failure to the slot its scope names, unless that attempt was
   * abandoned, in which case it lands nowhere. Safe to call more than once for
   * one attempt. `null` is the page's, and a page failure is never muted,
   * because the page does not go away.
   */
  report: (scope: string | null, text: string) => void;
}

/**
 * #479 — one error slot per place a message can appear: the page, and each
 * dialog by scope.
 *
 * Sales learned this over four review rounds (#474 → #477 → #480 → #481), one
 * per way a shared slot goes wrong. With every failure in one string:
 *
 *   • a dialog rendered whatever was in it, so a background read's failure was
 *     presented as that form's own — the message pointing at fields it had
 *     nothing to do with;
 *   • tagging the slot fixed the attribution but not the loss: the second
 *     failure erased the first, so a form went blank with nothing happening
 *     inside it and no way for the user to know why;
 *   • and clearing "the error" when anything started dropped failures the user
 *     had not dealt with yet.
 *
 * A map keyed by scope makes all three impossible rather than defended
 * against. It also removes the assumption the earlier versions rested on —
 * that only one dialog can be open — which nothing enforces (#480).
 */
export function useDialogErrors(): DialogErrors {
  const [page, setPage] = useState<string | null>(null);
  const [dialogs, setDialogs] = useState<Record<string, string>>({});
  // The scopes whose dialog was dismissed while their write was still out. A
  // ref, not state: it is read in the settle path of a request already running,
  // where a render-behind value is the wrong answer.
  const abandoned = useRef<Set<string>>(new Set());

  const clearDialog = useCallback((scope: string) => {
    setDialogs((current) => {
      // Identity is kept when there is nothing to drop, so a clear on a slot
      // that is already empty cannot cause a render.
      if (!(scope in current)) return current;
      const next = { ...current };
      delete next[scope];
      return next;
    });
  }, []);

  // Deliberately NOT returned. Writing a slot directly bypasses the mute, so a
  // screen reaching for the obvious-looking setter in a settle path would
  // reintroduce #474 with nothing failing. `report` is the only way in.
  const setDialog = useCallback((scope: string, text: string) => {
    setDialogs((current) => ({ ...current, [scope]: text }));
  }, []);

  const beginAttempt = useCallback((scope: string | null) => {
    if (scope === null) {
      setPage(null);
      return;
    }
    // Muting is per ATTEMPT, not per dialog: without this, one dismissal would
    // silence the form the user reopened and is filling in now.
    abandoned.current.delete(scope);
    clearDialog(scope);
  }, [clearDialog]);

  const abandon = useCallback((scope: string) => {
    abandoned.current.add(scope);
    clearDialog(scope);
  }, [clearDialog]);

  const report = useCallback((scope: string | null, text: string) => {
    if (scope === null) {
      setPage(text);
      return;
    }
    // The user gave up on this one, so its verdict has nowhere honest to land:
    // not on the page, which is the context-free message #474 was filed about,
    // and not in the dialog, which by now may be a second session.
    //
    // A read, never a write. Consuming the entry here was tried and reverted:
    // it makes reporting twice in one settle path — a catch plus a finally, a
    // retry wrapper, a validation throw after a caught network error — put the
    // SECOND message inside the dialog the user already dismissed, which is
    // #474 reintroduced by its own safeguard. Pruning belongs to `beginAttempt`
    // and only there, because starting an attempt is the one moment that knows
    // a new one exists.
    if (abandoned.current.has(scope)) return;
    setDialog(scope, text);
  }, [setDialog]);

  const forDialog = useCallback((scope: string) => dialogs[scope], [dialogs]);

  return { page, setPage, forDialog, clearDialog, beginAttempt, abandon, report };
}
