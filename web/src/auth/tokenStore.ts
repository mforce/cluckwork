// #145 — the access token lives ONLY in JS memory, never in localStorage/
// sessionStorage. An XSS payload therefore can't read a durable credential; the
// token's short (15-min) lifetime bounds the exposure. The refresh token is an
// HttpOnly cookie the browser attaches automatically and JS cannot read.
//
// A page reload clears this module's memory, so the session is restored by a
// silent refresh against the cookie (see client.restoreSession / AuthContext).

// Pre-#145 sessions persisted the token pair here; purged on first load so those
// users cleanly re-login (a stale access token is never trusted from storage).
const LEGACY_KEY = "cluckwork.tokens";

let accessToken: string | null = null;

export function getAccessToken(): string | null {
  return accessToken;
}

export function setAccessToken(token: string | null): void {
  accessToken = token;
}

export function clearAccessToken(): void {
  accessToken = null;
}

// #532 — the farm this TAB is bound to, tracked separately from the access
// token and deliberately NOT cleared by clearAccessToken(). It is a non-secret
// selector, persisted in sessionStorage so this tab can name its farm after a
// reload when the browser holds several farms' HttpOnly cookies. sessionStorage
// is tab-scoped and disappears when the tab closes; the access token remains
// memory-only.
//
// The cross-farm guard in client.ts compares a refreshed token's account
// against this. Deriving it from the stored token instead makes the guard a
// one-shot: refusing a foreign token tears the session down, clearing the
// token, and the next refresh then sees an empty store — the same state as a
// legitimate cold restore — and adopts the foreign farm with no comparison.
// Round 7 reproduced exactly that with two concurrent requests.
//
// Cleared only by an explicit logout (the user chose to end the session). A
// genuinely fresh tab has no binding and uses restoreSession's unbound
// bootstrap path.
const BOUND_ACCOUNT_KEY = "cluckwork.boundAccountId";
let boundAccountId: string | null = sessionStorage.getItem(BOUND_ACCOUNT_KEY);

export function getBoundAccountId(): string | null {
  return boundAccountId;
}

export function bindAccount(accountId: string | null): void {
  boundAccountId = accountId;
  if (accountId === null) sessionStorage.removeItem(BOUND_ACCOUNT_KEY);
  else sessionStorage.setItem(BOUND_ACCOUNT_KEY, accountId);
}

export function clearBoundAccount(): void {
  bindAccount(null);
}

// Remove any token left in localStorage by the pre-#145 scheme. Called once at
// startup; safe to call when storage is unavailable (private mode).
export function purgeLegacyTokens(): void {
  try {
    localStorage.removeItem(LEGACY_KEY);
  } catch {
    // storage unavailable — nothing to purge
  }
}
