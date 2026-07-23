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

// Remove any token left in localStorage by the pre-#145 scheme. Called once at
// startup; safe to call when storage is unavailable (private mode).
export function purgeLegacyTokens(): void {
  try {
    localStorage.removeItem(LEGACY_KEY);
  } catch {
    // storage unavailable — nothing to purge
  }
}
