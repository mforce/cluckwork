import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act, waitFor } from "@testing-library/react";
import { useLocation, useNavigate } from "react-router";
import { SalesPage } from "./SalesPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { account, NO_RECORD_HISTORY, RECORD_HISTORY } from "../test/fixtures";
import i18n from "../i18n";
import type { DiscountReasonValue } from "../i18n/enums";
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
    status: "Draft", totalMinorUnits: 0, currencyCode, currencyMinorUnit, voidReason: null,
    // A draft has no settlement figure at all — payments attach to confirmed
    // orders only (#769).
    discountReasonCode: null, discountReasonNote: null, outstandingMinorUnits: null, items: [],
  };
}

// A single-line draft for edit/display, price + scale parametrised by currency.
function draftWithItem(currencyMinorUnit: number, currencyCode: string, unitPrice: number, id = "o5"): SalesOrder {
  const item: OrderItem = {
    id: "e1", productId: "p1", eggGradeId: "gr1", unit: "Dozen", baseUnitFactor: 12,
    quantity: 3, quantityBase: 36, unitPriceMinorUnits: unitPrice, currencyCode, currencyMinorUnit,
    listUnitPriceMinorUnits: null,
  };
  return { ...draftEmpty(currencyMinorUnit, currencyCode, id), referenceNumber: "SO-5", items: [item], totalMinorUnits: unitPrice * 3 };
}

// Two lines with DIFFERENT line totals so the order total can't be confused with
// any single line: A = 300×3 = 900 (9.00), B = 1000×2 = 2000 (20.00), order 2900.
const ITEM_A: OrderItem = {
  id: "it1", productId: "p1", eggGradeId: "gr1", unit: "Dozen", baseUnitFactor: 12,
  quantity: 3, quantityBase: 36, unitPriceMinorUnits: 300, currencyCode: "USD", currencyMinorUnit: 2,
  // 375, not 300: a list price EQUAL to unitPriceMinorUnits would render the
  // same "$3.00" text in both the list-price and unit-price cells, breaking
  // "shows per-line base eggs and money" below, which asserts on rowA's
  // unit price by bare text.
  listUnitPriceMinorUnits: 375,
};
const ITEM_B: OrderItem = {
  id: "it2", productId: "p2", eggGradeId: "gr2", unit: "Tray", baseUnitFactor: 30,
  quantity: 2, quantityBase: 60, unitPriceMinorUnits: 1000, currencyCode: "USD", currencyMinorUnit: 2,
  listUnitPriceMinorUnits: 1200,
};
const DRAFT_TWO: SalesOrder = {
  ...draftEmpty(2, "USD", "o2"), referenceNumber: "SO-2", totalMinorUnits: 2900, items: [ITEM_A, ITEM_B],
};

