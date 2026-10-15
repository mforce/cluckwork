import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { beforeEach, expect, it, vi } from "vitest";
import { Route, Routes } from "react-router";
import { ReportsPage } from "../ReportsPage";
import { renderWithProviders } from "../../test/renderWithProviders";
import { getExpenseSummary, getProductionReport, getProfitReport, getSalesSummary } from "../../api/cluckwork";
import type { ExpenseSummaryReport, ProductionReport, ProfitReport, SalesSummary } from "../../api/cluckwork";
import { periodChange, previousPeriod } from "./periodComparison";

vi.mock("../../api/cluckwork", async (importOriginal) => ({
  ...await importOriginal<typeof import("../../api/cluckwork")>(),
  getProductionReport: vi.fn(), getSalesSummary: vi.fn(),
  getExpenseSummary: vi.fn(), getProfitReport: vi.fn(),
}));

const production: ProductionReport = {
  days: [], gradeTotals: [], flockTotals: [], totalEggs: 120, totalSellable: 108,
  totalFromCounts: 0, totalLosses: 12, totalDeaths: 0, totalHenDays: 150,
  totalRecordedHenDays: 150, totalRatedEggs: 120,
  periodHenDayPct: 80, periodSellablePct: 90, periodLossPct: 10,
};
const priorProduction: ProductionReport = {
  ...production, totalEggs: 100, totalSellable: 80, totalLosses: 20,
  periodHenDayPct: 78.8, periodSellablePct: 80, periodLossPct: 20,
};
const sales: SalesSummary = {
  confirmedCount: 1, voidedCount: 0, revenueMinorUnits: 12000,
  paidMinorUnits: 12000, outstandingMinorUnits: 0, eggsSold: 120,
  eggsSoldByGrade: [], currencyCode: "USD", currencyMinorUnit: 2,
};
const expenses: ExpenseSummaryReport = {
  categories: [], grandTotalMinorUnits: 6000, currencyCode: "USD", currencyMinorUnit: 2,
};
const profit: ProfitReport = {
  revenueMinorUnits: 12000, expensesMinorUnits: 6000, profitMinorUnits: 6000,
  feedCostMinorUnits: 1200, feedCostPerDozenMinorUnits: 120,
  currencyCode: "USD", currencyMinorUnit: 2,
};
const getProduction = vi.mocked(getProductionReport);
const getSales = vi.mocked(getSalesSummary);
const getExpenses = vi.mocked(getExpenseSummary);
const getProfit = vi.mocked(getProfitReport);
const FROM = "2026-06-01";
const TO = "2026-06-30";

function mount(tab = "production") {
  return renderWithProviders(<Routes><Route path="reports/:tab" element={<ReportsPage />} /></Routes>, {
    route: `/reports/${tab}?from=${FROM}&to=${TO}`, token: { sub: "u1", role: "Admin" },
  });
}

beforeEach(() => {
  vi.resetAllMocks();
  getProduction.mockImplementation(async (from) => from === FROM ? production : priorProduction);
  getSales.mockImplementation(async (from) => from === FROM ? sales : { ...sales, revenueMinorUnits: 10000, eggsSold: 100 });
  getExpenses.mockImplementation(async (from) => from === FROM ? expenses : { ...expenses, grandTotalMinorUnits: 4000 });
  getProfit.mockImplementation(async (from) => from === FROM ? profit : { ...profit, profitMinorUnits: -3000, feedCostPerDozenMinorUnits: 100 });
});

it.each([
  [FROM, TO, "2026-05-02", "2026-05-31", 30],
  ["2024-03-01", "2024-03-31", "2024-01-30", "2024-02-29", 31],
  ["2026-01-01", "2026-01-01", "2025-12-31", "2025-12-31", 1],
])("compares %s through %s with exactly the preceding inclusive range", (from, to, start, end, days) => {
  expect(previousPeriod(from, to)).toEqual({ from: start, to: end, days });
});

it("omits an unrepresentable previous period", () => {
  expect(previousPeriod("0001-01-01", "0001-01-31")).toBeNull();
});

it("shows percent for counts and percentage points for every rate, on headline tiles only", async () => {
  mount();
  const tiles = await screen.findByLabelText("Period");
  await within(tiles).findByText("+1.2 pp vs previous 30 days");
  for (const [label, text] of [
    ["Eggs", "+20.0% vs previous 30 days"],
    ["Sellable", "+35.0% vs previous 30 days"],
    ["Losses (cr/di/ds)", "-40.0% vs previous 30 days"],
    ["Sellable %", "+10.0 pp vs previous 30 days"],
    ["Loss %", "-10.0 pp vs previous 30 days"],
  ]) {
    expect(within(tiles).getByText(label).parentElement).toHaveTextContent(text);
  }
  expect(screen.getAllByText(/vs previous/)).toHaveLength(6);
  expect(getProduction).toHaveBeenNthCalledWith(2, "2026-05-02", "2026-05-31", undefined, expect.any(AbortSignal));
  expect(getSales).not.toHaveBeenCalled();
  expect(getExpenses).not.toHaveBeenCalled();
  expect(getProfit).not.toHaveBeenCalled();
});

