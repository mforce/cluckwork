import type { LoginRequest, ProblemDetails, TokenPair } from "./types";
import { clearTokens, loadTokens, saveTokens } from "../auth/tokenStore";

const BASE = "/api/v1";

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

export async function login(body: LoginRequest): Promise<TokenPair> {
  const tokens = await raw<TokenPair>("/auth/login", {
    method: "POST",
    body: JSON.stringify(body),
  });
  saveTokens(tokens);
  onTokensChanged?.();
  return tokens;
}

export async function logout(): Promise<void> {
  const tokens = loadTokens();
  clearTokens();
  if (!tokens) return;
  try {
    await raw<void>(
      "/auth/logout",
      { method: "POST", body: JSON.stringify({ refreshToken: tokens.refreshToken }) },
      tokens.accessToken,
    );
  } catch {
    // best-effort revoke; local tokens already cleared
  }
}

// Single-flight refresh: concurrent 401s share one in-flight refresh call.
let refreshInFlight: Promise<TokenPair> | null = null;

async function refreshTokens(): Promise<TokenPair> {
  if (refreshInFlight) return refreshInFlight;
  const current = loadTokens();
  if (!current) throw new ApiError(401, "NoSession", "Not authenticated.");

  refreshInFlight = raw<TokenPair>("/auth/refresh", {
    method: "POST",
    body: JSON.stringify({ refreshToken: current.refreshToken }),
  })
    .then((tokens) => {
      saveTokens(tokens);
      onTokensChanged?.();
      return tokens;
    })
    .finally(() => {
      refreshInFlight = null;
    });

  return refreshInFlight;
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
    headers: { "Idempotency-Key": idempotencyKey ?? crypto.randomUUID() },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

export function apiPut<T>(path: string, body: unknown, idempotencyKey?: string): Promise<T> {
  return apiFetch<T>(path, {
    method: "PUT",
    headers: { "Idempotency-Key": idempotencyKey ?? crypto.randomUUID() },
    body: JSON.stringify(body),
  });
}

export function apiDelete<T>(path: string, idempotencyKey?: string): Promise<T> {
  return apiFetch<T>(path, {
    method: "DELETE",
    headers: { "Idempotency-Key": idempotencyKey ?? crypto.randomUUID() },
  });
}

// File download with the same auth + one transparent refresh-and-retry as
// apiFetch. Returns the body as a Blob plus the server's suggested filename.
export async function apiGetBlob(
  path: string,
): Promise<{ blob: Blob; filename: string | null }> {
  const tokens = loadTokens();
  if (!tokens) {
    onUnauthenticated?.();
    throw new ApiError(401, "NoSession", "Not authenticated.");
  }

  try {
    return await rawBlob(path, tokens.accessToken);
  } catch (err) {
    if (!(err instanceof ApiError) || err.status !== 401) throw err;
    try {
      const refreshed = await refreshTokens();
      return await rawBlob(path, refreshed.accessToken);
    } catch {
      clearTokens();
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
  const tokens = loadTokens();
  if (!tokens) {
    onUnauthenticated?.();
    throw new ApiError(401, "NoSession", "Not authenticated.");
  }

  try {
    return await raw<T>(path, init, tokens.accessToken);
  } catch (err) {
    if (!(err instanceof ApiError) || err.status !== 401) throw err;
    try {
      const refreshed = await refreshTokens();
      return await raw<T>(path, init, refreshed.accessToken);
    } catch {
      clearTokens();
      onUnauthenticated?.();
      throw err;
    }
  }
}
