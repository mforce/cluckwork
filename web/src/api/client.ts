import type { AccessTokenResponse, LoginRequest, ProblemDetails } from "./types";
import { clearAccessToken, getAccessToken, setAccessToken } from "../auth/tokenStore";
import { newId } from "../lib/ids";
import i18n from "../i18n";
import { newTraceparent } from "../lib/traceparent";

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
      errorCodes?: Record<string, (string | null)[]>;
    };
    title = body.title ?? title;
    // ValidationProblem carries an errors map and usually no detail — flatten
    // it so the user sees which field is wrong, not just "Bad Request".
    if (body.errors && Object.keys(body.errors).length > 0) {
      // Per-field, index-aligned: a slot with an explicit code whose catalog key
      // exists renders the translated message; otherwise the English server
      // message at the SAME index (uncoded slots, or codes without a catalog key,
      // keep their English — matching the additive, explicit-only #45 contract).
      const parts: string[] = [];
      for (const [field, messages] of Object.entries(body.errors)) {
        const codes = body.errorCodes?.[field] ?? [];
        messages.forEach((msg, i) => {
          const code = codes[i];
          // `defaultValue: msg` IS the fallback: a known catalog key wins, an
          // unknown code (or an uncoded/null slot) renders the server English
          // message. This also satisfies i18next's typed t() — a runtime string
          // key needs the defaultValue overload (a bare i18n.t(dynamicKey) is a
          // type error under CustomTypeOptions). keySeparator:false makes the
          // dotted `errors:Me.Language.Format` a literal key, not a nested path.
          parts.push(code ? i18n.t(`errors:${code}`, { defaultValue: msg }) : msg);
        });
      }
      detail = parts.join(" ");
    } else {
      detail = body.detail ?? detail;
    }
  } catch {
    // non-JSON body — keep status text
  }
  return new ApiError(res.status, title, detail);
}

// #217 — the trace id of the most recent API request, success or failure (a
// FAILED request's id is exactly the one worth having: the crash report
// attaches it, joining a browser crash to that request's server-side story).
let lastTraceId: string | null = null;
export function getLastTraceId(): string | null {
  return lastTraceId;
}

// Mint per request, never reused: the server treats the incoming id as the
// request's TraceId (#214), so sharing one across requests would fuse
// unrelated request logs into one trace.
function attachTraceparent(headers: Headers): void {
  const tp = newTraceparent();
  headers.set("traceparent", tp.header);
  lastTraceId = tp.traceId;
}

