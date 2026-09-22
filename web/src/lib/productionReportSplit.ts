// web/src/lib/productionReportSplit.ts
import type { ProductionDay, ProductionReport } from "../api/cluckwork";

// #918 — Codex review: the Dashboard used to fire two adjacent production
// requests per load (current week, previous week) plus a third for the
// Morning collection panel's own yesterday-close caption — three of the
// account's four shared report-concurrency permits
// (RateLimitingOptions.ReportsConcurrency: PermitLimit 4, QueueLimit 0, no
// queue), so two workers on the same farm opening the app together could
// exceed it. The two adjacent windows are now ONE `daysBefore(today,14)..
// daysBefore(today,1)` request, split here into the two weeks the trend
// comparison needs.
//
// This is deliberately NOT the general "pages/cards never re-derive or sum
// rows" rule (IReportQueries.cs, lib/dashboard.ts) reopened: every
// report-level total is a PLAIN SUM over `days[]`, computed the identical
// way server-side (ReportQueries.GetProductionAsync) —
// `totalEggs = sum(days.totalEggs)`, `periodHenDayPct = round(sum(days.
// ratedEggs) * 100 / sum(days.recordedHenDays), 1)` — so summing a SUBSET of
// an already-fetched, contiguous period reproduces exactly what a second
// request for that subset would have returned. Proven against the real
// server in dashboard-flock-scope.spec.ts.
//
// `gradeTotals` cannot be reconstructed this way — it is a per-grade
// breakdown the per-day rows do not carry — so each half's is empty. Neither
// half of the split is ever rendered with a grade breakdown (the Dashboard's
// trend consumers, `dayStrip`/`henDayTrend`, read only `days` and
// `periodHenDayPct`), so this is a deliberate, harmless gap, not a guess.
export function splitProductionReport(report: ProductionReport, cutoffDate: string): {
  earlier: ProductionReport;
  later: ProductionReport;
} {
  const earlierDays: ProductionDay[] = [];
  const laterDays: ProductionDay[] = [];
  for (const d of report.days) (d.date < cutoffDate ? earlierDays : laterDays).push(d);
  return { earlier: summarize(earlierDays), later: summarize(laterDays) };
}

function summarize(days: ProductionDay[]): ProductionReport {
  const sum = (pick: (d: ProductionDay) => number) => days.reduce((total, d) => total + pick(d), 0);
  const totalRatedEggs = sum((d) => d.ratedEggs);
  const totalRecordedHenDays = sum((d) => d.recordedHenDays);
  return {
    days,
    totalEggs: sum((d) => d.totalEggs),
    totalSellable: sum((d) => d.sellable),
    totalFromCounts: sum((d) => d.fromCounts),
    totalDeaths: sum((d) => d.deaths),
    totalHenDays: sum((d) => d.henDays),
    totalRecordedHenDays,
    totalRatedEggs,
    periodHenDayPct: totalRecordedHenDays > 0 ? Math.round((totalRatedEggs * 100 / totalRecordedHenDays) * 10) / 10 : null,
    gradeTotals: [],
  };
}
