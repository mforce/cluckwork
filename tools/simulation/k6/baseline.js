// #243 Task 7 — k6 baseline scenario: warmup + capacity as two explicit
// SEQUENTIAL phases, with every reported number filtered to phase:capacity.
//
// WHY two k6 `scenarios` (not just a tag on one flat run): k6's built-in
// aggregates (http_req_duration, checks, the end-of-run summary) fold in
// EVERY sample regardless of tags — tagging warmup alone does not remove it
// from the reported percentiles. Two scenarios, `capacity.startTime` set to
// exactly `warmup`'s duration, gives a genuinely non-overlapping run (proven
// below); a `tags: {phase: 'warmup'|'capacity'}` on EACH scenario (not on
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
// EACH-VU-OWN-USER, why capacity defaults to 8 VUs (not the full 10-user
// cast) when warmup defaults to 2: k6 numbers VUs uniquely for the WHOLE
// test, not contiguously per scenario — confirmed live (two scenarios of
// 3+3 VUs handed out ids like warmup={1,2,6}/capacity={3,4,5} in one run and
// warmup={1,2,4}/capacity={3,5,6} in another; same script, different result
// each run). `assignUser` (auth.js) maps a VU id to a cast user via
// `(vuId-1) % castSize` — that's a bijection with ZERO collision risk ONLY
// while every id k6 could ever hand out across the WHOLE test stays inside
// [1, castSize] (10 here): direct-probed live and found a REAL collision
// otherwise — WARMUP_VUS=2 + CAPACITY_VUS=10 (12 total) put both VU id 1 and
// VU id 11 in the capacity scenario in one run, and (1-1)%10 == (11-1)%10,
// i.e. two CONCURRENT capacity VUs assigned to the SAME cast user — exactly
// the token-family collision the plan forbids. Defaults below
// (WARMUP_VUS=2, CAPACITY_VUS=8, sum=10=castSize) keep every id k6 can ever
// hand out inside [1,10], which makes `assignUser` collision-free by
// construction for BOTH phases, always, regardless of which ids the
// scheduler happens to give which scenario. If you raise WARMUP_VUS or
// CAPACITY_VUS past that sum, setup() prints an explicit warning: the
// script still runs (assignUser cycles deterministically, the plan's own
// documented fallback: "cycle deterministically with a clear note") but the
// zero-collision guarantee no longer holds — regenerate a bigger cast via
// bootstrap.sh (raise Simulation__MaxVus) for a real run that needs more
// concurrent VUs than the cast has people.
//
// PERSONA RATIO: capacity's actual persona mix is READ from whichever cast
// users land in the capacity scenario (`user.role`, not forced by VU-id
// arithmetic) — see the note above on why VU-id-to-scenario assignment is
// scheduler-controlled and not something this script can pin down. With the
// safe defaults (2 warmup + 8 capacity out of a 10-person cast: 1 Owner/1
// Manager/1 Sales/3 Worker/4 ReadOnly), capacity gets whichever 8 of the 10
// cast members warmup didn't borrow — i.e. the full ratio minus up to 2
// members, not always the exact 1/1/1/2/3 scaled split. This is the
// documented, deliberate trade: an EXACT forced ratio would need a
// phase-local VU rank k6 does not expose to scripts, and would only be
// achievable by giving up the zero-collision guarantee, which is the
// harder, explicitly-stated safety rule. See the Task 7 report for the live
// probe that established this.
//
// REAL RUN REQUIREMENT: access tokens live 15 minutes (DEFAULT_REFRESH_SKEW
// in auth.js proactively refreshes ~60s before expiry). To actually exercise
// the refresh path under load, CAPACITY_DURATION must be >= 2x that lifetime
// (>= ~35m) for a real baseline rep — Task 9/10's job. The default here
// stays short (2m) for fast dev iteration; handleSummary prints a note when
// the configured duration is short.

import { sleep } from 'k6';
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

// --- env / params ----------------------------------------------------------

