import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act, waitFor } from "@testing-library/react";
import { useLocation, useNavigate } from "react-router";
import { SalesPage } from "./SalesPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { account, NO_RECORD_HISTORY, RECORD_HISTORY } from "../test/fixtures";
import i18n from "../i18n";
import {
  addOrderItem, cancelOrder, confirmOrder, createOrder, getCustomer, getOrder, listCustomers, listEggGrades,
  listEggUnitConversions, listOrderPayments, listOrders, listProducts, recordPayment,
  removeOrderItem, updateOrderItem, voidOrder, voidPayment,
} from "../api/cluckwork";
import type { Customer, EggGrade, EggUnitConversion, OrderItem, Product, SalesOrder } from "../api/cluckwork";
import { ApiError } from "../api/client";

// Keep the REAL formatMoney + parseMoneyToMinorUnits (the money math under test)
// via importOriginal; stub only the network seam. Every network call the screen
// can make is stubbed — even the ones no current test triggers (confirm/cancel/
// void/remove/pay) — so a future edit that clicks them can't silently hit the
// real fetch client. The screen also uses useAuth + the router → renderWithProviders.
vi.mock("../api/cluckwork", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/cluckwork")>();
  return {
    ...actual,
    listCustomers: vi.fn(),
    listProducts: vi.fn(),
    listEggGrades: vi.fn(),
    listEggUnitConversions: vi.fn(),
    listOrders: vi.fn(),
    listOrderPayments: vi.fn(),
    createOrder: vi.fn(),
    getOrder: vi.fn(),
    addOrderItem: vi.fn(),
    updateOrderItem: vi.fn(),
    removeOrderItem: vi.fn(),
    cancelOrder: vi.fn(),
    confirmOrder: vi.fn(),
    voidOrder: vi.fn(),
    recordPayment: vi.fn(),
    voidPayment: vi.fn(),
    getFlock: vi.fn(),
  getCustomer: vi.fn(),
};
});

const mockListCustomers = vi.mocked(listCustomers);
const mockListProducts = vi.mocked(listProducts);
const mockListEggGrades = vi.mocked(listEggGrades);
const mockListEggUnitConversions = vi.mocked(listEggUnitConversions);
const mockListOrders = vi.mocked(listOrders);
const mockListOrderPayments = vi.mocked(listOrderPayments);
const mockCreateOrder = vi.mocked(createOrder);
const mockGetOrder = vi.mocked(getOrder);
const mockAddOrderItem = vi.mocked(addOrderItem);
const mockUpdateOrderItem = vi.mocked(updateOrderItem);
const mockRecordPayment = vi.mocked(recordPayment);
const mockGetCustomer = vi.mocked(getCustomer);

const CUSTOMER: Customer = {
  id: "c1", name: "Acme Eggs", phone: "555", email: null, address: null, note: null, version: 0,
};

// #512 US5 (T055) — canonical 8-4-4-4-12 GUIDs for the URL-owned customer
// filter. GUID_A is deliberately typed UPPERCASE in a URL to exercise
// normalization; GUID_MALFORMED is a well-formed-LOOKING but short value.
const GUID_A = "aaaaaaaa-1111-1111-1111-111111111111";
const GUID_B = "22222222-2222-2222-2222-222222222222";
const GUID_MALFORMED = "not-a-guid";
const CUSTOMER_A: Customer = { ...CUSTOMER, id: GUID_A, name: "Filtered Farm A" };
const CUSTOMER_B: Customer = { ...CUSTOMER, id: GUID_B, name: "Filtered Farm B" };

// A sibling of SalesPage inside the SAME MemoryRouter: exposes the live
// location (for asserting `search`) and captures `navigate` (module-scoped,
// reused across renders in the same test) so a test can drive selection,
// Back (`navigate(-1)`), and Forward (`navigate(1)`) the same way a real
// browser would — MemoryRouter's own history stack, not window.history.
let capturedNavigate: ReturnType<typeof useNavigate> | null = null;
function RouterProbe() {
  const location = useLocation();
  capturedNavigate = useNavigate();
  return <div data-testid="location-probe">{location.pathname}{location.search}</div>;
}
function probeSearch(): string {
  return screen.getByTestId("location-probe").textContent!.replace(/^\/sales/, "");
}
// Only gr1 is saleable → the picker offers PRODUCT_A only; gr2/PRODUCT_B exists
// solely to resolve the second line's display name (allProducts).
const GRADE: EggGrade = { ...NO_RECORD_HISTORY, id: "gr1", farmId: "farm1", name: "Grade A", gradeType: "Size", sortOrder: 1, isSaleable: true, dailyEntryKind: "Manual", active: true };
const PRODUCT_A: Product = {
  id: "p1", name: "Grade A Dozen", productType: "Egg", defaultUnit: "Dozen",
  defaultPriceMinorUnits: 300, currencyCode: "USD", currencyMinorUnit: 2,
  eggGradeId: "gr1", notes: null, active: true, version: 1,
};
const PRODUCT_B: Product = {
  id: "p2", name: "Grade B Tray", productType: "Egg", defaultUnit: "Tray",
  defaultPriceMinorUnits: 1000, currencyCode: "USD", currencyMinorUnit: 2,
  eggGradeId: "gr2", notes: null, active: true, version: 1,
};

// An empty draft in the given currency — used to prove the parse honours the
// ORDER's currencyMinorUnit, not a hard-coded 2.
function draftEmpty(currencyMinorUnit: number, currencyCode: string, id = "o1"): SalesOrder {
  return {
    ...NO_RECORD_HISTORY,
    id, customerId: "c1", customerName: "Acme Eggs", referenceNumber: "SO-1", orderDate: "2026-07-20",
    status: "Draft", totalMinorUnits: 0, currencyCode, currencyMinorUnit, voidReason: null, items: [],
  };
}

// A single-line draft for edit/display, price + scale parametrised by currency.
function draftWithItem(currencyMinorUnit: number, currencyCode: string, unitPrice: number, id = "o5"): SalesOrder {
  const item: OrderItem = {
    id: "e1", productId: "p1", eggGradeId: "gr1", unit: "Dozen", baseUnitFactor: 12,
    quantity: 3, quantityBase: 36, unitPriceMinorUnits: unitPrice, currencyCode, currencyMinorUnit,
  };
  return { ...draftEmpty(currencyMinorUnit, currencyCode, id), referenceNumber: "SO-5", items: [item], totalMinorUnits: unitPrice * 3 };
}

// Two lines with DIFFERENT line totals so the order total can't be confused with
// any single line: A = 300×3 = 900 (9.00), B = 1000×2 = 2000 (20.00), order 2900.
const ITEM_A: OrderItem = {
  id: "it1", productId: "p1", eggGradeId: "gr1", unit: "Dozen", baseUnitFactor: 12,
  quantity: 3, quantityBase: 36, unitPriceMinorUnits: 300, currencyCode: "USD", currencyMinorUnit: 2,
};
const ITEM_B: OrderItem = {
  id: "it2", productId: "p2", eggGradeId: "gr2", unit: "Tray", baseUnitFactor: 30,
  quantity: 2, quantityBase: 60, unitPriceMinorUnits: 1000, currencyCode: "USD", currencyMinorUnit: 2,
};
const DRAFT_TWO: SalesOrder = {
  ...draftEmpty(2, "USD", "o2"), referenceNumber: "SO-2", totalMinorUnits: 2900, items: [ITEM_A, ITEM_B],
};

// #445 — the conversions feeding the unit-clarity surfaces (unit-aware
// quantity label text comes from i18n; the FACTORS come from here). "Case" is
// deliberately inactive: the no-active-definition fallback (bare labels, no
// hint) needs a real selling unit to exercise it through.
const CONVERSIONS: EggUnitConversion[] = [
  { id: "cv1", unitCode: "Individual", eggsPerUnit: 1, active: true, version: 1 },
  { id: "cv2", unitCode: "Dozen", eggsPerUnit: 12, active: true, version: 1 },
  { id: "cv3", unitCode: "Tray", eggsPerUnit: 30, active: true, version: 1 },
  { id: "cv4", unitCode: "Case", eggsPerUnit: 360, active: false, version: 1 },
];

// role irrelevant to add/update/display (Admin only unlocks void + payments,
// which these tests don't touch) — just a stable authenticated session.
const ADMIN = { sub: "u1", role: "Admin" };

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  mockListCustomers.mockResolvedValue([CUSTOMER]);
  mockListProducts.mockResolvedValue([PRODUCT_A, PRODUCT_B]);
  mockListEggGrades.mockResolvedValue([GRADE]);
  mockListEggUnitConversions.mockResolvedValue(CONVERSIONS);
  mockListOrders.mockResolvedValue([]);
  mockListOrderPayments.mockResolvedValue({
    items: [], paidMinorUnits: 0, outstandingMinorUnits: 0, totalMinorUnits: 0,
    currencyCode: "USD", currencyMinorUnit: 2,
  });
});

// The "New order" action only appears once customers have loaded; wait on it so
// the mount effects have settled.
async function renderReady(route?: string) {
  renderWithProviders(<SalesPage />, { token: ADMIN, route });
  await screen.findByRole("button", { name: "New order" });
}

// #512 US5 (T055) — same mount contract as renderReady, plus the RouterProbe
// sibling for tests that assert `location.search` or drive Back/Forward.
async function renderReadyWithProbe(route?: string) {
  capturedNavigate = null;
  renderWithProviders(<><SalesPage /><RouterProbe /></>, { token: ADMIN, route });
  await screen.findByRole("button", { name: "New order" });
}

// F131: starting an order goes through a dialog now.
const dialog = () => screen.getByRole("dialog");

async function createDraft(order: SalesOrder) {
  mockCreateOrder.mockResolvedValue({ id: order.id });
  mockGetOrder.mockResolvedValue(order);
  fireEvent.click(screen.getByRole("button", { name: "New order" }));
  await act(async () => {
    fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));
  });
  await screen.findByText(new RegExp(order.referenceNumber)); // panel header
}

async function openOrder(order: SalesOrder, rowName: RegExp) {
  mockListOrders.mockResolvedValue([order]);
  mockGetOrder.mockResolvedValue(order);
  await renderReady();
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "open" }));
  });
  return screen.findByRole("row", { name: rowName });
}

describe("SalesPage new-order customer picker (#512)", () => {
  it("closes after commit or outside pointer and lets the dialog own Escape", async () => {
    await renderReady();
    fireEvent.click(screen.getByRole("button", { name: "New order" }));

    const initialInput = await within(dialog()).findByRole("combobox", { name: "Customer" });
    fireEvent.keyDown(initialInput, { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());

    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    const newOrder = dialog();
    const customerInput = () => within(newOrder).queryByRole("combobox");
    const committedTrigger = () => within(newOrder).getByRole("button", { name: "Customer Acme Eggs" });

    fireEvent.click(await within(newOrder).findByRole("option", { name: "Acme Eggs" }));
    await waitFor(() => expect(customerInput()).not.toBeInTheDocument());
    expect(committedTrigger()).toBeVisible();

    fireEvent.click(committedTrigger());
    await within(newOrder).findByRole("combobox", { name: "Customer" });
    fireEvent.mouseDown(within(newOrder).getByLabelText("Date"));
    await waitFor(() => expect(customerInput()).not.toBeInTheDocument());
    expect(committedTrigger()).toBeVisible();
  });
});

describe("SalesPage i18n", () => {
  function withOverride(ns: string, key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", ns, key) as string;
    i18n.addResource("en", ns, key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", ns, key, original);
    });
  }

  it("renders its heading and primary action from the sales i18n catalog (#182)", async () => {
    await renderReady();

    // Pinned to i18n.t, not the literal — proves the screen is reading the
    // catalog rather than a string that happens to still match it.
    expect(screen.getByRole("heading", { name: i18n.t("sales:title") })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: i18n.t("sales:newOrder") })).toBeInTheDocument();
  });

  // #182 reconciliation: the status-filter labels now read the shared
  // enums:status family via statusLabel(), NOT the removed sales-local
  // statusDraft/statusConfirmed/… duplicate. Overriding the enums key flows
  // through to the option TEXT while the option VALUE (the server filter param)
  // stays the raw status code — the two read from different places. A
  // regression that re-added a local sales:statusConfirmed key, or hardcoded
  // the label, would break this.
  it("reads the status-filter option text from enums:status while its value stays the raw code (#182)", async () => {
    await withOverride("enums", "status.Confirmed", "CONFIRMED-ENUM-MARKER", async () => {
      await renderReady();
      const option = screen.getByRole("option", { name: "CONFIRMED-ENUM-MARKER" }) as HTMLOptionElement;
      expect(option.value).toBe("Confirmed");
      expect(screen.queryByRole("option", { name: "Confirmed" })).not.toBeInTheDocument();
    });
  });
});

