// #243 Task 6 — smoke test for bundles.js + personas/*.js against the LIVE
// sim stack (not a mock).
//
// Deliberately a plain default-function script (no `options.scenarios`) so
// the exact command in the Task 6 report — `k6 run --vus 5 --duration 20s
// persona-smoke.js` — controls VU count/duration directly. Each of the 5 VUs
// takes one persona in a fixed round-robin (VU 1 = Owner, 2 = Manager,
// 3 = Sales, 4 = Worker, 5 = ReadOnly for the prescribed --vus 5; a larger
// --vus wraps to a second lap over the same 5 personas), logs in ONCE as its
// own distinct cast user from that role's pool (assignUser, scoped per role
// so a Worker VU never gets handed a Manager's credentials), then runs that
// persona's weighted iterate() loop for the run's duration — maybeRefresh
// included, so a long smoke run exercises the refresh path for free.
//
// Assertions:
//   - checks: rate==1.00 — every authedGet/authedPost/authedGetExpect403
//     check in bundles.js already encodes what "correct" means for that call
//     (2xx for a normal read/write, 403 for a deep-link probe, and a small
//     tolerated set — 403/409/422 — for the one constrained write,
//     dailyEntryScreen's rare save). A failed check means a REAL surprise.
//   - unexpected_status (bundles.js Counter): count==0 — belt-and-suspenders
//     on top of the checks, independent of them: any response status outside
//     a call's own expected/tolerated set bumps this counter, tagged
//     persona/flow/endpoint, regardless of whether a check happened to catch
//     it too. This is the metric that would catch a missing Idempotency-Key
//     (400) or a hot-loop write landing on a unique-constraint wall (409)
//     that the design didn't anticipate and tolerate.
//
// Run:
//   nix-shell -p k6 --run \
//     'BASE_URL=http://127.0.0.1:8081 k6 run --vus 5 --duration 20s \
//       tools/simulation/k6/persona-smoke.js'

import { loadCastUsers } from './config.js';
import { assignUser, login, preflightCredentials } from './auth.js';
import { staticAssets } from './bundles.js';
import * as ownerPersona from './personas/owner.js';
import * as managerPersona from './personas/manager.js';
import * as salesPersona from './personas/sales.js';
import * as workerPersona from './personas/worker.js';
import * as readonlyPersona from './personas/readonly.js';

const ALL_USERS = loadCastUsers();

// Persona -> that role's own slice of the cast, so assignUser (VU -> distinct
// user, wrapping modulo the pool) never crosses role lines.
const USERS_BY_PERSONA = {
  Owner: ALL_USERS.filter((u) => u.role === 'Owner'),
  Manager: ALL_USERS.filter((u) => u.role === 'Manager'),
  Sales: ALL_USERS.filter((u) => u.role === 'Sales'),
  Worker: ALL_USERS.filter((u) => u.role === 'Worker'),
  ReadOnly: ALL_USERS.filter((u) => u.role === 'ReadOnly'),
};

const PERSONA_ORDER = ['Owner', 'Manager', 'Sales', 'Worker', 'ReadOnly'];
const PERSONA_MODULES = {
  Owner: ownerPersona,
  Manager: managerPersona,
  Sales: salesPersona,
  Worker: workerPersona,
  ReadOnly: readonlyPersona,
};

function personaForVu(vuId) {
  return PERSONA_ORDER[(vuId - 1) % PERSONA_ORDER.length];
}

export const options = {
  thresholds: {
    checks: ['rate==1.00'],
    unexpected_status: ['count==0'],
  },
};

export function setup() {
  // Same rationale as auth-smoke.js: validate every cast credential once,
  // sequentially, before any VU starts hammering the API — a bad password in
  // the fixture should abort the smoke run, not surface as a confusing 401
  // buried in the middle of a 20s load window.
  preflightCredentials(ALL_USERS);
}

// Per-VU state: k6 gives each VU its own JS runtime, so this module-level
// `let` is per-VU (never shared across VUs) and persists across that VU's
// iterations — login once, reuse (and maybeRefresh) the session thereafter.
let session = null;
let persona = null;

export default function () {
  if (session === null) {
    persona = personaForVu(__VU);
    const pool = USERS_BY_PERSONA[persona];
    const user = assignUser(__VU, pool);
    session = login(user);
    // One "page load" per VU: proves the staticAssets bundle (index.html +
    // a discovered hashed asset/manifest) works end to end. Not part of any
    // persona's weighted mix — Kestrel serves the SPA shell identically
    // regardless of who's logged in, so it doesn't belong in the
    // role-specific frequency tables; Task 7/9 can layer it in as its own
    // light "browser" scenario alongside the persona VUs.
    staticAssets(persona);
  }

  session = PERSONA_MODULES[persona].iterate(session);
}
