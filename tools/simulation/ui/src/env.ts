// tools/simulation/ui/src/env.ts — where the stack is, and how patient to be.

/**
 * The sim stack's app. `reset.sh` pins `APP_PORT=8081` and prints this URL when
 * it finishes; k6/config.js defaults to the same origin under the same override
 * name, so one `BASE_URL` moves both harnesses together.
 */
export const BASE_URL = process.env.BASE_URL?.trim() || "http://127.0.0.1:8081";

/**
 * Opt-in for specs that spend real wall-clock time proving a real deadline —
 * today just the genuine 15-minute access-token expiry. Off by default: a suite
 * nobody will run because it takes twenty minutes is a suite that finds nothing.
 * See session-refresh.spec.ts, which runs the fast version either way and says
 * plainly which of the two it just proved.
 */
export const RUN_SLOW_SPECS = isTruthy(process.env.CLUCKWORK_E2E_SLOW);

/**
 * Set by the canary (#386) so shared specs know they are competing with k6 for
 * the backend and must not treat ordinary slowness as a functional failure.
 */
export const UNDER_LOAD = isTruthy(process.env.CLUCKWORK_E2E_UNDER_LOAD);

function isTruthy(v: string | undefined): boolean {
  if (!v) return false;
  const s = v.trim().toLowerCase();
  return s === "1" || s === "true" || s === "yes";
}

/**
 * The API path prefix. Every SPA call is same-origin `/api/v1/...` — there is no
 * separate API host to configure, because the container serves the built SPA and
 * the API together (AGENTS.md: "single container").
 */
export const API_PREFIX = "/api/v1";

/**
 * Presence-only CSRF header the API requires on `/auth/refresh`. Any value is
 * accepted; the header must be there. Mirrors k6/config.js's CSRF_HEADER_NAME —
 * and the SPA's own client.ts, which is what the browser will actually send.
 */
export const CSRF_HEADER_NAME = "X-Cluckwork-Auth";

/** Base prefix of the per-farm HttpOnly refresh cookies the API sets. */
export const REFRESH_COOKIE_NAME_PREFIX = "cluckwork_rt";

/** Finds a per-farm refresh cookie, optionally restricted by an additional condition. */
export function findRefreshCookie<T extends { name: string }>(
  cookies: readonly T[],
  predicate: (cookie: T) => boolean = () => true,
): T | undefined {
  const perFarmPrefix = `${REFRESH_COOKIE_NAME_PREFIX}_`;
  return cookies.find((cookie) => cookie.name.startsWith(perFarmPrefix) && predicate(cookie));
}
