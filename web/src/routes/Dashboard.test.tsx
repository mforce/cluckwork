// web/src/routes/Dashboard.test.tsx
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { screen, within } from "@testing-library/react";
import { Dashboard } from "./Dashboard";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  getProductionReport, getStock, listDailyEntries, listFlocks, listOrders,
} from "../api/cluckwork";
import type { DailyEntry, Flock, ProductionDay, ProductionReport, SalesOrder, StockRow } from "../api/cluckwork";
import { daysBefore, todayIso } from "../lib/dates";
import i18n from "../i18n";
import { NO_RECORD_HISTORY, account } from "../test/fixtures";

// Keep the real formatters; stub the six read endpoints the dashboard fans out.
vi.mock("../api/cluckwork", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/cluckwork")>();
  return {
    ...actual,
    listFlocks: vi.fn(),
    listDailyEntries: vi.fn(),
    getStock: vi.fn(),
    listOrders: vi.fn(),
    getProductionReport: vi.fn(),
  };
});

const mockFlocks = vi.mocked(listFlocks);
const mockEntries = vi.mocked(listDailyEntries);
const mockStock = vi.mocked(getStock);
const mockOrders = vi.mocked(listOrders);
const mockReport = vi.mocked(getProductionReport);

const flock = (id: string, status: string): Flock => ({
  ...NO_RECORD_HISTORY,
  id, farmId: "f", houseId: "h", name: `Flock ${id}`, breed: "ISA",
  placementDate: "2026-01-01", initialCount: 100, currentBirds: 98, status,
});
const entry = (flockId: string, status: string, totalEggs: number): DailyEntry => ({
  ...NO_RECORD_HISTORY,
  id: `de-${flockId}`, farmId: "f", houseId: "h", flockId, date: "2026-07-21", status,
  totalEggs, crackedEggs: 0, dirtyEggs: 0, discardedEggs: 0, mortalityCount: 0,
  crackedGradeId: null, dirtyGradeId: null, grades: [],
  version: 1, adjustReason: null, voidReason: null, lockedAtUtc: null, adjustedFrom: null,
});
const day = (date: string, totalEggs: number): ProductionDay => ({
  date, totalEggs, cracked: 0, dirty: 0, discarded: 0, sellable: totalEggs, fromCounts: 0,
  deaths: 0, henDays: 100, henDayPct: totalEggs,
});
const report = (periodHenDayPct: number | null, days: ProductionDay[]): ProductionReport => ({
  days, totalEggs: 0, totalSellable: 0, totalFromCounts: 0, totalDeaths: 0, totalHenDays: 0,
  periodHenDayPct, gradeTotals: [],
});
const STOCK: StockRow[] = [
  { eggGradeId: "g1", gradeName: "Grade A", available: 1240, restricted: 0 },
  { eggGradeId: "g2", gradeName: "Grade B", available: 320, restricted: 0 },
];
const order = (id: string, ref: string, customerName: string | null): SalesOrder => ({
  ...NO_RECORD_HISTORY, id, customerId: "c1", customerName, referenceNumber: ref,
  orderDate: "2026-07-21", status: "Draft", totalMinorUnits: 1000, currencyCode: "USD",
  currencyMinorUnit: 2, voidReason: null, items: [],
});

// The report mock answers by window (relative to a given "today") so the two
// calls can be told apart. Values: previous week 307..301, current week 327..321.
const previousFor = (today: string) => report(85.1, [7, 6, 5, 4, 3, 2, 1].map((n) => day(daysBefore(today, n + 7), 300 + n)));
const currentFor = (today: string) => report(87.4, [7, 6, 5, 4, 3, 2, 1].map((n) => day(daysBefore(today, n), 320 + n)));
const reportByWindow = (today: string) => (from: string, to: string) => {
  if (from === daysBefore(today, 7) && to === daysBefore(today, 1)) return Promise.resolve(currentFor(today));
  if (from === daysBefore(today, 14) && to === daysBefore(today, 8)) return Promise.resolve(previousFor(today));
  return Promise.reject(new Error(`unexpected window ${from}..${to}`));
};

// Outside a FarmProvider useFarmToday() computes today browser-local — the
// same value the screen uses, so the report-call oracle below is exact. The
// farm-scoped test at the bottom is the one that proves the FARM's day wins.
const today = todayIso();

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  mockFlocks.mockResolvedValue([flock("f1", "Active"), flock("f2", "Active"), flock("f3", "Active")]);
  mockEntries.mockResolvedValue([entry("f1", "Submitted", 178), entry("f2", "Voided", 999)]);
  mockStock.mockResolvedValue(STOCK);
  mockOrders.mockResolvedValue([] as SalesOrder[]);
  mockReport.mockImplementation(reportByWindow(today));
});

