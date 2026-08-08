import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, fireEvent, act, within } from "@testing-library/react";
import { FeedPage } from "./FeedPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { listFlocks, listInventoryItems, listFeedUsage, recordFeedUsage } from "../api/cluckwork";
import type { Flock, InventoryItem, FeedUsage } from "../api/cluckwork";
import { ApiError } from "../api/client";
import i18n from "../i18n";

// Network seam stubbed; everything else real (formatMoney, i18n, auth).
vi.mock("../api/cluckwork", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/cluckwork")>();
  return {
    ...actual,
    listFlocks: vi.fn(),
    listInventoryItems: vi.fn(),
    listFeedUsage: vi.fn(),
    recordFeedUsage: vi.fn(),
  };
});
const mockListFlocks = vi.mocked(listFlocks);
const mockListItems = vi.mocked(listInventoryItems);
const mockListUsage = vi.mocked(listFeedUsage);
const mockRecord = vi.mocked(recordFeedUsage);

const FLOCK: Flock = {
  id: "f1", farmId: "farm1", houseId: "h1", name: "Barn A", breed: "Leghorn",
  startDate: "2026-01-01", initialCount: 100, currentCount: 98,
  status: "Active", depletedDate: null, notes: null, version: 1,
} as unknown as Flock;

function item(overrides: Partial<InventoryItem> = {}): InventoryItem {
  return {
    id: "i1", farmId: "farm1", name: "Layer feed", category: "Feed", unit: "kg",
    defaultCostMinorUnits: 2500, defaultCostCurrencyCode: "USD", defaultCostCurrencyMinorUnit: 2,
    quantityOnHand: 120, active: true,
    ...overrides,
  };
}

function usageRow(overrides: Partial<FeedUsage> = {}): FeedUsage {
  return {
    id: "u1", flockId: "f1", inventoryItemId: "i1", date: "2026-08-07",
    quantity: 18, unit: "kg", estimatedCostMinorUnits: 45_000,
    currencyCode: "USD", currencyMinorUnit: 2, note: "morning feed", dailyEntryId: null,
    ...overrides,
  };
}

const ADMIN = { sub: "u1", role: "Admin" };

beforeEach(() => {
  vi.clearAllMocks();
  mockListFlocks.mockResolvedValue([FLOCK]);
  mockListItems.mockResolvedValue([item()]);
  mockListUsage.mockResolvedValue([]);
});

async function renderReady(route = "/feed") {
  renderWithProviders(<FeedPage />, { token: ADMIN, route });
  await screen.findByRole("button", { name: "Record feed" });
}

