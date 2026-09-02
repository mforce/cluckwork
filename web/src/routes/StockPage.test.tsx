import { describe, it, expect, vi, beforeEach } from "vitest";
import type { ReactNode } from "react";
import { act, screen, waitFor, within, fireEvent } from "@testing-library/react";
import { StockPage } from "./StockPage";
import {
  getStock, listEggLots, listEggLotMovements, recordEggLotMovement,
} from "../api/cluckwork";
import type { StockRow, EggLotRow, EggMovementRow, EggLotMovementResult } from "../api/cluckwork";
import i18n from "../i18n";
import { renderWithProviders } from "../test/renderWithProviders";

// Mock the API seam so the screen renders against controlled data — no network,
// no backend. This proves the component test harness handles an async data load,
// the loading/empty branches, and the client-side total.
vi.mock("../api/cluckwork", () => ({
  getStock: vi.fn(),
  listEggLots: vi.fn(),
  listEggLotMovements: vi.fn(),
  recordEggLotMovement: vi.fn(),
  getFlock: vi.fn(),
  getCustomer: vi.fn(),
}));

const mockGetStock = vi.mocked(getStock);
const mockListEggLots = vi.mocked(listEggLots);
const mockListEggLotMovements = vi.mocked(listEggLotMovements);
const mockRecordEggLotMovement = vi.mocked(recordEggLotMovement);

// The screen role-gates the write-off action through the real AuthProvider, so
// every render seeds a token; OWNER for the existing read-path tests (gating
// is asserted separately below).
const OWNER = { sub: "u1", role: "Admin" };
const WORKER = { sub: "u2" }; // no role claim — a plain Worker

const render = (ui: ReactNode, token: Record<string, unknown> = OWNER) =>
  renderWithProviders(ui, { token });

const ROWS: StockRow[] = [
  { eggGradeId: "g1", gradeName: "Grade A", available: 100, restricted: 0 },
  { eggGradeId: "g2", gradeName: "Grade B", available: 50, restricted: 5 },
];

const LOTS: EggLotRow[] = [
  { id: "lot1", eggGradeId: "g1", productionDate: "2026-07-01", quantityProduced: 120, quantityAvailable: 99, restrictedUntil: null, dailyEntryId: "de1" },
];

const MOVEMENTS: EggMovementRow[] = [
  { id: "mv1", movementType: "Production", quantityDelta: 120, referenceType: "DailyEntry", referenceId: "de1", reason: null, createdAtUtc: "2026-07-01T08:00:00Z" },
];

beforeEach(() => {
  mockGetStock.mockReset();
  mockListEggLots.mockReset();
  mockListEggLotMovements.mockReset();
  mockRecordEggLotMovement.mockReset();
});

describe("StockPage", () => {
  it("shows a loading state until the stock request resolves", async () => {
    let resolve!: (rows: StockRow[]) => void;
    mockGetStock.mockReturnValue(new Promise<StockRow[]>((r) => (resolve = r)));
    render(<StockPage />);
    expect(screen.getByText(/Loading/)).toBeInTheDocument();
    resolve([]); // settle so the pending fetch doesn't dangle past the test
    await screen.findByText(/No stock yet/);
  });

  // The error-branch render (getStock rejects → "Could not load stock. Is the
  // API up?") is not asserted here: in this Vitest 3.2.7 + React 19.1 stack, a
  // rejection the component *does* handle (its own `.catch` → setError) is still
  // flagged as unhandled through an internal promise the test can't reach — a
  // documented interaction (vitest-dev/vitest #7940, #5796). Every reviewer-
  // suggested workaround (pending-promise + reject-in-act; a scoped no-op
  // `.catch`) was tried and still tripped the detector. The error path itself is
  // a fixed message on any getStock rejection; the fetch client's error + refresh
  // transport is covered directly in api/client.test.ts (PR #111).

  it("shows the empty-state hint when there is no stock", async () => {
    mockGetStock.mockResolvedValue([]);
    render(<StockPage />);
    expect(await screen.findByText(/No stock yet/)).toBeInTheDocument();
  });

  it("renders each grade and sums available eggs across grades", async () => {
    mockGetStock.mockResolvedValue(ROWS);
    render(<StockPage />);

    expect(await screen.findByText("Grade A")).toBeInTheDocument();
    // Scope the restricted-count assertion to Grade B's row so it pins that
    // cell, not just "some 5 rendered somewhere".
    const gradeBRow = screen.getByRole("row", { name: /Grade B\b/ });
    expect(within(gradeBRow).getByText("5")).toBeInTheDocument();

    // 100 + 50 = 150 across 2 grades — the client-side reduce.
    expect(
      screen.getByText(
        (_, el) => el?.tagName === "P" && /^150 eggs available across 2 grade\(s\)\./.test(el.textContent ?? ""),
      ),
    ).toBeInTheDocument();
  });

  it("renders the grade table as a table.data scroller (the scroll-cue hook, #150)", async () => {
    // The mobile scroll-shadow affordance keys entirely off `table.data` in the
    // stylesheet (no JS, no wrapper), so the class IS the contract: drop it in a
    // refactor and the last-column-clipped cue silently disappears. jsdom can't
    // render the gradient, but it can guard the hook the CSS depends on.
    mockGetStock.mockResolvedValue(ROWS);
    const { container } = render(<StockPage />);
    await screen.findByText("Grade A");
    expect(container.querySelector("table.data")).not.toBeNull();
  });
});

