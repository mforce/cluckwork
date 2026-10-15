import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, waitFor } from "@testing-library/react";
import { ReportsPage } from "./ReportsPage";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  getExpenseSummary, getProductionReport, getProfitReport, getSalesSummary,
} from "../api/cluckwork";
import type {
  ExpenseSummaryReport, ProductionReport, ProfitReport, SalesSummary,
} from "../api/cluckwork";
import { ApiError } from "../api/client";
import i18n from "../i18n";
import { responsiveStyle } from "../test/renderedStyle";

// Keep the REAL formatMoney (the sales/expenses/profit templates under test
// interpolate its output as pre-formatted DATA — see the `reports` namespace
// header comment in en.ts); stub only the four report-read endpoints, the
// network seam this screen depends on. useFarmToday + useAuth ride on
// renderWithProviders.
vi.mock("../api/cluckwork", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/cluckwork")>();
  return {
    ...actual,
    getProductionReport: vi.fn(),
    getSalesSummary: vi.fn(),
    getExpenseSummary: vi.fn(),
    getProfitReport: vi.fn(),
    getFlock: vi.fn(),
  getCustomer: vi.fn(),
};
});

const mockGetProductionReport = vi.mocked(getProductionReport);
const mockGetSalesSummary = vi.mocked(getSalesSummary);
const mockGetExpenseSummary = vi.mocked(getExpenseSummary);
const mockGetProfitReport = vi.mocked(getProfitReport);

// Two days: one with a real henDayPct, one with null — exercises both sides
// of the `?? "—"` fallback (DATA, left raw per the namespace header comment).
// Every day's halves reproduce its percentage (100 / 109 = 91.7): the over-100
// flag decides from the halves, so an inconsistent fixture would flag itself.
const PRODUCTION: ProductionReport = {
  days: [
    { date: "2026-07-19", totalEggs: 100, cracked: 2, dirty: 3, discarded: 5, sellable: 90, fromCounts: 6, deaths: 1, recordedFlocks: 1, expectedFlocks: 1, missingFlocks: 0, henDays: 109, recordedHenDays: 109, ratedEggs: 100, henDayPct: 91.7 },
    { date: "2026-07-18", totalEggs: 95, cracked: 1, dirty: 1, discarded: 2, sellable: 91, fromCounts: 0, deaths: 0, recordedFlocks: 1, expectedFlocks: 1, missingFlocks: 0, henDays: 98, recordedHenDays: 98, ratedEggs: 95, henDayPct: null },
  ],
  totalEggs: 195, totalSellable: 181, totalFromCounts: 6,
  // #1297 — the server's losses total and rates: (2+3+5)+(1+1+2) − 6 = 8,
  // (181+6)/195 = 95.9, 8/195 = 4.1 — the two rates add up to 100.
  totalLosses: 8, totalDeaths: 1, totalHenDays: 207, totalRecordedHenDays: 207, totalRatedEggs: 195,
  periodHenDayPct: 94.2, periodSellablePct: 95.9, periodLossPct: 4.1,
  gradeTotals: [
    { eggGradeId: "gr1", name: "Grade A", quantity: 60 },
    { eggGradeId: "gr2", name: "Grade B", quantity: 30 },
  ],
  // #1299 — the per-flock breakdown: two rows that reconcile with the totals
  // above (140+55 = 195 eggs, 130+51 = 181 sellable, 5+3 = 8 losses, 1+0 = 1
  // death, 140+55 = 195 rated). House B sat outside its recorded window, so
  // it carries no recorded exposure and a dash — not 0 — under Hen-day %.
  flockTotals: [
    // placedOn — placement dates; names are not unique, the date keeps rows apart.
    { flockId: "f1", flockName: "House A", placedOn: "2026-06-01", houseId: "h1", eggs: 140, sellable: 130, losses: 5, deaths: 1, recordedHenDays: 200, ratedEggs: 140, henDayPct: 70.0 },
    { flockId: "f2", flockName: "House B", placedOn: "2026-07-15", houseId: "h2", eggs: 55, sellable: 51, losses: 3, deaths: 0, recordedHenDays: 0, ratedEggs: 0, henDayPct: null },
  ],
};
const PRODUCTION_NO_GRADES: ProductionReport = { ...PRODUCTION, gradeTotals: [] };
// #943 — a rate above 100% (a hen lays at most one egg a day). Day 1 rates
// 104 eggs over 100 recorded hen-days; the period, 199 over 196.
const PRODUCTION_OVER_100: ProductionReport = {
  ...PRODUCTION,
  days: [
    { ...PRODUCTION.days[0], totalEggs: 104, ratedEggs: 104, recordedHenDays: 100, henDayPct: 104.0 },
    PRODUCTION.days[1],
  ],
  totalRecordedHenDays: 196, totalRatedEggs: 199, periodHenDayPct: 101.5,
};
// The flag decides from the unrounded halves: 10,001 over 10,000 displays as
// 100.0 and is still an excess; 100 over 100 is not. The period sums both.
const PRODUCTION_ROUNDS_TO_100: ProductionReport = {
  ...PRODUCTION,
  days: [
    { ...PRODUCTION.days[0], totalEggs: 10001, ratedEggs: 10001, recordedHenDays: 10000, henDayPct: 100.0 },
    { ...PRODUCTION.days[1], totalEggs: 100, ratedEggs: 100, recordedHenDays: 100, henDayPct: 100.0 },
  ],
  totalRatedEggs: 10101, totalRecordedHenDays: 10100, periodHenDayPct: 100.0,
};
const OVER_100_FLAG = "Above 100%: more eggs were recorded than birds on the ledger. Check the flocks' bird counts.";

