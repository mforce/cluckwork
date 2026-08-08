import { describe, it, expect, vi, beforeEach } from "vitest";
import type { ReactNode } from "react";
import { screen, within, fireEvent } from "@testing-library/react";
import { StockPage } from "./StockPage";
import {
  getStock, listEggLots, listEggLotMovements, recordEggLotMovement,
} from "../api/cluckwork";
import type { StockRow, EggLotRow, EggMovementRow } from "../api/cluckwork";
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

    const lotRow = await screen.findByRole("row", { name: /2026-07-01/ });
    expect(mockListEggLots).toHaveBeenCalledWith({ gradeId: "g1" });
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
    expect(mockListEggLots).toHaveBeenCalledWith({ gradeId: "g2" });
  });

  it("collapses the lots again on 'hide lots'", async () => {
    mockListEggLots.mockResolvedValue(LOTS);
    await renderWithData();
    const gradeA = screen.getByRole("row", { name: /Grade A\b/ });
    fireEvent.click(within(gradeA).getByRole("button", { name: "lots" }));
    await screen.findByText("2026-07-01");
    fireEvent.click(within(gradeA).getByRole("button", { name: "hide lots" }));
    expect(screen.queryByText("2026-07-01")).not.toBeInTheDocument();
  });

  it("expands a lot into its movement ledger on 'history'", async () => {
    mockListEggLots.mockResolvedValue(LOTS);
    mockListEggLotMovements.mockResolvedValue(MOVEMENTS);
    await renderWithData();

    fireEvent.click(within(screen.getByRole("row", { name: /Grade A\b/ })).getByRole("button", { name: "lots" }));
    await screen.findByText("2026-07-01");
    // the lot row's history button
    const lotRow = screen.getByRole("row", { name: /2026-07-01/ });
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
    const lotRow = await screen.findByRole("row", { name: /2026-07-01/ });
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
      const lotRow = await screen.findByRole("row", { name: /2026-07-01/ });
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
    return await screen.findByRole("row", { name: /2026-07-01/ });
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
