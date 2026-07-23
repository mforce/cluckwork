import { describe, it, expect, afterEach, vi } from "vitest";
import { todayIso, ageWeeks, daysBefore, isKnownTimeZone } from "./dates";

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

describe("todayIso in a farm timezone (#123)", () => {
  afterEach(() => vi.useRealTimers());

  // 2026-07-15T23:30Z: still the 15th in UTC, already the 16th in Tokyo
  // (UTC+9), and still the 15th in Los Angeles (UTC-7). One instant, three
  // farm days — which is the entire reason the timezone has to come from the
  // farm and not the device.
  const ACROSS_MIDNIGHT = new Date("2026-07-15T23:30:00Z");

  it("reads the farm's calendar day, not the runner's", () => {
    vi.useFakeTimers();
    vi.setSystemTime(ACROSS_MIDNIGHT);
    expect(todayIso("Asia/Tokyo")).toBe("2026-07-16");
    expect(todayIso("America/Los_Angeles")).toBe("2026-07-15");
    expect(todayIso("UTC")).toBe("2026-07-15");
  });

  it("reads the far side of the date line", () => {
    // UTC+14, the earliest zone there is: at this instant it is already the
    // 16th there while UTC is on the 15th. Paired with the UTC assertion above,
    // one clock produces two different farm days — which no implementation
    // that ignores its timezone argument can do, on any runner.
    vi.useFakeTimers();
    vi.setSystemTime(ACROSS_MIDNIGHT);
    const farm = todayIso("Pacific/Kiritimati");
    expect(farm).toBe("2026-07-16");
    expect(todayIso("UTC")).toBe("2026-07-15");
    expect(daysBefore(farm, 1)).toBe("2026-07-15");
  });

  it("zero-pads the farm's month and day", () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-01-05T12:00:00Z"));
    expect(todayIso("UTC")).toBe("2026-01-05");
  });

  it("falls back to browser-local for a zone this browser does not know", () => {
    // The server's IANA catalogue can be newer than the browser's. A zone it
    // cannot resolve must degrade to the old behaviour, not throw and take
    // every date field on the screen down with it.
    vi.useFakeTimers();
    vi.setSystemTime(new Date(2026, 2, 4, 12, 0, 0)); // local Mar 4, 2026
    // The literal is what carries this: the second assertion would hold by
    // construction if the timezone argument were ignored entirely.
    expect(todayIso("Mars/Olympus_Mons")).toBe("2026-03-04");
    expect(todayIso("Mars/Olympus_Mons")).toBe(todayIso());
  });

  it("reports whether this browser can format in a zone at all", () => {
    // What the settings screen checks with, so an admin is told at the field
    // rather than discovering it as every date silently following the device.
    expect(isKnownTimeZone("Asia/Tokyo")).toBe(true);
    expect(isKnownTimeZone("UTC")).toBe(true);
    expect(isKnownTimeZone("Mars/Olympus_Mons")).toBe(false);
    expect(isKnownTimeZone("")).toBe(false);
  });
});

describe("daysBefore", () => {
  it("subtracts whole calendar days", () => {
    expect(daysBefore("2026-07-15", 6)).toBe("2026-07-09");
    expect(daysBefore("2026-07-15", 0)).toBe("2026-07-15");
  });

  it("crosses month and year boundaries", () => {
    expect(daysBefore("2026-03-02", 3)).toBe("2026-02-27");
    expect(daysBefore("2026-01-02", 5)).toBe("2025-12-28");
  });

  it("knows February's length in a leap year and a common one", () => {
    expect(daysBefore("2028-03-01", 1)).toBe("2028-02-29");
    expect(daysBefore("2026-03-01", 1)).toBe("2026-02-28");
  });

  it("crosses a DST boundary as plain calendar days", () => {
    // US DST springs forward on 2026-03-08 and falls back on 2026-11-01.
    //
    // Stated honestly, because the obvious framing does not hold: these cases
    // would ALSO pass a naive `getTime() - days * 86400000` on a UTC runner,
    // and nothing here pins TZ (agent review of #123). They pin the calendar
    // answer across the two dates a timezone-sensitive implementation is most
    // likely to get wrong, not the absence of a specific bug — the guarantee
    // that this function never touches the runner's zone is structural, and is
    // asserted where it can be: the farm-timezone cases above, which do
    // discriminate on every runner.
    expect(daysBefore("2026-03-09", 1)).toBe("2026-03-08");
    expect(daysBefore("2026-03-09", 2)).toBe("2026-03-07");
    expect(daysBefore("2026-11-02", 1)).toBe("2026-11-01");
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