const SALES: SalesSummary = {
  confirmedCount: 5, revenueMinorUnits: 10000, paidMinorUnits: 8000, outstandingMinorUnits: 2000,
  voidedCount: 2, eggsSold: 360,
  eggsSoldByGrade: [
    { eggGradeId: "gr1", name: "Grade A", quantity: 240 },
    { eggGradeId: "gr2", name: "Grade B", quantity: 120 },
  ],
  currencyCode: "USD", currencyMinorUnit: 2,
};
const SALES_NO_VOIDED: SalesSummary = { ...SALES, voidedCount: 0 };

const EXPENSES: ExpenseSummaryReport = {
  categories: [
    { expenseCategoryId: "c1", name: "Feed", totalMinorUnits: 5000 },
    { expenseCategoryId: "c2", name: "Utilities", totalMinorUnits: 1500 },
  ],
  grandTotalMinorUnits: 6500, currencyCode: "USD", currencyMinorUnit: 2,
};
const EXPENSES_EMPTY: ExpenseSummaryReport = { ...EXPENSES, categories: [], grandTotalMinorUnits: 0 };

const PROFIT: ProfitReport = {
  revenueMinorUnits: 10000, expensesMinorUnits: 6500, profitMinorUnits: 3500,
  // #1298 — 122_400 minor over 240 eggs = 20 dozen → 6_120/dozen; no-egg range → null.
  feedCostMinorUnits: 122_400, feedCostPerDozenMinorUnits: 6_120,
  currencyCode: "USD", currencyMinorUnit: 2,
};

const ADMIN = { sub: "u1", role: "Admin" };
// A real, recognized non-admin role claim (claims.ts: isAdmin = Admin ||
// Manager only) — not an absent-claim stand-in — same choice HistoryPage's
// role-gating tests make.
const NON_ADMIN = { sub: "u2", role: "Sales" };

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  mockGetProductionReport.mockResolvedValue(PRODUCTION);
  mockGetSalesSummary.mockResolvedValue(SALES);
  mockGetExpenseSummary.mockResolvedValue(EXPENSES);
  mockGetProfitReport.mockResolvedValue(PROFIT);
});