// #612 — the persistent, generic notice for a restricted plain Worker under
// AllFarmFlocks: this farm setting lets their confirmations draw from
// outside their assigned flocks.
describe("SalesPage farm-wide allocation notice (#612)", () => {
  it("shows the notice when the account flags it", async () => {
    renderWithProviders(<SalesPage />, {
      token: ADMIN,
      farm: account({ showFarmWideSaleAllocationNotice: true }),
    });
    await screen.findByRole("button", { name: "New order" });

    // Pinned to i18n.t, not the literal — proves the screen reads the
    // catalog rather than a hardcoded string (same convention as the
    // "SalesPage i18n" describe block above).
    expect(screen.getByRole("status")).toHaveTextContent(i18n.t("sales:farmWideAllocationNotice"));
  });

  it("shows nothing when the account does not flag it", async () => {
    renderWithProviders(<SalesPage />, {
      token: ADMIN,
      farm: account({ showFarmWideSaleAllocationNotice: false }),
    });
    await screen.findByRole("button", { name: "New order" });

    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });

  it("reads the notice text from the sales i18n catalog, not a hardcoded literal", async () => {
    const original = i18n.getResource("en", "sales", "farmWideAllocationNotice") as string;
    i18n.addResource("en", "sales", "farmWideAllocationNotice", "NOTICE-MARKER");
    try {
      renderWithProviders(<SalesPage />, {
        token: ADMIN,
        farm: account({ showFarmWideSaleAllocationNotice: true }),
      });
      await screen.findByRole("button", { name: "New order" });
      expect(screen.getByRole("status")).toHaveTextContent("NOTICE-MARKER");
    } finally {
      i18n.addResource("en", "sales", "farmWideAllocationNotice", original);
    }
  });
});

// #250 — the quantity fields use the shared NumberField stepper (F134): −/+
// beside the input, floored at 1 (a zero-quantity sale line is meaningless).
// Steps land through the keyboard/click path here; the hold-to-repeat physics
// are NumberField.test.tsx's job, not re-proven per screen.
describe("SalesPage quantity steppers (#250)", () => {
  it("steps the add-line quantity with −/+ and floors it at 1", async () => {
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));

    // Role query, not getByLabelText: the wrapping <label> makes every control
    // inside it (the −/+ buttons too) answer to "Quantity"; only the input has
    // the spinbutton role. Since #445 the label names the unit too — the first
    // sellable product (PRODUCT_A) defaults to Dozen.
    const qty = screen.getByRole("spinbutton", { name: "Quantity (dozen)" });
    fireEvent.change(qty, { target: { value: "2" } });
    expect(qty).toHaveValue(2);

    const minus = screen.getByRole("button", { name: "Decrease quantity (dozen)" });
    fireEvent.click(minus);
    expect(qty).toHaveValue(1);
    // At the floor the − disables rather than silently no-opping…
    expect(minus).toBeDisabled();

    fireEvent.click(screen.getByRole("button", { name: "Increase quantity (dozen)" }));
    expect(qty).toHaveValue(2);

    // …and typing below it clamps back up.
    fireEvent.change(qty, { target: { value: "0" } });
    expect(qty).toHaveValue(1);
  });

  it("steps the inline-edit quantity with −/+ and floors it at 1", async () => {
    const row = await openOrder(DRAFT_TWO, /Grade A Dozen/);
    fireEvent.click(within(row).getByRole("button", { name: "edit" }));

    const qty = screen.getByRole("spinbutton", { name: "Edit quantity" }); // sr-only label in the cell
    expect(qty).toHaveValue(3); // seeded from the line (ITEM_A)

    fireEvent.click(screen.getByRole("button", { name: "Decrease edit quantity" }));
    fireEvent.click(screen.getByRole("button", { name: "Decrease edit quantity" }));
    expect(qty).toHaveValue(1);
    expect(screen.getByRole("button", { name: "Decrease edit quantity" })).toBeDisabled();

    fireEvent.change(qty, { target: { value: "0" } });
    expect(qty).toHaveValue(1);
  });
});

// #398 — a fractional quantity (e.g. 2.5) used to reach the server and fail
// during minimal-API JSON binding (Quantity is an int), surfacing the raw
// internal "Failed to read parameter ..." message. These pin the CLIENT-side
// half of the fix: reject before any network call, with a localized message.
// NumberField's typed input isn't step-constrained (no wrapping <form>, per
// the "Deliberately NOT a <form>" comment in SalesPage.tsx), so typing "2.5"
// really does land a fractional value in `qty`/`editQty` here, same as #250's
// steppers tests above prove integer steps land cleanly.
describe("SalesPage quantity must be a whole number (#398)", () => {
  it("rejects a fractional add-line quantity before sending, with a localized message", async () => {
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));

    const qty = screen.getByRole("spinbutton", { name: "Quantity (dozen)" });
    fireEvent.change(qty, { target: { value: "2.5" } });
    expect(qty).toHaveValue(2.5);

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add line" }));
    });

    expect(mockAddOrderItem).not.toHaveBeenCalled();
    expect(await screen.findByText(i18n.t("sales:quantityMustBeWholeNumber"))).toBeInTheDocument();
  });

  it("rejects a fractional inline-edit quantity before sending, with a localized message", async () => {
    const row = await openOrder(DRAFT_TWO, /Grade A Dozen/);
    fireEvent.click(within(row).getByRole("button", { name: "edit" }));
    const editRow = screen.getByRole("row", { name: /Grade A Dozen/ });

    const qty = within(editRow).getByRole("spinbutton", { name: "Edit quantity" });
    fireEvent.change(qty, { target: { value: "1.5" } });
    expect(qty).toHaveValue(1.5);

    await act(async () => {
      fireEvent.click(within(editRow).getByRole("button", { name: "save" }));
    });

    expect(mockUpdateOrderItem).not.toHaveBeenCalled();
    expect(await screen.findByText(i18n.t("sales:quantityMustBeWholeNumber"))).toBeInTheDocument();
  });
});

// #445 — users typed the EGG TOTAL into the quantity field (60 eggs → 60 trays
// = 1,800 eggs sold, silently 30x over). Three reinforcing surfaces make the
// unit visible AT ENTRY TIME: the unit in the quantity label, a live "= N eggs"
// preview, and the unit size on the product option. All display-only — the
// unit math itself is the server's (snapshotted per line, spec §9.7).
describe("SalesPage quantity unit clarity (#445)", () => {
  it("names the selected unit in the quantity label and follows the Per picker", async () => {
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));

    // First sellable product (PRODUCT_A) defaults the unit to Dozen.
    expect(screen.getByRole("spinbutton", { name: "Quantity (dozen)" })).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("Per"), { target: { value: "Tray" } });
    expect(screen.getByRole("spinbutton", { name: "Quantity (tray)" })).toBeInTheDocument();
    expect(screen.queryByRole("spinbutton", { name: "Quantity (dozen)" })).not.toBeInTheDocument();
  });

  it("previews the resulting egg count live while the quantity changes", async () => {
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));

    // qty starts at 30, unit Dozen (12/unit) → 360. THE reported mistake:
    // "60" meant as an egg count reads back as 720 eggs, not 60.
    expect(screen.getByText("= 360 eggs")).toBeInTheDocument();
    const qty = screen.getByRole("spinbutton", { name: "Quantity (dozen)" });
    fireEvent.change(qty, { target: { value: "60" } });
    expect(screen.getByText("= 720 eggs")).toBeInTheDocument();

    // Factor follows the Per picker too: 60 trays → 1,800 eggs.
    fireEvent.change(screen.getByLabelText("Per"), { target: { value: "Tray" } });
    expect(screen.getByText("= 1800 eggs")).toBeInTheDocument();
  });

  it("keeps a packed unit deliberately defined as 1 egg/unit visible — suppression is by identity, not factor", async () => {
    // Only "Individual" is pinned to 1 server-side; a farm CAN define Dozen
    // as 1 egg/unit, and that nonstandard setup is exactly what must stay
    // visible at entry time (codex review of #445). An `f > 1` threshold
    // would hide it — this pins the identity-based rule, and the singular
    // _one catalog forms with it.
    mockListEggUnitConversions.mockResolvedValue([
      { id: "cv2", unitCode: "Dozen", eggsPerUnit: 1, active: true, version: 1 },
    ]);
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));

    expect(screen.getByRole("option", { name: "Grade A Dozen (1 egg/dozen)" })).toBeInTheDocument();
    expect(screen.getByText("= 30 eggs")).toBeInTheDocument(); // qty 30 × 1
    fireEvent.change(screen.getByRole("spinbutton", { name: "Quantity (dozen)" }),
      { target: { value: "1" } });
    expect(screen.getByText("= 1 egg")).toBeInTheDocument(); // singular form
  });

  it("shows no preview for the per-egg unit — '= 30 eggs' under 30 eggs is noise", async () => {
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));

    fireEvent.change(screen.getByLabelText("Per"), { target: { value: "Egg" } });
    // Suppressed by unit IDENTITY (Egg needs no translation), not by factor.
    expect(screen.getByRole("spinbutton", { name: "Quantity (egg)" })).toBeInTheDocument();
    expect(screen.queryByText(/= \d+ eggs?/)).not.toBeInTheDocument();
  });

  it("degrades to the labeled field with no preview when the unit has no active definition", async () => {
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));

    // CONVERSIONS carries Case as INACTIVE — the label (pure i18n) keeps the
    // unit, the hint (needs a factor) disappears rather than showing a stale
    // or wrong number. The server's own SalesOrder.NoUnitConversion check
    // still decides at add time.
    fireEvent.change(screen.getByLabelText("Per"), { target: { value: "Case" } });
    expect(screen.getByRole("spinbutton", { name: "Quantity (case)" })).toBeInTheDocument();
    expect(screen.queryByText(/= \d+ eggs/)).not.toBeInTheDocument();
  });

  it("annotates product options with the default unit's size, leaving factor-1 products bare", async () => {
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));

    // PRODUCT_A sells by the dozen → annotated. Both products are offered
    // (PRODUCT_B's grade is unsaleable, so only A is in the picker) — assert
    // via the option list, not the line table (which renders bare names).
    expect(screen.getByRole("option", { name: "Grade A Dozen (12 eggs/dozen)" })).toBeInTheDocument();
  });

  it("keeps bare product names and no preview when the conversions read fails (graceful degrade)", async () => {
    mockListEggUnitConversions.mockRejectedValue(new Error("boom"));
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));

    // The screen still works — the supplementary surfaces just vanish.
    expect(screen.getByRole("option", { name: "Grade A Dozen" })).toBeInTheDocument();
    expect(screen.getByRole("spinbutton", { name: "Quantity (dozen)" })).toBeInTheDocument();
    expect(screen.queryByText(/= \d+ eggs/)).not.toBeInTheDocument();
  });

  it("binds the previewed factor to the write — expectedEggsPerUnit rides the add-item request", async () => {
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));
    mockAddOrderItem.mockResolvedValue({ orderId: "o1", itemId: "new" });

    // Dozen previews 12 → the write carries 12, so the server can refuse if
    // an admin redefined the unit after this page read its conversions.
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add line" }));
    });
    expect(mockAddOrderItem.mock.calls[0][1]).toMatchObject({
      productId: "p1", quantity: 30, unit: "Dozen", expectedEggsPerUnit: 12,
    });
  });

  it("omits expectedEggsPerUnit when nothing was previewed (per-egg unit)", async () => {
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));
    mockAddOrderItem.mockResolvedValue({ orderId: "o1", itemId: "new" });

    fireEvent.change(screen.getByLabelText("Per"), { target: { value: "Egg" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add line" }));
    });
    // No preview was shown, so there is no displayed factor to hold the
    // server to — the write must not fabricate one.
    expect(mockAddOrderItem.mock.calls[0][1].expectedEggsPerUnit).toBeUndefined();
  });

  it("refreshes the conversions after a rejected add, so the preview leaves the stale factor", async () => {
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));
    // The server refuses: the definition changed under the page.
    mockAddOrderItem.mockRejectedValue(new ApiError(422, "SalesOrder.UnitDefinitionChanged",
      "The eggs-per-unit definition for 'Dozen' is now 6, not 12 — re-check the quantity and try again."));
    mockListEggUnitConversions.mockResolvedValue([
      { id: "cv2", unitCode: "Dozen", eggsPerUnit: 6, active: true, version: 2 },
    ]);

    expect(screen.getByText("= 360 eggs")).toBeInTheDocument(); // 30 × stale 12
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add line" }));
    });

    // The refusal surfaces AND the preview now shows the current factor —
    // without the refetch every retry would loop on the stale 12.
    expect(await screen.findByText(/is now 6, not 12/)).toBeInTheDocument();
    expect(await screen.findByText("= 180 eggs")).toBeInTheDocument(); // 30 × fresh 6
  });

  it("tracks the edited quantity live in the eggs column during an inline edit", async () => {
    const row = await openOrder(DRAFT_TWO, /Grade A Dozen/);
    fireEvent.click(within(row).getByRole("button", { name: "edit" }));
    const editRow = screen.getByRole("row", { name: /Grade A Dozen/ });

    // ITEM_A: factor 12, qty 3 → the eggs cell shows 36 (not the old "—")…
    expect(within(editRow).getByText("36")).toBeInTheDocument();
    // …and follows the edit: 60 dozen is visibly 720 eggs before save.
    const qty = within(editRow).getByRole("spinbutton", { name: "Edit quantity" });
    fireEvent.change(qty, { target: { value: "60" } });
    expect(within(editRow).getByText("720")).toBeInTheDocument();
    expect(within(editRow).queryByText("36")).not.toBeInTheDocument();
  });

  it("reads the label, preview, and option annotation from the sales catalog, not literals", async () => {
    const withOverride = (key: string, value: string) => {
      const original = i18n.getResource("en", "sales", key) as string;
      i18n.addResource("en", "sales", key, value);
      return () => i18n.addResource("en", "sales", key, original);
    };
    const restores = [
      withOverride("quantityWithUnit", "QTY-MARKER {{unit}}"),
      withOverride("equalsEggs", "EGGS-MARKER {{count}}"),
      withOverride("productOptionWithUnit", "OPT-MARKER {{name}} {{count}} {{unit}}"),
    ];
    try {
      await renderReady();
      await createDraft(draftEmpty(2, "USD"));
      expect(screen.getByRole("spinbutton", { name: "QTY-MARKER dozen" })).toBeInTheDocument();
      expect(screen.getByText("EGGS-MARKER 360")).toBeInTheDocument();
      expect(screen.getByRole("option", { name: "OPT-MARKER Grade A Dozen 12 dozen" })).toBeInTheDocument();
    } finally {
      restores.forEach((r) => r());
    }
  });
});

