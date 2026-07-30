// #243 Task 6 — SPA call bundles.
//
// One exported function per screen/flow, each firing that screen's real API
// call bundle: same paths, query params and request bodies the SPA sends
// (verified against src/Cluckwork.Api/Endpoints/**/*.cs and web/src/api/**,
// then live-curled against the sim stack — see the Task 6 report). Every
// call carries `{ persona, flow, endpoint }` tags (Task 7/9 slice p95 by
// them) and every mutating call carries a globally-unique Idempotency-Key
// built by makeIdemKeyFactory().
//
// Ground truth worth keeping visible here (surprises found while grounding
// this file against the live stack, not just the source):
//   - RecordDailyEntry (POST /daily-entries) is an UPSERT keyed on
//     (accountId, farmId, houseId, flockId, date): a second POST for the same
//     natural key updates the existing Draft row instead of 409ing. BUT the
//     domain re-validates the NEW totalEggs against the EXISTING grade-line
//     sum (grades omitted = "leave unchanged" — DailyEntryEndpoints.cs), so a
//     small totalEggs against a flock that already has bigger seeded grade
//     lines 422s as "DailyEntry.GradesExceedTotal". A genuinely concurrent
//     first-insert for the same key can also 409 (unique constraint). Hence:
//     never in the hot loop, a generous totalEggs floor, and 403/409/422 all
//     tolerated (not failed checks) on the rare write — see dailyEntryScreen.
//   - The farm's seeded timezone (America/Chicago) is BEHIND UTC, so a
//     UTC "today" date can land after the farm's own "today" for part of the
//     day and trip the validator's not-future rule. Using UTC "yesterday" for
//     the daily-entry write date is safe for any real timezone offset.
//   - AddOrderItem/CreateSalesOrder need only {customerId, orderDate} and
//     {productId, quantity} — Unit/UnitPriceMinorUnits default from the
//     product's own catalog row when omitted (AddOrderItemCommand.cs).

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';
import { BASE_URL } from './config.js';
import { authHeaders } from './auth.js';

// Every response whose status falls OUTSIDE that call's own
// expected/tolerated set bumps this — independent of the per-request
// `check()`s below, so a threshold on it (persona-smoke.js) gives a single
// "zero unexpected 4xx/5xx" gate that can't be muddied by an expected 403
// deep-link probe or a tolerated 409/422 constrained-write outcome. Tagged
// the same as the request that produced it, so a breakdown by
// persona/flow/endpoint is one `k6 run --summary-export` away.
export const unexpectedStatus = new Counter('unexpected_status');

// --- run identity / idempotency --------------------------------------------

// "A per-run constant": module (init) code runs once per VU at VU startup, so
// this is fixed for that VU's whole run when RUN_ID isn't supplied — and
// identical across every VU when the caller DOES set RUN_ID (Task 7/9 wiring
// every VU against one shared run label). Either way it's stable for the
// life of one `k6 run` invocation, which is all the idempotency contract
// needs: VU/ITER/opIndex already guarantee uniqueness within a run.
const RUN_ID = __ENV.RUN_ID || `local-${Date.now()}`;
const REP = __ENV.REP || '0';

// One key per intended write. `opIndex` disambiguates multiple writes inside
// the SAME iteration (e.g. sales: create-order then add-item both fire while
// __VU/__ITER are unchanged) — call the returned function once per write, in
// order, and never reuse a value except to retry that EXACT operation.
//
// `phase` (default 'capacity', the only caller today — see baseline.js's
// personas/*.js) is baked into the key itself, not just an implicit
// assumption: baseline.js's warmup and capacity scenarios REUSE the same
// k6 VU pool once the inter-phase drain gap separates them (see baseline.js
// file header), so a future warmup iteration could otherwise land on the
// same {persona, __VU, __ITER, opIndex} tuple as a capacity iteration and
// collide on the exact same idempotency key. warmupFn today never calls a
// write bundle at all (dailyEntryScreen/salesBundle are only reachable via
// personas/*.js's capacity `iterate()`), so this is a guard against a
// FUTURE regression, not a live bug — see the file header's "Ground truth".
export function makeIdemKeyFactory(persona, phase = 'capacity') {
  let opIndex = 0;
  return function nextIdemKey() {
    const key = `${RUN_ID}:${REP}:${phase}:${persona}:${__VU}:${__ITER}:${opIndex}`;
    opIndex += 1;
    return key;
  };
}

// --- tags --------------------------------------------------------------

function tagsFor(persona, flow, endpoint) {
  return { persona, flow, endpoint };
}

// --- dates -------------------------------------------------------------

function isoDate(d) {
  return d.toISOString().slice(0, 10);
}
export function today() {
  return isoDate(new Date());
}
export function daysAgo(n) {
  const d = new Date();
  d.setUTCDate(d.getUTCDate() - n);
  return isoDate(d);
}