describe("ReportsPage production section (renders for every role)", () => {
  it("renders the production table rows, period totals, and grade-totals line from the mocked report", async () => {
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });

    const row1 = await screen.findByRole("row", { name: /07\/19\/2026/ });
    // #780 — Eggs and Rated eggs are equal on an ordinary day, which is the
    // point: a gap between them means a filing and the bird ledger disagree
    // about that date, and the rate divides the SECOND one.
    expect(within(row1).getAllByText("100")).toHaveLength(2); // totalEggs, ratedEggs
    within(row1).getByText("2/3/5"); // cracked/dirty/discarded
    within(row1).getByText("90"); // sellable
    // #780 — Hen-days and Recorded are adjacent and equal on a fully recorded
    // day, which is the point: the gap between them is what a period is
    // missing, and eggs ÷ Recorded has to reproduce the percentage beside it.
    expect(within(row1).getAllByText("109")).toHaveLength(2); // henDays, recordedHenDays
    within(row1).getByText("91.7"); // henDayPct
    for (const cell of within(row1).getAllByText("100")) expect(cell).toHaveStyle({ textAlign: "right" });
    expect(within(row1).getByText("07/19/2026")).not.toHaveStyle({ textAlign: "right" });
    // The per-flock table repeats the Eggs header, so assert the FIRST match.
    expect(screen.getAllByRole("columnheader", { name: "Eggs" })[0]).toHaveStyle({ textAlign: "right" });

    const row2 = screen.getByRole("row", { name: /07\/18\/2026/ });
    within(row2).getByText("—"); // null henDayPct falls back to the em dash

    const periodRow = screen.getByRole("row", { name: /Period/ });
    expect(within(periodRow).getAllByText("195")).toHaveLength(2); // totalEggs, totalRatedEggs
    within(periodRow).getByText("181"); // totalSellable
    expect(within(periodRow).getAllByText("207")).toHaveLength(2); // totalHenDays, totalRecordedHenDays
    within(periodRow).getByText("94.2"); // periodHenDayPct

    expect(screen.getByRole("list", { name: "Reported grade totals" })).toHaveTextContent("Grade A60eggsGrade B30eggs");
  });

  // #943 — a rate above 100% is impossible and means a filing and the bird
  // ledger disagree. The figure stays as computed, so it still reproduces
  // from Rated eggs ÷ Recorded, and a flag beside it says so rather than the
  // page presenting it as a result.
  it("flags a hen-day % above 100 on its day row and on both period figures", async () => {
    mockGetProductionReport.mockResolvedValue(PRODUCTION_OVER_100);
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });

    const row1 = await screen.findByRole("row", { name: /07\/19\/2026/ });
    within(row1).getByText("104.0");
    within(row1).getByLabelText(OVER_100_FLAG);
    const row2 = screen.getByRole("row", { name: /07\/18\/2026/ });
    expect(within(row2).queryByLabelText(OVER_100_FLAG)).toBeNull();
    // The day row, the period summary and the period footer row.
    expect(screen.getAllByLabelText(OVER_100_FLAG)).toHaveLength(3);
  });

  it("flags an excess that rounds to 100.0 and leaves exact equality unflagged", async () => {
    mockGetProductionReport.mockResolvedValue(PRODUCTION_ROUNDS_TO_100);
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });

    const row1 = await screen.findByRole("row", { name: /07\/19\/2026/ });
    within(row1).getByText("100.0");
    within(row1).getByLabelText(OVER_100_FLAG);
    const row2 = screen.getByRole("row", { name: /07\/18\/2026/ });
    within(row2).getByText("100.0");
    expect(within(row2).queryByLabelText(OVER_100_FLAG)).toBeNull();
    expect(screen.getAllByLabelText(OVER_100_FLAG)).toHaveLength(3);
  });

  it("shows no flag while every hen-day % is at or under 100", async () => {
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });
    await screen.findByRole("row", { name: /07\/19\/2026/ });
    expect(screen.queryByLabelText(OVER_100_FLAG)).toBeNull();
  });

  // #396 — Condition sits BESIDE Sellable, never folded into it. The fixture
  // gives day 1 sellable 90 and fromCounts 6, two values that cannot be
  // confused for one another, so a column wired to the wrong field fails.
  it("renders the Condition column beside Sellable, and keeps Sellable unchanged", async () => {
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });

    const row1 = await screen.findByRole("row", { name: /07\/19\/2026/ });
    within(row1).getByText("90"); // sellable — still the hand-graded remainder
    within(row1).getByText("6");  // condition — what the counters contributed

    const periodRow = screen.getByRole("row", { name: /Period/ });
    within(periodRow).getByText("181"); // totalSellable
    within(periodRow).getByText("6");   // totalFromCounts

    // The header must resolve through the catalog, not be hardcoded English.
    expect(screen.getByRole("columnheader", { name: "Condition" })).toBeInTheDocument();
  });

  // #1297 — the Losses tile reads the server's total and the two rate tiles
  // sit beside it; the browser never sums days itself.
  it("shows the server's losses total and the two period rates in the tiles", async () => {
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });
    const tiles = await screen.findByLabelText("Period");
    within(tiles).getByText("8"); // (2+3+5)+(1+1+2) − 6, not the raw 14
    within(tiles).getByText("95.9%"); // (181+6)/195
    within(tiles).getByText("4.1%"); // 8/195 — the two rates add up to 100
  });

  it("shows 0 in the Condition column for a day whose conditions were losses", async () => {
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });

    // Day 2 has cracked 1 / dirty 1 but fromCounts 0 — recorded as losses. The
    // column must NOT fall back to the raw counters, which is the mistake that
    // would invent stock the farm never had.
    // Columns: date, eggs, losses, sellable, CONDITION, deaths, henDays, pct.
    // Indexed: day 2 has a 0 in deaths too, so a text match proves nothing.
    const row2 = await screen.findByRole("row", { name: /07\/18\/2026/ });
    expect(within(row2).getAllByRole("cell")[4]).toHaveTextContent("0");
  });

  // #1299 — the By-flock table: one row per flock that filed an official
  // entry in the range, carrying the same fields as the period totals, with a
  // dash (never 0) for a flock with no recorded exposure (#780).
  it("renders the per-flock breakdown for every flock that filed, with a dash for a flock with no recorded exposure", async () => {
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });

    expect(await screen.findByRole("heading", { name: "By flock" })).toBeInTheDocument();
    expect(screen.getByRole("columnheader", { name: "Flock" })).toBeInTheDocument();

    const houseA = screen.getByRole("row", { name: /House A/ });
    expect(within(houseA).getAllByRole("cell")[1]).toHaveTextContent("140"); // eggs
    within(houseA).getByText("70.0");
    const houseB = screen.getByRole("row", { name: /House B/ });
    expect(within(houseB).getAllByRole("cell")[1]).toHaveTextContent("55");
    within(houseB).getByText("—"); // null henDayPct, not 0
  });

  it("omits the per-flock table when no flock filed in the range", async () => {
    mockGetProductionReport.mockResolvedValue({ ...PRODUCTION, flockTotals: [] });
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });

    await screen.findByRole("row", { name: /07\/19\/2026/ });
    expect(screen.queryByRole("heading", { name: "By flock" })).not.toBeInTheDocument();
    expect(screen.queryByRole("columnheader", { name: "Flock" })).not.toBeInTheDocument();
  });

  // #1299 round-1 review — flock names are NOT unique: two flocks can share a
  // name, so the row must carry a second human-readable detail (the placement
  // date) and each row must be addressable by it.
  it("keeps two same-named flocks apart by their placement dates", async () => {
    mockGetProductionReport.mockResolvedValue({
      ...PRODUCTION,
      flockTotals: [
        { ...PRODUCTION.flockTotals[0]!, flockName: "Layers", placedOn: "2026-05-01" },
        { ...PRODUCTION.flockTotals[1]!, flockName: "Layers", placedOn: "2026-06-20" },
      ],
    });
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });

    await screen.findByRole("heading", { name: "By flock" });
    const placedMay = screen.getByRole("row", { name: /Layers.*05\/01\/2026/ });
    const placedJune = screen.getByRole("row", { name: /Layers.*06\/20\/2026/ });
    // Same name, different rows — the eggs column tells them apart.
    expect(within(placedMay).getAllByRole("cell")[1]).toHaveTextContent("140");
    expect(within(placedJune).getAllByRole("cell")[1]).toHaveTextContent("55");
  });

  // #1299 — the over-100 flag applies per flock too: a single flock whose
  // rated eggs exceed its recorded hen-days is the same disagreement, one row
  // down, while the other flock row stays unflagged.
  it("flags a flock row whose rated eggs exceed its recorded hen-days", async () => {
    mockGetProductionReport.mockResolvedValue({
      ...PRODUCTION,
      flockTotals: [
        { ...PRODUCTION.flockTotals[0]!, ratedEggs: 201, henDayPct: 100.5 },
        PRODUCTION.flockTotals[1]!,
      ],
    });
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });

    const rowA = await screen.findByRole("row", { name: /House A/ });
    within(rowA).getByLabelText(OVER_100_FLAG);
    const rowB = screen.getByRole("row", { name: /House B/ });
    expect(within(rowB).queryByLabelText(OVER_100_FLAG)).toBeNull();
  });

  it("falls back to the em dash for a null periodHenDayPct in the Period row too, not just per-day", async () => {
    mockGetProductionReport.mockResolvedValue({ ...PRODUCTION, periodHenDayPct: null });
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });

    const periodRow = await screen.findByRole("row", { name: /Period/ });
    within(periodRow).getByText("—");
  });

  it("omits the grade-totals line when the report carries no graded totals", async () => {
    mockGetProductionReport.mockResolvedValue(PRODUCTION_NO_GRADES);
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });

    await screen.findByRole("row", { name: /07\/19\/2026/ });
    expect(screen.queryByText(/Reported grade totals/)).not.toBeInTheDocument();
  });

  it("shows the shared common:loading copy while the initial fetch is in flight, not a duplicated literal", () => {
    mockGetProductionReport.mockReturnValue(new Promise(() => {})); // never resolves in this test
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });
    // Asserted via i18n.t(), not the literal "Loading…" — proves the screen
    // reuses `common:loading` rather than a reports-local duplicate.
    expect(screen.getByText(i18n.t("common:loading"))).toBeInTheDocument();
  });

  it("shows the error message when the production report fails to load", async () => {
    mockGetProductionReport.mockRejectedValue(new Error("boom"));
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });
    expect(await screen.findByRole("alert")).toHaveTextContent("boom");
  });

  // errText's other branch: a real ApiError forwards its own message (server
  // English, out of this task's client-authored-copy scope per
  // CONTRIBUTING-i18n.md) rather than falling through to the generic
  // Error/String(err) path.
  it("shows the ApiError's own message when the production report fails with one", async () => {
    mockGetProductionReport.mockRejectedValue(new ApiError(403, "Forbidden", "not allowed here"));
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });
    expect(await screen.findByRole("alert")).toHaveTextContent("not allowed here");
  });

  // #311/PR #335 review: a browser reload resets `from`/`to` to the default
  // 7-day window (ReportsPage.tsx state init), so the help copy's promise
  // that "the dates stay in the boxes" only holds for an IN-PLACE retry, not
  // a reload. This proves that retry control exists and actually leaves
  // `from`/`to` untouched while re-issuing the same request.
  it("re-issues the production report with the SAME range when retry is clicked after a failure, and clears the error on success", async () => {
    mockGetProductionReport.mockRejectedValueOnce(new Error("boom"));
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("boom");
    const [firstFrom, firstTo] = mockGetProductionReport.mock.calls[0]!;

    mockGetProductionReport.mockResolvedValueOnce(PRODUCTION);
    fireEvent.click(within(alert).getByRole("button", { name: "retry" }));

    await waitFor(() => expect(mockGetProductionReport).toHaveBeenCalledTimes(2));
    expect(mockGetProductionReport.mock.calls[1]).toEqual([firstFrom, firstTo]);
    await screen.findByRole("row", { name: /07\/19\/2026/ });
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("re-fetches the production report for a newly picked From/To range", async () => {
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });
    await screen.findByRole("row", { name: /07\/19\/2026/ });
    mockGetProductionReport.mockClear();

    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-06-01" } });
    fireEvent.change(screen.getByLabelText("To"), { target: { value: "2026-06-30" } });

    await waitFor(() => expect(mockGetProductionReport).toHaveBeenCalledWith("2026-06-01", "2026-06-30"));
  });
});

