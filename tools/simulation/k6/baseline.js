// #243 Task 7 — k6 baseline scenario: warmup + capacity as two explicit
// SEQUENTIAL phases, with every reported number filtered to phase:capacity.
//
// WHY two k6 `scenarios` (not just a tag on one flat run): k6's built-in
// aggregates (http_req_duration, checks, the end-of-run summary) fold in
// EVERY sample regardless of tags — tagging warmup alone does not remove it
// from the reported percentiles. Two scenarios, `capacity.startTime` pushed
// past `warmup`'s entire window (duration + gracefulStop + a buffer — see
// "WHY THE DRAIN GAP IS THE FIX" below), gives a genuinely non-overlapping
// run (proven below); a `tags: {phase: 'warmup'|'capacity'}` on EACH scenario (not on
// individual requests) then labels every metric sample k6 produces during
// that scenario — http_req_duration, checks, http_req_failed, http_reqs, and
// even our own bundles.js `unexpected_status` Counter — with that phase,
// automatically, for every request bundles.js/personas/*.js make (verified
// live against this stack; see the Task 7 report). Referencing a tag
// combination in `thresholds` (even with an empty condition list, `[]`) is
// what makes k6 materialize that exact submetric in `handleSummary`'s
// `data.metrics` — that's how the {phase:capacity} and {phase:capacity,
// persona:X}/{phase:capacity,flow:X} breakdowns below get built without
// touching bundles.js's own {persona,flow,endpoint} tagging at all.
//
// #243 TASK 9 MUST-FIX — ALL 10 CAST USERS / ALL 5 PERSONAS IN CAPACITY:
// Task 7 originally shipped WARMUP_VUS=2 + CAPACITY_VUS=8 (sum=10=cast size)
// specifically to dodge a VU-id collision (see "WHY THE DRAIN GAP IS THE
// FIX" below) — but that meant capacity exercised only 8 of the 10 cast
// users, so a single-instance persona (Owner/Manager/Sales — each exactly 1
// user) could be ENTIRELY ABSENT from a given capacity run's numbers. #243
// is a 10-user day: capacity MUST exercise all 10 users / all 5 personas,
// every run. Fixed here:
//   - CAPACITY_VUS now defaults to 10 (the full cast, all 5 personas in the
//     seeded 1/1/1/3/4 ratio), not 8.
//   - `capacity.startTime` is no longer just `WARMUP_DURATION`. It is pushed
//     out to `WARMUP_DURATION + warmup's own gracefulStop + a buffer` (see
//     INTER_PHASE_DRAIN_BUFFER_SECONDS below) — an INTER-PHASE DRAIN GAP
//     wide enough that every warmup VU, including a gracefulStop straggler,
//     is provably finished before capacity's first VU starts.
//
// WHY THE DRAIN GAP IS THE FIX (not cosmetic): k6 does not number VUs
// contiguously per scenario, and — this is the load-bearing fact — it does
// NOT always simply sum `vus` across scenarios either. Direct-probed live
// against this k6 version (`k6 v2.0.0`), both with and without a drain gap:
//   - WITH a drain gap that fully separates warmup's window (duration +
//     gracefulStop) from capacity's start: k6 REUSES the same underlying VU
//     pool for both scenarios, sized to `max(WARMUP_VUS, CAPACITY_VUS)`.
//     With CAPACITY_VUS=10 > WARMUP_VUS=2, capacity consistently received
//     EXACTLY `__VU` in {1..10} — a complete, collision-free residue system
//     for `assignUser`'s `(vuId-1) % castSize` mapping, confirmed across
//     repeated probes (same script, every run).
//   - WITHOUT a sufficient gap (`capacity.startTime == WARMUP_DURATION`,
//     with no allowance for warmup's `gracefulStop` straggler window): k6
//     keeps extra VU slots alive to cover the brief overlap, so the pool
//     grows past the cast size (ids up to 11, 12 observed live) — and
//     capacity can then receive BOTH members of an aliased pair (e.g. ids 1
//     and 11, both ≡0 mod 10), i.e. two CONCURRENT capacity VUs assigned the
//     SAME cast user — exactly the token-family collision this harness
//     forbids. This is the exact failure Task 7's original 2+8=10 sizing
//     was dodging; the drain gap removes the need to dodge it at all by
//     keeping the two scenarios' VU pools from ever needing to coexist.
// Because warmup is fully drained before capacity's first login, it is safe
// for warmup's (whichever, scheduler-assigned) VU ids to alias with cast
// users capacity will ALSO use later — that's a sequential re-login onto an
// already-idle session, not a concurrent collision. Warmup therefore does
// not need, and does not attempt, persona-faithful coverage; it borrows
// whichever cast members its VU ids happen to hash to, purely to prime the
// JIT/EF-model/connection pool.
//
// Documented fallback (unchanged from Task 7): if you raise CAPACITY_VUS
// past the cast size, `assignUser` cycles deterministically (modulo cast
// size) rather than erroring, but the zero-collision guarantee stops
// holding and setup() prints an explicit warning. Regenerate a bigger cast
// via bootstrap.sh (raise Simulation__MaxVus) for a real run that needs
// more concurrent capacity VUs than the cast has people.
//
// PERSONA RATIO: with CAPACITY_VUS=10=CAST_SIZE and the drain gap above,
// capacity deterministically gets all 10 cast members every run — i.e. the
// full seeded 1/1/1/3/4 (Owner/Manager/Sales/Worker/ReadOnly) ratio, not a
// scaled-down subset.
//
// REAL RUN REQUIREMENT: access tokens live 15 minutes (DEFAULT_REFRESH_SKEW
// in auth.js proactively refreshes ~60s before expiry). To actually exercise
// the refresh path under load, CAPACITY_DURATION must be >= 2x that lifetime
// (>= ~35m) for a real baseline rep — Task 9/10's job. The default here
// stays short (2m) for fast dev iteration; handleSummary prints a note when
// the configured duration is short.

