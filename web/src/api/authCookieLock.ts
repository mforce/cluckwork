// #169/#364 — serialise refresh and self-password-change across tabs. The
// refresh token lives only in the
// shared HttpOnly cookie (#145), so two tabs refreshing at once each present the
// SAME cookie value; the second hits an already-rotated token, the server reads
// that as a replay and revokes the whole family — logging BOTH tabs out. The Web
// Locks API lets only one tab refresh at a time: the next tab runs its refresh
// only AFTER the first has rotated the cookie, so it presents the fresh token,
// not a replay. Server-side reuse-detection stays strict (unchanged). Browsers
// without navigator.locks (older Safari, insecure origins) degrade to per-tab
// single-flight only: no cross-tab guarantee, but never worse than before #169.
//
// Residual (a page-owned lock cannot make server rotation + cookie receipt
// atomic): if a tab is closed in the sub-second between sending a refresh and
// receiving the rotated cookie, the lock auto-releases while the cookie is still
// the old value, so the next tab can still trip reuse-detection. Narrow, but a
// full fix needs an idempotent server refresh — tracked in #176.
const AUTH_COOKIE_LOCK = "cluckwork.auth.refresh";

// Cap how long one tab may hold the cross-tab lock. fetch() has no default
// timeout, so a hung /auth/refresh would otherwise keep the lock — and every
// other tab's refresh — parked indefinitely (#169 review). On timeout we abort,
// which releases the lock; the aborting tab recovers on its next request.
export const REFRESH_TIMEOUT_MS = 15_000;

// #310/#364 — the abort handle of whichever cookie-mutating auth operation is
// currently running (if any), so logout() can cancel it outright rather than
// only waiting for the generation check below to discard its result on arrival.
export let abortInFlightAuthCookieOperation: (() => void) | null = null;

// Web Locks coordinate tabs, while this queue provides the same ordering within
// one tab and on browsers where navigator.locks is unavailable. Rejections are
// absorbed only by the tail so one failed auth write never poisons later work;
// each caller still receives its own original result.
let authCookieTail: Promise<void> = Promise.resolve();

export function withAuthCookieLock<T>(
  run: (signal: AbortSignal) => Promise<T>,
  timeoutMs?: number,
): Promise<T> {
  const attempt = () => {
    const controller = new AbortController();
    const abort = () => controller.abort();
    abortInFlightAuthCookieOperation = abort;
    // Refresh is replayable and must not starve every tab indefinitely. A
    // password change is not: aborting after the server commits can strand the
    // browser with neither the old credential nor the fresh response, so that
    // caller deliberately supplies no timeout. Explicit logout still aborts it
    // through abortInFlightAuthCookieOperation above.
    const timer = timeoutMs === undefined ? undefined : setTimeout(abort, timeoutMs);
    return run(controller.signal).finally(() => {
      if (timer !== undefined) clearTimeout(timer);
      if (abortInFlightAuthCookieOperation === abort)
        abortInFlightAuthCookieOperation = null;
    });
  };
  const queued = authCookieTail.then(() => {
    const locks: LockManager | undefined = globalThis.navigator?.locks;
    return locks ? (locks.request(AUTH_COOKIE_LOCK, attempt) as Promise<T>) : attempt();
  });
  authCookieTail = queued.then(
    () => undefined,
    () => undefined,
  );
  return queued;
}

export type HeldAuthCookieLock = Readonly<{
  generation: number;
  signal: AbortSignal;
}>;