// The Money section (Sales/Expenses/Profit) is admin-gated in TWO places:
// the render guard (`isAdmin && sales && expenses && profit`) and the fetch
// itself (`if (isAdmin)` inside load()) — both sides of this branch are
// asserted below, since a non-admin session must neither render the section
// NOR fetch the three money endpoints (the API would 403 them anyway, but
// the screen shouldn't even try).
describe("ReportsPage money section is admin-gated (#182, Task 28)", () => {
  it("renders Sales/Expenses/Profit for an Admin, with the voided-count suffix", async () => {
    renderWithProviders(<ReportsPage />, { token: ADMIN });

    expect(await screen.findByRole("heading", { name: "Money" })).toBeInTheDocument();
    expect(screen.getByText(
      "5 confirmed order(s) — revenue $100.00, paid $80.00, outstanding $20.00 (2 voided)",
    )).toBeInTheDocument();
    // #1296 — the eggs-sold row sits beside revenue: total from QuantityBase,
    // the per-grade split as the secondary DATA line (like the expenses row's).
    expect(screen.getByText("Eggs sold")).toBeInTheDocument();
    expect(screen.getByText("360")).toBeInTheDocument();
    expect(screen.getByText("Grade A 240, Grade B 120")).toBeInTheDocument();
    expect(screen.getByText("Feed $50.00, Utilities $15.00 — total $65.00")).toBeInTheDocument();
    // #1298 — the feed-cost row sits between expenses and profit: the server's
    // per-dozen figure in the report's currency, the range total as the
    // secondary DATA line (like the expenses row's).
    expect(screen.getByText("Feed cost per dozen")).toBeInTheDocument();
    expect(screen.getByText("$61.20")).toBeInTheDocument();
    expect(screen.getByText("$1,224.00 of feed recorded in the range")).toBeInTheDocument();
    expect(screen.getByText(/revenue \$100\.00 − expenses \$65\.00 =/)).toBeInTheDocument();
    expect(screen.getByText("$35.00", { selector: "strong" }).tagName).toBe("STRONG");

    expect(mockGetSalesSummary).toHaveBeenCalledWith(expect.any(String), expect.any(String));
    expect(mockGetExpenseSummary).toHaveBeenCalledWith(expect.any(String), expect.any(String));
    expect(mockGetProfitReport).toHaveBeenCalledWith(expect.any(String), expect.any(String));
  });

  it("omits the voided suffix when nothing was voided", async () => {
    mockGetSalesSummary.mockResolvedValue(SALES_NO_VOIDED);
    renderWithProviders(<ReportsPage />, { token: ADMIN });

    await screen.findByText(/confirmed order\(s\)/);
    expect(screen.queryByText(/voided\)/)).not.toBeInTheDocument();
  });

  // #1298 — a range with no eggs has no per-dozen value: a dash, never 0 or
  // infinity. The range total is still shown beneath it.
  it("shows a dash for the per-dozen figure when the range collected no eggs", async () => {
    mockGetProfitReport.mockResolvedValue({ ...PROFIT, feedCostPerDozenMinorUnits: null });
    renderWithProviders(<ReportsPage />, { token: ADMIN });

    await screen.findByText("Feed cost per dozen");
    // The dash itself, scoped to the Money section (production days render their own).
    const money = screen.getByRole("region", { name: "Money" });
    expect(within(money).getByText("—")).toBeInTheDocument();
    expect(screen.getByText("$1,224.00 of feed recorded in the range")).toBeInTheDocument();
  });

  it("shows 'none recorded' plus the (zero) total when there are no expense categories", async () => {
    mockGetExpenseSummary.mockResolvedValue(EXPENSES_EMPTY);
    renderWithProviders(<ReportsPage />, { token: ADMIN });

    expect(await screen.findByText("none recorded — total $0.00")).toBeInTheDocument();
  });

  it("hides the Money section and never fetches sales/expense/profit data for a non-admin role", async () => {
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });

    await screen.findByRole("row", { name: /07\/19\/2026/ }); // production loaded, so the mount effects settled
    expect(screen.queryByRole("heading", { name: "Money" })).not.toBeInTheDocument();
    expect(screen.queryByText(/confirmed order/)).not.toBeInTheDocument();
    expect(mockGetSalesSummary).not.toHaveBeenCalled();
    expect(mockGetExpenseSummary).not.toHaveBeenCalled();
    expect(mockGetProfitReport).not.toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 28)