describe("FeedPage (#446 — feed usage promoted out of the Inventory drill-down)", () => {
  it("offers feedable items — active, or inactive with stock left to feed out — never other categories", async () => {
    mockListItems.mockResolvedValue([
      item(),
      item({ id: "i2", name: "Supplement mix", category: "Supplement", quantityOnHand: 4 }),
      item({ id: "i3", name: "Egg cartons", category: "Packaging" }),
      // Inactive with stock: deactivation only stops NEW purchases; the
      // remaining feed still gets eaten out (server-documented semantics).
      item({ id: "i4", name: "Old feed", category: "Feed", active: false, quantityOnHand: 12 }),
      item({ id: "i5", name: "Spent feed", category: "Feed", active: false, quantityOnHand: 0 }),
    ]);
    await renderReady();

    const picker = screen.getByLabelText("Item");
    const options = within(picker).getAllByRole("option");
    expect(options.map((o) => o.textContent)).toEqual([
      "Layer feed (120 kg on hand)",
      "Supplement mix (4 kg on hand)",
      "Old feed (12 kg on hand) — inactive, feeding out remaining stock",
    ]);
  });

  it("records usage with the typed payload and a stable idempotency key, rotating only on success", async () => {
    mockRecord.mockRejectedValueOnce(new ApiError(500, "boom", "boom"));
    mockRecord.mockResolvedValue({
      feedUsageId: "u9", quantityUsed: 25, estimatedCostMinorUnits: 62_500, currencyCode: "USD",
    });
    await renderReady();

    fireEvent.change(screen.getByLabelText(/Quantity/), { target: { value: "25" } });
    fireEvent.change(screen.getByLabelText("Note"), { target: { value: "evening" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Record feed" }));
    });
    // Failed → key kept; retry replays the SAME key.
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Record feed" }));
    });

    expect(mockRecord).toHaveBeenCalledTimes(2);
    const [firstItemId, firstBody, firstKey] = mockRecord.mock.calls[0];
    const [, , secondKey] = mockRecord.mock.calls[1];
    expect(firstItemId).toBe("i1");
    expect(firstBody).toMatchObject({ flockId: "f1", quantity: 25, note: "evening" });
    expect(secondKey).toBe(firstKey);
    expect(await screen.findByText("Feed recorded.")).toBeInTheDocument();
    // Success cleared quantity for the next capture.
    expect(screen.getByLabelText(/Quantity/)).toHaveValue(null);
  });

  it("refuses a non-positive quantity client-side without calling the server", async () => {
    await renderReady();
    fireEvent.change(screen.getByLabelText(/Quantity/), { target: { value: "0" } });
    // Submit the form directly: jsdom otherwise blocks the submit-button
    // click on the min= constraint before onSubmit's own check can run.
    const form = screen.getByRole("button", { name: "Record feed" }).closest("form")!;
    await act(async () => { fireEvent.submit(form); });
    expect(mockRecord).not.toHaveBeenCalled();
    expect(screen.getByText("Quantity must be a positive number.")).toBeInTheDocument();
  });

  it("surfaces the server's insufficient-stock refusal", async () => {
    mockRecord.mockRejectedValue(new ApiError(422, "InventoryLot.InsufficientStock",
      "Requested 500 kg but only 120 kg in stock on 2026-08-08."));
    await renderReady();
    fireEvent.change(screen.getByLabelText(/Quantity/), { target: { value: "500" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Record feed" }));
    });
    expect(await screen.findByText(/only 120 kg in stock/)).toBeInTheDocument();
  });

  it("renders the history with item names, unit quantities, and formatted cost", async () => {
    mockListUsage.mockResolvedValue([usageRow()]);
    await renderReady();

    const row = screen.getByRole("row", { name: /Barn A/ });
    expect(within(row).getByText("2026-08-07")).toBeInTheDocument();
    expect(within(row).getByText("Layer feed")).toBeInTheDocument();
    expect(within(row).getByText("18 kg")).toBeInTheDocument();
    expect(within(row).getByText("450.00 USD")).toBeInTheDocument();
    expect(within(row).getByText("morning feed")).toBeInTheDocument();
  });

  it("re-queries the list when the flock filter changes and appends on load more", async () => {
    const first = Array.from({ length: 50 }, (_, i) => usageRow({ id: `u${i}` }));
    mockListUsage.mockResolvedValueOnce(first);
    await renderReady();
    expect(mockListUsage).toHaveBeenCalledWith(
      expect.objectContaining({ limit: 50, offset: 0 }));

    mockListUsage.mockResolvedValueOnce([usageRow({ id: "u99", note: "page 2" })]);
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "load more" }));
    });
    expect(mockListUsage).toHaveBeenLastCalledWith(
      expect.objectContaining({ offset: 50 }));
    expect(screen.getByText("page 2")).toBeInTheDocument();

    mockListUsage.mockResolvedValueOnce([]);
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Filter by flock"), { target: { value: "f1" } });
    });
    expect(mockListUsage).toHaveBeenLastCalledWith(
      expect.objectContaining({ flockId: "f1", offset: 0 }));
  });

  it("blocks a second load-more click while the first flight is still open", async () => {
    const first = Array.from({ length: 50 }, (_, i) => usageRow({ id: `u${i}` }));
    mockListUsage.mockResolvedValueOnce(first);
    await renderReady();

    let release!: (rows: FeedUsage[]) => void;
    mockListUsage.mockReturnValueOnce(new Promise((r) => { release = r; }));
    fireEvent.click(screen.getByRole("button", { name: "load more" }));
    fireEvent.click(screen.getByRole("button", { name: "load more" }));
    await act(async () => { release([usageRow({ id: "u99" })]); });

    // 1 initial + 1 load-more — the second click was swallowed by the guard,
    // so the same page can never append twice.
    expect(mockListUsage).toHaveBeenCalledTimes(2);
  });

  it("preselects a deactivated item named by the deep link — remaining stock still gets fed out", async () => {
    // Server-documented semantics: deactivation only stops NEW stock; an
    // inactive item with stock keeps being eaten. The Inventory link to it
    // must land on IT, never silently swap to another item.
    mockListItems.mockResolvedValue([
      item(),
      item({ id: "i2", name: "Old feed", category: "Feed", active: false, quantityOnHand: 30 }),
      item({ id: "i3", name: "Spent feed", category: "Feed", active: false, quantityOnHand: 0 }),
    ]);
    await renderReady("/feed?item=i2");
    expect(screen.getByLabelText("Item")).toHaveValue("i2");
    expect(screen.getByRole("option", { name: /Old feed .*inactive, feeding out/ })).toBeInTheDocument();
    // Inactive AND empty is genuinely gone.
    expect(screen.queryByRole("option", { name: /Spent feed/ })).not.toBeInTheDocument();
  });

  it("honors a deep link to an inactive empty item rather than substituting another one", async () => {
    // The Inventory link (or a stale bookmark) can name an item that has
    // since gone inactive AND empty. Falling back to feedable[0] would let
    // the user drain an UNRELATED item — keep the requested one selected
    // with its own empty marker and let the stock check refuse the submit.
    mockListItems.mockResolvedValue([
      item(),
      item({ id: "i3", name: "Spent feed", category: "Feed", active: false, quantityOnHand: 0 }),
    ]);
    await renderReady("/feed?item=i3");
    expect(screen.getByLabelText("Item")).toHaveValue("i3");
    expect(screen.getByRole("option", { name: /Spent feed .*inactive, no stock left/ })).toBeInTheDocument();
  });

  it("ignores a stale filter response that lands after a newer one", async () => {
    await renderReady();

    let releaseStale!: (rows: FeedUsage[]) => void;
    mockListUsage.mockReturnValueOnce(new Promise((r) => { releaseStale = r; }));
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-08-01" } });
    mockListUsage.mockResolvedValueOnce([usageRow({ id: "uF", note: "fresh rows" })]);
    await act(async () => {
      fireEvent.change(screen.getByLabelText("To"), { target: { value: "2026-08-02" } });
    });
    expect(screen.getByText("fresh rows")).toBeInTheDocument();

    // The abandoned first request resolves LAST — it must not clobber the
    // rows that match the currently displayed filters.
    await act(async () => { releaseStale([usageRow({ id: "uS", note: "stale rows" })]); });
    expect(screen.getByText("fresh rows")).toBeInTheDocument();
    expect(screen.queryByText("stale rows")).not.toBeInTheDocument();
  });

  it("ignores a stale filter rejection that lands after a newer success", async () => {
    await renderReady();

    let rejectStale!: (err: Error) => void;
    mockListUsage.mockReturnValueOnce(new Promise((_, rej) => { rejectStale = rej; }));
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-08-01" } });
    mockListUsage.mockResolvedValueOnce([usageRow({ id: "uF", note: "fresh rows" })]);
    await act(async () => {
      fireEvent.change(screen.getByLabelText("To"), { target: { value: "2026-08-02" } });
    });

    await act(async () => { rejectStale(new Error("boom")); });
    expect(screen.getByText("fresh rows")).toBeInTheDocument();
    expect(screen.queryByText("Could not load feed records.")).not.toBeInTheDocument();
  });

  it("refreshes the picker's on-hand figures after a successful record", async () => {
    mockRecord.mockResolvedValue({
      feedUsageId: "u9", quantityUsed: 20, estimatedCostMinorUnits: 100, currencyCode: "USD",
    });
    await renderReady();
    expect(screen.getByRole("option", { name: "Layer feed (120 kg on hand)" })).toBeInTheDocument();

    mockListItems.mockResolvedValue([item({ quantityOnHand: 100 })]);
    fireEvent.change(screen.getByLabelText(/Quantity/), { target: { value: "20" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Record feed" }));
    });
    // The pre-submit sanity number must not lie after the feeding it enabled.
    expect(screen.getByRole("option", { name: "Layer feed (100 kg on hand)" })).toBeInTheDocument();
  });

  it("initializes the list filters from the Daily Entry strip's URL parameters", async () => {
    await renderReady("/feed?flockId=f1&from=2026-08-01&to=2026-08-01");
    expect(mockListUsage).toHaveBeenCalledWith(expect.objectContaining(
      { flockId: "f1", from: "2026-08-01", to: "2026-08-01" }));
    expect(screen.getByLabelText("Filter by flock")).toHaveValue("f1");
  });

  it("keeps the capture form usable when the history read fails", async () => {
    mockListUsage.mockRejectedValueOnce(new Error("boom"));
    await renderReady(); // findByRole('Record feed') IS the form being alive
    expect(screen.getByText("Could not load feed records.")).toBeInTheDocument();
    expect(screen.getByLabelText(/Quantity/)).toBeEnabled();
  });

  it("preselects the item named by the ?item= deep link from the Inventory page", async () => {
    mockListItems.mockResolvedValue([
      item(),
      item({ id: "i2", name: "Supplement mix", category: "Supplement" }),
    ]);
    await renderReady("/feed?item=i2");
    expect(screen.getByLabelText("Item")).toHaveValue("i2");
  });

  it("tells the user corrections happen via Inventory adjustments — feed is create-only", async () => {
    await renderReady();
    expect(screen.getByText(/corrected with an Inventory adjustment/)).toBeInTheDocument();
  });
});

describe("FeedPage i18n wiring (#446)", () => {
  it("reads the recorded message from the catalog, not a literal", async () => {
    const original = i18n.getResource("en", "feed", "recordedMessage") as string;
    i18n.addResource("en", "feed", "recordedMessage", "RECORDED-MARKER");
    try {
      mockRecord.mockResolvedValue({
        feedUsageId: "u9", quantityUsed: 5, estimatedCostMinorUnits: 100, currencyCode: "USD",
      });
      await renderReady();
      fireEvent.change(screen.getByLabelText(/Quantity/), { target: { value: "5" } });
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "Record feed" }));
      });
      expect(await screen.findByText("RECORDED-MARKER")).toBeInTheDocument();
      expect(screen.queryByText("Feed recorded.")).not.toBeInTheDocument();
    } finally {
      i18n.addResource("en", "feed", "recordedMessage", original);
    }
  });
});
