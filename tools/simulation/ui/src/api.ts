// tools/simulation/ui/src/api.ts — a small HTTP client for the things a BROWSER
// is the wrong instrument for.
//
// SCOPE, AND WHY IT IS NARROW. The point of this suite is what a user sees in a
// real browser. So this module is used for exactly three jobs, none of which is
// asserting on application behaviour:
//
//   1. PREFLIGHT — is the stack up, and does the fixture actually contain the
//      data the specs are about to assert on (preflight.ts).
//   2. GROUND TRUTH the browser cannot see — the farm's configured IANA timezone,
//      which every date field's bounds derive from (farm.ts).
//   3. CLEANUP/ARRANGEMENT for a write spec that needs a known starting state.
//
// It is NOT a second way to test the app. An assertion that a persona "can" do
// something belongs in a browser, driving the same screens the farm uses. If a
// spec is reaching for this module to make an assertion, that is the signal it
// has drifted into re-testing the API — which is what k6 (#243) and the
// integration suite already do, better.

import { API_PREFIX, BASE_URL } from "./env";
import type { CastMember } from "./cast";

export class ApiProbeError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly body: string,
  ) {
    super(message);
    this.name = "ApiProbeError";
  }
}

/** Sign in and return the bearer token. Throws on anything but 200. */
export async function signInForToken(member: CastMember): Promise<string> {
  const res = await fetch(`${BASE_URL}${API_PREFIX}/auth/login`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ email: member.email, password: member.password }),
  });
  if (!res.ok) {
    throw new ApiProbeError(
      `Sign-in failed for ${member.email} (${res.status}). The cast file and the seeded `
        + `database have diverged — re-run \`bash tools/simulation/reset.sh\`.`,
      res.status,
      await res.text(),
    );
  }
  const body = (await res.json()) as { accessToken: string };
  return body.accessToken;
}

/** An authenticated GET returning parsed JSON. Throws on a non-2xx. */
export async function apiGet<T>(token: string, path: string): Promise<T> {
  const res = await fetch(`${BASE_URL}${API_PREFIX}${path}`, {
    headers: { authorization: `Bearer ${token}` },
  });
  if (!res.ok) {
    throw new ApiProbeError(`GET ${path} -> ${res.status}`, res.status, await res.text());
  }
  return (await res.json()) as T;
}

/** An authenticated GET returning only the status — for probing an expected refusal. */
export async function apiGetStatus(token: string, path: string): Promise<number> {
  const res = await fetch(`${BASE_URL}${API_PREFIX}${path}`, {
    headers: { authorization: `Bearer ${token}` },
  });
  return res.status;
}

/** Is the app answering at all? Used by preflight before anything more specific. */
export async function isReady(): Promise<{ ok: boolean; detail: string }> {
  try {
    const res = await fetch(`${BASE_URL}/health/ready`);
    return { ok: res.ok, detail: `${res.status} ${res.statusText}` };
  } catch (cause) {
    return { ok: false, detail: `unreachable (${(cause as Error).message})` };
  }
}
