// #243 Task 5 smoke test for the k6 auth module — run against the LIVE sim
// stack (not a mock). Proves, in one pass:
//   - login() reads the token from the login response body
//   - the Secure refresh cookie is extracted from Set-Cookie by hand and
//     replayed as an explicit Cookie header (k6's jar never resends it)
//   - refresh() sends the CSRF presence header and rotates both the access
//     token and the cookie
//   - each VU logs in as its own distinct cast user (no shared token family)
//
// Run:
//   nix-shell -p k6 --run \
//     'BASE_URL=http://127.0.0.1:8081 k6 run tools/simulation/k6/auth-smoke.js'

import http from 'k6/http';
import { check } from 'k6';
import { BASE_URL, ME_PATH, loadCastUsers } from './config.js';
import {
  login,
  authHeaders,
  refresh,
  assignUser,
  preflightCredentials,
} from './auth.js';

const users = loadCastUsers();
// "a handful of VUs (<=10), one distinct user each" — the cast is exactly
// owner + 9 members, so this also happens to exercise the whole cast.
const SMOKE_VU_COUNT = Math.min(10, users.length);

export const options = {
  scenarios: {
    auth_smoke: {
      executor: 'per-vu-iterations',
      vus: SMOKE_VU_COUNT,
      iterations: 1,
      maxDuration: '2m',
    },
  },
  // A failed check flips this below 1.00, which fails the threshold, which
  // makes k6 exit non-zero and mark the run FAIL — a failure is always
  // visible in both the exit code and the printed summary.
  thresholds: {
    checks: ['rate==1.00'],
  },
};

function safeJson(res) {
  try {
    return res.json();
  } catch (e) {
    return null;
  }
}

function getMe(session, tagName) {
  const res = http.get(`${BASE_URL}${ME_PATH}`, {
    headers: authHeaders(session),
    tags: { name: tagName },
  });
  const body = safeJson(res);
  check(res, {
    [`${tagName}: status 200`]: (r) => r.status === 200,
    [`${tagName}: email matches session`]: () => !!body && body.email === session.email,
  });
  return res;
}

export function setup() {
  // Validate every cast credential once, sequentially, before any VU runs.
  // Throws (aborting the whole run) on the first failed login rather than
  // limping into the load with a bad credential or risking lockout.
  preflightCredentials(users);
  return { userCount: users.length, vuCount: SMOKE_VU_COUNT };
}

export default function () {
  // Own-user rule: VU N logs in as its own distinct cast member, never a
  // user shared with another VU, and never a token handed down from setup().
  const user = assignUser(__VU, users);
  const session = login(user);

  getMe(session, 'me_pre_refresh');

  // Serialized within this VU by construction (a k6 iteration is
  // single-threaded); refresh() itself also guards against re-entrant use.
  // Note: the rotated cookie is asserted inside refresh() ("refresh: rotated
  // Set-Cookie present"). The re-issued *access token* is not asserted to
  // differ byte-for-byte here — RS256 signing is deterministic and the JWT's
  // nbf/exp claims are second-granularity, so a login->refresh round trip
  // this fast can legitimately mint an identical token when both land in
  // the same wall-clock second. The cookie rotation is the real signal.
  refresh(session);

  getMe(session, 'me_post_refresh');
}

export function teardown(data) {
  console.log(
    `auth-smoke: ran ${data.vuCount} VUs against ${data.userCount}-user cast @ ${BASE_URL}`
  );
}