// #721 — the same two lines sold AT list, so confirming asks the plain yes/no
// instead of the discount-reason picklist. Unit prices are untouched, so the
// order total is still 2900.
const DRAFT_TWO_AT_LIST: SalesOrder = {
  ...DRAFT_TWO,
  items: [
    { ...ITEM_A, listUnitPriceMinorUnits: ITEM_A.unitPriceMinorUnits },
    { ...ITEM_B, listUnitPriceMinorUnits: ITEM_B.unitPriceMinorUnits },
  ],
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

  it("sends the product's list price as the expectation on the add-item request", async () => {
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));
    mockAddOrderItem.mockResolvedValue({ orderId: "o1", itemId: "new" });

    // PRODUCT_A's own default (300), not the typed price — the point is to
    // catch the CATALOGUE moving, not to echo what the seller typed.
    fireEvent.change(screen.getByLabelText(/Unit price/), { target: { value: "2.00" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add line" }));
    });
    expect(mockAddOrderItem.mock.calls[0][1]).toMatchObject({ expectedListUnitPriceMinorUnits: 300 });
  });

  it("sends expectedListPriceIsUnset when the shown product has no list price", async () => {
    // #720 R2 — "the seller saw no list price" is an expectation, distinct
    // from having no opinion at all; it must ride as its OWN flag, not be
    // silently dropped alongside "no value".
    mockListProducts.mockResolvedValue([{ ...PRODUCT_A, defaultPriceMinorUnits: null }, PRODUCT_B]);
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));
    mockAddOrderItem.mockResolvedValue({ orderId: "o1", itemId: "new" });

    fireEvent.change(screen.getByLabelText(/Unit price/), { target: { value: "2.00" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add line" }));
    });
    const body = mockAddOrderItem.mock.calls[0][1];
    expect(body).toMatchObject({ expectedListPriceIsUnset: true });
    expect(body).not.toHaveProperty("expectedListUnitPriceMinorUnits");
  });

  it("sends neither list-price field once the selected product drops out of the refreshed list", async () => {
    // No opinion, not "the seller saw nothing": productId still names p1
    // after a rejection-triggered refresh whose response no longer carries
    // it — the same reachable-staleness shape as the refetch test below.
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));
    mockAddOrderItem.mockRejectedValueOnce(new ApiError(422, "SalesOrder.ListPriceChanged",
      "This product's list price is now 999, not 300 — re-check the price and try again."));
    mockListProducts.mockResolvedValue([PRODUCT_B]); // p1 (PRODUCT_A) is gone

    fireEvent.change(screen.getByLabelText(/Unit price/), { target: { value: "2.00" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add line" }));
    });
    expect(await screen.findByText(/is now 999, not 300/)).toBeInTheDocument();

    mockAddOrderItem.mockResolvedValue({ orderId: "o1", itemId: "new" });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add line" }));
    });
    const body = mockAddOrderItem.mock.calls[1][1];
    expect(body).not.toHaveProperty("expectedListUnitPriceMinorUnits");
    expect(body).not.toHaveProperty("expectedListPriceIsUnset");
  });

  it("refreshes products after a ListPriceChanged rejection, so the retry carries the current list price", async () => {
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));
    mockAddOrderItem.mockRejectedValueOnce(new ApiError(422, "SalesOrder.ListPriceChanged",
      "This product's list price is now 999, not 300 — re-check the price and try again."));
    mockListProducts.mockResolvedValue([{ ...PRODUCT_A, defaultPriceMinorUnits: 999 }, PRODUCT_B]);

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add line" }));
    });
    expect(await screen.findByText(/is now 999, not 300/)).toBeInTheDocument();

    mockAddOrderItem.mockResolvedValue({ orderId: "o1", itemId: "new" });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add line" }));
    });
    // Without the refetch this would still send the stale 300.
    expect(mockAddOrderItem.mock.calls[1][1]).toMatchObject({ expectedListUnitPriceMinorUnits: 999 });
  });

  it("hints a below-list amount and percent while the typed price undercuts the product's list price", async () => {
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));

    // PRODUCT_A lists at 300; typing 200 is 100 under, 33.3%.
    fireEvent.change(screen.getByLabelText(/Unit price/), { target: { value: "2.00" } });
    expect(screen.getByText("$1.00 below list (33.3%)")).toBeInTheDocument();
  });

  it("hints an above-list amount and percent while the typed price exceeds the product's list price", async () => {
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));

    // PRODUCT_A lists at 300; typing 400 is 100 over, 33.3%.
    fireEvent.change(screen.getByLabelText(/Unit price/), { target: { value: "4.00" } });
    expect(screen.getByText("$1.00 above list (33.3%)")).toBeInTheDocument();
  });

  // #720 R11 — R8 (a hint inside its own grid cell) and R9 (position:absolute
  // out of that cell) both broke on the SAME shape: a cell taller than its
  // siblings floats above the row under .form-grid's align-items:end, and a
  // fixed out-of-flow reservation for the hint overlapped Add line once a
  // translation wrapped past one line. R11 removes the cell entirely — the
  // hint is a normal block AFTER .form-grid, not a child of it — so the row
  // is what holds the invariant now, not the hint's own positioning. This
  // asserts the two facts that actually matter: every field (Unit price
  // included) still shares one row with Add line, and the hint is NOT inside
  // that row to begin with.
  it("keeps the Unit price field in the same .form-grid row as Add line, with the hint OUTSIDE that row (#720 R11)", async () => {
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));

    fireEvent.change(screen.getByLabelText(/Unit price/), { target: { value: "2.00" } });
    const hint = screen.getByText("$1.00 below list (33.3%)");
    const priceField = screen.getByLabelText(/Unit price/);
    const addLineBtn = screen.getByRole("button", { name: "Add line" });
    const row = priceField.closest(".form-grid");

    expect(row).not.toBeNull();
    expect(addLineBtn.closest(".form-grid")).toBe(row);
    expect(row).not.toContainElement(hint);
  });

  it("hints an above-list amount with NO percent when the product's list price is zero", async () => {
    mockListProducts.mockResolvedValue([{ ...PRODUCT_A, defaultPriceMinorUnits: 0 }, PRODUCT_B]);
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));

    fireEvent.change(screen.getByLabelText(/Unit price/), { target: { value: "5.00" } });
    expect(screen.getByText("$5.00 above list")).toBeInTheDocument();
    expect(screen.queryByText(/%/)).not.toBeInTheDocument();
  });

  it("hints a below-list amount with NO percent when the product's list price is zero", async () => {
    // <input min={0}> is a validation constraint, not an input filter — a
    // negative typed price is reachable (paste, keyboard) and would divide
    // by a zero list price for the below branch's percent.
    mockListProducts.mockResolvedValue([{ ...PRODUCT_A, defaultPriceMinorUnits: 0 }, PRODUCT_B]);
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));

    fireEvent.change(screen.getByLabelText(/Unit price/), { target: { value: "-5.00" } });
    expect(screen.getByText("$5.00 below list")).toBeInTheDocument();
    expect(screen.queryByText(/%/)).not.toBeInTheDocument();
  });

  it("shows no hint when the selected product has no list price", async () => {
    mockListProducts.mockResolvedValue([{ ...PRODUCT_A, defaultPriceMinorUnits: null }, PRODUCT_B]);
    await renderReady();
    await createDraft(draftEmpty(2, "USD"));

    fireEvent.change(screen.getByLabelText(/Unit price/), { target: { value: "2.00" } });
    expect(screen.queryByText(/list/)).not.toBeInTheDocument();
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

// #720 — the list price snapshot and the discount it implies, rendered per
// line. Four states: none, at list, below list, above list.
describe("SalesPage list price and discount (#720)", () => {
  it('shows "No list price" when the line has none — in the chip AND in the discount cell', async () => {
    const row = await openOrder(draftWithItem(2, "USD", 500, "o-nolist"), /Grade A Dozen/);
    // #723 — TWO mentions, deliberately: the chip beside the product names the
    // state where the eye lands, and the Discount cell keeps the wording #720
    // shipped and the Help text documents. Pinned at exactly 2 so a future
    // change that drops either one goes red rather than silently halving it.
    expect(within(row).getAllByText(i18n.t("sales:noListPrice"))).toHaveLength(2);
  });

  it("puts No list price in the DISCOUNT cell and an em dash in the LIST PRICE cell", async () => {
    const row = await openOrder(draftWithItem(2, "USD", 500, "o-nolist-cells"), /Grade A Dozen/);
    const cells = within(row).getAllByRole("cell");

    // The design's four-state table, and the owner's mockup, put these two the
    // way round below. It is not cosmetic: if "No list price" sits in the List
    // price cell, the Discount cell falls through to an em dash — which is
    // exactly what an AT-LIST line renders, so "we do not know" becomes
    // indistinguishable from "no discount was given" (INV-3, criterion 6).
    expect(cells[3]).toHaveTextContent("—");
    expect(cells[5]).toHaveTextContent(i18n.t("sales:noListPrice"));
    // #720 R7 — "we do not know" is not a discount either.
    expect(cells[5]).not.toHaveClass("discount");
  });

  it("shows an em dash for the discount when the line sold exactly at list", async () => {
    const order: SalesOrder = {
      ...draftEmpty(2, "USD", "o-atlist"),
      referenceNumber: "SO-ATLIST",
      totalMinorUnits: 900,
      items: [{
        id: "it-atlist", productId: "p1", eggGradeId: "gr1", unit: "Dozen", baseUnitFactor: 12,
        quantity: 3, quantityBase: 36, unitPriceMinorUnits: 300, currencyCode: "USD", currencyMinorUnit: 2,
        listUnitPriceMinorUnits: 300,
      }],
    };
    const row = await openOrder(order, /Grade A Dozen/);
    // The list price cell and the unit price cell show the same amount at list.
    expect(within(row).getAllByText("$3.00")).toHaveLength(2);
    expect(within(row).getByText("—")).toBeInTheDocument();
    // #720 R7 — at list is not a discount; emphasising it would be the same
    // "unknown reads as a discount" conflation this slice has already fixed
    // twice (once for "No list price" landing in the wrong cell, R2).
    expect(within(row).getByText("—")).not.toHaveClass("discount");
  });

  it("shows an amount and a percent when the line sold below list, emphasised", async () => {
    // ITEM_B: sold 1000, list 1200 → 400 minor units back (200/unit × 2), 16.7%.
    const row = await openOrder(DRAFT_TWO, /Grade B Tray/);
    expect(within(row).getByText("$12.00")).toBeInTheDocument(); // list price
    // #720 R7 — a text-only check would pass against an unstyled cell; the
    // emphasis IS the point of this render, so the class is asserted too.
    expect(within(row).getByText("$4.00 · 16.7%")).toHaveClass("discount");
  });

  it("shows Above list when the line sold above list", async () => {
    const order: SalesOrder = {
      ...draftEmpty(2, "USD", "o-above"),
      referenceNumber: "SO-ABOVE",
      totalMinorUnits: 900,
      items: [{
        id: "it-above", productId: "p1", eggGradeId: "gr1", unit: "Dozen", baseUnitFactor: 12,
        quantity: 3, quantityBase: 36, unitPriceMinorUnits: 300, currencyCode: "USD", currencyMinorUnit: 2,
        listUnitPriceMinorUnits: 250,
      }],
    };
    const row = await openOrder(order, /Grade A Dozen/);
    expect(within(row).getByText("$2.50")).toBeInTheDocument(); // list price
    expect(within(row).getByText(i18n.t("sales:aboveList"))).toBeInTheDocument();
    // #720 R8 — a line that gave nothing away is not a discount either;
    // applying the emphasis here survived as an untested mutant (M20) until
    // this assertion existed.
    expect(within(row).getByText(i18n.t("sales:aboveList"))).not.toHaveClass("discount");
  });

  // #723 — colour is not the only signal. A discounted row carries a text chip
  // and a struck-through list price, both of which survive greyscale; the tint
  // is the third layer, asserted through the row's class because jsdom computes
  // no layout.
  it("marks a below-list row with a chip, a struck list price and the row class", async () => {
    // ITEM_B: sold 1000 against a list of 1200 → below list.
    const row = await openOrder(DRAFT_TWO, /Grade B Tray/);
    // Both classes: `badge` is the pill, `badge-warn` is what the stylesheet's
    // `tr.discounted .badge-warn` rule keys on to lift the chip off the row's
    // own tint. Asserting only `badge` let the JSX drop `badge-warn`, orphaning
    // that rule and restoring the invisible-chip defect with the suite green.
    expect(within(row).getByText(i18n.t("sales:belowListBadge"))).toHaveClass("badge", "badge-warn");
    expect(row).toHaveClass("discounted");
    // The list-price money is struck through — the <s> element, not a class, so
    // it survives a stylesheet change and reads as struck to a screen reader.
    expect(within(row).getByText("$12.00").closest("s")).not.toBeNull();
  });

  it("gives an at-list row no chip, no strikethrough and no row class", async () => {
    const order: SalesOrder = {
      ...draftEmpty(2, "USD", "o-atlist-mark"),
      referenceNumber: "SO-ATLIST-MARK",
      totalMinorUnits: 900,
      items: [{
        id: "it-atlist-mark", productId: "p1", eggGradeId: "gr1", unit: "Dozen", baseUnitFactor: 12,
        quantity: 3, quantityBase: 36, unitPriceMinorUnits: 300, currencyCode: "USD", currencyMinorUnit: 2,
        listUnitPriceMinorUnits: 300,
      }],
    };
    const row = await openOrder(order, /Grade A Dozen/);
    expect(within(row).queryByText(i18n.t("sales:belowListBadge"))).toBeNull();
    expect(row).not.toHaveClass("discounted");
    // The fixture renders $3.00 in the List price AND the Unit price cell.
    // Checking only the first let an implementation that struck the other pass.
    for (const match of within(row).getAllByText("$3.00")) {
      expect(match.closest("s")).toBeNull();
    }
  });

  it("chips a no-list-price row without marking it discounted", async () => {
    const order: SalesOrder = {
      ...draftEmpty(2, "USD", "o-null-mark"),
      referenceNumber: "SO-NULL-MARK",
      totalMinorUnits: 900,
      items: [{
        id: "it-null-mark", productId: "p1", eggGradeId: "gr1", unit: "Dozen", baseUnitFactor: 12,
        quantity: 3, quantityBase: 36, unitPriceMinorUnits: 300, currencyCode: "USD", currencyMinorUnit: 2,
        listUnitPriceMinorUnits: null,
      }],
    };
    const row = await openOrder(order, /Grade A Dozen/);
    // The chip names the state beside the product; the Discount cell keeps the
    // wording #720 shipped and the Help text documents. Both, deliberately.
    const noListMatches = within(row).getAllByText(i18n.t("sales:noListPrice"));
    expect(noListMatches).toHaveLength(2);
    // One is the chip beside the product (cell 0), the other is the Discount
    // cell's own wording. Counting alone let two plain strings in any two cells
    // satisfy a test named for a chip.
    const productCell = within(row).getAllByRole("cell")[0];
    expect(within(productCell).getByText(i18n.t("sales:noListPrice"))).toHaveClass("badge");
    expect(row).not.toHaveClass("discounted");
  });

  // #723 — the order-level figure. Percent is off LIST, over comparable lines
  // only: DRAFT_TWO is ITEM_A (3 x 300 against list 375) + ITEM_B (2 x 1000
  // against list 1200), so the give-away is 3x75 + 2x200 = 625 and the list
  // value is 3x375 + 2x1200 = 3525 → 17.7%.
  it("totals the order's discount above the order total", async () => {
    await openOrder(DRAFT_TWO, /Grade B Tray/);
    const paragraph = screen.getByTestId("order-discount");
    expect(paragraph).toHaveTextContent(i18n.t("sales:discountTotal", { amount: "$6.25", percent: "17.7" }));
    // ABOVE is half the requirement and was the untested half: the element
    // immediately following the paragraph is the order total.
    expect(paragraph.nextElementSibling?.textContent).toContain("$29.00");
  });

  // The one arithmetic error the PROTECTED helper exists to prevent: an
  // ABOVE-list line is comparable and belongs in the DENOMINATOR, never in the
  // numerator. Line 1 sells 110 against a list of 100 (above); line 2 sells 90
  // against a list of 100 (below, giving 10 back). List value is 100 + 100 =
  // 200, so the answer is $0.10 and 5.0% — not the 10% an implementation that
  // drops above-list lines from the denominator would print.
  it("counts an above-list line in the denominator but never in the discount", async () => {
    const order: SalesOrder = {
      ...draftEmpty(2, "USD", "o-above-mix"),
      referenceNumber: "SO-ABOVE-MIX",
      totalMinorUnits: 200,
      items: [
        { id: "it-am1", productId: "p1", eggGradeId: "gr1", unit: "Dozen", baseUnitFactor: 12,
          quantity: 1, quantityBase: 12, unitPriceMinorUnits: 110, currencyCode: "USD", currencyMinorUnit: 2,
          listUnitPriceMinorUnits: 100 },
        { id: "it-am2", productId: "p2", eggGradeId: "gr2", unit: "Tray", baseUnitFactor: 30,
          quantity: 1, quantityBase: 30, unitPriceMinorUnits: 90, currencyCode: "USD", currencyMinorUnit: 2,
          listUnitPriceMinorUnits: 100 },
      ],
    };
    await openOrder(order, /Grade A Dozen/);
    expect(screen.getByTestId("order-discount"))
      .toHaveTextContent(i18n.t("sales:discountTotal", { amount: "$0.10", percent: "5.0" }));
  });

  it("says the figure covers only part of an order carrying a no-list-price line", async () => {
    const order: SalesOrder = {
      ...draftEmpty(2, "USD", "o-partial"),
      referenceNumber: "SO-PARTIAL",
      totalMinorUnits: 1200,
      items: [
        { id: "it-p1", productId: "p1", eggGradeId: "gr1", unit: "Dozen", baseUnitFactor: 12,
          quantity: 1, quantityBase: 12, unitPriceMinorUnits: 300, currencyCode: "USD", currencyMinorUnit: 2,
          listUnitPriceMinorUnits: 400 },
        { id: "it-p2", productId: "p2", eggGradeId: "gr2", unit: "Tray", baseUnitFactor: 30,
          quantity: 1, quantityBase: 30, unitPriceMinorUnits: 900, currencyCode: "USD", currencyMinorUnit: 2,
          listUnitPriceMinorUnits: null },
      ],
    };
    await openOrder(order, /Grade A Dozen/);
    // Scoped to the paragraph. `openOrder` stubs listOrders with this SAME
    // order (SalesPage.test.tsx:197-205) and the Orders table renders outside
    // the `{active && …}` panel, so once Increment 3 lands its cell prints the
    // same note a second time — and a page-wide getByText throws on two
    // matches. Scoping here keeps Increment 2's test green at 3c.
    expect(within(screen.getByTestId("order-discount"))
      .getByText(i18n.t("sales:discountPartialNote"), { exact: false })).toBeInTheDocument();
  });

  it("shows no Discount total when every comparable line sold at list", async () => {
    const order: SalesOrder = {
      ...draftEmpty(2, "USD", "o-atlist-total"),
      referenceNumber: "SO-ATLIST-TOTAL",
      totalMinorUnits: 900,
      items: [{
        id: "it-atlist-total", productId: "p1", eggGradeId: "gr1", unit: "Dozen", baseUnitFactor: 12,
        quantity: 3, quantityBase: 36, unitPriceMinorUnits: 300, currencyCode: "USD", currencyMinorUnit: 2,
        listUnitPriceMinorUnits: 300,
      }],
    };
    await openOrder(order, /Grade A Dozen/);
    // #723 acceptance: an at-list order carries no discount treatment at all.
    // Asserted on the paragraph's own hook, NOT on a regex: a regex built from
    // another fixture's numbers matches nothing whatever the code does, so it
    // is a test that cannot fail. This one goes red if the paragraph renders
    // for an at-list order at all — including as "−$0.00 · 0.0% of list".
    expect(screen.queryByTestId("order-discount")).toBeNull();
  });

  // A ZERO list price is legal — Product.cs:38 rejects only negatives — so the
  // denominator can be zero with a comparable line present. Dividing by it
  // yields Infinity, which would render as an "∞%" discount.
  it("omits the percent when every comparable line has a zero list price", async () => {
    const order: SalesOrder = {
      ...draftEmpty(2, "USD", "o-zero-list"),
      referenceNumber: "SO-ZERO-LIST",
      totalMinorUnits: 0,
      items: [{
        id: "it-zero-list", productId: "p1", eggGradeId: "gr1", unit: "Dozen", baseUnitFactor: 12,
        quantity: 1, quantityBase: 12, unitPriceMinorUnits: -100, currencyCode: "USD", currencyMinorUnit: 2,
        listUnitPriceMinorUnits: 0,
      }],
    };
    await openOrder(order, /Grade A Dozen/);
    // Both assertions are scoped to the ORDER paragraph, which is what this
    // test is named for. A page-wide sweep for ∞ also catches #720's shipped
    // per-LINE Discount cell, which has no such fallback and does render "∞%"
    // for this fixture — a different surface, owned by a shipped slice, and one
    // no API caller can reach: UpdateOrderItemValidator.cs:11 requires
    // UnitPriceMinorUnits >= 0, and a below-list line against a zero list price
    // needs a NEGATIVE unit price.
    const paragraph = screen.getByTestId("order-discount");
    expect(paragraph).toHaveTextContent(i18n.t("sales:discountTotalNoPct", { amount: "$1.00" }));
    // textContent, not within(...).queryByText: the percent is text in the <p>
    // itself, not in a descendant element, so a queryByText inside the
    // paragraph would return null however wrong the code went.
    expect(paragraph.textContent).not.toMatch(/∞|Infinity|NaN/);
  });

  // Round 1, render-states seat. An order with nothing discounted but something
  // unmeasurable must NOT read as a measured at-list order.
  it("says so when nothing was discounted but part of the order has no list price", async () => {
    const order: SalesOrder = {
      ...draftEmpty(2, "USD", "o-atlist-partial"),
      referenceNumber: "SO-ATLIST-PARTIAL",
      totalMinorUnits: 1200,
      items: [
        { id: "it-ap1", productId: "p1", eggGradeId: "gr1", unit: "Dozen", baseUnitFactor: 12,
          quantity: 1, quantityBase: 12, unitPriceMinorUnits: 300, currencyCode: "USD", currencyMinorUnit: 2,
          listUnitPriceMinorUnits: 300 },
        { id: "it-ap2", productId: "p2", eggGradeId: "gr2", unit: "Tray", baseUnitFactor: 30,
          quantity: 1, quantityBase: 30, unitPriceMinorUnits: 900, currencyCode: "USD", currencyMinorUnit: 2,
          listUnitPriceMinorUnits: null },
      ],
    };
    await openOrder(order, /Grade A Dozen/);
    expect(screen.getByTestId("order-discount-partial"))
      .toHaveTextContent(i18n.t("sales:discountPartialOnly"));
    expect(screen.getByTestId("order-discount-partial")).toHaveClass("discount-note");
  });

  // Round 2. The Orders list says "Unknown" for an order no line of which can
  // be measured; the panel said nothing at all, so opening the order made the
  // warning disappear.
  it("says the order cannot be measured at all when no line has a list price", async () => {
    const order: SalesOrder = {
      ...draftEmpty(2, "USD", "o-unknown-panel"),
      referenceNumber: "SO-UNKNOWN-PANEL",
      totalMinorUnits: 900,
      items: [{
        id: "it-up1", productId: "p1", eggGradeId: "gr1", unit: "Dozen", baseUnitFactor: 12,
        quantity: 3, quantityBase: 36, unitPriceMinorUnits: 300, currencyCode: "USD", currencyMinorUnit: 2,
        listUnitPriceMinorUnits: null,
      }],
    };
    await openOrder(order, /Grade A Dozen/);
    expect(screen.getByTestId("order-discount-unknown"))
      .toHaveTextContent(i18n.t("sales:discountUnknownOrder"));
    expect(screen.getByTestId("order-discount-unknown")).toHaveClass("discount-note");
  });
});

describe("SalesPage Orders-list discount column (#724)", () => {
  // `renderReady()` is this file's own mount helper (SalesPage.test.tsx:171):
  // it renders <SalesPage/> with the ADMIN token and awaits the "New order"
  // button, and the file's beforeEach already stubs every other network call.
  // Do NOT call renderWithProviders directly here — the page would race its
  // own setup reads.
  // The list route returns items — SaleEndpoints.ToResponse projects
  // o.Items for both /sales and /sales/{id}, and SalesOrderRepository.ListAsync
  // Includes them — so the cell is computed from data already on the page.
  function listedOrder(
    id: string, items: OrderItem[], totalMinorUnits: number,
    outstandingMinorUnits: number | null = null,
  ): SalesOrder {
    return {
      ...NO_RECORD_HISTORY,
      id, customerId: "c1", customerName: "Acme Eggs", referenceNumber: `SO-${id}`,
      orderDate: "2026-07-20", status: "Confirmed", totalMinorUnits,
      currencyCode: "USD", currencyMinorUnit: 2, voidReason: null,
      discountReasonCode: null, discountReasonNote: null, outstandingMinorUnits, items,
    };
  }

  it("badges a discounted order with the percent leading the amount", async () => {
    mockListOrders.mockResolvedValue([listedOrder("disc", [ITEM_A, ITEM_B], 2900)]);
    await renderReady();
    const row = screen.getByRole("row", { name: /SO-disc/ });
    // Cell 4 is the Discount column: Reference, Date, Customer, Status,
    // Discount, Total, Provenance, actions. Scoping to the row alone let the
    // text pass from any cell, and let a plain string pass as a badge.
    const cell = within(row).getAllByRole("cell")[4];
    expect(within(cell).getByText(
      i18n.t("sales:discountBadge", { percent: "17.7", amount: "$6.25" }),
    )).toHaveClass("badge");
  });

  it("shows an em dash for an order sold entirely at list", async () => {
    const atList: OrderItem = { ...ITEM_A, id: "at1", listUnitPriceMinorUnits: 300 };
    mockListOrders.mockResolvedValue([listedOrder("atlist", [atList], 900)]);
    await renderReady();
    const row = screen.getByRole("row", { name: /SO-atlist/ });
    // Assert the CELL, by index. A bare text lookup for "—" is ambiguous: the
    // row's ProvenanceCell also renders one under NO_RECORD_HISTORY
    // (ProvenanceCell.tsx:57). Cells are Reference, Date, Customer, Status,
    // Discount, Total, Provenance, actions — so the new column is index 4.
    // Without this, an implementation returning null for every non-below order
    // leaves the cell EMPTY and both negative assertions still pass.
    expect(within(row).getAllByRole("cell")[4]).toHaveTextContent("—");
    expect(within(row).queryByText(/%/)).toBeNull();
  });

  it("an order with no lines reads as an em dash, not as Unknown", async () => {
    // The Orders list ALREADY renders empty orders — SalesPage.test.tsx:1310
    // lists HISTORY_ORDER, built from draftEmpty (:102) with items: []. An
    // empty draft is not an order we cannot measure; calling it Unknown would
    // be a user-visible lie, so the empty case is pinned here rather than left
    // to the "no comparable line" branch.
    mockListOrders.mockResolvedValue([listedOrder("empty", [], 0)]);
    await renderReady();
    const row = screen.getByRole("row", { name: /SO-empty/ });
    expect(within(row).getAllByRole("cell")[4]).toHaveTextContent("—");
    expect(within(row).queryByText(i18n.t("sales:discountUnknown"))).toBeNull();
  });

  it("reads Unknown for an order predating the list-price snapshot", async () => {
    const preSnapshot: OrderItem = { ...ITEM_A, id: "pre1", listUnitPriceMinorUnits: null };
    mockListOrders.mockResolvedValue([listedOrder("pre", [preSnapshot], 900)]);
    await renderReady();
    const row = screen.getByRole("row", { name: /SO-pre/ });
    // #719: an order with no snapshot reads as unknown, never as a clean zero.
    expect(within(row).getAllByRole("cell")[4]).toHaveTextContent(i18n.t("sales:discountUnknown"));
    // The wrap class is applied here, not just declared in the stylesheet: this
    // cell is inside td.num, which is pinned white-space: nowrap.
    expect(within(within(row).getAllByRole("cell")[4]).getByText(i18n.t("sales:discountUnknown")))
      .toHaveClass("discount-note");
  });

  it("does not print a bare em dash for an order only part of which can be measured", async () => {
    const atList: OrderItem = { ...ITEM_A, id: "ap1", listUnitPriceMinorUnits: 300 };
    const noList: OrderItem = { ...ITEM_B, id: "ap2", listUnitPriceMinorUnits: null };
    mockListOrders.mockResolvedValue([listedOrder("partial", [atList, noList], 2900)]);
    await renderReady();
    const row = screen.getByRole("row", { name: /SO-partial/ });
    const cell = within(row).getAllByRole("cell")[4];
    // The em dash means "sold at list". This order was not fully measured, so
    // the cell must carry the note instead.
    expect(cell).toHaveTextContent(i18n.t("sales:discountPartialNote"));
    expect(cell.textContent?.trim()).not.toBe("—");
  });

  // The BELOW-list partial branch: a discounted order that also carries an
  // unmeasurable line. The at-list partial test above never enters it, which
  // left SalesPage.tsx:1537 the one `discount-note` call site with no assertion
  // — so the class could be dropped there and the note would stop wrapping
  // inside td.num, with the suite green. Found by CodeRabbit on 0c65418.
  it("wraps the partial note on a DISCOUNTED order that also has an unmeasurable line", async () => {
    const below: OrderItem = { ...ITEM_A, id: "bp1", listUnitPriceMinorUnits: 375 };
    const noList: OrderItem = { ...ITEM_B, id: "bp2", listUnitPriceMinorUnits: null };
    mockListOrders.mockResolvedValue([listedOrder("belowpartial", [below, noList], 2900)]);
    await renderReady();
    const row = screen.getByRole("row", { name: /SO-belowpartial/ });
    const cell = within(row).getAllByRole("cell")[4];
    // The badge renders (it IS discounted) AND the partial note is present and wrappable.
    expect(within(cell).getByText(/%/)).toHaveClass("badge");
    expect(within(cell).getByText(i18n.t("sales:discountPartialNote"))).toHaveClass("discount-note");
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
        listUnitPriceMinorUnits: 1200,
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
    // At list, so this stays the plain yes/no path. The below-list path has its
    // own block below (#721).
    await openOrder(DRAFT_TWO_AT_LIST, /Grade A Dozen/);
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

    // No body at all for an at-list order: the server refuses a discount reason
    // on an order that gave nothing away (#721).
    expect(vi.mocked(confirmOrder)).toHaveBeenCalledWith("o2", undefined, expect.any(String));
  });

  // #612 — the distinct, generic 422 a restricted Worker gets is not a dialog
  // scope (only create-order/record-payment are), so it lands on the SAME
  // page-level error paragraph as every other confirm failure — the smallest
  // existing error-display mechanism, not a bespoke banner. client.ts (#612)
  // is what turns the domain-error TITLE into this localized text; here the
  // mocked confirmOrder rejects with the ALREADY-resolved message, same as a
  // real ApiError leaving the fetch client.
  it("shows the localized assigned-flocks-insufficient-stock warning on the page, generically", async () => {
    await openOrder(DRAFT_TWO_AT_LIST, /Grade A Dozen/);
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

  // #721 — a below-list order cannot be confirmed without a reason, so the
  // plain yes/no is replaced by a picklist. The predicate is lineDiscount's
  // "below", the same one the row markers use, so an at-list or above-list
  // order is untouched (covered by the at-list case above).
  describe("discount reason at confirm (#721)", () => {
    const reason = (value: DiscountReasonValue) =>
      within(dialog()).getByRole("radio", { name: i18n.t(`enums:discountReason.${value}`) });
    const accept = () =>
      within(dialog()).getByRole("button", { name: i18n.t("sales:confirmOrderConfirmLabel") });

    async function openBelowListAndAsk() {
      await openOrder(DRAFT_TWO, /Grade A Dozen/);
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:confirmOrderButton") }));
      });
    }

    it("asks for a reason instead of the plain confirmation", async () => {
      await openBelowListAndAsk();

      // Same title and same FIFO prose as the plain confirmation — the reason
      // is one more required field on the confirm, not a separate scolding.
      expect(dialog()).toHaveAccessibleName(i18n.t("sales:confirmOrderTitle"));
      expect(within(dialog()).getByText(i18n.t("sales:confirmOrderBody"))).toBeInTheDocument();
      expect(within(dialog()).getAllByRole("radio").length).toBeGreaterThan(0);
      expect(vi.mocked(confirmOrder)).not.toHaveBeenCalled();
    });

    // The mockup on #721: "the person confirming sees what they are giving away
    // before they justify it." Both figures come from the same
    // orderDiscount/lineDiscount the screen behind the dialog renders.
    it("shows what the order gives away, and on which lines", async () => {
      await openBelowListAndAsk();

      // ITEM_A: 375 list, sold at 300, x3 -> 2.25 off, 20%.
      // ITEM_B: 1200 list, sold at 1000, x2 -> 4.00 off, 16.7%.
      // Order: 6.25 off a list value of 375x3 + 1200x2 = 3525 -> 17.7%.
      expect(screen.getByTestId("confirm-discount-headline")).toHaveTextContent(
        "This order is $6.25 below list price, 17.7% of the order, across 2 of 2 lines.");
      const lines = within(screen.getByTestId("confirm-discount-lines")).getAllByRole("listitem");
      expect(lines.map((li) => li.textContent)).toEqual([
        "Grade A Dozen20.0% · $2.25",
        "Grade B Tray16.7% · $4.00",
      ]);
    });

    it("lists only the lines that are actually below list", async () => {
      const atList = { ...ITEM_B, id: "atlist", listUnitPriceMinorUnits: ITEM_B.unitPriceMinorUnits };
      await openOrder({ ...DRAFT_TWO, items: [ITEM_A, atList] }, /Grade A Dozen/);
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:confirmOrderButton") }));
      });

      const lines = within(screen.getByTestId("confirm-discount-lines")).getAllByRole("listitem");
      expect(lines).toHaveLength(1);
      expect(lines[0]).toHaveTextContent("Grade A Dozen");
      expect(screen.getByTestId("confirm-discount-headline")).toHaveTextContent("across 1 of 2 lines");
    });

    // Literal labels, not a map over the same constant SalesPage renders: that
    // comparison moves with any rename and would stay green. The wire VALUES
    // are pinned against the C# enum from the other side, by
    // DiscountReasonVocabularyTests.
    it("offers every reason, labelled, most routine first", async () => {
      await openBelowListAndAsk();

      const radios = within(dialog()).getAllByRole("radio");
      expect(radios.map((r) => r.getAttribute("value"))).toEqual([
        "Volume", "DamagedStock", "LongStandingCustomer", "ManagerApproved", "Other",
      ]);
      expect(radios.map((r) => r.closest("label")?.textContent)).toEqual([
        "Volume", "Damaged stock", "Long-standing customer", "Manager approved", "Other",
      ]);
    });

    it("sends the chosen reason with the confirm", async () => {
      await openBelowListAndAsk();
      vi.mocked(confirmOrder).mockResolvedValue(undefined as never);
      mockGetOrder.mockResolvedValue({ ...DRAFT_TWO, status: "Confirmed" });

      fireEvent.click(reason("DamagedStock"));
      await act(async () => { fireEvent.click(accept()); });

      expect(vi.mocked(confirmOrder)).toHaveBeenCalledWith(
        DRAFT_TWO.id, { discountReasonCode: "DamagedStock" }, expect.any(String));
    });

    it("sends a note when one is typed", async () => {
      await openBelowListAndAsk();
      vi.mocked(confirmOrder).mockResolvedValue(undefined as never);
      mockGetOrder.mockResolvedValue({ ...DRAFT_TWO, status: "Confirmed" });

      fireEvent.click(reason("Other"));
      fireEvent.change(
        within(dialog()).getByLabelText(i18n.t("sales:discountReasonNoteLabel")),
        { target: { value: "  agreed with the buyer  " } });
      await act(async () => { fireEvent.click(accept()); });

      expect(vi.mocked(confirmOrder)).toHaveBeenCalledWith(
        DRAFT_TWO.id,
        { discountReasonCode: "Other", discountReasonNote: "agreed with the buyer" },
        expect.any(String));
    });

    it("refuses to confirm with no reason chosen, and keeps the dialog open", async () => {
      await openBelowListAndAsk();

      await act(async () => { fireEvent.click(accept()); });

      expect(within(dialog()).getByText(i18n.t("sales:discountReasonRequired"))).toBeInTheDocument();
      expect(vi.mocked(confirmOrder)).not.toHaveBeenCalled();
    });

    it("demands a note for Other, inline, without losing the chosen reason", async () => {
      await openBelowListAndAsk();

      fireEvent.click(reason("Other"));
      await act(async () => { fireEvent.click(accept()); });

      expect(within(dialog()).getByText(i18n.t("sales:discountReasonNoteRequired"))).toBeInTheDocument();
      expect(vi.mocked(confirmOrder)).not.toHaveBeenCalled();
      expect(reason("Other")).toBeChecked();
    });

    it("takes the other reasons with no note", async () => {
      await openBelowListAndAsk();
      vi.mocked(confirmOrder).mockResolvedValue(undefined as never);
      mockGetOrder.mockResolvedValue({ ...DRAFT_TWO, status: "Confirmed" });

      fireEvent.click(reason("Volume"));
      await act(async () => { fireEvent.click(accept()); });

      expect(vi.mocked(confirmOrder)).toHaveBeenCalledWith(
        DRAFT_TWO.id, { discountReasonCode: "Volume" }, expect.any(String));
    });

    it("confirms nothing when the dialog is dismissed", async () => {
      await openBelowListAndAsk();

      await act(async () => {
        fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
      });

      expect(vi.mocked(confirmOrder)).not.toHaveBeenCalled();
    });

    it("shows the stored reason on the confirmed order and in the Orders list", async () => {
      const confirmed: SalesOrder = {
        ...DRAFT_TWO, status: "Confirmed",
        discountReasonCode: "ManagerApproved", discountReasonNote: "signed off by Ana",
      };
      await openOrder(confirmed, /Grade A Dozen/);

      expect(screen.getByTestId("order-discount-reason")).toHaveTextContent(
        i18n.t("sales:discountReasonSummaryWithNote", {
          reason: i18n.t("enums:discountReason.ManagerApproved"),
          note: "signed off by Ana",
        }));
      expect(screen.getByTestId("row-discount-reason")).toHaveTextContent(
        i18n.t("enums:discountReason.ManagerApproved"));
    });

    it("renders the reason without a note when none was recorded", async () => {
      const confirmed: SalesOrder = {
        ...DRAFT_TWO, status: "Confirmed",
        discountReasonCode: "Volume", discountReasonNote: null,
      };
      await openOrder(confirmed, /Grade A Dozen/);

      expect(screen.getByTestId("order-discount-reason")).toHaveTextContent(
        i18n.t("sales:discountReasonSummary", {
          reason: i18n.t("enums:discountReason.Volume"),
        }));
    });

    // No backfill: an order confirmed before #721 has no reason, and the screen
    // must say nothing rather than imply the order took no discount.
    it("shows nothing for an order confirmed before the reason existed", async () => {
      await openOrder({ ...DRAFT_TWO, status: "Confirmed" }, /Grade A Dozen/);

      expect(screen.queryByTestId("order-discount-reason")).not.toBeInTheDocument();
      expect(screen.queryByTestId("row-discount-reason")).not.toBeInTheDocument();
    });
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

  it("starts a new payment session with the default method", async () => {
    // CodeRabbit: the method is as much part of the abandoned attempt as the
    // amount. Clearing three of the four fields leaves the next payment
    // preselected with a method it was never given.
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
    fireEvent.change(within(dialog()).getByLabelText(/Method/), { target: { value: "BankTransfer" } });
    fireEvent.click(within(dialog()).getByRole("button", { name: "Record payment" }));
    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    await act(async () => { resolvePay({}); });

    fireEvent.click(await screen.findByRole("button", { name: "Record payment" }));

    expect(within(dialog()).getByLabelText(/Method/)).toHaveValue("Cash");
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

// #703 PR 5 — Close remains available throughout panel writes and reads.
describe("SalesPage panel liveness (#703 PR 5)", () => {
  it.each(["write", "refresh"])("keeps panel closed after removal settles when closed during %s", async (phase) => {
    let resolveWrite!: () => void;
    let resolveRead!: (order: SalesOrder) => void;
    const write = new Promise<void>((resolve) => { resolveWrite = resolve; });
    const read = new Promise<SalesOrder>((resolve) => { resolveRead = resolve; });
    vi.mocked(removeOrderItem).mockReturnValueOnce(write);
    const row = await openOrder(DRAFT_TWO, /Grade A Dozen/);
    mockGetOrder.mockReturnValueOnce(read);
    await act(async () => {
      fireEvent.click(within(row).getByRole("button", { name: "remove" }));
    });
    expect(vi.mocked(removeOrderItem)).toHaveBeenCalledTimes(1);
    if (phase === "refresh") {
      await act(async () => { resolveWrite(); });
      expect(mockGetOrder).toHaveBeenCalledTimes(2);
    }
    const close = screen.getByRole("button", { name: i18n.t("sales:close") });
    expect(close).toBeEnabled();
    fireEvent.click(close);
    expect(document.querySelector(".order-panel")).toBeNull();
    await act(async () => {
      resolveWrite();
      resolveRead({ ...DRAFT_TWO, items: [ITEM_B] });
    });
    expect(mockGetOrder).toHaveBeenCalledTimes(2);
    expect(screen.getByRole("button", { name: "open" })).toBeEnabled();
    expect(document.querySelector(".order-panel")).toBeNull();
  });
});

// These behavioral checks retain the real dialog, pending-action and list hooks.
describe("SalesPage panel write contracts (#703 PR 5)", () => {
  function deferred<T>() {
    let resolve!: (value: T) => void;
    const promise = new Promise<T>((res) => { resolve = res; });
    return { promise, resolve };
  }

  const CONFIRMED: SalesOrder = { ...DRAFT_TWO, status: "Confirmed" };
  const closePanel = () => {
    const close = screen.getByRole("button", { name: i18n.t("sales:close") });
    expect(close).toBeEnabled();
    fireEvent.click(close);
    expect(document.querySelector(".order-panel")).toBeNull();
  };
  const openButton = () => screen.getByRole("button", { name: i18n.t("sales:open") });

  async function submit(action: "add" | "update" | "confirm" | "void" | "cancel") {
    if (action === "add") {
      await act(async () => { fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:addLine") })); });
    } else if (action === "update") {
      const row = screen.getByRole("row", { name: /Grade A Dozen/ });
      fireEvent.click(within(row).getByRole("button", { name: i18n.t("sales:edit") }));
      await act(async () => { fireEvent.click(within(row).getByRole("button", { name: i18n.t("sales:save") })); });
    } else {
      const trigger = action === "confirm" ? "confirmOrderButton" : action === "void" ? "voidOrderButton" : "cancelDraft";
      const accept = action === "confirm" ? "confirmOrderConfirmLabel" : action === "void" ? "voidOrderConfirmLabel" : "cancelDraft";
      fireEvent.click(screen.getByRole("button", { name: i18n.t(`sales:${trigger}`) }));
      // DRAFT_TWO is below list, so #721 puts the discount-reason picklist
      // between the trigger and the confirm.
      if (action === "confirm") {
        fireEvent.click(within(dialog()).getByRole(
          "radio", { name: i18n.t("enums:discountReason.Volume") }));
      }
      if (action === "void") {
        fireEvent.change(within(dialog()).getByLabelText(i18n.t("useConfirm:reasonLabel")), { target: { value: "wrong order" } });
      }
      await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: i18n.t(`sales:${accept}`) })); });
    }
  }

  for (const action of ["add", "update", "confirm", "void"] as const) {
    it.each(["write", "refresh"])(`does not reopen after ${action} settles following Close during %s`, async (phase) => {
      const write = deferred<void>();
      const read = deferred<SalesOrder>();
      const api = vi.mocked({ add: addOrderItem, update: updateOrderItem, confirm: confirmOrder, void: voidOrder }[action]);
      api.mockReturnValueOnce(write.promise as never);
      const order = action === "void" ? CONFIRMED : DRAFT_TWO;
      await openOrder(order, /Grade A Dozen/);
      const listCalls = mockListOrders.mock.calls.length;
      mockGetOrder.mockReturnValueOnce(read.promise);
      await submit(action);
      expect(api).toHaveBeenCalledTimes(1);
      expect(api.mock.calls[0][0]).toBe(order.id);
      if (phase === "refresh") {
        await act(async () => { write.resolve(); });
        expect(mockGetOrder).toHaveBeenCalledTimes(2);
      }
      expect(openButton()).toBeDisabled();
      closePanel();
      expect(openButton()).toBeDisabled();
      await act(async () => { write.resolve(); });
      expect(mockGetOrder).toHaveBeenLastCalledWith(order.id);
      expect(mockGetOrder).toHaveBeenCalledTimes(2);
      expect(openButton()).toBeDisabled();
      await act(async () => { read.resolve({ ...order, status: action === "confirm" ? "Confirmed" : action === "void" ? "Voided" : "Draft" }); });
      expect(openButton()).toBeEnabled();
      expect(document.querySelector(".order-panel")).toBeNull();
      if (action === "confirm" || action === "void") {
        expect(screen.queryByText(i18n.t(action === "confirm" ? "sales:orderConfirmed" : "sales:orderVoided", { ref: order.referenceNumber }))).not.toBeInTheDocument();
        expect(mockListOrders).toHaveBeenCalledTimes(listCalls + 1);
      }
    });
  }

  it("does not report a cancelled draft after Close but still refreshes the list", async () => {
    const write = deferred<void>();
    vi.mocked(cancelOrder).mockReturnValueOnce(write.promise);
    await openOrder(DRAFT_TWO, /Grade A Dozen/);
    const listCalls = mockListOrders.mock.calls.length;
    await submit("cancel");
    expect(vi.mocked(cancelOrder)).toHaveBeenCalledWith(DRAFT_TWO.id, expect.any(String));
    expect(openButton()).toBeDisabled();
    closePanel();
    await act(async () => { write.resolve(); });
    expect(openButton()).toBeEnabled();
    expect(document.querySelector(".order-panel")).toBeNull();
    expect(screen.queryByText(i18n.t("sales:draftOrderCancelled"))).not.toBeInTheDocument();
    expect(mockListOrders).toHaveBeenCalledTimes(listCalls + 1);
  });

  it("reports a current-panel successful cancel and closes it", async () => {
    vi.mocked(cancelOrder).mockResolvedValueOnce(undefined);
    await openOrder(DRAFT_TWO, /Grade A Dozen/);
    const listCalls = mockListOrders.mock.calls.length;
    await submit("cancel");
    expect(vi.mocked(cancelOrder)).toHaveBeenCalledWith(DRAFT_TWO.id, expect.any(String));
    expect(document.querySelector(".order-panel")).toBeNull();
    expect(screen.getByText(i18n.t("sales:draftOrderCancelled"))).toBeInTheDocument();
    expect(mockListOrders).toHaveBeenCalledTimes(listCalls + 1);
  });

  it("refreshes a live panel opened by create-order", async () => {
    await renderReady();
    await createDraft(draftEmpty(2, "USD", DRAFT_TWO.id));
    expect(screen.queryByRole("row", { name: /Grade A Dozen/ })).not.toBeInTheDocument();
    mockAddOrderItem.mockResolvedValueOnce({ orderId: DRAFT_TWO.id, itemId: ITEM_A.id });
    mockGetOrder.mockResolvedValueOnce(DRAFT_TWO);
    await submit("add");
    expect(mockAddOrderItem).toHaveBeenCalledWith(DRAFT_TWO.id, expect.objectContaining({ productId: PRODUCT_A.id }), expect.any(String));
    expect(screen.getByRole("row", { name: /Grade A Dozen/ })).toBeInTheDocument();
    expect(mockGetOrder).toHaveBeenLastCalledWith(DRAFT_TWO.id);
  });

  it.each(["add", "confirm"] as const)("keeps the order/line key when the detail refresh fails for %s", async (action) => {
    const api = vi.mocked(action === "add" ? addOrderItem : confirmOrder);
    api.mockResolvedValue(undefined as never);
    await openOrder(DRAFT_TWO, /Grade A Dozen/);
    mockGetOrder.mockRejectedValueOnce(new Error("detail read unavailable"));
    await submit(action);
    expect(screen.getByText("detail read unavailable")).toBeInTheDocument();
    expect(api).toHaveBeenCalledTimes(1);
    const first = api.mock.calls[0];
    const key = first[first.length - 1];
    expect(key).toEqual(expect.any(String));
    // Deliberately return a draft again: the fake backend lets the same
    // status request be retried so this isolates the client key policy.
    await submit(action);
    expect(api).toHaveBeenCalledTimes(2);
    expect(api.mock.calls[1]).toEqual(first);
    expect(mockGetOrder).toHaveBeenCalledTimes(3);
  });

  it("rotates the order/line key after an abandoned successful refresh", async () => {
    const write = deferred<Awaited<ReturnType<typeof addOrderItem>>>();
    mockAddOrderItem.mockReturnValueOnce(write.promise);
    await openOrder(DRAFT_TWO, /Grade A Dozen/);
    await submit("add");
    expect(mockAddOrderItem).toHaveBeenCalledTimes(1);
    const first = mockAddOrderItem.mock.calls[0];
    expect(first[2]).toEqual(expect.any(String));
    closePanel();
    await act(async () => { write.resolve({ orderId: DRAFT_TWO.id, itemId: ITEM_A.id }); });
    expect(mockGetOrder).toHaveBeenCalledTimes(2);
    expect(mockGetOrder).toHaveBeenLastCalledWith(DRAFT_TWO.id);
    expect(document.querySelector(".order-panel")).toBeNull();
    expect(openButton()).toBeEnabled();
    // The fixture deliberately remains a draft, allowing the identical add
    // request after reopening without simulating backend line merging.
    await act(async () => { fireEvent.click(openButton()); });
    mockAddOrderItem.mockResolvedValueOnce({ orderId: DRAFT_TWO.id, itemId: ITEM_A.id });
    await submit("add");
    expect(mockAddOrderItem).toHaveBeenCalledTimes(2);
    expect(mockAddOrderItem.mock.calls[1].slice(0, 2)).toEqual(first.slice(0, 2));
    expect(mockAddOrderItem.mock.calls[1][2]).not.toBe(first[2]);
    expect(mockGetOrder).toHaveBeenCalledTimes(4);
  });
});

describe("SalesPage payment panel contracts (#703 PR 5)", () => {
  const order: SalesOrder = { ...DRAFT_TWO, status: "Confirmed" };
  const ledger: Awaited<ReturnType<typeof listOrderPayments>> = {
    items: [{
      id: "pay1", salesOrderId: order.id, customerId: CUSTOMER.id, amountMinorUnits: 500,
      currencyCode: "USD", currencyMinorUnit: 2, method: "Cash", paymentDate: "2026-07-20",
      referenceNumber: "PR5 receipt", note: null, voided: false, voidReason: null, version: 3,
    }],
    paidMinorUnits: 500, outstandingMinorUnits: 2400, totalMinorUnits: 2900,
    currencyCode: "USD", currencyMinorUnit: 2,
  };

  async function openPaidOrder() {
    mockListOrderPayments.mockResolvedValue(ledger);
    await openOrder(order, /Grade A Dozen/);
    await screen.findByText("PR5 receipt");
  }

  async function submitVoidPayment() {
    fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:voidPaymentButton") }));
    fireEvent.change(within(dialog()).getByLabelText(i18n.t("useConfirm:reasonLabel")), { target: { value: "wrong order" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: i18n.t("sales:voidPaymentConfirmLabel") }));
    });
  }

  it.each(["refresh", "transport", "ApiError"] as const)("void-payment key policy after %s failure", async (failure) => {
    await openPaidOrder();
    const api = vi.mocked(voidPayment);
    api.mockResolvedValue(undefined as never);
    if (failure === "refresh") mockListOrderPayments.mockRejectedValueOnce(new Error("payment refresh failed"));
    else api.mockRejectedValueOnce(failure === "transport"
      ? new Error("payment transport failed")
      : new ApiError(409, "Conflict", "payment version changed"));
    await submitVoidPayment();
    expect(api).toHaveBeenCalledTimes(1);
    const first = api.mock.calls[0];
    expect(first).toEqual(["pay1", { version: 3, reason: "wrong order" }, expect.any(String)]);
    expect(screen.getByText(failure === "refresh" ? "payment refresh failed" : failure === "transport" ? "payment transport failed" : "payment version changed")).toBeInTheDocument();
    expect(mockListOrderPayments).toHaveBeenCalledTimes(failure === "refresh" ? 2 : 1);
    // Keep the fake ledger unvoided to retry the identical versioned request.
    await submitVoidPayment();
    expect(api).toHaveBeenCalledTimes(2);
    expect(api.mock.calls[1].slice(0, 2)).toEqual(first.slice(0, 2));
    if (failure === "transport") expect(api.mock.calls[1][2]).toBe(first[2]);
    else expect(api.mock.calls[1][2]).not.toBe(first[2]);
    expect(mockListOrderPayments).toHaveBeenCalledTimes(failure === "refresh" ? 3 : 2);
  });

  it.each(["write", "refresh"])("keeps page-owned payment success after Close during %s and reaches the late helper", async (phase) => {
    let resolveWrite!: (value: Awaited<ReturnType<typeof voidPayment>>) => void;
    let resolveRead!: (value: typeof ledger) => void;
    const write = new Promise<Awaited<ReturnType<typeof voidPayment>>>((resolve) => { resolveWrite = resolve; });
    const read = new Promise<typeof ledger>((resolve) => { resolveRead = resolve; });
    await openPaidOrder();
    vi.mocked(voidPayment).mockReturnValueOnce(write);
    mockListOrderPayments.mockReturnValueOnce(read);
    await submitVoidPayment();
    expect(vi.mocked(voidPayment)).toHaveBeenCalledWith("pay1", { version: 3, reason: "wrong order" }, expect.any(String));
    if (phase === "refresh") {
      await act(async () => { resolveWrite(undefined as never); });
      expect(mockListOrderPayments).toHaveBeenCalledTimes(2);
    }
    const close = screen.getByRole("button", { name: i18n.t("sales:close") });
    expect(close).toBeEnabled();
    fireEvent.click(close);
    expect(document.querySelector(".order-panel")).toBeNull();
    expect(screen.getByRole("button", { name: i18n.t("sales:open") })).toBeDisabled();
    await act(async () => { resolveWrite(undefined as never); });
    expect(mockListOrderPayments).toHaveBeenCalledTimes(2);
    expect(mockListOrderPayments).toHaveBeenLastCalledWith(order.id);
    await act(async () => { resolveRead({ ...ledger, items: [{ ...ledger.items[0], referenceNumber: "late receipt", voided: true }] }); });
    expect(screen.getByText(i18n.t("sales:paymentVoided"))).toBeInTheDocument();
    expect(document.querySelector(".order-panel")).toBeNull();
    expect(screen.queryByText("late receipt")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: i18n.t("sales:open") })).toBeEnabled();
    // DOM absence cannot observe a hidden setPayments overwrite: reopening
    // clears payments in the existing effect. The calls plus success prove
    // the helper completed; M7 records this observation limit separately.
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:open") })); });
    expect(mockListOrderPayments).toHaveBeenCalledTimes(3);
    expect(screen.getByText("PR5 receipt")).toBeInTheDocument();
    expect(screen.queryByText("late receipt")).not.toBeInTheDocument();
  });
});

