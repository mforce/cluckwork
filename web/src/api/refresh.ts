import { getAccessToken, setAccessToken, clearAccessToken, bindAccount, getBoundAccountId } from "../auth/tokenStore";
import { accountIdFromToken } from "../auth/claims";
import type { AccessTokenResponse } from "./types";
import { raw, CSRF_HEADER, ACCOUNT_HEADER } from "./request";
import { ApiError } from "./errors";
import { StaleSessionError, revokeSupersededCookie, onUnauthenticated, onTokensChanged } from "./sessionState";
import { withAuthCookieLock, REFRESH_TIMEOUT_MS } from "./authCookieLock";
import { sessionGeneration, settledLogin } from "./session";
import { newId } from "../lib/ids";
import { rawBlob } from "./request";
import type { HeldAuthCookieLock } from "./authCookieLock";

const SESSION_CHANGED_TITLE = "Auth.SessionChanged";
// #532 — the server's distinct code for "several per-farm cookies are present
// and the caller named none" (the bootstrap-without-a-header path). The SPA
// treats it like any other terminal 401: clear the tab's session and send the
// user to login, whose farm-code field handles it (the farm picker is #535's
// job).
const FARM_SELECTION_REQUIRED_TITLE = "Auth.FarmSelectionRequired";

// Single-flight refresh: concurrent 401s (and the load-time bootstrap) share one
// in-flight refresh call. The refresh token rides the cookie; no body is sent.
// The per-tab latch dedupes within a tab; the cross-tab Web Lock (above) then
// serialises whatever refresh each tab still needs against the other tabs.
export let refreshInFlight: Promise<string> | null = null;

// #532 round 8 — a REFUSED adoption (cross-farm guard) is a distinct failure
// mode from an obsolete generation, a network error, or a rate limit. The
// caller's refresh-failure branch treats it as a session teardown (clear the
// token, fire onUnauthenticated, rethrow the ORIGINAL 401), and the catch
// below re-throws it as itself — never wraps it in StaleSessionError — so a
// parked request can never ride a refusal into a superseded session.
class RefreshAdoptionRefusedError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "RefreshAdoptionRefusedError";
  }
}

async function executeRefresh(generation: number, signal?: AbortSignal): Promise<string> {
  try {
    // #547/#532 — tell the server which per-farm cookie THIS tab expects.
    // Unbound tabs (the load-time bootstrap) omit it; the server accepts one
    // unambiguous per-farm cookie and asks the user to choose when several are
    // present.
    const headers: Record<string, string> = { [CSRF_HEADER]: "1" };
    const expectedAccount = getBoundAccountId();
    if (expectedAccount !== null) headers[ACCOUNT_HEADER] = expectedAccount;
    const res = await raw<AccessTokenResponse>("/auth/refresh", {
      method: "POST",
      headers,
      signal,
    });

    // A SUCCESSFUL refresh rotated the cookie before we got here, so a
    // discarded one leaves live credentials in the browser — revoke them.
    // The generation check runs FIRST: a stale refresh is discarded wholesale,
    // and the cross-farm guard is irrelevant to a refresh that will never be
    // adopted. Running the guard before the generation check would let a
    // stale refresh's token (for a DIFFERENT farm) trigger a refusal + revoke
    // that tears down the NEWER session — exactly what #310 forbids.
    if (sessionGeneration !== generation) {
      await revokeSupersededCookie(res.accessToken);
      throw new StaleSessionError();
    }

    // #532 — defence-in-depth, not the primary guarantee: since the per-farm
    // refresh cookie, a refresh can no longer RETURN another farm's session —
    // the cookie name selects which farm's cookie the server reads, and this
    // tab only ever names its own farm (X-Cluckwork-Account, or the bootstrap
    // path's single cookie). The comparison below is what keeps a tampered or
    // confused server from still being adopted; the COOKIE NAME is what
    // prevents cross-farm adoption.
    //
    // Two rules, and both fail CLOSED:
    //   - a token we cannot attribute to a farm is never adopted. Round 7
    //     adopted a token with no account_id, and the literal string
    //     "not-a-jwt", by trusting a null here;
    //   - a token for a different farm than this tab is bound to is never
    //     adopted, and the binding survives teardown so this cannot be
    //     defeated by simply refreshing twice.
    //
    // An UNBOUND tab is the legitimate cold restore (first load, or after a
    // reload) and adopts — then binds, so it is unbound exactly once.
    const refreshedAccountId = accountIdFromToken(res.accessToken);
    if (refreshedAccountId === null) {
      throw new RefreshAdoptionRefusedError(
        "Refresh returned a token with no usable account claim.",
      );
    }
    const boundAccountId = getBoundAccountId();
    if (boundAccountId !== null && refreshedAccountId !== boundAccountId) {
      throw new RefreshAdoptionRefusedError(
        "Refresh returned a session for a different farm.",
      );
    }

    bindAccount(refreshedAccountId);
    setAccessToken(res.accessToken);
    onTokensChanged?.();
    return res.accessToken;
  } catch (err) {
    // A late FAILURE from an obsolete generation is discarded the same as a
    // late success: the real error (e.g. a genuinely revoked refresh token)
    // might otherwise tear down a newer, still-valid session. No revoke here —
    // a failed refresh issued no cookie to revoke.
    //
    // A REFUSED adoption is re-thrown as itself when the generation is still
    // current: it IS the session teardown, and #547 says the teardown reaches
    // THIS tab only — never a revoke of the cookie the other farm's tab
    // legitimately owns (see the RefreshAdoptionRefusedError branch below).
    // When the generation is stale the refusal is discarded like any other
    // stale failure — the NEWER session keeps its cookie (that revoke is
    // exactly what #310 forbids), and the refusal must not be wrapped in
    // StaleSessionError (that would let a parked request ride the refusal into
    // the newer session instead of failing closed).
    if (err instanceof StaleSessionError) throw err;
    if (sessionGeneration !== generation) {
      if (err instanceof RefreshAdoptionRefusedError) throw err;
      throw new StaleSessionError();
    }
    // #547/#532 — the server refused (Auth.SessionChanged: this farm has no
    // live cookie, or the stored token's farm differs from the one this tab
    // told it to expect) or reported several farms without a selector
    // (Auth.FarmSelectionRequired: the bootstrap path found more than one
    // per-farm cookie). Both are terminal for THIS tab: recoverable by
    // re-bootstrapping (a login on the right farm), never by signing in on
    // another farm's behalf — and never by revoking: another farm's cookie is
    // not ours to touch (#310, same reason the stale-generation branch above
    // declines to revoke).
    //
    // #532 P1-1 — the binding SURVIVES the teardown. Round 9 proved that
    // clearing it here puts the tab back into the trusting cold-restore state:
    // the next refresh omits X-Cluckwork-Account, the bootstrap path runs, and
    // a tab that KNOWS its farm adopts whatever cookie the server picks.
    // Keeping the binding makes the next refresh name the farm again and fail
    // closed instead.
    if (err instanceof ApiError && err.status === 401
        && (err.title === SESSION_CHANGED_TITLE || err.title === FARM_SELECTION_REQUIRED_TITLE)) {
      clearAccessToken();
      onUnauthenticated?.(err.title);
      throw err;
    }
    if (err instanceof RefreshAdoptionRefusedError) {
      // #547/#532 — do NOT revoke here. This refusal means the browser's
      // refresh cookie belongs to a DIFFERENT farm than this tab chose; the
      // other tab that owns it did nothing wrong, and signing it out is
      // exactly what #310 forbids — the same reason the stale-generation
      // branch above declines to revoke. Refusal tears down THIS tab only.
      throw err;
    }
    throw err;
  }
}

