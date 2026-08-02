// node --test tools/simulation/k6/dates.test.mjs
//
// Pins the one property the #243 harness needs from its date helpers and that
// no amount of running k6 at a convenient hour will check for you: the report
// range must END on a date the FARM has already reached, for every real
// timezone and at every hour of the day.
//
// The bug this exists to prevent (PR: sim harness fixes) shipped because the
// only test was "run the harness and see". That passes for ~19 hours a day and
// fails for the rest, so both recorded runs in findings/ were green while the
// defect was fully present. The fix for that is not a longer run — it is a
// test that supplies the clock instead of reading it.

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  isoDate, today, daysAgo, reportRangeStart, reportRangeEnd, REPORT_RANGE_DAYS,
} from "./dates.js";

// Every whole-hour UTC offset a real IANA zone uses, from Baker Island (-12)
// to Kiritimati (+14), including the half/quarter-hour ones that break naive
// arithmetic (India +5:30, Nepal +5:45, Chatham +12:45).
const OFFSETS_MINUTES = [
  -720, -660, -600, -570, -540, -480, -420, -360, -300, -270, -240, -210, -180,
  -120, -60, 0, 60, 120, 180, 210, 240, 270, 300, 330, 345, 360, 390, 420, 480,
  525, 540, 570, 600, 630, 660, 720, 765, 780, 840,
];

// The farm's local calendar date at `now`, for a farm at `offsetMinutes`.
// Deliberately computed independently of dates.js — a test that reuses the
// implementation's own arithmetic proves only that it equals itself.
function farmLocalDate(now, offsetMinutes) {
  return new Date(now.getTime() + offsetMinutes * 60_000).toISOString().slice(0, 10);
}

// Every hour of a day, across a month boundary, a year boundary, and a leap
// day — the three places date arithmetic classically breaks.
function instants() {
  const days = ["2026-08-01", "2026-07-31", "2026-12-31", "2026-01-01", "2028-02-29", "2028-03-01"];
  const out = [];
  for (const day of days)
    for (let h = 0; h < 24; h++)
      out.push(new Date(`${day}T${String(h).padStart(2, "0")}:30:00Z`));
  return out;
}

test("the report range never ends after the farm's own today, at any offset or hour", () => {
  for (const now of instants()) {
    const end = reportRangeEnd(now);
    for (const offset of OFFSETS_MINUTES) {
      const farmToday = farmLocalDate(now, offset);
      assert.ok(
        end <= farmToday,
        `range end ${end} is in the future for a farm at offset ${offset} `
        + `(its today is ${farmToday}) at ${now.toISOString()}`,
      );
    }
  }
});

// The above passes trivially for an absurdly old end date, so pin the other
// side too: the range must stay CURRENT, never more than one day behind the
// most-behind farm. Together these two force the end to the single correct day.
test("the report range end stays as recent as it can be without going future", () => {
  for (const now of instants()) {
    const end = reportRangeEnd(now);
    // The earliest "today" any farm on earth can have is UTC-1 day.
    const earliestFarmToday = daysAgo(1, now);
    assert.equal(
      end, earliestFarmToday,
      `range end drifted from the most recent always-safe date at ${now.toISOString()}`,
    );
  }
});

test("the range spans REPORT_RANGE_DAYS inclusive, matching the screen's window", () => {
  for (const now of instants()) {
    const start = new Date(`${reportRangeStart(now)}T00:00:00Z`);
    const end = new Date(`${reportRangeEnd(now)}T00:00:00Z`);
    const spanDays = (end - start) / 86_400_000 + 1;
    assert.equal(spanDays, REPORT_RANGE_DAYS, `span at ${now.toISOString()}`);
  }
});

// THE REGRESSION ITSELF. The old code used `to = today()` (UTC). This asserts
// that specific value is unsafe, so a future edit that "simplifies" the range
// end back to UTC-today fails here with a message naming the reason, rather
// than passing CI and breaking the harness for five hours a day.
test("UTC today — the value this replaced — really is unsafe for a farm behind UTC", () => {
  // 03:47Z is the instant the live failure was observed; America/Chicago
  // (-5 CDT) is the seeded zone, and is still on the previous date.
  const now = new Date("2026-08-02T03:47:00Z");
  const chicagoToday = farmLocalDate(now, -300);

  assert.equal(today(now), "2026-08-02");
  assert.equal(chicagoToday, "2026-08-01");
  assert.ok(
    today(now) > chicagoToday,
    "premise broken: UTC today must be ahead of the farm here, or this test proves nothing",
  );
  // ...and the value actually shipped is not.
  assert.ok(reportRangeEnd(now) <= chicagoToday);
});

test("isoDate/daysAgo cross month, year and leap boundaries in UTC", () => {
  assert.equal(isoDate(new Date("2026-08-01T00:00:00Z")), "2026-08-01");
  assert.equal(daysAgo(1, new Date("2026-08-01T00:30:00Z")), "2026-07-31");
  assert.equal(daysAgo(1, new Date("2026-01-01T00:30:00Z")), "2025-12-31");
  assert.equal(daysAgo(1, new Date("2028-03-01T00:30:00Z")), "2028-02-29");
  assert.equal(daysAgo(7, new Date("2026-08-01T23:59:00Z")), "2026-07-25");
});
