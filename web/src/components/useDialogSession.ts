import { useCallback, useRef } from "react";

export interface DialogSession {
  /**
   * Starts a new session for this dialog. Called when it OPENS and when it is
   * DISMISSED — both end whatever session was on screen, and an attempt still
   * in flight belongs to the one that ended.
   */
  begin: (scope: string) => void;
  /**
   * Claims the session an attempt belongs to. Call it BEFORE the first await,
   * so the claim records the session the user was actually in when they asked
   * — not whichever one happens to be current when the network answers.
   */
  claim: (scope: string) => number;
  /** Whether a claimed session is still the one on screen. */
  isCurrent: (scope: string, claimed: number) => boolean;
}

/**
 * #477 part 2 — a per-session generation for dialogs, so a superseded attempt's
 * SUCCESS cannot touch the session that replaced it.
 *
 * `useDialogErrors` already stops an abandoned attempt reporting a failure. It
 * does nothing about the other direction, which is worse because it destroys
 * work rather than withholding a message: on Sales, opening **New order**,
 * submitting, cancelling, and reopening left the first POST in flight — and
 * when it succeeded its side effects ran unconditionally, swapping the order
 * panel to the abandoned attempt's order and force-closing the dialog the user
 * was typing customer B's details into.
 *
 * The rule is the one `usePagedList` already applies to reads: claim a
 * monotonic ticket before anything awaits, and let a superseded settle do
 * nothing. Here the ticket identifies a *dialog session* rather than a load.
 *
 * **What a superseded success must still do is the screen's decision, not this
 * hook's, and it is rarely "nothing".** The write happened. Anything that
 * records that fact — releasing a spent idempotency key, refreshing a ledger —
 * has to run regardless of who is watching, or the next attempt reuses a key
 * the server has already answered and silently replays the abandoned write.
 * What must NOT run is anything that writes state belonging to the session on
 * screen now: closing its dialog, resetting its fields, swapping the record it
 * is about.
 *
 * A ref, not state: it is read in the settle path of a request already running,
 * where a render-behind value is the wrong answer — the same reason
 * `useDialogErrors` keeps its abandoned set in one.
 */
export function useDialogSession(): DialogSession {
  // Absent means "never opened", which claims as 0 and stays current until
  // something begins a session. A dialog whose screen forgets to call `begin`
  // therefore behaves exactly as it did before this hook existed, rather than
  // silently gating every success off.
  const sessions = useRef<Map<string, number>>(new Map());

  const begin = useCallback((scope: string) => {
    sessions.current.set(scope, (sessions.current.get(scope) ?? 0) + 1);
  }, []);

  const claim = useCallback((scope: string) => sessions.current.get(scope) ?? 0, []);

  const isCurrent = useCallback(
    (scope: string, claimed: number) => (sessions.current.get(scope) ?? 0) === claimed,
    [],
  );

  return { begin, claim, isCurrent };
}
