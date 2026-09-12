// web/src/lib/dashboard.test.ts
import { describe, it, expect } from "vitest";
import {
  GRADE_COLOURS, TILE_CAP, captureTiles, dayStrip, henDayTrend, stockBar, todaysEggs, visibleTiles,
} from "./dashboard";
import type { DailyEntry, Flock, ProductionDay, ProductionReport, StockRow } from "../api/cluckwork";
import { NO_RECORD_HISTORY } from "../test/fixtures";

const flock = (id: string, status: string): Flock => ({
  ...NO_RECORD_HISTORY,
  id, farmId: "f", houseId: "h", name: `Flock ${id}`, breed: "ISA",
  placementDate: "2026-01-01", initialCount: 100, currentBirds: 98, status,
});
const entry = (flockId: string, status: string, totalEggs: number, id = `de-${flockId}-${status}`): DailyEntry => ({
  ...NO_RECORD_HISTORY,
  id, farmId: "f", houseId: "h", flockId, date: "2026-07-21", status,
  totalEggs, crackedEggs: 0, dirtyEggs: 0, discardedEggs: 0, mortalityCount: 0,
  crackedGradeId: null, dirtyGradeId: null, grades: [],
  version: 1, adjustReason: null, voidReason: null, lockedAtUtc: null, adjustedFrom: null,
});
// One house, and it recorded: the ordinary complete day. `missing` is the day
// nobody recorded and `partly` the day some houses did — the two distinctions
// #780 added, and the two that used to arrive identical to a complete zero.
const day = (date: string, totalEggs: number, henDays = 100, recordedFlocks = 1, expectedFlocks = 1): ProductionDay => ({
  date, totalEggs, cracked: 0, dirty: 0, discarded: 0, sellable: totalEggs, fromCounts: 0,
  deaths: 0, recordedFlocks, expectedFlocks, henDays,
  recordedHenDays: expectedFlocks > 0 ? Math.round((henDays * recordedFlocks) / expectedFlocks) : 0,
  henDayPct: henDays > 0 && recordedFlocks > 0 ? Math.round((totalEggs * 1000) / henDays) / 10 : null,
});
const missing = (date: string) => day(date, 0, 100, 0, 1);
const partly = (date: string, totalEggs: number, recorded = 1, expected = 3) =>
  day(date, totalEggs, 100, recorded, expected);
const report = (periodHenDayPct: number | null, days: ProductionDay[] = []): ProductionReport => ({
  days, totalEggs: 0, totalSellable: 0, totalFromCounts: 0, totalDeaths: 0, totalHenDays: 0,
  totalRecordedHenDays: 0, periodHenDayPct, gradeTotals: [],
});

describe("captureTiles (#654, INV-3, INV-9)", () => {
  it("puts flocks with no entry first and keeps input order inside each group", () => {
    const tiles = captureTiles(
      [flock("a", "Active"), flock("b", "Active"), flock("c", "Active"), flock("d", "Active")],
      [entry("a", "Submitted", 10), entry("c", "Draft", 3)],
    );
    expect(tiles.map((t) => [t.flock.id, t.entry === null])).toEqual([
      ["b", true], ["d", true], ["a", false], ["c", false],
    ]);
  });
  it("skips a Voided entry and picks the Submitted one that follows it (#82)", () => {
    const voided = entry("a", "Voided", 999);
    const submitted = entry("a", "Submitted", 178);
    const [tile] = captureTiles([flock("a", "Active")], [voided, submitted]);
    expect(tile.entry?.id).toBe(submitted.id);
  });
  it("treats a Voided-only day as no entry", () => {
    const [tile] = captureTiles([flock("a", "Active")], [entry("a", "Voided", 999)]);
    expect(tile.entry).toBeNull();
  });
  it("shows a Depleted or Archived flock only when it has an entry today", () => {
    expect(captureTiles([flock("d", "Depleted")], [entry("d", "Submitted", 5)]).map((t) => t.flock.id)).toEqual(["d"]);
    expect(captureTiles([flock("z", "Archived")], [entry("z", "Submitted", 1)]).map((t) => t.flock.id)).toEqual(["z"]);
    expect(captureTiles([flock("d", "Depleted"), flock("z", "Archived")], [])).toEqual([]);
  });
});

