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
const PRODUCTION: ProductionReport = {
  days: [
    { date: "2026-07-19", totalEggs: 100, cracked: 2, dirty: 3, discarded: 5, sellable: 90, fromCounts: 6, deaths: 1, henDays: 98, henDayPct: 91.8 },
    { date: "2026-07-18", totalEggs: 95, cracked: 1, dirty: 1, discarded: 2, sellable: 91, fromCounts: 0, deaths: 0, henDays: 98, henDayPct: null },
  ],
  totalEggs: 195, totalSellable: 181, totalFromCounts: 6, totalDeaths: 1, totalHenDays: 196, periodHenDayPct: 92.3,
  gradeTotals: [
    { eggGradeId: "gr1", name: "Grade A", quantity: 60 },
    { eggGradeId: "gr2", name: "Grade B", quantity: 30 },
  ],
};
const PRODUCTION_NO_GRADES: ProductionReport = { ...PRODUCTION, gradeTotals: [] };

const SALES: SalesSummary = {
  confirmedCount: 5, revenueMinorUnits: 10000, paidMinorUnits: 8000, outstandingMinorUnits: 2000,
  voidedCount: 2, currencyCode: "USD", currencyMinorUnit: 2,
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
  revenueMinorUnits: 10000, expensesMinorUnits: 6500, profitMinorUnits: 3500, currencyCode: "USD", currencyMinorUnit: 2,
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

    const row1 = await screen.findByRole("row", { name: /2026-07-19/ });
    within(row1).getByText("100"); // totalEggs
    within(row1).getByText("2/3/5"); // cracked/dirty/discarded
    within(row1).getByText("90"); // sellable
    within(row1).getByText("98"); // henDays
    within(row1).getByText("91.8"); // henDayPct

    const row2 = screen.getByRole("row", { name: /2026-07-18/ });
    within(row2).getByText("—"); // null henDayPct falls back to the em dash

    const periodRow = screen.getByRole("row", { name: /Period/ });
    within(periodRow).getByText("195"); // totalEggs
    within(periodRow).getByText("181"); // totalSellable
    within(periodRow).getByText("196"); // totalHenDays
    within(periodRow).getByText("92.3"); // periodHenDayPct

    expect(screen.getByText("By grade: Grade A 60, Grade B 30")).toBeInTheDocument();
  });

  // #396 — Condition sits BESIDE Sellable, never folded into it. The fixture
  // gives day 1 sellable 90 and fromCounts 6, two values that cannot be
  // confused for one another, so a column wired to the wrong field fails.
  it("renders the Condition column beside Sellable, and keeps Sellable unchanged", async () => {
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });

    const row1 = await screen.findByRole("row", { name: /2026-07-19/ });
    within(row1).getByText("90"); // sellable — still the hand-graded remainder
    within(row1).getByText("6");  // condition — what the counters contributed

    const periodRow = screen.getByRole("row", { name: /Period/ });
    within(periodRow).getByText("181"); // totalSellable
    within(periodRow).getByText("6");   // totalFromCounts

    // The header must resolve through the catalog, not be hardcoded English.
    expect(screen.getByRole("columnheader", { name: "Condition" })).toBeInTheDocument();
  });

  it("shows 0 in the Condition column for a day whose conditions were losses", async () => {
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });

    // Day 2 has cracked 1 / dirty 1 but fromCounts 0 — recorded as losses. The
    // column must NOT fall back to the raw counters, which is the mistake that
    // would invent stock the farm never had.
    // Columns: date, eggs, losses, sellable, CONDITION, deaths, henDays, pct.
    // Indexed: day 2 has a 0 in deaths too, so a text match proves nothing.
    const row2 = await screen.findByRole("row", { name: /2026-07-18/ });
    expect(within(row2).getAllByRole("cell")[4]).toHaveTextContent("0");
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

    await screen.findByRole("row", { name: /2026-07-19/ });
    expect(screen.queryByText(/By grade:/)).not.toBeInTheDocument();
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
    await screen.findByRole("row", { name: /2026-07-19/ });
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("re-fetches the production report for a newly picked From/To range", async () => {
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });
    await screen.findByRole("row", { name: /2026-07-19/ });
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
      "5 confirmed order(s) — revenue 100.00 USD, paid 80.00 USD, outstanding 20.00 USD (2 voided)",
    )).toBeInTheDocument();
    expect(screen.getByText("Feed 50.00 USD, Utilities 15.00 USD — total 65.00 USD")).toBeInTheDocument();
    expect(screen.getByText(/revenue 100\.00 USD − expenses 65\.00 USD =/)).toBeInTheDocument();
    expect(screen.getByText("35.00 USD").tagName).toBe("STRONG");

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

  it("shows 'none recorded' plus the (zero) total when there are no expense categories", async () => {
    mockGetExpenseSummary.mockResolvedValue(EXPENSES_EMPTY);
    renderWithProviders(<ReportsPage />, { token: ADMIN });

    expect(await screen.findByText("none recorded — total 0.00 USD")).toBeInTheDocument();
  });

  it("hides the Money section and never fetches sales/expense/profit data for a non-admin role", async () => {
    renderWithProviders(<ReportsPage />, { token: NON_ADMIN });

    await screen.findByRole("row", { name: /2026-07-19/ }); // production loaded, so the mount effects settled
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
    await withOverride(key, `${key.toUpperCase()}-MARKER`, async () => {
      renderWithProviders(<ReportsPage />, { token: NON_ADMIN });
      expect(await screen.findByRole("columnheader", { name: `${key.toUpperCase()}-MARKER` })).toBeInTheDocument();
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
      expect(await screen.findByText("GRADE-TOTALS-MARKER Grade A 60, Grade B 30")).toBeInTheDocument();
      expect(screen.queryByText(/By grade:/)).not.toBeInTheDocument();
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
    ["salesRowLabel", "Sales"],
    ["expensesRowLabel", "Expenses"],
    ["profitRowLabel", "Profit (basic)"],
  ])("reads the %s row label from the catalog, not a hardcoded literal", async (key, original) => {
    await withOverride(key, `${key.toUpperCase()}-MARKER`, async () => {
      renderWithProviders(<ReportsPage />, { token: ADMIN });
      expect(await screen.findByText(`${key.toUpperCase()}-MARKER`)).toBeInTheDocument();
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
          /SALES-MARKER count=5 rev=100\.00 USD paid=80\.00 USD out=20\.00 USD END/,
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
      expect(await screen.findByText(/\(\(TOTAL=65\.00 USD\)\)/)).toBeInTheDocument();
    });
  });

  // Proves the profitLine TEMPLATE itself is catalog-sourced: the override
  // carries no <strong> tag, so <Trans> renders it as ONE plain text node —
  // same technique as AccountPage's roleLine wiring test.
  it("reads the profit line template from the catalog via <Trans>, not a hardcoded literal", async () => {
    await withOverride("profitLine", "PROFIT-MARKER {{revenue}}/{{expenses}}/{{profit}} END", async () => {
      renderWithProviders(<ReportsPage />, { token: ADMIN });
      expect(await screen.findByText(
        "PROFIT-MARKER 100.00 USD/65.00 USD/35.00 USD END",
      )).toBeInTheDocument();
      // The Sales row also contains "revenue 100.00 USD" (same fixture
      // revenue amount) — match the profit line's DISTINCT "− expenses"
      // fragment so this only passes if the profitLine template itself (not
      // some other row) is still hardcoded.
      expect(screen.queryByText(/revenue 100\.00 USD − expenses/)).not.toBeInTheDocument();
    });
  });

  // Proves the REAL catalog's <strong> tag maps through <Trans>'s components
  // prop to an actual <strong> DOM element wrapping just the profit figure —
  // a hardcoded template (or one that dropped the tag) could still render the
  // right number but never inside a real <strong>.
  it("wraps the profit figure in a real <strong> element via the <Trans> components mapping", async () => {
    renderWithProviders(<ReportsPage />, { token: ADMIN });
    expect(await screen.findByText("35.00 USD")).toHaveProperty("tagName", "STRONG");
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
