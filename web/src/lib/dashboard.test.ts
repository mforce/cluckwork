// web/src/lib/dashboard.test.ts
import { describe, it, expect } from "vitest";
import {
  TILE_CAP, captureTiles, henDayTrend, sparkline, stockBar, todaysEggs, visibleTiles,
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
const day = (date: string, totalEggs: number, henDays = 100): ProductionDay => ({
  date, totalEggs, cracked: 0, dirty: 0, discarded: 0, sellable: totalEggs, fromCounts: 0,
  deaths: 0, henDays, henDayPct: henDays > 0 ? Math.round((totalEggs * 1000) / henDays) / 10 : null,
});
const report = (periodHenDayPct: number | null, days: ProductionDay[] = []): ProductionReport => ({
  days, totalEggs: 0, totalSellable: 0, totalFromCounts: 0, totalDeaths: 0, totalHenDays: 0,
  periodHenDayPct, gradeTotals: [],
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

describe("sparkline (#654, INV-5 geometry)", () => {
  it("maps [0, 10, 5] to exact viewBox points, oldest first", () => {
    const s = sparkline([day("2026-07-01", 0), day("2026-07-02", 10), day("2026-07-03", 5)]);
    expect(s.points).toBe("0,32 50,0 100,16");
    expect(s.values).toEqual([0, 10, 5]);
    expect(s).toMatchObject({ min: 0, max: 10, last: 5 });
  });
  it("maps the 14-day dashboard fixture to the exact point string", () => {
    // previous week 307..301 then current week 327..321 (oldest first).
    const values = [307, 306, 305, 304, 303, 302, 301, 327, 326, 325, 324, 323, 322, 321];
    const s = sparkline(values.map((v, i) => day(`2026-07-${String(i + 1).padStart(2, "0")}`, v)));
    expect(s.points).toBe(
      "0,2 7.7,2.1 15.4,2.2 23.1,2.3 30.8,2.3 38.5,2.4 46.2,2.5 53.8,0 61.5,0.1 69.2,0.2 76.9,0.3 84.6,0.4 92.3,0.5 100,0.6",
    );
    expect(s).toMatchObject({ min: 301, max: 327, last: 321 });
  });
  it("draws a flat baseline (every y = 32) when every day is zero", () => {
    const s = sparkline([day("2026-07-01", 0), day("2026-07-02", 0)]);
    expect(s.points).toBe("0,32 100,32");
    expect(s).toMatchObject({ min: 0, max: 0, last: 0 });
  });
  it("is empty for no days and a single point for one day", () => {
    expect(sparkline([])).toEqual({ points: "", values: [], min: 0, max: 0, last: 0 });
    expect(sparkline([day("2026-07-01", 7)]).points).toBe("0,0");
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

describe("stockBar (#654, INV-4)", () => {
  const rows: StockRow[] = [
    { eggGradeId: "g1", gradeName: "Large", available: 1240, restricted: 0 },
    { eggGradeId: "g2", gradeName: "Medium", available: 320, restricted: 12 },
    { eggGradeId: "g3", gradeName: "Pee-wee", available: 0, restricted: 3 },
  ];
  it("gives each non-empty grade its exact share of the plain available total", () => {
    const bar = stockBar(rows);
    expect(bar.totalAvailable).toBe(rows.reduce((a, r) => a + r.available, 0)); // the Stock screen's reduce
    expect(bar.totalRestricted).toBe(15);
    expect(bar.segments.map((s) => [s.gradeName, s.pct, s.opacity])).toEqual([
      ["Large", 79.5, 1], ["Medium", 20.5, 0.87],
    ]);
  });
  it("floors the opacity at 0.35 for many grades", () => {
    const many = Array.from({ length: 8 }, (_, i) => ({ eggGradeId: `g${i}`, gradeName: `G${i}`, available: 10, restricted: 0 }));
    expect(stockBar(many).segments.map((s) => s.opacity)).toEqual([1, 0.87, 0.74, 0.61, 0.48, 0.35, 0.35, 0.35]);
  });
  it("indexes the opacity ladder by SURVIVING segment, not by the row's position among all rows", () => {
    // Two empty grades, one leading and one in the middle: an implementation
    // that read the ladder off the original row index would hand these
    // segments 0.87 and 0.61 instead of 0.87 and 0.74.
    const bar = stockBar([
      { eggGradeId: "z0", gradeName: "Empty first", available: 0, restricted: 0 },
      { eggGradeId: "g1", gradeName: "Large", available: 100, restricted: 0 },
      { eggGradeId: "z1", gradeName: "Empty middle", available: 0, restricted: 0 },
      { eggGradeId: "g2", gradeName: "Medium", available: 60, restricted: 0 },
      { eggGradeId: "g3", gradeName: "Small", available: 40, restricted: 0 },
    ]);
    expect(bar.segments.map((s) => [s.gradeName, s.opacity])).toEqual([
      ["Large", 1], ["Medium", 0.87], ["Small", 0.74],
    ]);
    expect(bar.segments.map((s) => s.pct)).toEqual([50, 30, 20]);
  });
  it("has no segments and zero available when nothing is available, but keeps the restricted total", () => {
    expect(stockBar([])).toEqual({ segments: [], totalAvailable: 0, totalRestricted: 0 });
    expect(stockBar([{ eggGradeId: "g", gradeName: "G", available: 0, restricted: 4 }]))
      .toEqual({ segments: [], totalAvailable: 0, totalRestricted: 4 });
  });
});