const WARMUP_VUS = Number(__ENV.WARMUP_VUS || 2);
const WARMUP_DURATION = __ENV.WARMUP_DURATION || '20s';
const CAPACITY_VUS = Number(__ENV.CAPACITY_VUS || 8);
const CAPACITY_DURATION = __ENV.CAPACITY_DURATION || '2m';
const SUMMARY_OUT = __ENV.SUMMARY_OUT || 'tools/simulation/out/summary.json';

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
      gracefulStop: '5s',
    },
    // Sequential, not concurrent: startTime == warmup's own duration, so
    // capacity's first iteration cannot start before warmup's nominal
    // window has elapsed (a `gracefulStop` straggler from warmup may still
    // be in flight for a few seconds after — those requests stay tagged
    // phase:warmup, so they never leak into the phase:capacity numbers
    // below; see the Task 7 report for the live proof).
    capacity: {
      executor: 'constant-vus',
      vus: CAPACITY_VUS,
      duration: CAPACITY_DURATION,
      startTime: WARMUP_DURATION,
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
    // Loose dev-box sanity bound, NOT a prod SLA — this is an uncapped,
    // sidecar, no-TLS, single-IP shakeout box (plan Fork B); an honest
    // absolute latency/throughput claim is Task 9/10's job, not this one's.
    'http_req_duration{phase:capacity}': ['p(95)<3000'],
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
  return t;
}

// --- setup ---------------------------------------------------------------

export function setup() {
  // Validate every cast credential once, sequentially, before any VU starts
  // — aborts the whole run on the first bad login rather than limping into
  // load with a stale credential or risking the 5-fails/15-min lockout.
  preflightCredentials(ALL_USERS);

  const totalVus = WARMUP_VUS + CAPACITY_VUS;
  if (CAPACITY_VUS > CAST_SIZE) {
    console.warn(
      `baseline: CAPACITY_VUS (${CAPACITY_VUS}) exceeds the cast size ` +
        `(${CAST_SIZE}) on its own — capacity VUs will deterministically ` +
        `CYCLE through the cast (assignUser wraps modulo cast size), so ` +
        `some concurrently-active capacity VUs WILL share a login/user. ` +
        `Regenerate a bigger cast via bootstrap.sh (raise Simulation__MaxVus) ` +
        `for a real run needing more concurrent VUs than the cast has people.`,
    );
  } else if (totalVus > CAST_SIZE) {
    console.warn(
      `baseline: WARMUP_VUS + CAPACITY_VUS (${totalVus}) exceeds the cast ` +
        `size (${CAST_SIZE}) — k6 assigns VU ids uniquely per TEST, not ` +
        `contiguously per scenario (confirmed nondeterministic across runs; ` +
        `see the file header), so assignUser's collision-free guarantee no ` +
        `longer holds for every id the scheduler could hand out. Defaults ` +
        `(2 + 8 = 10) stay at the cast size specifically to avoid this — ` +
        `you have overridden past it.`,
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
    // One "page load" per VU, matching persona-smoke.js — proves the static
    // shell serves fine under whatever concurrent load capacity is putting
    // on the app, not part of any persona's weighted mix.
    staticAssets(user.role);
  }

  capacitySession = capacityPersonaModule.iterate(capacitySession);
}

export function teardown(data) {
  console.log(
    `baseline: cast=${data.castSize} warmupVus=${data.warmupVus} ` +
      `capacityVus=${data.capacityVus} @ ${__ENV.BASE_URL || 'http://127.0.0.1:8081'}`,
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

  const summary = {
    generatedAt: new Date().toISOString(),
    baseUrl: __ENV.BASE_URL || 'http://127.0.0.1:8081',
    params: {
      warmupVus: WARMUP_VUS,
      warmupDuration: WARMUP_DURATION,
      capacityVus: CAPACITY_VUS,
      capacityDuration: CAPACITY_DURATION,
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
    },
    notes,
  };

  const lines = [];
  lines.push('#243 k6 baseline — warmup + capacity (capacity-only report)');
  lines.push(`  base URL: ${summary.baseUrl}`);
  lines.push(`  warmup:   ${WARMUP_VUS} VUs x ${WARMUP_DURATION} (discarded)`);
  lines.push(`  capacity: ${CAPACITY_VUS} VUs x ${CAPACITY_DURATION} (login jitter <= ${CAPACITY_LOGIN_JITTER_SECONDS}s)`);
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
