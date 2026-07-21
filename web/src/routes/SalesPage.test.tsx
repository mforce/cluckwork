import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act } from "@testing-library/react";
import { SalesPage } from "./SalesPage";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  addOrderItem, createOrder, getOrder, listCustomers, listEggGrades,
  listOrderPayments, listOrders, listProducts, updateOrderItem,
} from "../api/cluckwork";
import type { Customer, EggGrade, OrderItem, Product, SalesOrder } from "../api/cluckwork";

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
  };
});

const mockListCustomers = vi.mocked(listCustomers);
const mockListProducts = vi.mocked(listProducts);
const mockListEggGrades = vi.mocked(listEggGrades);
const mockListOrders = vi.mocked(listOrders);
const mockListOrderPayments = vi.mocked(listOrderPayments);
const mockCreateOrder = vi.mocked(createOrder);
const mockGetOrder = vi.mocked(getOrder);
const mockAddOrderItem = vi.mocked(addOrderItem);
const mockUpdateOrderItem = vi.mocked(updateOrderItem);

const CUSTOMER: Customer = { id: "c1", name: "Acme Eggs", phone: "555", email: null, address: null, note: null };
// Only gr1 is saleable → the picker offers PRODUCT_A only; gr2/PRODUCT_B exists
// solely to resolve the second line's display name (allProducts).
const GRADE: EggGrade = { id: "gr1", farmId: "farm1", name: "Grade A", gradeType: "Size", sortOrder: 1, isSaleable: true, active: true };
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
    id, customerId: "c1", referenceNumber: "SO-1", orderDate: "2026-07-20",
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

// role irrelevant to add/update/display (Admin only unlocks void + payments,
// which these tests don't touch) — just a stable authenticated session.
const ADMIN = { sub: "u1", role: "Admin" };

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  mockListCustomers.mockResolvedValue([CUSTOMER]);
  mockListProducts.mockResolvedValue([PRODUCT_A, PRODUCT_B]);
  mockListEggGrades.mockResolvedValue([GRADE]);
  mockListOrders.mockResolvedValue([]);
  mockListOrderPayments.mockResolvedValue({
    items: [], paidMinorUnits: 0, outstandingMinorUnits: 0, totalMinorUnits: 0,
    currencyCode: "USD", currencyMinorUnit: 2,
  });
});

// The create form only appears once customers + the (initially empty) order list
// have loaded; wait on it so the mount effects have settled.
async function renderReady() {
  renderWithProviders(<SalesPage />, { token: ADMIN });
  await screen.findByRole("button", { name: "New draft order" });
}

async function createDraft(order: SalesOrder) {
  mockCreateOrder.mockResolvedValue({ id: order.id });
  mockGetOrder.mockResolvedValue(order);
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "New draft order" }));
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

describe("SalesPage line display", () => {
  it("shows per-line base eggs and money, with the order total distinct from any single line", async () => {
    const rowA = await openOrder(DRAFT_TWO, /Grade A Dozen/);

    // baseUnitFactor > 1 → the "(N eggs)" note; quantityBase in the Eggs column
    expect(within(rowA).getByText(/per dozen \(12 eggs\)/)).toBeInTheDocument();
    expect(within(rowA).getByText("36")).toBeInTheDocument();
    // line total = unitPrice × quantity (300 × 3), NOT the order total
    expect(within(rowA).getByText("3.00 USD")).toBeInTheDocument();
    expect(within(rowA).getByText("9.00 USD")).toBeInTheDocument();

    const rowB = screen.getByRole("row", { name: /Grade B Tray/ });
    expect(within(rowB).getByText("60")).toBeInTheDocument();
    expect(within(rowB).getByText("20.00 USD")).toBeInTheDocument(); // 1000 × 2

    // order total (2900) differs from both line totals (900, 2000) → this pins
    // that the line cell renders its own line, not active.totalMinorUnits
    expect(screen.getByText(/Total: 29\.00 USD/)).toBeInTheDocument();
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
    // 1500 minor units @ 3 decimals → "1.500 BHD" (would read "15.00" at 2dp) —
    // proves formatMoney uses the item's currencyMinorUnit, not a hard-coded 2.
    const row = await openOrder(draftWithItem(3, "BHD", 1500, "o4"), /Grade A Dozen/);
    expect(within(row).getByText("1.500 BHD")).toBeInTheDocument(); // unit price
    expect(within(row).getByText("4.500 BHD")).toBeInTheDocument(); // line total 1500 × 3
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