describe("visibleTiles (#654, INV-9 — the cap never hides a missing flock while ≤ 12 are missing)", () => {
  const tilesOf = (missing: number, present: number) => captureTiles(
    Array.from({ length: missing + present }, (_, i) => flock(`f${i}`, "Active")),
    Array.from({ length: present }, (_, i) => entry(`f${i}`, "Submitted", 1)), // f0..f(present-1) have entries
  );
  it("shows everything when at or under the cap", () => {
    const tiles = tilesOf(3, 9);
    expect(visibleTiles(tiles)).toEqual({ shown: tiles, hidden: 0 });
    expect(TILE_CAP).toBe(12);
  });
  it("shows the first 12 — all three missing flocks included — and counts the rest", () => {
    const tiles = tilesOf(3, 12);
    const { shown, hidden } = visibleTiles(tiles);
    // The exact twelve, in order: keeping the count and the missing-first
    // property while picking a different twelve is the wrong implementation
    // this pins.
    expect(shown.map((t) => t.flock.id)).toEqual(tiles.slice(0, 12).map((t) => t.flock.id));
    expect(shown.slice(0, 3).every((t) => t.entry === null)).toBe(true);
    expect(hidden).toBe(3);
  });
  it("with 13 missing flocks shows 12 of them and counts one hidden", () => {
    const tiles = tilesOf(13, 0);
    const { shown, hidden } = visibleTiles(tiles);
    expect(shown.map((t) => t.flock.id)).toEqual(tiles.slice(0, 12).map((t) => t.flock.id));
    expect(shown.every((t) => t.entry === null)).toBe(true);
    expect(hidden).toBe(1);
  });
});

describe("todaysEggs (#654, INV-3)", () => {
  it("sums non-Voided entries only — 178 beside a Voided 999 is 178", () => {
    expect(todaysEggs([entry("a", "Submitted", 178), entry("b", "Voided", 999)])).toBe(178);
  });
  it("counts a Draft (captured, not yet submitted) and returns 0 for no entries", () => {
    expect(todaysEggs([entry("a", "Draft", 40), entry("b", "Locked", 2)])).toBe(42);
    expect(todaysEggs([])).toBe(0);
  });
});

