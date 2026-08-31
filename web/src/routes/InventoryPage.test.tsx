import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act, waitFor, cleanup } from "@testing-library/react";
import { InventoryPage } from "./InventoryPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { account, NO_RECORD_HISTORY } from "../test/fixtures";
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
  ...NO_RECORD_HISTORY,
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

    expect(mockListMovements).toHaveBeenCalledWith("it1", { limit: 100, offset: 0 });
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

  // #511 round 2 — restored. Round 1 briefly rewrote this to assert the
  // ledger error was CLEARED by an unrelated create, which was a consequence
  // of wrapping every write in the ledger's runWrite, not a behaviour anyone
  // wanted. Increment 5 removed that coupling, so the original guarantee is
  // back: a dialog write that has nothing to do with the ledger must not
  // touch the ledger's error, and the dialog's own failure stays in the
  // dialog (#479).
  it("keeps a page failure visible after opening a dialog and running a failing dialog write", async () => {
    mockListMovements.mockRejectedValueOnce(new ApiError(500, "Server error", "ledger down"));
    mockCreate.mockRejectedValueOnce(new ApiError(500, "Server error", "create boom"));
    await renderReady(ADMIN);
    const row = screen.getByRole("row", { name: /Layer Feed/ });
    await act(async () => {
      fireEvent.click(within(row).getByRole("button", { name: "open" }));
    });
    await screen.findByText("Could not load the movement ledger.");

    openDialog("New item");
    fireEvent.change(within(dialog()).getByLabelText(/Name/i), { target: { value: "Grit" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Add item" }));
    });

    // The create never touched the ledger, so the ledger's failure is untouched.
    expect(screen.getByText("Could not load the movement ledger.")).toBeInTheDocument();
    // And the create's own failure stays inside the create dialog.
    expect(within(dialog()).getByText("create boom")).toBeInTheDocument();
    expect(screen.getAllByText("create boom")).toHaveLength(1);
    // The unrelated create must not have re-read the ledger at all: one call,
    // from opening the item. This is the assertion that pins WHY the error
    // survived, rather than just that it did.
    expect(mockListMovements).toHaveBeenCalledTimes(1);
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

// #511 — the movement ledger asked for 100 rows and rendered them with no
// pager, so the oldest movements were unreachable. These pin the paged
// behaviour and the per-item identity that keeps one item's rows off
// another item's heading.
const invMovementRow = (over: Partial<InventoryMovement> = {}): InventoryMovement => ({
  id: "im0", inventoryItemId: "it1", inventoryLotId: "lot1", date: "2026-07-01",
  type: "Purchase", quantityDelta: 1, unit: "kg", flockId: null, note: "note",
  referenceType: null, referenceId: null, ...over,
});
const invMovementPage = (n: number, prefix = "im") =>
  Array.from({ length: n }, (_, i) =>
    invMovementRow({ id: `${prefix}${i}`, note: `${prefix} note ${String(i).padStart(3, "0")}` }));

describe("InventoryPage ledger paging (#511)", () => {
  it("reaches a movement past the first page through load more", async () => {
    mockListMovements.mockResolvedValueOnce(invMovementPage(100));
    await renderReady(ADMIN);
    await openItem(FEED);
    await screen.findByText("im note 000");
    expect(mockListMovements).toHaveBeenCalledWith("it1",
      expect.objectContaining({ limit: 100, offset: 0 }));

    mockListMovements.mockResolvedValueOnce([invMovementRow({ id: "old", note: "oldest row" })]);
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "load more" }));
    });

    expect(mockListMovements).toHaveBeenLastCalledWith("it1",
      expect.objectContaining({ offset: 100 }));
    expect(await screen.findByText("oldest row")).toBeInTheDocument();
    expect(screen.getByText("im note 000")).toBeInTheDocument();
  });

  it("withdraws the pager on a short page", async () => {
    mockListMovements.mockResolvedValueOnce(invMovementPage(3));
    await renderReady(ADMIN);
    await openItem(FEED);
    await screen.findByText("im note 000");
    expect(screen.queryByRole("button", { name: "load more" })).not.toBeInTheDocument();
  });

  it("hides the previous item's movements while the next item's ledger is loading", async () => {
    // INV-4, render half. Item one's page has ALREADY LANDED, so `rows` holds
    // it; the user then switches straight to item two and that replacement is
    // in flight. The rows still in `rows` belong to an item the user has
    // left, and `reloading` is the only state that knows it.
    mockListMovements.mockResolvedValueOnce([invMovementRow({ id: "a1", note: "item one row" })]);
    await renderReady(ADMIN);
    await openItem(FEED);
    await screen.findByText("item one row");

    let releaseSecond!: (rows: InventoryMovement[]) => void;
    mockListMovements.mockReturnValueOnce(new Promise((r) => { releaseSecond = r; }));
    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Egg Cartons/ })).getByRole("button", { name: "open" }));
    });

    expect(screen.queryByText("item one row")).not.toBeInTheDocument();

    await act(async () => {
      releaseSecond([invMovementRow({ id: "b1", inventoryItemId: "it2", note: "item two row" })]);
    });
    expect(await screen.findByText("item two row")).toBeInTheDocument();
  });

  it("refreshes every loaded page after recording a purchase", async () => {
    mockListMovements.mockResolvedValueOnce(invMovementPage(100));
    await renderReady(ADMIN);
    await openItem(PACKAGING);
    await screen.findByText("im note 000");

    mockListMovements.mockResolvedValueOnce([invMovementRow({ id: "old", note: "oldest row" })]);
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "load more" }));
    });
    await screen.findByText("oldest row");

    // The refresh has to be OBSERVABLE: the post-write fixtures carry
    // DIFFERENT text from the pre-write ones, same technique as FlocksPage's
    // equivalent (#511).
    mockPurchase.mockResolvedValue({ lotId: "lot9" });
    mockListMovements.mockResolvedValueOnce(
      invMovementPage(100).map((m, i) => ({ ...m, note: `refreshed ${String(i).padStart(3, "0")}` })));
    mockListMovements.mockResolvedValueOnce([invMovementRow({ id: "old", note: "refreshed oldest" })]);

    const form = openDialog("Record purchase");
    fireEvent.change(within(form).getByLabelText(/Quantity/), { target: { value: "3" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record purchase" }));
    });

    // Both pages the user had loaded were re-read, not just page one.
    expect(await screen.findByText("refreshed oldest")).toBeInTheDocument();
    expect(screen.getByText("refreshed 000")).toBeInTheDocument();
  });

  // Mutation table row 6: `clearKey` must stay AFTER `runWrite` resolves, not
  // inside its callback — otherwise a failed refresh still rotates the key,
  // and a retry mints a fresh write instead of replaying the idempotent one.
  it("keeps the same key when the post-write refresh fails, so a retry replays the write", async () => {
    mockPurchase.mockResolvedValue({ lotId: "lot9" });
    // The write succeeds; refreshAll's own read (listInventoryItems) is what
    // fails — this is the refresh runWrite wraps, not the write itself.
    mockListItems.mockResolvedValueOnce([FEED, PACKAGING, INACTIVE]); // initial mount load
    await renderReady(ADMIN);
    await openItem(PACKAGING);

    const form = openDialog("Record purchase");
    fireEvent.change(within(form).getByLabelText(/Quantity/), { target: { value: "3" } });
    mockListItems.mockRejectedValueOnce(new ApiError(500, "Server error", "refresh down"));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record purchase" }));
    });
    // The failed refresh reports through the purchase dialog's own slot.
    expect(within(dialog()).getByText("refresh down")).toBeInTheDocument();

    // Retry with the refresh healthy this time.
    mockListItems.mockResolvedValueOnce([FEED, PACKAGING, INACTIVE]);
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record purchase" }));
    });

    const k1 = mockPurchase.mock.calls[0][2];
    const k2 = mockPurchase.mock.calls[1][2];
    expect(k2).toBe(k1); // the failed refresh kept the key → exact replay
  });

  it("re-reads the ledger when the already-open item is opened again", async () => {
    // Pre-#511 this screen re-read the ledger on EVERY open click. The hook
    // only reloads when `activeId` CHANGES, so re-opening the same item is
    // exactly the case that silently stopped refreshing.
    mockListMovements.mockResolvedValueOnce([invMovementRow({ id: "a1", note: "stale row" })]);
    await renderReady(ADMIN);
    await openItem(FEED);
    await screen.findByText("stale row");
    expect(mockListMovements).toHaveBeenCalledTimes(1);

    mockListMovements.mockResolvedValueOnce([invMovementRow({ id: "a2", note: "fresh row" })]);
    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Layer Feed/ })).getByRole("button", { name: "open" }));
    });

    expect(mockListMovements).toHaveBeenCalledTimes(2);
    expect(await screen.findByText("fresh row")).toBeInTheDocument();
    expect(screen.queryByText("stale row")).not.toBeInTheDocument();
  });

  it("clears the previous item's lots the moment a different item is opened", async () => {
    // #511 round 3 — the version of this test written in round 2 asserted on
    // the panel AFTER releasing item one's late lots response, which the
    // pre-existing `lotsRequest` ticket already rejected on its own: it passed
    // with the whole fix deleted. The window that actually needs guarding is
    // the one BEFORE item two's lots land, where item one's list is still the
    // only thing in `lots`.
    //
    // Adapted from the runbook literal (reported): a lot's NUMBER text
    // ("A-1") only ever renders inside the Adjust dialog's <select>, and
    // `onOpen`'s displacement guard force-closes that dialog on every item
    // switch regardless of whether `lots` itself was cleared — so a
    // closed-dialog absence of "A-1" would pass whether or not the clear ran.
    // The `lots.length > 0` gate on the "Correct stock" button lives
    // directly on the panel, untouched by the dialog's open/close state, so
    // it is what actually observes whether `lots` still holds the departed
    // item's rows while the next item's own read is in flight.
    mockListMovements.mockResolvedValue([]);
    mockListLots.mockResolvedValueOnce([
      { ...LOT, id: "lotA", lotNumber: "A-1" },
    ]);
    await renderReady(ADMIN);
    await openItem(FEED);
    await screen.findByRole("button", { name: "Correct stock" });

    // Item two's lots never settle during this test: the assertion is about
    // what is on screen while they are still in flight.
    mockListLots.mockReturnValueOnce(new Promise(() => {}));
    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Egg Cartons/ })).getByRole("button", { name: "open" }));
    });

    expect(screen.queryByRole("button", { name: "Correct stock" })).not.toBeInTheDocument();
  });

  // One test, two locales, per the round-2 runbook. Two adaptations from a
  // literal read of the CustomersPage precedent, both reported: (1)
  // renderReady()/render doesn't auto-unmount between calls within one test
  // — only afterEach does that between tests — so an explicit cleanup() is
  // needed between the two locale checks; (2) openItem()'s hardcoded "open"
  // button name is English-only — inventory:openButton is itself translated
  // ("abrir"/"buksan") — so opening the item under es/tl needs the CURRENT
  // locale's label, read via i18n.t rather than the helper.
  it("renders the pager label from the active locale", async () => {
    mockListMovements.mockResolvedValueOnce(invMovementPage(100));
    await i18n.changeLanguage("es");
    try {
      await renderReady(ADMIN);
      const openLabel = i18n.t("inventory:openButton");
      await act(async () => {
        fireEvent.click(within(screen.getByRole("row", { name: /Layer Feed/ })).getByRole("button", { name: openLabel }));
      });
      await screen.findByText("im note 000");
      expect(screen.getByRole("button", { name: "cargar más" })).toBeInTheDocument();
    } finally {
      await i18n.changeLanguage("en");
      cleanup();
    }

    mockListMovements.mockResolvedValueOnce(invMovementPage(100));
    await i18n.changeLanguage("tl");
    try {
      await renderReady(ADMIN);
      const openLabel = i18n.t("inventory:openButton");
      await act(async () => {
        fireEvent.click(within(screen.getByRole("row", { name: /Layer Feed/ })).getByRole("button", { name: openLabel }));
      });
      await screen.findByText("im note 000");
      expect(screen.getByRole("button", { name: "mag-load pa" })).toBeInTheDocument();
    } finally {
      await i18n.changeLanguage("en");
    }
  });

  it("blames the lots read, not the movement ledger, when lots fail to load", async () => {
    // #511 round 4 — before the round-1 split, one catch covered a combined
    // movements+lots read and "Could not load the movement ledger." was
    // accurate for both. After the split this catch only ever wraps loadLots,
    // so a lots failure was reporting a failure of a read that succeeded.
    mockListMovements.mockResolvedValue([]);
    mockListLots.mockRejectedValueOnce(new ApiError(500, "Server error", "lots down"));
    await renderReady(ADMIN);
    await openItem(FEED);

    expect(await screen.findByText("Could not load the item's lots.")).toBeInTheDocument();
    expect(screen.queryByText("Could not load the movement ledger.")).not.toBeInTheDocument();
  });

  it("keeps the loaded movements on screen when a load-more fails, and offers the retry", async () => {
    // AC3. usePagedList keeps `rows` and `hasMore` when an EXTENSION fails —
    // only a failed REPLACEMENT empties them — so the screen must not throw
    // the table away on `error`. Paging deep and hitting one transient failure
    // must not cost the user everything already on screen.
    mockListMovements.mockResolvedValueOnce(invMovementPage(100));
    await renderReady(ADMIN);
    await openItem(FEED);
    await screen.findByText("im note 000");

    mockListMovements.mockRejectedValueOnce(new ApiError(500, "Server error", "ledger down"));
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "load more" }));
    });

    // The error is shown...
    expect(screen.getByText("Could not load the movement ledger.")).toBeInTheDocument();
    // ...and the rows already loaded are STILL THERE, with the pager to retry.
    expect(screen.getByText("im note 000")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "load more" })).toBeInTheDocument();
  });

  it("does not report a superseded item's lots failure over the item now open", async () => {
    // INV-1, failure path. The ticket already drops a stale SUCCESS; a stale
    // REJECTION is exactly as stale and must not paint over a healthy panel.
    mockListMovements.mockResolvedValue([]);
    let failFirst!: (err: unknown) => void;
    mockListLots.mockReturnValueOnce(new Promise((_, rej) => { failFirst = rej; }));
    await renderReady(ADMIN);
    await openItem(FEED);

    mockListLots.mockResolvedValueOnce([]);
    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /Egg Cartons/ })).getByRole("button", { name: "open" }));
    });

    await act(async () => {
      failFirst(new ApiError(500, "Server error", "lots down"));
    });

    expect(screen.queryByText("Could not load the item's lots.")).not.toBeInTheDocument();
  });

  it("fails the write and keeps its key when the post-write LOTS re-read fails", async () => {
    // INV-6, via the lots branch specifically. The existing key-survival test
    // drives refreshAll's fetchItems() failure; nothing drove its loadLots
    // failure, which is exactly the gap that let round 5's proposed fix look
    // safe. A live lots rejection must still fail the write.
    mockListMovements.mockResolvedValue([]);
    mockPurchase.mockResolvedValue({ lotId: "lot9" });
    await renderReady(ADMIN);
    await openItem(PACKAGING);

    const form = openDialog("Record purchase");
    fireEvent.change(within(form).getByLabelText(/Quantity/), { target: { value: "3" } });
    mockListLots.mockRejectedValueOnce(new ApiError(500, "Server error", "lots down"));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record purchase" }));
    });

    // The write is reported FAILED even though the POST succeeded, because its
    // refresh did not: the dialog stays open carrying the failure.
    expect(within(dialog()).getByText("lots down")).toBeInTheDocument();

    // And the key survived, so a retry replays rather than repeats.
    mockListLots.mockResolvedValueOnce([]);
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record purchase" }));
    });
    expect(mockPurchase.mock.calls[1][2]).toBe(mockPurchase.mock.calls[0][2]);
  });
});
