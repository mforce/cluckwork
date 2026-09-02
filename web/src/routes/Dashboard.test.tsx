import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within } from "@testing-library/react";
import { Dashboard } from "./Dashboard";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  getStock, listCustomers, listDailyEntries, listFlocks, listOrders,
} from "../api/cluckwork";
import type { DailyEntry, Flock, SalesOrder, StockRow } from "../api/cluckwork";
import i18n from "../i18n";
import { NO_RECORD_HISTORY } from "../test/fixtures";

// Keep the real formatMoney; stub the five read endpoints the dashboard fans out.
vi.mock("../api/cluckwork", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/cluckwork")>();
  return {
    ...actual,
    listFlocks: vi.fn(),
    listDailyEntries: vi.fn(),
    getStock: vi.fn(),
    listOrders: vi.fn(),
    listCustomers: vi.fn(),
    getFlock: vi.fn(),
  getCustomer: vi.fn(),
};
});

const mockFlocks = vi.mocked(listFlocks);
const mockEntries = vi.mocked(listDailyEntries);
const mockStock = vi.mocked(getStock);
const mockOrders = vi.mocked(listOrders);
const mockCustomers = vi.mocked(listCustomers);

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
const STOCK: StockRow[] = [
  { eggGradeId: "g1", gradeName: "Grade A", available: 1240, restricted: 0 },
  { eggGradeId: "g2", gradeName: "Grade B", available: 320, restricted: 0 },
];

// value cell sits immediately before its label in a .stat card
const statFor = (label: string) => screen.getByText(label).previousElementSibling;

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  mockFlocks.mockResolvedValue([flock("f1", "Active"), flock("f2", "Active"), flock("f3", "Active")]);
  mockEntries.mockResolvedValue([entry("f1", "Submitted", 178), entry("f2", "Voided", 999)]);
  mockStock.mockResolvedValue(STOCK);
  mockOrders.mockResolvedValue([] as SalesOrder[]);
  mockCustomers.mockResolvedValue([]);
});

describe("Dashboard stat cards", () => {
  it("sums today's eggs excluding voided entries, totals available stock, and counts active flocks", async () => {
    renderWithProviders(<Dashboard />);
    // eggs collected today = 178 only (the Voided 999 entry is excluded)
    expect(await screen.findByText("Eggs collected today")).toBeInTheDocument();
    expect(statFor("Eggs collected today")).toHaveTextContent("178");
    expect(statFor("Eggs available")).toHaveTextContent("1560"); // 1240 + 320
    expect(statFor("Active flocks")).toHaveTextContent("3");
  });

  it("shows an em-dash rather than a misleading zero when a fetch fails", async () => {
    mockStock.mockRejectedValue(new Error("stock down")); // only stock fails
    renderWithProviders(<Dashboard />);
    expect(await screen.findByText("Eggs available")).toBeInTheDocument();
    expect(statFor("Eggs available")).toHaveTextContent("—");
    // the other stats still resolve from their successful fetches
    expect(statFor("Eggs collected today")).toHaveTextContent("178");
    expect(statFor("Active flocks")).toHaveTextContent("3");
  });
});

// #127 — the customer/sales reads now 403 for ReadOnly; the dashboard must not
// fetch them or render the sales panel (it would blank with an error otherwise).
// #512 US4 (T043/T052) — a recent-sales row's own customerName is null (the
// customer left the caller's tenant scope between reads), even though the
// SAME id is present in the page's own customer catalog fetch under a
// DIFFERENT-looking name. The row must show the translated unavailable
// label, never that catalog substitution and never a raw id fragment.
describe("Dashboard recent sales row-owned customer name (#512 US4)", () => {
  const order = (id: string, ref: string, customerName: string | null): SalesOrder => ({
    ...NO_RECORD_HISTORY, id, customerId: "c1", customerName, referenceNumber: ref,
    orderDate: "2026-07-21", status: "Draft", totalMinorUnits: 1000, currencyCode: "USD",
    currencyMinorUnit: 2, voidReason: null, items: [],
  });

  it("a row whose own customerName is null shows the translated unavailable label — never the catalog's name for that id, never an id fragment", async () => {
    mockOrders.mockResolvedValue([order("o-gone", "SO-1", null)]);
    mockCustomers.mockResolvedValue([{ id: "c1", name: "Acme Eggs", phone: "", email: null, address: null, note: null, version: 1 }]);
    renderWithProviders(<Dashboard />);

    const row = await screen.findByRole("row", { name: /SO-1/ });
    expect(within(row).getByText(i18n.t("dashboard:rowCustomerUnavailable"))).toBeInTheDocument();
    expect(within(row).queryByText("Acme Eggs")).not.toBeInTheDocument();
    expect(within(row).queryByText("c1")).not.toBeInTheDocument();
  });

  it("a row's own customerName renders directly — no catalog lookup needed", async () => {
    mockOrders.mockResolvedValue([order("o-1", "SO-2", "Row-Owned Name")]);
    mockCustomers.mockResolvedValue([]);
    renderWithProviders(<Dashboard />);

    const row = await screen.findByRole("row", { name: /SO-2/ });
    expect(within(row).getByText("Row-Owned Name")).toBeInTheDocument();
  });
});