// --- think time ----------------------------------------------------------

// Lognormal-ish think time between iteration actions (Box-Muller normal ->
// exp()), clamped so an unlucky draw never parks a VU for an absurd amount of
// time. medianSeconds is the sleep at z=0.
export function thinkSleep(medianSeconds = 1.5, sigma = 0.6, maxSeconds = 8) {
  const u1 = Math.random() || 1e-9;
  const u2 = Math.random();
  const z = Math.sqrt(-2 * Math.log(u1)) * Math.cos(2 * Math.PI * u2);
  const seconds = Math.min(maxSeconds, medianSeconds * Math.exp(sigma * z));
  sleep(Math.max(0.05, seconds));
}

// --- weighted pick -----------------------------------------------------

// weights: [[name, weight], ...]. Not seeded/deterministic run-to-run (k6 has
// no seedable RNG hook exposed to scripts), but the distribution converges on
// the given weights over enough iterations — "deterministic-ish" per the plan.
export function weightedPick(weights) {
  const total = weights.reduce((sum, [, w]) => sum + w, 0);
  let r = Math.random() * total;
  for (const [name, w] of weights) {
    if (r < w) return name;
    r -= w;
  }
  return weights[weights.length - 1][0];
}

// --- request helpers -----------------------------------------------------

function safeJson(res) {
  try {
    return res.json();
  } catch (e) {
    return null;
  }
}

function noteIfUnexpected(res, tags, okStatuses) {
  if (!okStatuses.includes(res.status)) unexpectedStatus.add(1, tags);
}

function authedGet(session, path, tags, expectedStatuses = [200]) {
  const res = http.get(`${BASE_URL}${path}`, {
    headers: authHeaders(session),
    tags,
  });
  check(res, {
    [`${tags.endpoint}: status ${expectedStatuses.join('/')}`]: (r) =>
      expectedStatuses.includes(r.status),
  });
  noteIfUnexpected(res, tags, expectedStatuses);
  return res;
}

// Expected-403 reads (deep links the SPA never renders a nav entry for, but
// the API still gates — #127/#243 review). A 403 here is success, not a
// failed check.
function authedGetExpect403(session, path, tags) {
  const res = http.get(`${BASE_URL}${path}`, {
    headers: authHeaders(session),
    tags,
  });
  check(res, {
    [`${tags.endpoint}: status 403 (expected — no SPA route gate)`]: (r) => r.status === 403,
  });
  noteIfUnexpected(res, tags, [403]);
  return res;
}

function authedPost(session, path, body, tags, idemKey, expectedStatuses, toleratedStatuses = []) {
  const res = http.post(`${BASE_URL}${path}`, body === undefined ? null : JSON.stringify(body), {
    headers: Object.assign(
      { 'Content-Type': 'application/json', 'Idempotency-Key': idemKey },
      authHeaders(session),
    ),
    tags,
  });
  const label = toleratedStatuses.length > 0
    ? `${tags.endpoint}: status ${expectedStatuses.join('/')} (or tolerated ${toleratedStatuses.join('/')})`
    : `${tags.endpoint}: status ${expectedStatuses.join('/')}`;
  check(res, {
    [label]: (r) => expectedStatuses.includes(r.status) || toleratedStatuses.includes(r.status),
  });
  noteIfUnexpected(res, tags, expectedStatuses.concat(toleratedStatuses));
  return res;
}

// --- sessionBootstrap (/me + /account) ----------------------------------

export function sessionBootstrap(session, persona) {
  authedGet(session, '/api/v1/me', tagsFor(persona, 'sessionBootstrap', 'me'));
  authedGet(session, '/api/v1/account', tagsFor(persona, 'sessionBootstrap', 'account'));
}

// --- dashboard (F5 — 5-way fan-out, Dashboard.tsx) ----------------------

export function dashboard(session, persona) {
  const canSeeSales = persona !== 'ReadOnly';
  const d = today();
  authedGet(session, '/api/v1/flocks?limit=500', tagsFor(persona, 'dashboard', 'flocks_list'));
  authedGet(
    session,
    `/api/v1/daily-entries?from=${d}&to=${d}&limit=500`,
    tagsFor(persona, 'dashboard', 'daily_entries_today'),
  );
  authedGet(session, '/api/v1/stock', tagsFor(persona, 'dashboard', 'stock_summary'));
  if (canSeeSales) {
    authedGet(session, '/api/v1/sales?limit=5', tagsFor(persona, 'dashboard', 'sales_recent'));
    authedGet(session, '/api/v1/customers?limit=500', tagsFor(persona, 'dashboard', 'customers_list'));
  }
}

// --- reports (ReportsPage.tsx: production always, money AdminOnly) -----