describe("SalesPage line display", () => {
  it("shows per-line base eggs and money, with the order total distinct from any single line", async () => {
    const rowA = await openOrder(DRAFT_TWO, /Grade A Dozen/);

    // baseUnitFactor > 1 → the "(N eggs)" note; quantityBase in the Eggs column
    expect(within(rowA).getByText(/per dozen \(12 eggs\)/)).toBeInTheDocument();
    expect(within(rowA).getByText("36")).toBeInTheDocument();
    // line total = unitPrice × quantity (300 × 3), NOT the order total
    expect(within(rowA).getByText("$3.00")).toBeInTheDocument();
    expect(within(rowA).getByText("$9.00")).toBeInTheDocument();
    // #650 — money and quantity cells are numeric cells; the product cell is not.
    expect(within(rowA).getByText("$9.00")).toHaveClass("num");
    expect(within(rowA).getByText("36")).toHaveClass("num");
    expect(within(rowA).getByText(/Grade A Dozen/)).not.toHaveClass("num");

    const rowB = screen.getByRole("row", { name: /Grade B Tray/ });
    expect(within(rowB).getByText("60")).toBeInTheDocument();
    expect(within(rowB).getByText("$20.00")).toBeInTheDocument(); // 1000 × 2

    // order total (2900) differs from both line totals (900, 2000) → this pins
    // that the line cell renders its own line, not active.totalMinorUnits
    expect(screen.getByText(/Total: \$29\.00/)).toBeInTheDocument();
  });

  it("omits the egg-multiplier note and shows eggs === quantity for a per-egg line (factor 1)", async () => {
    const eggItem: OrderItem = { ...ITEM_A, id: "it9", unit: "Egg", baseUnitFactor: 1, quantity: 30, quantityBase: 30 };
    const order: SalesOrder = { ...DRAFT_TWO, id: "o3", items: [eggItem], totalMinorUnits: 9000 };
    const row = await openOrder(order, /Grade A Dozen/);

    expect(within(row).getByText(/per egg/)).toBeInTheDocument();
    expect(within(row).queryByText(/eggs\)/)).not.toBeInTheDocument(); // no "(… eggs)" suffix at factor 1
    expect(within(row).getAllByText("30")).toHaveLength(2); // quantity and quantityBase coincide at factor 1
  });

  it("renders line money at the order's currency scale (3-decimal)", async () => {
    // 1500 minor units @ 3 decimals → "BHD 1.500" (would read "15.00" at 2dp) —
    // proves formatMoney uses the item's currencyMinorUnit, not a hard-coded 2.
    const row = await openOrder(draftWithItem(3, "BHD", 1500, "o4"), /Grade A Dozen/);
    expect(within(row).getByText("BHD 1.500")).toBeInTheDocument(); // unit price
    expect(within(row).getByText("BHD 4.500")).toBeInTheDocument(); // line total 1500 × 3
  });
});

describe("SalesPage unit-price parsing", () => {
  // Different currency scales prove parseMoneyToMinorUnits uses the order's
  // currencyMinorUnit: "5" is 5 in JPY (0dp) but would be 500 at 2dp.
  it.each([
    { code: "USD", minorUnit: 2, typed: "1.50", expected: 150 },
    { code: "JPY", minorUnit: 0, typed: "5", expected: 5 },
    { code: "BHD", minorUnit: 3, typed: "1.5", expected: 1500 },
  ])("parses the entered price into $code minor units on add ($typed → $expected)", async ({ code, minorUnit, typed, expected }) => {
    const order = draftEmpty(minorUnit, code);
    await renderReady();
    await createDraft(order);
    mockAddOrderItem.mockResolvedValue({ orderId: order.id, itemId: "new" });

    fireEvent.change(screen.getByLabelText(/Unit price/), { target: { value: typed } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add line" }));
    });

    // full body: a swapped/dropped field (quantity, unit, productId) would fail too
    expect(mockAddOrderItem.mock.calls[0][1]).toMatchObject({
      productId: "p1", quantity: 30, unit: "Dozen", unitPriceMinorUnits: expected,
    });
  });

  it("omits the unit price from the request when the field is blank", async () => {
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));
    mockAddOrderItem.mockResolvedValue({ orderId: "o1", itemId: "new" });

    fireEvent.change(screen.getByLabelText(/Unit price/), { target: { value: "" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add line" }));
    });

    const body = mockAddOrderItem.mock.calls[0][1];
    expect(body).toMatchObject({ productId: "p1", quantity: 30, unit: "Dozen" });
    expect(body.unitPriceMinorUnits).toBeUndefined(); // server falls back to the product default
  });

  it.each([
    { code: "USD", minorUnit: 2, seedPrice: 300, typed: "2.50", expected: 250 },
    { code: "JPY", minorUnit: 0, seedPrice: 100, typed: "5", expected: 5 },
  ])("parses the edited price into $code minor units on update ($typed → $expected)", async ({ code, minorUnit, seedPrice, typed, expected }) => {
    mockUpdateOrderItem.mockResolvedValue(undefined);
    const row = await openOrder(draftWithItem(minorUnit, code, seedPrice), /Grade A Dozen/);

    fireEvent.click(within(row).getByRole("button", { name: "edit" }));
    const editRow = screen.getByRole("row", { name: /Grade A Dozen/ });
    // query the price input by its accessible name (not a positional spinbutton index)
    fireEvent.change(within(editRow).getByRole("spinbutton", { name: /unit price/i }), { target: { value: typed } });
    await act(async () => {
      fireEvent.click(within(editRow).getByRole("button", { name: "save" }));
    });

    expect(mockUpdateOrderItem.mock.calls[0][2]).toMatchObject({
      quantity: 3, unitPriceMinorUnits: expected, // quantity prefilled from the item, unchanged
    });
  });
});

// #123 — the price field is READ at one scale and WRITTEN at another, and the
// two used to be different objects: the prefill divided by the PRODUCT's minor
// unit while the submit multiplied by the ORDER's. Every case below gives the
// product a scale the order does not share, so the old code renders a value a
// hundred or a thousand times out and these fail. They cannot diverge in
// production (#159 locks a priced product to the farm currency), which is
// exactly why only a test can hold the line.
describe("SalesPage price scale", () => {
  // The screen mounts before any order exists, so its first prefill has only
  // the farm to go on — and the order it will be typed into carries the farm's
  // currency.
  const KWD_FARM = account({ currencyCode: "KWD", currencyMinorUnit: 3 });

  async function renderWithFarm(farm = KWD_FARM) {
    renderWithProviders(<SalesPage />, { token: ADMIN, farm });
    await screen.findByRole("button", { name: "New order" });
  }

  it("prefills the first product at the FARM's scale, not the product's", async () => {
    await renderWithFarm();
    await createDraft(draftEmpty(3, "KWD"));

    // PRODUCT_A: 300 minor units, its own row says 2dp. At the farm's 3dp that
    // is 0.300; reading the product's would show 3.00 — the same number priced
    // ten times higher.
    expect(screen.getByLabelText(/Unit price/)).toHaveValue(0.3);
  });

  it("re-prefills at the ORDER's scale when the product is changed", async () => {
    // Both grades saleable, so the picker offers two products to switch between.
    mockListEggGrades.mockResolvedValue([GRADE, { ...GRADE, id: "gr2", name: "Grade B" }]);
    await renderWithFarm();
    await createDraft(draftEmpty(3, "KWD"));

    fireEvent.change(screen.getByLabelText("Product"), { target: { value: "p2" } });

    // PRODUCT_B: 1000 minor units. Order 3dp → 1.000; the product's 2dp would
    // put 10.00 in the field.
    expect(screen.getByLabelText(/Unit price/)).toHaveValue(1);
  });

  it("prefills the row editor at the ORDER's scale, not the line's own snapshot", async () => {
    // A line whose stored scale disagrees with its order's. The edit is
    // submitted at the order's, so it must be read at the order's too.
    const order: SalesOrder = {
      ...draftEmpty(3, "KWD", "o7"),
      referenceNumber: "SO-7",
      totalMinorUnits: 4500,
      items: [{
        id: "e7", productId: "p1", eggGradeId: "gr1", unit: "Dozen", baseUnitFactor: 12,
        quantity: 3, quantityBase: 36, unitPriceMinorUnits: 1500,
        currencyCode: "KWD", currencyMinorUnit: 2,
      }],
    };
    mockListOrders.mockResolvedValue([order]);
    mockGetOrder.mockResolvedValue(order);
    await renderWithFarm();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "open" })); });

    const row = await screen.findByRole("row", { name: /Grade A Dozen/ });
    fireEvent.click(within(row).getByRole("button", { name: "edit" }));

    // 1500 at the order's 3dp is 1.500; at the line's 2dp it would read 15.00.
    const editRow = screen.getByRole("row", { name: /Grade A Dozen/ });
    expect(within(editRow).getByRole("spinbutton", { name: /unit price/i })).toHaveValue(1.5);
  });

  it("leaves the price blank rather than guessing when no farm has loaded", async () => {
    // /account failed: the scale is unknown, and an empty field falls back to
    // the server's own default for the line. A guessed 2dp would be a silent
    // 100x on a JPY or KWD farm.
    renderWithProviders(<SalesPage />, { token: ADMIN });
    await screen.findByRole("button", { name: "New order" });
    await createDraft(draftEmpty(3, "KWD"));

    // The mount prefill ran with no scale to divide by. The old code divided by
    // the product's and would show 3.00 here.
    expect(screen.getByLabelText(/Unit price/)).toHaveValue(null);
  });
});

