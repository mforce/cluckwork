import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, fireEvent, act, within, waitFor } from "@testing-library/react";
import { FeedPage } from "./FeedPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { listFlocks, listInventoryItems, listFeedUsage, recordFeedUsage, getFlock } from "../api/cluckwork";
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
    getFlock: vi.fn(),
    getCustomer: vi.fn(),
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
    flockName: "Barn A",
    ...overrides,
  };
}

const ADMIN = { sub: "u1", role: "Admin" };

beforeEach(() => {
  vi.clearAllMocks();
  // #512 — the FlockPicker's discovery uses the SAME listFlocks seam (typed
  // eligibility query). The default fixture serves the full list for any
  // eligibility; the picker's exact-identity read (T038) resolves the
  // fixture flock.
  mockListFlocks.mockImplementation(
    async (p?: { search?: string | null; eligibility?: string; limit?: number; offset?: number }) =>
      [FLOCK].slice(p?.offset ?? 0, (p?.offset ?? 0) + (p?.limit ?? 50)),
  );
  vi.mocked(getFlock).mockImplementation(async (id: string) =>
    id === FLOCK.id ? FLOCK : Promise.reject(new Error(`Unknown flock: ${id}`)));
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
    // #512 — the filter flock is now a FlockPicker, not a native select.
    // Open the picker (trigger), pick the option, and the picker commits.
    // The records list re-queries with the EXACT committed flockId.
    const filterTrigger = screen.getByRole("button", { name: /All/ });
    fireEvent.click(filterTrigger);
    const option = await screen.findByRole("option", { name: /Barn A/ });
    fireEvent.click(option);
    await waitFor(() => expect(mockListUsage).toHaveBeenLastCalledWith(
      expect.objectContaining({ flockId: "f1", offset: 0 })));
  });

  it("withdraws load-more for the duration of its own flight", async () => {
    // #469 made this stricter than the old "the second click is swallowed":
    // usePagedList's canLoadMore folds in `loading`, so there is no control to
    // click a second time. (The hook additionally no-ops a load-more issued
    // while one is in flight — pinned in usePagedList.test.tsx, for callers
    // that do not render from canLoadMore.)
    const first = Array.from({ length: 50 }, (_, i) => usageRow({ id: `u${i}` }));
    mockListUsage.mockResolvedValueOnce(first);
    await renderReady();

    let release!: (rows: FeedUsage[]) => void;
    mockListUsage.mockReturnValueOnce(new Promise((r) => { release = r; }));
    fireEvent.click(screen.getByRole("button", { name: "load more" }));
    expect(screen.queryByRole("button", { name: "load more" })).not.toBeInTheDocument();

    await act(async () => { release([usageRow({ id: "u99", note: "appended page" })]); });

    // 1 initial + 1 load-more, and the appended page is the only new one.
    expect(mockListUsage).toHaveBeenCalledTimes(2);
    expect(screen.getByText("appended page")).toBeInTheDocument();
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

  it("clears stale rows and pagination when a filtered reload fails", async () => {
    // Rows from the PREVIOUS filter must not sit under the NEW filter's
    // controls — and a stale hasMore would let load-more append a new-filter
    // page into old-filter rows.
    const first = Array.from({ length: 50 }, (_, i) => usageRow({ id: `u${i}`, note: "old rows" }));
    mockListUsage.mockResolvedValueOnce(first);
    await renderReady();
    expect(screen.getByRole("button", { name: "load more" })).toBeInTheDocument();

    mockListUsage.mockRejectedValueOnce(new Error("boom"));
    await act(async () => {
      fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-08-01" } });
    });
    expect(screen.getByText("Could not load feed records.")).toBeInTheDocument();
    expect(screen.queryByText("old rows")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "load more" })).not.toBeInTheDocument();
  });

  it("hides load-more while a filtered reload is in flight", async () => {
    // With hasMore still true from the old filter, clicking load-more during
    // the pending reload would start load(oldRows.length) under the NEW
    // filters, supersede the offset-zero flight, and append a new-filter
    // page onto old-filter rows while skipping the new filter's first page.
    const first = Array.from({ length: 50 }, (_, i) => usageRow({ id: `u${i}`, note: "old rows" }));
    mockListUsage.mockResolvedValueOnce(first);
    await renderReady();
    expect(screen.getByRole("button", { name: "load more" })).toBeInTheDocument();

    let release!: (rows: FeedUsage[]) => void;
    mockListUsage.mockReturnValueOnce(new Promise((r) => { release = r; }));
    await act(async () => {
      fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-08-01" } });
    });
    expect(screen.queryByRole("button", { name: "load more" })).not.toBeInTheDocument();

    await act(async () => { release([usageRow({ id: "uB", note: "new rows" })]); });
    expect(screen.getByText("new rows")).toBeInTheDocument();
    expect(screen.queryByText("old rows")).not.toBeInTheDocument();
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
    // #512 — the filter picker's trigger shows the EXACT row-owned identity's
    // name (resolved via the exact GET, T038), not a raw id. The capture
    // picker's trigger ALSO shows "Barn A" (the default), so scope to the
    // filter's own trigger (the one inside the .filter-flock container).
    const filterTrigger = screen.getByLabelText("Filter by flock");
    expect(filterTrigger).toBeInTheDocument();
    expect(filterTrigger).toHaveTextContent(/Barn A/);
  });

  // #512 US4 (T043/T051) — a record row's own flockName is null (the flock
  // left the caller's tenant/flock scope between reads), even though the
  // SAME id is present in the page's own capped `flocks` list under a
  // DIFFERENT-looking name. The row must show the translated unavailable
  // label, never that catalog substitution and never a raw id fragment.
  it("a record row whose own flockName is null shows the translated unavailable label — never the catalog's name for that id, never an id fragment", async () => {
    mockListUsage.mockResolvedValue([usageRow({ id: "u-gone", flockId: "f1", flockName: null })]);
    await renderReady();

    const dataRow = await screen.findByRole("row", { name: /morning feed/ });
    expect(within(dataRow).getByText(i18n.t("feed:rowFlockUnavailable"))).toBeInTheDocument();
    expect(within(dataRow).queryByText("Barn A")).not.toBeInTheDocument();
    expect(within(dataRow).queryByText("f1")).not.toBeInTheDocument();
  });

  // #512 US3 remediation — the deep-linked filter's picker mounts CLOSED
  // (`filterPickerOpen` starts false); before this fix a failed exact GET for
  // that row-owned id had no adjacent recovery until the user opened the
  // picker themselves. Now the translated unavailable status and a
  // keyboard-reachable Retry render right beside the trigger, closed or not.
  it("a deep-linked flock filter whose exact GET fails renders unavailable with an adjacent Retry, closed — never a first-result substitution, and Retry is GET-only and can recover", async () => {
    vi.mocked(getFlock).mockRejectedValueOnce(new Error("not found"));
    await renderReady("/feed?flockId=f-gone&from=2026-08-01&to=2026-08-01");

    // The list stays scoped to the EXACT requested id — never dropped or
    // substituted with "all flocks" / the first discovery result.
    await waitFor(() => expect(mockListUsage).toHaveBeenCalledWith(
      expect.objectContaining({ flockId: "f-gone", from: "2026-08-01", to: "2026-08-01" })));
    await waitFor(() => expect(screen.getByLabelText("Filter by flock"))
      .toHaveTextContent(i18n.t("feed:filterFlockUnavailable")));
    const filterTrigger = screen.getByLabelText("Filter by flock");
    expect(filterTrigger).not.toHaveTextContent("Barn A"); // never the first result

    // The engine's own adjacent recovery — translated, visible without
    // opening the picker.
    const unavailableLabel = i18n.t("namedEntityPicker:unavailable");
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent(unavailableLabel));
    const retryLabel = i18n.t("namedEntityPicker:retry");
    const retryBtn = screen.getByRole("button", { name: retryLabel });

    // Retry re-issues ONLY the exact GET; success commits the exact entity.
    const getFlockCallsBefore = vi.mocked(getFlock).mock.calls.length;
    vi.mocked(getFlock).mockResolvedValueOnce({ ...FLOCK, id: "f-gone" });
    fireEvent.click(retryBtn);
    await waitFor(() => expect(vi.mocked(getFlock).mock.calls.length).toBe(getFlockCallsBefore + 1));
    await waitFor(() => expect(screen.getByLabelText("Filter by flock")).toHaveTextContent("Barn A"));
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
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