export function reports(session, persona) {
  const from = daysAgo(6);
  const to = today();
  authedGet(
    session,
    `/api/v1/reports/production?from=${from}&to=${to}`,
    tagsFor(persona, 'reports', 'production'),
  );
  if (persona === 'Owner' || persona === 'Manager') {
    authedGet(
      session,
      `/api/v1/reports/sales?from=${from}&to=${to}`,
      tagsFor(persona, 'reports', 'sales_summary'),
    );
    authedGet(
      session,
      `/api/v1/reports/expenses?from=${from}&to=${to}`,
      tagsFor(persona, 'reports', 'expense_summary'),
    );
    authedGet(
      session,
      `/api/v1/reports/profit?from=${from}&to=${to}`,
      tagsFor(persona, 'reports', 'profit'),
    );
  }
}

// --- stock (StockPage.tsx: summary always, lots on grade expand) -------

export function stock(session, persona) {
  const res = authedGet(session, '/api/v1/stock', tagsFor(persona, 'stock', 'stock_summary'));
  const rows = safeJson(res) || [];
  // Half the time, simulate expanding a grade row (StockPage's toggleGrade).
  if (rows.length > 0 && Math.random() < 0.5) {
    const row = rows[Math.floor(Math.random() * rows.length)];
    authedGet(
      session,
      `/api/v1/stock/lots?gradeId=${row.eggGradeId}`,
      tagsFor(persona, 'stock', 'stock_lots'),
    );
  }
}

// --- history (HistoryPage.tsx initial load) -----------------------------

export function history(session, persona) {
  authedGet(session, '/api/v1/flocks?includeArchived=true', tagsFor(persona, 'history', 'flocks_list'));
  authedGet(
    session,
    '/api/v1/egg-grades?includeInactive=true',
    tagsFor(persona, 'history', 'egg_grades_list'),
  );
  authedGet(session, '/api/v1/daily-entries?limit=50', tagsFor(persona, 'history', 'daily_entries_list'));
}

// --- dailyEntryScreen (DailyEntryPage.tsx: read-heavy, rare tolerant save)

// Generous floor so the domain's "grades already on the row can't exceed the
// new total" re-validation (see file header) almost never trips on repeat
// writes to the same flock/date; 403/409/422 stay tolerated regardless.
const DAILY_ENTRY_WRITE_PROB = 0.12;
const DAILY_ENTRY_MIN_EGGS = 400;
const DAILY_ENTRY_EGG_SPREAD = 300;

export function dailyEntryScreen(session, persona, idemKeyFn) {
  const flocksRes = authedGet(session, '/api/v1/flocks', tagsFor(persona, 'dailyEntry', 'flocks_list'));
  authedGet(
    session,
    '/api/v1/egg-grades?includeInactive=true',
    tagsFor(persona, 'dailyEntry', 'egg_grades_list'),
  );

  const flocks = (safeJson(flocksRes) || []).filter((f) => f.status !== 'Archived');
  if (flocks.length === 0) return;
  // Deliberately uniform across ALL fetched flocks, assigned or not: a
  // flock-restricted worker (the sim seed's sim-worker-1) picking the
  // unassigned flock is exactly how the "restricted worker 403s on an
  // out-of-scope flock" case (#243 review, load-model rule 5) gets exercised
  // for free, without hardcoding which cast user is restricted.
  const flock = flocks[Math.floor(Math.random() * flocks.length)];
  const readDate = today();

  authedGet(
    session,
    `/api/v1/daily-entries?flockId=${flock.id}&from=${readDate}&to=${readDate}&limit=100`,
    tagsFor(persona, 'dailyEntry', 'daily_entry_prefill'),
  );

  if (Math.random() >= DAILY_ENTRY_WRITE_PROB) return;

  // UTC "yesterday": safe against the seeded farm timezone (America/Chicago,
  // behind UTC) rejecting a UTC "today" as future — see file header.
  const writeDate = daysAgo(1);
  const body = {
    farmId: flock.farmId,
    houseId: flock.houseId,
    flockId: flock.id,
    date: writeDate,
    totalEggs: DAILY_ENTRY_MIN_EGGS + Math.floor(Math.random() * DAILY_ENTRY_EGG_SPREAD),
    crackedEggs: 0,
    dirtyEggs: 0,
    discardedEggs: 0,
    mortalityCount: 0,
  };
  authedPost(
    session,
    '/api/v1/daily-entries',
    body,
    tagsFor(persona, 'dailyEntry', 'daily_entry_save'),
    idemKeyFn(),
    [201],
    [403, 409, 422],
  );
}

// --- sales (SalesPage.tsx: list + the draft-order write path) ----------