it("shows percent changes for every Money headline, including profit from a negative base", async () => {
  mount("money");
  const money = await screen.findByRole("region", { name: "Money" });
  await within(money).findByText("+300.0% vs previous 30 days");
  for (const [label, text] of [
    ["Revenue", "+20.0%"], ["Eggs sold", "+20.0%"], ["Expenses", "+50.0%"],
    ["Feed cost per dozen", "+20.0%"], ["Profit (basic)", "+300.0%"],
  ]) {
    expect(within(money).getByText(label).parentElement).toHaveTextContent(`${text} vs previous 30 days`);
  }
  expect(getProduction).not.toHaveBeenCalled();
  for (const read of [getSales, getExpenses, getProfit]) {
    expect(read).toHaveBeenNthCalledWith(2, "2026-05-02", "2026-05-31", expect.any(AbortSignal));
  }
});

it.each([null, undefined, 0])("shows no change for a missing or zero previous value (%s)", (previous) => {
  expect(periodChange(120, previous)).toBeNull();
  expect(periodChange(80, previous, true)).toBeNull();
});

it("shows no comparison for an empty previous production period", async () => {
  getProduction.mockResolvedValueOnce(production).mockResolvedValueOnce({
    ...priorProduction, days: [], totalEggs: 0, totalSellable: 0, totalLosses: 0,
    periodHenDayPct: null, periodSellablePct: null, periodLossPct: null,
  });
  mount();
  await waitFor(() => expect(getProduction).toHaveBeenCalledTimes(2));
  await act(async () => {});
  expect(await screen.findByLabelText("Period")).toHaveTextContent("120");
  expect(screen.queryByText(/vs previous/)).not.toBeInTheDocument();
});

it.each(["production", "money"])("keeps current %s figures when a previous read fails", async (tab) => {
  if (tab === "production") getProduction.mockResolvedValueOnce(production).mockRejectedValueOnce(new Error("previous failed"));
  else getSales.mockResolvedValueOnce(sales).mockRejectedValueOnce(new Error("previous failed"));
  mount(tab);
  await waitFor(() => expect(tab === "production" ? getProduction : getSales).toHaveBeenCalledTimes(2));
  await act(async () => {});
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  expect(screen.queryByText(/vs previous/)).not.toBeInTheDocument();
  expect(tab === "production" ? screen.getByLabelText("Period") : screen.getByRole("region", { name: "Money" }))
    .toHaveTextContent(tab === "production" ? "120" : "$120.00");
});

it.each(["production", "money"])("aborts previous %s reads on range changes and tab switches", async (tab) => {
  const read = tab === "production" ? getProduction : getSales;
  if (tab === "production") getProduction.mockResolvedValueOnce(production).mockReturnValueOnce(new Promise(() => {}));
  else getSales.mockResolvedValueOnce(sales).mockReturnValueOnce(new Promise(() => {}));
  mount(tab);
  await waitFor(() => expect(read).toHaveBeenCalledTimes(2));
  const previousSignal = tab === "production" ? getProduction.mock.calls[1]?.[3] : getSales.mock.calls[1]?.[2];
  expect(previousSignal).toBeInstanceOf(AbortSignal);
  expect(previousSignal?.aborted).toBe(false);
  fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-06-02" } });
  await waitFor(() => expect(previousSignal?.aborted).toBe(true));
  await waitFor(() => expect(read).toHaveBeenCalledTimes(4));
  const nextSignal = tab === "production" ? getProduction.mock.calls[3]?.[3] : getSales.mock.calls[3]?.[2];
  expect(nextSignal).toBeInstanceOf(AbortSignal);
  fireEvent.click(screen.getByRole("tab", { name: tab === "production" ? "Money" : "Production" }));
  await waitFor(() => expect(nextSignal?.aborted).toBe(true));
});

it("keeps Money reads sequential and stops the previous chain after unmount", async () => {
  let resolvePrevious!: (value: SalesSummary) => void;
  getSales.mockResolvedValueOnce(sales).mockReturnValueOnce(new Promise((resolve) => { resolvePrevious = resolve; }));
  mount("money");
  await waitFor(() => expect(getSales).toHaveBeenCalledTimes(2));
  expect(getExpenses).toHaveBeenCalledTimes(1);
  expect(getProfit).toHaveBeenCalledTimes(1);
  fireEvent.click(screen.getByRole("tab", { name: "Production" }));
  await act(async () => resolvePrevious(sales));
  expect(getExpenses).toHaveBeenCalledTimes(1);
  expect(getProfit).toHaveBeenCalledTimes(1);
});
