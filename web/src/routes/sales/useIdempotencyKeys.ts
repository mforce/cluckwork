import { useRef } from "react";
import { newId } from "../../lib/ids";

// Other idempotency keys are bound to (action, target) and rotated ONLY after the whole
// action (write + refresh) succeeds: a retry after any failure — including a
// lost response or a failed follow-up read — replays the same key, so the
// server dedupes instead of duplicating the write.
//
// Replays it, that is, when the retry sends the SAME BODY. The middleware
// hashes the body, so a retry that answers the dialog differently — a
// different void reason, or since #721 a different discount reason — is a
// different request under a used key and gets the 409 that says so, not a
// replay. That is the contract working rather than a gap: the first write
// committed, and the page recovers on its next read. Void has had this shape
// since it gained a free-text reason.
export function useIdempotencyKeys() {
  const keys = useRef(new Map<string, string>());
  const keyFor = (scope: string) => {
    const existing = keys.current.get(scope);
    if (existing) return existing;
    const fresh = newId();
    keys.current.set(scope, fresh);
    return fresh;
  };
  const clearKey = (scope: string) => keys.current.delete(scope);
  return { keyFor, clearKey };
}