import { sleep } from 'k6';
import { Counter } from 'k6/metrics';
import { loadCastUsers } from './config.js';
import { assignUser, login, maybeRefresh, preflightCredentials } from './auth.js';
import {
  dashboard, history, reports, sessionBootstrap, staticAssets, stock, thinkSleep, weightedPick,
} from './bundles.js';
import * as ownerPersona from './personas/owner.js';
import * as managerPersona from './personas/manager.js';
import * as salesPersona from './personas/sales.js';
import * as workerPersona from './personas/worker.js';
import * as readonlyPersona from './personas/readonly.js';

const ALL_USERS = loadCastUsers();
const CAST_SIZE = ALL_USERS.length;

const PERSONA_MODULES = {
  Owner: ownerPersona,
  Manager: managerPersona,
  Sales: salesPersona,
  Worker: workerPersona,
  ReadOnly: readonlyPersona,
};
const PERSONA_ORDER = ['Owner', 'Manager', 'Sales', 'Worker', 'ReadOnly'];

// Every distinct `flow` tag bundles.js hands out (see tagsFor() call sites
// across bundles.js) — kept as a literal list, not derived, so a new bundle
// added later doesn't silently vanish from (or crash) the breakdown; it just
// won't appear until added here.
const FLOW_ORDER = [
  'sessionBootstrap', 'dashboard', 'reports', 'stock', 'history', 'dailyEntry',
  'sales', 'customers', 'audit', 'users', 'export', 'deepLinkProbe', 'staticAssets',
];

// PR #279 review — per-cast-user capacity coverage (hardens the persona-tag
// coverage check above, which only ever proved 5 aggregated ROLE tags showed
// up, not that all castSize INDIVIDUAL users each got exactly one capacity
// VU). Incremented exactly ONCE per VU's lifetime (guarded by
// capacityOwnerRecorded below, not on every relogin) so a legitimate
// re-login after a fatal refresh 401 never inflates a user's count — a count
// of anything other than 1 for a given userIndex is a real signal: 0 means
// that cast user never got a capacity VU at all, >1 means two DIFFERENT VUs
// both claimed the same user (the exact VU-pool-aliasing collision the
// inter-phase drain gap above exists to prevent).
const capacityUserOwner = new Counter('capacity_user_owner');

// --- env / params ----------------------------------------------------------

