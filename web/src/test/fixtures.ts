import type { Account } from "../api/cluckwork";
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
    brand: "aubergine",
    defaultStepperUnit: "Individual",
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
