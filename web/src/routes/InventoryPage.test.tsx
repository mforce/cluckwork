import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act, waitFor } from "@testing-library/react";
import { InventoryPage } from "./InventoryPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { account } from "../test/fixtures";
import {
  activateInventoryItem, createInventoryItem, deactivateInventoryItem, getAccount,
  listFlocks, listInventoryItems, listInventoryLots, listInventoryMovements,
  recordInventoryAdjustment, recordInventoryPurchase, updateInventoryItem,
} from "../api/cluckwork";
import type { Account, Flock, InventoryItem, InventoryLot, InventoryMovement } from "../api/cluckwork";
import { ApiError } from "../api/client";
import i18n from "../i18n";

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
const mockAdjust = vi.mocked(recordInventoryAdjustment);
const mockListMovements = vi.mocked(listInventoryMovements);

// Admin/Manager are admin; a claim-less session is a plain Worker (auth/claims).
const ADMIN = { sub: "u1", role: "Admin" };
const WORKER = { sub: "u1" };

const USD_ACCOUNT: Account = account({ name: "Farm" });

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

// F131: every capture form on this screen lives in a dialog now. Open the one
// under test, then scope fills to it — the forms still share labels
// ("Quantity", "Note"), but only one is mounted at a time. Assertions stay on
// the mock-call arguments.
const dialog = () => screen.getByRole("dialog");

function openDialog(opener: string): HTMLElement {
  fireEvent.click(screen.getByRole("button", { name: opener }));
  return dialog();
}

// A promise the test resolves by hand — holds a request open so the busy
// window is asserted deterministically, no timing guesses (client.test.ts idiom).
function deferred<T>() {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
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
    mockGetAccount.mockResolvedValue(account({ id: "a2", name: "JP Farm", currencyCode: "JPY", currencyMinorUnit: 0 }));
    mockCreate.mockResolvedValue({ id: "new1" });
    await renderReady(ADMIN);

    const form = openDialog("New item");
    fireEvent.change(within(form).getByLabelText("Item name *"), { target: { value: "Bulk Grain" } });
    fireEvent.change(within(form).getByLabelText("Category"), { target: { value: "Supplement" } });
    fireEvent.change(within(form).getByLabelText("Unit *"), { target: { value: "kg" } });
    fireEvent.change(within(form).getByLabelText(/Default cost/), { target: { value: "5" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Add item" }));
    });

    expect(mockCreate.mock.calls[0][0]).toEqual({
      name: "Bulk Grain", category: "Supplement", unit: "kg", defaultUnitCostMinorUnits: 5,
    });
    expect(mockCreate.mock.calls[0][1]).toEqual(expect.any(String)); // idempotency key
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument(); // success dismisses it
    expect(screen.getByText("Item created.")).toBeInTheDocument();
    expect(within(openDialog("New item")).getByLabelText("Item name *")).toHaveValue(""); // reset on success
  });

  it("sends a null default cost when the cost field is left blank", async () => {
    mockCreate.mockResolvedValue({ id: "new2" });
    await renderReady(ADMIN);

    const form = openDialog("New item");
    fireEvent.change(within(form).getByLabelText("Item name *"), { target: { value: "Water Additive" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Add item" }));
    });

    expect(mockCreate.mock.calls[0][0]).toMatchObject({ name: "Water Additive" });
    expect(mockCreate.mock.calls[0][0].defaultUnitCostMinorUnits).toBeNull();
  });
});

