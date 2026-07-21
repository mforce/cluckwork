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
// via importOriginal; stub only the network seam. The screen also uses useAuth
// and the router, so it renders through renderWithProviders.
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
const GRADE: EggGrade = { id: "gr1", farmId: "farm1", name: "Grade A", gradeType: "Size", sortOrder: 1, isSaleable: true, active: true };
// USD-like: 2 minor-unit decimals. Default price 300 → the mount effect prefills "3.00".
const PRODUCT: Product = {
  id: "p1", name: "Grade A Dozen", productType: "Egg", defaultUnit: "Dozen",
  defaultPriceMinorUnits: 300, currencyCode: "USD", currencyMinorUnit: 2,
  eggGradeId: "gr1", notes: null, active: true, version: 1,
};

const DRAFT_EMPTY: SalesOrder = {
  id: "o1", customerId: "c1", referenceNumber: "SO-1", orderDate: "2026-07-20",
  status: "Draft", totalMinorUnits: 0, currencyCode: "USD", currencyMinorUnit: 2,
  voidReason: null, items: [],
};

// 3 dozen × 12 = 36 base eggs; unit price 300 (3.00) → line total 900 (9.00).
const DOZEN_ITEM: OrderItem = {
  id: "it1", productId: "p1", eggGradeId: "gr1", unit: "Dozen", baseUnitFactor: 12,
  quantity: 3, quantityBase: 36, unitPriceMinorUnits: 300, currencyCode: "USD", currencyMinorUnit: 2,
};
const DRAFT_WITH_ITEM: SalesOrder = {
  ...DRAFT_EMPTY, id: "o2", referenceNumber: "SO-2", totalMinorUnits: 900, items: [DOZEN_ITEM],
};

// role irrelevant to the add/update/display paths (Admin only unlocks void +
// payments, which these tests don't touch) — a stable authenticated session.
const ADMIN = { sub: "u1", role: "Admin" };

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  mockListCustomers.mockResolvedValue([CUSTOMER]);
  mockListProducts.mockResolvedValue([PRODUCT]);
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

async function openOrder(order: SalesOrder) {
  mockListOrders.mockResolvedValue([order]);
  mockGetOrder.mockResolvedValue(order);
  await renderReady();
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "open" }));
  });
  return screen.findByRole("row", { name: /Grade A Dozen/ });
}

describe("SalesPage line display", () => {
  it("renders base-egg count and money through the real formatMoney/quantityBase", async () => {
    const row = await openOrder(DRAFT_WITH_ITEM);

    // baseUnitFactor > 1 → the "(N eggs)" note next to the unit
    expect(within(row).getByText(/per dozen \(12 eggs\)/)).toBeInTheDocument();
    // quantityBase (3 dozen × 12) shown in the Eggs column
    expect(within(row).getByText("36")).toBeInTheDocument();
    // unit price 300 → "3.00 USD"; line total 300 × 3 → "9.00 USD"
    expect(within(row).getByText("3.00 USD")).toBeInTheDocument();
    expect(within(row).getByText("9.00 USD")).toBeInTheDocument();
    // order total via formatMoney(active.totalMinorUnits)
    expect(screen.getByText(/Total: 9\.00 USD/)).toBeInTheDocument();
  });

  it("omits the egg-multiplier note and shows eggs === quantity for a per-egg line (factor 1)", async () => {
    const eggItem: OrderItem = { ...DOZEN_ITEM, id: "it2", unit: "Egg", baseUnitFactor: 1, quantity: 30, quantityBase: 30 };
    const order: SalesOrder = { ...DRAFT_WITH_ITEM, id: "o3", items: [eggItem], totalMinorUnits: 9000 };
    const row = await openOrder(order);

    expect(within(row).getByText(/per egg/)).toBeInTheDocument();
    // no "(… eggs)" suffix at factor 1
    expect(within(row).queryByText(/eggs\)/)).not.toBeInTheDocument();
    // quantity 30 and quantityBase 30 are the same cell value at factor 1
    expect(within(row).getAllByText("30")).toHaveLength(2);
  });
});

describe("SalesPage unit-price parsing", () => {
  it("parses the entered price into the order's minor units on add", async () => {
    await renderReady();
    await createDraft(DRAFT_EMPTY);
    mockAddOrderItem.mockResolvedValue({ orderId: "o1", itemId: "new" });

    fireEvent.change(screen.getByLabelText(/Unit price/), { target: { value: "1.50" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add line" }));
    });

    const body = mockAddOrderItem.mock.calls[0][1];
    expect(body.unitPriceMinorUnits).toBe(150); // 1.50 × 10^2
  });

  it("omits the price when the field is blank so the server default applies", async () => {
    await renderReady();
    await createDraft(DRAFT_EMPTY);
    mockAddOrderItem.mockResolvedValue({ orderId: "o1", itemId: "new" });

    fireEvent.change(screen.getByLabelText(/Unit price/), { target: { value: "" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add line" }));
    });

    const body = mockAddOrderItem.mock.calls[0][1];
    expect(body.unitPriceMinorUnits).toBeUndefined();
  });

  it("parses the edited price into minor units on update", async () => {
    mockUpdateOrderItem.mockResolvedValue(undefined);
    const row = await openOrder(DRAFT_WITH_ITEM);

    fireEvent.click(within(row).getByRole("button", { name: "edit" }));
    // edit mode swaps the row's cells for [qty, price] number inputs
    const editRow = screen.getByRole("row", { name: /Grade A Dozen/ });
    const [, priceInput] = within(editRow).getAllByRole("spinbutton");
    fireEvent.change(priceInput, { target: { value: "2.50" } });
    await act(async () => {
      fireEvent.click(within(editRow).getByRole("button", { name: "save" }));
    });

    const body = mockUpdateOrderItem.mock.calls[0][2];
    expect(body.unitPriceMinorUnits).toBe(250); // 2.50 × 10^2
    expect(body.quantity).toBe(3); // prefilled from the item, unchanged
  });
});
