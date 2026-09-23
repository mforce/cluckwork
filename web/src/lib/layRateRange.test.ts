// web/src/lib/layRateRange.test.ts
import { describe, it, expect } from "vitest";
import {
  DEFAULT_RANGE, MAX_RANGE_DAYS, RANGE_PRESETS, customRangeError, formatStoredRange,
  parseStoredRange, trendWindow,
} from "./layRateRange";

// A fixed farm-local today, so every expectation below is a literal date.
const TODAY = "2026-07-21";
// What the card will plot up to: yesterday, never today.
const LATEST = "2026-07-20";

describe("trendWindow (#914)", () => {
  it("offers 7, 14 and 30 days and starts on 14", () => {
    expect(RANGE_PRESETS).toEqual([7, 14, 30]);
    expect(DEFAULT_RANGE).toEqual({ kind: "preset", days: 14 });
  });

  it("ends a preset on yesterday and puts the comparison window directly before it", () => {
    expect(trendWindow({ kind: "preset", days: 14 }, TODAY)).toEqual({
      from: "2026-07-07", to: "2026-07-20", days: 14,
      previousFrom: "2026-06-23", previousTo: "2026-07-06",
    });
  });

  it("gives the seven-day preset seven days and the thirty-day preset thirty", () => {
    expect(trendWindow({ kind: "preset", days: 7 }, TODAY)).toEqual({
      from: "2026-07-14", to: "2026-07-20", days: 7,
      previousFrom: "2026-07-07", previousTo: "2026-07-13",
    });
    expect(trendWindow({ kind: "preset", days: 30 }, TODAY)).toEqual({
      from: "2026-06-21", to: "2026-07-20", days: 30,
      previousFrom: "2026-05-22", previousTo: "2026-06-20",
    });
  });

  // The two windows are adjacent by construction, which is what lets the card
  // fetch them as ONE report and split it.
  it("measures a custom range inclusively and mirrors its length backwards", () => {
    expect(trendWindow({ kind: "custom", from: "2026-01-01", to: "2026-03-01" }, TODAY)).toEqual({
      from: "2026-01-01", to: "2026-03-01", days: 60,
      previousFrom: "2025-11-02", previousTo: "2025-12-31",
    });
  });

  it("counts a leap day like any other", () => {
    expect(trendWindow({ kind: "custom", from: "2024-02-28", to: "2024-03-01" }, TODAY))
      .toMatchObject({ days: 3, previousFrom: "2024-02-25", previousTo: "2024-02-27" });
  });
});

describe("customRangeError (#914)", () => {
  it("accepts a range that ends on the newest day the card plots", () => {
    expect(customRangeError("2026-07-14", LATEST, LATEST)).toBeNull();
  });

  it("rejects an unparseable or empty date", () => {
    expect(customRangeError("", "2026-07-14", LATEST)).toBe("incomplete");
    expect(customRangeError("2026-13-01", "2026-07-14", LATEST)).toBe("incomplete");
  });

  it("rejects a start after the end", () => {
    expect(customRangeError("2026-03-01", "2026-01-01", LATEST)).toBe("order");
  });

  it("rejects a range reaching past yesterday", () => {
    expect(customRangeError("2026-07-01", "2026-07-21", LATEST)).toBe("future");
  });

  // Rejected in the form, never silently truncated.
  it("takes exactly ninety days and refuses the ninety-first", () => {
    expect(MAX_RANGE_DAYS).toBe(90);
    expect(customRangeError("2026-01-01", "2026-03-31", LATEST)).toBeNull();
    expect(customRangeError("2026-01-01", "2026-04-01", LATEST)).toBe("tooLong");
  });
});

// A window whose comparison period
// would start before the calendar begins has no comparison to make, and the
// arithmetic that produced it cannot be trusted either.
describe("customRangeError at the edge of the calendar", () => {
  // Under the two-digit-year mapping this span measured NEGATIVE, which walked
  // straight past the length cap. It is an ordinary two-day range.
  it("measures a span across year 100 instead of letting it past the cap", () => {
    expect(customRangeError("0099-12-31", "0100-01-01", LATEST)).toBeNull();
    expect(trendWindow({ kind: "custom", from: "0099-12-31", to: "0100-01-01" }, TODAY))
      .toMatchObject({ days: 2, previousFrom: "0099-12-29", previousTo: "0099-12-30" });
  });

  it("refuses a range with no equal window before it", () => {
    expect(customRangeError("0001-01-05", "0001-01-10", LATEST)).toBe("beforeCalendar");
    expect(customRangeError("0001-01-01", "0001-03-31", LATEST)).toBe("beforeCalendar");
  });

  it("accepts a range whose comparison window starts exactly on 0001-01-01", () => {
    expect(customRangeError("0001-01-11", "0001-01-20", LATEST)).toBeNull();
    expect(trendWindow({ kind: "custom", from: "0001-01-11", to: "0001-01-20" }, TODAY))
      .toMatchObject({ days: 10, previousFrom: "0001-01-01", previousTo: "0001-01-10" });
  });
});

describe("remembered range (#914, #535 per-farm storage)", () => {
  it("round-trips a preset and a custom range", () => {
    expect(formatStoredRange({ kind: "preset", days: 30 })).toBe("30");
    expect(formatStoredRange({ kind: "custom", from: "2026-01-01", to: "2026-03-01" }))
      .toBe("2026-01-01..2026-03-01");
    expect(parseStoredRange("30", LATEST)).toEqual({ kind: "preset", days: 30 });
    expect(parseStoredRange("2026-01-01..2026-03-01", LATEST))
      .toEqual({ kind: "custom", from: "2026-01-01", to: "2026-03-01" });
  });

  it("reads nothing remembered as nothing remembered", () => {
    expect(parseStoredRange(null, LATEST)).toBeNull();
  });

  // Storage outlives every rule this build knows, so what comes back out is
  // held to the same checks the form applies.
  it("drops a stored value the form itself would reject", () => {
    expect(parseStoredRange("15", LATEST)).toBeNull();
    expect(parseStoredRange("nonsense", LATEST)).toBeNull();
    expect(parseStoredRange("2026-01-01..2026-04-01", LATEST)).toBeNull();
    expect(parseStoredRange("2026-01-01..2026-07-21", LATEST)).toBeNull();
    expect(parseStoredRange("2026-01-01..2026-03-01..2026-04-01", LATEST)).toBeNull();
  });
});
