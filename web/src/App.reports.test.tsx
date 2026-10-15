import type { ReactNode } from "react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { App } from "./App";
import {
  getExpenseSummary, getProductionReport, getProfitReport, getSalesSummary,
} from "./api/cluckwork";
import type {
  ExpenseSummaryReport, ProductionReport, ProfitReport, SalesSummary,
} from "./api/cluckwork";
import { clearAccessToken } from "./auth/tokenStore";
import { setStoredToken } from "./test/jwt";

vi.mock("./session/SessionContext", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./session/SessionContext")>();
  return {
    ...actual,
    SessionProvider: ({ children }: { children: ReactNode }) => children,
  };
});

vi.mock("./pwa/UpdatePrompt", () => ({ UpdatePrompt: () => null }));

vi.mock("./api/cluckwork", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./api/cluckwork")>();
  return {
    ...actual,
    getProductionReport: vi.fn(),
    getSalesSummary: vi.fn(),
    getExpenseSummary: vi.fn(),
    getProfitReport: vi.fn(),
  };
});

const production: ProductionReport = {
  days: [{
    date: "2026-07-19", totalEggs: 100, cracked: 0, dirty: 0, discarded: 0,
    sellable: 100, fromCounts: 0, deaths: 0, recordedFlocks: 1,
    expectedFlocks: 1, missingFlocks: 0, henDays: 110, recordedHenDays: 110,
    ratedEggs: 100, henDayPct: 90.9,
  }],
  totalEggs: 100,
  totalSellable: 100,
  totalFromCounts: 0,
  totalLosses: 0,
  totalDeaths: 0,
  totalHenDays: 110,
  totalRecordedHenDays: 110,
  totalRatedEggs: 100,
  periodHenDayPct: 90.9,
  periodSellablePct: 100,
  periodLossPct: 0,
  gradeTotals: [],
  flockTotals: [],
};

const sales: SalesSummary = {
  confirmedCount: 1,
  revenueMinorUnits: 1000,
  paidMinorUnits: 1000,
  outstandingMinorUnits: 0,
  voidedCount: 0,
  eggsSold: 12,
  eggsSoldByGrade: [],
  currencyCode: "USD",
  currencyMinorUnit: 2,
};

const expenses: ExpenseSummaryReport = {
  categories: [],
  grandTotalMinorUnits: 0,
  currencyCode: "USD",
  currencyMinorUnit: 2,
};

const profit: ProfitReport = {
  revenueMinorUnits: 1000,
  expensesMinorUnits: 0,
  profitMinorUnits: 1000,
  feedCostMinorUnits: 0,
  feedCostPerDozenMinorUnits: 0,
  currencyCode: "USD",
  currencyMinorUnit: 2,
};

const mockGetProductionReport = vi.mocked(getProductionReport);
const mockGetSalesSummary = vi.mocked(getSalesSummary);
const mockGetExpenseSummary = vi.mocked(getExpenseSummary);
const mockGetProfitReport = vi.mocked(getProfitReport);

beforeEach(() => {
  vi.clearAllMocks();
  setStoredToken({ sub: "u1", role: "Admin" });
  mockGetProductionReport.mockResolvedValue(production);
  mockGetSalesSummary.mockResolvedValue(sales);
  mockGetExpenseSummary.mockResolvedValue(expenses);
  mockGetProfitReport.mockResolvedValue(profit);
});

afterEach(() => {
  clearAccessToken();
  window.history.replaceState(null, "", "/");
});

it("keeps the range and Reports navigation state through the real /reports route", async () => {
  window.history.replaceState(null, "", "/reports?from=2026-06-01&to=2026-06-30");
  render(<App />);

  await screen.findByRole("row", { name: /07\/19\/2026/ });
  expect(window.location.pathname).toBe("/reports/production");
  expect(window.location.search).toBe("?from=2026-06-01&to=2026-06-30");
  expect(mockGetProductionReport).toHaveBeenCalledWith(
    "2026-06-01", "2026-06-30", undefined, expect.any(AbortSignal),
  );
  expect(screen.getAllByRole("link", { name: "Reports" })
    .some((link) => link.getAttribute("aria-current") === "page")).toBe(true);

  fireEvent.click(screen.getByRole("tab", { name: "Money" }));
  await screen.findByRole("region", { name: "Money" });

  expect(window.location.pathname).toBe("/reports/money");
  expect(window.location.search).toBe("?from=2026-06-01&to=2026-06-30");
  expect(screen.getAllByRole("link", { name: "Reports" })
    .some((link) => link.getAttribute("aria-current") === "page")).toBe(true);
});
