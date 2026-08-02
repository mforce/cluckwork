// tools/simulation/k6/dates.js — date helpers for the #243 harness.
//
// DELIBERATELY k6-FREE: this module imports nothing from `k6/*`, so it can be
// loaded by plain Node and unit-tested (`dates.test.mjs`).
//
// WHERE THAT TEST ACTUALLY RUNS — nowhere automated. This harness is
// deliberately not in CI (dev tooling), so the test runs only when
// `verify-harness.sh` invokes it, which `reset.sh` does before it wipes the
// volume, and when a human runs either by hand. Do NOT treat these helpers as
// covered by a pipeline: nothing will tell you when they break. (An earlier
// revision of this comment claimed "run by CI", which was true for exactly one
// commit before the CI job was dropped — PR #371 review.)
//
// Every function takes an injectable `now` — a date helper whose only clock is
// the wall clock can only be tested at whatever time it happens to run, which
// is precisely how the bug below survived.
//
// WHY THIS FILE EXISTS AT ALL — the bug it is the fix for:
//
// The seeded farm timezone (Simulation__TimeZoneId, America/Chicago) is BEHIND
// UTC. For the hours between 00:00 UTC and midnight farm-local, a UTC "today"
// is a date the FARM has not reached yet, and the four report endpoints reject
// it with 400 Report.FutureRange. `reports()` sent exactly that, so the harness
// failed 12.4% of its capacity requests and crossed its thresholds — but only
// during that window. Both runs recorded in findings/ were launched during UTC
// daytime, when the two dates agree, so both passed and nothing ever reported
// the defect. A harness that works in the morning and lies in the evening is
// worse than one that is plainly broken.
//
// WHY NOT MIRROR THE SPA EXACTLY: ReportsPage.tsx asks for
// [farmToday-6, farmToday], resolving farm-local today through
// Intl.DateTimeFormat with the farm's zone (web/src/lib/dates.ts) — formatted,
// not offset arithmetic, so DST is the platform's problem. That is unavailable
// here: k6 v2.0.0's runtime has NO Intl at all (probed directly against the
// pinned build — `typeof Intl === 'undefined'`), so there is no way to ask it
// what date it is in America/Chicago.
//
// The alternative, a hardcoded UTC offset for the seeded zone, is worse than
// the bug: it is wrong twice a year at the DST boundaries, and it silently
// rots if Simulation__TimeZoneId is ever changed.
//
// So the range ENDS at UTC "yesterday". That is not a timezone calculation at
// all — it is the observation that no real zone is more than 24h from UTC, so
// UTC-yesterday is in the past for EVERY farm, at every instant, under any DST
// rule. The daily-entry WRITE already used this exact reasoning (see
// bundles.js's header); reports() simply never got it.
//
// The cost is fidelity: the window is [utcToday-7, utcToday-1] rather than the
// screen's [farmToday-6, farmToday]. Same 7-day width, so scan volume and
// therefore load characteristics are unchanged; it is shifted by at most one
// day. That is the deliberate trade — a correct 7-day scan every hour of the
// day beats an exact one that 400s for five of them.

export function isoDate(d) {
  return d.toISOString().slice(0, 10);
}

// UTC "today". Still correct for the screens that genuinely mean "today" as a
// READ filter (the dashboard's today-entries fan-out, the daily-entry prefill):
// those endpoints do not enforce a not-future rule, and using UTC there keeps
// the harness mirroring what the SPA requests.
export function today(now = new Date()) {
  return isoDate(now);
}

export function daysAgo(n, now = new Date()) {
  const d = new Date(now.getTime());
  d.setUTCDate(d.getUTCDate() - n);
  return isoDate(d);
}

// The report range. Both ends come from here so they can never drift apart,
// and so the invariant test has a single thing to assert against.
export function reportRangeStart(now = new Date()) {
  return daysAgo(REPORT_RANGE_DAYS, now);
}

export function reportRangeEnd(now = new Date()) {
  return daysAgo(1, now);
}

// [start, end] inclusive spans this many days; kept equal to the screen's own
// 7-day default so the scan volume the baseline measures is representative.
export const REPORT_RANGE_DAYS = 7;