const WARMUP_VUS = Number(__ENV.WARMUP_VUS || 2);
const WARMUP_DURATION = __ENV.WARMUP_DURATION || '20s';
// #243 Task 9 MUST-FIX: defaults to the FULL cast (all 5 personas in the
// seeded 1/1/1/3/4 ratio — CAST_SIZE, not a hardcoded 10, so this can never
// silently drift from whatever bootstrap.sh actually generated), see the
// file header. Safe because of the inter-phase drain gap computed below, not
// VU-id arithmetic alone. PR #279 review: setup() below now HARD-ERRORS
// (not just warns) if CAPACITY_VUS != CAST_SIZE — the per-user coverage
// guarantee only holds at exact equality (see capacityUserOwner above).
const CAPACITY_VUS = Number(__ENV.CAPACITY_VUS || CAST_SIZE);
const CAPACITY_DURATION = __ENV.CAPACITY_DURATION || '2m';
// PR #279 review: default changed from tools/simulation/out/ (written into
// by the app container as root — see README's "Why no host-writable path
// for the collector") to the monitor/out/ sibling dir the monitor scripts
// already use, which is always host-writable. run-baseline.sh always
// overrides this explicitly per rep; this default only matters for a
// by-hand `k6 run baseline.js` invocation.
const SUMMARY_OUT = __ENV.SUMMARY_OUT || 'tools/simulation/monitor/out/summary.json';
// How long warmup's executor waits for an in-flight iteration to finish
// after its `duration` elapses before k6 force-stops it. Applied as both
// the warmup scenario's own `gracefulStop` and (below) as one component of
// capacity's drain-gap startTime — the two must agree, so it is one
// constant, not a literal duplicated in two places.
const WARMUP_GRACEFUL_STOP = __ENV.WARMUP_GRACEFUL_STOP || '5s';
// Extra safety margin (seconds) added on top of `WARMUP_DURATION +
// WARMUP_GRACEFUL_STOP` before capacity's startTime — covers real-world
// jitter (container scheduling, HTTP round-trip variance) beyond the exact
// boundary that was already sufficient in repeated local probes. See "WHY
// THE DRAIN GAP IS THE FIX" in the file header.
const INTER_PHASE_DRAIN_BUFFER_SECONDS = Number(__ENV.INTER_PHASE_DRAIN_BUFFER_SECONDS || 5);

// Access tokens live 15 minutes; a real baseline rep needs >= 2x that so the
// refresh path is actually exercised under load. See the file header.
const REAL_RUN_MIN_CAPACITY_SECONDS = 2 * 15 * 60;

function parseDurationSeconds(str) {
  const re = /(\d+)\s*(h|m|s)/g;
  let total = 0;
  let found = false;
  let match = re.exec(str);
  while (match !== null) {
    found = true;
    const value = Number(match[1]);
    if (match[2] === 'h') total += value * 3600;
    else if (match[2] === 'm') total += value * 60;
    else total += value;
    match = re.exec(str);
  }
  if (!found) {
    throw new Error(`cannot parse duration string: "${str}" (expected e.g. "20s", "2m", "1h30m")`);
  }
  return total;
}

const CAPACITY_DURATION_SECONDS = parseDurationSeconds(CAPACITY_DURATION);
const WARMUP_DURATION_SECONDS = parseDurationSeconds(WARMUP_DURATION);
const WARMUP_GRACEFUL_STOP_SECONDS = parseDurationSeconds(WARMUP_GRACEFUL_STOP);

// #243 Task 9 MUST-FIX — the inter-phase drain gap: capacity's startTime
// pushed past warmup's ENTIRE window (nominal duration + gracefulStop
// straggler allowance + a buffer), so k6 reuses one VU pool for both
// scenarios (sized to max(WARMUP_VUS, CAPACITY_VUS) — verified live, see
// file header) instead of needing extra concurrent slots that would let
// capacity receive an aliased pair of VU ids. This is what makes
// CAPACITY_VUS=10=castSize collision-free by construction.
const CAPACITY_START_SECONDS = WARMUP_DURATION_SECONDS + WARMUP_GRACEFUL_STOP_SECONDS
  + INTER_PHASE_DRAIN_BUFFER_SECONDS;
const CAPACITY_START_TIME = `${CAPACITY_START_SECONDS}s`;