// #703 PR 5 — dismissal owns the editor reset; a late write cannot preserve a stale draft.
it.each([false, true])("discards the line editor on Close before same-order reload (write pending: %s)", async (pending) => {
  let settle!: () => void;
  if (pending) mockUpdateOrderItem.mockReturnValueOnce(new Promise<void>((resolve) => { settle = resolve; }));
  const row = await openOrder(DRAFT_TWO, /Grade A Dozen/);
  fireEvent.click(within(row).getByRole("button", { name: "edit" }));
  if (pending) await act(async () => { fireEvent.click(within(row).getByRole("button", { name: "save" })); });
  fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:close") }));
  expect(document.querySelector(".order-panel")).toBeNull();
  if (pending) await act(async () => { settle(); });
  expect(document.querySelector(".order-panel")).toBeNull();
  // Another writer changed the line while this panel was closed. A fresh Open
  // must show that fetched row, not the dismissed editor's old quantity/price.
  mockGetOrder.mockResolvedValue({ ...DRAFT_TWO, items: [{ ...ITEM_A, quantity: 9, quantityBase: 108, unitPriceMinorUnits: 400 }, ITEM_B] });
  await act(async () => { fireEvent.click(screen.getByRole("button", { name: "open" })); });
  const reopened = screen.getByRole("row", { name: /Grade A Dozen/ });
  expect(within(reopened).queryByRole("button", { name: "save" })).not.toBeInTheDocument();
  fireEvent.click(within(reopened).getByRole("button", { name: "edit" }));
  expect(within(reopened).getByLabelText(i18n.t("sales:editQuantityAriaLabel"))).toHaveValue(9);
  expect(within(reopened).getByLabelText(i18n.t("sales:editUnitPriceAriaLabel"))).toHaveValue(4);
});