// F131: taking a payment is a discrete per-order action, so it moved behind a
// "Record payment" button into a dialog. Payments had no coverage before this
// slice — the money path is asserted at a 3-decimal scale so a hard-coded ×100
// cannot pass.
describe("SalesPage payment dialog", () => {
  // A confirmed BHD order (3dp) with 12.000 outstanding.
  const CONFIRMED: SalesOrder = {
    ...draftEmpty(3, "BHD", "o9"), referenceNumber: "SO-9", status: "Confirmed",
    totalMinorUnits: 12000, items: [ITEM_A],
  };

  async function openWithOutstanding() {
    mockListOrderPayments.mockResolvedValue({
      items: [], paidMinorUnits: 0, outstandingMinorUnits: 12000, totalMinorUnits: 12000,
      currencyCode: "BHD", currencyMinorUnit: 3,
    });
    await openOrder(CONFIRMED, /Grade A Dozen/);
    fireEvent.click(await screen.findByRole("button", { name: "Record payment" }));
  }

  it("records the payment with the full body at the order's currency scale, then closes", async () => {
    mockRecordPayment.mockResolvedValue({ id: "pay1" });
    await openWithOutstanding();

    // every field off its default (date = today, method = Cash, blanks)
    fireEvent.change(within(dialog()).getByLabelText("Date"), { target: { value: "2026-07-21" } });
    fireEvent.change(within(dialog()).getByLabelText(/Amount/), { target: { value: "1.5" } }); // BHD 3dp → 1500
    fireEvent.change(within(dialog()).getByLabelText("Method"), { target: { value: "BankTransfer" } });
    fireEvent.change(within(dialog()).getByLabelText(/Reference/), { target: { value: "TRX-7" } });
    fireEvent.change(within(dialog()).getByLabelText(/Note/), { target: { value: "part payment" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record payment" }));
    });

    expect(mockRecordPayment.mock.calls[0][0]).toBe("o9");
    expect(mockRecordPayment.mock.calls[0][1]).toEqual({
      paymentDate: "2026-07-21",
      amountMinorUnits: 1500, // "1.5" at 3dp — a 2dp path would send 150
      method: "BankTransfer",
      referenceNumber: "TRX-7",
      note: "part payment",
    });
    expect(mockRecordPayment.mock.calls[0][2]).toEqual(expect.any(String)); // idempotency key
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument(); // success dismisses it
  });

  it("nulls the blank optional fields", async () => {
    mockRecordPayment.mockResolvedValue({ id: "pay2" });
    await openWithOutstanding();

    fireEvent.change(within(dialog()).getByLabelText(/Amount/), { target: { value: "2" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record payment" }));
    });

    const body = mockRecordPayment.mock.calls[0][1];
    expect(body.referenceNumber).toBeNull();
    expect(body.note).toBeNull();
  });

  it("closes on Cancel without recording anything", async () => {
    await openWithOutstanding();
    fireEvent.change(within(dialog()).getByLabelText(/Amount/), { target: { value: "5" } });

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockRecordPayment).not.toHaveBeenCalled();
  });
});

// F135: the four one-way order actions asked through window.confirm /
// window.prompt. They now ask in the app's own dialog — same guards, same
// idempotency scoping, but the reason checks land inline instead of after the
// popup has already thrown the text away.
describe("SalesPage one-way actions", () => {
  const CONFIRMED: SalesOrder = {
    ...draftEmpty(2, "USD", "o9"), referenceNumber: "SO-9", status: "Confirmed",
    totalMinorUnits: 2900, items: [ITEM_A, ITEM_B],
  };

  it("allocates nothing until the confirm is answered, then confirms", async () => {
    await openOrder(DRAFT_TWO, /Grade A Dozen/);
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /Confirm order/ }));
    });

    expect(dialog()).toHaveAccessibleName("Confirm this order?");
    expect(vi.mocked(confirmOrder)).not.toHaveBeenCalled();

    vi.mocked(confirmOrder).mockResolvedValue(undefined as never);
    mockGetOrder.mockResolvedValue({ ...DRAFT_TWO, status: "Confirmed" });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Confirm order" }));
    });

    expect(vi.mocked(confirmOrder)).toHaveBeenCalledWith("o2", expect.any(String));
  });

  // #612 — the distinct, generic 422 a restricted Worker gets is not a dialog
  // scope (only create-order/record-payment are), so it lands on the SAME
  // page-level error paragraph as every other confirm failure — the smallest
  // existing error-display mechanism, not a bespoke banner. client.ts (#612)
  // is what turns the domain-error TITLE into this localized text; here the
  // mocked confirmOrder rejects with the ALREADY-resolved message, same as a
  // real ApiError leaving the fetch client.
  it("shows the localized assigned-flocks-insufficient-stock warning on the page, generically", async () => {
    await openOrder(DRAFT_TWO, /Grade A Dozen/);
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /Confirm order/ }));
    });

    const localized = i18n.t("errors:EggLot.AssignedFlocksInsufficientStock");
    vi.mocked(confirmOrder).mockRejectedValue(
      new ApiError(422, "EggLot.AssignedFlocksInsufficientStock", localized));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Confirm order" }));
    });

    expect(screen.getByText(localized)).toBeInTheDocument();
  });

  it("leaves the draft alone when the cancel is dismissed", async () => {
    await openOrder(DRAFT_TWO, /Grade A Dozen/);
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Cancel draft" }));
    });

    expect(dialog()).toHaveAccessibleName("Cancel this draft?");
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    });

    expect(screen.queryByRole("dialog")).toBeNull();
    expect(vi.mocked(cancelOrder)).not.toHaveBeenCalled();
    // The draft is still open and still workable — dismissing is not a dead end.
    expect(screen.getByRole("button", { name: "Cancel draft" })).toBeEnabled();
  });

  it("refuses a blank void reason inline, then sends the trimmed one", async () => {
    await openOrder(CONFIRMED, /Grade A Dozen/);
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /Void order/ }));
    });

    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Void order" }));
    });
    expect(screen.getByText("A reason is required.")).toBeInTheDocument();
    expect(vi.mocked(voidOrder)).not.toHaveBeenCalled();

    vi.mocked(voidOrder).mockResolvedValue(undefined as never);
    mockGetOrder.mockResolvedValue({ ...CONFIRMED, status: "Voided", voidReason: "double sold" });
    fireEvent.change(within(dialog()).getByLabelText("Reason *"),
      { target: { value: "  double sold  " } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Void order" }));
    });

    expect(vi.mocked(voidOrder)).toHaveBeenCalledWith("o9", "double sold", expect.any(String));
  });

  it("voids a payment with its own reason and loaded version", async () => {
    mockListOrderPayments.mockResolvedValue({
      items: [{
        id: "pay1", salesOrderId: "o9", customerId: "c1", amountMinorUnits: 500, currencyCode: "USD",
        currencyMinorUnit: 2, method: "Cash", paymentDate: "2026-07-20",
        referenceNumber: null, note: null, voided: false, voidReason: null, version: 3,
      }],
      paidMinorUnits: 500, outstandingMinorUnits: 2400, totalMinorUnits: 2900,
      currencyCode: "USD", currencyMinorUnit: 2,
    });
    await openOrder(CONFIRMED, /Grade A Dozen/);

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "void" }));
    });
    expect(dialog()).toHaveAccessibleName("Void this payment?");

    vi.mocked(voidPayment).mockResolvedValue(undefined as never);
    fireEvent.change(within(dialog()).getByLabelText("Reason *"),
      { target: { value: "posted to the wrong order" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Void payment" }));
    });

    expect(vi.mocked(voidPayment)).toHaveBeenCalledWith(
      "pay1", { version: 3, reason: "posted to the wrong order" }, expect.any(String));
  });
});

// #236 — the pending-state migration. Every held flight uses a deferred
// promise (same idiom as client.test.ts): assert what the screen shows BEFORE
// the request settles, no timing guesses.
describe("SalesPage pending states (#236)", () => {
  function deferred<T>() {
    let resolve!: (v: T) => void;
    let reject!: (e: unknown) => void;
    const promise = new Promise<T>((res, rej) => {
      resolve = res;
      reject = rej;
    });
    return { promise, resolve, reject };
  }

  const CONFIRMED_PAID: SalesOrder = {
    ...draftEmpty(2, "USD", "o9"), referenceNumber: "SO-9", status: "Confirmed",
    totalMinorUnits: 2900, items: [ITEM_A],
  };
  const payment = (id: string, ref: string) => ({
    id, salesOrderId: "o9", customerId: "c1", amountMinorUnits: 500, currencyCode: "USD",
    currencyMinorUnit: 2, method: "Cash", paymentDate: "2026-07-20",
    referenceNumber: ref, note: null, voided: false, voidReason: null, version: 1,
  });

  it("spins only the voided payment's own button; every other verb disables without aria-busy", async () => {
    mockListOrderPayments.mockResolvedValue({
      items: [payment("pay1", "R1"), payment("pay2", "R2")],
      paidMinorUnits: 1000, outstandingMinorUnits: 1900, totalMinorUnits: 2900,
      currencyCode: "USD", currencyMinorUnit: 2,
    });
    const gate = deferred<void>();
    vi.mocked(voidPayment).mockReturnValue(gate.promise as never);
    await openOrder(CONFIRMED_PAID, /Grade A Dozen/);

    await act(async () => {
      fireEvent.click(within(screen.getByRole("row", { name: /R1/ })).getByRole("button", { name: "void" }));
    });
    fireEvent.change(within(dialog()).getByLabelText("Reason *"), { target: { value: "wrong order" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Void payment" }));
    });

    // The clicked verb is the ONE pending indicator…
    const voidR1 = within(screen.getByRole("row", { name: /R1/ })).getByRole("button", { name: "void" });
    expect(voidR1).toHaveAttribute("aria-busy", "true");
    expect(voidR1).toBeDisabled();
    // …while the sibling row's same verb, and the order's own void, merely
    // disable — a second spinner would lie about what is being worked on.
    const voidR2 = within(screen.getByRole("row", { name: /R2/ })).getByRole("button", { name: "void" });
    expect(voidR2).toBeDisabled();
    expect(voidR2).not.toHaveAttribute("aria-busy");
    const voidOrderButton = screen.getByRole("button", { name: /Void order/ });
    expect(voidOrderButton).toBeDisabled();
    expect(voidOrderButton).not.toHaveAttribute("aria-busy");

    await act(async () => { gate.resolve(); });
    expect(document.querySelector('[aria-busy="true"]')).toBeNull();
    expect(screen.getByRole("button", { name: /Void order/ })).toBeEnabled();
  });

  it("spins only the removed line's own verb on a row that carries two", async () => {
    const gate = deferred<void>();
    vi.mocked(removeOrderItem).mockReturnValue(gate.promise as never);
    const rowA = await openOrder(DRAFT_TWO, /Grade A Dozen/);

    await act(async () => {
      fireEvent.click(within(rowA).getByRole("button", { name: "remove" }));
    });

    const rowANow = screen.getByRole("row", { name: /Grade A Dozen/ });
    const removeA = within(rowANow).getByRole("button", { name: "remove" });
    expect(removeA).toHaveAttribute("aria-busy", "true");
    expect(removeA).toBeDisabled();
    // The SAME row's other verb disables without spinning…
    const editA = within(rowANow).getByRole("button", { name: "edit" });
    expect(editA).toBeDisabled();
    expect(editA).not.toHaveAttribute("aria-busy");
    // …as does the sibling row's copy of the clicked verb.
    const removeB = within(screen.getByRole("row", { name: /Grade B Tray/ })).getByRole("button", { name: "remove" });
    expect(removeB).toBeDisabled();
    expect(removeB).not.toHaveAttribute("aria-busy");

    await act(async () => { gate.resolve(); });
    expect(document.querySelector('[aria-busy="true"]')).toBeNull();
  });

  it("closes the New order dialog only after the held create settles — nothing left busy, no act warning", async () => {
    const errorSpy = vi.spyOn(console, "error");
    const order = draftEmpty(2, "USD");
    const gate = deferred<{ id: string }>();
    mockCreateOrder.mockReturnValue(gate.promise as never);
    mockGetOrder.mockResolvedValue(order);
    await renderReady();

    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));
    });

    // Held: the dialog stays up and its submit is the pending indicator.
    const submit = within(dialog()).getByRole("button", { name: "New draft order" });
    expect(submit).toHaveAttribute("aria-busy", "true");
    expect(submit).toBeDisabled();

    await act(async () => { gate.resolve({ id: order.id }); });

    // Close + busy-clear land together (React batching, pinned here): no
    // dialog, no stale pending scope anywhere on the screen.
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(document.querySelector('[aria-busy="true"]')).toBeNull();
    await screen.findByText(new RegExp(order.referenceNumber));
    expect(errorSpy.mock.calls.filter(([first]) => String(first).includes("act("))).toEqual([]);
    errorSpy.mockRestore();
  });
});