describe("dayStrip (#654, #777, #780 — one slot per day, bars anchored at zero)", () => {
  const shape = (d: ReturnType<typeof dayStrip>) =>
    d.slots.map((s) => [s.date, s.kind, s.kind === "recorded" ? s.heightPct : null]);

  it("sizes each bar as its share of the peak, oldest first", () => {
    const d = dayStrip({ days: [day("2026-07-01", 5), day("2026-07-02", 10), day("2026-07-03", 8)] });
    expect(shape(d)).toEqual([
      ["2026-07-01", "recorded", 50], ["2026-07-02", "recorded", 100], ["2026-07-03", "recorded", 80],
    ]);
    expect(d).toMatchObject({ max: 10, average: 7.7, complete: 3, partial: 0, unrecorded: 0 });
  });

  // The whole of #780 in one assertion. Before `entryCount` both of these days
  // arrived as totalEggs 0 and drew the same empty slot, so the chart asserted
  // a zero it had no evidence for.
  it("separates a day that produced no eggs from a day nobody recorded", () => {
    const d = dayStrip({ days: [day("2026-07-01", 400), day("2026-07-02", 0), missing("2026-07-03")] });
    expect(shape(d)).toEqual([
      ["2026-07-01", "recorded", 100],
      // Recorded and genuinely zero: a stub at the baseline, not nothing.
      ["2026-07-02", "recorded", 2],
      ["2026-07-03", "unrecorded", null],
    ]);
    expect(d).toMatchObject({ complete: 2, unrecorded: 1 });
  });

  it("computes the peak and the average over the recorded days only", () => {
    // The unrecorded day is worth nothing to either. Averaging over the
    // calendar rather than over the evidence would report 166.7.
    const d = dayStrip({ days: [day("2026-07-01", 300), missing("2026-07-02"), day("2026-07-03", 200)] });
    expect(d).toMatchObject({ max: 300, average: 250, averagePct: 83.3 });
  });

  // A day only some houses reported is its own state: its total is a floor, so
  // it must not set the peak, must not move the average, and must not be drawn
  // as a complete day that produced less.
  it("keeps a partly recorded day out of the peak and the average", () => {
    const d = dayStrip({ days: [day("2026-07-01", 300), partly("2026-07-02", 500, 2, 3), day("2026-07-03", 200)] });
    expect(d).toMatchObject({ max: 300, average: 250, complete: 2, partial: 1, unrecorded: 0 });
    expect(d.slots[1]).toMatchObject({ kind: "partial", eggs: 500, recordedFlocks: 2, expectedFlocks: 3 });
    // 500 over a 300 peak would overflow the slot, so the bar caps at the top.
    expect(d.slots[1]).toMatchObject({ heightPct: 100 });
  });

  it("has no peak or average when no day was recorded by every house", () => {
    const d = dayStrip({ days: [partly("2026-07-01", 500, 2, 3), missing("2026-07-02")] });
    expect(d).toMatchObject({ max: null, average: null, averagePct: null, complete: 0, partial: 1, unrecorded: 1 });
  });

  it("owes nothing on a day the farm had no flocks, rather than counting a gap", () => {
    // Before the first placement, 0 recorded of 0 expected is not a shortfall.
    // The old shape called it `unrecorded`, so a new farm's first fortnight
    // announced fourteen missing filings against an obligation nobody had.
    const d = dayStrip({ days: [day("2026-07-01", 0, 0, 0, 0)] });
    expect(d).toMatchObject({ complete: 0, partial: 0, unrecorded: 0 });
    expect(d.slots[0].kind).toBe("none");
  });

  // `isComplete` is `recorded > 0 && recorded >= expected`. The `>=` decides
  // only the case where MORE flocks filed than were expected, which happens at
  // a depletion boundary — a flock files against a date the bird ledger says it
  // had already ended. Under `===` that day flips to partial and its readout
  // reads "2 of 1 flocks". The `> 0` conjunct short-circuits the 0/0 case above
  // before `>=` is ever evaluated, so that test does NOT cover this.
  it("counts a day where more flocks filed than were expected as complete", () => {
    const d = dayStrip({ days: [day("2026-07-01", 90, 100, 2, 1)] });
    expect(d).toMatchObject({ complete: 1, partial: 0, unrecorded: 0 });
    expect(d.slots[0].kind).toBe("recorded");
  });

  it("floors a recorded day at 2% so the farm's worst real day is still a bar", () => {
    const d = dayStrip({ days: [day("2026-07-01", 1000), day("2026-07-02", 3)] });
    expect(d.slots[1]).toMatchObject({ date: "2026-07-02", kind: "recorded", heightPct: 2 });
  });

  it("marks the week boundary at the start of the recent window, and nowhere else", () => {
    const days = Array.from({ length: 14 }, (_, i) => day(`2026-07-${String(i + 1).padStart(2, "0")}`, 100 + i));
    const breaks = dayStrip({ days, recentCount: 7 }).slots.map((s) => s.weekBreak);
    expect(breaks.filter(Boolean)).toHaveLength(1);
    expect(breaks.indexOf(true)).toBe(7);
    expect(dayStrip({ days, recentCount: 7 }).slots[7].date).toBe("2026-07-08");
  });

  it("draws no boundary when the recent window is absent or is the whole window", () => {
    const days = [day("2026-07-01", 1), day("2026-07-02", 2)];
    expect(dayStrip({ days }).slots.some((s) => s.weekBreak)).toBe(false);
    expect(dayStrip({ days, recentCount: 2 }).slots.some((s) => s.weekBreak)).toBe(false);
  });

  it("keeps a stub on every day when every recorded day is zero, and draws no average line", () => {
    // A real average of 0 with no scale to place it on. The line would sit on
    // the floor and read as data.
    const d = dayStrip({ days: [day("2026-07-01", 0), day("2026-07-02", 0)] });
    expect(shape(d)).toEqual([["2026-07-01", "recorded", 2], ["2026-07-02", "recorded", 2]]);
    expect(d).toMatchObject({ max: 0, average: 0, averagePct: null });
  });

  it("has no peak and no average when no day in the window was recorded", () => {
    const d = dayStrip({ days: [missing("2026-07-01"), missing("2026-07-02")] });
    expect(shape(d)).toEqual([["2026-07-01", "unrecorded", null], ["2026-07-02", "unrecorded", null]]);
    expect(d).toMatchObject({ max: null, average: null, complete: 0, unrecorded: 2 });
  });

  it("is empty for no days and one full-height slot for one day", () => {
    expect(dayStrip({ days: [] })).toMatchObject({ slots: [], max: null, average: null });
    expect(dayStrip({ days: [day("2026-07-01", 7)] }).slots[0])
      .toMatchObject({ kind: "recorded", heightPct: 100 });
  });
});

describe("henDayTrend (#654, INV-5 — the server's figure, never a re-sum)", () => {
  it("reads periodHenDayPct from each report and returns their difference to 1 dp", () => {
    expect(henDayTrend(report(87.4), report(85.1))).toEqual({ current: 87.4, previous: 85.1, delta: 2.3 });
    expect(henDayTrend(report(80), report(82.5))).toEqual({ current: 80, previous: 82.5, delta: -2.5 });
  });
  it("never derives any of the three figures from the day rows", () => {
    // Rows that would AVERAGE to 60 and re-SUM to 60 — the period figures the server sent must win, on both sides.
    const rows = [day("2026-07-01", 100, 100), day("2026-07-02", 20, 100)];
    expect(henDayTrend(report(91.7, rows), report(33.3, rows))).toEqual({ current: 91.7, previous: 33.3, delta: 58.4 });
  });
  it("propagates null (no hen-days in that window) without inventing a zero", () => {
    expect(henDayTrend(report(87.4), report(null))).toEqual({ current: 87.4, previous: null, delta: null });
    expect(henDayTrend(report(null), report(85.1))).toEqual({ current: null, previous: 85.1, delta: null });
    expect(henDayTrend(report(null), report(null))).toEqual({ current: null, previous: null, delta: null });
  });
});