export function salesBundle(session, persona, idemKeyFn) {
  authedGet(session, '/api/v1/sales?limit=50', tagsFor(persona, 'sales', 'sales_list'));

  const custRes = authedGet(session, '/api/v1/customers', tagsFor(persona, 'sales', 'customers_list'));
  const prodRes = authedGet(
    session,
    '/api/v1/products?includeInactive=true',
    tagsFor(persona, 'sales', 'products_list'),
  );

  const customers = safeJson(custRes) || [];
  const products = (safeJson(prodRes) || []).filter((p) => p.active);
  if (customers.length === 0 || products.length === 0) return;

  const customer = customers[Math.floor(Math.random() * customers.length)];
  const product = products[Math.floor(Math.random() * products.length)];

  // A fresh Draft order every iteration: no natural key, never collides, and
  // being Draft never touches egg-lot stock — the safe hot-loop write
  // (#243 load-model rule 2).
  const createRes = authedPost(
    session,
    '/api/v1/sales',
    { customerId: customer.id, orderDate: today() },
    tagsFor(persona, 'sales', 'sales_create_order'),
    idemKeyFn(),
    [201],
  );
  if (createRes.status !== 201) return;
  const orderId = safeJson(createRes)?.id;
  if (!orderId) return;

  authedPost(
    session,
    `/api/v1/sales/${orderId}/items`,
    { productId: product.id, quantity: 1 + Math.floor(Math.random() * 4) },
    tagsFor(persona, 'sales', 'sales_add_item'),
    idemKeyFn(),
    [201],
  );
}

// --- customers (CustomersPage.tsx directory read) -----------------------

export function customersBundle(session, persona) {
  authedGet(session, '/api/v1/customers', tagsFor(persona, 'customers', 'customers_list'));
}

// Sales persona's "payments" bucket per the plan is a READ of balances, not
// new payments (confirming sales/recording payments stays in the deferred
// hazard passes — #243 load-model rule 2).
export function customerBalances(session, persona) {
  authedGet(session, '/api/v1/customers/balances', tagsFor(persona, 'customers', 'customer_balances'));
}

// --- audit / users / export (Owner-tier reads) --------------------------

export function auditBundle(session, persona) {
  authedGet(session, '/api/v1/audit?limit=50', tagsFor(persona, 'audit', 'audit_list'));
}

export function usersRead(session, persona) {
  authedGet(session, '/api/v1/users', tagsFor(persona, 'users', 'users_list'));
}

// Heavy (whole-account CSV zip via a streaming response) — only ever weighted
// lightly, and only for personas that actually pass AdminOnly.
export function exportBackup(session, persona) {
  authedGet(session, '/api/v1/export/all', tagsFor(persona, 'export', 'export_all'));
}

// --- deepLinkProbe (ReadOnly/Worker: no SPA nav entry, API still 403s) -

const DEEP_LINK_TARGETS = [
  ['/api/v1/audit', 'audit_probe'],
  ['/api/v1/users', 'users_probe'],
  ['/api/v1/export/all', 'export_probe'],
];

export function deepLinkProbe(session, persona) {
  const [path, endpoint] = DEEP_LINK_TARGETS[Math.floor(Math.random() * DEEP_LINK_TARGETS.length)];
  authedGetExpect403(session, path, tagsFor(persona, 'deepLinkProbe', endpoint));
}

// --- staticAssets (index.html + a couple of discovered hashed assets) --

// Cached per VU (module state persists across a VU's iterations, but each VU
// gets its own JS runtime — see auth.js's own per-VU notes) so we only parse
// index.html once and don't refetch the whole bundle on every light-weight
// pass.
let discoveredAssets = null;

export function staticAssets(persona) {
  const indexTags = tagsFor(persona, 'staticAssets', 'index_html');
  const indexRes = http.get(`${BASE_URL}/`, { tags: indexTags });
  check(indexRes, {
    'index_html: status 200': (r) => r.status === 200,
  });
  noteIfUnexpected(indexRes, indexTags, [200]);

  if (discoveredAssets === null) {
    discoveredAssets = [];
    const re = /(?:src|href)="(\/(?:assets\/[^"]+|manifest\.webmanifest|theme-init\.js|favicon\.svg))"/g;
    let m = re.exec(indexRes.body);
    while (m !== null) {
      discoveredAssets.push(m[1]);
      m = re.exec(indexRes.body);
    }
    if (discoveredAssets.length === 0) discoveredAssets.push('/manifest.webmanifest');
  }

  const asset = discoveredAssets[Math.floor(Math.random() * discoveredAssets.length)];
  const assetTags = tagsFor(persona, 'staticAssets', 'static_asset');
  const assetRes = http.get(`${BASE_URL}${asset}`, { tags: assetTags });
  check(assetRes, {
    'static_asset: status 200': (r) => r.status === 200,
  });
  noteIfUnexpected(assetRes, assetTags, [200]);
}
