import { apiGet } from "../client";

// --- Reports (#91) ---

export interface ProductionDay {
  date: string;
  totalEggs: number;
  cracked: number;
  dirty: number;
  discarded: number;
  /** Hand-graded remainder: total − cracked − dirty − discarded (#394). */
  sellable: number;
  /**
   * #396 — eggs that became stock WITHOUT being hand-graded: the cracked and
   * dirty counters, but only where that entry resolved the condition to a
   * grade. Separate from `sellable` on purpose; the two answer different
   * questions and were only ever equal while conditions were always losses.
   */
  fromCounts: number;
  deaths: number;
  /**
   * #780 — houses that filed an OFFICIAL entry (Submitted, Locked or
   * ManagerAdjusted). The field that separates a day nobody recorded from one
   * that genuinely produced no eggs: every other figure here is 0 for both. A
   * day holding only a Draft counts 0 here while the capture tiles still show
   * it as captured.
   */
  recordedFlocks: number;
  /**
   * #780 — houses that owed a filing that day: placed, not yet depleted or
   * archived. `recordedFlocks < expectedFlocks` is a PARTIALLY recorded day,
   * whose `totalEggs` is a floor rather than a figure.
   */
  expectedFlocks: number;
  /**
   * #780 — expected flocks with no filing, and the only sound test for a
   * partly recorded day. Comparing `recordedFlocks` against `expectedFlocks`
   * is not: they count different sets, so expected {A, B} against filings
   * {A, C} gives 2 and 2 and hides B.
   */
  missingFlocks: number;
  /** Every bird alive that day, whether or not its house reported. */
  henDays: number;
  /** The exposure that reported — `henDayPct`'s denominator (#780). */
  recordedHenDays: number;
  /**
   * `henDayPct`'s NUMERATOR (#780): the eggs of exactly the flocks whose birds
   * are in `recordedHenDays`. Equal to `totalEggs` on any ordinary day; they
   * part only where a flock filed while the bird ledger says it had no birds,
   * and carrying it is what keeps the rate reproducible from the payload.
   */
  ratedEggs: number;
  /** `ratedEggs` over `recordedHenDays`. null when nothing reported. */
  henDayPct: number | null;
}

export interface ProductionReport {
  days: ProductionDay[];
  totalEggs: number;
  totalSellable: number;
  totalFromCounts: number;
  /**
   * #1297 — the eggs the period produced but never turned into stock: the raw
   * cracked/dirty/discarded counters minus `totalFromCounts`. The Losses tile
   * reads this instead of summing days in the browser.
   */
  totalLosses: number;
  totalDeaths: number;
  totalHenDays: number;
  /** `periodHenDayPct`'s denominator and numerator, so it is reproducible (#780). */
  totalRecordedHenDays: number;
  totalRatedEggs: number;
  periodHenDayPct: number | null;
  /**
   * #1297 — (totalSellable + totalFromCounts) / totalEggs × 100 and
   * totalLosses / totalEggs × 100, rounded like `periodHenDayPct`. Together
   * they add up to 100, and both follow the Cracked and Dirty grades'
   * saleable setting via `totalFromCounts`. null for a period with no eggs.
   */
  periodSellablePct: number | null;
  periodLossPct: number | null;
  gradeTotals: { eggGradeId: string; name: string; quantity: number }[];
  /**
   * #1299 — per-flock breakdown: one row per flock that filed an official
   * entry in the range, computed server-side by the same walk as the period
   * totals, so its columns sum to them. `henDayPct` follows the #780 rules at
   * flock grain (recorded hen-days only, numerator limited to the flock) and
   * is null — never 0 — for a flock with no recorded exposure. `houseId` is
   * the flock's house; houses have no aggregate yet, so there is no house name.
   */
  flockTotals: {
    flockId: string;
    flockName: string;
    /** The flock's placement date (names are not unique — the row needs a second detail). */
    placedOn: string;
    houseId: string;
    eggs: number;
    sellable: number;
    losses: number;
    deaths: number;
    recordedHenDays: number;
    ratedEggs: number;
    henDayPct: number | null;
  }[];
}

export interface SalesSummary {
  confirmedCount: number;
  revenueMinorUnits: number;
  paidMinorUnits: number;
  outstandingMinorUnits: number;
  voidedCount: number;
  /** Individual eggs (QuantityBase) on confirmed egg lines in the range — the same orders revenue sums. */
  eggsSold: number;
  eggsSoldByGrade: { eggGradeId: string; name: string; quantity: number }[];
  currencyCode: string;
  currencyMinorUnit: number;
}

export interface ExpenseSummaryReport {
  categories: { expenseCategoryId: string; name: string; totalMinorUnits: number }[];
  grandTotalMinorUnits: number;
  currencyCode: string;
  currencyMinorUnit: number;
}

export interface ProfitReport {
  revenueMinorUnits: number;
  expensesMinorUnits: number;
  profitMinorUnits: number;
  /**
   * #1298 — Σ FeedUsage.EstimatedCost over the range, and that total × 12 over
   * the official eggs the production report counts, rounded to a whole minor
   * unit by the server; null per dozen when the range collected no eggs.
   */
  feedCostMinorUnits: number;
  feedCostPerDozenMinorUnits: number | null;
  currencyCode: string;
  currencyMinorUnit: number;
}

const rangeQuery = (from: string, to: string) => `?from=${from}&to=${to}`;

// #916 — `flockId` scopes the whole report (days, totals, hen-day %) to one
// flock, server-side (IReportQueries.cs / FlockScope's own query filter): the
// browser never filters an unscoped payload itself. Omitted, the report
// covers every flock the caller can see, exactly as before.
export const getProductionReport = (from: string, to: string, flockId?: string, signal?: AbortSignal) =>
  apiGet<ProductionReport>(`/reports/production${rangeQuery(from, to)}${flockId ? `&flockId=${flockId}` : ""}`, signal);
export const getSalesSummary = (from: string, to: string) =>
  apiGet<SalesSummary>(`/reports/sales${rangeQuery(from, to)}`);
export const getExpenseSummary = (from: string, to: string) =>
  apiGet<ExpenseSummaryReport>(`/reports/expenses${rangeQuery(from, to)}`);
export const getProfitReport = (from: string, to: string) =>
  apiGet<ProfitReport>(`/reports/profit${rangeQuery(from, to)}`);
