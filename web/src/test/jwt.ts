// Test-only helpers for seeding a decoded session. Not matched by the Vitest
// `include` glob (only *.test.ts(x) run), so importing this never registers a
// suite. Since #145 the access token lives in memory, so seeding is a direct
// setAccessToken — no storage key.
import { setAccessToken } from "../auth/tokenStore";

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

// Seed the in-memory access token the same way a real login/refresh would, so
// the decode under test reads exactly what a live session holds.
export function setStoredToken(payload: Record<string, unknown>): void {
  setAccessToken(makeToken(payload));
}

// Seed a raw (possibly malformed) access token to exercise fail-closed paths.
export function setRawAccessToken(accessToken: string): void {
  setAccessToken(accessToken);
}
