import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, within, fireEvent, act } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { InventoryPage } from "./InventoryPage";
import {
  createInventoryItem, getAccount, listInventoryItems, listInventoryLots, listInventoryMovements,
  recordInventoryAdjustment, updateInventoryItem,
} from "../api/cluckwork";
import type { Account, InventoryItem, InventoryLot } from "../api/cluckwork";
import { account } from "../test/fixtures";
import i18n from "../i18n";

// #703 review r2 (PR 2) — the create/edit/adjust dialogs are admin-gated
// (`open={… && isAdmin}`), so a role change hides them without firing onClose.
// A role has to flip MID-RENDER for that to be observable, and the
// token-seeded AuthProvider the main suite renders through offers no path for
// it, so this file mocks useAuth directly — DailyEntryPage.test.tsx's
// technique, kept in its own file so InventoryPage.test.tsx stays on the real
// provider. `auth` is a single mutable object (vi.hoisted so the mock factory
// can close over it); beforeEach resets it to admin.
const auth = vi.hoisted(() => ({ isAdmin: true }));
vi.mock("../auth/useAuth", () => ({
  useAuth: () => ({ isAdmin: auth.isAdmin }),
}));

vi.mock("../api/cluckwork", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/cluckwork")>();
  return {
    ...actual,
    listInventoryItems: vi.fn(),
    getAccount: vi.fn(),
    createInventoryItem: vi.fn(),
    updateInventoryItem: vi.fn(),
    recordInventoryAdjustment: vi.fn(),
    listInventoryLots: vi.fn(),
    listInventoryMovements: vi.fn(),
    getFlock: vi.fn(),
    getCustomer: vi.fn(),
  };
});

const mockListItems = vi.mocked(listInventoryItems);
const mockCreate = vi.mocked(createInventoryItem);
const mockUpdate = vi.mocked(updateInventoryItem);
const mockAdjust = vi.mocked(recordInventoryAdjustment);
const mockListLots = vi.mocked(listInventoryLots);

const USD_ACCOUNT: Account = account({ name: "Farm" });
const FEED: InventoryItem = {
  id: "it1", farmId: "f1", name: "Layer Feed", category: "Feed", unit: "kg",
  defaultCostMinorUnits: 4500, defaultCostCurrencyCode: "USD", defaultCostCurrencyMinorUnit: 2,
  quantityOnHand: 200, active: true,
};
const LOT: InventoryLot = {
  id: "lot1", inventoryItemId: "it1", receivedDate: "2026-07-01", lotNumber: "L-1",
  expiryDate: null, quantityReceived: 100, quantityAvailable: 80,
  unitCostMinorUnits: 4500, unitCostCurrencyCode: "USD", unitCostCurrencyMinorUnit: 2,
};

// A promise the test resolves by hand — holds a request open so the demotion
// lands while the write is out.
function deferred<T>() {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

const dialog = () => screen.getByRole("dialog");

beforeEach(() => {
  vi.clearAllMocks();
  auth.isAdmin = true;
  localStorage.clear();
  mockListItems.mockResolvedValue([FEED]);
  vi.mocked(getAccount).mockResolvedValue(USD_ACCOUNT);
  mockListLots.mockResolvedValue([]);
  vi.mocked(listInventoryMovements).mockResolvedValue([]);
});

// Not renderWithProviders(): this needs the render RESULT (for rerender) so the
// SAME mounted tree is re-evaluated, the way a live role change would — a
// fresh render() call would just mount closed from the start and prove nothing
// about the gate reacting to a change.
async function renderAdmin() {
  const view = render(<MemoryRouter><InventoryPage /></MemoryRouter>);
  await screen.findByText("Layer Feed");
  return view;
}
const demote = (view: ReturnType<typeof render>) => { auth.isAdmin = false; view.rerender(<MemoryRouter><InventoryPage /></MemoryRouter>); };
const promote = (view: ReturnType<typeof render>) => { auth.isAdmin = true; view.rerender(<MemoryRouter><InventoryPage /></MemoryRouter>); };

describe("InventoryPage admin-gated dialogs end their session on demotion (#703 r2)", () => {
  it("does not restore a hidden create dialog on re-promotion, and its abandoned write cannot act on it", async () => {
    const gate = deferred<{ id: string }>();
    mockCreate.mockReturnValueOnce(gate.promise);
    const view = await renderAdmin();
    fireEvent.click(screen.getByRole("button", { name: "New item" }));
    fireEvent.change(within(dialog()).getByLabelText("Item name *"), { target: { value: "One" } });
    fireEvent.click(within(dialog()).getByRole("button", { name: "Add item" })); // create in flight

    demote(view);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    promote(view);
    // The session ended on the demotion: there is nothing to restore.
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();

    await act(async () => { gate.resolve({ id: "new" }); }); // the old create lands
    // The item exists (the list re-read) — but the abandoned session's success
    // has no dialog to close and no message to claim.
    expect(mockListItems).toHaveBeenCalledTimes(2);
    expect(screen.queryByText(i18n.t("inventory:itemCreatedMessage"))).not.toBeInTheDocument();
  });

  it("does not restore a hidden edit dialog on re-promotion", async () => {
    const gate = deferred<void>();
    mockUpdate.mockReturnValueOnce(gate.promise);
    const view = await renderAdmin();
    fireEvent.click(within(screen.getByRole("row", { name: /Layer Feed/ })).getByRole("button", { name: "edit" }));
    fireEvent.click(within(dialog()).getByRole("button", { name: "Save" })); // edit in flight

    demote(view);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    promote(view);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();

    await act(async () => { gate.resolve(); });
    expect(mockListItems).toHaveBeenCalledTimes(2);
  });

  it("does not restore a hidden correction dialog on re-promotion, and its abandoned write cannot act on it", async () => {
    mockListLots.mockResolvedValue([LOT]);
    const gate = deferred<{ movementId: string }>();
    mockAdjust.mockReturnValueOnce(gate.promise);
    const view = await renderAdmin();
    fireEvent.click(within(screen.getByRole("row", { name: /Layer Feed/ })).getByRole("button", { name: "open" }));
    await screen.findByRole("heading", { name: /Layer Feed/ });
    fireEvent.click(screen.getByRole("button", { name: "Correct stock" }));
    fireEvent.change(within(dialog()).getByLabelText(/Quantity/), { target: { value: "2" } });
    fireEvent.change(within(dialog()).getByLabelText(/Reason/), { target: { value: "spillage" } });
    fireEvent.click(within(dialog()).getByRole("button", { name: "Record correction" })); // correction in flight

    demote(view);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    promote(view);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();

    await act(async () => { gate.resolve({ movementId: "adj1" }); });
    expect(mockListItems).toHaveBeenCalledTimes(2);
    expect(screen.queryByText(i18n.t("inventory:correctionRecordedMessage"))).not.toBeInTheDocument();
  });
});
