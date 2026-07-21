// Test-only helpers for seeding a decoded session. Not matched by the Vitest
// `include` glob (only *.test.ts(x) run), so importing this never registers a
// suite. Kept DRY so a fix to the encoder or the storage key applies once
// (PR #106 review).
import { KEY } from "../auth/tokenStore";

// base64url-encode a value the way real JWT segments are: standard base64 with
// +/→-_ and no padding. encodeURIComponent guards non-Latin1 chars so a payload
// with a Unicode claim fails in claims.ts, not in this helper.
function b64url(value: unknown): string {
  const utf8 = encodeURIComponent(JSON.stringify(value)).replace(
    /%([0-9A-F]{2})/g,
    (_, hex: string) => String.fromCharCode(parseInt(hex, 16)),
  );
  return btoa(utf8).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

// A JWT-shaped string with the given payload. Header + signature are inert —
// claims.ts only decodes the payload segment.
export function makeToken(payload: Record<string, unknown>): string {
  return `${b64url({ alg: "HS256", typ: "JWT" })}.${b64url(payload)}.signature`;
}

// Seed the token store the same way the app does (same KEY), so the decode
// under test reads exactly what a real session would.
export function setStoredToken(payload: Record<string, unknown>): void {
  localStorage.setItem(
    KEY,
    JSON.stringify({ accessToken: makeToken(payload), refreshToken: "r", expiresAt: "2099-01-01T00:00:00Z" }),
  );
}

// Store a raw (possibly malformed) access token to exercise fail-closed paths.
export function setRawAccessToken(accessToken: string): void {
  localStorage.setItem(KEY, JSON.stringify({ accessToken, refreshToken: "r", expiresAt: "x" }));
}