// Stagger initial logins across capacity VUs so they don't all hit /login in
// the same instant capacity starts (a thundering-herd login stampede) — one
// jitter draw per VU, applied once, before that VU's first login. Capped at
// a quarter of the run so a short smoke run still spends most of its time
// actually loaded, not staggering.
const CAPACITY_LOGIN_JITTER_SECONDS = Number(
  __ENV.CAPACITY_LOGIN_JITTER_SECONDS
    || Math.max(1, Math.min(CAPACITY_VUS, 10, Math.floor(CAPACITY_DURATION_SECONDS / 4))),
);

// --- scenarios ---------------------------------------------------------

export const options = {
  scenarios: {
    // Discarded: JIT/EF-model/connection-pool warmup only. Bursty on
    // purpose (no jitter) — a sudden concurrent hit is exactly what best
    // grows the Npgsql pool and forces first-request JIT/EF-model costs to
    // pay off before capacity starts measuring.
    warmup: {
      executor: 'constant-vus',
      vus: WARMUP_VUS,
      duration: WARMUP_DURATION,
      tags: { phase: 'warmup' },
      exec: 'warmupFn',
      gracefulStop: WARMUP_GRACEFUL_STOP,
    },
    // Sequential, not concurrent: startTime is warmup's ENTIRE window
    // (nominal duration + gracefulStop + a buffer — CAPACITY_START_TIME, see
    // the file header's "WHY THE DRAIN GAP IS THE FIX"), not just
    // WARMUP_DURATION. This is the #243 Task 9 MUST-FIX: without the
    // gracefulStop+buffer allowance, a warmup straggler still in flight when
    // capacity starts forces k6 to keep extra VU-pool slots alive
    // concurrently, which can hand capacity two VU ids that alias to the
    // same cast user (a real, confirmed-live collision). With the full
    // drain gap, warmup is provably 100% finished before capacity's first
    // VU starts, so k6 reuses one VU pool sized to CAPACITY_VUS and
    // `assignUser` maps all 10 capacity VUs to all 10 distinct cast users,
    // every run.
    capacity: {
      executor: 'constant-vus',
      vus: CAPACITY_VUS,
      duration: CAPACITY_DURATION,
      startTime: CAPACITY_START_TIME,
      tags: { phase: 'capacity' },
      exec: 'capacityFn',
    },
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  thresholds: buildThresholds(),
};

function buildThresholds() {
  const t = {
    // The two capacity-only gates the Task 7 verify step checks live. These
    // are the CORRECT correctness gates for this workload — checks() and
    // unexpected_status (bundles.js) already encode "expected 403 deep-link
    // probe" / "tolerated 403/409/422 on the one constrained write" as
    // success, unlike k6's own http_req_failed classification below.
    'checks{phase:capacity}': ['rate==1.00'],
    'unexpected_status{phase:capacity}': ['count==0'],
    // Informational only (empty condition list — never gates the run):
    // k6 marks ANY non-2xx/3xx response http_req_failed by default, which
    // includes our OWN deliberately-provoked 403s (deepLinkProbe) and
    // tolerated 403/409/422 (dailyEntryScreen's rare write) — so a nonzero
    // rate here is expected for this workload, not a signal of trouble.
    // checks{phase:capacity} and unexpected_status{phase:capacity} above are
    // the real correctness gates.
    'http_req_failed{phase:capacity}': [],
    // PR #279 review: this was a live gate (`p(95)<3000`) — an absolute
    // latency SLA, which contradicts the locked Fork B decision ("no
    // absolute latency/throughput claim from this uncapped, co-located,
    // non-sizing shakeout box" — see findings/TEMPLATE.md's banner). Empty
    // condition list now, same as the other informational entries below:
    // this ONLY exists to force k6 to materialize the submetric in
    // data.metrics for handleSummary/the findings doc's relative-shape
    // rendering — it never gates the run. Correctness (checks==100%,
    // unexpected_status==0 above) is the only thing that gates.
    'http_req_duration{phase:capacity}': [],
    // Referenced with an empty condition list purely to force k6 to
    // materialize the submetric in data.metrics for handleSummary — these
    // never fail the run on their own.
    'http_reqs{phase:capacity}': [],
    'http_reqs{phase:warmup}': [],
    'iterations{phase:capacity}': [],
    'checks{phase:warmup}': [],
    'unexpected_status{phase:warmup}': [],
  };
  for (const persona of PERSONA_ORDER) {
    t[`http_req_duration{phase:capacity,persona:${persona}}`] = [];
  }
  for (const flow of FLOW_ORDER) {
    t[`http_req_duration{phase:capacity,flow:${flow}}`] = [];
  }
  // PR #279 review — per-cast-user capacity coverage (see capacityUserOwner
  // above): one threshold per cast-user index so k6 materializes each in
  // data.metrics, exactly the same pattern as persona/flow above.
  for (let i = 0; i < CAST_SIZE; i += 1) {
    t[`capacity_user_owner{userIndex:${i}}`] = [];
  }
  return t;
}

// --- setup ---------------------------------------------------------------

export function setup() {
  // Validate every cast credential once, sequentially, before any VU starts
  // — aborts the whole run on the first bad login rather than limping into
  // load with a stale credential or risking the 5-fails/15-min lockout.
  preflightCredentials(ALL_USERS);

  // PR #279 review: was a warning (only when CAPACITY_VUS > CAST_SIZE) — now
  // a hard error on ANY inequality, in either direction. The per-user
  // capacity coverage guarantee (capacityUserOwner above: every cast user
  // appears as exactly one capacity VU) only holds when CAPACITY_VUS ==
  // CAST_SIZE exactly — see the file header's "WHY THE DRAIN GAP IS THE
  // FIX". Below CAST_SIZE silently drops some users from capacity entirely;
  // above it forces assignUser to wrap and share a login across
  // concurrently-active VUs. run-baseline.sh also preflights this same
  // check before starting any rep, so this is defense in depth for a direct
  // `k6 run baseline.js` invocation.
  if (CAPACITY_VUS !== CAST_SIZE) {
    throw new Error(
      `baseline: CAPACITY_VUS (${CAPACITY_VUS}) must equal the cast size ` +
        `(${CAST_SIZE}) exactly — the per-user capacity coverage guarantee ` +
        '(every cast user owned by exactly one capacity VU, zero collisions) ' +
        'only holds at equality. Regenerate a differently-sized cast via ' +
        'bootstrap.sh (Simulation__Managers/Sales/Workers/ReadOnly) rather ' +
        'than setting CAPACITY_VUS independently.',
    );
  }
  if (WARMUP_VUS > CAPACITY_VUS) {
    // The #243 Task 9 drain-gap fix relies on k6 reusing one VU pool sized
    // to max(WARMUP_VUS, CAPACITY_VUS) once the two scenarios provably don't
    // overlap in time (see file header). That reuse was only verified live
    // for the WARMUP_VUS < CAPACITY_VUS shape this harness ships with —
    // flip it and the pool sizes to WARMUP_VUS instead, which the
    // (vuId-1)%castSize mapping still tolerates (it wraps deterministically)
    // but no longer guarantees capacity sees all `min(CAPACITY_VUS,
    // CAST_SIZE)` distinct cast users on every run.
    console.warn(
      `baseline: WARMUP_VUS (${WARMUP_VUS}) exceeds CAPACITY_VUS ` +
        `(${CAPACITY_VUS}) — the drain-gap VU-pool-reuse guarantee this ` +
        `harness relies on (see file header) was only verified live for ` +
        `WARMUP_VUS < CAPACITY_VUS. Keep warmup smaller than capacity for a ` +
        `real run.`,
    );
  }
  if (CAPACITY_DURATION_SECONDS < REAL_RUN_MIN_CAPACITY_SECONDS) {
    console.warn(
      `baseline: CAPACITY_DURATION (${CAPACITY_DURATION} = ` +
        `${CAPACITY_DURATION_SECONDS}s) is under the ${REAL_RUN_MIN_CAPACITY_SECONDS}s ` +
        `(2x the 15-minute access-token lifetime) a REAL baseline rep needs ` +
        `to exercise the refresh path under load. Fine for dev verification; ` +
        `Task 9/10 must set a longer CAPACITY_DURATION for the real run.`,
    );
  }

  return {
    castSize: CAST_SIZE,
    warmupVus: WARMUP_VUS,
    capacityVus: CAPACITY_VUS,
    capacityStartTime: CAPACITY_START_TIME,
  };
}

// --- warmup (role-agnostic; discarded) ------------------------------------

const WARMUP_WEIGHTS = [
  ['sessionBootstrap', 20],
  ['dashboard', 30],
  ['stock', 20],
  ['reports', 15],
  ['history', 15],
];

let warmupSession = null;
let warmupPersonaLabel = null;

export function warmupFn() {
  if (warmupSession === null) {
    const user = assignUser(__VU, ALL_USERS);
    warmupSession = login(user);
    warmupPersonaLabel = user.role;
  }
  warmupSession = maybeRefresh(warmupSession);

  switch (weightedPick(WARMUP_WEIGHTS)) {
    case 'sessionBootstrap':
      sessionBootstrap(warmupSession, warmupPersonaLabel);
      break;
    case 'dashboard':
      dashboard(warmupSession, warmupPersonaLabel);
      break;
    case 'stock':
      stock(warmupSession, warmupPersonaLabel);
      break;
    case 'reports':
      reports(warmupSession, warmupPersonaLabel);
      break;
    case 'history':
      history(warmupSession, warmupPersonaLabel);
      break;
    default:
      break;
  }
  thinkSleep(0.5, 0.4, 3);
}

// --- capacity (own-user login, staggered, real persona loop) -------------

let capacitySession = null;
let capacityPersonaModule = null;
// PR #279 review: recorded exactly once per VU's lifetime (NOT reset when
// capacitySession is nulled after a fatal refresh 401 below) — a relogin by
// the SAME VU must never look like a second, different VU claiming the same
// cast user. See capacityUserOwner's own comment above.
let capacityOwnerRecorded = false;

export function capacityFn() {
  if (capacitySession === null) {
    // Staggered first login — see CAPACITY_LOGIN_JITTER_SECONDS above.
    sleep(Math.random() * CAPACITY_LOGIN_JITTER_SECONDS);
    const user = assignUser(__VU, ALL_USERS);
    const module = PERSONA_MODULES[user.role];
    if (!module) {
      throw new Error(`baseline: no persona module for role "${user.role}" (user ${user.email})`);
    }
    capacitySession = login(user);
    capacityPersonaModule = module;
    if (!capacityOwnerRecorded) {
      const userIndex = ALL_USERS.indexOf(user);
      capacityUserOwner.add(1, { userIndex: String(userIndex) });
      capacityOwnerRecorded = true;
    }
    // One "page load" per VU, matching persona-smoke.js — proves the static
    // shell serves fine under whatever concurrent load capacity is putting
    // on the app, not part of any persona's weighted mix.
    staticAssets(user.role);
  }

  try {
    capacitySession = capacityPersonaModule.iterate(capacitySession);
  } catch (err) {
    // PR #279 review: a fatal refresh 401 (auth.js's refresh() — the server
    // burns the whole token family on a reused/stale refresh cookie) used to
    // leave `capacitySession` pointing at the now-permanently-invalid
    // session forever, since the assignment above never completes when
    // iterate() throws. Every SUBSEQUENT iteration for this VU would then
    // retry maybeRefresh() against that same dead session and fail again —
    // one real failure cascading into failed checks for the rest of the
    // run. Null it here so the NEXT iteration's `capacitySession === null`
    // branch above re-logs-in from scratch; this one iteration's
    // already-recorded failed check(s)/thrown error remain the only
    // casualty. Re-throw so k6 still surfaces this iteration's failure
    // exactly as it did before.
    capacitySession = null;
    throw err;
  }
}

export function teardown(data) {
  console.log(
    `baseline: cast=${data.castSize} warmupVus=${data.warmupVus} ` +
      `capacityVus=${data.capacityVus} capacityStartTime=${data.capacityStartTime} ` +
      `@ ${__ENV.BASE_URL || 'http://127.0.0.1:8081'}`,
  );
}

// --- handleSummary: capacity-only JSON + a human-readable stdout summary --

function extractTrend(metric) {
  if (!metric || !metric.values) return null;
  const v = metric.values;
  return {
    p50: v.med, p95: v['p(95)'], p99: v['p(99)'], avg: v.avg, min: v.min, max: v.max,
  };
}

function countOf(metric) {
  return metric && metric.values ? metric.values.count : null;
}

function rateOf(metric) {
  return metric && metric.values ? metric.values.rate : null;
}

function formatTrend(label, trend) {
  if (!trend) return `  ${label}: (no data)`;
  const fmt = (n) => (typeof n === 'number' ? `${n.toFixed(1)}ms` : 'n/a');
  return `  ${label}: p50=${fmt(trend.p50)} p95=${fmt(trend.p95)} p99=${fmt(trend.p99)} avg=${fmt(trend.avg)} max=${fmt(trend.max)}`;
}

export function handleSummary(data) {
  const m = data.metrics;

  const totalRequests = countOf(m.http_reqs);
  const capacityRequests = countOf(m['http_reqs{phase:capacity}']);
  const warmupRequests = countOf(m['http_reqs{phase:warmup}']);

  const byPersona = {};
  for (const persona of PERSONA_ORDER) {
    byPersona[persona] = extractTrend(m[`http_req_duration{phase:capacity,persona:${persona}}`]);
  }
  const byFlow = {};
  for (const flow of FLOW_ORDER) {
    byFlow[flow] = extractTrend(m[`http_req_duration{phase:capacity,flow:${flow}}`]);
  }

  // PR #279 review — per-cast-user capacity coverage: each entry is exactly
  // how many times that cast user's VU recorded ownership (see
  // capacityUserOwner above) — the expected/healthy value is 1 for every
  // index. 0 = that user never got a capacity VU; >1 = two different VUs
  // both claimed it (a real collision).
  const byCastUser = {};
  const missingCastUsers = [];
  const duplicatedCastUsers = [];
  for (let i = 0; i < CAST_SIZE; i += 1) {
    const count = countOf(m[`capacity_user_owner{userIndex:${i}}`]) || 0;
    byCastUser[i] = { email: ALL_USERS[i].email, role: ALL_USERS[i].role, ownerCount: count };
    if (count === 0) missingCastUsers.push(ALL_USERS[i].email);
    else if (count > 1) duplicatedCastUsers.push(ALL_USERS[i].email);
  }
  const castUserCoverageOk = missingCastUsers.length === 0 && duplicatedCastUsers.length === 0;

  const notes = [];
  if (totalRequests !== null && warmupRequests !== null && capacityRequests !== null
    && totalRequests !== warmupRequests + capacityRequests) {
    notes.push(
      `allPhaseRequestCount (${totalRequests}) != warmup (${warmupRequests}) + capacity ` +
        `(${capacityRequests}); the ${totalRequests - warmupRequests - capacityRequests} request(s) ` +
        'gap is expected — setup() runs preflightCredentials() (one login per cast user) ' +
        'before either scenario starts, so those requests carry no phase tag at all.',
    );
  }
  if (CAPACITY_DURATION_SECONDS < REAL_RUN_MIN_CAPACITY_SECONDS) {
    notes.push(
      `CAPACITY_DURATION (${CAPACITY_DURATION}) is shorter than the ` +
        `${REAL_RUN_MIN_CAPACITY_SECONDS}s (2x 15-min token life) a real baseline ` +
        'rep needs to exercise the refresh path — this run is dev/smoke only.',
    );
  }
  // #243 Task 9 MUST-FIX validity self-check: if CAPACITY_VUS==CAST_SIZE
  // (the default), every persona must show up here with real requests — a
  // missing/zero persona means the drain-gap fix (see file header) did not
  // hold for this run and capacity silently dropped a persona again.
  if (CAPACITY_VUS >= CAST_SIZE) {
    const missingPersonas = PERSONA_ORDER.filter((persona) => {
      const trend = byPersona[persona];
      return !trend || !Number.isFinite(trend.p50);
    });
    if (missingPersonas.length > 0) {
      notes.push(
        `CAPACITY_VUS (${CAPACITY_VUS}) >= castSize (${CAST_SIZE}) but ` +
          `byPersona has NO capacity requests for: ${missingPersonas.join(', ')}. ` +
          'This should not happen with the inter-phase drain gap in place — ' +
          'treat this run as INVALID for persona-coverage purposes and investigate.',
      );
    }
  }
  // PR #279 review: role-level coverage above can pass (all 5 roles present)
  // while still missing an individual user, or — the collision the drain
  // gap exists to prevent — silently double-counting one user under two
  // concurrent VUs. This is the stricter, per-user check.
  if (!castUserCoverageOk) {
    const parts = [];
    if (missingCastUsers.length > 0) parts.push(`missing: ${missingCastUsers.join(', ')}`);
    if (duplicatedCastUsers.length > 0) parts.push(`duplicated (claimed by >1 VU): ${duplicatedCastUsers.join(', ')}`);
    notes.push(
      `Per-cast-user capacity coverage FAILED (${parts.join('; ')}). Every cast user must be ` +
        'owned by exactly one capacity VU — see capacityUserOwner/byCastUser and the file ' +
        'header\'s "WHY THE DRAIN GAP IS THE FIX". Treat this run as INVALID and investigate.',
    );
  }

  const summary = {
    generatedAt: new Date().toISOString(),
    baseUrl: __ENV.BASE_URL || 'http://127.0.0.1:8081',
    params: {
      warmupVus: WARMUP_VUS,
      warmupDuration: WARMUP_DURATION,
      warmupGracefulStop: WARMUP_GRACEFUL_STOP,
      capacityVus: CAPACITY_VUS,
      capacityDuration: CAPACITY_DURATION,
      capacityStartTime: CAPACITY_START_TIME,
      interPhaseDrainBufferSeconds: INTER_PHASE_DRAIN_BUFFER_SECONDS,
      capacityLoginJitterSecondsMax: CAPACITY_LOGIN_JITTER_SECONDS,
      castSize: CAST_SIZE,
    },
    totals: {
      allPhaseRequestCount: totalRequests,
      warmupRequestCount: warmupRequests,
      capacityRequestCount: capacityRequests,
    },
    capacity: {
      requestsPerSecond: rateOf(m['http_reqs{phase:capacity}']),
      httpReqDuration: extractTrend(m['http_req_duration{phase:capacity}']),
      httpReqFailedRate: rateOf(m['http_req_failed{phase:capacity}']),
      checksRate: rateOf(m['checks{phase:capacity}']),
      unexpectedStatusCount: countOf(m['unexpected_status{phase:capacity}']),
      iterationCount: countOf(m['iterations{phase:capacity}']),
      byPersona,
      byFlow,
      byCastUser,
      castUserCoverageOk,
    },
    notes,
  };

  const lines = [];
  lines.push('#243 k6 baseline — warmup + capacity (capacity-only report)');
  lines.push(`  base URL: ${summary.baseUrl}`);
  lines.push(`  warmup:   ${WARMUP_VUS} VUs x ${WARMUP_DURATION} (discarded, gracefulStop=${WARMUP_GRACEFUL_STOP})`);
  lines.push(`  capacity: ${CAPACITY_VUS} VUs x ${CAPACITY_DURATION} (starts @ ${CAPACITY_START_TIME}, login jitter <= ${CAPACITY_LOGIN_JITTER_SECONDS}s)`);
  lines.push(`  requests: total=${totalRequests} warmup=${warmupRequests} capacity=${capacityRequests}`);
  lines.push(`  capacity req/s: ${summary.capacity.requestsPerSecond?.toFixed(2)}`);
  lines.push(`  capacity http_req_failed rate: ${summary.capacity.httpReqFailedRate}`);
  lines.push(`  capacity checks rate: ${summary.capacity.checksRate}`);
  lines.push(`  capacity unexpected_status count: ${summary.capacity.unexpectedStatusCount}`);
  lines.push(formatTrend('capacity http_req_duration (all)', summary.capacity.httpReqDuration));
  lines.push('  by persona:');
  for (const persona of PERSONA_ORDER) {
    lines.push(formatTrend(`    ${persona}`, byPersona[persona]));
  }
  lines.push('  by flow:');
  for (const flow of FLOW_ORDER) {
    lines.push(formatTrend(`    ${flow}`, byFlow[flow]));
  }
  lines.push(`  per-cast-user capacity coverage OK (all ${CAST_SIZE}, exactly once each): ${castUserCoverageOk}`);
  if (notes.length > 0) {
    lines.push('  notes:');
    for (const note of notes) lines.push(`    - ${note}`);
  }
  const text = `${lines.join('\n')}\n`;

  const out = {
    stdout: text,
  };
  out[SUMMARY_OUT] = JSON.stringify(summary, null, 2);
  return out;
}
