import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act } from "@testing-library/react";
import { SalesPage } from "./SalesPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { account } from "../test/fixtures";
import i18n from "../i18n";
import {
  addOrderItem, cancelOrder, confirmOrder, createOrder, getOrder, listCustomers, listEggGrades,
  listOrderPayments, listOrders, listProducts, recordPayment, updateOrderItem, voidOrder, voidPayment,
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
const mockRecordPayment = vi.mocked(recordPayment);

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

// The "New order" action only appears once customers have loaded; wait on it so
// the mount effects have settled.
async function renderReady() {
  renderWithProviders(<SalesPage />, { token: ADMIN });
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

describe("SalesPage i18n", () => {
  it("renders its heading and primary action from the sales i18n catalog (#182)", async () => {
    await renderReady();

    // Pinned to i18n.t, not the literal — proves the screen is reading the
    // catalog rather than a string that happens to still match it.
    expect(screen.getByRole("heading", { name: i18n.t("sales:title") })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: i18n.t("sales:newOrder") })).toBeInTheDocument();
  });
});

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