// The page renders "Loading…" until every read settles, so a panel is found, never got.
const panel = async (title: string) =>
  (await screen.findByRole("heading", { name: title, level: 3 })).closest(".panel") as HTMLElement;

describe("Dashboard capture status (#654)", () => {
  it("renders one tile per active flock, no-entry tiles first, each linking to that flock's entry for today", async () => {
    renderWithProviders(<Dashboard />);
    const f1 = await screen.findByRole("link", { name: "Flock f1: open today's entry" });
    expect(f1).toHaveAttribute("href", `/daily-entry?flockId=f1&date=${today}`);
    expect(within(f1).getByText("178")).toBeInTheDocument();
    expect(within(f1).getByText("Submitted")).toBeInTheDocument();
    const names = screen.getAllByRole("link", { name: /open today's entry/ }).map((a) => a.getAttribute("aria-label"));
    expect(names).toEqual(["Flock f2: open today's entry", "Flock f3: open today's entry", "Flock f1: open today's entry"]);
  });

  it("marks a flock with no entry — and one whose only entry is Voided — as missing (#82)", async () => {
    renderWithProviders(<Dashboard />);
    const f2 = await screen.findByRole("link", { name: "Flock f2: open today's entry" });
    const f3 = screen.getByRole("link", { name: "Flock f3: open today's entry" });
    for (const tile of [f2, f3]) {
      expect(tile).toHaveClass("is-missing");
      expect(within(tile).getByText("no entry")).toBeInTheDocument();
      expect(within(tile).getByText("—")).toBeInTheDocument();
      expect(within(tile).queryByText("999")).not.toBeInTheDocument();
    }
    expect(screen.getByRole("link", { name: "Flock f1: open today's entry" })).not.toHaveClass("is-missing");
  });

  it("sums today's eggs excluding the Voided entry — 178, never 1,177", async () => {
    renderWithProviders(<Dashboard />);
    expect(await screen.findByText("178 eggs today")).toBeInTheDocument();
    expect(screen.queryByText(/1,177/)).not.toBeInTheDocument();
  });

  it("caps the grid at 12 tiles, the missing ones first, and links the rest (INV-9)", async () => {
    mockFlocks.mockResolvedValue(Array.from({ length: 15 }, (_, i) => flock(`f${i}`, "Active")));
    mockEntries.mockResolvedValue(Array.from({ length: 12 }, (_, i) => entry(`f${i}`, "Submitted", 1))); // f12..f14 missing
    renderWithProviders(<Dashboard />);
    const more = await screen.findByRole("link", { name: "3 more flocks" });
    expect(more).toHaveAttribute("href", "/daily-entry");
    const tiles = screen.getAllByRole("link", { name: /open today's entry/ });
    expect(tiles).toHaveLength(12);
    expect(tiles.slice(0, 3).map((t) => t.getAttribute("aria-label")))
      .toEqual(["Flock f12: open today's entry", "Flock f13: open today's entry", "Flock f14: open today's entry"]);
    expect(tiles.slice(0, 3).every((t) => t.classList.contains("is-missing"))).toBe(true);
  });
});

describe("Dashboard last 14 days (#654, INV-5)", () => {
  it("asks the production report for exactly the two 7-day windows ending yesterday", async () => {
    renderWithProviders(<Dashboard />);
    await screen.findByText("178 eggs today");
    expect(mockReport).toHaveBeenCalledTimes(2);
    expect(mockReport).toHaveBeenCalledWith(daysBefore(today, 7), daysBefore(today, 1));
    expect(mockReport).toHaveBeenCalledWith(daysBefore(today, 14), daysBefore(today, 8));
  });

  it("draws the 14 report days oldest-first as the exact polyline and captions the server's hen-day figures", async () => {
    renderWithProviders(<Dashboard />);
    const svg = await screen.findByRole("img", { name: "Eggs per day, last 14 days: lowest 301, highest 327, yesterday 321" });
    expect(svg.querySelector("polyline")).toHaveAttribute(
      "points",
      "0,2 7.7,2.1 15.4,2.2 23.1,2.3 30.8,2.3 38.5,2.4 46.2,2.5 53.8,0 61.5,0.1 69.2,0.2 76.9,0.3 84.6,0.4 92.3,0.5 100,0.6",
    );
    expect(screen.getByText("Hen-day 87.4% · +2.3 pts vs the previous 7 days")).toBeInTheDocument();
  });

  it("shows a negative delta with the minus form, one decimal on both figures", async () => {
    mockReport.mockImplementation((from, to) =>
      reportByWindow(today)(from, to).then((r) => (r.periodHenDayPct === 87.4 ? report(80, r.days) : r)));
    renderWithProviders(<Dashboard />);
    expect(await screen.findByText("Hen-day 80.0% · −5.1 pts vs the previous 7 days")).toBeInTheDocument();
  });

  it("renders — for a null hen-day figure, never 0", async () => {
    mockReport.mockImplementation((from, to) =>
      reportByWindow(today)(from, to).then((r) => report(null, r.days)));
    renderWithProviders(<Dashboard />);
    expect(await screen.findByText("Hen-day — · — vs the previous 7 days")).toBeInTheDocument();
  });
});

describe("Dashboard stock bar (#654, INV-4)", () => {
  it("renders the grade segments with exact widths and a caption equal to the Stock screen's total", async () => {
    renderWithProviders(<Dashboard />);
    const stock = await panel("Stock");
    await within(stock).findByText(/1,560 eggs available\./);
    const spans = Array.from(stock.querySelectorAll(".meter-stack > span")) as HTMLElement[];
    expect(spans.map((s) => s.style.width)).toEqual(["79.5%", "20.5%"]);
    expect(within(stock).getByText(/Grade A 1,240 · Grade B 320/)).toBeInTheDocument();
    expect(within(stock).queryByText(/restricted/)).not.toBeInTheDocument();
  });

  it("appends the restricted suffix only when something is restricted", async () => {
    mockStock.mockResolvedValue([{ ...STOCK[0], restricted: 12 }, STOCK[1]]);
    renderWithProviders(<Dashboard />);
    expect(await screen.findByText(/· 12 restricted$/)).toBeInTheDocument();
  });

  it("says '1 egg available.' — singular — when exactly one egg is in stock", async () => {
    mockStock.mockResolvedValue([{ eggGradeId: "g1", gradeName: "Grade A", available: 1, restricted: 0 }]);
    renderWithProviders(<Dashboard />);
    const stock = await panel("Stock");
    expect(within(stock).getByText(/^1 egg available\. · Grade A 1$/)).toBeInTheDocument();
    expect(within(stock).queryByText(/1 eggs available/)).not.toBeInTheDocument();
  });

  it("still renders a restricted-only stock (0 available, 4 restricted) — that is not the empty state", async () => {
    mockStock.mockResolvedValue([{ eggGradeId: "g1", gradeName: "Grade A", available: 0, restricted: 4 }]);
    renderWithProviders(<Dashboard />);
    const stock = await panel("Stock");
    expect(await within(stock).findByText(/^0 eggs available\. · 4 restricted$/)).toBeInTheDocument();
    expect(stock.querySelectorAll(".meter-stack > span")).toHaveLength(0);
    expect(within(stock).queryByText("No stock yet — record and submit a daily entry.")).not.toBeInTheDocument();
  });
});

// One rejection at a time: the failed panel shows the panel error, every other
// panel still shows its real figures, and no zero appears where the failed
// figure would be (INV-1).
describe("Dashboard degrades one panel at a time (#654, INV-1)", () => {
  const boom = () => Promise.reject(new Error("down"));
  const asSales = { token: { sub: "u1", role: "Sales" } };
  const expectOthersIntact = async (except: "today" | "trend" | "stock" | "sales") => {
    if (except !== "today") expect(await screen.findByText("178 eggs today")).toBeInTheDocument();
    if (except !== "trend") expect(await screen.findByText(/Hen-day 87\.4%/)).toBeInTheDocument();
    if (except !== "stock") expect(await screen.findByText(/1,560 eggs available\./)).toBeInTheDocument();
    if (except !== "sales") expect(await screen.findByText("No orders yet.")).toBeInTheDocument();
  };

  it("flocks failed → Today panel errors, others intact", async () => {
    mockFlocks.mockImplementation(boom);
    renderWithProviders(<Dashboard />, asSales);
    expect(within(await panel("Today")).getByText("Could not load.")).toBeInTheDocument();
    expect(screen.queryByText(/eggs today/)).not.toBeInTheDocument();
    await expectOthersIntact("today");
  });
  it("entries failed → Today panel errors, others intact", async () => {
    mockEntries.mockImplementation(boom);
    renderWithProviders(<Dashboard />, asSales);
    expect(within(await panel("Today")).getByText("Could not load.")).toBeInTheDocument();
    expect(screen.queryByText("0 eggs today")).not.toBeInTheDocument();
    await expectOthersIntact("today");
  });
  it("current-week report failed → trend panel errors, others intact", async () => {
    mockReport.mockImplementation((from, to) => (to === daysBefore(today, 1) ? boom() : reportByWindow(today)(from, to)));
    renderWithProviders(<Dashboard />, asSales);
    expect(within(await panel("Last 14 days")).getByText("Could not load.")).toBeInTheDocument();
    expect(screen.queryByRole("img")).not.toBeInTheDocument();
    await expectOthersIntact("trend");
  });
  it("previous-week report failed → trend panel errors, others intact", async () => {
    mockReport.mockImplementation((from, to) => (to === daysBefore(today, 8) ? boom() : reportByWindow(today)(from, to)));
    renderWithProviders(<Dashboard />, asSales);
    expect(within(await panel("Last 14 days")).getByText("Could not load.")).toBeInTheDocument();
    expect(screen.queryByText(/Hen-day/)).not.toBeInTheDocument();
    await expectOthersIntact("trend");
  });
  it("stock failed → Stock panel errors, others intact", async () => {
    mockStock.mockImplementation(boom);
    renderWithProviders(<Dashboard />, asSales);
    expect(within(await panel("Stock")).getByText("Could not load.")).toBeInTheDocument();
    expect(screen.queryByText(/0 eggs available/)).not.toBeInTheDocument();
    await expectOthersIntact("stock");
  });
  it("orders failed → Recent sales panel errors, others intact", async () => {
    mockOrders.mockImplementation(boom);
    renderWithProviders(<Dashboard />, asSales);
    expect(within(await panel("Recent sales")).getByText("Could not load.")).toBeInTheDocument();
    await expectOthersIntact("sales");
  });
  it("every issued fetch failed → the page-level message, not four panel errors", async () => {
    for (const m of [mockFlocks, mockEntries, mockStock, mockOrders]) m.mockImplementation(boom);
    mockReport.mockImplementation(boom);
    renderWithProviders(<Dashboard />, asSales);
    expect(await screen.findByText("Could not load dashboard. Is the API up?")).toBeInTheDocument();
    expect(screen.queryByText("Could not load.")).not.toBeInTheDocument();
  });

  it("every issued fetch failed for a ReadOnly user too — the inert sales placeholder does not count as a success", async () => {
    for (const m of [mockFlocks, mockEntries, mockStock]) m.mockImplementation(boom);
    mockReport.mockImplementation(boom);
    renderWithProviders(<Dashboard />, { token: { sub: "u1", role: "ReadOnly" } });
    expect(await screen.findByText("Could not load dashboard. Is the API up?")).toBeInTheDocument();
    expect(screen.queryByText("Could not load.")).not.toBeInTheDocument();
    expect(mockOrders).not.toHaveBeenCalled();
  });
});

// #127 — the customer/sales reads 403 for ReadOnly; the dashboard must not
// fetch them or render the sales panel.
describe("Dashboard sales panel role gate (#127)", () => {
  it("neither fetches nor shows sales for a ReadOnly user", async () => {
    renderWithProviders(<Dashboard />, { token: { sub: "u1", role: "ReadOnly" } });
    expect(await screen.findByText("178 eggs today")).toBeInTheDocument();
    expect(screen.queryByText("Recent sales")).not.toBeInTheDocument();
    expect(mockOrders).not.toHaveBeenCalled();
  });
  it("neither fetches nor shows sales for a Denied user", async () => {
    renderWithProviders(<Dashboard />, { token: { sub: "u1", role: "Denied" } });
    expect(await screen.findByText("178 eggs today")).toBeInTheDocument();
    expect(screen.queryByText("Recent sales")).not.toBeInTheDocument();
    expect(mockOrders).not.toHaveBeenCalled();
  });
  it("fetches and shows the sales panel for a non-ReadOnly user", async () => {
    renderWithProviders(<Dashboard />, { token: { sub: "u1", role: "Sales" } });
    expect(await screen.findByText("Recent sales")).toBeInTheDocument();
    expect(mockOrders).toHaveBeenCalled();
  });
});

// #512 US4/US5 — rows carry their own customerName; the link goes to Sales
// filtered by the canonical customerId; never an id fragment.
describe("Dashboard recent sales rows (#512)", () => {
  it("renders a compact list row with the row-owned name linked to /sales?customerId=<id>", async () => {
    mockOrders.mockResolvedValue([order("o-1", "SO-3", "Filtered Farm")]);
    renderWithProviders(<Dashboard />, { token: { sub: "u1", role: "Sales" } });
    const row = await screen.findByRole("listitem", { name: /SO-3/ });
    expect(within(row).getByRole("link", { name: "Filtered Farm" })).toHaveAttribute("href", "/sales?customerId=c1");
    expect(within(row).getByText("Draft")).toBeInTheDocument();
    expect(within(row).getByText("$10.00")).toBeInTheDocument();
  });
  it("gives each row its own name and its own customer link, never the first row's", async () => {
    mockOrders.mockResolvedValue([
      { ...order("o-1", "SO-10", "First Farm") },
      { ...order("o-2", "SO-11", "Second Farm"), customerId: "c2" },
    ]);
    renderWithProviders(<Dashboard />, { token: { sub: "u1", role: "Sales" } });

    const first = await screen.findByRole("listitem", { name: /SO-10/ });
    const second = screen.getByRole("listitem", { name: /SO-11/ });
    expect(within(first).getByRole("link", { name: "First Farm" })).toHaveAttribute("href", "/sales?customerId=c1");
    expect(within(second).getByRole("link", { name: "Second Farm" })).toHaveAttribute("href", "/sales?customerId=c2");
    expect(within(second).queryByText("First Farm")).not.toBeInTheDocument();
  });
  it("shows the translated unavailable label for a null customerName — never an id fragment", async () => {
    mockOrders.mockResolvedValue([order("o-gone", "SO-1", null)]);
    renderWithProviders(<Dashboard />, { token: { sub: "u1", role: "Sales" } });
    const row = await screen.findByRole("listitem", { name: /SO-1/ });
    expect(within(row).getByText(i18n.t("dashboard:rowCustomerUnavailable"))).toBeInTheDocument();
    expect(within(row).queryByText("c1")).not.toBeInTheDocument();
  });
});

// The farm's day and locale win over the browser's (#123, #650): with time
// frozen at 23:30Z, a +14 farm is already on the next calendar day while any
// browser between UTC−12 and UTC+0:30 is not; a de-DE farm groups with "." and
// uses "," for the decimal.
describe("Dashboard follows the farm's day and locale", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-07-21T23:30:00Z"));
  });
  afterEach(() => vi.useRealTimers());

  it("asks for the farm's yesterday, links the tile to the farm's today, and formats in the farm locale", async () => {
    const farm = account({ locale: "de-DE", timeZoneId: "Pacific/Kiritimati" });
    const farmToday = "2026-07-22";
    expect(todayIso()).not.toBe(farmToday); // the browser is still on the 21st — the two days differ
    mockReport.mockImplementation(reportByWindow(farmToday));
    renderWithProviders(<Dashboard />, { farm });

    const f1 = await screen.findByRole("link", { name: "Flock f1: open today's entry" });
    expect(f1).toHaveAttribute("href", `/daily-entry?flockId=f1&date=${farmToday}`);
    // The tiles' own query must use the FARM's day too, not just the links and
    // the report windows: with the clock frozen at 23:30Z a +14 farm is already
    // on the 22nd while the browser is on the 21st, so a regression to
    // browser-local todayIso() shows yesterday's entries under today's date.
    expect(mockEntries).toHaveBeenCalledWith({ from: farmToday, to: farmToday, limit: 500 });
    expect(mockReport).toHaveBeenCalledWith("2026-07-15", "2026-07-21");
    expect(mockReport).toHaveBeenCalledWith("2026-07-08", "2026-07-14");
    expect(screen.getByText(/1\.560 eggs available\./)).toBeInTheDocument();
    expect(screen.getByText("Hen-day 87,4% · +2,3 pts vs the previous 7 days")).toBeInTheDocument();
  });
});

// i18n wiring: swap catalog values at runtime so each marker only renders if
// the screen reads the catalog rather than a literal that happens to match.
describe("Dashboard i18n wiring (#654)", () => {
  function withOverride(ns: string, key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", ns, key) as string;
    i18n.addResource("en", ns, key, value);
    return Promise.resolve(run()).finally(() => { i18n.addResource("en", ns, key, original); });
  }
  it("reads the heading, the trend title and the today total from the catalog", async () => {
    await withOverride("dashboard", "title", "TITLE-MARKER", async () => {
      renderWithProviders(<Dashboard />);
      expect(await screen.findByRole("heading", { name: "TITLE-MARKER" })).toBeInTheDocument();
    });
    await withOverride("dashboard", "trendPanelTitle", "TREND-MARKER", async () => {
      renderWithProviders(<Dashboard />);
      expect(await screen.findByText("TREND-MARKER")).toBeInTheDocument();
    });
    await withOverride("dashboard", "todayEggsTotal", "TOTAL-MARKER {{total}}", async () => {
      renderWithProviders(<Dashboard />);
      expect(await screen.findByText("TOTAL-MARKER 178")).toBeInTheDocument();
    });
  });
});
