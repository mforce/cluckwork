// #243 k6 harness — auth module.
//
// Ground truth (verified against the live sim stack, see #243 Task 5):
//   - POST /api/v1/auth/login  {farmCode,email,password} -> 200 {accessToken,accessTokenExpiry}
//     + Set-Cookie: cluckwork_rt_<account-id>=... (HttpOnly, Secure, SameSite=Strict, Path=/api/v1/auth)
//   - The cookie is Secure and the sim stack is plain HTTP, so k6's cookie
//     jar will NOT resend it. We extract the full name and value from the
//     Set-Cookie response header ourselves and send them back as an explicit Cookie
//     header on every subsequent refresh call.
//   - POST /api/v1/auth/refresh, header Cookie: cluckwork_rt_<account-id>=<value> + header
//     X-Cluckwork-Auth: 1 (presence-only), NO body -> 200
//     {accessToken,accessTokenExpiry} + a NEW rotated Set-Cookie.
//   - Authed calls: Authorization: Bearer <accessToken>.
//
// Correctness rules this module enforces:
//   - Each VU logs in as its OWN distinct user (assignUser) — never shares a
//     user/token-family across VUs, never distributes a setup() token to
//     multiple VUs.
//   - refresh() is not reentrant per session: a `refreshing` flag on the
//     session object blocks a second concurrent refresh call on the same
//     session, so callers MUST serialize refreshes within a VU (a single VU
//     is single-threaded in k6, so simply not overlapping async refresh
//     calls on one session is enough).
//   - A 401 from refresh is treated as fatal: it fails a check and throws.
//     It is NEVER retried with the same (now-ambiguous/stale) cookie — the
//     server burns the whole token family on a reused/stale refresh, so a
//     retry would only cascade the failure.

import http from 'k6/http';
import { check } from 'k6';
import {
  BASE_URL,
  AUTH_PATHS,
  CSRF_HEADER_NAME,
  CSRF_HEADER_VALUE,
  REFRESH_COOKIE_NAME_PREFIX,
} from './config.js';

// How close to `accessTokenExpiry` (in seconds) before maybeRefresh() will
// proactively refresh. Access tokens live 15 minutes; 60s of slack is ample
// for a single request round-trip.
const DEFAULT_REFRESH_SKEW_SECONDS = 60;

/**
 * Pull a single per-farm cookie's full name and value (still percent-encoded,
 * exactly as the server sent it) out of a k6 response's Set-Cookie header. k6
 * folds a single Set-Cookie header into a string, but returns an array when a
 * response sets more than one cookie — handle both.
 */
function extractSetCookie(res, cookieNamePrefix) {
  const raw = res.headers['Set-Cookie'];
  if (!raw) {
    return null;
  }
  const entries = Array.isArray(raw) ? raw : [raw];
  for (const entry of entries) {
    const firstPair = entry.split(';', 1)[0].trim();
    const eqIndex = firstPair.indexOf('=');
    if (eqIndex === -1) {
      continue;
    }
    const name = firstPair.slice(0, eqIndex);
    if (name.startsWith(`${cookieNamePrefix}_`)) {
      return { name, value: firstPair.slice(eqIndex + 1) };
    }
  }
  return null;
}

function safeJson(res) {
  try {
    return res.json();
  } catch (e) {
    return null;
  }
}

/**
 * Log in as `user` ({email,password}). Returns a fresh session:
 *   { email, token, expiry, cookieName, cookie, refreshing }
 * Throws (and marks failed checks) on anything but a clean 200 with both a
 * token and the refresh cookie.
 */
export function login(user) {
  const res = http.post(
    `${BASE_URL}${AUTH_PATHS.LOGIN}`,
    JSON.stringify({ farmCode: 'default-farm', email: user.email, password: user.password }),
    {
      headers: { 'Content-Type': 'application/json' },
      tags: { name: 'auth_login' },
    }
  );

  const body = safeJson(res);
  const cookie = extractSetCookie(res, REFRESH_COOKIE_NAME_PREFIX);

  const ok = check(res, {
    'login: status 200': (r) => r.status === 200,
    'login: has accessToken': () => !!(body && body.accessToken),
    'login: has accessTokenExpiry': () => !!(body && body.accessTokenExpiry),
    'login: Set-Cookie has per-farm refresh cookie': () => cookie !== null,
  });

  if (!ok) {
    throw new Error(
      `login failed for ${user.email}: status=${res.status} body=${res.body}`
    );
  }

  return {
    email: user.email,
    token: body.accessToken,
    expiry: body.accessTokenExpiry,
    cookieName: cookie.name,
    cookie: cookie.value,
    refreshing: false,
  };
}