describe("Dashboard sales panel role gate (#127)", () => {
  it("neither fetches nor shows sales for a ReadOnly user", async () => {
    renderWithProviders(<Dashboard />, { token: { sub: "u1", role: "ReadOnly" } });
    // a core panel still resolves, so the dashboard did mount
    expect(await screen.findByText("Eggs collected today")).toBeInTheDocument();
    expect(screen.queryByText("Recent sales")).not.toBeInTheDocument();
    expect(mockOrders).not.toHaveBeenCalled();
    expect(mockCustomers).not.toHaveBeenCalled();
  });

  it("fetches and shows the sales panel for a non-ReadOnly user", async () => {
    renderWithProviders(<Dashboard />, { token: { sub: "u1", role: "Sales" } });
    expect(await screen.findByText("Recent sales")).toBeInTheDocument();
    expect(mockOrders).toHaveBeenCalled();
    expect(mockCustomers).toHaveBeenCalled();
  });
});

// #182, Task 12: the "Today" panel's per-flock entry status now goes through
// the `enums` statusLabel helper instead of rendering StatusBadge's raw text.
// This is an INTENTIONAL harmonization, not text-preserving, for exactly one
// value — a ManagerAdjusted entry used to render raw ("ManagerAdjusted", no
// visible word boundary) and now reads "Adjusted", matching the label
// HistoryPage already shows for the same state via its own bespoke badge (see
// en.ts's `enums` header comment).
describe("Dashboard status pills (#182, Task 12)", () => {
  it("shows the harmonized 'Adjusted' label, not the raw status, for a manager-adjusted entry", async () => {
    mockEntries.mockResolvedValue([entry("f1", "ManagerAdjusted", 150)]);
    renderWithProviders(<Dashboard />);

    expect(await screen.findByText("Adjusted")).toBeInTheDocument();
    expect(screen.queryByText("ManagerAdjusted")).not.toBeInTheDocument();
    expect(screen.queryByText(/manageradjusted/i)).not.toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 12, batch B2)
// ---------------------------------------------------------------------------

// `dashboard` is English-only (not in TRANSLATED_NAMESPACES — see
// translations-status.ts), so under ANY UI language the rendered text falls
// back to this exact English string, same as a still-hardcoded literal would
// render — asserting it, even under a non-English locale, would prove nothing
// (CONTRIBUTING-i18n.md's fallback trap). Swap the catalog value at runtime
// instead, the same i18n.addResource technique the other batches use, so each
// marker only renders if the screen actually reads the catalog rather than a
// literal that happens to still match it.
describe("Dashboard i18n wiring (#182, Task 12)", () => {
  function withOverride(ns: string, key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", ns, key) as string;
    i18n.addResource("en", ns, key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", ns, key, original);
    });
  }

  it("reads the heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("dashboard", "title", "TITLE-MARKER", async () => {
      renderWithProviders(<Dashboard />);
      expect(await screen.findByRole("heading", { name: "TITLE-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Dashboard" })).not.toBeInTheDocument();
    });
  });

  it("reads a stat label from the catalog, not a hardcoded literal", async () => {
    await withOverride("dashboard", "statEggsCollectedToday", "STAT-MARKER", async () => {
      renderWithProviders(<Dashboard />);
      expect(await screen.findByText("STAT-MARKER")).toBeInTheDocument();
      expect(screen.queryByText("Eggs collected today")).not.toBeInTheDocument();
    });
  });

  it("interpolates the stock total into the catalog template, not a hardcoded literal", async () => {
    await withOverride("dashboard", "eggsAvailableMessage", "COUNT-MARKER {{count}} MARKER-END", async () => {
      renderWithProviders(<Dashboard />);
      // 1240 + 320 from the default STOCK fixture (see the stat-card test above).
      expect(await screen.findByText("COUNT-MARKER 1560 MARKER-END")).toBeInTheDocument();
      expect(screen.queryByText(/eggs available\./)).not.toBeInTheDocument();
    });
  });
});