// #494 — the record-history column is a shared component, well tested on its
// own; what is NOT tested by that unit suite is the per-page WIRING that hands
// it the CORRECT row's history object. A page passing the wrong variable (a
// different row, or a stray constant) would go uncaught otherwise.
describe("SalesPage record history column (#494)", () => {
  it("shows the record history column for the row that has one", async () => {
    const HISTORY_ORDER: SalesOrder = {
      ...draftEmpty(2, "USD", "o-hist"), ...RECORD_HISTORY, referenceNumber: "SO-HIST",
    };
    mockListOrders.mockResolvedValue([DRAFT_TWO, HISTORY_ORDER]);
    await renderReady();

    const historyRow = screen.getByRole("row", { name: /SO-HIST/ });
    // #653 — the visible line shows the CHANGER (the more recent event);
    // both facts still live in the title, unchanged from #494.
    expect(within(historyRow).getByText(/bo/)).toBeInTheDocument();
    expect((historyRow.querySelector("td.provenance-cell") as HTMLElement).title).toBe(
      "Created by ana@farm.test on 2026-05-01 08:00:00\nLast changed by bo@farm.test on 2026-05-03 14:30:00",
    );

    // The OTHER row must not carry the history row's data — this is what
    // catches every row being wired to the same object.
    const otherRow = screen.getByRole("row", { name: /SO-2/ });
    expect(otherRow.querySelector("td.provenance-cell")).toBeNull();
  });
});

// #493 — full audit trail, distinct from the two-point summary above.
describe("SalesPage audit history link (#493)", () => {
  it("links each row to its own entity-scoped audit history", async () => {
    mockListOrders.mockResolvedValue([DRAFT_TWO]);
    await renderReady();
    const row = screen.getByRole("row", { name: /SO-2/ });
    expect(within(row).getByRole("link", { name: "Audit history" }))
      .toHaveAttribute("href", "/audit?entityId=o2");
  });

  // codex review of #516 — /api/v1/audit is AdminOnly; the Sales role (which
  // can view and settle orders here) would otherwise hit a 403.
  it("hides the link from a non-admin", async () => {
    mockListOrders.mockResolvedValue([DRAFT_TWO]);
    renderWithProviders(<SalesPage />, { token: { sub: "u1", role: "Sales" } });
    await screen.findByRole("row", { name: /SO-2/ });
    expect(screen.queryByRole("link", { name: "Audit history" })).not.toBeInTheDocument();
  });
});

// #512 US4 (T043/T052) — an order row's own customerName is null (the
// customer left the caller's tenant scope between reads), even though the
// SAME id is present in the page's own capped customer catalog under a
// DIFFERENT-looking name. The row must show the translated unavailable
// label, never that catalog substitution and never a raw id fragment.
describe("SalesPage row-owned customer name (#512 US4)", () => {
  it("a row whose own customerName is null shows the translated unavailable label — never the catalog's name for that id, never an id fragment", async () => {
    const GONE: SalesOrder = { ...DRAFT_TWO, id: "o-gone", customerName: null };
    mockListOrders.mockResolvedValue([GONE]);
    await renderReady();

    const row = screen.getByRole("row", { name: /SO-2/ });
    expect(within(row).getByText(i18n.t("sales:rowCustomerUnavailable"))).toBeInTheDocument();
    expect(within(row).queryByText("Acme Eggs")).not.toBeInTheDocument();
    expect(within(row).queryByText("c1")).not.toBeInTheDocument();
  });

  // page-adoption.md: "active order heading uses row-owned customer" — the
  // SAME rowCustomerName function, exercised at its OTHER call site.
  it("the active order panel's heading shows the translated unavailable label when the order's own customerName is null", async () => {
    const GONE: SalesOrder = { ...DRAFT_TWO, id: "o-gone2", referenceNumber: "SO-GONE", customerName: null };
    await renderReady();
    await createDraft(GONE);

    const heading = screen.getByRole("heading", { name: /SO-GONE/ });
    expect(heading).toHaveTextContent(i18n.t("sales:rowCustomerUnavailable"));
    expect(heading).not.toHaveTextContent("Acme Eggs");
  });
});

// #512 US5 (T055, FR-045..050) — the canonical `customerId` URL identity is
// the sole truth for the Sales customer filter: validation/normalization,
// select/clear preserving unrelated keys, malformed absence, unavailable
// Retry/Clear, Back/Forward, and synchronous stale-row hiding.
describe("SalesPage URL-owned customer filter (#512 US5)", () => {
  it("shows a neutral loading label while a URL customer is still resolving", async () => {
    let resolveCustomer!: (customer: Customer) => void;
    mockGetCustomer.mockReturnValue(new Promise((resolve) => { resolveCustomer = resolve; }));
    await renderReady(`/sales?customerId=${GUID_A}`);

    expect(screen.getByRole("button", { name: new RegExp(i18n.t("namedEntityPicker:loading")) })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: i18n.t("sales:filterCustomerUnavailable") })).not.toBeInTheDocument();

    await act(async () => { resolveCustomer(CUSTOMER_A); });
    expect(await screen.findByRole("button", { name: /Filtered Farm A/ })).toBeInTheDocument();
  });

  it("normalizes a mixed-case canonical GUID to lowercase before requesting and resolving — direct navigation is the source of truth", async () => {
    const MIXED = GUID_A.toUpperCase();
    mockGetCustomer.mockResolvedValue(CUSTOMER_A);
    await renderReady(`/sales?customerId=${MIXED}`);

    await waitFor(() => expect(mockGetCustomer).toHaveBeenCalledWith(GUID_A));
    expect(mockGetCustomer).not.toHaveBeenCalledWith(MIXED);
    await waitFor(() => expect(mockListOrders).toHaveBeenCalledWith(
      expect.objectContaining({ customerId: GUID_A })));
    expect(await screen.findByRole("button", { name: /Filtered Farm A/ })).toBeInTheDocument();
  });

  it("treats a malformed customerId as absent — no filtered request, no exact GET, trigger shows All", async () => {
    await renderReady(`/sales?customerId=${GUID_MALFORMED}`);

    expect(mockGetCustomer).not.toHaveBeenCalled();
    await waitFor(() => expect(mockListOrders).toHaveBeenCalledWith(
      expect.objectContaining({ customerId: undefined })));
    expect(screen.getByRole("button", { name: new RegExp(i18n.t("sales:allOption")) })).toBeInTheDocument();
  });

  it("selecting a customer sets customerId while preserving unrelated query keys", async () => {
    mockListCustomers.mockResolvedValue([CUSTOMER_A, CUSTOMER_B]);
    await renderReadyWithProbe("/sales?status=Draft&foo=bar");

    fireEvent.click(screen.getByRole("button", { name: new RegExp(i18n.t("sales:allOption")) }));
    const option = await screen.findByRole("option", { name: "Filtered Farm A" });
    fireEvent.click(option);

    await waitFor(() => expect(probeSearch()).toContain(`customerId=${GUID_A}`));
    expect(probeSearch()).toContain("status=Draft");
    expect(probeSearch()).toContain("foo=bar");
  });

  it("clearing the filter removes only customerId, preserving unrelated query keys", async () => {
    mockGetCustomer.mockResolvedValue(CUSTOMER_A);
    await renderReadyWithProbe(`/sales?customerId=${GUID_A}&status=Draft`);
    await screen.findByRole("button", { name: /Filtered Farm A/ });

    fireEvent.click(screen.getByRole("button", { name: /Filtered Farm A/ }));
    fireEvent.click(await screen.findByRole("button", { name: i18n.t("namedEntityPicker:clear") }));

    await waitFor(() => expect(probeSearch()).not.toContain("customerId"));
    expect(probeSearch()).toContain("status=Draft");
  });

  it("closes the customer filter on Escape or an outside pointer without changing the URL", async () => {
    await renderReadyWithProbe("/sales?foo=bar");

    fireEvent.click(screen.getByRole("button", { name: new RegExp(i18n.t("sales:allOption")) }));
    const input = await screen.findByRole("combobox", { name: i18n.t("sales:customer") });
    fireEvent.keyDown(input, { key: "Escape" });
    expect(screen.queryByRole("combobox", { name: i18n.t("sales:customer") })).not.toBeInTheDocument();
    expect(probeSearch()).toContain("foo=bar");

    fireEvent.click(screen.getByRole("button", { name: new RegExp(i18n.t("sales:allOption")) }));
    await screen.findByRole("combobox", { name: i18n.t("sales:customer") });
    fireEvent.mouseDown(document.body);
    expect(screen.queryByRole("combobox", { name: i18n.t("sales:customer") })).not.toBeInTheDocument();
    expect(probeSearch()).toContain("foo=bar");
  });

  it("a well-formed but inaccessible customerId enters unavailable with Retry — never rewritten to All, never a raw id", async () => {
    mockGetCustomer.mockRejectedValueOnce(new Error("not found"));
    await renderReady(`/sales?customerId=${GUID_A}`);

    const unavailableLabel = i18n.t("sales:filterCustomerUnavailable");
    await waitFor(() => expect(screen.getByRole("button", { name: new RegExp(unavailableLabel) })).toBeInTheDocument());
    expect(screen.queryByText(GUID_A)).not.toBeInTheDocument();
    // Neither rewritten to All nor silently dropped — the URL still carries it.
    expect(screen.queryByRole("button", { name: new RegExp(`\\b${i18n.t("sales:allOption")}\\b`) })).not.toBeInTheDocument();

    const retryBtn = screen.getByRole("button", { name: i18n.t("namedEntityPicker:retry") });
    mockGetCustomer.mockResolvedValueOnce(CUSTOMER_A);
    fireEvent.click(retryBtn);
    expect(await screen.findByRole("button", { name: /Filtered Farm A/ })).toBeInTheDocument();

  });

  it("clear is available while the filter is unavailable, not just once something is committed", async () => {
    mockGetCustomer.mockRejectedValueOnce(new Error("not found"));
    await renderReadyWithProbe(`/sales?customerId=${GUID_A}`);
    await waitFor(() => expect(screen.getByRole("button", { name: new RegExp(i18n.t("sales:filterCustomerUnavailable")) })).toBeInTheDocument());

    const clearBtn = await screen.findByRole("button", { name: i18n.t("namedEntityPicker:clear") });
    fireEvent.click(clearBtn);
    await waitFor(() => expect(probeSearch()).not.toContain("customerId"));
  });

  it("Back restores the prior URL identity and its filtered rows; Forward restores the newer one", async () => {
    mockGetCustomer.mockImplementation(async (id: string) => id === GUID_A ? CUSTOMER_A : CUSTOMER_B);
    mockListOrders.mockImplementation(async (p?: { customerId?: string }) =>
      p?.customerId === GUID_A ? [{ ...DRAFT_TWO, id: "oa", referenceNumber: "SO-A", customerName: "Filtered Farm A" }]
        : p?.customerId === GUID_B ? [{ ...DRAFT_TWO, id: "ob", referenceNumber: "SO-B", customerName: "Filtered Farm B" }]
        : []);
    await renderReadyWithProbe(`/sales?customerId=${GUID_A}`);
    await screen.findByRole("row", { name: /SO-A/ });

    await act(async () => { capturedNavigate!(`/sales?customerId=${GUID_B}`); });
    await screen.findByRole("row", { name: /SO-B/ });
    expect(screen.queryByRole("row", { name: /SO-A/ })).not.toBeInTheDocument();

    await act(async () => { capturedNavigate!(-1); }); // Back
    await screen.findByRole("row", { name: /SO-A/ });
    expect(screen.queryByRole("row", { name: /SO-B/ })).not.toBeInTheDocument();

    await act(async () => { capturedNavigate!(1); }); // Forward
    await screen.findByRole("row", { name: /SO-B/ });
    expect(screen.queryByRole("row", { name: /SO-A/ })).not.toBeInTheDocument();
  });

  it("hides the previous identity's rows and trigger name SYNCHRONOUSLY on a URL identity change — never a paint of stale data under the new id", async () => {
    let releaseB!: (rows: SalesOrder[]) => void;
    mockGetCustomer.mockImplementation(async (id: string) => id === GUID_A ? CUSTOMER_A : CUSTOMER_B);
    mockListOrders.mockImplementation(async (p?: { customerId?: string }) => {
      if (p?.customerId === GUID_A) return [{ ...DRAFT_TWO, id: "oa", referenceNumber: "SO-A", customerName: "Filtered Farm A" }];
      if (p?.customerId === GUID_B) return new Promise<SalesOrder[]>((r) => { releaseB = r; });
      return [];
    });
    await renderReadyWithProbe(`/sales?customerId=${GUID_A}`);
    await screen.findByRole("row", { name: /SO-A/ });
    await waitFor(() => expect(screen.getByRole("button", { name: /Filtered Farm A/ })).toBeInTheDocument());

    // Navigate to B; its list read is HELD. Neither the A row nor the A
    // trigger name may still be on screen — synchronous hide, not "hidden
    // once B's request settles."
    act(() => { capturedNavigate!(`/sales?customerId=${GUID_B}`); });
    expect(screen.queryByRole("row", { name: /SO-A/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Filtered Farm A/ })).not.toBeInTheDocument();

    await act(async () => { releaseB([{ ...DRAFT_TWO, id: "ob", referenceNumber: "SO-B", customerName: "Filtered Farm B" }]); });
    await screen.findByRole("row", { name: /SO-B/ });
  });
});

