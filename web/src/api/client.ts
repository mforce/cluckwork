import type { AccessTokenResponse, LoginRequest, ProblemDetails } from "./types";
import { clearAccessToken, getAccessToken, setAccessToken } from "../auth/tokenStore";
import { newId } from "../lib/ids";

const BASE = "/api/v1";

// #145 — refresh/logout ride the HttpOnly refresh cookie; this custom header
// (which a cross-site simple request cannot set) is the CSRF second factor
// alongside the cookie's SameSite=Strict. Mirrors AuthCookies.CsrfHeaderName.
const CSRF_HEADER = "X-Cluckwork-Auth";

export class ApiError extends Error {
  constructor(
    public status: number,
    public title: string,
    message: string,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

// Called when the session is unrecoverable (refresh failed). AuthContext wires
// this to drop React auth state and bounce to /login.
let onUnauthenticated: (() => void) | null = null;
export function setOnUnauthenticated(cb: (() => void) | null): void {
  onUnauthenticated = cb;
}

// Called whenever a new token pair is saved (login or transparent refresh).
// AuthContext re-derives the role from the fresh access token so a demotion
// or promotion shows in the UI within one token lifetime, not at next reload.
let onTokensChanged: (() => void) | null = null;
export function setOnTokensChanged(cb: (() => void) | null): void {
  onTokensChanged = cb;
}

async function parseError(res: Response): Promise<ApiError> {
  let title = res.statusText;
  let detail = res.statusText;
  try {
    const body = (await res.json()) as ProblemDetails & {
      errors?: Record<string, string[]>;
    };
    title = body.title ?? title;
    // ValidationProblem carries an errors map and usually no detail — flatten
    // it so the user sees which field is wrong, not just "Bad Request".
    if (body.errors && Object.keys(body.errors).length > 0)
      detail = Object.values(body.errors).flat().join(" ");
    else detail = body.detail ?? detail;
  } catch {
    // non-JSON body — keep status text
  }
  return new ApiError(res.status, title, detail);
}

async function raw<T>(
  path: string,
  init: RequestInit,
  accessToken?: string,
): Promise<T> {
  const headers = new Headers(init.headers);
  headers.set("Content-Type", "application/json");
  if (accessToken) headers.set("Authorization", `Bearer ${accessToken}`);

  const res = await fetch(`${BASE}${path}`, { ...init, headers });
  if (!res.ok) throw await parseError(res);
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

// --- Auth endpoints -------------------------------------------------------

export async function login(body: LoginRequest): Promise<void> {
  // The server sets the HttpOnly refresh cookie; the body returns only the
  // access token, which lives in memory for this tab's lifetime.
  const res = await raw<AccessTokenResponse>("/auth/login", {
    method: "POST",
    body: JSON.stringify(body),
  });
  setAccessToken(res.accessToken);
  onTokensChanged?.();
}

export async function logout(): Promise<void> {
  const token = getAccessToken();
  clearAccessToken();
  if (!token) return;
  try {
    // The refresh cookie rides along automatically; the server revokes it and
    // expires the cookie. Custom header satisfies the CSRF check.
    await raw<void>(
      "/auth/logout",
      { method: "POST", headers: { [CSRF_HEADER]: "1" } },
      token,
    );
  } catch {
    // best-effort revoke; the in-memory token is already cleared
  }
}

// Single-flight refresh: concurrent 401s (and the load-time bootstrap) share one
// in-flight refresh call. The refresh token rides the cookie; no body is sent.
let refreshInFlight: Promise<string> | null = null;

async function refreshTokens(): Promise<string> {
  if (refreshInFlight) return refreshInFlight;

  refreshInFlight = raw<AccessTokenResponse>("/auth/refresh", {
    method: "POST",
    headers: { [CSRF_HEADER]: "1" },
  })
    .then((res) => {
      setAccessToken(res.accessToken);
      onTokensChanged?.();
      return res.accessToken;
    })
    .finally(() => {
      refreshInFlight = null;
    });

  return refreshInFlight;
}

// Session bootstrap (page load): the access token is memory-only, so it's gone
// after a reload — try one silent refresh against the cookie. Resolves true if a
// session was restored, false if unauthenticated (→ clean login, no error flash).
export async function restoreSession(): Promise<boolean> {
  try {
    await refreshTokens();
    return true;
  } catch {
    return false;
  }
}

// --- Authenticated request with one transparent refresh-and-retry ---------

export function apiGet<T>(path: string): Promise<T> {
  return apiFetch<T>(path, { method: "GET" });
}

// Writes require an Idempotency-Key (server middleware): a retry with the same
// key replays the original response instead of repeating the side effect.
// Callers retrying a logical mutation after an ambiguous failure should pass
// the SAME key so the server dedupes instead of repeating the write.
export function apiPost<T>(path: string, body?: unknown, idempotencyKey?: string): Promise<T> {
  return apiFetch<T>(path, {
    method: "POST",
    headers: { "Idempotency-Key": idempotencyKey ?? newId() },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

export function apiPut<T>(path: string, body: unknown, idempotencyKey?: string): Promise<T> {
  return apiFetch<T>(path, {
    method: "PUT",
    headers: { "Idempotency-Key": idempotencyKey ?? newId() },
    body: JSON.stringify(body),
  });
}

export function apiDelete<T>(path: string, idempotencyKey?: string): Promise<T> {
  return apiFetch<T>(path, {
    method: "DELETE",
    headers: { "Idempotency-Key": idempotencyKey ?? newId() },
  });
}

// File download with the same auth + one transparent refresh-and-retry as
// apiFetch. Returns the body as a Blob plus the server's suggested filename.
export async function apiGetBlob(
  path: string,
): Promise<{ blob: Blob; filename: string | null }> {
  const token = await currentAccessToken();
  try {
    return await rawBlob(path, token);
  } catch (err) {
    if (!(err instanceof ApiError) || err.status !== 401) throw err;
    try {
      const refreshed = await refreshTokens();
      return await rawBlob(path, refreshed);
    } catch (refreshErr) {
      if (isTransientRefreshFailure(refreshErr)) throw refreshErr;
      clearAccessToken();
      onUnauthenticated?.();
      throw err;
    }
  }
}

async function rawBlob(
  path: string,
  accessToken: string,
): Promise<{ blob: Blob; filename: string | null }> {
  const res = await fetch(`${BASE}${path}`, {
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (!res.ok) throw await parseError(res);
  const disposition = res.headers.get("Content-Disposition");
  const match = disposition?.match(/filename\*?=(?:UTF-8''|")?([^";]+)/i);
  return {
    blob: await res.blob(),
    filename: match ? decodeURIComponent(match[1]) : null,
  };
}

export async function apiFetch<T>(path: string, init: RequestInit = {}): Promise<T> {
  const token = await currentAccessToken();
  try {
    return await raw<T>(path, init, token);
  } catch (err) {
    if (!(err instanceof ApiError) || err.status !== 401) throw err;
    try {
      const refreshed = await refreshTokens();
      return await raw<T>(path, init, refreshed);
    } catch (refreshErr) {
      if (isTransientRefreshFailure(refreshErr)) throw refreshErr;
      clearAccessToken();
      onUnauthenticated?.();
      throw err;
    }
  }
}

// The in-memory access token, or one obtained via a silent refresh when memory
// is empty (e.g. a request racing the load-time bootstrap). Throws + signals
// unauthenticated if no session can be established.
async function currentAccessToken(): Promise<string> {
  const token = getAccessToken();
  if (token) return token;
  try {
    return await refreshTokens();
  } catch {
    onUnauthenticated?.();
    throw new ApiError(401, "NoSession", "Not authenticated.");
  }
}

// A 429 during refresh is transient throttling (#143), not an invalid session:
// the refresh token is still good, so keep it and surface the error rather than
// clearing tokens — which would force a re-login through the same rate limit.
function isTransientRefreshFailure(err: unknown): boolean {
  return err instanceof ApiError && err.status === 429;
}
