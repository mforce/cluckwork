import { afterEach, describe, expect, it } from "vitest";
import { relativeTime } from "./relativeTime";
import i18n, { RESOURCES } from "../i18n";

afterEach(async () => {
  await i18n.changeLanguage("en");
});

// A fixed "now", so these never race the real clock. Timezone is passed
// explicitly on every call — the runner's own TZ must never leak in.
const NOW = Date.parse("2026-03-15T12:00:00Z");

describe("relativeTime", () => {
  it("renders the same farm-local day as today", () => {
    expect(relativeTime("2026-03-15T01:00:00Z", "UTC", NOW)).toBe("Today");
  });

  it("renders one farm-local day back as yesterday", () => {
    expect(relativeTime("2026-03-14T01:00:00Z", "UTC", NOW)).toBe("Yesterday");
  });

  it("counts a few days back", () => {
    expect(relativeTime("2026-03-12T01:00:00Z", "UTC", NOW)).toBe("3 days ago");
  });

  it("switches to weeks about a week back", () => {
    expect(relativeTime("2026-03-08T01:00:00Z", "UTC", NOW)).toBe("1 week ago");
  });

  it("pluralizes weeks", () => {
    expect(relativeTime("2026-03-01T01:00:00Z", "UTC", NOW)).toBe("2 weeks ago");
  });

  it("switches to months over a month back", () => {
    // Jan 10 -> Mar 15 is 64 days: 21 remaining in Jan, 28 in Feb (2026 is not
    // a leap year), 15 in Mar.
    expect(relativeTime("2026-01-10T01:00:00Z", "UTC", NOW)).toBe("2 months ago");
  });

  it("clamps a future instant to today instead of counting forward", () => {
    // These are audit trail instants the server wrote in the past, never
    // scheduled ahead — a future instant here can only be clock skew between
    // this device and the server, so it is clamped rather than rendered as
    // "in N days", which would misleadingly imply a real future event.
    expect(relativeTime("2026-03-16T01:00:00Z", "UTC", NOW)).toBe("Today");
  });

  it("computes the day boundary on the FARM's calendar, not UTC", () => {
    // Pacific/Kiritimati is UTC+14. This instant is March 14 in UTC but
    // already March 15 local — the same farm-local day as `nowKiribati`
    // below, also March 15 local despite being March 15 in UTC too. A UTC-day
    // diff would call this "Yesterday" (Mar 14 UTC vs Mar 15 UTC); the
    // farm-correct answer is "Today", because on Kiritimati's calendar both
    // instants fall on March 15.
    const eventUtcMar14ButFarmMar15 = "2026-03-14T23:00:00Z";
    const nowKiribati = Date.parse("2026-03-15T01:00:00Z");
    expect(relativeTime(eventUtcMar14ButFarmMar15, "Pacific/Kiritimati", nowKiribati)).toBe(
      "Today",
    );
  });

  it("falls back to browser-local when no farm timezone is known, without throwing", () => {
    // Same contract as lib/dates.ts's todayIso: an undefined zone means the
    // farm hasn't loaded yet (or its zone read failed), not an error. `iso`
    // equals `nowMs` exactly so the result is "Today" under ANY runner
    // timezone — the two instants land on the same local calendar day
    // whichever zone `undefined` resolves to.
    expect(relativeTime("2026-03-15T12:00:00Z", undefined, NOW)).toBe("Today");
  });

  it("translates the phrase in every installed language pack", async () => {
    for (const language of Object.keys(RESOURCES)) {
      await i18n.changeLanguage(language);
      const phrase = relativeTime("2026-03-14T01:00:00Z", "UTC", NOW);
      expect(phrase.length).toBeGreaterThan(0);
      expect(phrase).not.toBe("relativeTime.yesterday");
    }
  });
});
