import type { AccessTokenResponse, LoginRequest, ProblemDetails } from "./types";
import { clearAccessToken, getAccessToken, setAccessToken } from "../auth/tokenStore";
import { accountIdFromToken } from "../auth/claims";
import { newId } from "../lib/ids";
import i18n from "../i18n";
import { newTraceparent } from "../lib/traceparent";

const BASE = "/api/v1";

// #145 — refresh/logout ride the HttpOnly refresh cookie; this custom header
// (which a cross-site simple request cannot set) is the CSRF second factor
// alongside the cookie's SameSite=Strict. Mirrors AuthCookies.CsrfHeaderName.
const CSRF_HEADER = "X-Cluckwork-Auth";

// #308 — carries a short-lived step-up grant (see stepUp() below) on the two
// sensitive user-administration calls that need one. Mirrors
// AuthEndpoints.StepUpHeaderName.
export const STEP_UP_HEADER = "X-Cluckwork-Step-Up";

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
let onUnauthenticated: ((title?: string) => void) | null = null;
export function setOnUnauthenticated(cb: ((title?: string) => void) | null): void {
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
async function revokeSupersededCookie(): Promise<void> {
  try {
    await raw<void>("/auth/logout", { method: "POST", headers: { [CSRF_HEADER]: "1" } });
  } catch (err) {
    // Best-effort, like logout's own revoke: report it rather than swallow, but
    // never surface it to the caller whose result was already discarded.
    console.error("discarded response: revoking its refresh cookie failed", err);
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
  // do not resurrect a session the user already ended. The response already
  // set a refresh cookie, so revoke it too, not just the in-memory token.
  if (sessionGeneration !== generation) {
    await revokeSupersededCookie();
    throw new StaleSessionError();
  }
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
  // #310 — this is a token-store writer like login/refresh, so it needs the
  // same generation guard: logout is reachable from every screen (AppLayout's
  // persistent button), so a user who submits a password change and logs out
  // before it lands would otherwise have the response write a fresh token back
  // over the cleared session.
  const generation = sessionGeneration;
  // Refresh and password-change both replace the one per-origin HttpOnly
  // cookie. Keep the password change inside the same cross-tab lock as refresh,
  // or an already-started old-epoch refresh can answer last and overwrite the
  // E+1 cookie this request just issued. Passing the held lock context lets
  // apiFetch do its normal 401 refresh-and-retry inside this lock without
  // trying to acquire the non-reentrant Web Lock a second time.
  const res = await withAuthCookieLock((signal) => {
    // The operation may have waited behind another tab's refresh. Logout or a
    // newer login that happened while queued wins before this request sends a
    // byte; never let the stale password form act on the superseding session.
    if (sessionGeneration !== generation) throw new StaleSessionError();
    return apiFetch<AccessTokenResponse>(
      "/auth/change-password",
      {
        method: "POST",
        headers: { "Idempotency-Key": newId() },
        body: JSON.stringify(body),
        signal,
      },
      { generation, signal },
    );
  });
  // This response rotates the refresh cookie too, so a discarded one needs that
  // cookie revoked, not merely the in-memory token dropped.
  if (sessionGeneration !== generation) {
    await revokeSupersededCookie();
    throw new StaleSessionError();
  }
  setAccessToken(res.accessToken);
  onTokensChanged?.();
}

// #308 — re-confirms the CURRENT password to mint a short-lived step-up grant
// for one sensitive user-administration call (creating another Owner;
// resetting an Owner's password). The grant is returned to the caller and
// used EXACTLY ONCE, immediately, as the X-Cluckwork-Step-Up header on that
// one follow-up request (see UsersPage.tsx) — this function itself never
// stores the token or the password anywhere longer-lived than its own return
// value, and there is nothing here to clear on logout: unlike the access
// token, no module-level state holds a step-up grant at all.
//
// #336 review — this goes through apiPost, so it inherits the transparent
// refresh-and-retry every 401 triggers. That is only safe because the server
// answers a REJECTED password with 400 (Users.CurrentPasswordIncorrect), the
// same status change-password uses, and reserves 401 for a genuinely
// unauthenticated caller. If /auth/step-up ever went back to 401 for a wrong
// password, one typed password would be replayed as two failed accesses —
// halving the server's five-attempt account lockout and rotating the refresh
// token per attempt. The contract is pinned by a test in client.test.ts.
export async function stepUp(password: string): Promise<{ token: string; expiresAt: string }> {
  return apiPost<{ token: string; expiresAt: string }>("/auth/step-up", { password });
}

export async function logout(): Promise<void> {
  // #310 — bump the generation and clear in-memory state SYNCHRONOUSLY, before
  // any await: local logout must win immediately, whatever a slower in-flight
  // login/refresh does later. Also cancel any refresh currently in flight —
  // note this only reaches a refresh that has already been GRANTED the
  // cross-tab lock (#169); one still queued behind another tab has no
  // controller yet, so it will still fire its request and then be discarded on
  // arrival by the generation check. Correctness rests on the generation, not
  // on the abort; the abort is only an optimisation so it stops sooner
  // instead of only being
  // discarded on arrival by the generation check in refreshTokens().
  sessionGeneration++;
  abortInFlightAuthCookieOperation?.();
  // #336 — capture BEFORE clearing: this tab's access token names the user who
  // actually clicked logout, and the clear below is deliberately synchronous.
  // Read it after and we would always send nothing.
  const bearer = getAccessToken();
  clearAccessToken();
  try {
    // Cookie-authenticated: the HttpOnly refresh cookie rides along and the CSRF
    // header satisfies the check. Always fires — JS can't read the HttpOnly
    // cookie to know whether a session exists, so we always ask the server to
    // revoke + clear it.
    //
    // #336 — the bearer is sent too, when this tab still has one. The refresh
    // cookie is per-origin (one per browser, last login wins) while this store
    // is per-tab, so the cookie can belong to a DIFFERENT user than the one
    // logging out here; without the bearer the server would revoke that other
    // user's step-up grants and leave this user's alive. It stays OPTIONAL —
    // the endpoint is AllowAnonymous, so an already-expired token just leaves
    // the call unauthenticated and the cookie path still ends the session, and
    // the server exempts /auth/logout from the Idempotency-Key requirement so
    // authenticating it does not make the request need one.
    await raw<void>(
      "/auth/logout",
      { method: "POST", headers: { [CSRF_HEADER]: "1" } },
      bearer ?? undefined,
    );
  } catch (err) {
    // Best-effort revoke: the in-memory token is already cleared above and this
    // failure can never reverse that. Still surfaced (not silently swallowed,
    // #310) so an operator can see the server-side session may be un-revoked —
    // logged, never the (already-cleared) token itself.
    console.error("logout: server-side session revoke failed", err);
  }
}

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
const REFRESH_TIMEOUT_MS = 15_000;

// #310/#364 — the abort handle of whichever cookie-mutating auth operation is
// currently running (if any), so logout() can cancel it outright rather than
// only waiting for the generation check below to discard its result on arrival.
let abortInFlightAuthCookieOperation: (() => void) | null = null;

// Web Locks coordinate tabs, while this queue provides the same ordering within
// one tab and on browsers where navigator.locks is unavailable. Rejections are
// absorbed only by the tail so one failed auth write never poisons later work;
// each caller still receives its own original result.
let authCookieTail: Promise<void> = Promise.resolve();

function withAuthCookieLock<T>(
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

// Single-flight refresh: concurrent 401s (and the load-time bootstrap) share one
// in-flight refresh call. The refresh token rides the cookie; no body is sent.
// The per-tab latch dedupes within a tab; the cross-tab Web Lock (above) then
// serialises whatever refresh each tab still needs against the other tabs.
let refreshInFlight: Promise<string> | null = null;

type HeldAuthCookieLock = Readonly<{
  generation: number;
  signal: AbortSignal;
}>;

async function executeRefresh(generation: number, signal?: AbortSignal): Promise<string> {
  // Captured BEFORE the call: the refresh may rotate the cookie and the token
  // store, so reading it afterwards would compare the new token against itself.
  const tabAccountId = accountIdFromToken(getAccessToken());
  try {
    const res = await raw<AccessTokenResponse>("/auth/refresh", {
      method: "POST",
      headers: { [CSRF_HEADER]: "1" },
      signal,
    });

    // A SUCCESSFUL refresh rotated the cookie before we got here, so a
    // discarded one leaves live credentials in the browser — revoke them.
    if (sessionGeneration !== generation) {
      await revokeSupersededCookie();
      throw new StaleSessionError();
    }
    // #532 — the cookie is per-origin, this store is per-tab. Another tab
    // logging into a different farm replaces the cookie, so this refresh can
    // return a session for a farm this tab never chose. Adopting it would let
    // the retry replay this tab's pending request — including its BODY —
    // against the other farm. Refuse instead, and tear the tab's session down.
    //
    // A tab with NO prior token is the legitimate cold-restore path (nothing to
    // compare against), so only a genuine MISMATCH is refused.
    //
    // A plain Error (not StaleSessionError, not a 429 ApiError, not an
    // AbortError): it is not transient, so the caller's refresh-failure branch
    // runs its usual single teardown — clearAccessToken() + onUnauthenticated
    // + rethrow the ORIGINAL 401 — instead of retrying on the other farm's
    // token. Reusing that existing teardown is what keeps this path from
    // inventing a second "session is over" code path.
    const refreshedAccountId = accountIdFromToken(res.accessToken);
    if (tabAccountId !== null && refreshedAccountId !== null && refreshedAccountId !== tabAccountId) {
      throw new Error("Refused: refresh returned a different account's session.");
    }
    setAccessToken(res.accessToken);
    onTokensChanged?.();
    return res.accessToken;
  } catch (err) {
    // A late FAILURE from an obsolete generation is discarded the same as a
    // late success: the real error (e.g. a genuinely revoked refresh token)
    // might otherwise tear down a newer, still-valid session. No revoke here —
    // a failed refresh issued no cookie to revoke.
    if (err instanceof StaleSessionError) throw err;
    throw sessionGeneration !== generation ? new StaleSessionError() : err;
  }
}

async function executeHeldRefresh(
  generation: number,
  parentSignal: AbortSignal,
): Promise<string> {
  // The parent password-change operation is deliberately unbounded because it
  // cannot be replayed safely after an ambiguous commit. Refresh remains
  // replayable, though, and must not hold the shared cookie lock forever. Give
  // only this nested request a timeout while forwarding explicit logout's abort.
  const controller = new AbortController();
  const abort = () => controller.abort();
  if (parentSignal.aborted) abort();
  else parentSignal.addEventListener("abort", abort, { once: true });
  const timer = setTimeout(abort, REFRESH_TIMEOUT_MS);
  try {
    return await executeRefresh(generation, controller.signal);
  } finally {
    clearTimeout(timer);
    parentSignal.removeEventListener("abort", abort);
  }
}

async function refreshTokens(heldLock?: HeldAuthCookieLock): Promise<string> {
  // changePassword owns the shared cookie lock while apiFetch performs its
  // normal 401 refresh-and-retry. Web Locks are not reentrant, so execute that
  // nested refresh directly under the lock already held by the caller. Keep
  // the generation that entered the queue: resnapshotting here could attach a
  // stale password-change operation to a logout/new-login generation.
  if (heldLock)
    return executeHeldRefresh(heldLock.generation, heldLock.signal);

  if (refreshInFlight) return refreshInFlight;

  // #310 — captured once, at the START of this flight. Checked again at
  // settlement (success or failure) so a logout/login that lands WHILE this
  // network call is in the air discards its outcome instead of committing it.
  const generation = sessionGeneration;

  refreshInFlight = withAuthCookieLock(
    (signal) => executeRefresh(generation, signal),
    REFRESH_TIMEOUT_MS,
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
  } catch (err) {
    // The normal no-cookie case remains a quiet false result in the UI because
    // Login only renders the two credential-revocation titles. Preserve a
    // server-provided 401 title nevertheless: if the refresh endpoint can
    // classify a superseded/disabled session, load-time bootstrap must not be
    // the one teardown path that discards that reason.
    if (err instanceof ApiError && err.status === 401)
      onUnauthenticated?.(err.title);
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
//
// #308 — extraHeaders is how CreateUser/SetUserPassword attach the
// X-Cluckwork-Step-Up header for a sensitive operation; every other caller
// omits it.
export function apiPost<T>(
  path: string,
  body?: unknown,
  idempotencyKey?: string,
  extraHeaders?: Record<string, string>,
): Promise<T> {
  return apiFetch<T>(path, {
    method: "POST",
    headers: { "Idempotency-Key": idempotencyKey ?? newId(), ...extraHeaders },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

export function apiPut<T>(
  path: string,
  body: unknown,
  idempotencyKey?: string,
  extraHeaders?: Record<string, string>,
): Promise<T> {
  return apiFetch<T>(path, {
    method: "PUT",
    headers: { "Idempotency-Key": idempotencyKey ?? newId(), ...extraHeaders },
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
      // #310 review — same supersession handling as apiFetch.
      if (refreshErr instanceof StaleSessionError) {
        const fresh = getAccessToken();
        if (fresh) return await rawBlob(path, fresh);
        throw err;
      }
      if (isTransientRefreshFailure(refreshErr)) throw refreshErr;
      clearAccessToken();
      onUnauthenticated?.(err.title);
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

export async function apiFetch<T>(
  path: string,
  init: RequestInit = {},
  heldAuthCookieLock?: HeldAuthCookieLock,
): Promise<T> {
  const token = await currentAccessToken(heldAuthCookieLock);
  // currentAccessToken may itself await a cookie refresh. A newer login can
  // supersede the non-replayable password form during that await; never let the
  // stale form borrow the newer session's bearer for its first request.
  if (heldAuthCookieLock && sessionGeneration !== heldAuthCookieLock.generation)
    throw new StaleSessionError();
  try {
    return await raw<T>(path, init, token);
  } catch (err) {
    if (!(err instanceof ApiError) || err.status !== 401) throw err;
    // A held context identifies the non-replayable password form. Generic API
    // requests may continue on a newer login after supersession; this one must
    // not refresh that newer cookie or resend the old password body against it.
    if (heldAuthCookieLock && sessionGeneration !== heldAuthCookieLock.generation)
      throw new StaleSessionError();
    try {
      const refreshed = await refreshTokens(heldAuthCookieLock);
      if (heldAuthCookieLock && sessionGeneration !== heldAuthCookieLock.generation)
        throw new StaleSessionError();
      return await raw<T>(path, init, refreshed);
    } catch (refreshErr) {
      // #310 review — superseded mid-retry: retry once on the newer login's
      // token, or surface the ORIGINAL 401 if a logout ended the session.
      // Never let the internal marker reach the caller.
      if (refreshErr instanceof StaleSessionError) {
        if (heldAuthCookieLock) throw refreshErr;
        const fresh = getAccessToken();
        if (fresh) return await raw<T>(path, init, fresh);
        throw err;
      }
      if (isTransientRefreshFailure(refreshErr)) throw refreshErr;
      clearAccessToken();
      onUnauthenticated?.(err.title);
      throw err;
    }
  }
}

// The in-memory access token, or one obtained via a silent refresh when memory
// is empty (e.g. a request racing the load-time bootstrap). Throws + signals
// unauthenticated if no session can be established.
async function currentAccessToken(
  heldAuthCookieLock?: HeldAuthCookieLock,
): Promise<string> {
  const token = getAccessToken();
  if (token) return token;
  try {
    return await refreshTokens(heldAuthCookieLock);
  } catch (err) {
    // #310 — a stale (obsolete-generation) failure belongs to an already-
    // superseded session: a newer login already committed its own token, or a
    // logout already tore everything down. Either way, this failure must not
    // touch the CURRENT session — no clearing, no onUnauthenticated navigate.
    if (err instanceof StaleSessionError) return supersededToken();
    onUnauthenticated?.(err instanceof ApiError ? err.title : undefined);
    throw new ApiError(401, "NoSession", "Not authenticated.");
  }
}

// #310 review — resolve a discarded refresh against whatever superseded it.
// A newer LOGIN has already committed its token, so the caller's request is
// perfectly valid and should proceed on it. A newer LOGOUT left no session,
// and has already navigated, so this raises a plain 401 rather than firing
// onUnauthenticated a second time. Either way StaleSessionError itself stops
// here: it is an internal control marker, and every screen renders a non-
// ApiError's `.message` verbatim, so letting it escape would show the user
// "Discarded: superseded by..." in place of a real error.
function supersededToken(): string {
  const fresh = getAccessToken();
  if (fresh) return fresh;
  throw new ApiError(401, "NoSession", "Not authenticated.");
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
