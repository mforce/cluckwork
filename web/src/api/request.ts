import { parseError } from "./errors";
import { attachTraceparent } from "./trace";

const BASE = "/api/v1";

// #145 — refresh/logout ride the HttpOnly refresh cookie; this custom header
// (which a cross-site simple request cannot set) is the CSRF second factor
// alongside the cookie's SameSite=Strict. Mirrors AuthCookies.CsrfHeaderName.
export const CSRF_HEADER = "X-Cluckwork-Auth";

// #547 — the tab tells /auth/refresh which farm it expects; the server compares
// it against the stored token's AccountId BEFORE anything rotates. Mirrors
// AuthCookies.ExpectedAccountHeaderName. The 401 title the server sends back
// when they differ (a distinct, re-bootstrap-able failure — not a dead
// session, see executeRefresh).
export const ACCOUNT_HEADER = "X-Cluckwork-Account";

export async function raw<T>(
  path: string,
  init: RequestInit,
  accessToken?: string,
): Promise<T> {
  const headers = new Headers(init.headers);
  // A caller that declared its own type keeps it: the farm-logo upload (#123)
  // PUTs raw image bytes, not JSON.
  if (!headers.has("Content-Type")) headers.set("Content-Type", "application/json");
  // A caller that declared its own bearer keeps it: login() passes "" (the
  // endpoint is AllowAnonymous and the test pins the header's ABSENCE) while
  // the access token it returns is still needed by the caller for local state
  // — decode it from the RESPONSE, never from an Authorization header.
  if (!headers.has("Authorization")) {
    if (accessToken) headers.set("Authorization", `Bearer ${accessToken}`);
  }
  attachTraceparent(headers);

  const res = await fetch(`${BASE}${path}`, { ...init, headers });
  if (!res.ok) throw await parseError(res);
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export async function rawBlob(
  path: string,
  accessToken: string,
): Promise<{ blob: Blob; filename: string | null }> {
  const headers = new Headers({ Authorization: `Bearer ${accessToken}` });
  attachTraceparent(headers);
  const res = await fetch(`${BASE}${path}`, { headers });
  if (!res.ok) throw await parseError(res);
  const disposition = res.headers.get("Content-Disposition");
  const match = disposition?.match(/filename\*?=(?:UTF-8''|")?([^";]+)/i);
  return {
    blob: await res.blob(),
    filename: match ? decodeURIComponent(match[1]) : null,
  };
}
