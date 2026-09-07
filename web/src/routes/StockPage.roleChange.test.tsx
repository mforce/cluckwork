import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, within, fireEvent, act } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { StockPage } from "./StockPage";
import { getStock, listEggLots, listEggLotMovements, recordEggLotMovement } from "../api/cluckwork";
import type { StockRow, EggLotRow, EggLotMovementResult } from "../api/cluckwork";

// #703 review r2 (PR 2) — the write-off dialog is admin-gated (`open={isAdmin}`),
// so a role change hides it without firing onClose. A role has to flip
// MID-RENDER for that to be observable, and the token-seeded AuthProvider the
// main suite renders through offers no path for it, so this file mocks useAuth
// directly — DailyEntryPage.test.tsx's technique, kept in its own file so
// StockPage.test.tsx stays on the real provider.
const auth = vi.hoisted(() => ({ isAdmin: true }));
vi.mock("../auth/useAuth", () => ({
  useAuth: () => ({ isAdmin: auth.isAdmin }),
}));

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
const mockRecordEggLotMovement = vi.mocked(recordEggLotMovement);

const ROWS: StockRow[] = [
  { eggGradeId: "g1", gradeName: "Grade A", available: 100, restricted: 0 },
];
const LOTS: EggLotRow[] = [
  { id: "lot1", eggGradeId: "g1", productionDate: "2026-07-01", quantityProduced: 120, quantityAvailable: 99, restrictedUntil: null, dailyEntryId: "de1" },
];
const RESULT: EggLotMovementResult = {
  movementId: "wo1", eggLotId: "lot1", movementType: "Discard",
  quantityDelta: -7, reason: "dropped a tray",
  createdAtUtc: "2026-08-08T10:00:00Z", quantityAvailable: 92, version: 2,
};

const dialog = () => screen.getByRole("dialog");

beforeEach(() => {
  vi.clearAllMocks();
  auth.isAdmin = true;
  mockGetStock.mockResolvedValue(ROWS);
  mockListEggLots.mockResolvedValue(LOTS);
  vi.mocked(listEggLotMovements).mockResolvedValue([]);
});

describe("StockPage write-off dialog ends its session on demotion (#703 r2)", () => {
  it("does not restore a hidden write-off dialog on re-promotion, and its abandoned write cannot act on it", async () => {
    let resolveFirst!: (v: EggLotMovementResult) => void;
    mockRecordEggLotMovement.mockReturnValueOnce(new Promise((resolve) => { resolveFirst = resolve; }));
    // Not renderWithProviders(): this needs the render RESULT (for rerender)
    // so the SAME mounted tree is re-evaluated, the way a live role change
    // would.
    const view = render(<MemoryRouter><StockPage /></MemoryRouter>);
    await screen.findByText("Grade A");
    fireEvent.click(within(screen.getByRole("row", { name: /Grade A\b/ })).getByRole("button", { name: "lots" }));
    const lotRow = await screen.findByRole("row", { name: /2026/ });
    fireEvent.click(within(lotRow).getByRole("button", { name: "write off" }));
    fireEvent.change(within(dialog()).getByRole("spinbutton"), { target: { value: "7" } });
    fireEvent.change(within(dialog()).getByLabelText(/Reason/), { target: { value: "dropped a tray" } });
    fireEvent.click(within(dialog()).getByRole("button", { name: /Record/ })); // write-off in flight

    auth.isAdmin = false; view.rerender(<MemoryRouter><StockPage /></MemoryRouter>); // demote
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    auth.isAdmin = true; view.rerender(<MemoryRouter><StockPage /></MemoryRouter>);  // re-promote
    // The session ended on the demotion: there is nothing to restore.
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();

    await act(async () => { resolveFirst(RESULT); }); // the old write-off lands
    // The write-off happened (the totals re-read, the row patched) — but the
    // abandoned session's success has no dialog to close and no message to claim.
    expect(mockGetStock).toHaveBeenCalledTimes(2);
    expect(within(lotRow).getByText("92")).toBeInTheDocument();
    expect(screen.queryByText(/92 now available/)).not.toBeInTheDocument();
  });
});
