// #532 — the per-farm refresh cookie makes the round-6 cross-tab bootstrap
// broadcast unnecessary: a login writes ONLY its own farm's cookie, so the
// other tabs' cookies are no longer invalidated by it. The BroadcastChannel
// receiver is deleted outright with it — round 9 showed it was untested and
// that any same-origin context could post an unauthenticated or malformed
// message to force-logout every tab and strip the client half of the guard.
import { clearAccessToken, getAccessToken, setAccessToken, bindAccount, clearBoundAccount, getBoundAccountId } from "../auth/tokenStore";
import { accountIdFromToken } from "../auth/claims";
import { newId } from "../lib/ids";
import type { AccessTokenResponse, LoginRequest } from "./types";
import { raw, CSRF_HEADER, ACCOUNT_HEADER } from "./request";
import { StaleSessionError, revokeSupersededCookie, onTokensChanged } from "./sessionState";
import { withAuthCookieLock, REFRESH_TIMEOUT_MS, abortInFlightAuthCookieOperation } from "./authCookieLock";
import { apiFetch, apiPost, refreshInFlight } from "./refresh";

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
export let sessionGeneration = 0;

// #438 — the settlement handle of the login currently in flight, or null. A
// request parked on a refresh that came back superseded cannot simply peek at
// the token store any more: since login joins the cross-tab cookie lock below,
// the login that superseded that refresh may still be QUEUED behind it and
// therefore hold nothing yet. The parked caller awaits this instead, the same
// way concurrent 401s share refreshInFlight.
let loginInFlight: Promise<void> | null = null;

// #438 — resolve once there is no login still pending. Its FAILURE is the login
// caller's to report; a parked request only needs to know no login is on its way
// before it decides whether a session exists.
//
// #648 review — LOOP ON IDENTITY, not on a single snapshot. A snapshot returns
// as soon as the login it happened to observe settles, and logins replace one
// another: with A pending, B replacing it, and A then settling as stale, the
// parked caller would resume while B is still queued, read no token, and 401 —
// the same premature 401 this function exists to prevent, one layer out. Each
// pass consumes exactly one settled flight, so this terminates; re-reading after
// the await and comparing IDENTITY (never truthiness) is the same rule the
// generation check follows. The identity guard on `loginInFlight`'s clear below
// is what makes the re-read meaningful: without it a stale settle nulls a newer
// flight's handle and this loop exits early on the `!flight` branch.
export async function settledLogin(): Promise<void> {
  for (;;) {
    const flight = loginInFlight;
    if (!flight) return;
    // A rejection is as much a settlement as a success, and just as stale: the
    // caller must re-read rather than conclude anything from it.
    await flight.catch(() => undefined);
    if (loginInFlight === flight) return;
  }
}

export function login(body: LoginRequest): Promise<void> {
  // #310 — bump first, and SYNCHRONOUSLY, before this login queues for the
  // lock: login is a NEWER session than anything already in flight (a
  // bootstrap refresh, say), so its resolution must be the one that wins, and
  // any earlier flight must see its captured generation go stale.
  const generation = ++sessionGeneration;
  // #438 — published before the first await, so a request that parks while
  // this login is still queued can find it.
  const flight = performLogin(body, generation).finally(() => {
    if (loginInFlight === flight) loginInFlight = null;
  });
  loginInFlight = flight;
  return flight;
}