async function executeHeldRefresh(
  generation: number,
  parentSignal: AbortSignal,
): Promise<string> {
  // The parent password-change operation is deliberately unbounded because it
  // cannot be replayed safely after an ambiguous commit. Refresh remains
  // replayable, though, and must not hold the cross-tab auth lock forever. Give
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

export async function refreshTokens(heldLock?: HeldAuthCookieLock): Promise<string> {
  // changePassword owns the cross-tab auth lock while apiFetch performs its
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

export function apiGet<T>(path: string, signal?: AbortSignal): Promise<T> {
  return apiFetch<T>(path, { method: "GET", signal });
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

// #606 — extraHeaders is how UnassignFlock attaches the X-Cluckwork-Step-Up
// header, same shape as apiPost/apiPut above; every other caller omits it.
export function apiDelete<T>(
  path: string,
  idempotencyKey?: string,
  extraHeaders?: Record<string, string>,
): Promise<T> {
  return apiFetch<T>(path, {
    method: "DELETE",
    headers: { "Idempotency-Key": idempotencyKey ?? newId(), ...extraHeaders },
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
        // #438 — as in apiFetch: await the queued login before concluding
        // there is no session to download on.
        await settledLogin();
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
        // #438 — same wait as supersededToken: the login that superseded this
        // refresh can still be queued behind it.
        await settledLogin();
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
export async function currentAccessToken(
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
    if (err instanceof StaleSessionError) return await supersededToken(heldAuthCookieLock);
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
async function supersededToken(
  heldAuthCookieLock?: HeldAuthCookieLock,
): Promise<string> {
  // #438 — the superseding login may still be QUEUED behind the very refresh
  // that just failed, so a synchronous peek here would see an empty store and
  // 401 the user out of a session that is about to exist. Wait for it first.
  //
  // NEVER wait when the caller HOLDS the auth cookie lock: that login is queued
  // BEHIND the lock this caller owns, so waiting for it here deadlocks both.
  // The only such caller is the non-replayable password change, which must fail
  // closed on supersession anyway — apiFetch re-checks the generation the
  // moment this returns and throws rather than borrowing the newer session.
  if (!heldAuthCookieLock) await settledLogin();
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
export function isTransientRefreshFailure(err: unknown): boolean {
  if (err instanceof StaleSessionError) return true;
  if (err instanceof ApiError && err.status === 429) return true;
  return err instanceof DOMException && err.name === "AbortError";
}