describe("InventoryPage edit", () => {
  it("saves an edit: id, changed name/unit/cost + key, parsing cost at the account scale (BHD 3dp)", async () => {
    // BHD has 3 decimals: "2.5" must become 2500 minor units, not 250 (a hard-coded
    // ×100 would fail here). The edit parse honours account.currencyMinorUnit.
    mockGetAccount.mockResolvedValue(account({ id: "a4", name: "BH Farm", currencyCode: "BHD", currencyMinorUnit: 3 }));
    mockUpdate.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    // Open the edit dialog from the Layer Feed row (admin-only "edit" control).
    const row = screen.getByRole("row", { name: /Layer Feed/ });
    fireEvent.click(within(row).getByRole("button", { name: "edit" }));

    // The dialog is seeded from the row before anything is changed.
    expect(within(dialog()).getByLabelText("Item name")).toHaveValue("Layer Feed");
    fireEvent.change(within(dialog()).getByLabelText("Item name"), { target: { value: "Layer Feed Plus" } });
    fireEvent.change(within(dialog()).getByLabelText("Unit"), { target: { value: "bag" } });
    fireEvent.change(within(dialog()).getByLabelText(/Default cost/), { target: { value: "2.5" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
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
    mockGetAccount.mockResolvedValue(account({ id: "a3", name: "Farm", currencyCode: code, currencyMinorUnit: minorUnit }));
    mockPurchase.mockResolvedValue({ lotId: "lot9" });
    await renderReady(ADMIN);
    await openItem(PACKAGING);

    const form = openDialog("Record purchase");
    fireEvent.change(within(form).getByLabelText(/Received/), { target: { value: "2026-07-10" } });
    fireEvent.change(within(form).getByLabelText(/Quantity/), { target: { value: "12.5" } });
    fireEvent.change(within(form).getByLabelText(/Unit cost/), { target: { value: typed } });
    fireEvent.change(within(form).getByLabelText(/Lot #/), { target: { value: "L-9" } });
    fireEvent.change(within(form).getByLabelText(/Expiry/), { target: { value: "2026-08-01" } });
    fireEvent.change(within(form).getByLabelText(/Note/), { target: { value: "carton batch" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record purchase" }));
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

    const form = openDialog("Record purchase");
    fireEvent.change(within(form).getByLabelText(/Quantity/), { target: { value: "3" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record purchase" }));
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

// #446 — the usage dialog moved to the /feed page; the panel keeps only a
// deep link that carries the opened item along. Recording behavior is pinned
// in FeedPage.test.tsx.
describe("InventoryPage feed usage link", () => {
  it("links a feedable item's panel to the Feed page with the item preselected", async () => {
    await renderReady(WORKER); // recording is open to everyone, not just admins
    await openItem(FEED);

    const link = screen.getByRole("link", { name: "Record usage on the Feed page" });
    expect(link).toHaveAttribute("href", "/feed?item=it1");
  });

  it("does not offer the usage link for a non-feedable category", async () => {
    await renderReady(WORKER);
    await openItem(PACKAGING);

    expect(screen.getByText(/aren't fed to flocks/)).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Record usage on the Feed page" })).not.toBeInTheDocument();
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

    const form = openDialog("Correct stock");
    // Move off the prefilled first lot (lot1) to the second (lot2).
    fireEvent.change(within(form).getByLabelText(/Lot/), { target: { value: "lot2" } });
    // negative quantity is passed through untouched for an "Adjustment"
    fireEvent.change(within(form).getByLabelText(/Quantity/), { target: { value: "-5" } });
    fireEvent.change(within(form).getByLabelText(/Reason/), { target: { value: "spillage" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record correction" }));
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

    const form = openDialog("Correct stock");
    fireEvent.change(within(form).getByLabelText(/Type/), { target: { value: "Discard" } });
    fireEvent.change(within(form).getByLabelText(/Quantity/), { target: { value: "5" } }); // positive input
    fireEvent.change(within(form).getByLabelText(/Reason/), { target: { value: "expired" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record correction" }));
    });

    expect(mockAdjust.mock.calls[0][1]).toMatchObject({
      type: "Discard", quantityDelta: -5, reason: "expired", // -Math.abs(5)
    });
  });
});

describe("InventoryPage pending states (#236)", () => {
  it("stock correction: the submit spins on its composite adjust scope while held; row verbs disable without spinning", async () => {
    const gate = deferred<{ movementId: string }>();
    mockListLots.mockResolvedValue([LOT]);
    mockAdjust.mockReturnValue(gate.promise);
    await renderReady(ADMIN);
    await openItem(FEED);

    const form = openDialog("Correct stock");
    fireEvent.change(within(form).getByLabelText(/Quantity/), { target: { value: "-5" } });
    fireEvent.change(within(form).getByLabelText(/Reason/), { target: { value: "spillage" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record correction" }));
    });

    // The composite key scope (adjust:<item>:<lot>) doubles as the pending
    // scope — exactly this one submit spins.
    const submit = within(dialog()).getByRole("button", { name: "Record correction" });
    expect(submit).toBeDisabled();
    expect(submit).toHaveAttribute("aria-busy", "true");
    // The lot select feeds that composite scope: changing the selection
    // mid-flight would re-point isPending at a scope nobody is running and
    // drop the spinner — so it locks with the flight (#242 review).
    expect(within(dialog()).getByLabelText(/Lot/)).toBeDisabled();
    // Behind the dialog, the row verbs are inert but not spinning.
    const row = screen.getByRole("row", { name: /Egg Cartons/ });
    const deactivate = within(row).getByRole("button", { name: "deactivate" });
    expect(deactivate).toBeDisabled();
    expect(deactivate).not.toHaveAttribute("aria-busy");

    await act(async () => {
      gate.resolve({ movementId: "adj1" });
    });
    // Success closes the dialog; nothing is left spinning.
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(document.querySelector('[aria-busy="true"]')).toBeNull();
  });
});

describe("InventoryPage idempotency & lifecycle", () => {
  it("replays the SAME key after a failed create and rotates it after success", async () => {
    mockCreate.mockRejectedValueOnce(new ApiError(500, "Server error", "boom"));
    mockCreate.mockResolvedValue({ id: "ok" });
    await renderReady(ADMIN);
    openDialog("New item");
    const name = () => within(dialog()).getByLabelText("Item name *");
    const submit = async () => {
      await act(async () => {
        fireEvent.click(within(dialog()).getByRole("button", { name: "Add item" }));
      });
    };

    fireEvent.change(name(), { target: { value: "One" } });
    await submit();
    // A failure keeps the dialog up, with the error inside it.
    expect(within(dialog()).getByText(/boom/)).toBeInTheDocument();

    fireEvent.change(name(), { target: { value: "One" } }); // retry after failure
    await submit();

    openDialog("New item"); // success closed it
    fireEvent.change(name(), { target: { value: "Two" } }); // next write after success
    await submit();

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

describe("InventoryPage dialog dismissal", () => {
  it("closes the create dialog on Cancel without writing", async () => {
    await renderReady(ADMIN);
    const form = openDialog("New item");
    fireEvent.change(within(form).getByLabelText("Item name *"), { target: { value: "Abandoned" } });

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockCreate).not.toHaveBeenCalled();
  });

  it("closes the edit dialog on Cancel without writing", async () => {
    await renderReady(ADMIN);
    fireEvent.click(within(screen.getByRole("row", { name: /Layer Feed/ })).getByRole("button", { name: "edit" }));

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockUpdate).not.toHaveBeenCalled();
  });

  it("closes the purchase dialog on Cancel without writing", async () => {
    await renderReady(ADMIN);
    await openItem(PACKAGING);
    openDialog("Record purchase");

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockPurchase).not.toHaveBeenCalled();
  });

  it("closes the correction dialog on Cancel without writing", async () => {
    mockListLots.mockResolvedValue([LOT]);
    await renderReady(ADMIN);
    await openItem(FEED);
    openDialog("Correct stock");

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockAdjust).not.toHaveBeenCalled();
  });
});

describe("InventoryPage role gating", () => {
  it("hides admin-only controls from a worker but keeps purchases available", async () => {
    await renderReady(WORKER);
    expect(screen.queryByRole("button", { name: "New item" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "edit" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "deactivate" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "activate" })).not.toBeInTheDocument();

    await openItem(FEED);
    // corrections are admin-only; recording a purchase stays open to everyone
    expect(screen.getByText(/Stock corrections need an admin/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Correct stock" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Record purchase" })).toBeInTheDocument();
  });
});

describe("InventoryPage errors scoped per dialog (#479)", () => {
  it("renders the create dialog's own failure inside it, not on the page", async () => {
    mockCreate.mockRejectedValueOnce(new ApiError(500, "Server error", "create boom"));
    await renderReady(ADMIN);
    const form = openDialog("New item");
    fireEvent.change(within(form).getByLabelText("Item name *"), { target: { value: "X" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Add item" }));
    });
    expect(within(dialog()).getByText("create boom")).toBeInTheDocument();
    expect(screen.getAllByText("create boom")).toHaveLength(1);
  });

  it("renders the edit dialog's own failure inside it, not on the page", async () => {
    mockUpdate.mockRejectedValueOnce(new ApiError(500, "Server error", "edit boom"));
    await renderReady(ADMIN);
    fireEvent.click(within(screen.getByRole("row", { name: /Layer Feed/ })).getByRole("button", { name: "edit" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
    });
    expect(within(dialog()).getByText("edit boom")).toBeInTheDocument();
    expect(screen.getAllByText("edit boom")).toHaveLength(1);
  });

  it("renders the purchase dialog's own failure inside it, not on the page", async () => {
    mockPurchase.mockRejectedValueOnce(new ApiError(500, "Server error", "purchase boom"));
    await renderReady(ADMIN);
    await openItem(PACKAGING);
    const form = openDialog("Record purchase");
    fireEvent.change(within(form).getByLabelText(/Quantity/), { target: { value: "3" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record purchase" }));
    });
    expect(within(dialog()).getByText("purchase boom")).toBeInTheDocument();
    expect(screen.getAllByText("purchase boom")).toHaveLength(1);
  });

  it("renders the correction dialog's own failure inside it, not on the page", async () => {
    mockListLots.mockResolvedValue([LOT]);
    mockAdjust.mockRejectedValueOnce(new ApiError(500, "Server error", "adjust boom"));
    await renderReady(ADMIN);
    await openItem(FEED);
    const form = openDialog("Correct stock");
    fireEvent.change(within(form).getByLabelText(/Quantity/), { target: { value: "-5" } });
    fireEvent.change(within(form).getByLabelText(/Reason/), { target: { value: "spillage" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record correction" }));
    });
    expect(within(dialog()).getByText("adjust boom")).toBeInTheDocument();
    expect(screen.getAllByText("adjust boom")).toHaveLength(1);
  });

  // The reachable bug: nothing on this screen closes one dialog when another
  // opens except the create/edit pair, so a create dialog and the purchase
  // dialog can be open at once. The quantity guard fires on every wrong
  // keystroke — no race needed — and with ONE shared error slot its message
  // used to leak into whichever other dialog happened to be open too.
  // Bypasses the HTML min constraint via a direct submit, same technique as
  // the WaterPage/FeedPage siblings (a real click never reaches the handler).
  it("keeps the purchase quantity validation message inside the purchase dialog, not another open dialog or the page", async () => {
    await renderReady(ADMIN);
    fireEvent.click(screen.getByRole("button", { name: "New item" })); // left open
    await openItem(PACKAGING);
    fireEvent.click(screen.getByRole("button", { name: "Record purchase" }));
    const purchaseDialog = screen.getByRole("dialog", { name: /Record purchase/ });

    fireEvent.change(within(purchaseDialog).getByLabelText(/Quantity/), { target: { value: "-1" } });
    const form = within(purchaseDialog).getByRole("button", { name: "Record purchase" }).closest("form")!;
    await act(async () => { fireEvent.submit(form); });

    expect(within(purchaseDialog).getByText("Quantity must be a positive number.")).toBeInTheDocument();
    expect(screen.getAllByText("Quantity must be a positive number.")).toHaveLength(1);
    expect(mockPurchase).not.toHaveBeenCalled();
  });

  // Displacement: the edit scope is per-item (`edit:${id}`), so a switch
  // straight from item A's failed edit to item B leaves A's verdict parked in
  // a slot nothing currently renders — until A's edit is REOPENED, which
  // would replay a dead session's failure into a fresh one (pi review of
  // #491). The row buttons behind the backdrop stay reachable to a screen
  // reader's virtual cursor (#480).
  it("does not replay a failed edit when that item's dialog is reopened after switching items", async () => {
    mockUpdate.mockRejectedValueOnce(new ApiError(500, "Server error", "edit boom"));
    await renderReady(ADMIN);
    fireEvent.click(within(screen.getByRole("row", { name: /Layer Feed/ })).getByRole("button", { name: "edit" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
    });
    expect(within(dialog()).getByText("edit boom")).toBeInTheDocument();

    // Switch straight to Egg Cartons' edit — no Cancel in between.
    fireEvent.click(within(screen.getByRole("row", { name: /Egg Cartons/ })).getByRole("button", { name: "edit" }));
    expect(within(dialog()).getByLabelText(/Item name/)).toHaveValue("Egg Cartons");
    expect(screen.queryByText("edit boom")).not.toBeInTheDocument();

    // Cancel, then reopen Layer Feed: a new session, no stale verdict.
    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    fireEvent.click(within(screen.getByRole("row", { name: /Layer Feed/ })).getByRole("button", { name: "edit" }));
    expect(within(dialog()).getByLabelText(/Item name/)).toHaveValue("Layer Feed");
    expect(screen.queryByText("edit boom")).not.toBeInTheDocument();
  });

  // The purchase/adjust dialogs are bound to the ACTIVE item's panel, so
  // opening another item's panel COULD rebind an open dialog to the new item
  // in place — title changes, nothing else does — leaving a stale quantity
  // and someone else's verdict sitting in a form that now claims to be about
  // a different item. `onOpen` closes both dialogs on a genuine item switch
  // instead (adversarial review of #491: rebinding silently is worse than
  // closing, since the leftover values are one Enter away from a purchase
  // recorded against the wrong item).
  it("closes an open purchase dialog, instead of rebinding it, when a different item is opened", async () => {
    mockPurchase.mockRejectedValueOnce(new ApiError(500, "Server error", "purchase boom"));
    await renderReady(ADMIN);
    await openItem(PACKAGING);
    const form = openDialog("Record purchase");
    fireEvent.change(within(form).getByLabelText(/Quantity/), { target: { value: "3" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record purchase" }));
    });
    expect(within(dialog()).getByText("purchase boom")).toBeInTheDocument();

    await openItem(FEED);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.queryByText("purchase boom")).not.toBeInTheDocument();
  });

  // Re-opening the SAME still-active item is not a displacement — the panel
  // heading re-renders (loadLedger runs again) but the open purchase dialog,
  // and whatever the user has typed into it, must survive.
  it("keeps an open purchase dialog and its typed values when the same item is opened again", async () => {
    await renderReady(ADMIN);
    await openItem(PACKAGING);
    const form = openDialog("Record purchase");
    fireEvent.change(within(form).getByLabelText(/Quantity/), { target: { value: "3" } });

    // Not openItem(PACKAGING): its heading wait would collide with the still-
    // open dialog's own title naming the same item.
    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Egg Cartons/ })).getByRole("button", { name: "open" }));
    });
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    expect(within(dialog()).getByLabelText(/Quantity/)).toHaveValue(3);
  });

  // The panel's own Close button (`setActive(null)`) does not run the
  // onOpen guard — `active` is already null when a DIFFERENT item is opened
  // next, so an id comparison alone would miss it and the purchase dialog
  // would spring back open over the new item, stale quantity and all.
  it("does not resurrect a purchase dialog for a new item after the panel was closed and reopened elsewhere", async () => {
    await renderReady(ADMIN);
    await openItem(PACKAGING);
    const form = openDialog("Record purchase");
    fireEvent.change(within(form).getByLabelText(/Quantity/), { target: { value: "3" } });

    // The PANEL's own close link ("close", lowercase) — not the purchase
    // dialog's own "X" (accessible name "Close"), which already runs
    // `closePurchase` via `onClose` and would pass this test regardless of
    // the guard under test.
    fireEvent.click(screen.getByRole("button", { name: "close" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();

    await openItem(FEED);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("keeps a background ledger-read failure off an open dialog and puts it on the page instead", async () => {
    mockListMovements.mockRejectedValueOnce(new ApiError(500, "Server error", "ledger down"));
    await renderReady(ADMIN);
    const createDialogEl = openDialog("New item");
    const row = screen.getByRole("row", { name: /Layer Feed/ });
    await act(async () => {
      fireEvent.click(within(row).getByRole("button", { name: "open" }));
    });

    expect(await screen.findByText("Could not load the movement ledger.")).toBeInTheDocument();
    expect(within(createDialogEl).queryByText("Could not load the movement ledger.")).not.toBeInTheDocument();
    expect(screen.getAllByText("Could not load the movement ledger.")).toHaveLength(1);
  });

  it("keeps a page failure visible after opening a dialog and running a failing dialog write", async () => {
    mockListMovements.mockRejectedValueOnce(new ApiError(500, "Server error", "ledger down"));
    mockCreate.mockRejectedValueOnce(new ApiError(500, "Server error", "create boom"));
    await renderReady(ADMIN);

    const row = screen.getByRole("row", { name: /Layer Feed/ });
    await act(async () => {
      fireEvent.click(within(row).getByRole("button", { name: "open" }));
    });
    expect(await screen.findByText("Could not load the movement ledger.")).toBeInTheDocument();

    const form = openDialog("New item");
    fireEvent.change(within(form).getByLabelText("Item name *"), { target: { value: "X" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Add item" }));
    });

    expect(screen.getByText("Could not load the movement ledger.")).toBeInTheDocument();
    expect(within(dialog()).getByText("create boom")).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 16, batch B3 — the biggest B3 screen)
// ---------------------------------------------------------------------------

// `inventory` is English-only (not in TRANSLATED_NAMESPACES — see
// translations-status.ts), so under ANY UI language the rendered text falls
// back to this exact English string, same as a still-hardcoded literal would
// render — asserting it, even under a non-English locale, would prove nothing
// (CONTRIBUTING-i18n.md's fallback trap). Swap the catalog value at runtime
// instead, the same i18n.addResource technique the other batches use, so each
// marker only renders if the screen actually reads the catalog rather than a
// literal that happens to still match it.
describe("InventoryPage i18n wiring (#182, Task 16)", () => {
  function withOverride(ns: string, key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", ns, key) as string;
    i18n.addResource("en", ns, key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", ns, key, original);
    });
  }

  it("reads the heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("inventory", "title", "TITLE-MARKER", async () => {
      await renderReady(ADMIN);
      expect(screen.getByRole("heading", { name: "TITLE-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Feed & inventory" })).not.toBeInTheDocument();
    });
  });

  it("reads the new-item button label from the catalog, not a hardcoded literal", async () => {
    await withOverride("inventory", "newItemButton", "NEW-ITEM-MARKER", async () => {
      await renderReady(ADMIN);
      expect(screen.getByRole("button", { name: "NEW-ITEM-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "New item" })).not.toBeInTheDocument();
    });
  });

  // Proves the items-table Category cell AND the create-dialog Category
  // picker both read the inventory-category ENUM label from the catalog (via
  // inventoryCategoryLabel), not the raw wire value "Feed" or a hardcoded
  // literal — FEED's category is "Feed".
  it("reads the inventory-category enum label from the catalog for both the table cell and the picker", async () => {
    await withOverride("enums", "inventoryCategory.Feed", "FEED-MARKER", async () => {
      await renderReady(ADMIN);
      const feedRow = screen.getByRole("row", { name: /Layer Feed/ });
      expect(within(feedRow).getByText("FEED-MARKER")).toBeInTheDocument();

      const form = openDialog("New item");
      expect(within(form).getByRole("option", { name: "FEED-MARKER" })).toBeInTheDocument();
      expect(within(form).queryByRole("option", { name: "Feed" })).not.toBeInTheDocument();
    });
  });

  // Proves the "not feedable" message reads BOTH the catalog template AND the
  // enum-labelled (inventoryCategoryLabel) category — a hardcoded literal, or
  // one that interpolated the raw wire value instead of the label, would
  // still pass a naive check since "Packaging" is its own identity label, but
  // would fail to pick up the catalog marker text at all.
  it("interpolates the enum-labelled category into the not-feedable message from the catalog", async () => {
    await withOverride(
      "inventory", "notFeedableMessage", "NOT-FEEDABLE-MARKER {{category}} MARKER-END",
      async () => {
        await renderReady(ADMIN);
        await openItem(PACKAGING);
        expect(screen.getByText("NOT-FEEDABLE-MARKER Packaging MARKER-END")).toBeInTheDocument();
        expect(screen.queryByText(/aren't fed to flocks/)).not.toBeInTheDocument();
      },
    );
  });

  // Proves the movement LEDGER's Type cell reads the inventory-movement ENUM
  // label from the catalog (via inventoryMovementLabel) — MOVEMENT's type is
  // "Purchase".
  it("reads the ledger movement-type enum label from the catalog", async () => {
    mockListMovements.mockResolvedValue([MOVEMENT]);
    await withOverride("enums", "inventoryMovement.Purchase", "PURCHASE-MARKER", async () => {
      await renderReady(ADMIN);
      await openItem(FEED);
      const mvRow = screen.getByRole("row", { name: /PURCHASE-MARKER/ });
      expect(within(mvRow).getByText("PURCHASE-MARKER")).toBeInTheDocument();
    });
  });

  // The Correct-stock Type PICKER shows DECORATED screen copy ("Adjustment
  // (±)"/"Discard (write-off)"), not the ledger's inventoryMovementLabel
  // identity text — this is the deliberate split called out in en.ts's
  // `inventory` namespace header comment. This test proves the picker DOES
  // read the screen-copy catalog key.
  it("wires the adjust-type picker's decorated option to inventory screen copy", async () => {
    mockListLots.mockResolvedValue([LOT]);
    await withOverride("inventory", "adjustTypeAdjustmentOption", "ADJ-SCREEN-MARKER", async () => {
      await renderReady(ADMIN);
      await openItem(FEED);
      const form = openDialog("Correct stock");
      expect(within(form).getByRole("option", { name: "ADJ-SCREEN-MARKER" })).toBeInTheDocument();
    });
  });

  // ...and this test proves the picker's option is UNAFFECTED by the ledger's
  // enum helper — overriding enums:inventoryMovement.Adjustment must NOT
  // change the picker text, confirming the two displays are wired to
  // genuinely different sources, not just coincidentally identical English.
  it("does not route the adjust-type picker option through the ledger's movement-type enum label", async () => {
    mockListLots.mockResolvedValue([LOT]);
    await withOverride("enums", "inventoryMovement.Adjustment", "ENUM-MARKER", async () => {
      await renderReady(ADMIN);
      await openItem(FEED);
      const form = openDialog("Correct stock");
      expect(within(form).queryByRole("option", { name: "ENUM-MARKER" })).not.toBeInTheDocument();
      expect(within(form).getByRole("option", { name: "Adjustment (±)" })).toBeInTheDocument();
    });
  });

  // Proves the items-table Status badge reads the `status` ENUM label from
  // the catalog (via statusLabel) — FEED is active.
  it("reads the status badge enum label from the catalog, not a hardcoded literal", async () => {
    await withOverride("enums", "status.Active", "ACTIVE-MARKER", async () => {
      await renderReady(ADMIN);
      const feedRow = screen.getByRole("row", { name: /Layer Feed/ });
      expect(within(feedRow).getByText("ACTIVE-MARKER")).toBeInTheDocument();
      expect(within(feedRow).queryByText("Active")).not.toBeInTheDocument();
    });
  });

  // Proves the opened item's heading interpolates name/quantity/unit (all
  // free-form DATA, left raw) into the catalog template — not a hardcoded
  // literal.
  it("interpolates item data into the item-panel heading from the catalog", async () => {
    await withOverride("inventory", "itemPanelHeading", "PANEL-MARKER {{name}} / {{quantity}} / {{unit}}", async () => {
      await renderReady(ADMIN);
      await openItem(FEED);
      expect(screen.getByRole("heading", { name: "PANEL-MARKER Layer Feed / 200 / kg" })).toBeInTheDocument();
    });
  });

  // The success message is built with the imperative i18n.t() (onCreate is an
  // event handler, not render — see CONTRIBUTING-i18n.md).
  it("reads the item-created success message from the catalog, not a hardcoded literal", async () => {
    mockCreate.mockResolvedValue({ id: "new1" });
    await withOverride("inventory", "itemCreatedMessage", "CREATED-MARKER", async () => {
      await renderReady(ADMIN);
      const form = openDialog("New item");
      fireEvent.change(within(form).getByLabelText("Item name *"), { target: { value: "Bulk Grain" } });
      await act(async () => {
        fireEvent.click(within(dialog()).getByRole("button", { name: "Add item" }));
      });
      expect(screen.getByText("CREATED-MARKER")).toBeInTheDocument();
      expect(screen.queryByText("Item created.")).not.toBeInTheDocument();
    });
  });

  // The ledger-load-failure message is built with the imperative i18n.t()
  // (onOpen catches the rejection in its own handled try/catch — see the
  // functional test above for why this rejection path is safe to test).
  it("reads the ledger-load-failure message from the catalog, not a hardcoded literal", async () => {
    mockListMovements.mockRejectedValueOnce(new ApiError(500, "Server error", "ledger down"));
    await withOverride("inventory", "loadLedgerFailed", "LEDGER-FAILED-MARKER", async () => {
      await renderReady(ADMIN);
      const row = screen.getByRole("row", { name: /Layer Feed/ });
      await act(async () => {
        fireEvent.click(within(row).getByRole("button", { name: "open" }));
      });
      expect(await screen.findByText("LEDGER-FAILED-MARKER")).toBeInTheDocument();
      expect(screen.queryByText("Could not load the movement ledger.")).not.toBeInTheDocument();
    });
  });
});