describe("SalesPage empty states (#655)", () => {
  // #655 — a customer filter narrowing the list to zero is "filtered to
  // nothing" (offer Clear filters), distinct from the truly-empty "New
  // order" state every other test in this file exercises by default.
  it("offers Clear filters, not New order, when a customer filter matches no orders", async () => {
    mockGetCustomer.mockResolvedValue(CUSTOMER_A);
    renderWithProviders(<SalesPage />, { token: ADMIN, route: `/sales?customerId=${GUID_A}` });
    await screen.findByRole("button", { name: /Filtered Farm A/ });

    expect(await screen.findByText("No orders match.")).toBeInTheDocument();
    // The book isn't empty (the header's own New order stays) — only ONE
    // action comes from the empty state itself, and it clears the filter.
    expect(screen.getAllByRole("button", { name: "New order" })).toHaveLength(1);
    fireEvent.click(screen.getByRole("button", { name: "Clear filters" }));
    await waitFor(() => expect(screen.getByRole("button", { name: new RegExp(i18n.t("sales:allOption")) })).toBeInTheDocument());
  });

  // #655 — role/data-aware: the same condition AND handler as the page-head
  // button (customers.length > 0), reused rather than re-derived — a
  // customer-less farm sees the sentence alone here too.
  it("withholds the create action when there are no customers to bill", async () => {
    mockListCustomers.mockResolvedValue([]);
    renderWithProviders(<SalesPage />, { token: ADMIN });
    expect(await screen.findByText("No orders yet.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "New order" })).not.toBeInTheDocument();
  });
});

describe("SalesPage list failures (#469)", () => {
  // The old behaviour: ANY rejection from the order-list fetch set a
  // `loadError` that nothing ever cleared, and the render replaced the whole
  // workspace with it — so a transient blip during a filter change threw away
  // an order the user was part-way through editing, for the rest of the
  // session. Both halves are fixed: the error is a banner, and it heals.
  it("keeps the workspace and shows a banner when the order list fails", async () => {
    await renderReady();

    mockListOrders.mockRejectedValueOnce(new Error("boom"));
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Status"), { target: { value: "Draft" } });
    });

    expect(screen.getByRole("alert")).toHaveTextContent("Could not load orders.");
    // The workspace survives — this is what the full-screen replacement ate.
    expect(screen.getByRole("button", { name: "New order" })).toBeInTheDocument();
    expect(screen.getByLabelText("Status")).toBeInTheDocument();
  });

  it("heals the banner on the next successful load", async () => {
    await renderReady();
    mockListOrders.mockRejectedValueOnce(new Error("boom"));
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Status"), { target: { value: "Draft" } });
    });
    expect(screen.getByRole("alert")).toBeInTheDocument();

    mockListOrders.mockResolvedValueOnce([]);
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Status"), { target: { value: "Confirmed" } });
    });
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("ignores a stale filter response that lands after a newer one", async () => {
    await renderReady();

    let releaseStale!: (orders: SalesOrder[]) => void;
    mockListOrders.mockReturnValueOnce(new Promise((r) => { releaseStale = r; }));
    fireEvent.change(screen.getByLabelText("Status"), { target: { value: "Draft" } });
    mockListOrders.mockResolvedValueOnce([{ ...DRAFT_TWO, referenceNumber: "SO-FRESH" }]);
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Status"), { target: { value: "Confirmed" } });
    });
    expect(screen.getByText("SO-FRESH")).toBeInTheDocument();

    await act(async () => {
      releaseStale([{ ...DRAFT_TWO, id: "stale", referenceNumber: "SO-STALE" }]);
    });
    expect(screen.getByText("SO-FRESH")).toBeInTheDocument();
    expect(screen.queryByText("SO-STALE")).not.toBeInTheDocument();
  });
});

describe("SalesPage cross-window display while loading (#469)", () => {
  it("hides the previous filter's orders while the new one loads", async () => {
    mockListOrders.mockResolvedValueOnce([{ ...DRAFT_TWO, referenceNumber: "SO-OLD" }]);
    await renderReady();
    expect(screen.getByText("SO-OLD")).toBeInTheDocument();

    mockListOrders.mockReturnValueOnce(new Promise(() => {}));
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Status"), { target: { value: "Draft" } });
    });

    // One window's orders must never sit under another window's filters.
    expect(screen.queryByText("SO-OLD")).not.toBeInTheDocument();
  });
});