// ---------------------------------------------------------------------------

// `reports` is English-only (not in TRANSLATED_NAMESPACES — see
// translations-status.ts), so under ANY UI language the rendered text falls
// back to this exact English string, same as a still-hardcoded literal would
// render — asserting plain English under default lng:"en" would prove nothing
// (CONTRIBUTING-i18n.md's fallback trap). Swap the catalog value at runtime
// instead, the same i18n.addResource technique the other batches use, so each
// marker only renders if the screen actually reads the catalog rather than a
// literal that happens to still match it.
describe("ReportsPage i18n wiring (#182, Task 28)", () => {
  function withOverride(key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", "reports", key) as string;
    i18n.addResource("en", "reports", key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", "reports", key, original);
    });
  }

  it("reads the heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("title", "TITLE-MARKER", async () => {
      renderWithProviders(<ReportsPage />, { token: NON_ADMIN });
      expect(await screen.findByRole("heading", { name: "TITLE-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Reports" })).not.toBeInTheDocument();
    });
  });

  it("reads the From filter label from the catalog, not a hardcoded literal", async () => {
    await withOverride("fromLabel", "FROM-MARKER", async () => {
      renderWithProviders(<ReportsPage />, { token: NON_ADMIN });
      expect(await screen.findByLabelText("FROM-MARKER")).toBeInTheDocument();
      expect(screen.queryByLabelText("From")).not.toBeInTheDocument();
    });
  });

  it("reads the To filter label from the catalog, not a hardcoded literal", async () => {
    await withOverride("toLabel", "TO-MARKER", async () => {
      renderWithProviders(<ReportsPage />, { token: NON_ADMIN });
      expect(await screen.findByLabelText("TO-MARKER")).toBeInTheDocument();
      expect(screen.queryByLabelText("To")).not.toBeInTheDocument();
    });
  });

  it("reads the Production heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("productionHeading", "PRODUCTION-MARKER", async () => {
      renderWithProviders(<ReportsPage />, { token: NON_ADMIN });
      expect(await screen.findByRole("heading", { name: "PRODUCTION-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Production" })).not.toBeInTheDocument();
    });
  });

  it.each([
    ["dateHeader", "Date"],
    ["eggsHeader", "Eggs"],
    ["lossesHeader", "Losses (cr/di/ds)"],
    ["sellableHeader", "Sellable"],
    ["deathsHeader", "Deaths"],
    ["henDaysHeader", "Hen-days"],
    ["henDayPctHeader", "Hen-day %"],
  ])("reads the %s production column header from the catalog, not a hardcoded literal", async (key, original) => {
    // The per-flock table (#1299) repeats eggs/sellable/deaths/hen-day, so a
    // shared header may match MORE than once — assert at least one.
    await withOverride(key, `${key.toUpperCase()}-MARKER`, async () => {
      renderWithProviders(<ReportsPage />, { token: NON_ADMIN });
      const markers = await screen.findAllByRole("columnheader", { name: `${key.toUpperCase()}-MARKER` });
      expect(markers.length).toBeGreaterThan(0);
      expect(screen.queryByRole("columnheader", { name: original })).not.toBeInTheDocument();
    });
  });

  it("reads the period row label from the catalog, not a hardcoded literal", async () => {
    await withOverride("periodRowLabel", "PERIOD-MARKER", async () => {
      renderWithProviders(<ReportsPage />, { token: NON_ADMIN });
      expect(await screen.findByText("PERIOD-MARKER")).toBeInTheDocument();
      expect(screen.queryByText("Period")).not.toBeInTheDocument();
    });
  });

  it("reads the grade-totals prefix from the catalog, not a hardcoded literal", async () => {
    await withOverride("gradeTotalsLabel", "GRADE-TOTALS-MARKER", async () => {
      renderWithProviders(<ReportsPage />, { token: NON_ADMIN });
      expect(await screen.findByRole("list", { name: "GRADE-TOTALS-MARKER" })).toHaveTextContent("Grade A60eggsGrade B30eggs");
      expect(screen.queryByText(/Reported grade totals/)).not.toBeInTheDocument();
    });
  });

  // #1299 — the per-flock table's own labels follow the same rule. The daily
  // table's losses header is the compound "Losses (cr/di/ds)", so the plain
  // "Losses" original here is uniquely the per-flock column's.
  it.each([
    ["flockHeading", "By flock"],
    ["flockCaption", "Flocks that filed in the range"],
    ["flockHeader", "Flock"],
    ["flockLossesHeader", "Losses"],
  ])("reads the %s per-flock label from the catalog, not a hardcoded literal", async (key: string, original: string) => {
    await withOverride(key, `${key.toUpperCase()}-MARKER`, async () => {
      renderWithProviders(<ReportsPage />, { token: NON_ADMIN });
      expect(await screen.findByText(`${key.toUpperCase()}-MARKER`)).toBeInTheDocument();
      expect(screen.queryByText(original)).not.toBeInTheDocument();
    });
  });

  it("reads the Money heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("moneyHeading", "MONEY-MARKER", async () => {
      renderWithProviders(<ReportsPage />, { token: ADMIN });
      expect(await screen.findByRole("heading", { name: "MONEY-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Money" })).not.toBeInTheDocument();
    });
  });

  it.each([
    ["revenueRowLabel", "Revenue"],
    ["eggsSoldRowLabel", "Eggs sold"],
    ["expensesRowLabel", "Expenses"],
    ["profitRowLabel", "Profit (basic)"],
  ])("reads the %s row label from the catalog, not a hardcoded literal", async (key, original) => {
    await withOverride(key, `${key.toUpperCase()}-MARKER`, async () => {
      renderWithProviders(<ReportsPage />, { token: ADMIN });
      expect((await screen.findAllByText(`${key.toUpperCase()}-MARKER`)).length).toBeGreaterThan(0);
      expect(screen.queryByText(original)).not.toBeInTheDocument();
    });
  });

  // Explicit brief requirement — {{count}} interpolation #1: confirmedCount,
  // alongside the three pre-formatted money DATA values in the same template.
  it("interpolates {{count}} (confirmedCount) and the formatted money figures into the sales summary from the catalog", async () => {
    await withOverride(
      "salesSummary",
      "SALES-MARKER count={{count}} rev={{revenue}} paid={{paid}} out={{outstanding}} END",
      async () => {
        renderWithProviders(<ReportsPage />, { token: ADMIN });
        // The voided suffix (a separate text node) trails this one in the
        // same <td>, so match the marker text as a substring, not exactly.
        expect(await screen.findByText(
          /SALES-MARKER count=5 rev=\$100\.00 paid=\$80\.00 out=\$20\.00 END/,
        )).toBeInTheDocument();
      },
    );
  });

  // Explicit brief requirement — {{count}} interpolation #2: a SECOND,
  // independent counter (voidedCount) on the same screen, proving the
  // interpolation isn't hardwired to `confirmedCount` alone.
  it("interpolates {{count}} (voidedCount) into the voided-orders suffix from the catalog", async () => {
    await withOverride("salesVoidedSuffix", " [[{{count}} VOIDED-MARKER]]", async () => {
      renderWithProviders(<ReportsPage />, { token: ADMIN });
      expect(await screen.findByText(/\[\[2 VOIDED-MARKER\]\]/)).toBeInTheDocument();
    });
  });

  it("reads the expenses-none copy from the catalog, not a hardcoded literal", async () => {
    mockGetExpenseSummary.mockResolvedValue(EXPENSES_EMPTY);
    await withOverride("expensesNone", "EXPENSES-NONE-MARKER", async () => {
      renderWithProviders(<ReportsPage />, { token: ADMIN });
      expect(await screen.findByText(/EXPENSES-NONE-MARKER/)).toBeInTheDocument();
      expect(screen.queryByText(/none recorded/)).not.toBeInTheDocument();
    });
  });

  it("interpolates {{total}} into the expenses total suffix from the catalog", async () => {
    await withOverride("expensesTotalSuffix", " ((TOTAL={{total}}))", async () => {
      renderWithProviders(<ReportsPage />, { token: ADMIN });
      expect(await screen.findByText(/\(\(TOTAL=\$65\.00\)\)/)).toBeInTheDocument();
    });
  });

  // Proves the profitLine TEMPLATE itself is catalog-sourced: the override
  // carries no <strong> tag, so <Trans> renders it as ONE plain text node —
  // same technique as AccountPage's roleLine wiring test.
  it("reads the profit line template from the catalog via <Trans>, not a hardcoded literal", async () => {
    await withOverride("profitLine", "PROFIT-MARKER {{revenue}}/{{expenses}}/{{profit}} END", async () => {
      renderWithProviders(<ReportsPage />, { token: ADMIN });
      expect(await screen.findByText(
        "PROFIT-MARKER $100.00/$65.00/$35.00 END",
      )).toBeInTheDocument();
      // The Sales row also contains "revenue $100.00" (same fixture
      // revenue amount) — match the profit line's DISTINCT "− expenses"
      // fragment so this only passes if the profitLine template itself (not
      // some other row) is still hardcoded.
      expect(screen.queryByText(/revenue \$100\.00 − expenses/)).not.toBeInTheDocument();
    });
  });

  // Proves the REAL catalog's <strong> tag maps through <Trans>'s components
  // prop to an actual <strong> DOM element wrapping just the profit figure —
  // a hardcoded template (or one that dropped the tag) could still render the
  // right number but never inside a real <strong>.
  it("wraps the profit figure in a real <strong> element via the <Trans> components mapping", async () => {
    renderWithProviders(<ReportsPage />, { token: ADMIN });
    expect(await screen.findByText("$35.00", { selector: "strong" })).toHaveProperty("tagName", "STRONG");
  });

  it("reads the profit footnote from the catalog, not a hardcoded literal", async () => {
    await withOverride("profitFootnote", "FOOTNOTE-MARKER", async () => {
      renderWithProviders(<ReportsPage />, { token: ADMIN });
      expect(await screen.findByText("FOOTNOTE-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/no cost-of-goods/)).not.toBeInTheDocument();
    });
  });

  // The retry button reuses common:retry (the same key/pattern as
  // DailyEntryPage's prefill-failed banner) rather than a reports-local
  // literal or duplicate key — swap the COMMON catalog, not the reports one.
  it("reads the error retry button from the common catalog, not a hardcoded literal", async () => {
    const original = i18n.getResource("en", "common", "retry") as string;
    i18n.addResource("en", "common", "retry", "RETRY-MARKER");
    try {
      mockGetProductionReport.mockRejectedValueOnce(new Error("boom"));
      renderWithProviders(<ReportsPage />, { token: NON_ADMIN });
      const alert = await screen.findByRole("alert");
      expect(within(alert).getByRole("button", { name: "RETRY-MARKER" })).toBeInTheDocument();
      expect(within(alert).queryByRole("button", { name: "retry" })).not.toBeInTheDocument();
    } finally {
      i18n.addResource("en", "common", "retry", original);
    }
  });
});


