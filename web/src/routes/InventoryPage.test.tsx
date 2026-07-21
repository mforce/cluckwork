import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act } from "@testing-library/react";
import { InventoryPage } from "./InventoryPage";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  activateInventoryItem, createInventoryItem, deactivateInventoryItem, getAccount,
  listFlocks, listInventoryItems, listInventoryLots, listInventoryMovements,
  recordFeedUsage, recordInventoryAdjustment, recordInventoryPurchase, updateInventoryItem,
} from "../api/cluckwork";
import type { Account, Flock, InventoryItem, InventoryLot, InventoryMovement } from "../api/cluckwork";
import { ApiError } from "../api/client";

// Keep the REAL formatMoney + parseMoneyToMinorUnits (the money math under test)
// via importOriginal; stub only the network seam. EVERY network fn the screen can
// reach is stubbed — even ones a given test doesn't click — so no test can slip
// through to the real fetch client. The screen uses useAuth + the router →
// renderWithProviders seeds the session/role.
vi.mock("../api/cluckwork", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/cluckwork")>();
  return {
    ...actual,
    listInventoryItems: vi.fn(),
    getAccount: vi.fn(),
    listFlocks: vi.fn(),
    createInventoryItem: vi.fn(),
    updateInventoryItem: vi.fn(),
    activateInventoryItem: vi.fn(),
    deactivateInventoryItem: vi.fn(),
    recordInventoryPurchase: vi.fn(),
    listInventoryLots: vi.fn(),
    recordFeedUsage: vi.fn(),
    recordInventoryAdjustment: vi.fn(),
    listInventoryMovements: vi.fn(),
  };
});

const mockListItems = vi.mocked(listInventoryItems);
const mockGetAccount = vi.mocked(getAccount);
const mockListFlocks = vi.mocked(listFlocks);
const mockCreate = vi.mocked(createInventoryItem);
const mockUpdate = vi.mocked(updateInventoryItem);
const mockActivate = vi.mocked(activateInventoryItem);
const mockDeactivate = vi.mocked(deactivateInventoryItem);
const mockPurchase = vi.mocked(recordInventoryPurchase);
const mockListLots = vi.mocked(listInventoryLots);
const mockUsage = vi.mocked(recordFeedUsage);
const mockAdjust = vi.mocked(recordInventoryAdjustment);
const mockListMovements = vi.mocked(listInventoryMovements);

// Admin/Manager are admin; a claim-less session is a plain Worker (auth/claims).
const ADMIN = { sub: "u1", role: "Admin" };
const WORKER = { sub: "u1" };

const USD_ACCOUNT: Account = { id: "a1", name: "Farm", currencyCode: "USD", currencyMinorUnit: 2 };

// Feed → feedable; Packaging → NOT feedable (isolates the purchase form: no usage
// form renders, and with no lots no adjust form either); one inactive row proves
// the activate branch + status column.
const FEED: InventoryItem = {
  id: "it1", farmId: "f1", name: "Layer Feed", category: "Feed", unit: "kg",
  defaultCostMinorUnits: 4500, defaultCostCurrencyCode: "USD", defaultCostCurrencyMinorUnit: 2,
  quantityOnHand: 200, active: true,
};
const PACKAGING: InventoryItem = {
  id: "it2", farmId: "f1", name: "Egg Cartons", category: "Packaging", unit: "unit",
  defaultCostMinorUnits: null, defaultCostCurrencyCode: null, defaultCostCurrencyMinorUnit: null,
  quantityOnHand: 0, active: true,
};
const INACTIVE: InventoryItem = { ...FEED, id: "it3", name: "Old Additive", category: "Additive", active: false };

const FLOCK: Flock = {
  id: "fl1", farmId: "f1", houseId: "h1", name: "Flock One", breed: "ISA",
  placementDate: "2026-01-01", initialCount: 100, currentBirds: 98, status: "Active",
};
// A second Active flock so the usage "Flock" select offers TWO options: picking
// the second proves the request carries the chosen flockId, not a hard-coded index.
const FLOCK2: Flock = { ...FLOCK, id: "fl2", name: "Flock Two" };

