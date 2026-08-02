// tools/simulation/ui/src/farm.ts — farm-local dates, resolved the way the SPA
// resolves them.
//
// ================== THE BUG THIS FILE EXISTS TO NOT REPEAT ==================
//
// The seeded farm timezone is BEHIND UTC (America/Chicago). Between 00:00 UTC and
// midnight farm-local, a UTC "today" is a date the FARM HAS NOT REACHED, and the
// report endpoints reject it with `400 Report.FutureRange`. The k6 harness sent
// exactly that and failed 12.4% of its capacity requests — but only during that
// window, so both recorded baseline runs, launched during UTC daytime, passed and
// nothing reported it. tools/simulation/k6/dates.js carries the full account.
//
// A date picker in this suite has the same exposure and one more: every date
// `<input>` in the SPA takes `max={today}` from `useFarmToday()`, so a UTC-derived
// date can be REJECTED BY THE BROWSER before a request is ever made — a spec that
// would then fail with "could not fill the field", pointing nowhere near the cause.
//
// ================== WHY THIS MIRRORS THE SPA AND k6 DOES NOT ==================
//
// k6 v2.0.0's runtime has NO `Intl` at all (probed directly against the pinned
// build), so dates.js cannot ask what date it is in America/Chicago and settles
// for UTC-yesterday — correct for every zone, one day off the screen's real range.
//
// Node has `Intl`. So this suite does what the SPA does, exactly:
// `Intl.DateTimeFormat` with the farm's zone, formatted rather than offset
// arithmetic, so DST is the platform's problem (web/src/lib/dates.ts `todayIso`).
// Do NOT "simplify" this into a UTC offset — that is wrong twice a year and rots
// silently the moment Simulation__TimeZoneId changes.
//
// ================== AND WHY THE ZONE IS FETCHED, NOT HARDCODED ==================
//
// `America/Chicago` appears in bootstrap.sh, the README and three k6 files. It is
// still not hardcoded here: the zone is read from `GET /account`, the same record
// the SPA reads it from. If somebody retunes Simulation__TimeZoneId, this suite
// follows automatically instead of asserting against a stale constant — and the
// value it uses is by construction the one the app is using.

import { apiGet, signInForToken } from "./api";
import { owner } from "./cast";

export interface FarmContext {
  timeZoneId: string;
  /** ISO 4217 code — what the money field LABELS interpolate ("Unit price ({{code}})"). */
  currencyCode: string;
  /** The display symbol — what rendered amounts carry. Not interchangeable with the code. */
  currencySymbol: string;
  /** Decimal places, so a spec converts minor units without assuming 2. */
  currencyMinorUnit: number;
  locale: string;
  name: string;
}

interface AccountResponse {
  name: string;
  timeZoneId: string;
  currencyCode: string;
  currencySymbol: string;
  currencyMinorUnit: number;
  locale: string;
}

let cached: FarmContext | null = null;

/** The farm's own settings, read once per process from the API the SPA reads. */
export async function farmContext(): Promise<FarmContext> {
  if (cached) return cached;
  const token = await signInForToken(owner());
  const account = await apiGet<AccountResponse>(token, "/account");
  if (!account.timeZoneId) {
    throw new Error("GET /account returned no timeZoneId — the farm clock cannot be resolved.");
  }
  cached = {
    timeZoneId: account.timeZoneId,
    currencyCode: account.currencyCode,
    currencySymbol: account.currencySymbol,
    currencyMinorUnit: account.currencyMinorUnit,
    locale: account.locale,
    name: account.name,
  };
  return cached;
}

/**
 * Farm-local "today" as `YYYY-MM-DD`.
 *
 * Character-for-character the algorithm in `web/src/lib/dates.ts todayIso()`:
 * `formatToParts` in the farm's zone, reassembled. Deliberately NOT the SPA's
 * `catch` fallback to browser-local — in the app that fallback keeps a farm with
 * a mistyped zone usable, but here it would silently substitute the RUNNER's
 * timezone for the farm's and hand every date assertion a plausible wrong answer.
 * A bad zone should fail the spec loudly.
 */
export function farmToday(timeZone: string, now: Date = new Date()): string {
  let parts: Intl.DateTimeFormatPart[];
  try {
    parts = new Intl.DateTimeFormat("en-US", {
      timeZone,
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
    }).formatToParts(now);
  } catch (cause) {
    throw new Error(
      `The farm's timezone "${timeZone}" is not resolvable in this Node runtime. `
        + `Deliberately not falling back to the runner's local zone, which would silently `
        + `substitute a plausible wrong date into every date field.`,
      { cause },
    );
  }
  const at = (type: Intl.DateTimeFormatPartTypes) => parts.find((p) => p.type === type)?.value;
  const [y, m, d] = [at("year"), at("month"), at("day")];
  if (!y || !m || !d) throw new Error(`Could not format a farm-local date in "${timeZone}".`);
  return `${y}-${m}-${d}`;
}

/**
 * N days before a farm-local ISO date.
 *
 * `Date.UTC` arithmetic on the already-resolved calendar date, never a local
 * `Date` — same reason `web/src/lib/dates.ts daysBefore()` does it that way: a
 * local `Date` would re-introduce the runner's timezone into a farm-local
 * calculation and drift by a day near midnight.
 */
export function daysBefore(isoDate: string, days: number): string {
  const [y, m, d] = isoDate.split("-").map(Number);
  if (!y || !m || !d) throw new Error(`Not an ISO date: "${isoDate}"`);
  return new Date(Date.UTC(y, m - 1, d - days)).toISOString().slice(0, 10);
}