it("keeps period KPIs in one phone strip and aligns primary Money amounts", async () => {
  renderWithProviders(<ReportsPage />, { token: ADMIN });
  const money = await screen.findByRole("region", { name: "Money" });
  const strip = screen.getByLabelText("Period");
  expect(responsiveStyle(strip, "(min-width:0px)", "grid-auto-flow")).toBe("column");
  for (const [label, amount] of [["Revenue", "$100.00"], ["Expenses", "$65.00"], ["Profit (basic)", "$35.00"]]) {
    const row = within(money).getByText(label, { selector: "dt" }).parentElement!;
    expect(within(row).getByText(amount, { selector: "dd" })).toHaveStyle({ textAlign: "right" });
  }
});

it("restores the default report window and labels the auditable detail and grade units", async () => {
  renderWithProviders(<ReportsPage />, { token: ADMIN });
  await screen.findByRole("region", { name: "Money" });
  const from = screen.getByLabelText("From");
  const to = screen.getByLabelText("To");
  const initialFrom = from.getAttribute("value");
  const initialTo = to.getAttribute("value");
  fireEvent.change(from, { target: { value: "2026-01-01" } });
  fireEvent.change(to, { target: { value: "2026-01-02" } });
  fireEvent.click(screen.getByRole("button", { name: "Clear filters" }));
  expect(from).toHaveValue(initialFrom);
  expect(to).toHaveValue(initialTo);
  await screen.findByRole("heading", { name: "Daily production" });
  expect(screen.getByText("Raw auditable detail")).toBeInTheDocument();
  expect(within(screen.getByRole("list", { name: "Reported grade totals" })).getAllByText("eggs")).toHaveLength(2);
});


it("emphasises every Period total cell in ink at semibold weight", async () => {
  renderWithProviders(<ReportsPage />, { token: ADMIN });
  const row = await screen.findByRole("row", { name: /Period/ });
  const cells = within(row).getAllByRole("columnheader");
  expect(cells).toHaveLength(10);
  for (const cell of cells) {
    expect(cell).toHaveStyle({ color: "var(--ink)", fontWeight: "600" });
  }
});
