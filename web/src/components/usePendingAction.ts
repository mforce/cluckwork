import { useCallback, useRef, useState } from "react";

// #236 — the one shared guard for every mutating action. A save that takes
// seconds must read as "still working", and a second press must not
// double-record; screens bind `busy` to their triggers and `isPending(scope)`
// to the one control that should visibly spin.
//
// Scopes are `verb` or `verb:<id>` ("create", "archive:7", "void-payment:12")
// so rows with several verbs stay distinguishable. Idempotency keys and
// refresh-before-rotate stay with the screens that were reviewed with them —
// this hook owns only the flight.
export function usePendingAction(): {
  busy: boolean;
  isPending(scope: string): boolean;
  run<T>(scope: string, action: () => Promise<T>): Promise<T | undefined>;
} {
  // The guard is a ref, not state: two clicks land in the same tick before any
  // re-render, so a state check would wave both through. The ref is set before
  // the action is invoked and cleared in `finally`, so a thrown action can
  // never wedge it shut. State rides alongside purely to re-render the UI.
  const inFlightRef = useRef(false);
  const [pendingScope, setPendingScope] = useState<string | null>(null);

  const run = useCallback(
    async <T,>(scope: string, action: () => Promise<T>): Promise<T | undefined> => {
      // Skipped, not queued, and `undefined` — callers must not read this as
      // success; screens that branch on outcome keep their boolean wrappers.
      if (inFlightRef.current) return undefined;
      inFlightRef.current = true;
      setPendingScope(scope);
      try {
        // Exceptions propagate — error rendering stays per screen.
        return await action();
      } finally {
        inFlightRef.current = false;
        setPendingScope(null);
      }
    },
    [],
  );

  const isPending = useCallback((scope: string) => pendingScope === scope, [pendingScope]);

  return { busy: pendingScope !== null, isPending, run };
}