describe("stockBar (#654, INV-4, #777)", () => {
  const rows: StockRow[] = [
    { eggGradeId: "g1", gradeName: "Large", available: 1240, restricted: 0 },
    { eggGradeId: "g2", gradeName: "Medium", available: 320, restricted: 12 },
    { eggGradeId: "g3", gradeName: "Pee-wee", available: 0, restricted: 3 },
  ];
  it("gives each non-empty grade its exact share of the plain available total", () => {
    const bar = stockBar(rows);
    expect(bar.totalAvailable).toBe(rows.reduce((a, r) => a + r.available, 0)); // the Stock screen's reduce
    expect(bar.totalRestricted).toBe(15);
    expect(bar.segments.map((s) => [s.gradeName, s.pct, s.colorIndex])).toEqual([
      ["Large", 79.5, 1], ["Medium", 20.5, 2],
    ]);
  });
  it("cycles the hue wheel past the last colour instead of repeating one fill", () => {
    // The opacity ramp this replaced hit its 0.35 floor at the sixth grade, so
    // a seventh and an eighth were literally the same fill.
    const many = Array.from({ length: GRADE_COLOURS + 2 }, (_, i) => (
      { eggGradeId: `g${i}`, gradeName: `G${i}`, available: 10, restricted: 0 }
    ));
    const indexes = stockBar(many).segments.map((s) => s.colorIndex);
    expect(indexes.slice(0, GRADE_COLOURS)).toEqual(Array.from({ length: GRADE_COLOURS }, (_, i) => i + 1));
    expect(new Set(indexes.slice(0, GRADE_COLOURS)).size).toBe(GRADE_COLOURS);
    expect(indexes.slice(GRADE_COLOURS)).toEqual([1, 2]);
  });
  it("keeps a grade's hue when a DIFFERENT grade sells out", () => {
    // The defect this pins: off the filtered index the hue is positional, so
    // one sale renames every colour after it. Same three grades, two days.
    const grades = [
      { eggGradeId: "g1", gradeName: "Large", restricted: 0 },
      { eggGradeId: "g2", gradeName: "Medium", restricted: 0 },
      { eggGradeId: "g3", gradeName: "Small", restricted: 0 },
    ];
    const hues = (available: number[]) => Object.fromEntries(
      stockBar(grades.map((g, i) => ({ ...g, available: available[i] })))
        .segments.map((s) => [s.gradeName, s.colorIndex]),
    );
    expect(hues([100, 60, 40])).toEqual({ Large: 1, Medium: 2, Small: 3 });
    expect(hues([0, 60, 40])).toEqual({ Medium: 2, Small: 3 });
    expect(hues([100, 0, 40])).toEqual({ Large: 1, Small: 3 });
  });

  it("indexes the hue by the grade's place in the FULL row set, gaps included", () => {
    const bar = stockBar([
      { eggGradeId: "z0", gradeName: "Empty first", available: 0, restricted: 0 },
      { eggGradeId: "g1", gradeName: "Large", available: 100, restricted: 0 },
      { eggGradeId: "z1", gradeName: "Empty middle", available: 0, restricted: 0 },
      { eggGradeId: "g2", gradeName: "Medium", available: 60, restricted: 0 },
      { eggGradeId: "g3", gradeName: "Small", available: 40, restricted: 0 },
    ]);
    expect(bar.segments.map((s) => [s.gradeName, s.colorIndex])).toEqual([
      ["Large", 2], ["Medium", 4], ["Small", 5],
    ]);
    expect(bar.segments.map((s) => s.pct)).toEqual([50, 30, 20]);
  });
  it("has no segments and zero available when nothing is available, but keeps the restricted total", () => {
    expect(stockBar([])).toEqual({ segments: [], totalAvailable: 0, totalRestricted: 0 });
    expect(stockBar([{ eggGradeId: "g", gradeName: "G", available: 0, restricted: 4 }]))
      .toEqual({ segments: [], totalAvailable: 0, totalRestricted: 4 });
  });
});