/** Bearer auth header for an authed call using `session`. */
export function authHeaders(session) {
  return { Authorization: `Bearer ${session.token}` };
}

/**
 * Rotate `session`'s access token + refresh cookie. Mutates and returns
 * `session`. A 401 is fatal: it marks a failed check and throws WITHOUT
 * retrying (re-presenting a stale/rotated cookie only cascades the family
 * burn onto every other request the VU makes).
 */
export function refresh(session) {
  if (session.refreshing) {
    throw new Error(
      `refresh() called for ${session.email} while a refresh was already ` +
        'in-flight for this session — refreshes must be serialized within a VU'
    );
  }

  session.refreshing = true;
  try {
    const res = http.post(`${BASE_URL}${AUTH_PATHS.REFRESH}`, null, {
      headers: {
        Cookie: `${session.cookieName}=${session.cookie}`,
        [CSRF_HEADER_NAME]: CSRF_HEADER_VALUE,
      },
      tags: { name: 'auth_refresh' },
    });

    if (res.status === 401) {
      check(res, { 'refresh: not 401 (stale/reused cookie)': () => false });
      throw new Error(
        `refresh returned 401 for ${session.email} — treating as fatal, ` +
          'not retrying with the old cookie'
      );
    }

    const body = safeJson(res);
    const newCookie = extractSetCookie(res, REFRESH_COOKIE_NAME_PREFIX);

    const ok = check(res, {
      'refresh: status 200': (r) => r.status === 200,
      'refresh: has accessToken': () => !!(body && body.accessToken),
      'refresh: has accessTokenExpiry': () => !!(body && body.accessTokenExpiry),
      'refresh: rotated Set-Cookie present': () => newCookie !== null,
    });

    if (!ok) {
      throw new Error(
        `refresh failed for ${session.email}: status=${res.status} body=${res.body}`
      );
    }

    session.token = body.accessToken;
    session.expiry = body.accessTokenExpiry;
    session.cookieName = newCookie.name;
    session.cookie = newCookie.value;

    return session;
  } finally {
    session.refreshing = false;
  }
}

/**
 * Refresh `session` in place if it is within `skewSeconds` of
 * `accessTokenExpiry` (default 60s). No-op otherwise. Returns the session.
 */
export function maybeRefresh(session, skewSeconds = DEFAULT_REFRESH_SKEW_SECONDS) {
  const expiryMs = Date.parse(session.expiry);
  if (Number.isNaN(expiryMs)) {
    // Can't tell — be conservative and refresh rather than risk running on
    // an expired token.
    return refresh(session);
  }
  if (expiryMs - Date.now() <= skewSeconds * 1000) {
    return refresh(session);
  }
  return session;
}

/**
 * Deterministic VU -> distinct cast-user mapping. k6 VU ids are 1-based;
 * this wraps modulo the cast size so it also works for a max-VU run larger
 * than the cast (at the cost of reuse only once every `users.length` VUs —
 * fine for a smoke run, revisit if capacity VUs ever exceed the cast pool).
 */
export function assignUser(vuId, users) {
  if (!users || users.length === 0) {
    throw new Error('assignUser: users list is empty');
  }
  const index = (vuId - 1) % users.length;
  return users[index];
}

/**
 * Log in as every user once, sequentially, to validate the whole cast's
 * credentials before any load runs. Aborts (throws) on the FIRST failure —
 * it does not keep going and does not retry — so a load run never starts
 * against a bad credential or trips the login lockout (5 fails/15 min).
 * Intended to be called once from setup().
 */
export function preflightCredentials(users) {
  if (!users || users.length === 0) {
    throw new Error('preflightCredentials: users list is empty');
  }
  for (const user of users) {
    // login() itself throws + fails a check on anything but a clean 200;
    // let that propagate immediately rather than catching and continuing.
    login(user);
  }
}
