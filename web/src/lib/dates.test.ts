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

  it("uses the local calendar day, not UTC — a late-evening instant stays on today", () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date(2026, 10, 30, 23, 59, 0)); // local Nov 30, 2026 23:59
    // toISOString() could report Dec 1 for east-of-UTC runners; todayIso must not.
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
});
