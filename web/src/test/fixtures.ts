import type { Account } from "../api/cluckwork";

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
    ...overrides,
  };
}
