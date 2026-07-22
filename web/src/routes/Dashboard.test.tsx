import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen } from "@testing-library/react";
import { Dashboard } from "./Dashboard";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  getStock, listCustomers, listDailyEntries, listFlocks, listOrders,
} from "../api/cluckwork";
import type { DailyEntry, Flock, SalesOrder, StockRow } from "../api/cluckwork";

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
  };
});

const mockFlocks = vi.mocked(listFlocks);
const mockEntries = vi.mocked(listDailyEntries);
const mockStock = vi.mocked(getStock);
const mockOrders = vi.mocked(listOrders);
const mockCustomers = vi.mocked(listCustomers);

const flock = (id: string, status: string): Flock => ({
  id, farmId: "f", houseId: "h", name: `Flock ${id}`, breed: "ISA",
  placementDate: "2026-01-01", initialCount: 100, currentBirds: 98, status,
});
const entry = (flockId: string, status: string, totalEggs: number): DailyEntry => ({
  id: `de-${flockId}`, farmId: "f", houseId: "h", flockId, date: "2026-07-21", status,
  totalEggs, crackedEggs: 0, dirtyEggs: 0, discardedEggs: 0, mortalityCount: 0, grades: [],
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
