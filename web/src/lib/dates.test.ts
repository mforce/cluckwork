import { describe, it, expect, afterEach, vi } from "vitest";
import { todayIso, ageWeeks } from "./dates";

describe("todayIso", () => {
  afterEach(() => vi.useRealTimers());

  it("formats the browser-local date as zero-padded YYYY-MM-DD", () => {
    // new Date(y, monthIndex, day, ...) is LOCAL time, so this is deterministic
    // regardless of the runner's timezone — todayIso reads the same local fields.
    vi.useFakeTimers();
    vi.setSystemTime(new Date(2026, 0, 5, 12, 0, 0)); // local Jan 5, 2026
    expect(todayIso()).toBe("2026-01-05");
  });

  it("zero-pads single-digit months and days", () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date(2026, 8, 9, 8, 0, 0)); // local Sep 9, 2026
    expect(todayIso()).toBe("2026-09-09");
  });

  it("uses the local calendar day, not UTC — a late-evening instant stays on today", () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date(2026, 10, 30, 23, 59, 0)); // local Nov 30, 2026 23:59
    // toISOString() would report Dec 1 for a runner WEST of UTC (negative offset,
    // e.g. the Americas); todayIso reads local fields, so it stays on Nov 30. On a
    // UTC/east runner both agree — this pins the local-field behavior, and only a
    // west-of-UTC runner would additionally catch a regression to toISOString().
    expect(todayIso()).toBe("2026-11-30");
  });
});

describe("ageWeeks", () => {
  // Parse placement the same way the function does, then advance by fixed
  // milliseconds so the result is exact and timezone/DST-independent.
  const placedMs = new Date("2026-01-01T00:00:00").getTime();
  const DAY = 86_400_000;

  it("is 0 on the placement day", () => {
    expect(ageWeeks("2026-01-01", placedMs)).toBe(0);
  });

  it("floors partial weeks (6 days → 0, 13 days → 1)", () => {
    expect(ageWeeks("2026-01-01", placedMs + 6 * DAY)).toBe(0);
    expect(ageWeeks("2026-01-01", placedMs + 13 * DAY)).toBe(1);
  });

  it("rolls a full week exactly on day 7 and 14", () => {
    expect(ageWeeks("2026-01-01", placedMs + 7 * DAY)).toBe(1);
    expect(ageWeeks("2026-01-01", placedMs + 14 * DAY)).toBe(2);
  });

  it("clamps a not-yet-placed flock (future date) to 0, never negative", () => {
    expect(ageWeeks("2026-01-01", placedMs - 5 * DAY)).toBe(0);
  });

  it("reads the wall clock when nowMs is omitted (the FlocksPage call form)", () => {
    // Pins the default-parameter overload that FlocksPage relies on — a refactor
    // that dropped `= Date.now()` would otherwise slip through unnoticed.
    vi.useFakeTimers();
    vi.setSystemTime(placedMs + 8 * DAY);
    try {
      expect(ageWeeks("2026-01-01")).toBe(1);
    } finally {
      vi.useRealTimers();
    }
  });
});