// #474 — the screen renders the error paragraph three times: once per dialog
// and once for the page. All three carried the PAGE's guard (`!creatingOrder &&
// !paying`), which is false exactly when the dialog holding that copy is open —
// so a mutation that failed under a dialog cleared its spinner and said
// nothing. The dialog copies are guarded by the Dialog itself (it renders
// nothing while closed), and the page copy keeps the suppression so the message
// is never duplicated.
describe("SalesPage in-dialog errors (#474)", () => {
  it("shows a failed create-order inside the new-order dialog", async () => {
    await renderReady();
    mockCreateOrder.mockRejectedValueOnce(
      new ApiError(422, "Validation failed", "Order date cannot be in the future."));

    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));
    });

    // The dialog stays up (a throw keeps it open) and now says why.
    expect(within(dialog()).getByText("Order date cannot be in the future.")).toBeInTheDocument();
    // Exactly one copy: the page-level paragraph stays suppressed behind the
    // open dialog, so a fix that simply dropped the page guard fails here.
    expect(screen.getAllByText("Order date cannot be in the future.")).toHaveLength(1);
  });

  // codex review of this branch. Both of these are about MONEY, which is why
  // they are pinned rather than argued about.
  it("still announces a payment that succeeded after its dialog was abandoned", async () => {
    // #477 calls this message "stray". It is not: the money was recorded.
    // Withholding the confirmation because the user closed the dialog leaves
    // them believing it did not happen, and the likely next act is paying twice.
    mockListOrderPayments.mockResolvedValue({
      items: [], paidMinorUnits: 0, outstandingMinorUnits: 12000, totalMinorUnits: 12000,
      currencyCode: "BHD", currencyMinorUnit: 3,
    });
    await openOrder(
      { ...draftEmpty(3, "BHD", "o9"), referenceNumber: "SO-9", status: "Confirmed", totalMinorUnits: 12000, items: [ITEM_A] },
      /Grade A Dozen/);
    fireEvent.click(await screen.findByRole("button", { name: "Record payment" }));

    let resolvePay!: (v: unknown) => void;
    mockRecordPayment.mockReturnValueOnce(new Promise((res) => { resolvePay = res; }) as never);
    fireEvent.change(within(dialog()).getByLabelText(/Amount/), { target: { value: "5" } });
    fireEvent.click(within(dialog()).getByRole("button", { name: "Record payment" }));
    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    await act(async () => { resolvePay({}); });

    expect(screen.getByText("Payment recorded.")).toBeInTheDocument(); // the catalogue string ends in a period
  });

  it("starts a new payment session with an empty amount", async () => {
    // An attempt abandoned and then succeeded leaves its amount in the field
    // otherwise — ready to be sent again under a fresh key, which is a second
    // payment of money already taken.
    mockListOrderPayments.mockResolvedValue({
      items: [], paidMinorUnits: 0, outstandingMinorUnits: 12000, totalMinorUnits: 12000,
      currencyCode: "BHD", currencyMinorUnit: 3,
    });
    await openOrder(
      { ...draftEmpty(3, "BHD", "o9"), referenceNumber: "SO-9", status: "Confirmed", totalMinorUnits: 12000, items: [ITEM_A] },
      /Grade A Dozen/);
    fireEvent.click(await screen.findByRole("button", { name: "Record payment" }));

    let resolvePay!: (v: unknown) => void;
    mockRecordPayment.mockReturnValueOnce(new Promise((res) => { resolvePay = res; }) as never);
    fireEvent.change(within(dialog()).getByLabelText(/Amount/), { target: { value: "5" } });
    fireEvent.click(within(dialog()).getByRole("button", { name: "Record payment" }));
    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    await act(async () => { resolvePay({}); });

    fireEvent.click(await screen.findByRole("button", { name: "Record payment" }));

    expect(within(dialog()).getByLabelText(/Amount/)).toHaveValue(null);
  });

  it("shows a failed payment inside the payment dialog", async () => {
    mockListOrderPayments.mockResolvedValue({
      items: [], paidMinorUnits: 0, outstandingMinorUnits: 12000, totalMinorUnits: 12000,
      currencyCode: "BHD", currencyMinorUnit: 3,
    });
    await openOrder(
      { ...draftEmpty(3, "BHD", "o9"), referenceNumber: "SO-9", status: "Confirmed", totalMinorUnits: 12000, items: [ITEM_A] },
      /Grade A Dozen/);
    fireEvent.click(await screen.findByRole("button", { name: "Record payment" }));

    mockRecordPayment.mockRejectedValueOnce(
      new ApiError(422, "Validation failed", "Payment exceeds the outstanding balance."));
    fireEvent.change(within(dialog()).getByLabelText(/Amount/), { target: { value: "99" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record payment" }));
    });

    expect(within(dialog()).getByText("Payment exceeds the outstanding balance.")).toBeInTheDocument();
    expect(screen.getAllByText("Payment exceeds the outstanding balance.")).toHaveLength(1);
  });

  // Codex review of #476: `error` is ONE state shared by every action on the
  // screen, and neither dialog trigger is disabled while another request is in
  // flight. So an unconditional in-dialog render presents someone else's
  // failure — a payments read, a write started before the dialog opened — as
  // the dialog's own. The error carries the scope that raised it, and each
  // dialog shows only its own.
  const CONFIRMED_9: SalesOrder = {
    ...draftEmpty(2, "USD", "o9"), referenceNumber: "SO-9", status: "Confirmed",
    totalMinorUnits: 2900, items: [ITEM_A],
  };

  it("keeps an unrelated failure out of the new-order dialog", async () => {
    let rejectPayments!: (e: unknown) => void;
    mockListOrderPayments.mockReturnValueOnce(
      new Promise((_, rej) => { rejectPayments = rej; }) as never);
    await openOrder(CONFIRMED_9, /Grade A Dozen/);

    // The trigger is live while that read is still out.
    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    await act(async () => { rejectPayments(new Error("boom")); });

    const message = "Could not load this order's payments.";
    expect(within(dialog()).queryByText(message)).not.toBeInTheDocument();
    // Not swallowed either — it belongs to the page, and says so there.
    expect(screen.getByText(message)).toBeInTheDocument();
  });

  it("keeps a panel write's failure out of the new-order dialog", async () => {
    // The other source: not a background read but another WRITE, started
    // before the dialog was opened. Its scope is the one run() was called
    // with, so tagging every failure alike would land it here.
    await openOrder(DRAFT_TWO, /Grade A Dozen/);
    let rejectAdd!: (e: unknown) => void;
    vi.mocked(addOrderItem).mockReturnValue(
      new Promise((_, rej) => { rejectAdd = rej; }) as never);
    fireEvent.click(screen.getByRole("button", { name: "Add line" }));

    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    await act(async () => { rejectAdd(new ApiError(422, "Validation failed", "That product is no longer sellable.")); });

    expect(within(dialog()).queryByText("That product is no longer sellable.")).not.toBeInTheDocument();
    expect(screen.getByText("That product is no longer sellable.")).toBeInTheDocument();
  });

  it("keeps an unrelated failure out of the payment dialog", async () => {
    mockListOrderPayments.mockResolvedValue({
      items: [{
        id: "pay1", salesOrderId: "o9", customerId: "c1", amountMinorUnits: 500,
        currencyCode: "USD", currencyMinorUnit: 2, method: "Cash", paymentDate: "2026-07-20",
        referenceNumber: "R1", note: null, voided: false, voidReason: null, version: 1,
      }],
      paidMinorUnits: 500, outstandingMinorUnits: 2400, totalMinorUnits: 2900,
      currencyCode: "USD", currencyMinorUnit: 2,
    });
    let rejectVoid!: (e: unknown) => void;
    vi.mocked(voidPayment).mockReturnValue(
      new Promise((_, rej) => { rejectVoid = rej; }) as never);
    await openOrder(CONFIRMED_9, /Grade A Dozen/);

    // Start voiding a payment, leave it in flight…
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "void" }));
    });
    fireEvent.change(within(dialog()).getByLabelText("Reason *"), { target: { value: "wrong order" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Void payment" }));
    });
    // …then open the payment dialog and let the void fail underneath it.
    fireEvent.click(screen.getByRole("button", { name: "Record payment" }));
    await act(async () => { rejectVoid(new ApiError(409, "Conflict", "That payment was already voided.")); });

    expect(within(dialog()).queryByText("That payment was already voided.")).not.toBeInTheDocument();
    expect(screen.getByText("That payment was already voided.")).toBeInTheDocument();
  });

  it("drops the dialog's own error when the dialog is dismissed", async () => {
    // #474's own complaint, the other way round: a message about an abandoned
    // attempt, left on the page after its dialog is gone, "reads as a
    // page-level error with no context". The attempt is over — so is the
    // message.
    await renderReady();
    mockCreateOrder.mockRejectedValueOnce(
      new ApiError(422, "Validation failed", "Order date cannot be in the future."));
    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));
    });
    expect(within(dialog()).getByText("Order date cannot be in the future.")).toBeInTheDocument();

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.queryByText("Order date cannot be in the future.")).not.toBeInTheDocument();
  });

  it("says nothing when the dialog is dismissed before its write fails", async () => {
    // Codex P2 + pi, same hole: dismissing only cleared an error that had
    // ALREADY landed. A slow request the user gave up on still reported at
    // page level afterwards — the context-free message again, now with the
    // form that explains it gone. Cancel is live during `busy`, and Escape and
    // the backdrop close the dialog too, so this is the ordinary case.
    await renderReady();
    let rejectCreate!: (e: unknown) => void;
    mockCreateOrder.mockReturnValueOnce(new Promise((_, rej) => { rejectCreate = rej; }) as never);
    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    await act(async () => {
      rejectCreate(new ApiError(422, "Validation failed", "Order date cannot be in the future."));
    });

    expect(screen.queryByText("Order date cannot be in the future.")).not.toBeInTheDocument();
  });

  it("does not report an abandoned attempt against the session that replaced it", async () => {
    // The dismissal alone is not enough. Nothing gates the trigger on `busy`,
    // so the user can reopen the same dialog while the attempt they gave up on
    // is still out — and its failure would then be shown against the form they
    // are filling in now, describing an attempt that no longer exists.
    await renderReady();
    let rejectCreate!: (e: unknown) => void;
    mockCreateOrder.mockReturnValueOnce(new Promise((_, rej) => { rejectCreate = rej; }) as never);
    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));
    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    fireEvent.click(screen.getByRole("button", { name: "New order" })); // second session
    await act(async () => {
      rejectCreate(new ApiError(422, "Validation failed", "Order date cannot be in the future."));
    });

    expect(within(dialog()).queryByText("Order date cannot be in the future.")).not.toBeInTheDocument();
    expect(screen.queryByText("Order date cannot be in the future.")).not.toBeInTheDocument();
  });

  // #477 part 2 — the abandonment marker covers the FAILURE path only. A stale
  // SUCCESS still ran its side effects unconditionally: it swapped the order
  // panel to the abandoned attempt's order and force-closed the dialog the user
  // had reopened and was typing into, discarding what they had entered.
  it("does not let an abandoned attempt's success hijack the session that replaced it", async () => {
    await renderReady();
    let resolveCreate!: (v: { id: string }) => void;
    mockCreateOrder.mockReturnValueOnce(new Promise((res) => { resolveCreate = res; }) as never);
    mockGetOrder.mockResolvedValue(DRAFT_TWO);

    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));
    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    fireEvent.click(screen.getByRole("button", { name: "New order" })); // second session
    await act(async () => { resolveCreate({ id: DRAFT_TWO.id }); });

    // The session the user is in must survive its predecessor landing.
    expect(screen.queryByRole("dialog")).toBeInTheDocument();
    // …and the panel must not have been swapped to the order they gave up on.
    // Asserted on the READ rather than on rendered text: `setActive` lands in a
    // state update that has not necessarily flushed by the time this line runs,
    // so a `queryByText` here passes while the swap is still queued — it let the
    // panel-swap mutant survive until a diagnostic caught the fetch happening
    // anyway. The fetch is the earliest deterministic evidence of the swap.
    expect(mockGetOrder).not.toHaveBeenCalled();
  });

  // Found by an internal review seat against this fix's own first version: the
  // gate was checked after the POST, then a SECOND await (`getOrder`) ran and
  // its result was written unguarded. The whole bug, shifted one round trip
  // later — the dialog survives, but the panel still gets hijacked.
  it("does not swap the panel when the session is abandoned during the follow-up read", async () => {
    await renderReady();
    let resolveCreate!: (v: { id: string }) => void;
    let resolveGet!: (v: SalesOrder) => void;
    mockCreateOrder.mockReturnValueOnce(new Promise((res) => { resolveCreate = res; }) as never);
    mockGetOrder.mockReturnValueOnce(new Promise((res) => { resolveGet = res; }) as never);

    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));
    // The POST lands while the user is still in the session that started it, so
    // the first gate passes and the follow-up read begins.
    await act(async () => { resolveCreate({ id: DRAFT_TWO.id }); });

    // Only NOW does the user give up and start again.
    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    await act(async () => { resolveGet(DRAFT_TWO); });

    // A REGEX, not the bare string: the panel heading renders
    // "SO-2 — Acme Eggs [Draft]" in one element, and `queryByText("SO-2")`
    // demands the element's whole text equal that — so the exact-string form
    // can never match and the assertion passes whatever the code does. A
    // positive control (a clean create, which SHOULD show the order) proved it
    // vacuous before this was corrected.
    expect(screen.queryByText(/SO-2/)).not.toBeInTheDocument();
    expect(screen.queryByRole("dialog")).toBeInTheDocument();
  });

  // codex review of this branch: the key was released only AFTER the follow-up
  // read, which can fail. A succeeded POST plus a failed GET therefore stranded
  // a spent key, and the next order replayed the first one — the customer the
  // user actually chose never got an order. The payment path already states
  // this rule ("the key rotates the moment the WRITE lands", #90); create-order
  // did not follow it.
  it("releases the idempotency key when the write succeeds but the follow-up read fails", async () => {
    await renderReady();
    mockCreateOrder.mockResolvedValueOnce({ id: DRAFT_TWO.id } as never);
    mockGetOrder.mockRejectedValueOnce(new ApiError(500, "Server error", "read failed"));

    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));
    });
    const spentKey = mockCreateOrder.mock.calls[0][1];

    mockCreateOrder.mockResolvedValueOnce({ id: "o9" } as never);
    mockGetOrder.mockResolvedValueOnce(DRAFT_TWO as never);
    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));
    });

    expect(mockCreateOrder).toHaveBeenCalledTimes(2);
    expect(mockCreateOrder.mock.calls[1][1]).not.toBe(spentKey);
  });

  // The POST succeeded, so the attempt is spent whether or not anyone is still
  // watching. Its idempotency key MUST be released: `keys` holds one entry per
  // scope until cleared, so a gate that skipped `clearKey` would make the next
  // order reuse a spent key — the server replays the abandoned order and the
  // customer the user actually chose never gets one.
  it("releases the idempotency key when an abandoned attempt succeeds", async () => {
    await renderReady();
    let resolveCreate!: (v: { id: string }) => void;
    mockCreateOrder.mockReturnValueOnce(new Promise((res) => { resolveCreate = res; }) as never);
    mockGetOrder.mockResolvedValue(DRAFT_TWO);

    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));
    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    await act(async () => { resolveCreate({ id: DRAFT_TWO.id }); });

    const abandonedKey = mockCreateOrder.mock.calls[0][1];
    mockCreateOrder.mockResolvedValueOnce({ id: "o9" } as never);
    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));
    });

    expect(mockCreateOrder).toHaveBeenCalledTimes(2);
    expect(mockCreateOrder.mock.calls[1][1]).not.toBe(abandonedKey);
  });

  it("reports the next attempt after an abandoned one", async () => {
    // The abandonment is per-attempt, not permanent: reopening and failing
    // again must still say so, or the first Cancel would mute the dialog for
    // the rest of the session.
    await renderReady();
    let rejectCreate!: (e: unknown) => void;
    mockCreateOrder.mockReturnValueOnce(new Promise((_, rej) => { rejectCreate = rej; }) as never);
    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));
    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    await act(async () => { rejectCreate(new ApiError(500, "Server error", "abandoned")); });

    mockCreateOrder.mockRejectedValueOnce(
      new ApiError(422, "Validation failed", "Order date cannot be in the future."));
    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));
    });

    expect(within(dialog()).getByText("Order date cannot be in the future.")).toBeInTheDocument();
  });

  it("does not let a background read wipe the open dialog's own message", async () => {
    // Codex, third round: with one slot, a `payments` failure that lands while
    // the dialog is up REPLACES the actionable 422 the user is reading — the
    // form's own explanation vanishes underneath them. The two live in
    // separate state, so neither can overwrite the other.
    let rejectPayments!: (e: unknown) => void;
    mockListOrderPayments.mockReturnValueOnce(
      new Promise((_, rej) => { rejectPayments = rej; }) as never);
    await openOrder(CONFIRMED_9, /Grade A Dozen/);

    mockCreateOrder.mockRejectedValueOnce(
      new ApiError(422, "Validation failed", "Order date cannot be in the future."));
    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));
    });
    expect(within(dialog()).getByText("Order date cannot be in the future.")).toBeInTheDocument();

    await act(async () => { rejectPayments(new Error("boom")); });

    // Still there, and still the dialog's own.
    expect(within(dialog()).getByText("Order date cannot be in the future.")).toBeInTheDocument();
    // The read's failure is reported too — on the page, where it belongs.
    expect(screen.getByText("Could not load this order's payments.")).toBeInTheDocument();
  });

  it("keeps a page failure the user has not dealt with when a dialog opens", async () => {
    // The other half of the split: opening a form clears what the last attempt
    // at THAT form said, not an unrelated failure standing on the page.
    mockListOrderPayments.mockRejectedValueOnce(new Error("boom"));
    await openOrder(CONFIRMED_9, /Grade A Dozen/);
    expect(await screen.findByText("Could not load this order's payments.")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "New order" }));

    expect(screen.getByText("Could not load this order's payments.")).toBeInTheDocument();
    expect(within(dialog()).queryByText("Could not load this order's payments.")).not.toBeInTheDocument();
  });

  it("keeps a page failure while a dialog write runs and fails", async () => {
    // Each attempt clears its OWN slot before it starts. Clearing both would
    // make an unrelated page failure disappear the moment the user tries
    // something else — dismissed by an action that never addressed it.
    mockListOrderPayments.mockRejectedValueOnce(new Error("boom"));
    await openOrder(CONFIRMED_9, /Grade A Dozen/);
    expect(await screen.findByText("Could not load this order's payments.")).toBeInTheDocument();

    mockCreateOrder.mockRejectedValueOnce(
      new ApiError(422, "Validation failed", "Order date cannot be in the future."));
    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));
    });

    expect(within(dialog()).getByText("Order date cannot be in the future.")).toBeInTheDocument();
    expect(screen.getByText("Could not load this order's payments.")).toBeInTheDocument();
  });

  it("keeps someone else's error when a dialog is dismissed", async () => {
    // Only the dialog's OWN message goes with it. A failure that was never
    // this dialog's is still the page's to report.
    let rejectPayments!: (e: unknown) => void;
    mockListOrderPayments.mockReturnValueOnce(
      new Promise((_, rej) => { rejectPayments = rej; }) as never);
    await openOrder(CONFIRMED_9, /Grade A Dozen/);
    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    await act(async () => { rejectPayments(new Error("boom")); });

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    // The name promised a dismissal; assert one happened, or this passes with
    // Cancel wired to nothing (internal review of #478).
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.getByText("Could not load this order's payments.")).toBeInTheDocument();
  });

  it("shows a dialog's failure only in the dialog that raised it, even with both open", async () => {
    // I had written "they are modal, so at most one is ever open" in the
    // source and shared one slot between them on that basis. Nothing enforces
    // it: `creatingOrder` and `paying` are independent, and both triggers stay
    // mounted and enabled. Only the CSS backdrop stops a mouse — not a screen
    // reader's virtual cursor, and not a second click racing the paint
    // (internal review of #478).
    mockListOrderPayments.mockResolvedValue({
      items: [], paidMinorUnits: 0, outstandingMinorUnits: 12000, totalMinorUnits: 12000,
      currencyCode: "USD", currencyMinorUnit: 2,
    });
    await openOrder(CONFIRMED_9, /Grade A Dozen/);
    fireEvent.click(screen.getByRole("button", { name: "Record payment" }));
    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    const dialogs = screen.getAllByRole("dialog");
    expect(dialogs).toHaveLength(2); // the state this fixture exists to cover

    const newOrder = dialogs.find((d) => within(d).queryByRole("button", { name: "New draft order" }))!;
    const payment = dialogs.find((d) => d !== newOrder)!;
    mockCreateOrder.mockRejectedValueOnce(
      new ApiError(422, "Validation failed", "Order date cannot be in the future."));
    await act(async () => {
      fireEvent.click(within(newOrder).getByRole("button", { name: "New draft order" }));
    });

    expect(within(newOrder).getByText("Order date cannot be in the future.")).toBeInTheDocument();
    // The payment form did not fail. It must not say it did.
    expect(within(payment).queryByText("Order date cannot be in the future.")).not.toBeInTheDocument();

    // The other direction — and each form keeps its own. A single slot held
    // one message, so this second failure ERASED the first: the new-order form
    // lost its explanation with nothing happening inside it and no way for the
    // user to know why (internal review of #481). Each dialog owns its entry.
    mockRecordPayment.mockRejectedValueOnce(
      new ApiError(422, "Validation failed", "Payment exceeds the outstanding balance."));
    fireEvent.change(within(payment).getByLabelText(/Amount/), { target: { value: "99" } });
    await act(async () => {
      fireEvent.click(within(payment).getByRole("button", { name: "Record payment" }));
    });

    expect(within(payment).getByText("Payment exceeds the outstanding balance.")).toBeInTheDocument();
    expect(within(newOrder).queryByText("Payment exceeds the outstanding balance.")).not.toBeInTheDocument();
    expect(within(newOrder).getByText("Order date cannot be in the future.")).toBeInTheDocument();

    // And clearing one entry clears ONE entry: dismissing and reopening the
    // payment form drops its own message and leaves the new-order form's.
    fireEvent.click(within(payment).getByRole("button", { name: "Cancel" }));
    fireEvent.click(screen.getByRole("button", { name: "Record payment" }));

    const reopened = screen.getAllByRole("dialog")
      .find((d) => within(d).queryByRole("button", { name: "New draft order" }) === null)!;
    expect(within(reopened).queryByText("Payment exceeds the outstanding balance.")).not.toBeInTheDocument();
    expect(within(newOrder).getByText("Order date cannot be in the future.")).toBeInTheDocument();
  });

  it("does not carry a payment failure across to another order", async () => {
    // Codex on #481: the payment form belongs to the OPEN ORDER, but its key
    // does not say so. Per-dialog entries survive longer than the shared slot
    // did, so a failure left behind when the active order changes would be
    // shown against a different order's money — the worst possible place for a
    // message about a wrong amount.
    mockListOrderPayments.mockResolvedValue({
      items: [], paidMinorUnits: 0, outstandingMinorUnits: 12000, totalMinorUnits: 12000,
      currencyCode: "USD", currencyMinorUnit: 2,
    });
    await openOrder(CONFIRMED_9, /Grade A Dozen/);
    fireEvent.click(await screen.findByRole("button", { name: "Record payment" }));
    mockRecordPayment.mockRejectedValueOnce(
      new ApiError(422, "Validation failed", "Payment exceeds the outstanding balance."));
    fireEvent.change(within(dialog()).getByLabelText(/Amount/), { target: { value: "99" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record payment" }));
    });
    expect(within(dialog()).getByText("Payment exceeds the outstanding balance.")).toBeInTheDocument();

    // Open a different confirmed order while that message is still up.
    mockGetOrder.mockResolvedValue({ ...CONFIRMED_9, id: "o10", referenceNumber: "SO-10" });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "open" }));
    });

    expect(screen.queryByText("Payment exceeds the outstanding balance.")).not.toBeInTheDocument();
    // …and the form itself does not reopen on the new order either.
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("opens the next order's payment form without the last order's failure", async () => {
    // #479 — until now the trigger cleared the slot on the way in, so this was
    // covered by accident. The clear moved onto the dismissal, and the screen
    // closing the form because the ORDER changed is not a dismissal: without an
    // explicit clear there, a 422 about SO-9's money is sitting in SO-10's form
    // when the user opens it. The test therefore has to REOPEN — the previous
    // test stops at "the message is not on screen", which a closed dialog
    // satisfies whether or not the slot was emptied.
    mockListOrderPayments.mockResolvedValue({
      items: [], paidMinorUnits: 0, outstandingMinorUnits: 12000, totalMinorUnits: 12000,
      currencyCode: "USD", currencyMinorUnit: 2,
    });
    await openOrder(CONFIRMED_9, /Grade A Dozen/);
    fireEvent.click(await screen.findByRole("button", { name: "Record payment" }));
    mockRecordPayment.mockRejectedValueOnce(
      new ApiError(422, "Validation failed", "Payment exceeds the outstanding balance."));
    fireEvent.change(within(dialog()).getByLabelText(/Amount/), { target: { value: "99" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Record payment" }));
    });
    expect(within(dialog()).getByText("Payment exceeds the outstanding balance.")).toBeInTheDocument();

    mockGetOrder.mockResolvedValue({ ...CONFIRMED_9, id: "o10", referenceNumber: "SO-10" });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "open" }));
    });
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();

    fireEvent.click(await screen.findByRole("button", { name: "Record payment" }));

    expect(within(dialog()).queryByText("Payment exceeds the outstanding balance."))
      .not.toBeInTheDocument();
  });

  it("clears the form's last message while its next attempt is in flight", async () => {
    // A form mid-save must not still be showing why the PREVIOUS attempt
    // failed — the user cannot tell whether it is a stale message or the
    // verdict on what they just submitted.
    await renderReady();
    mockCreateOrder.mockRejectedValueOnce(
      new ApiError(422, "Validation failed", "Order date cannot be in the future."));
    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));
    });
    expect(within(dialog()).getByText("Order date cannot be in the future.")).toBeInTheDocument();

    mockCreateOrder.mockReturnValueOnce(new Promise(() => {}) as never); // never settles
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));
    });

    expect(within(dialog()).queryByText("Order date cannot be in the future.")).not.toBeInTheDocument();
  });

  it("opening the other dialog leaves the first one's message alone", async () => {
    // The clear is per dialog. Clearing the slot outright would blank a
    // message the OTHER form is still displaying — and the user is still
    // looking at it.
    mockListOrderPayments.mockResolvedValue({
      items: [], paidMinorUnits: 0, outstandingMinorUnits: 12000, totalMinorUnits: 12000,
      currencyCode: "USD", currencyMinorUnit: 2,
    });
    await openOrder(CONFIRMED_9, /Grade A Dozen/);
    mockCreateOrder.mockRejectedValueOnce(
      new ApiError(422, "Validation failed", "Order date cannot be in the future."));
    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));
    });

    fireEvent.click(screen.getByRole("button", { name: "Record payment" })); // the other dialog

    const newOrder = screen.getAllByRole("dialog")
      .find((d) => within(d).queryByRole("button", { name: "New draft order" }))!;
    expect(within(newOrder).getByText("Order date cannot be in the future.")).toBeInTheDocument();
  });

  it("reopening a dialog does not show the message its last attempt left", async () => {
    // The dismissal empties the slot (#479 moved it there from the reopen).
    // Without that, a form opens already accusing the user of a mistake they
    // made minutes ago, about a submission they never made this time.
    await renderReady();
    mockCreateOrder.mockRejectedValueOnce(
      new ApiError(422, "Validation failed", "Order date cannot be in the future."));
    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "New draft order" }));
    });
    expect(within(dialog()).getByText("Order date cannot be in the future.")).toBeInTheDocument();
    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    fireEvent.click(screen.getByRole("button", { name: "New order" }));

    expect(within(dialog()).queryByText("Order date cannot be in the future.")).not.toBeInTheDocument();
  });

  it("still renders a page-level error with no dialog open", async () => {
    // The panel's own writes are not behind a dialog — their errors must keep
    // landing on the page, which is what the page copy's guard exists for.
    await openOrder(DRAFT_TWO, /Grade A Dozen/);
    vi.mocked(addOrderItem).mockRejectedValueOnce(
      new ApiError(422, "Validation failed", "That product is no longer sellable."));

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add line" }));
    });

    expect(screen.getByText("That product is no longer sellable.")).toBeInTheDocument();
  });
});