// #712 — primary Open reads respect a later panel dismissal.
describe.each(["Draft", "Confirmed"] as const)("Sales Open dismissal (#712), %s panel", (status) => {
  it.each([false, true])("stays closed after a held Open settles (different order: %s), then allows a fresh Open", async (different) => {
    const original: SalesOrder = { ...DRAFT_TWO, status };
    const target: SalesOrder = different
      ? { ...original, id: "other-order", referenceNumber: "SO-OTHER" }
      : original;
    mockListOrders.mockResolvedValue(different ? [original, target] : [original]);
    mockGetOrder.mockResolvedValue(original);
    await renderReady();
    const openRow = async (order: SalesOrder) => {
      await act(async () => {
        fireEvent.click(within(screen.getByRole("row", { name: new RegExp(order.referenceNumber) }))
          .getByRole("button", { name: i18n.t("sales:open") }));
      });
    };
    await openRow(original);
    expect(document.querySelector(".order-panel")).not.toBeNull();

    let resolveRead!: (order: SalesOrder) => void;
    mockGetOrder.mockReturnValueOnce(new Promise<SalesOrder>((resolve) => { resolveRead = resolve; }));
    await openRow(target);
    const close = screen.getByRole("button", { name: i18n.t("sales:close") });
    expect(close).toBeEnabled();
    expect(within(screen.getByRole("row", { name: new RegExp(target.referenceNumber) }))
      .getByRole("button", { name: i18n.t("sales:open") })).toBeDisabled();
    fireEvent.click(close);
    expect(document.querySelector(".order-panel")).toBeNull();
    await act(async () => { resolveRead(target); });
    expect(document.querySelector(".order-panel")).toBeNull();

    mockGetOrder.mockResolvedValue(target);
    await openRow(target);
    expect(document.querySelector(".order-panel")).not.toBeNull();
    expect(within(document.querySelector<HTMLElement>(".order-panel")!)
      .getByText(new RegExp(target.referenceNumber))).toBeInTheDocument();
  });
});