// #438 — login takes the SAME cross-tab cookie lock as refresh/changePassword.
// sessionGeneration and the token store are per-tab module state, so a refresh
// in another tab cannot see this login's generation bump: its completion looks
// uncontested there and #393's stale branch never fires, leaving real network
// arrival order to decide whose Set-Cookie survives. Ordering is the fix a
// page-owned lock can actually give — any in-flight refresh settles, cookie
// applied, before this login sends a byte, so login's cookie is provably last.
async function performLogin(body: LoginRequest, generation: number): Promise<void> {
  // #648 review — `withAuthCookieLock`'s own timeout starts when this login's
  // TURN arrives, so it caps the request and not the wait for the turn. That was
  // enough while every holder of this lock was itself bounded, but
  // changePassword holds it deliberately unbounded (it cannot be replayed after
  // an ambiguous commit), and the lock name is global — one stalled password
  // change would otherwise park a sign-in forever, including one in another tab
  // on another farm. So the clock starts HERE, at the call, and a turn that
  // arrives past it sends nothing: an abandoned login must never put credentials
  // on the wire or a cookie in the jar behind the user's back.
  let abandoned = false;
  let abandon!: () => void;
  const abandonedAtDeadline = new Promise<never>((_, reject) => {
    abandon = () => {
      abandoned = true;
      // #648 review — GIVE THE GENERATION BACK. The eager bump in `login()` is
      // what makes the newest sign-in win, but a login that never sends has not
      // superseded anything: leaving the bump in place makes an in-flight
      // password change discard a response the SERVER ALREADY COMMITTED, revoke
      // the cookie it just issued, and report failure to a user whose password
      // did change. Failing closed is the right instinct for a write that might
      // not have landed; this one has landed.
      //
      // Two conditions, and both are load-bearing. `sessionGeneration ===
      // generation` means nothing bumped after us, so this rollback restores
      // exactly the value every earlier flight captured. `refreshInFlight ===
      // null` means no refresh can have captured OUR value while we waited —
      // one that had would be discarded by the rollback and have its own valid
      // cookie revoked, trading this bug for a forced re-auth. When either
      // fails we keep the conservative behaviour rather than guess.
      if (sessionGeneration === generation && refreshInFlight === null)
        sessionGeneration = generation - 1;
      reject(
        new DOMException("Timed out waiting for the auth cookie lock.", "AbortError"),
      );
    };
  });
  const deadline = setTimeout(abandon, REFRESH_TIMEOUT_MS);
  // The server sets the HttpOnly refresh cookie; the body returns only the
  // access token, which lives in memory for this tab's lifetime.
  const queued = withAuthCookieLock(
    (signal) =>
      // Superseded while queued (a logout, or a newer login on another form) —
      // never send the credentials at all, exactly as changePassword does.
      sessionGeneration !== generation
        ? Promise.reject(new StaleSessionError())
        : abandoned
          ? Promise.reject(
              new DOMException("Abandoned before its turn on the auth cookie lock.", "AbortError"),
            )
          : raw<AccessTokenResponse>(
            "/auth/login",
            {
              method: "POST",
              body: JSON.stringify(body),
              signal,
            },
            // #532 — login's response token is decoded to bind this tab to
            // its farm. The REAL server token always carries the account_id
            // claim (the cross-farm guard depends on it), so this is an
            // attribution of a known token, not adoption of an unknown one —
            // unlike a refresh, which must fail closed on an unattributable
            // token.
            "",
          ),
    // Login is replayable (the user simply signs in again), so it must not hold
    // the cross-tab lock indefinitely on a hung request, unlike a password
    // change. Same cap as refresh.
    REFRESH_TIMEOUT_MS,
  );
  // The abandoned branch still settles the queued attempt later; nothing awaits
  // it by then, and an unobserved rejection must not surface as an unhandled one.
  queued.catch(() => undefined);
  const res = await Promise.race([queued, abandonedAtDeadline]).finally(() =>
    clearTimeout(deadline),
  );
  // Superseded while in flight (e.g. a logout landed before this resolved) —
  // do not resurrect a session the user already ended. The response already
  // set a refresh cookie, so revoke it too, not just the in-memory token.
  if (sessionGeneration !== generation) {
    await revokeSupersededCookie(res.accessToken);
    throw new StaleSessionError();
  }
  setAccessToken(res.accessToken);
  // #532 — a tab that logged in must be bound to its farm before its first
  // refresh, or that refresh is treated as a cold restore and could adopt a
  // foreign session. bindAccount(null) leaves the tab unbound when the token
  // carries no usable account claim: the next refresh then fails closed on the
  // unattributable-token rule instead of trusting a fresh farm.
  bindAccount(accountIdFromToken(res.accessToken));
  // #532 — no cross-tab announcement: a login now writes only its own
  // farm's cookie, so the other tabs' cookies are untouched and their
  // sessions remain valid. (The round-6 BroadcastChannel bootstrap is
  // deleted — see the import comment at the top of this file.)
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
  // Refresh and password-change both replace this farm's HttpOnly cookie.
  // Keep the password change inside the same cross-tab lock as refresh,
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
    await revokeSupersededCookie(res.accessToken);
    throw new StaleSessionError();
  }
  setAccessToken(res.accessToken);
  // #532 — changePassword adopts a token (the server hands this device a fresh
  // pair after revoking every other session), so like login it must bind the
  // tab to its farm. Without this, the tab's next refresh would omit
  // X-Cluckwork-Account and fall into the bootstrap path instead of naming
  // the farm it just authenticated.
  bindAccount(accountIdFromToken(res.accessToken));
  onTokensChanged?.();
}

// #308/#360/#606 — re-confirms the CURRENT password to mint a short-lived
// step-up grant for one durable user-access mutation: creating any user,
// resetting any user's password, changing any user's role, changing a login
// email, disabling a user, re-enabling a user, assigning a worker to a flock,
// or removing a worker's flock assignment. The grant is returned to the caller and
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
  // #336/#532 — capture BEFORE clearing: the bearer identifies the actor and
  // the farm binding selects this tab's refresh cookie. The clears below are
  // deliberately synchronous; read either value afterward and logout would
  // lose the context needed for its best-effort server-side revocation.
  const bearer = getAccessToken();
  const accountId = getBoundAccountId();
  clearAccessToken();
  // #532 — explicit logout is the user ending the session: the binding is
  // cleared with the token, and the next login rebinds.
  clearBoundAccount();
  try {
    // Cookie-authenticated: the HttpOnly refresh cookie rides along and the CSRF
    // header satisfies the check. Always fires — JS can't read the HttpOnly
    // cookie to know whether a session exists, so we always ask the server to
    // revoke + clear it.
    //
    // #336/#532 — the bearer records the actor when this tab still has one;
    // the account header selects the same tab's per-farm cookie even after the
    // access token expires. Both remain optional because the endpoint is
    // AllowAnonymous, and it is exempt from the Idempotency-Key requirement.
    const headers: Record<string, string> = { [CSRF_HEADER]: "1" };
    if (accountId !== null) headers[ACCOUNT_HEADER] = accountId;
    await raw<void>(
      "/auth/logout",
      { method: "POST", headers },
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
