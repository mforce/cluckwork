import type { Account, RecordHistory } from "../api/cluckwork";
import type { FarmState } from "../farm/FarmContext";

// A whole /account payload with the §4.5 fields filled in, so a test that only
// cares about the currency (or the timezone, or the logo) states just that one
// and does not carry ten irrelevant literals (#123).
export function account(overrides: Partial<Account> = {}): Account {
  return {
    id: "a1",
    name: "Test Farm",
    currencyCode: "USD",
    currencyMinorUnit: 2,
    currencySymbol: "$",
    // UTC by default: a test that does not care about the zone gets the same
    // day the runner is on, whatever TZ the CI box runs in.
    timeZoneId: "UTC",
    locale: "en-US",
    unitSystem: "Metric",
    firstDayOfWeek: null,
    dateFormatOverride: null,
    timeFormatOverride: null,
    version: 1,
    logoContentHash: null,
    bannerContentHash: null,
    brand: "aubergine",
    defaultStepperUnit: "Individual",
    showFarmWideSaleAllocationNotice: false,
    ...overrides,
  };
}

// A farm-context value for a screen test that only cares WHICH farm it is
// looking at. `today: null` on purpose — useFarmToday() then computes it live
// from the farm's timezone, which is what the real provider produces too; a
// test that wants a fixed day sets one.
export function farmState(overrides: Partial<FarmState> = {}): FarmState {
  return {
    farm: null,
    loadFailed: false,
    today: null,
    refresh: async () => true,
    ...overrides,
  };
}

// #494 — what the API sends for a record created before record history
// shipped: all four fields null together, because there is no backfill and its
// audit trail has no creation event to report. Spread into a fixture that does
// not care about history, so the fixture stays about what it IS testing.
export const NO_RECORD_HISTORY: RecordHistory = {
  createdByEmail: null,
  createdAtUtc: null,
  lastChangedByEmail: null,
  lastChangedAtUtc: null,
};

// The opposite fixture: a record created by one person and later changed by
// another, so a page test can assert BOTH halves land in that row's cell — the
// cheap way to catch a page handing the history column the wrong row's object.
export const RECORD_HISTORY: RecordHistory = {
  createdByEmail: "ana@farm.test",
  createdAtUtc: "2026-05-01T08:00:00+00:00",
  lastChangedByEmail: "bo@farm.test",
  lastChangedAtUtc: "2026-05-03T14:30:00+00:00",
};