describe("Sales primary Open controls (#712)", () => {
  it("opens from no panel", async () => {
    mockListOrders.mockResolvedValue([DRAFT_TWO]);
    mockGetOrder.mockResolvedValue(DRAFT_TWO);
    await renderReady();
    expect(document.querySelector(".order-panel")).toBeNull();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:open") })); });
    expect(document.querySelector(".order-panel")).not.toBeNull();
  });

  it("shows an Open failure and allows retry", async () => {
    mockListOrders.mockResolvedValue([DRAFT_TWO]);
    mockGetOrder.mockRejectedValueOnce(new Error("Order read failed")).mockResolvedValue(DRAFT_TWO);
    await renderReady();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:open") })); });
    expect(document.querySelector(".order-panel")).toBeNull();
    expect(screen.getByText("Order read failed")).toBeInTheDocument();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:open") })); });
    expect(document.querySelector(".order-panel")).not.toBeNull();
    expect(screen.queryByText("Order read failed")).not.toBeInTheDocument();
  });

  it("keeps a dismissed Open failure on the page and the panel closed", async () => {
    await openOrder(DRAFT_TWO, /Grade A Dozen/);
    let rejectRead!: (reason: Error) => void;
    mockGetOrder.mockReturnValueOnce(new Promise<SalesOrder>((_, reject) => { rejectRead = reject; }));
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:open") })); });
    fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:close") }));
    await act(async () => { rejectRead(new Error("Dismissed order read failed")); });
    expect(document.querySelector(".order-panel")).toBeNull();
    expect(screen.getByText("Dismissed order read failed")).toBeInTheDocument();
  });
});