const LOT: InventoryLot = {
  id: "lot1", inventoryItemId: "it1", receivedDate: "2026-07-01", lotNumber: "L-1",
  expiryDate: null, quantityReceived: 100, quantityAvailable: 80,
  unitCostMinorUnits: 4500, unitCostCurrencyCode: "USD", unitCostCurrencyMinorUnit: 2,
};
// A second lot so the correction "Lot" select offers TWO options: picking the
// second proves the request carries the chosen inventoryLotId, not lots[0].
const LOT2: InventoryLot = { ...LOT, id: "lot2", receivedDate: "2026-07-05", lotNumber: "L-2" };

const MOVEMENT: InventoryMovement = {
  id: "mv1", inventoryItemId: "it1", inventoryLotId: "lot1", date: "2026-07-01", type: "Purchase",
  quantityDelta: 100, unit: "kg", flockId: null, note: "initial receive",
  referenceType: null, referenceId: null,
};

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  // Mount-load defaults; individual tests override account currency / lots / movements.
  mockListItems.mockResolvedValue([FEED, PACKAGING, INACTIVE]);
  mockGetAccount.mockResolvedValue(USD_ACCOUNT);
  mockListFlocks.mockResolvedValue([FLOCK]);
  mockListLots.mockResolvedValue([]);
  mockListMovements.mockResolvedValue([]);
});

// Mount runs Promise.all([items, account, flocks]); wait for a known row so the
// effect has settled past the "Loading…" branch before a test acts.
async function renderReady(token: Record<string, unknown>) {
  renderWithProviders(<InventoryPage />, { token });
  await screen.findByText("Layer Feed");
}

// Open an item's panel (async loadLedger) and wait for the panel heading.
async function openItem(item: InventoryItem) {
  const row = screen.getByRole("row", { name: new RegExp(item.name) });
  await act(async () => {
    fireEvent.click(within(row).getByRole("button", { name: "open" }));
  });
  await screen.findByRole("heading", { name: new RegExp(item.name) });
}

// Scope a fill to one form when several forms (purchase/usage/adjust/create)
// render together and share labels ("Quantity", "Note"). Assertions stay on the
// mock-call arguments; this only picks which input to type into.
function formBySubmit(name: string): HTMLElement {
  const form = screen.getByRole("button", { name }).closest("form");
  if (!form) throw new Error(`no form for submit button "${name}"`);
  return form as HTMLElement;
}

describe("InventoryPage loading & display", () => {
  it("shows a loading state until the initial data resolves", async () => {
    let resolveItems!: (v: InventoryItem[]) => void;
    mockListItems.mockReturnValue(new Promise<InventoryItem[]>((r) => (resolveItems = r)));
    renderWithProviders(<InventoryPage />, { token: ADMIN });

    expect(screen.getByText(/Loading/)).toBeInTheDocument();
    await act(async () => {
      resolveItems([FEED]);
    });
    expect(await screen.findByText("Layer Feed")).toBeInTheDocument();
  });

  // The mount-effect error branch (Promise.all rejects → "Could not load
  // inventory") is intentionally NOT tested: in this Vitest 3 + React 19 stack a
  // rejection the component DOES catch is still flagged as an unhandled rejection
  // the test can't intercept (vitest #7940/#5796). The message is a fixed string
  // on any mount rejection; the transport is covered in api/client tests.

  it("renders each item's formatted default cost and active/inactive status", async () => {
    // Give the displayed feed a 3-decimal currency snapshot (BHD) so the assertion
    // exercises the item's OWN scale via the REAL formatMoney: 1500 minor @ 3dp →
    // "1.500 BHD". A hard-coded ÷100 (a 2dp assumption) would render "15.00" and
    // fail, so this pins the scale-aware formatting.
    const bhdFeed: InventoryItem = {
      ...FEED, defaultCostMinorUnits: 1500,
      defaultCostCurrencyCode: "BHD", defaultCostCurrencyMinorUnit: 3,
    };
    mockListItems.mockResolvedValue([bhdFeed, PACKAGING, INACTIVE]);
    await renderReady(ADMIN);

    // The mount load must ask for inactive rows too (the status column needs them).
    expect(mockListItems).toHaveBeenCalledWith({ includeInactive: true });

    const feedRow = screen.getByRole("row", { name: /Layer Feed/ });
    expect(within(feedRow).getByText("1.500 BHD")).toBeInTheDocument();
    expect(within(feedRow).getByText("Active")).toBeInTheDocument();
    expect(within(feedRow).getByText("200 kg")).toBeInTheDocument();

    const inactiveRow = screen.getByRole("row", { name: /Old Additive/ });
    expect(within(inactiveRow).getByText("Inactive")).toBeInTheDocument();

    // No default cost → the em-dash placeholder, scoped to the row.
    const packRow = screen.getByRole("row", { name: /Egg Cartons/ });
    expect(within(packRow).getByText("—")).toBeInTheDocument();
  });
});

