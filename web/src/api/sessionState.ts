import { raw, CSRF_HEADER, ACCOUNT_HEADER } from "./request";
import { accountIdFromToken } from "../auth/claims";

// Called when the session is unrecoverable (refresh failed). AuthContext wires
// this to drop React auth state and bounce to /login.
export let onUnauthenticated: ((title?: string) => void) | null = null;
export function setOnUnauthenticated(cb: ((title?: string) => void) | null): void {
  onUnauthenticated = cb;
}

// Called whenever a new token pair is saved (login or transparent refresh).
// AuthContext re-derives the role from the fresh access token so a demotion
// or promotion shows in the UI within one token lifetime, not at next reload.
export let onTokensChanged: (() => void) | null = null;
export function setOnTokensChanged(cb: (() => void) | null): void {
  onTokensChanged = cb;
}

export class StaleSessionError extends Error {
  constructor() {
    super("Discarded: superseded by a newer login or an intervening logout.");
    this.name = "StaleSessionError";
  }
}

// #310 review — discarding the JS result is NOT enough for a request that also
// rotates the refresh cookie (/auth/login and /auth/refresh both do). The
// browser applies Set-Cookie the moment the response arrives, before any of our
// code runs, so a login that lands after logout leaves a VALID HttpOnly refresh
// cookie behind: the next reload authenticates straight through
// restoreSession() and the session the user ended comes back.
//
// So when a cookie-setting response is discarded, ask the server to revoke what
// it just issued.
//
// #393 — ALWAYS revoke, even when an access token is currently present. A
// present token only proves a newer login has ALSO happened; it proves nothing
// about which of the two responses' Set-Cookie headers the browser actually
// kept — that's real network arrival order, which JS cannot observe, and the
// HttpOnly cookie itself can't be read back to check. Skipping the revoke
// whenever a token happened to be present (the previous behavior) could leave
// the PREVIOUS user's rotated cookie live in the browser, silently, with the
// new session's correct in-memory token masking it until reload. Revoking
// unconditionally is safe in both branches: worst case, the newer session's
// own cookie was the one actually live, and this forces IT to re-authenticate
// too — an inconvenience confined to an already-narrow race window, never a
// wrong-session security hole.
export async function revokeSupersededCookie(discardedAccessToken: string): Promise<void> {
  // #547 made the account selector mandatory for a precise logout: omitting it
  // is now a deliberate no-op when there is no bearer fallback. The discarded
  // response's own token is the authority: that response wrote the cookie named
  // for the token's farm. The tab binding may already name the NEWER session
  // that superseded it, so using the binding as a fallback could revoke exactly
  // the session the user chose. If the response token cannot identify its farm,
  // refuse to guess and report that best-effort cleanup could not uphold #393.
  const accountId = accountIdFromToken(discardedAccessToken);
  if (accountId === null) {
    console.error("discarded response: cannot attribute its refresh cookie to a farm; revoke skipped");
    return;
  }
  try {
    await raw<void>("/auth/logout", {
      method: "POST",
      headers: { [CSRF_HEADER]: "1", [ACCOUNT_HEADER]: accountId },
    });
  } catch (err) {
    // Best-effort, like logout's own revoke: report it rather than swallow, but
    // never surface it to the caller whose result was already discarded.
    console.error("discarded response: revoking its refresh cookie failed", err);
  }
}
