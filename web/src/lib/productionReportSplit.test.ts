// web/src/lib/productionReportSplit.test.ts
import { describe, it, expect } from "vitest";
import { splitProductionReport } from "./productionReportSplit";
import type { ProductionDay, ProductionReport } from "../api/cluckwork";

const day = (date: string, totalEggs: number, ratedEggs: number, recordedHenDays: number): ProductionDay => ({
  date, totalEggs, cracked: 0, dirty: 0, discarded: 0, sellable: totalEggs, fromCounts: 0, deaths: 0,
  recordedFlocks: 1, expectedFlocks: 1, missingFlocks: 0, henDays: 100, recordedHenDays, ratedEggs,
  henDayPct: recordedHenDays > 0 ? Math.round((ratedEggs * 100 / recordedHenDays) * 10) / 10 : null,
});
const report = (days: ProductionDay[]): ProductionReport => ({
  days,
  totalEggs: days.reduce((a, d) => a + d.totalEggs, 0),
  totalSellable: days.reduce((a, d) => a + d.sellable, 0),
  totalFromCounts: days.reduce((a, d) => a + d.fromCounts, 0),
  totalDeaths: days.reduce((a, d) => a + d.deaths, 0),
  totalHenDays: days.reduce((a, d) => a + d.henDays, 0),
  totalRecordedHenDays: days.reduce((a, d) => a + d.recordedHenDays, 0),
  totalRatedEggs: days.reduce((a, d) => a + d.ratedEggs, 0),
  periodHenDayPct: null, // irrelevant: the split recomputes its own from `days`, never trusts this
  gradeTotals: [{ eggGradeId: "g1", name: "Grade A", quantity: 999 }],
});

describe("splitProductionReport (#918 — folds two adjacent trend windows into one request)", () => {
  it("partitions days by date and recomputes each half's totals from its own days", () => {
    const fortnight = report([
      day("2026-07-08", 300, 300, 100), day("2026-07-09", 301, 0, 100), day("2026-07-10", 302, 0, 100),
      day("2026-07-11", 303, 0, 100), day("2026-07-12", 304, 0, 100), day("2026-07-13", 305, 0, 100),
      day("2026-07-14", 306, 296, 100), // previous week's 7 days: ratedEggs sums to 596 over 700 henDays
      day("2026-07-15", 320, 320, 100), day("2026-07-16", 321, 0, 100), day("2026-07-17", 322, 0, 100),
      day("2026-07-18", 323, 0, 100), day("2026-07-19", 324, 0, 100), day("2026-07-20", 325, 0, 100),
      day("2026-07-21", 326, 292, 100), // current week's 7 days: ratedEggs sums to 612 over 700 henDays
    ]);

    const { earlier, later } = splitProductionReport(fortnight, "2026-07-15");

    expect(earlier.days.map((d) => d.date)).toEqual([
      "2026-07-08", "2026-07-09", "2026-07-10", "2026-07-11", "2026-07-12", "2026-07-13", "2026-07-14",
    ]);
    expect(later.days.map((d) => d.date)).toEqual([
      "2026-07-15", "2026-07-16", "2026-07-17", "2026-07-18", "2026-07-19", "2026-07-20", "2026-07-21",
    ]);
    // The exact server formula (ReportQueries.GetProductionAsync):
    // round(totalRatedEggs * 100 / totalRecordedHenDays, 1).
    expect(earlier.periodHenDayPct).toBe(85.1); // round(596*100/700, 1)
    expect(later.periodHenDayPct).toBe(87.4); // round(612*100/700, 1)
    expect(earlier.totalEggs).toBe(300 + 301 + 302 + 303 + 304 + 305 + 306);
    expect(later.totalEggs).toBe(320 + 321 + 322 + 323 + 324 + 325 + 326);
    expect(earlier.totalRecordedHenDays).toBe(700);
    expect(later.totalRecordedHenDays).toBe(700);
    // Nothing in `days[]` carries a per-grade breakdown; recomputing it would
    // be a guess, so each half's is deliberately empty rather than borrowed
    // from the combined report.
    expect(earlier.gradeTotals).toEqual([]);
    expect(later.gradeTotals).toEqual([]);
  });

  it("reports null, never 0, when a half's exposure is entirely unrecorded", () => {
    const fortnight = report([
      day("2026-07-08", 0, 0, 0), day("2026-07-09", 0, 0, 0), day("2026-07-10", 0, 0, 0),
      day("2026-07-11", 0, 0, 0), day("2026-07-12", 0, 0, 0), day("2026-07-13", 0, 0, 0), day("2026-07-14", 0, 0, 0),
      day("2026-07-15", 320, 320, 100),
    ]);
    const { earlier } = splitProductionReport(fortnight, "2026-07-15");
    expect(earlier.periodHenDayPct).toBeNull();
  });
});