// #713 — reconcile the live editor whenever accepted order data changes.
describe("Sales live editor (#713)", () => {
  const changed = (): SalesOrder => ({ ...DRAFT_TWO, items: [
    { ...ITEM_A, quantity: 9, quantityBase: 108, unitPriceMinorUnits: 400 }, ITEM_B,
  ] });
  const beginEdit = async () => {
    const row = await openOrder(DRAFT_TWO, /Grade A Dozen/);
    fireEvent.click(within(row).getByRole("button", { name: i18n.t("sales:edit") }));
  };
  const refresh = async (order: SalesOrder) => {
    mockGetOrder.mockResolvedValue(order);
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:open") })); });
  };
  const quantity = () => screen.getByLabelText(i18n.t("sales:editQuantityAriaLabel"));
  const price = () => screen.getByLabelText(i18n.t("sales:editUnitPriceAriaLabel"));
  const save = () => screen.getByRole("button", { name: i18n.t("sales:save") });

  it.each(["Confirmed", "Cancelled", "Voided", "UnknownFutureStatus"])("%s refresh discards the editor, including if Draft data later returns", async (status) => {
    await beginEdit();
    await refresh({ ...DRAFT_TWO, status });
    expect(screen.queryByRole("button", { name: i18n.t("sales:save") })).not.toBeInTheDocument();
    await refresh(DRAFT_TWO);
    expect(screen.queryByRole("button", { name: i18n.t("sales:save") })).not.toBeInTheDocument();
    expect(mockUpdateOrderItem).not.toHaveBeenCalled();
  });

  it("Confirm through the dialog ends editing after the follow-up read", async () => {
    await beginEdit();
    mockGetOrder.mockResolvedValue({ ...DRAFT_TWO, status: "Confirmed" });
    vi.mocked(confirmOrder).mockResolvedValue(undefined as never);
    fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:confirmOrderButton") }));
    fireEvent.click(within(dialog()).getByRole(
      "radio", { name: i18n.t("enums:discountReason.Volume") }));
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: i18n.t("sales:confirmOrderConfirmLabel") })); });
    expect(confirmOrder).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("button", { name: i18n.t("sales:save") })).not.toBeInTheDocument();
  });

  it("a removed line cannot regain its discarded editor when it reappears", async () => {
    await beginEdit();
    await refresh({ ...DRAFT_TWO, items: [ITEM_B] });
    expect(screen.queryByRole("button", { name: i18n.t("sales:save") })).not.toBeInTheDocument();
    await refresh(DRAFT_TWO);
    expect(screen.queryByRole("button", { name: i18n.t("sales:save") })).not.toBeInTheDocument();
  });

  it("switching orders and back discards the old editor even with a reused item ID", async () => {
    const other = { ...DRAFT_TWO, id: "other", referenceNumber: "SO-OTHER" };
    mockListOrders.mockResolvedValue([DRAFT_TWO, other]);
    mockGetOrder.mockImplementation(async (id) => id === other.id ? other : DRAFT_TWO);
    await renderReady();
    const open = async (reference: string) => {
      await act(async () => { fireEvent.click(within(screen.getByRole("row", { name: new RegExp(reference) }))
        .getByRole("button", { name: i18n.t("sales:open") })); });
    };
    await open(DRAFT_TWO.referenceNumber);
    fireEvent.click(within(screen.getByRole("row", { name: /Grade A Dozen/ })).getByRole("button", { name: i18n.t("sales:edit") }));
    await open(other.referenceNumber);
    expect(screen.queryByRole("button", { name: i18n.t("sales:save") })).not.toBeInTheDocument();
    await open(DRAFT_TWO.referenceNumber);
    expect(screen.queryByRole("button", { name: i18n.t("sales:save") })).not.toBeInTheDocument();
  });

  it.each([false, true])("clean editor follows fresh server values (changed: %s) and can Save", async (isChanged) => {
    await beginEdit();
    const fresh = isChanged ? changed() : DRAFT_TWO;
    await refresh(fresh);
    expect(quantity()).toHaveValue(fresh.items[0].quantity);
    expect(price()).toHaveValue(fresh.items[0].unitPriceMinorUnits / 100);
    expect(save()).toBeEnabled();
    await act(async () => { fireEvent.click(save()); });
    expect(mockUpdateOrderItem).toHaveBeenCalledWith(DRAFT_TWO.id, ITEM_A.id,
      { quantity: fresh.items[0].quantity, unitPriceMinorUnits: fresh.items[0].unitPriceMinorUnits }, expect.any(String));
  });

  it("unchanged server values preserve both dirty fields and Save their values", async () => {
    await beginEdit();
    fireEvent.change(quantity(), { target: { value: "5" } });
    fireEvent.change(price(), { target: { value: "6.25" } });
    await refresh(DRAFT_TWO);
    expect(quantity()).toHaveValue(5);
    expect(price()).toHaveValue(6.25);
    expect(save()).toBeEnabled();
    await act(async () => { fireEvent.click(save()); });
    expect(mockUpdateOrderItem).toHaveBeenCalledWith(DRAFT_TWO.id, ITEM_A.id,
      { quantity: 5, unitPriceMinorUnits: 625 }, expect.any(String));
  });

  it.each(["quantity", "price"] as const)("a server %s conflict preserves both inputs until Reload, then Save uses the new baseline", async (field) => {
    await beginEdit();
    fireEvent.change(quantity(), { target: { value: "5" } });
    const fresh = { ...DRAFT_TWO, items: [{ ...ITEM_A,
      ...(field === "quantity" ? { quantity: 9, quantityBase: 108 } : { unitPriceMinorUnits: 400 }),
    }, ITEM_B] };
    await refresh(fresh);
    expect(quantity()).toHaveValue(5);
    expect(price()).toHaveValue(3);
    expect(save()).toBeDisabled();
    fireEvent.click(save());
    expect(mockUpdateOrderItem).not.toHaveBeenCalled();
    expect(screen.getByText(i18n.t("sales:editConflict"))).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:reloadLine") }));
    expect(quantity()).toHaveValue(fresh.items[0].quantity);
    expect(price()).toHaveValue(fresh.items[0].unitPriceMinorUnits / 100);
    expect(save()).toBeEnabled();
    expect(screen.queryByText(i18n.t("sales:editConflict"))).not.toBeInTheDocument();
    await refresh(fresh);
    expect(save()).toBeEnabled();
    await act(async () => { fireEvent.click(save()); });
    expect(mockUpdateOrderItem).toHaveBeenCalledWith(DRAFT_TWO.id, ITEM_A.id,
      { quantity: fresh.items[0].quantity, unitPriceMinorUnits: fresh.items[0].unitPriceMinorUnits }, expect.any(String));
  });

  it("blank price input is retained on conflict", async () => {
    await beginEdit();
    fireEvent.change(price(), { target: { value: "" } });
    await refresh(changed());
    expect(quantity()).toHaveValue(3);
    expect(price()).toHaveValue(null);
    expect(save()).toBeDisabled();
  });

  it.each([false, true])("a pending read respects later editor cancellation (cancel: %s)", async (cancel) => {
    await beginEdit();
    let settle!: (order: SalesOrder) => void;
    mockGetOrder.mockReturnValueOnce(new Promise<SalesOrder>((resolve) => { settle = resolve; }));
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:open") })); });
    if (cancel) fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:cancelEdit") }));
    else fireEvent.change(quantity(), { target: { value: "5" } });
    await act(async () => { settle(changed()); });
    if (cancel) expect(screen.queryByRole("button", { name: i18n.t("sales:save") })).not.toBeInTheDocument();
    else {
      expect(quantity()).toHaveValue(5);
      expect(price()).toHaveValue(3);
      expect(save()).toBeDisabled();
    }
  });

  it("a failed refetch retains input and a later retry detects the conflict", async () => {
    await beginEdit();
    fireEvent.change(quantity(), { target: { value: "5" } });
    mockGetOrder.mockRejectedValueOnce(new Error("Refresh unavailable"));
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:open") })); });
    expect(quantity()).toHaveValue(5);
    expect(save()).toBeEnabled();
    expect(screen.getByText("Refresh unavailable")).toBeInTheDocument();
    await refresh(changed());
    expect(quantity()).toHaveValue(5);
    expect(save()).toBeDisabled();
  });
});