describe("InventoryPage create item", () => {
  it("creates an item with the full body + key, parsing cost at the account currency scale (JPY 0dp)", async () => {
    // JPY has 0 decimals: "5" must become 5 minor units, not 500 (a hard-coded
    // ×100 would fail here). The parse honours account.currencyMinorUnit.
    mockGetAccount.mockResolvedValue({ id: "a2", name: "JP Farm", currencyCode: "JPY", currencyMinorUnit: 0 });
    mockCreate.mockResolvedValue({ id: "new1" });
    await renderReady(ADMIN);

    const form = formBySubmit("Add item");
    fireEvent.change(screen.getByPlaceholderText("Item name *"), { target: { value: "Bulk Grain" } });
    fireEvent.change(within(form).getByRole("combobox"), { target: { value: "Supplement" } });
    fireEvent.change(screen.getByPlaceholderText("Unit *"), { target: { value: "kg" } });
    fireEvent.change(within(form).getByLabelText(/Default cost/), { target: { value: "5" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add item" }));
    });

    expect(mockCreate.mock.calls[0][0]).toEqual({
      name: "Bulk Grain", category: "Supplement", unit: "kg", defaultUnitCostMinorUnits: 5,
    });
    expect(mockCreate.mock.calls[0][1]).toEqual(expect.any(String)); // idempotency key
    expect(screen.getByPlaceholderText("Item name *")).toHaveValue(""); // reset on success
    expect(screen.getByText("Item created.")).toBeInTheDocument();
  });

  it("sends a null default cost when the cost field is left blank", async () => {
    mockCreate.mockResolvedValue({ id: "new2" });
    await renderReady(ADMIN);

    fireEvent.change(screen.getByPlaceholderText("Item name *"), { target: { value: "Water Additive" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add item" }));
    });

    expect(mockCreate.mock.calls[0][0]).toMatchObject({ name: "Water Additive" });
    expect(mockCreate.mock.calls[0][0].defaultUnitCostMinorUnits).toBeNull();
  });
});

describe("InventoryPage inline edit", () => {
  it("saves an inline edit: id, changed name/unit/cost + key, parsing cost at the account scale (BHD 3dp)", async () => {
    // BHD has 3 decimals: "2.5" must become 2500 minor units, not 250 (a hard-coded
    // ×100 would fail here). The edit parse honours account.currencyMinorUnit.
    mockGetAccount.mockResolvedValue({ id: "a4", name: "BH Farm", currencyCode: "BHD", currencyMinorUnit: 3 });
    mockUpdate.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    // Enter edit mode on the Layer Feed row (admin-only "edit" control).
    const row = screen.getByRole("row", { name: /Layer Feed/ });
    fireEvent.click(within(row).getByRole("button", { name: "edit" }));

    // The edit inputs live in the one row that now shows a "save" button. The two
    // text inputs are name then unit (in DOM order); the cost is a number spinbutton.
    const editRow = screen.getByRole("button", { name: "save" }).closest("tr") as HTMLElement;
    const [nameInput, unitInput] = within(editRow).getAllByRole("textbox");
    fireEvent.change(nameInput, { target: { value: "Layer Feed Plus" } });
    fireEvent.change(unitInput, { target: { value: "bag" } });
    fireEvent.change(within(editRow).getByRole("spinbutton"), { target: { value: "2.5" } });
    await act(async () => {
      fireEvent.click(within(editRow).getByRole("button", { name: "save" }));
    });

    expect(mockUpdate.mock.calls[0]).toEqual([
      "it1",
      { name: "Layer Feed Plus", unit: "bag", defaultUnitCostMinorUnits: 2500 },
      expect.any(String), // idempotency key
    ]);
  });
});

describe("InventoryPage purchases", () => {
  // Different currency scales prove the unit cost is parsed at
  // account.currencyMinorUnit: "5" is 5 in JPY (0dp) but 500 at 2dp; "1.5" is
  // 1500 at 3dp. Packaging item + Admin → only the purchase form is on screen.
  it.each([
    { code: "USD", minorUnit: 2, typed: "2.50", expected: 250 },
    { code: "JPY", minorUnit: 0, typed: "5", expected: 5 },
    { code: "BHD", minorUnit: 3, typed: "1.5", expected: 1500 },
  ])("records a purchase with unit cost parsed at the $code scale, full body + key ($typed → $expected)", async ({ code, minorUnit, typed, expected }) => {
    mockGetAccount.mockResolvedValue({ id: "a3", name: "Farm", currencyCode: code, currencyMinorUnit: minorUnit });
    mockPurchase.mockResolvedValue({ lotId: "lot9" });
    await renderReady(ADMIN);
    await openItem(PACKAGING);

    const form = formBySubmit("Record purchase");
    fireEvent.change(within(form).getByLabelText(/Received/), { target: { value: "2026-07-10" } });
    fireEvent.change(within(form).getByLabelText(/Quantity/), { target: { value: "12.5" } });
    fireEvent.change(within(form).getByLabelText(/Unit cost/), { target: { value: typed } });
    fireEvent.change(within(form).getByLabelText(/Lot #/), { target: { value: "L-9" } });
    fireEvent.change(within(form).getByLabelText(/Expiry/), { target: { value: "2026-08-01" } });
    fireEvent.change(within(form).getByLabelText(/Note/), { target: { value: "carton batch" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Record purchase" }));
    });

    expect(mockPurchase.mock.calls[0][0]).toBe("it2"); // the opened item's id
    expect(mockPurchase.mock.calls[0][1]).toEqual({
      receivedDate: "2026-07-10", quantity: 12.5, unitCostMinorUnits: expected,
      lotNumber: "L-9", expiryDate: "2026-08-01", note: "carton batch",
    });
    expect(mockPurchase.mock.calls[0][2]).toEqual(expect.any(String)); // idempotency key
    expect(screen.getByText(/Purchase recorded/)).toBeInTheDocument();
  });

  it("sends a null unit cost and omits blank optional fields", async () => {
    mockPurchase.mockResolvedValue({ lotId: "lot9" });
    await renderReady(ADMIN);
    await openItem(PACKAGING);

    const form = formBySubmit("Record purchase");
    fireEvent.change(within(form).getByLabelText(/Quantity/), { target: { value: "3" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Record purchase" }));
    });

    const body = mockPurchase.mock.calls[0][1];
    expect(body).toMatchObject({ quantity: 3 });
    expect(body.unitCostMinorUnits).toBeNull(); // blank cost → null
    expect(body.lotNumber).toBeUndefined();
    expect(body.expiryDate).toBeUndefined();
    expect(body.note).toBeUndefined();
  });

  // The onPurchase/onRecordUsage "quantity must be positive" guard is NOT tested
  // through the form: the Quantity input is `required` with `min={0.001}`, so
  // jsdom's constraint validation blocks the submit event before the handler
  // runs — a non-positive value never reaches the guard via a real click.
});

describe("InventoryPage feed usage", () => {
  it("records feed usage against the SECOND selected flock, date, quantity + key on the opened item", async () => {
    // TWO flocks so the select has two options and "fl1" is the prefilled default:
    // choosing the second proves the request carries the selected flockId, not the
    // default index.
    mockListFlocks.mockResolvedValue([FLOCK, FLOCK2]);
    mockUsage.mockResolvedValue({ feedUsageId: "fu1", quantityUsed: 25, estimatedCostMinorUnits: 0, currencyCode: "USD" });
    await renderReady(WORKER); // usage is open to everyone, not just admins
    await openItem(FEED);

    const form = formBySubmit("Record usage");
    // Move off the prefilled first flock (fl1) to the second (fl2).
    fireEvent.change(within(form).getByLabelText(/Flock/), { target: { value: "fl2" } });
    fireEvent.change(within(form).getByLabelText(/Date/), { target: { value: "2026-07-10" } });
    fireEvent.change(within(form).getByLabelText(/Quantity/), { target: { value: "25" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Record usage" }));
    });

    expect(mockUsage.mock.calls[0][0]).toBe("it1");
    expect(mockUsage.mock.calls[0][1]).toEqual({
      flockId: "fl2", date: "2026-07-10", quantity: 25, note: undefined, // the CHOSEN flock, not fl1
    });
    expect(mockUsage.mock.calls[0][2]).toEqual(expect.any(String));
    expect(screen.getByText(/Feed usage recorded/)).toBeInTheDocument();
  });

  it("does not offer a usage form for a non-feedable category", async () => {
    await renderReady(WORKER);
    await openItem(PACKAGING);

    expect(screen.getByText(/aren't fed to flocks/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Record usage" })).not.toBeInTheDocument();
  });
});

describe("InventoryPage lot & movement drill-down", () => {
  it("loads lots and movements for the opened item id and renders the ledger rows", async () => {
    mockListLots.mockResolvedValue([LOT]);
    mockListMovements.mockResolvedValue([MOVEMENT]);
    await renderReady(ADMIN);
    await openItem(FEED);

    expect(mockListMovements).toHaveBeenCalledWith("it1", { limit: 100 });
    expect(mockListLots).toHaveBeenCalledWith("it1");

    const mvRow = screen.getByRole("row", { name: /Purchase/ });
    expect(within(mvRow).getByText("2026-07-01")).toBeInTheDocument();
    expect(within(mvRow).getByText("+100 kg")).toBeInTheDocument(); // signed positive delta
    expect(within(mvRow).getByText("initial receive")).toBeInTheDocument();
  });

  it("shows the empty-ledger hint when the item has no movements", async () => {
    mockListMovements.mockResolvedValue([]);
    await renderReady(ADMIN);
    await openItem(FEED);
    expect(screen.getByText(/No movements yet/)).toBeInTheDocument();
  });

  it("surfaces an error when the ledger fails to load on open", async () => {
    // onOpen awaits loadLedger inside its OWN try/catch, so this rejection is a
    // handled event-handler path — safe to test (unlike the mount-effect branch,
    // whose rejection Vitest flags as unhandled). We drive the click inline rather
    // than via openItem() because no "clean" ledger renders here.
    mockListMovements.mockRejectedValueOnce(new ApiError(500, "Server error", "ledger down"));
    await renderReady(ADMIN);
    const row = screen.getByRole("row", { name: /Layer Feed/ });
    await act(async () => {
      fireEvent.click(within(row).getByRole("button", { name: "open" }));
    });
    expect(await screen.findByText("Could not load the movement ledger.")).toBeInTheDocument();
  });
});

describe("InventoryPage adjustments", () => {
  it("records a signed adjustment against the SECOND selected lot with a reason + key", async () => {
    // TWO lots so the "Lot" select has two options and "lot1" is the prefilled
    // default: choosing the second proves the request targets the selected
    // inventoryLotId, not lots[0].
    mockListLots.mockResolvedValue([LOT, LOT2]);
    mockAdjust.mockResolvedValue({ movementId: "adj1" });
    await renderReady(ADMIN);
    await openItem(FEED);

    const form = formBySubmit("Record correction");
    // Move off the prefilled first lot (lot1) to the second (lot2).
    fireEvent.change(within(form).getByLabelText(/Lot/), { target: { value: "lot2" } });
    // negative quantity is passed through untouched for an "Adjustment"
    fireEvent.change(within(form).getByLabelText(/Quantity/), { target: { value: "-5" } });
    fireEvent.change(within(form).getByLabelText(/Reason/), { target: { value: "spillage" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Record correction" }));
    });

    expect(mockAdjust.mock.calls[0][0]).toBe("it1");
    expect(mockAdjust.mock.calls[0][1]).toMatchObject({
      inventoryLotId: "lot2", type: "Adjustment", quantityDelta: -5, reason: "spillage", // the CHOSEN lot, not lot1
      date: expect.stringMatching(/^\d{4}-\d{2}-\d{2}$/),
    });
    expect(mockAdjust.mock.calls[0][2]).toEqual(expect.any(String));
  });

  it("forces a negative quantityDelta for a Discard write-off", async () => {
    mockListLots.mockResolvedValue([LOT]);
    mockAdjust.mockResolvedValue({ movementId: "adj2" });
    await renderReady(ADMIN);
    await openItem(FEED);

    const form = formBySubmit("Record correction");
    fireEvent.change(within(form).getByLabelText(/Type/), { target: { value: "Discard" } });
    fireEvent.change(within(form).getByLabelText(/Quantity/), { target: { value: "5" } }); // positive input
    fireEvent.change(within(form).getByLabelText(/Reason/), { target: { value: "expired" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Record correction" }));
    });

    expect(mockAdjust.mock.calls[0][1]).toMatchObject({
      type: "Discard", quantityDelta: -5, reason: "expired", // -Math.abs(5)
    });
  });
});

describe("InventoryPage idempotency & lifecycle", () => {
  it("replays the SAME key after a failed create and rotates it after success", async () => {
    mockCreate.mockRejectedValueOnce(new ApiError(500, "Server error", "boom"));
    mockCreate.mockResolvedValue({ id: "ok" });
    await renderReady(ADMIN);
    const name = () => screen.getByPlaceholderText("Item name *");

    fireEvent.change(name(), { target: { value: "One" } });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Add item" })); });
    expect(await screen.findByText(/boom/)).toBeInTheDocument();

    fireEvent.change(name(), { target: { value: "One" } }); // retry after failure
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Add item" })); });

    fireEvent.change(name(), { target: { value: "Two" } }); // next write after success
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Add item" })); });

    const k1 = mockCreate.mock.calls[0][1];
    const k2 = mockCreate.mock.calls[1][1];
    const k3 = mockCreate.mock.calls[2][1];
    expect(k2).toBe(k1); // failure kept the key → exact replay
    expect(k3).not.toBe(k2); // success rotated it → the next write is fresh
  });

  it("deactivates an active item and activates an inactive one, each with a key", async () => {
    mockDeactivate.mockResolvedValue(undefined);
    mockActivate.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Layer Feed/ })).getByRole("button", { name: "deactivate" }));
    });
    expect(mockDeactivate).toHaveBeenCalledWith("it1", expect.any(String));

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Old Additive/ })).getByRole("button", { name: "activate" }));
    });
    expect(mockActivate).toHaveBeenCalledWith("it3", expect.any(String));
  });
});

describe("InventoryPage role gating", () => {
  it("hides admin-only controls from a worker but keeps purchases available", async () => {
    await renderReady(WORKER);
    expect(screen.queryByRole("button", { name: "Add item" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "edit" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "deactivate" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "activate" })).not.toBeInTheDocument();

    await openItem(FEED);
    // corrections are admin-only; the purchase form stays open to everyone
    expect(screen.getByText(/Stock corrections need an admin/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Record correction" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Record purchase" })).toBeInTheDocument();
  });
});