async function raw<T>(
  path: string,
  init: RequestInit,
  accessToken?: string,
): Promise<T> {
  const headers = new Headers(init.headers);
  // A caller that declared its own type keeps it: the farm-logo upload (#123)
  // PUTs raw image bytes, not JSON.
  if (!headers.has("Content-Type")) headers.set("Content-Type", "application/json");
  if (accessToken) headers.set("Authorization", `Bearer ${accessToken}`);
  attachTraceparent(headers);

  const res = await fetch(`${BASE}${path}`, { ...init, headers });
  if (!res.ok) throw await parseError(res);
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

// --- Auth endpoints -------------------------------------------------------

// #310 — login, the load-time bootstrap refresh, an explicit (401-triggered)
// refresh, and logout are treated as ONE browser-session state machine, not
// four independent calls racing each other. Every entry into that machine
// captures the CURRENT generation before its own await; login and logout each
// bump it (login because it is by definition a newer session than anything
// already in flight, logout because ending the session must supersede
// everything). A completion — success OR failure — may only commit tokens or
// user state while the generation it captured is still the current one;
// otherwise it is stale and must be discarded rather than resurrecting or
// clobbering whatever the (newer) generation already did.
let sessionGeneration = 0;

class StaleSessionError extends Error {
  constructor() {
    super("Discarded: superseded by a newer login or an intervening logout.");
    this.name = "StaleSessionError";
  }
}

export async function login(body: LoginRequest): Promise<void> {
  // #310 — bump first: login is a NEWER session than anything already in
  // flight (a bootstrap refresh, say), so its resolution must be the one that
  // wins, and any earlier flight must see its captured generation go stale.
  const generation = ++sessionGeneration;
  // The server sets the HttpOnly refresh cookie; the body returns only the
  // access token, which lives in memory for this tab's lifetime.
  const res = await raw<AccessTokenResponse>("/auth/login", {
    method: "POST",
    body: JSON.stringify(body),
  });
  // Superseded while in flight (e.g. a logout landed before this resolved) —
  // do not resurrect a session the user already ended.
  if (sessionGeneration !== generation) throw new StaleSessionError();
  setAccessToken(res.accessToken);
  onTokensChanged?.();
}

// #165 — self-service password change. The server revokes every session for the
// user (other devices are signed out) and hands this one a fresh pair, so we
// swap in the returned access token; the rotated refresh cookie is set by the
// response.
//
// apiPost attaches an Idempotency-Key like any write, but the SERVER exempts
// this route from the response cache, so that key is inert here. Caching this
// response would persist the access token and, on replay, return it WITHOUT the
// rotated Set-Cookie — leaving the client holding a revoked refresh cookie
// (#165 review). Replay protection isn't needed anyway: a repeat can't re-apply,
// because the current password no longer matches.
export async function changePassword(
  body: { currentPassword: string; newPassword: string },
): Promise<void> {
  const res = await apiPost<AccessTokenResponse>("/auth/change-password", body);
  setAccessToken(res.accessToken);
  onTokensChanged?.();
}

export async function logout(): Promise<void> {
  // #310 — bump the generation and clear in-memory state SYNCHRONOUSLY, before
  // any await: local logout must win immediately, whatever a slower in-flight
  // login/refresh does later. Also cancel any refresh currently in flight
  // (cancellation where possible) so it stops sooner instead of only being
  // discarded on arrival by the generation check in refreshTokens().
  sessionGeneration++;
  abortInFlightRefresh?.();
  clearAccessToken();
  try {
    // Cookie-authenticated: the HttpOnly refresh cookie rides along and the CSRF
    // header satisfies the check. No bearer (so it works even if the access token
    // had expired) and no Idempotency-Key (the request is anonymous). Always
    // fires — JS can't read the HttpOnly cookie to know whether a session exists,
    // so we always ask the server to revoke + clear it.
    await raw<void>("/auth/logout", { method: "POST", headers: { [CSRF_HEADER]: "1" } });
  } catch (err) {
    // Best-effort revoke: the in-memory token is already cleared above and this
    // failure can never reverse that. Still surfaced (not silently swallowed,
    // #310) so an operator can see the server-side session may be un-revoked —
    // logged, never the (already-cleared) token itself.
    console.error("logout: server-side session revoke failed", err);
  }
}

// #169 — serialise refresh across tabs. The refresh token lives only in the
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
const REFRESH_LOCK = "cluckwork.auth.refresh";

// Cap how long one tab may hold the cross-tab lock. fetch() has no default
// timeout, so a hung /auth/refresh would otherwise keep the lock — and every
// other tab's refresh — parked indefinitely (#169 review). On timeout we abort,
// which releases the lock; the aborting tab recovers on its next request.
const REFRESH_TIMEOUT_MS = 15_000;

// #310 — the abort handle of whichever refresh attempt is currently running
// (if any), so logout() can cancel it outright rather than only waiting for
// the generation check below to discard its result on arrival.
let abortInFlightRefresh: (() => void) | null = null;

function withRefreshLock<T>(run: (signal: AbortSignal) => Promise<T>): Promise<T> {
  const attempt = () => {
    const controller = new AbortController();
    abortInFlightRefresh = () => controller.abort();
    const timer = setTimeout(() => controller.abort(), REFRESH_TIMEOUT_MS);
    return run(controller.signal).finally(() => {
      clearTimeout(timer);
      abortInFlightRefresh = null;
    });
  };
  const locks: LockManager | undefined = globalThis.navigator?.locks;
  return locks ? (locks.request(REFRESH_LOCK, attempt) as Promise<T>) : attempt();
}

// Single-flight refresh: concurrent 401s (and the load-time bootstrap) share one
// in-flight refresh call. The refresh token rides the cookie; no body is sent.
// The per-tab latch dedupes within a tab; the cross-tab Web Lock (above) then
// serialises whatever refresh each tab still needs against the other tabs.
let refreshInFlight: Promise<string> | null = null;

async function refreshTokens(): Promise<string> {
  if (refreshInFlight) return refreshInFlight;

  // #310 — captured once, at the START of this flight. Checked again at
  // settlement (success or failure) so a logout/login that lands WHILE this
  // network call is in the air discards its outcome instead of committing it.
  const generation = sessionGeneration;

  refreshInFlight = withRefreshLock((signal) =>
    raw<AccessTokenResponse>("/auth/refresh", {
      method: "POST",
      headers: { [CSRF_HEADER]: "1" },
      signal,
    }),
  )
    .then(
      (res) => {
        if (sessionGeneration !== generation) throw new StaleSessionError();
        setAccessToken(res.accessToken);
        onTokensChanged?.();
        return res.accessToken;
      },
      (err: unknown) => {
        // A late FAILURE from an obsolete generation is discarded the same as
        // a late success: the real error (e.g. a genuinely revoked refresh
        // token) might otherwise tear down a newer, still-valid session.
        throw sessionGeneration !== generation ? new StaleSessionError() : err;
      },
    )
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

// Raw-bytes write (#123's logo upload): same auth and the same transparent
// refresh-and-retry as apiPut, but the body goes up as it is instead of being
// JSON-encoded. Safe to retry — a Blob can be read more than once. The declared
// content type is courtesy only: the API sniffs the format from the bytes and
// ignores what the client claims.
export function apiPutBytes<T>(
  path: string,
  body: Blob,
  contentType: string,
  idempotencyKey?: string,
): Promise<T> {
  return apiFetch<T>(path, {
    method: "PUT",
    headers: {
      "Content-Type": contentType,
      "Idempotency-Key": idempotencyKey ?? newId(),
    },
    body,
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
  } catch (err) {
    // #310 — a stale (obsolete-generation) failure belongs to an already-
    // superseded session: a newer login already committed its own token, or a
    // logout already tore everything down. Either way, this failure must not
    // touch the CURRENT session — no clearing, no onUnauthenticated navigate.
    if (err instanceof StaleSessionError) throw err;
    onUnauthenticated?.();
    throw new ApiError(401, "NoSession", "Not authenticated.");
  }
}

// A refresh failure is "transient" when the session is likely still valid, so we
// keep the tokens and surface the error rather than forcing a re-login:
//   - 429: rate-limit throttling (#143) — re-logging in hits the same limit.
//   - AbortError: our own cross-tab lock timeout (#169) fired on a slow/hung
//     refresh; the cookie is untouched, so the next attempt can still succeed.
//   - StaleSessionError (#310): the refresh belonged to an obsolete generation
//     (superseded by a newer login, or discarded by a logout) — a newer/
//     already-torn-down session must not be clobbered by this stale failure.
function isTransientRefreshFailure(err: unknown): boolean {
  if (err instanceof StaleSessionError) return true;
  if (err instanceof ApiError && err.status === 429) return true;
  return err instanceof DOMException && err.name === "AbortError";
}