it("#713 editor quantity steppers apply consecutive functional updates", async () => {
  const row = await openOrder(DRAFT_TWO, /Grade A Dozen/);
  fireEvent.click(within(row).getByRole("button", { name: i18n.t("sales:edit") }));
  const increase = within(row).getByRole("button", { name: i18n.t("numberField:increaseLabel", { label: i18n.t("sales:editQuantityAriaLabel").toLowerCase() }) });
  fireEvent.click(increase);
  fireEvent.click(increase);
  expect(screen.getByLabelText(i18n.t("sales:editQuantityAriaLabel"))).toHaveValue(5);
  await act(async () => { fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:save") })); });
  expect(mockUpdateOrderItem).toHaveBeenCalledWith(DRAFT_TWO.id, ITEM_A.id,
    { quantity: 5, unitPriceMinorUnits: 300 }, expect.any(String));
});

it("#713 clean refresh seeds from the incoming order currency scale", async () => {
  const row = await openOrder(DRAFT_TWO, /Grade A Dozen/);
  fireEvent.click(within(row).getByRole("button", { name: i18n.t("sales:edit") }));
  const fresh = { ...DRAFT_TWO, currencyCode: "JPY", currencyMinorUnit: 0,
    items: [{ ...ITEM_A, unitPriceMinorUnits: 400, currencyCode: "JPY", currencyMinorUnit: 0 }, ITEM_B] };
  mockGetOrder.mockResolvedValue(fresh);
  await act(async () => { fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:open") })); });
  expect(screen.getByLabelText(i18n.t("sales:editUnitPriceAriaLabel"))).toHaveValue(400);
  await act(async () => { fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:save") })); });
  expect(mockUpdateOrderItem).toHaveBeenCalledWith(DRAFT_TWO.id, ITEM_A.id,
    { quantity: 3, unitPriceMinorUnits: 400 }, expect.any(String));
});

// #713 review — retry the same line-update payload, but give changed intent a new key.
describe("Sales line-update retry intent (#713)", () => {
  beforeEach(() => { mockUpdateOrderItem.mockReset().mockResolvedValue(undefined); });
  const edit = () => fireEvent.click(within(screen.getByRole("row", { name: /Grade A Dozen/ }))
    .getByRole("button", { name: i18n.t("sales:edit") }));
  const begin = async () => { await openOrder(DRAFT_TWO, /Grade A Dozen/); edit(); };
  const quantity = (value: string) => fireEvent.change(screen.getByLabelText(i18n.t("sales:editQuantityAriaLabel")), { target: { value } });
  const price = (value: string) => fireEvent.change(screen.getByLabelText(i18n.t("sales:editUnitPriceAriaLabel")), { target: { value } });
  const save = async () => { await act(async () => { fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:save") })); }); };
  const key = (call: number) => mockUpdateOrderItem.mock.calls[call][3];

  it("Reload after a lost success response gives the changed payload a new key", async () => {
    await begin();
    quantity("5");
    mockUpdateOrderItem.mockRejectedValueOnce(new Error("Successful response lost"));
    await save();
    mockGetOrder.mockResolvedValue({ ...DRAFT_TWO, items: [{ ...ITEM_A, quantity: 9, quantityBase: 108 }, ITEM_B] });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:open") })); });
    expect(screen.getByRole("button", { name: i18n.t("sales:save") })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:reloadLine") }));
    await save();
    expect(mockUpdateOrderItem.mock.calls.map((call) => call[2].quantity)).toEqual([5, 9]);
    expect(key(1)).not.toBe(key(0));
  });

  it.each(["quantity", "price"])("changed %s gets a fresh key, including when returning to an earlier payload", async (field) => {
    await begin();
    mockUpdateOrderItem.mockRejectedValue(new Error("Response unavailable"));
    await save();
    if (field === "quantity") quantity("5"); else price("4.00");
    await save();
    if (field === "quantity") quantity("3"); else price("3.00");
    await save();
    expect(mockUpdateOrderItem).toHaveBeenCalledTimes(3);
    expect(mockUpdateOrderItem.mock.calls[1][2]).not.toEqual(mockUpdateOrderItem.mock.calls[0][2]);
    expect(mockUpdateOrderItem.mock.calls[2][2]).toEqual(mockUpdateOrderItem.mock.calls[0][2]);
    expect(new Set([key(0), key(1), key(2)]).size).toBe(3);
  });

  it("an identical retry retains its key even after raw price formatting changes", async () => {
    await begin();
    quantity("5");
    price("3.0");
    mockUpdateOrderItem.mockRejectedValueOnce(new Error("Response unavailable"));
    await save();
    price("3.00");
    await save();
    expect(mockUpdateOrderItem).toHaveBeenCalledTimes(2);
    expect(mockUpdateOrderItem.mock.calls[1][2]).toEqual(mockUpdateOrderItem.mock.calls[0][2]);
    expect(key(1)).toBe(key(0));
  });

  it.each([false, true])("a failed follow-up read retains the key only for the identical payload (changed: %s)", async (changed) => {
    await begin();
    quantity("5");
    mockGetOrder.mockRejectedValueOnce(new Error("Refresh unavailable"));
    await save();
    edit();
    quantity(changed ? "6" : "5");
    await save();
    expect(mockUpdateOrderItem).toHaveBeenCalledTimes(2);
    expect(mockUpdateOrderItem.mock.calls[1][2].quantity).toBe(changed ? 6 : 5);
    if (changed) expect(key(1)).not.toBe(key(0)); else expect(key(1)).toBe(key(0));
  });

  it("a completed write and refresh release the key for the next intent", async () => {
    await begin();
    await save();
    edit();
    await save();
    expect(mockUpdateOrderItem).toHaveBeenCalledTimes(2);
    expect(mockUpdateOrderItem.mock.calls[1][2]).toEqual(mockUpdateOrderItem.mock.calls[0][2]);
    expect(key(1)).not.toBe(key(0));
  });

  it("cancelling and reopening the editor retains an identical ambiguous retry", async () => {
    await begin();
    quantity("5");
    mockUpdateOrderItem.mockRejectedValueOnce(new Error("Response unavailable"));
    await save();
    fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:cancelEdit") }));
    edit();
    quantity("5");
    await save();
    expect(mockUpdateOrderItem).toHaveBeenCalledTimes(2);
    expect(key(1)).toBe(key(0));
  });
});

// #713 review — accepting fresh server values begins a new retry intent.
describe("Sales accepted edit baseline (#713)", () => {
  beforeEach(() => { mockUpdateOrderItem.mockReset().mockResolvedValue(undefined); });
  it.each([
    ["reload", "quantity"], ["reload", "price"], ["reopen", "quantity"],
    ["clean refresh", "quantity"], ["baseline round trip", "quantity"],
  ] as const)("%s lets an earlier %s value be applied again instead of replaying old success", async (path, field) => {
    let server = DRAFT_TWO;
    const completed = new Set<string>();
    let loseResponse = true;
    const row = await openOrder(server, /Grade A Dozen/);
    mockGetOrder.mockImplementation(async () => server);
    mockUpdateOrderItem.mockImplementation(async (_orderId, _itemId, payload, key) => {
      if (!key) throw new Error("Missing request key");
      // A completed idempotency key replays success without another mutation.
      if (completed.has(key)) return;
      completed.add(key);
      server = { ...server, items: [{ ...ITEM_A, quantity: payload.quantity,
        quantityBase: payload.quantity * ITEM_A.baseUnitFactor,
        unitPriceMinorUnits: payload.unitPriceMinorUnits }, ITEM_B] };
      if (loseResponse) { loseResponse = false; throw new Error("Successful response lost"); }
    });
    const edit = () => fireEvent.click(within(screen.getByRole("row", { name: /Grade A Dozen/ }))
      .getByRole("button", { name: i18n.t("sales:edit") }));
    const type = (value: string) => fireEvent.change(screen.getByLabelText(i18n.t(field === "quantity"
      ? "sales:editQuantityAriaLabel" : "sales:editUnitPriceAriaLabel")), { target: { value } });
    const save = async () => { await act(async () => { fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:save") })); }); };
    const refresh = async () => { await act(async () => { fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:open") })); }); };
    fireEvent.click(within(row).getByRole("button", { name: i18n.t("sales:edit") }));
    const desired = path === "clean refresh" ? "3" : field === "quantity" ? "5" : "4.00";
    if (path !== "clean refresh") type(desired);
    await save();
    expect(completed.size).toBe(1);
    server = { ...server, items: [{ ...server.items[0],
      ...(field === "quantity" ? { quantity: 9, quantityBase: 108 } : { unitPriceMinorUnits: 900 }),
    }, ITEM_B] };
    await refresh();
    if (path === "reopen") {
      fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:cancelEdit") }));
      edit();
    } else if (path !== "clean refresh") {
      expect(screen.getByRole("button", { name: i18n.t("sales:save") })).toBeDisabled();
      fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:reloadLine") }));
    }
    if (path === "baseline round trip") { server = DRAFT_TWO; await refresh(); }
    type(desired);
    await save();
    expect(mockUpdateOrderItem).toHaveBeenCalledTimes(2);
    expect(mockUpdateOrderItem.mock.calls[1][2]).toEqual(mockUpdateOrderItem.mock.calls[0][2]);
    expect(screen.queryByRole("button", { name: i18n.t("sales:save") })).not.toBeInTheDocument();
    expect(field === "quantity" ? server.items[0].quantity : server.items[0].unitPriceMinorUnits)
      .toBe(field === "quantity" ? Number(desired) : 400);
    expect(completed.size).toBe(2);
  });

  it("unchanged accepted values and continued typing preserve an identical ambiguous retry", async () => {
    const row = await openOrder(DRAFT_TWO, /Grade A Dozen/);
    fireEvent.click(within(row).getByRole("button", { name: i18n.t("sales:edit") }));
    const type = (value: string) => fireEvent.change(screen.getByLabelText(i18n.t("sales:editQuantityAriaLabel")), { target: { value } });
    const save = async () => { await act(async () => { fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:save") })); }); };
    type("5");
    mockUpdateOrderItem.mockRejectedValueOnce(new Error("Response unavailable"));
    await save();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: i18n.t("sales:open") })); });
    type("6");
    type("5");
    await save();
    expect(mockUpdateOrderItem).toHaveBeenCalledTimes(2);
    expect(mockUpdateOrderItem.mock.calls[1][3]).toBe(mockUpdateOrderItem.mock.calls[0][3]);
  });
});

// #752 — the discount treatment under inline edit, and the rounding boundary.
describe("SalesPage discount markers under inline edit (#752)", () => {
  // Cell order inside an editing row: product, quantity, eggs, list, price,
  // DISCOUNT, line total, actions.
  const DISCOUNT_CELL = 5;

  const beginEdit = async () => {
    const row = await openOrder(DRAFT_TWO, /Grade A Dozen/);
    fireEvent.click(within(row).getByRole("button", { name: i18n.t("sales:edit") }));
    return row;
  };
  const typePrice = (value: string) =>
    fireEvent.change(screen.getByLabelText(i18n.t("sales:editUnitPriceAriaLabel")), { target: { value } });

  it("keeps the tint, the chip and the Discount cell agreeing as the price is edited", async () => {
    const row = await beginEdit();
    // ITEM_A: 3 x $3.00 against a $3.75 list — below list before a key is pressed.
    expect(row).toHaveClass("discounted");
    expect(within(row).getByText(i18n.t("sales:belowListBadge"))).toBeInTheDocument();
    expect(within(row).getAllByRole("cell")[DISCOUNT_CELL]).toHaveTextContent("$2.25");

    // Typed UP to the list price: no longer a discount, so every marker goes.
    typePrice("3.75");
    expect(row).not.toHaveClass("discounted");
    expect(within(row).queryByText(i18n.t("sales:belowListBadge"))).toBeNull();
    expect(within(row).getAllByRole("cell")[DISCOUNT_CELL]).toHaveTextContent("—");

    // Typed ABOVE list: the Discount cell is this line's only marker, and it
    // used to be blanked to "—" for the whole edit.
    typePrice("4.00");
    expect(within(row).getAllByRole("cell")[DISCOUNT_CELL]).toHaveTextContent(i18n.t("sales:aboveList"));
    expect(row).not.toHaveClass("discounted");

    // Back below list: the markers come back rather than sticking.
    typePrice("2.00");
    expect(row).toHaveClass("discounted");
    expect(within(row).getByText(i18n.t("sales:belowListBadge"))).toBeInTheDocument();
  });

  it("falls back to the saved line rather than flickering when the price box is unparseable", async () => {
    const row = await beginEdit();
    typePrice("");
    expect(row).toHaveClass("discounted");
    expect(within(row).getAllByRole("cell")[DISCOUNT_CELL]).not.toHaveTextContent(i18n.t("sales:noListPrice"));
  });

  it("says <0.1% rather than 0.0% when a real discount rounds below the rendered precision", async () => {
    // 1 minor unit off a 1,000,000 list is 0.0001% — non-zero, and "0.0%"
    // beside a non-zero amount contradicts itself.
    const tiny: OrderItem = {
      ...ITEM_A, id: "tiny", quantity: 1, quantityBase: 12,
      unitPriceMinorUnits: 999_999, listUnitPriceMinorUnits: 1_000_000,
    };
    const order: SalesOrder = {
      ...draftEmpty(2, "USD", "otiny"), referenceNumber: "SO-tiny",
      totalMinorUnits: 999_999, items: [tiny],
    };
    const row = await openOrder(order, /SO-tiny/);
    // By content, not column index: a non-editing row has a different column
    // count from the editing row DISCOUNT_CELL is measured against.
    const cell = within(row).getByText(/·/).closest("td");
    expect(cell).toHaveTextContent("$0.01");
    expect(cell).toHaveTextContent("<0.1%");
    expect(cell).not.toHaveTextContent("0.0%");

    const panel = screen.getByTestId("order-discount");
    expect(panel).toHaveTextContent("<0.1%");
    expect(panel).not.toHaveTextContent("0.0%");
  });
});

// #727 — the per-farm discount ceiling on the Sales screen.
//
// ITEM_A is list 375 / unit 300, which is EXACTLY 20% off. ITEM_B is list 1200 /
// unit 1000, which is 16.67%. So a ceiling of 20 puts A precisely on the
// boundary (allowed), 19 puts it one step past (refused) and B under either —
// which is how these tests get a one-line breach and a boundary case out of the
// fixtures the rest of the file already uses.
describe("SalesPage discount ceiling (#727)", () => {
  async function openWithCeiling(percent: number | null, order: SalesOrder = DRAFT_TWO) {
    mockListOrders.mockResolvedValue([order]);
    mockGetOrder.mockResolvedValue(order);
    renderWithProviders(<SalesPage />, {
      token: ADMIN,
      farm: account({ yourMaxDiscountPercent: percent }),
    });
    await screen.findByRole("button", { name: "New order" });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "open" })); });
    return screen.findByRole("row", { name: /Grade A Dozen/ });
  }

  const notice = () => screen.queryByTestId("discount-ceiling-notice");
  const blocked = () => screen.queryByTestId("order-ceiling-warning");
  const confirmButton = () => screen.getByRole("button", { name: /Confirm order/ });

  it("says nothing at all when the caller is bound by no ceiling", async () => {
    await openWithCeiling(null);
    expect(notice()).not.toBeInTheDocument();
    expect(blocked()).not.toBeInTheDocument();
    expect(screen.queryByText("Over maximum")).not.toBeInTheDocument();
    expect(confirmButton()).toBeEnabled();
  });

  it("states the ceiling persistently whenever one binds this caller, breach or not", async () => {
    await openWithCeiling(25);
    expect(notice()).toHaveTextContent(i18n.t("sales:discountCeilingNotice", { percent: "25" }));
    expect(blocked()).not.toBeInTheDocument();
    expect(confirmButton()).toBeEnabled();
  });

  it("allows a line sitting exactly on the ceiling", async () => {
    const rowA = await openWithCeiling(20);
    expect(within(rowA).queryByText("Over maximum")).not.toBeInTheDocument();
    expect(blocked()).not.toBeInTheDocument();
    expect(confirmButton()).toBeEnabled();
  });

  it("marks only the breaching line, one step past the boundary", async () => {
    const rowA = await openWithCeiling(19);
    const rowB = screen.getByRole("row", { name: /Grade B Tray/ });

    expect(within(rowA).getByText("Over maximum")).toBeInTheDocument();
    expect(within(rowB).queryByText("Over maximum")).not.toBeInTheDocument();
    // Beside the below-list chip, not instead of it: the two say different things.
    expect(within(rowA).getByText("Below list")).toBeInTheDocument();
  });

  it("warns when a line breaches, and leaves Confirm usable", async () => {
    await openWithCeiling(19);
    // Advisory, NOT a gate. This number is fetched once per session and can be
    // a stale lower ceiling, or rounded for a list price past 2^53 — so
    // disabling Confirm here stopped the server's authoritative check from
    // running and left the seller no way to discover why.
    expect(confirmButton()).toBeEnabled();
    expect(blocked()).toHaveTextContent(i18n.t("sales:discountCeilingWarning", { percent: "19" }));
  });

  it("still asks for a discount reason and posts, so the server decides", async () => {
    await openWithCeiling(19);
    await act(async () => { fireEvent.click(confirmButton()); });

    // The reason dialog opens for a line the client believes is over the
    // ceiling. That is the accepted cost of not gating: the client may be
    // wrong, and only the POST can settle it.
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });

  it("treats a ceiling of 0 as a ceiling, not as an absence", async () => {
    const rowA = await openWithCeiling(0);
    expect(notice()).toHaveTextContent(i18n.t("sales:discountCeilingNotice", { percent: "0" }));
    expect(within(rowA).getByText("Over maximum")).toBeInTheDocument();
    // A ceiling of 0 still WARNS, like any other ceiling. It is the marking
    // that proves 0 is read as a ceiling rather than as an absence; Confirm
    // stays usable here for the same reason it does at 19.
    expect(blocked()).toBeInTheDocument();
  });

  it("marks a row live against the price being typed, before it is saved", async () => {
    const rowA = await openWithCeiling(25);
    expect(within(rowA).queryByText("Over maximum")).not.toBeInTheDocument();

    fireEvent.click(within(rowA).getByRole("button", { name: "edit" }));
    // 2.00 against a 3.75 list is 46.7% off, well past the 25% ceiling.
    fireEvent.change(screen.getByLabelText("Edit unit price"), { target: { value: "2.00" } });

    expect(screen.getByRole("row", { name: /Grade A Dozen/ })).toHaveTextContent("Over maximum");
  });

  it("leaves Confirm enabled while an unsaved edit breaches, because Confirm posts the saved line", async () => {
    const rowA = await openWithCeiling(25);
    fireEvent.click(within(rowA).getByRole("button", { name: "edit" }));
    fireEvent.change(screen.getByLabelText("Edit unit price"), { target: { value: "2.00" } });

    expect(screen.getByRole("row", { name: /Grade A Dozen/ })).toHaveTextContent("Over maximum");
    expect(confirmButton()).toBeEnabled();
    expect(blocked()).not.toBeInTheDocument();
  });

  it("never marks a line that has no comparable list price", async () => {
    const noList: SalesOrder = {
      ...DRAFT_TWO,
      items: [{ ...ITEM_A, listUnitPriceMinorUnits: null }],
      totalMinorUnits: 900,
    };
    const row = await openWithCeiling(0, noList);

    expect(within(row).queryByText("Over maximum")).not.toBeInTheDocument();
    expect(blocked()).not.toBeInTheDocument();
    expect(confirmButton()).toBeEnabled();
  });

  it("reads its strings from the sales catalog, not hardcoded literals", async () => {
    const original = i18n.getResource("en", "sales", "overMaximumBadge") as string;
    i18n.addResource("en", "sales", "overMaximumBadge", "BADGE-MARKER");
    try {
      const rowA = await openWithCeiling(19);
      expect(within(rowA).getByText("BADGE-MARKER")).toBeInTheDocument();
    } finally {
      i18n.addResource("en", "sales", "overMaximumBadge", original);
    }
  });
});