describe("StockPage drill-down", () => {
  async function renderWithData() {
    mockGetStock.mockResolvedValue(ROWS);
    render(<StockPage />);
    await screen.findByText("Grade A");
  }

  it("expands a grade into its lots on 'lots', scoped to that grade", async () => {
    mockListEggLots.mockResolvedValue(LOTS);
    await renderWithData();

    const gradeA = screen.getByRole("row", { name: /Grade A\b/ });
    fireEvent.click(within(gradeA).getByRole("button", { name: "lots" }));

    const lotRow = await screen.findByRole("row", { name: /07\/01\/2026/ });
    // #465: the page asks for an explicit first page rather than leaning on
    // the API's silent default.
    expect(mockListEggLots).toHaveBeenCalledWith({ gradeId: "g1", limit: 50, offset: 0 });
    expect(mockListEggLots).toHaveBeenCalledTimes(1);
    // Values scoped to the lot row — pins WHERE they render, not just that they exist.
    expect(within(lotRow).getByText("120")).toBeInTheDocument(); // quantityProduced
    expect(within(lotRow).getByText("99")).toBeInTheDocument(); // quantityAvailable
    // toggle text flips to "hide lots"; the other grade is untouched (still "lots").
    expect(within(gradeA).getByRole("button", { name: "hide lots" })).toBeInTheDocument();
    const gradeB = screen.getByRole("row", { name: /Grade B\b/ });
    expect(within(gradeB).getByRole("button", { name: "lots" })).toBeInTheDocument();
  });

  it("shows the empty-lots hint when a grade has no lots", async () => {
    mockListEggLots.mockResolvedValue([]);
    await renderWithData();
    const gradeB = screen.getByRole("row", { name: /Grade B\b/ });
    fireEvent.click(within(gradeB).getByRole("button", { name: "lots" }));
    expect(await screen.findByText(/No lots for this grade yet/)).toBeInTheDocument();
    expect(mockListEggLots).toHaveBeenCalledWith({ gradeId: "g2", limit: 50, offset: 0 });
  });

  // #655 — the date range is this section's own filter: a zero-lot result
  // AFTER narrowing it is "filtered to nothing" (offer Clear filters), not
  // the same truly-empty state the unfiltered case above asserts.
  it("offers Clear filters when a date-narrowed grade has no lots in range", async () => {
    mockListEggLots.mockResolvedValue(LOTS);
    await renderWithData();
    const gradeA = screen.getByRole("row", { name: /Grade A\b/ });
    fireEvent.click(within(gradeA).getByRole("button", { name: "lots" }));
    await screen.findByRole("row", { name: /07\/01\/2026/ });

    mockListEggLots.mockResolvedValue([]);
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-01-01" } });
    await waitFor(() => expect(mockListEggLots).toHaveBeenCalledWith({ gradeId: "g1", from: "2026-01-01", limit: 50, offset: 0 }));
    expect(await screen.findByText(/No lots match/)).toBeInTheDocument();

    mockListEggLots.mockResolvedValue(LOTS);
    fireEvent.click(screen.getByRole("button", { name: "Clear filters" }));
    await screen.findByRole("row", { name: /07\/01\/2026/ });
    expect(mockListEggLots).toHaveBeenLastCalledWith({ gradeId: "g1", limit: 50, offset: 0 });
  });

  it("collapses the lots again on 'hide lots'", async () => {
    mockListEggLots.mockResolvedValue(LOTS);
    await renderWithData();
    const gradeA = screen.getByRole("row", { name: /Grade A\b/ });
    fireEvent.click(within(gradeA).getByRole("button", { name: "lots" }));
    await screen.findByText("07/01/2026");
    fireEvent.click(within(gradeA).getByRole("button", { name: "hide lots" }));
    expect(screen.queryByText("07/01/2026")).not.toBeInTheDocument();
  });

  it("expands a lot into its movement ledger on 'history'", async () => {
    mockListEggLots.mockResolvedValue(LOTS);
    mockListEggLotMovements.mockResolvedValue(MOVEMENTS);
    await renderWithData();

    fireEvent.click(within(screen.getByRole("row", { name: /Grade A\b/ })).getByRole("button", { name: "lots" }));
    await screen.findByText("07/01/2026");
    // the lot row's history button
    const lotRow = screen.getByRole("row", { name: /07\/01\/2026/ });
    fireEvent.click(within(lotRow).getByRole("button", { name: "history" }));

    const mvRow = await screen.findByRole("row", { name: /Production/ });
    expect(mockListEggLotMovements).toHaveBeenCalledWith("lot1");
    expect(within(mvRow).getByText("+120")).toBeInTheDocument(); // signed positive delta, scoped

    // collapse: 'hide history' removes the ledger
    fireEvent.click(within(lotRow).getByRole("button", { name: "hide history" }));
    expect(screen.queryByText("Production")).not.toBeInTheDocument();
  });

  it("surfaces an error if the movement request fails", async () => {
    mockListEggLots.mockResolvedValue(LOTS);
    mockListEggLotMovements.mockRejectedValue(new Error("movements down"));
    await renderWithData();
    fireEvent.click(within(screen.getByRole("row", { name: /Grade A\b/ })).getByRole("button", { name: "lots" }));
    const lotRow = await screen.findByRole("row", { name: /07\/01\/2026/ });
    fireEvent.click(within(lotRow).getByRole("button", { name: "history" }));
    expect(await screen.findByText(/Could not load the lot's movements/)).toBeInTheDocument();
  });

  it("surfaces an error if the lots request fails, without unmounting the grade table", async () => {
    // toggleGrade awaits inside try/catch (not a mount-effect chain), so a lazily
    // created rejection is awaited immediately — no dangling unhandled promise.
    mockListEggLots.mockRejectedValue(new Error("lots down"));
    await renderWithData();
    fireEvent.click(within(screen.getByRole("row", { name: /Grade A\b/ })).getByRole("button", { name: "lots" }));
    expect(await screen.findByText(/Could not load the grade's lots/)).toBeInTheDocument();
    expect(screen.getByText("Grade A")).toBeInTheDocument(); // table still there
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 18, batch B3 — the last B3 screen)
// ---------------------------------------------------------------------------

// The tests run under the English catalog, so asserting an English string
// would prove nothing — a still-hardcoded literal renders identically
// (CONTRIBUTING-i18n.md's fallback trap). Swap the catalog value at runtime
// instead, the same i18n.addResource technique the other batches use, so each
// marker only renders if the screen actually reads the catalog rather than a
// literal that happens to still match it.
describe("StockPage i18n wiring (#182, Task 18)", () => {
  function withOverride(ns: string, key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", ns, key) as string;
    i18n.addResource("en", ns, key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", ns, key, original);
    });
  }

  // #493 — first introduced on this page (the other five screens share
  // common:recordHistory.viewHistoryLink, marker-tested on FlocksPage).
  it("reads the adjustment-history link label from the common catalog, not a hardcoded literal", async () => {
    await withOverride("common", "recordHistory.viewAdjustmentHistoryLink", "ADJUSTMENT-MARKER", async () => {
      mockGetStock.mockResolvedValue(ROWS);
      mockListEggLots.mockResolvedValue(LOTS);
      render(<StockPage />);
      await screen.findByText("Grade A");
      fireEvent.click(within(screen.getByRole("row", { name: /Grade A\b/ })).getByRole("button", { name: "lots" }));
      const lotRow = await screen.findByRole("row", { name: /07\/01\/2026/ });
      expect(within(lotRow).getByRole("link", { name: "ADJUSTMENT-MARKER" })).toBeInTheDocument();
      expect(within(lotRow).queryByRole("link", { name: "Adjustment history" })).not.toBeInTheDocument();
    });
  });

  it("reads the heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("stock", "title", "TITLE-MARKER", async () => {
      mockGetStock.mockResolvedValue(ROWS);
      render(<StockPage />);
      expect(await screen.findByRole("heading", { name: "TITLE-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Stock" })).not.toBeInTheDocument();
    });
  });

  it("reads the empty-state message from the catalog, not a hardcoded literal", async () => {
    await withOverride("stock", "noStockMessage", "NO-STOCK-MARKER", async () => {
      mockGetStock.mockResolvedValue([]);
      render(<StockPage />);
      expect(await screen.findByText("NO-STOCK-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/No stock yet/)).not.toBeInTheDocument();
    });
  });

  it("reads the lots-toggle button label from the catalog, not a hardcoded literal", async () => {
    await withOverride("stock", "lotsButton", "LOTS-MARKER", async () => {
      mockGetStock.mockResolvedValue(ROWS);
      render(<StockPage />);
      const gradeA = await screen.findByRole("row", { name: /Grade A\b/ });
      expect(within(gradeA).getByRole("button", { name: "LOTS-MARKER" })).toBeInTheDocument();
      expect(within(gradeA).queryByRole("button", { name: "lots" })).not.toBeInTheDocument();
    });
  });

  // Proves the muted summary reads BOTH totals (the client-side reduce over
  // `rows`) from the catalog template — a hardcoded literal would fail to pick
  // up the marker text even though the numbers would still look right.
  it("interpolates the stock totals into the summary message from the catalog", async () => {
    await withOverride(
      "stock", "totalAvailableMessage", "TOTAL-MARKER {{available}} of {{grades}} MARKER-END",
      async () => {
        mockGetStock.mockResolvedValue(ROWS);
        render(<StockPage />);
        expect(await screen.findByText("TOTAL-MARKER 150 of 2 MARKER-END")).toBeInTheDocument();
        expect(screen.queryByText(/eggs available across/)).not.toBeInTheDocument();
      },
    );
  });

  // Proves the ledger's Type cell reads the enum label from the catalog (via
  // stockMovementLabel), not a hardcoded literal or the raw wire value —
  // MOVEMENTS' movementType is "Production".
  it("reads the movement-type enum label from the catalog for the ledger cell", async () => {
    await withOverride("enums", "stockMovement.Production", "PRODUCTION-MARKER", async () => {
      mockGetStock.mockResolvedValue(ROWS);
      mockListEggLots.mockResolvedValue(LOTS);
      mockListEggLotMovements.mockResolvedValue(MOVEMENTS);
      render(<StockPage />);
      await screen.findByText("Grade A");
      fireEvent.click(within(screen.getByRole("row", { name: /Grade A\b/ })).getByRole("button", { name: "lots" }));
      const lotRow = await screen.findByRole("row", { name: /07\/01\/2026/ });
      fireEvent.click(within(lotRow).getByRole("button", { name: "history" }));
      expect(await screen.findByText("PRODUCTION-MARKER")).toBeInTheDocument();
      expect(screen.queryByText("Production")).not.toBeInTheDocument();
    });
  });

  it("reads the load-lots-failed message from the catalog, not a hardcoded literal", async () => {
    await withOverride("stock", "loadLotsFailed", "LOAD-LOTS-MARKER", async () => {
      mockGetStock.mockResolvedValue(ROWS);
      mockListEggLots.mockRejectedValue(new Error("lots down"));
      render(<StockPage />);
      await screen.findByText("Grade A");
      fireEvent.click(within(screen.getByRole("row", { name: /Grade A\b/ })).getByRole("button", { name: "lots" }));
      expect(await screen.findByText("LOAD-LOTS-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/Could not load the grade's lots/)).not.toBeInTheDocument();
    });
  });
});

// ---------------------------------------------------------------------------
// #406 — per-lot write-off / reconciliation. Admin-gated dialog: type,
// quantity (NumberField), direction (Reconciliation only), required reason.
// UI gating is cosmetic (#73/#103) — the API enforces the role separately.
// ---------------------------------------------------------------------------
describe("StockPage write-off (#406)", () => {
  const RESULT = {
    movementId: "wo1", eggLotId: "lot1", movementType: "Discard",
    quantityDelta: -7, reason: "dropped a tray",
    createdAtUtc: "2026-08-08T10:00:00Z", quantityAvailable: 92, version: 2,
  };

  async function openLotRow(token: Record<string, unknown> = OWNER) {
    mockGetStock.mockResolvedValue(ROWS);
    mockListEggLots.mockResolvedValue(LOTS);
    render(<StockPage />, token);
    await screen.findByText("Grade A");
    fireEvent.click(within(screen.getByRole("row", { name: /Grade A\b/ })).getByRole("button", { name: "lots" }));
    return await screen.findByRole("row", { name: /07\/01\/2026/ });
  }

  function fillAndSubmit({ qty = "7", reason = "dropped a tray" }: { qty?: string; reason?: string } = {}) {
    const dialog = screen.getByRole("dialog");
    fireEvent.change(within(dialog).getByRole("spinbutton"), { target: { value: qty } });
    if (reason) fireEvent.change(within(dialog).getByLabelText(/Reason/), { target: { value: reason } });
    fireEvent.click(within(dialog).getByRole("button", { name: /Record/ }));
    return dialog;
  }

  it("shows the write-off action to an admin on each lot row", async () => {
    const lotRow = await openLotRow();
    expect(within(lotRow).getByRole("button", { name: "write off" })).toBeInTheDocument();
  });

  it("hides the write-off action from a worker and explains why", async () => {
    const lotRow = await openLotRow(WORKER);
    expect(within(lotRow).queryByRole("button", { name: "write off" })).not.toBeInTheDocument();
    expect(screen.getByText(/need an Owner or Manager/)).toBeInTheDocument();
  });

  it("submits a discard as a negative delta and refreshes the balances", async () => {
    mockRecordEggLotMovement.mockResolvedValue(RESULT);
    const lotRow = await openLotRow();

    fireEvent.click(within(lotRow).getByRole("button", { name: "write off" }));
    fillAndSubmit();

    await screen.findByText(/92 now available/);
    expect(mockRecordEggLotMovement).toHaveBeenCalledWith(
      "lot1",
      { movementType: "Discard", quantityDelta: -7, reason: "dropped a tray" },
      expect.any(String));
    // Balances refetched — the by-grade totals and the open grade's lots.
    expect(mockGetStock).toHaveBeenCalledTimes(2);
    expect(mockListEggLots).toHaveBeenCalledTimes(2);
  });

  it("shows the resulting balance before submitting", async () => {
    const lotRow = await openLotRow();
    fireEvent.click(within(lotRow).getByRole("button", { name: "write off" }));
    const dialog = screen.getByRole("dialog");
    fireEvent.change(within(dialog).getByRole("spinbutton"), { target: { value: "7" } });
    // Lot has 99 available; writing off 7 leaves 92.
    expect(within(dialog).getByText(/99 → 92/)).toBeInTheDocument();
  });

  it("sends a positive delta for a reconciliation recount that found eggs", async () => {
    mockRecordEggLotMovement.mockResolvedValue(
      { ...RESULT, movementType: "Reconciliation", quantityDelta: 7, quantityAvailable: 106 });
    const lotRow = await openLotRow();

    fireEvent.click(within(lotRow).getByRole("button", { name: "write off" }));
    const dialog = screen.getByRole("dialog");
    fireEvent.change(within(dialog).getByLabelText(/Type/), { target: { value: "Reconciliation" } });
    fireEvent.change(within(dialog).getByLabelText(/Direction/), { target: { value: "add" } });
    fillAndSubmit();

    await screen.findByText(/106 now available/);
    expect(mockRecordEggLotMovement).toHaveBeenCalledWith(
      "lot1",
      { movementType: "Reconciliation", quantityDelta: 7, reason: "dropped a tray" },
      expect.any(String));
  });

  it("offers the direction choice only for a reconciliation", async () => {
    const lotRow = await openLotRow();
    fireEvent.click(within(lotRow).getByRole("button", { name: "write off" }));
    const dialog = screen.getByRole("dialog");
    expect(within(dialog).queryByLabelText(/Direction/)).not.toBeInTheDocument();
    fireEvent.change(within(dialog).getByLabelText(/Type/), { target: { value: "Reconciliation" } });
    expect(within(dialog).getByLabelText(/Direction/)).toBeInTheDocument();
  });

  it("blocks a submit without a reason", async () => {
    const lotRow = await openLotRow();
    fireEvent.click(within(lotRow).getByRole("button", { name: "write off" }));
    fillAndSubmit({ reason: "" });
    expect(mockRecordEggLotMovement).not.toHaveBeenCalled();
  });

  it("blocks a submit of zero eggs", async () => {
    const lotRow = await openLotRow();
    fireEvent.click(within(lotRow).getByRole("button", { name: "write off" }));
    fillAndSubmit({ qty: "0" });
    expect(mockRecordEggLotMovement).not.toHaveBeenCalled();
  });

  it("rotates the key once the write succeeds, even if the refresh after it fails", async () => {
    // Codex review: the write is durable the moment the server answers 200 —
    // keeping the key while the dialog is editable would hash-conflict a
    // later submit with edited values. Only the VIEW is stale on a failed
    // refresh, and that surfaces as a page-level load error.
    mockRecordEggLotMovement.mockResolvedValue(RESULT);
    const lotRow = await openLotRow();
    mockGetStock.mockRejectedValueOnce(new Error("refresh down"));

    fireEvent.click(within(lotRow).getByRole("button", { name: "write off" }));
    fillAndSubmit();
    // The write landed: success message, dialog closed, stale-view error shown.
    await screen.findByText(/92 now available/);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.getByText(/Could not load stock/)).toBeInTheDocument();

    // A second write-off uses a FRESH key — the first is spent.
    fireEvent.click(within(lotRow).getByRole("button", { name: "write off" }));
    fillAndSubmit();
    await screen.findByText(/92 now available/);
    expect(mockRecordEggLotMovement).toHaveBeenCalledTimes(2);
    const [, , firstKey] = mockRecordEggLotMovement.mock.calls[0];
    const [, , secondKey] = mockRecordEggLotMovement.mock.calls[1];
    expect(firstKey).not.toBe(secondKey);
  });

  it("keeps the same idempotency key across a retry after a failure", async () => {
    mockRecordEggLotMovement
      .mockRejectedValueOnce(new Error("network down"))
      .mockResolvedValueOnce(RESULT);
    const lotRow = await openLotRow();

    fireEvent.click(within(lotRow).getByRole("button", { name: "write off" }));
    fillAndSubmit();
    await screen.findByText(/network down/);
    fillAndSubmit();
    await screen.findByText(/92 now available/);

    expect(mockRecordEggLotMovement).toHaveBeenCalledTimes(2);
    const [, , firstKey] = mockRecordEggLotMovement.mock.calls[0];
    const [, , secondKey] = mockRecordEggLotMovement.mock.calls[1];
    expect(firstKey).toBe(secondKey); // a retry replays, never duplicates
  });

  it("reads the write-off button label from the catalog, not a hardcoded literal", async () => {
    const original = i18n.getResource("en", "stock", "writeOffButton") as string;
    i18n.addResource("en", "stock", "writeOffButton", "WRITE-OFF-MARKER");
    try {
      const lotRow = await openLotRow();
      expect(within(lotRow).getByRole("button", { name: "WRITE-OFF-MARKER" })).toBeInTheDocument();
      expect(within(lotRow).queryByRole("button", { name: "write off" })).not.toBeInTheDocument();
    } finally {
      i18n.addResource("en", "stock", "writeOffButton", original);
    }
  });
});

// #479 — this screen already kept the write-off dialog's own failures in a
// separate hand-rolled `dialogError` state, so converting it to the shared
// per-place store is for uniformity, not a bug fix. The one gap the shared
// hook closes for free: muting a late failure from an attempt abandoned
// mid-flight, so it can't land in a dialog reopened after it.
describe("StockPage error placement (#479)", () => {
  async function openLotRow() {
    mockGetStock.mockResolvedValue(ROWS);
    mockListEggLots.mockResolvedValue(LOTS);
    render(<StockPage />);
    await screen.findByText("Grade A");
    fireEvent.click(within(screen.getByRole("row", { name: /Grade A\b/ })).getByRole("button", { name: "lots" }));
    return await screen.findByRole("row", { name: /07\/01\/2026/ });
  }

  function fillAndSubmit({ qty = "7", reason = "dropped a tray" }: { qty?: string; reason?: string } = {}) {
    const dlg = screen.getByRole("dialog");
    fireEvent.change(within(dlg).getByRole("spinbutton"), { target: { value: qty } });
    if (reason) fireEvent.change(within(dlg).getByLabelText(/Reason/), { target: { value: reason } });
    fireEvent.click(within(dlg).getByRole("button", { name: /Record/ }));
    return dlg;
  }

  async function openLotRowAndWriteOff() {
    const lotRow = await openLotRow();
    fireEvent.click(within(lotRow).getByRole("button", { name: "write off" }));
    return lotRow;
  }

  it("shows a failed write-off inside the dialog, not on the page", async () => {
    mockRecordEggLotMovement.mockRejectedValue(new Error("network down"));
    await openLotRowAndWriteOff();
    const dlg = fillAndSubmit();

    await screen.findByText("network down");

    expect(within(dlg).getByText("network down")).toBeInTheDocument();
    // Exactly one copy: the page must not render the dialog's message too.
    expect(screen.getAllByText("network down")).toHaveLength(1);
  });

  // Displacement: the write-off scope is fixed ("write-off"), and a second
  // lot's write-off can begin without the first being dismissed — the row
  // buttons behind the backdrop are reachable to a screen reader's virtual
  // cursor (#480). Without an abandon on the switch, lot A's failed write-off
  // renders under lot B's date in the dialog title (pi review of #491).
  it("does not carry one lot's failed write-off into another lot's dialog", async () => {
    const LOT_2: EggLotRow = { ...LOTS[0], id: "lot2", productionDate: "2026-07-02", quantityAvailable: 50 };
    mockGetStock.mockResolvedValue(ROWS);
    mockListEggLots.mockResolvedValue([LOTS[0], LOT_2]);
    mockRecordEggLotMovement.mockRejectedValue(new Error("network down"));
    render(<StockPage />);
    await screen.findByText("Grade A");
    fireEvent.click(within(screen.getByRole("row", { name: /Grade A\b/ })).getByRole("button", { name: "lots" }));
    const lotRow1 = await screen.findByRole("row", { name: /07\/01\/2026/ });
    fireEvent.click(within(lotRow1).getByRole("button", { name: "write off" }));
    fillAndSubmit();
    await screen.findByText("network down");

    const lotRow2 = screen.getByRole("row", { name: /07\/02\/2026/ });
    fireEvent.click(within(lotRow2).getByRole("button", { name: "write off" }));
    // The dialog really swapped lots — its title names the new lot's date.
    expect(screen.getByRole("dialog")).toHaveAccessibleName(/07\/02\/2026/); // farm-formatted (#650)
    expect(screen.queryByText("network down")).not.toBeInTheDocument();
  });

  // The write-off trigger has no `disabled={busy}` gate, so lot A's submit
  // can still be in flight when lot B's write-off is opened over it. A
  // SUCCESSFUL settle for A must not close B's now-displayed dialog or claim
  // a success about A while B's form is what the admin is looking at
  // (adversarial review of #491).
  it("does not close another lot's dialog when a displaced write-off succeeds", async () => {
    const LOT_2: EggLotRow = { ...LOTS[0], id: "lot2", productionDate: "2026-07-02", quantityAvailable: 50 };
    mockGetStock.mockResolvedValue(ROWS);
    mockListEggLots.mockResolvedValue([LOTS[0], LOT_2]);
    let resolveFirst!: (v: EggLotMovementResult) => void;
    mockRecordEggLotMovement.mockReturnValueOnce(
      new Promise((resolve) => { resolveFirst = resolve; }));
    render(<StockPage />);
    await screen.findByText("Grade A");
    fireEvent.click(within(screen.getByRole("row", { name: /Grade A\b/ })).getByRole("button", { name: "lots" }));
    const lotRow1 = await screen.findByRole("row", { name: /07\/01\/2026/ });
    fireEvent.click(within(lotRow1).getByRole("button", { name: "write off" }));
    fillAndSubmit(); // lot A's submit is left pending

    const lotRow2 = screen.getByRole("row", { name: /07\/02\/2026/ });
    fireEvent.click(within(lotRow2).getByRole("button", { name: "write off" }));
    expect(screen.getByRole("dialog")).toHaveAccessibleName(/07\/02\/2026/); // farm-formatted (#650)

    await act(async () => {
      resolveFirst({
        movementId: "mv-new", eggLotId: "lot1", movementType: "Discard",
        quantityDelta: -7, reason: "dropped a tray", createdAtUtc: "2026-07-01T10:00:00Z",
        quantityAvailable: 42, version: 2,
      });
    });

    // Still open, still lot B — a success about lot A did not sweep it away.
    // (Lot A's row correctly patches to 42 available either way — that is
    // the write landing, not the bug; the bug is the dialog closing.)
    expect(screen.getByRole("dialog")).toHaveAccessibleName(/07\/02\/2026/); // farm-formatted (#650)
    expect(screen.queryByText(i18n.t("stock:writeOffRecordedMessage", { available: 42 }))).not.toBeInTheDocument();
  });

  it("keeps a movements-load failure out of the open write-off dialog", async () => {
    mockListEggLotMovements.mockRejectedValue(new Error("boom"));
    const lotRow = await openLotRowAndWriteOff();
    const dlg = screen.getByRole("dialog");

    await act(async () => {
      fireEvent.click(within(lotRow).getByRole("button", { name: "history" }));
    });

    const message = i18n.t("stock:loadMovementsFailed");
    expect(within(dlg).queryByText(message)).not.toBeInTheDocument();
    expect(screen.getByText(message)).toBeInTheDocument();
  });

  it("keeps a page failure while the write-off dialog opens and its own submit fails", async () => {
    mockListEggLotMovements.mockRejectedValue(new Error("boom"));
    const lotRow = await openLotRow();
    await act(async () => {
      fireEvent.click(within(lotRow).getByRole("button", { name: "history" }));
    });
    const pageFailure = i18n.t("stock:loadMovementsFailed");
    expect(screen.getByText(pageFailure)).toBeInTheDocument();

    mockRecordEggLotMovement.mockRejectedValue(new Error("network down"));
    fireEvent.click(within(lotRow).getByRole("button", { name: "write off" }));
    const dlg = fillAndSubmit();

    await screen.findByText("network down");
    expect(within(dlg).getByText("network down")).toBeInTheDocument();
    expect(screen.getByText(pageFailure)).toBeInTheDocument();
  });

  // The specific gap this conversion closes: the old `dialogError` only reset
  // on the NEXT open, which could not stop an already-in-flight request's late
  // failure from landing in a dialog reopened before that request settled.
  // `abandon` mutes the attempt itself, not just the visible slot.
  it("mutes a write-off's late failure once its dialog is abandoned mid-flight", async () => {
    let rejectFirst!: (err: unknown) => void;
    mockRecordEggLotMovement.mockReturnValueOnce(
      new Promise((_resolve, reject) => { rejectFirst = reject; }));
    const lotRow = await openLotRowAndWriteOff();
    fillAndSubmit(); // left pending — the promise above never settles yet

    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();

    // Reopen the same dialog — a new session, nothing to show yet.
    fireEvent.click(within(lotRow).getByRole("button", { name: "write off" }));
    const reopened = screen.getByRole("dialog");
    expect(within(reopened).queryByRole("alert")).not.toBeInTheDocument();

    // The abandoned attempt's failure lands late.
    await act(async () => {
      rejectFirst(new Error("late failure from the abandoned attempt"));
    });

    expect(screen.queryByText(/late failure from the abandoned attempt/)).not.toBeInTheDocument();
  });
});

// #465 — the drill-down used to show only the API's newest-50 default page,
// making older lots (the very ones a write-off targets) unreachable. Now the
// panel pages ("load more") and filters by production date, both server-side.
describe("StockPage lot paging + date filter (#465)", () => {
  const PAGE = 50;

  function makeLots(count: number, startDay = 1, month = "07"): EggLotRow[] {
    return Array.from({ length: count }, (_, i) => ({
      id: `lot-${month}-${i}`,
      eggGradeId: "g1",
      productionDate: `2026-${month}-${String(startDay + (i % 28)).padStart(2, "0")}`,
      quantityProduced: 100 + i,
      quantityAvailable: 90,
      restrictedUntil: null,
      dailyEntryId: null,
    }));
  }

  async function expandGradeA() {
    mockGetStock.mockResolvedValue(ROWS);
    render(<StockPage />);
    await screen.findByText("Grade A");
    fireEvent.click(within(screen.getByRole("row", { name: /Grade A\b/ })).getByRole("button", { name: "lots" }));
    await screen.findByText(/^Lots$/);
  }

  it("requests the first page explicitly and appends the next on 'load more'", async () => {
    mockListEggLots
      .mockResolvedValueOnce(makeLots(PAGE))
      .mockResolvedValueOnce([{ ...LOTS[0], id: "old-lot", productionDate: "2026-06-01" }]);
    await expandGradeA();

    expect(mockListEggLots).toHaveBeenCalledWith({ gradeId: "g1", limit: PAGE, offset: 0 });
    const loadMore = await screen.findByRole("button", { name: "load more" });
    fireEvent.click(loadMore);

    // The older lot appears BELOW the still-present first page (dates repeat
    // across the 50 generated lots, so "at least one" is the right shape).
    expect(await screen.findByText("06/01/2026")).toBeInTheDocument();
    expect(screen.getAllByText("07/01/2026").length).toBeGreaterThan(0);
    expect(mockListEggLots).toHaveBeenLastCalledWith({ gradeId: "g1", limit: PAGE, offset: PAGE });
    // The second page was short — nothing further to load.
    expect(screen.queryByRole("button", { name: "load more" })).not.toBeInTheDocument();
  });

  it("offers no 'load more' when the first page is short", async () => {
    mockListEggLots.mockResolvedValue(LOTS);
    await expandGradeA();
    expect(screen.queryByRole("button", { name: "load more" })).not.toBeInTheDocument();
  });

  it("filters by production date server-side and restarts paging from the top", async () => {
    mockListEggLots
      .mockResolvedValueOnce(makeLots(PAGE))
      .mockResolvedValueOnce([{ ...LOTS[0], id: "hit", productionDate: "2026-07-02" }]);
    await expandGradeA();

    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-02" } });
    expect(await screen.findByText("07/02/2026")).toBeInTheDocument();
    expect(mockListEggLots).toHaveBeenLastCalledWith(
      { gradeId: "g1", from: "2026-07-02", to: undefined, limit: PAGE, offset: 0 });
    // The filtered view REPLACES the unfiltered pages.
    expect(screen.queryByText("07/05/2026")).not.toBeInTheDocument();
  });

  it("clears the filter when switching to another grade", async () => {
    mockListEggLots.mockResolvedValue(LOTS);
    await expandGradeA();
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-02" } });
    await screen.findByText(/^Lots$/);

    fireEvent.click(within(screen.getByRole("row", { name: /Grade B\b/ })).getByRole("button", { name: "lots" }));
    await screen.findByText(/^Lots$/);
    expect(mockListEggLots).toHaveBeenLastCalledWith({ gradeId: "g2", limit: PAGE, offset: 0 });
    expect(screen.getByLabelText("From")).toHaveValue("");
  });

  it("re-fetches the whole loaded window after a write-off, not just page one", async () => {
    // Two pages loaded (51 rows). A refresh that only re-fetched the default
    // page would silently collapse the window back to 50 mid-correction.
    mockRecordEggLotMovement.mockResolvedValue({
      movementId: "wo1", eggLotId: "lot-07-0", movementType: "Discard",
      quantityDelta: -7, reason: "dropped a tray",
      createdAtUtc: "2026-08-08T10:00:00Z", quantityAvailable: 83, version: 2,
    });
    mockListEggLots
      .mockResolvedValueOnce(makeLots(PAGE))
      .mockResolvedValueOnce([{ ...LOTS[0], id: "old-lot", productionDate: "2026-06-01" }])
      .mockResolvedValue(makeLots(PAGE));
    await expandGradeA();
    fireEvent.click(await screen.findByRole("button", { name: "load more" }));
    await screen.findByText("06/01/2026");

    const lotRow = screen.getAllByRole("row", { name: /07\/01\/2026/ })[0];
    fireEvent.click(within(lotRow).getByRole("button", { name: "write off" }));
    const dialog = screen.getByRole("dialog");
    fireEvent.change(within(dialog).getByRole("spinbutton"), { target: { value: "7" } });
    fireEvent.change(within(dialog).getByLabelText(/Reason/), { target: { value: "dropped a tray" } });
    fireEvent.click(within(dialog).getByRole("button", { name: /Record/ }));
    await screen.findByText(/83 now available/);

    // Refresh walked the window page-by-page: offsets 0 and 50 again.
    const refreshCalls = mockListEggLots.mock.calls.slice(2);
    expect(refreshCalls.map(([args]) => args?.offset)).toEqual([0, PAGE]);
  });

  it("ignores a stale response that settles after a newer filter's", async () => {
    // From then To in quick succession = two in-flight requests. If the first
    // (broader) response lands last, it must NOT overwrite the narrower view.
    const pending: Array<(rows: EggLotRow[]) => void> = [];
    mockListEggLots
      .mockResolvedValueOnce(makeLots(3))
      .mockImplementation(() => new Promise<EggLotRow[]>((r) => pending.push(r)));
    await expandGradeA();

    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-02" } });
    fireEvent.change(screen.getByLabelText("To"), { target: { value: "2026-07-03" } });
    expect(pending).toHaveLength(2);

    // Newer (From+To) settles first with the narrow hit…
    pending[1]([{ ...LOTS[0], id: "hit", productionDate: "2026-07-02" }]);
    await screen.findByText("07/02/2026");
    // …then the stale From-only response arrives late and must be dropped.
    // act() flushes the state update the settle would trigger — without it a
    // buggy overwrite renders after the assertions and the test lies green.
    await act(async () => {
      pending[0](makeLots(40));
    });
    expect(screen.getByText("07/02/2026")).toBeInTheDocument();
    expect(screen.queryByText("07/15/2026")).not.toBeInTheDocument();
  });

  it("rolls the filter inputs back when the filter request fails (codex round 2)", async () => {
    // The inputs update optimistically; on failure the rows keep showing the
    // OLD window, so the inputs must return to describing it.
    mockListEggLots
      .mockResolvedValueOnce(LOTS)
      .mockRejectedValueOnce(new Error("boom"));
    await expandGradeA();

    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-02" } });
    await screen.findByText(/Could not load the grade's lots/);
    expect(screen.getByLabelText("From")).toHaveValue("");
    expect(screen.getByText("07/01/2026")).toBeInTheDocument();
  });

  it("abandons the post-write ledger refresh once a newer ledger intent exists (codex round 10)", async () => {
    // Write-off submitted with lot A's ledger open; while the refresh hangs
    // on getStock, the user opens lot B's History. The refresh's submit-time
    // closure still says openLot === A — it must consult the ledger ticket
    // or it paints A's movements under B's heading.
    const lotA = { ...LOTS[0] }; // lot1, 2026-07-01
    const lotB = { ...LOTS[0], id: "lot2", productionDate: "2026-06-15" };
    mockRecordEggLotMovement.mockResolvedValue({
      movementId: "wo1", eggLotId: "lot1", movementType: "Discard",
      quantityDelta: -7, reason: "dropped a tray",
      createdAtUtc: "2026-08-08T10:00:00Z", quantityAvailable: 92, version: 2,
    });
    let releaseStock!: (rows: StockRow[]) => void;
    mockGetStock
      .mockResolvedValueOnce(ROWS)
      .mockReturnValueOnce(new Promise<StockRow[]>((r) => (releaseStock = r)));
    mockListEggLots.mockResolvedValue([lotA, lotB]);
    mockListEggLotMovements.mockImplementation((id: string) =>
      Promise.resolve([{ ...MOVEMENTS[0], id: `mv-${id}`, reason: `marker-${id}` }]));
    await expandGradeA();

    const rowA = screen.getByRole("row", { name: /07\/01\/2026/ });
    fireEvent.click(within(rowA).getByRole("button", { name: "history" }));
    await screen.findByText("marker-lot1");

    fireEvent.click(within(rowA).getByRole("button", { name: "write off" }));
    const dialog = screen.getByRole("dialog");
    fireEvent.change(within(dialog).getByRole("spinbutton"), { target: { value: "7" } });
    fireEvent.change(within(dialog).getByLabelText(/Reason/), { target: { value: "dropped a tray" } });
    fireEvent.click(within(dialog).getByRole("button", { name: /Record/ }));

    // Refresh hangs on getStock; the user switches the ledger to lot B.
    const rowB = screen.getByRole("row", { name: /06\/15\/2026/ });
    fireEvent.click(within(rowB).getByRole("button", { name: "history" }));
    await screen.findByText("marker-lot2");

    await act(async () => {
      releaseStock(ROWS);
    });
    await screen.findByText(/92 now available/);
    expect(screen.getByText("marker-lot2")).toBeInTheDocument();
    expect(screen.queryByText("marker-lot1")).not.toBeInTheDocument();
  });

  it("discards a superseded ledger refresh's failure instead of raising the banner (codex round 11)", async () => {
    // Mirror of round 7 for the ledger: the post-write movement fetch
    // rejects only after a newer History load landed — the stale failure
    // must not surface as loadStockFailed over the healthy view.
    const lotA = { ...LOTS[0] };
    const lotB = { ...LOTS[0], id: "lot2", productionDate: "2026-06-15" };
    mockRecordEggLotMovement.mockResolvedValue({
      movementId: "wo1", eggLotId: "lot1", movementType: "Discard",
      quantityDelta: -7, reason: "dropped a tray",
      createdAtUtc: "2026-08-08T10:00:00Z", quantityAvailable: 92, version: 2,
    });
    mockGetStock.mockResolvedValue(ROWS);
    mockListEggLots.mockResolvedValue([lotA, lotB]);
    let rejectRefreshFetch!: (e: Error) => void;
    let call = 0;
    mockListEggLotMovements.mockImplementation((id: string) => {
      call += 1;
      if (call === 2) return new Promise<EggMovementRow[]>((_, rej) => (rejectRefreshFetch = rej));
      return Promise.resolve([{ ...MOVEMENTS[0], id: `mv-${id}-${call}`, reason: `marker-${id}` }]);
    });
    await expandGradeA();

    const rowA = screen.getByRole("row", { name: /07\/01\/2026/ });
    fireEvent.click(within(rowA).getByRole("button", { name: "history" }));
    await screen.findByText("marker-lot1");

    fireEvent.click(within(rowA).getByRole("button", { name: "write off" }));
    const dialog = screen.getByRole("dialog");
    fireEvent.change(within(dialog).getByRole("spinbutton"), { target: { value: "7" } });
    fireEvent.change(within(dialog).getByLabelText(/Reason/), { target: { value: "dropped a tray" } });
    fireEvent.click(within(dialog).getByRole("button", { name: /Record/ }));

    // The refresh's ledger fetch (call 2) hangs; the user opens lot B's
    // History, which lands successfully...
    await waitFor(() => expect(call).toBe(2));
    fireEvent.click(within(screen.getByRole("row", { name: /06\/15\/2026/ }))
      .getByRole("button", { name: "history" }));
    await screen.findByText("marker-lot2");

    // ...and only then does the superseded fetch reject.
    await act(async () => {
      rejectRefreshFetch(new Error("stale ledger fetch died"));
    });
    await screen.findByText(/92 now available/);
    expect(screen.queryByText(/Could not load stock/)).not.toBeInTheDocument();
    expect(screen.getByText("marker-lot2")).toBeInTheDocument();
  });

  it("clears a ledger opened DURING the filter load when the page commits (codex round 9)", async () => {
    // Inverse of round 3: History is clicked after the filter's invalidation
    // (old rows stay interactive), so it owns the newer ledger ticket — the
    // committing page must clear it again or the old lot's ledger sits under
    // a list that may not contain it.
    let releaseFilter!: (rows: EggLotRow[]) => void;
    mockListEggLotMovements.mockResolvedValue(MOVEMENTS);
    mockListEggLots
      .mockResolvedValueOnce(LOTS)
      .mockImplementationOnce(() => new Promise<EggLotRow[]>((r) => (releaseFilter = r)));
    await expandGradeA();

    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-02" } });
    fireEvent.click(screen.getByRole("button", { name: "history" }));
    await screen.findByText(/Movement ledger/);

    await act(async () => {
      releaseFilter([{ ...LOTS[0], id: "hit", productionDate: "2026-07-02" }]);
    });
    expect(screen.queryByText(/Movement ledger/)).not.toBeInTheDocument();
  });

  it("clears a ledger opened DURING a grade switch when the new grade commits (codex round 9)", async () => {
    // Same inverse order on the other replace path.
    let releaseSwitch!: (rows: EggLotRow[]) => void;
    mockListEggLotMovements.mockResolvedValue(MOVEMENTS);
    mockListEggLots
      .mockResolvedValueOnce(LOTS)
      .mockImplementationOnce(() => new Promise<EggLotRow[]>((r) => (releaseSwitch = r)));
    await expandGradeA();

    fireEvent.click(within(screen.getByRole("row", { name: /Grade B\b/ })).getByRole("button", { name: "lots" }));
    fireEvent.click(screen.getByRole("button", { name: "history" }));
    await screen.findByText(/Movement ledger/);

    await act(async () => {
      releaseSwitch([{ ...LOTS[0], id: "b-lot", eggGradeId: "g2", productionDate: "2026-03-03" }]);
    });
    expect(screen.queryByText(/Movement ledger/)).not.toBeInTheDocument();
  });

  it("re-applies the write patch when a pre-mutation GET settles after it (codex round 8)", async () => {
    // Inverse of round 5: the filter GET snapshots the OLD balance while the
    // POST is pending, but settles AFTER the patch — its setLots would
    // silently restore the stale number with no later correction.
    let releaseRecord!: (r: {
      movementId: string; eggLotId: string; movementType: string; quantityDelta: number;
      reason: string; createdAtUtc: string; quantityAvailable: number; version: number;
    }) => void;
    let releaseFilter!: (rows: EggLotRow[]) => void;
    mockRecordEggLotMovement.mockReturnValue(new Promise((r) => (releaseRecord = r)));
    mockListEggLots
      .mockResolvedValueOnce(LOTS)
      .mockImplementationOnce(() => new Promise<EggLotRow[]>((r) => (releaseFilter = r)));
    await expandGradeA();

    fireEvent.click(screen.getByRole("button", { name: "write off" }));
    const dialog = screen.getByRole("dialog");
    fireEvent.change(within(dialog).getByRole("spinbutton"), { target: { value: "7" } });
    fireEvent.change(within(dialog).getByLabelText(/Reason/), { target: { value: "dropped a tray" } });
    fireEvent.click(within(dialog).getByRole("button", { name: /Record/ }));
    // GET issued while the POST is pending — it will carry the old balance.
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-01" } });

    await act(async () => {
      releaseRecord({
        movementId: "wo1", eggLotId: "lot1", movementType: "Discard",
        quantityDelta: -7, reason: "dropped a tray",
        createdAtUtc: "2026-08-08T10:00:00Z", quantityAvailable: 92, version: 2,
      });
    });
    await screen.findByText(/92 now available/);
    // The stale GET settles last; the durable result must survive it.
    await act(async () => {
      releaseFilter([{ ...LOTS[0] }]); // pre-mutation snapshot: 99
    });
    const lotRow = screen.getByRole("row", { name: /07\/01\/2026/ });
    expect(within(lotRow).getByText("92")).toBeInTheDocument();
    expect(within(lotRow).queryByText("99")).not.toBeInTheDocument();
  });

  it("invalidates pending lot loads when the grade collapses (codex round 8)", async () => {
    // "hide lots" while a filter request hangs: its late rejection must not
    // paint loadLotsFailed under a closed panel.
    let rejectFilter!: (e: Error) => void;
    mockListEggLots
      .mockResolvedValueOnce(LOTS)
      .mockImplementationOnce(() => new Promise<EggLotRow[]>((_, rej) => (rejectFilter = rej)));
    await expandGradeA();
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-02" } });

    fireEvent.click(within(screen.getByRole("row", { name: /Grade A\b/ })).getByRole("button", { name: "hide lots" }));
    expect(screen.queryByText(/^Lots$/)).not.toBeInTheDocument();

    await act(async () => {
      rejectFilter(new Error("late boom"));
    });
    expect(screen.queryByText(/Could not load the grade's lots/)).not.toBeInTheDocument();
  });

  it("discards a superseded refresh page's failure instead of raising the error banner (codex round 7)", async () => {
    // The walk's page fetch rejects only AFTER a newer filter load took the
    // ticket and rendered successfully — that stale failure is moot and must
    // not paint loadStockFailed over the healthy current view.
    mockRecordEggLotMovement.mockResolvedValue({
      movementId: "wo1", eggLotId: "lot1", movementType: "Discard",
      quantityDelta: -7, reason: "dropped a tray",
      createdAtUtc: "2026-08-08T10:00:00Z", quantityAvailable: 92, version: 2,
    });
    let rejectWalk!: (e: Error) => void;
    mockListEggLots
      .mockResolvedValueOnce(LOTS)
      .mockImplementationOnce(() => new Promise<EggLotRow[]>((_, rej) => (rejectWalk = rej)))
      .mockResolvedValueOnce([{ ...LOTS[0], id: "hit", productionDate: "2026-07-02", quantityAvailable: 92 }]);
    await expandGradeA();

    fireEvent.click(screen.getByRole("button", { name: "write off" }));
    const dialog = screen.getByRole("dialog");
    fireEvent.change(within(dialog).getByRole("spinbutton"), { target: { value: "7" } });
    fireEvent.change(within(dialog).getByLabelText(/Reason/), { target: { value: "dropped a tray" } });
    fireEvent.click(within(dialog).getByRole("button", { name: /Record/ }));
    // The walk's page-0 request is pending; a filter change supersedes it
    // and lands successfully.
    await waitFor(() => expect(mockListEggLots).toHaveBeenCalledTimes(2));
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-02" } });
    await screen.findByText("07/02/2026");

    await act(async () => {
      rejectWalk(new Error("stale fetch died"));
    });
    await screen.findByText(/92 now available/);
    expect(screen.queryByText(/Could not load stock/)).not.toBeInTheDocument();
    expect(screen.getByText("07/02/2026")).toBeInTheDocument();
  });

  it("restores the applied filter when a failing write-off superseded a pending filter (codex round 6)", async () => {
    // Filter pending (inputs optimistic, never applied) when the write-off
    // claims the ticket; the POST then rejects. The stale filter completion
    // can neither apply nor roll back — the write-off's failure path must
    // re-sync the inputs to the applied window the visible rows still show.
    mockRecordEggLotMovement.mockRejectedValueOnce(new Error("network down"));
    mockListEggLots
      .mockResolvedValueOnce(LOTS)
      .mockImplementationOnce(() => new Promise<EggLotRow[]>(() => undefined));
    await expandGradeA();
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-02" } });

    fireEvent.click(screen.getByRole("button", { name: "write off" }));
    const dialog = screen.getByRole("dialog");
    fireEvent.change(within(dialog).getByRole("spinbutton"), { target: { value: "7" } });
    fireEvent.change(within(dialog).getByLabelText(/Reason/), { target: { value: "dropped a tray" } });
    fireEvent.click(within(dialog).getByRole("button", { name: /Record/ }));
    await screen.findByText(/network down/);

    expect(screen.getByLabelText("From")).toHaveValue("");
    expect(screen.getByText("07/01/2026")).toBeInTheDocument();
  });

  it("commits the filter its refresh walk applied, so a later rollback targets it (codex round 6)", async () => {
    // The dual of the failure case: a SUCCESSFUL write-off that superseded a
    // pending filter walks with the optimistic values — those become the
    // applied window, and a later failed change must roll back to THEM.
    mockRecordEggLotMovement.mockResolvedValue({
      movementId: "wo1", eggLotId: "lot1", movementType: "Discard",
      quantityDelta: -7, reason: "dropped a tray",
      createdAtUtc: "2026-08-08T10:00:00Z", quantityAvailable: 92, version: 2,
    });
    mockListEggLots
      .mockResolvedValueOnce(LOTS)
      .mockImplementationOnce(() => new Promise<EggLotRow[]>(() => undefined))
      .mockResolvedValueOnce([{ ...LOTS[0], quantityAvailable: 92 }]) // walk page 0
      .mockRejectedValueOnce(new Error("boom")); // the later filter change
    await expandGradeA();
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-01" } });

    fireEvent.click(screen.getByRole("button", { name: "write off" }));
    const dialog = screen.getByRole("dialog");
    fireEvent.change(within(dialog).getByRole("spinbutton"), { target: { value: "7" } });
    fireEvent.change(within(dialog).getByLabelText(/Reason/), { target: { value: "dropped a tray" } });
    fireEvent.click(within(dialog).getByRole("button", { name: /Record/ }));
    await screen.findByText(/92 now available/);
    expect(screen.getByLabelText("From")).toHaveValue("2026-07-01");

    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-03" } });
    await screen.findByText(/Could not load the grade's lots/);
    // Rolls back to the window the walk applied — not all the way to empty.
    expect(screen.getByLabelText("From")).toHaveValue("2026-07-01");
  });

  it("releases the loading flag when a write-off supersedes a pending filter (codex round 5)", async () => {
    // Filter load sets lotsLoading; the write-off submit supersedes it, so
    // the filter's settle can't clear the flag — the submit must own and
    // eventually release it, or load-more stays hidden forever.
    mockRecordEggLotMovement.mockResolvedValue({
      movementId: "wo1", eggLotId: "lot-07-0", movementType: "Discard",
      quantityDelta: -7, reason: "dropped a tray",
      createdAtUtc: "2026-08-08T10:00:00Z", quantityAvailable: 83, version: 2,
    });
    mockListEggLots
      .mockResolvedValueOnce(makeLots(PAGE))
      .mockImplementationOnce(() => new Promise<EggLotRow[]>(() => undefined))
      .mockResolvedValue(makeLots(PAGE));
    await expandGradeA();
    await screen.findByRole("button", { name: "load more" });
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-02" } });
    expect(screen.queryByRole("button", { name: "load more" })).not.toBeInTheDocument();

    const lotRow = screen.getAllByRole("row", { name: /07\/01\/2026/ })[0];
    fireEvent.click(within(lotRow).getByRole("button", { name: "write off" }));
    const dialog = screen.getByRole("dialog");
    fireEvent.change(within(dialog).getByRole("spinbutton"), { target: { value: "7" } });
    fireEvent.change(within(dialog).getByLabelText(/Reason/), { target: { value: "dropped a tray" } });
    fireEvent.click(within(dialog).getByRole("button", { name: /Record/ }));
    await screen.findByText(/83 now available/);

    // The walk returned a full page: load-more must be usable again.
    expect(await screen.findByRole("button", { name: "load more" })).toBeInTheDocument();
  });

  it("patches the lot row from the write response even when superseded (codex round 5)", async () => {
    // POST pending, user's filter GET settles first with the PRE-mutation
    // balance; the superseded refresh skips its walk — the durable write's
    // own response must still correct the visible row.
    let releaseRecord!: (r: {
      movementId: string; eggLotId: string; movementType: string; quantityDelta: number;
      reason: string; createdAtUtc: string; quantityAvailable: number; version: number;
    }) => void;
    mockRecordEggLotMovement.mockReturnValue(
      new Promise((r) => (releaseRecord = r)));
    mockListEggLots
      .mockResolvedValueOnce(LOTS)
      .mockResolvedValueOnce([{ ...LOTS[0] }]); // filter GET: stale 99 balance
    await expandGradeA();

    fireEvent.click(screen.getByRole("button", { name: "write off" }));
    const dialog = screen.getByRole("dialog");
    fireEvent.change(within(dialog).getByRole("spinbutton"), { target: { value: "7" } });
    fireEvent.change(within(dialog).getByLabelText(/Reason/), { target: { value: "dropped a tray" } });
    fireEvent.click(within(dialog).getByRole("button", { name: /Record/ }));

    // While the POST hangs, the user narrows the filter; the stale GET lands.
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-01" } });
    await waitFor(() => expect(mockListEggLots).toHaveBeenCalledTimes(2));

    await act(async () => {
      releaseRecord({
        movementId: "wo1", eggLotId: "lot1", movementType: "Discard",
        quantityDelta: -7, reason: "dropped a tray",
        createdAtUtc: "2026-08-08T10:00:00Z", quantityAvailable: 92, version: 2,
      });
    });
    await screen.findByText(/92 now available/);
    const lotRow = screen.getByRole("row", { name: /07\/01\/2026/ });
    expect(within(lotRow).getByText("92")).toBeInTheDocument();
    expect(within(lotRow).queryByText("99")).not.toBeInTheDocument();
  });

  it("hides load-more while a filter load is pending (codex round 4)", async () => {
    // Clicking load-more mid-filter-load would supersede the page-0 request
    // and append the NEW window's page onto the OLD window's rows.
    let release!: (rows: EggLotRow[]) => void;
    mockListEggLots
      .mockResolvedValueOnce(makeLots(PAGE))
      .mockReturnValueOnce(new Promise<EggLotRow[]>((r) => (release = r)));
    await expandGradeA();
    await screen.findByRole("button", { name: "load more" });

    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-02" } });
    expect(screen.queryByRole("button", { name: "load more" })).not.toBeInTheDocument();

    // The settled (full) filtered page brings it back.
    await act(async () => {
      release(makeLots(PAGE));
    });
    expect(screen.getByRole("button", { name: "load more" })).toBeInTheDocument();
  });

  it("restores the applied filter when a grade switch fails after superseding a pending filter (codex round 4)", async () => {
    // Filter request still pending (inputs optimistic, never applied) when
    // the user switches grades and THAT fails: the rows still come from the
    // applied (empty) window — the inputs must return to describing it.
    mockListEggLots
      .mockResolvedValueOnce(LOTS)
      .mockReturnValueOnce(new Promise<EggLotRow[]>(() => undefined))
      .mockRejectedValueOnce(new Error("boom"));
    await expandGradeA();
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-02" } });

    fireEvent.click(within(screen.getByRole("row", { name: /Grade B\b/ })).getByRole("button", { name: "lots" }));
    await screen.findByText(/Could not load the grade's lots/);
    expect(screen.getByLabelText("From")).toHaveValue("");
    expect(screen.getByText("07/01/2026")).toBeInTheDocument();
  });

  it("rolls back to the last APPLIED filter, not the previous optimistic input (codex round 3)", async () => {
    // From then To before the first request settles: if the second fails,
    // the rows still show the ORIGINAL window — rolling back to the first
    // change's never-applied From would misdescribe them.
    const pending: Array<{ resolve: (rows: EggLotRow[]) => void; reject: (e: Error) => void }> = [];
    mockListEggLots
      .mockResolvedValueOnce(LOTS)
      .mockImplementation(() => new Promise<EggLotRow[]>((resolve, reject) => pending.push({ resolve, reject })));
    await expandGradeA();

    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-02" } });
    fireEvent.change(screen.getByLabelText("To"), { target: { value: "2026-07-03" } });
    await waitFor(() => expect(pending).toHaveLength(2));

    await act(async () => {
      pending[1].reject(new Error("boom"));
    });
    await screen.findByText(/Could not load the grade's lots/);
    // Applied filter was the unfiltered initial load — both inputs go back
    // to empty, not to the intermediate From-only request that never landed.
    expect(screen.getByLabelText("From")).toHaveValue("");
    expect(screen.getByLabelText("To")).toHaveValue("");
  });

  it("drops a pending ledger load once the filter changes (codex round 3)", async () => {
    // History clicked, its movement request in flight; the filter change
    // clears the ledger — the late settle must not resurrect it under a
    // filtered list that may not contain the lot at all.
    let releaseMovements!: (rows: EggMovementRow[]) => void;
    mockListEggLotMovements.mockReturnValue(
      new Promise<EggMovementRow[]>((r) => (releaseMovements = r)));
    mockListEggLots
      .mockResolvedValueOnce(LOTS)
      .mockResolvedValueOnce([{ ...LOTS[0], id: "hit", productionDate: "2026-07-02" }]);
    await expandGradeA();

    fireEvent.click(screen.getByRole("button", { name: "history" }));
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-02" } });
    await screen.findByText("07/02/2026");

    await act(async () => {
      releaseMovements(MOVEMENTS);
    });
    expect(screen.queryByText(/Movement ledger/)).not.toBeInTheDocument();
  });

  it("abandons a write-off refresh superseded during the stock refetch (codex round 2)", async () => {
    // refreshAfterWriteOff awaits getStock() first. If the user switches
    // grades during that await, the refresh must NOT claim a fresh ticket
    // afterwards and re-render the OLD grade's lots under the new panel.
    mockRecordEggLotMovement.mockResolvedValue({
      movementId: "wo1", eggLotId: "lot1", movementType: "Discard",
      quantityDelta: -7, reason: "dropped a tray",
      createdAtUtc: "2026-08-08T10:00:00Z", quantityAvailable: 92, version: 2,
    });
    let releaseStock!: (rows: StockRow[]) => void;
    mockGetStock
      .mockResolvedValueOnce(ROWS)
      .mockReturnValueOnce(new Promise<StockRow[]>((r) => (releaseStock = r)));
    mockListEggLots
      .mockResolvedValueOnce(LOTS)
      .mockResolvedValueOnce([{ ...LOTS[0], id: "b-lot", eggGradeId: "g2", productionDate: "2026-03-03" }])
      .mockResolvedValue(LOTS);
    await expandGradeA();

    fireEvent.click(screen.getByRole("button", { name: "write off" }));
    const dialog = screen.getByRole("dialog");
    fireEvent.change(within(dialog).getByRole("spinbutton"), { target: { value: "7" } });
    fireEvent.change(within(dialog).getByLabelText(/Reason/), { target: { value: "dropped a tray" } });
    fireEvent.click(within(dialog).getByRole("button", { name: /Record/ }));

    // While the refresh's getStock() hangs, the user switches to Grade B.
    fireEvent.click(within(screen.getByRole("row", { name: /Grade B\b/ })).getByRole("button", { name: "lots" }));
    await screen.findByText("03/03/2026");

    await act(async () => {
      releaseStock(ROWS);
    });
    // Grade B's rows survive; the abandoned refresh issued no third lot fetch.
    expect(screen.getByText("03/03/2026")).toBeInTheDocument();
    expect(screen.queryByText("07/01/2026")).not.toBeInTheDocument();
    expect(mockListEggLots).toHaveBeenCalledTimes(2);
  });

  it("drops rows the next page repeats when a concurrent insert shifted the offset", async () => {
    // Offset paging over a live list: a lot created between page loads shifts
    // every index, so page two can re-serve page one's last row. Rendering it
    // twice collides the row key; the append must dedupe by id.
    const shifted = { ...LOTS[0], id: "lot-07-49", productionDate: "2026-05-05" };
    mockListEggLots
      .mockResolvedValueOnce([...makeLots(PAGE - 1), shifted])
      .mockResolvedValueOnce([shifted, { ...LOTS[0], id: "older", productionDate: "2026-04-04" }]);
    await expandGradeA();

    fireEvent.click(await screen.findByRole("button", { name: "load more" }));
    await screen.findByText("04/04/2026");
    expect(screen.getAllByText("05/05/2026")).toHaveLength(1);
  });

  it("closes an open movement ledger when the filter changes (codex P2)", async () => {
    // The expanded lot may not be in the filtered page at all — leaving its
    // ledger rendered under an unrelated list misattributes the history.
    mockListEggLots
      .mockResolvedValueOnce(LOTS)
      .mockResolvedValueOnce([{ ...LOTS[0], id: "hit", productionDate: "2026-07-02" }]);
    mockListEggLotMovements.mockResolvedValue(MOVEMENTS);
    await expandGradeA();
    fireEvent.click(screen.getByRole("button", { name: "history" }));
    await screen.findByText(/Movement ledger/);

    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-02" } });
    await screen.findByText("07/02/2026");
    expect(screen.queryByText(/Movement ledger/)).not.toBeInTheDocument();
  });

  it("keeps the filter inputs when a grade switch fails (codex P2)", async () => {
    // A failed switch leaves the OLD grade's filtered rows on screen — the
    // inputs must keep describing them, not blank out optimistically.
    mockListEggLots
      .mockResolvedValueOnce(LOTS)
      .mockResolvedValueOnce([{ ...LOTS[0], id: "hit", productionDate: "2026-07-02" }])
      .mockRejectedValueOnce(new Error("boom"));
    await expandGradeA();
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-02" } });
    await screen.findByText("07/02/2026");

    fireEvent.click(within(screen.getByRole("row", { name: /Grade B\b/ })).getByRole("button", { name: "lots" }));
    await screen.findByText(/Could not load the grade's lots/);
    expect(screen.getByLabelText("From")).toHaveValue("2026-07-02");
    expect(screen.getByText("07/02/2026")).toBeInTheDocument();
  });

  it("dedupes overlapping pages in the post-write-off window walk", async () => {
    // The same offset shift can happen between the walk's own page fetches.
    const shifted = { ...LOTS[0], id: "lot-07-49", productionDate: "2026-05-05" };
    mockRecordEggLotMovement.mockResolvedValue({
      movementId: "wo1", eggLotId: "lot-07-0", movementType: "Discard",
      quantityDelta: -7, reason: "dropped a tray",
      createdAtUtc: "2026-08-08T10:00:00Z", quantityAvailable: 83, version: 2,
    });
    mockListEggLots
      .mockResolvedValueOnce([...makeLots(PAGE - 1), shifted])
      .mockResolvedValueOnce([{ ...LOTS[0], id: "old-lot", productionDate: "2026-06-01" }])
      .mockResolvedValueOnce([...makeLots(PAGE - 1), shifted])
      .mockResolvedValueOnce([shifted, { ...LOTS[0], id: "older", productionDate: "2026-04-04" }]);
    await expandGradeA();
    fireEvent.click(await screen.findByRole("button", { name: "load more" }));
    await screen.findByText("06/01/2026");

    const lotRow = screen.getAllByRole("row", { name: /07\/01\/2026/ })[0];
    fireEvent.click(within(lotRow).getByRole("button", { name: "write off" }));
    const dialog = screen.getByRole("dialog");
    fireEvent.change(within(dialog).getByRole("spinbutton"), { target: { value: "7" } });
    fireEvent.change(within(dialog).getByLabelText(/Reason/), { target: { value: "dropped a tray" } });
    fireEvent.click(within(dialog).getByRole("button", { name: /Record/ }));

    await screen.findByText("04/04/2026");
    expect(screen.getAllByText("05/05/2026")).toHaveLength(1);
  });

  it("stops walking refresh pages once a newer load supersedes it", async () => {
    // A write-off refresh over a 2-page window issues its page fetches
    // sequentially; if a filter change lands after page one, the walk must
    // bail instead of firing the remaining page requests it will discard.
    mockRecordEggLotMovement.mockResolvedValue({
      movementId: "wo1", eggLotId: "lot-07-0", movementType: "Discard",
      quantityDelta: -7, reason: "dropped a tray",
      createdAtUtc: "2026-08-08T10:00:00Z", quantityAvailable: 83, version: 2,
    });
    const pending: Array<(rows: EggLotRow[]) => void> = [];
    mockListEggLots
      .mockResolvedValueOnce(makeLots(PAGE))
      .mockResolvedValueOnce([{ ...LOTS[0], id: "old-lot", productionDate: "2026-06-01" }])
      .mockImplementation(() => new Promise<EggLotRow[]>((r) => pending.push(r)));
    await expandGradeA();
    fireEvent.click(await screen.findByRole("button", { name: "load more" }));
    await screen.findByText("06/01/2026");

    const lotRow = screen.getAllByRole("row", { name: /07\/01\/2026/ })[0];
    fireEvent.click(within(lotRow).getByRole("button", { name: "write off" }));
    const dialog = screen.getByRole("dialog");
    fireEvent.change(within(dialog).getByRole("spinbutton"), { target: { value: "7" } });
    fireEvent.change(within(dialog).getByLabelText(/Reason/), { target: { value: "dropped a tray" } });
    fireEvent.click(within(dialog).getByRole("button", { name: /Record/ }));

    // Refresh's page-0 request is in flight; a filter change supersedes it.
    await waitFor(() => expect(pending).toHaveLength(1));
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-07-02" } });
    await waitFor(() => expect(pending).toHaveLength(2));

    // Settle the superseded page-0: the walk must NOT request offset 50.
    await act(async () => {
      pending[0](makeLots(PAGE));
    });
    const requestsAfter = mockListEggLots.mock.calls.slice(2);
    expect(requestsAfter.map(([args]) => args?.offset)).toEqual([0, 0]);
  });

  it("reads the load-more label from the catalog, not a hardcoded literal", async () => {
    const original = i18n.getResource("en", "stock", "loadMoreButton") as string;
    i18n.addResource("en", "stock", "loadMoreButton", "LOAD-MORE-MARKER");
    try {
      mockListEggLots.mockResolvedValue(makeLots(PAGE));
      await expandGradeA();
      expect(await screen.findByRole("button", { name: "LOAD-MORE-MARKER" })).toBeInTheDocument();
    } finally {
      i18n.addResource("en", "stock", "loadMoreButton", original);
    }
  });
});

// #493 — the full audit trail for a lot is a distinct affordance from the
// "history"/"hide history" toggle StockPage already had for the inventory
// MOVEMENT ledger. Both live on the same row; the two must do genuinely
// different things, not just carry different labels.
describe("StockPage audit history link (#493)", () => {
  it("links a lot row to its own entity-scoped audit history, distinct from the movement-history toggle", async () => {
    mockGetStock.mockResolvedValue(ROWS);
    mockListEggLots.mockResolvedValue(LOTS);
    mockListEggLotMovements.mockResolvedValue(MOVEMENTS);
    render(<StockPage />);
    await screen.findByText("Grade A");
    fireEvent.click(within(screen.getByRole("row", { name: /Grade A\b/ })).getByRole("button", { name: "lots" }));
    const lotRow = await screen.findByRole("row", { name: /07\/01\/2026/ });

    // The link navigates to the entity-scoped audit trail — never opens
    // anything in place.
    expect(within(lotRow).getByRole("link", { name: "Adjustment history" }))
      .toHaveAttribute("href", "/audit?entityId=lot1");
    expect(screen.queryByText("Movement ledger")).not.toBeInTheDocument();

    // The toggle button expands the movement ledger IN PLACE — never
    // navigates. Clicking it must not be mistaken for the link above.
    await act(async () => {
      fireEvent.click(within(lotRow).getByRole("button", { name: "history" }));
    });
    expect(await screen.findByText("Movement ledger")).toBeInTheDocument();
    expect(within(lotRow).getByRole("button", { name: "hide history" })).toBeInTheDocument();
    // Still on StockPage — the toggle is not a navigation.
    expect(within(lotRow).getByRole("link", { name: "Adjustment history" }))
      .toHaveAttribute("href", "/audit?entityId=lot1");
  });

  // codex review of #516 — /api/v1/audit is AdminOnly; this screen is
  // readable by non-admins too, who would otherwise hit a 403.
  it("hides the link from a non-admin", async () => {
    mockGetStock.mockResolvedValue(ROWS);
    mockListEggLots.mockResolvedValue(LOTS);
    render(<StockPage />, WORKER);
    await screen.findByText("Grade A");
    fireEvent.click(within(screen.getByRole("row", { name: /Grade A\b/ })).getByRole("button", { name: "lots" }));
    const lotRow = await screen.findByRole("row", { name: /07\/01\/2026/ });
    expect(within(lotRow).queryByRole("link", { name: "Adjustment history" })).not.toBeInTheDocument();
  });
});
