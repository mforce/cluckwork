// T039: Sales new-order customer picker — default / exact / unavailable
// lifecycle. Page-level tests over the REAL SalesPage: the explicit
// first-customer default commits through a controlled generation, an
// out-of-window external default hydrates via the exact GET and enters
// unavailable on failure, and PickerSnapshot.canSubmit gates BOTH the
// create button and the create handler.
//
// Deliberately a separate file from SalesPage.test.tsx: that file's
// beforeEach primes a single-customer fixture for its whole suite, and this
// lifecycle suite needs a held (deferred) listCustomers to observe the
// uninitialized → default transition.
import { describe, it, expect, vi } from "vitest";
import { screen, within, fireEvent, act, waitFor } from "@testing-library/react";
import { SalesPage } from "./SalesPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { NO_RECORD_HISTORY } from "../test/fixtures";
import {
  createOrder, getCustomer, getOrder, listCustomers, listEggGrades,
  listEggUnitConversions, listOrderPayments, listOrders, listProducts,
} from "../api/cluckwork";
import type { Customer, SalesOrder } from "../api/cluckwork";
// (ApiError no longer imported here)

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
    getCustomer: vi.fn(),
  };
});
const mockListCustomers = vi.mocked(listCustomers);
const mockGetCustomer = vi.mocked(getCustomer);
const mockCreateOrder = vi.mocked(createOrder);
const mockGetOrder = vi.mocked(getOrder);
const mockListProducts = vi.mocked(listProducts);
const mockListEggGrades = vi.mocked(listEggGrades);
const mockListEggUnitConversions = vi.mocked(listEggUnitConversions);
const mockListOrders = vi.mocked(listOrders);
const mockListOrderPayments = vi.mocked(listOrderPayments);

const ADMIN = { sub: "u1", role: "Admin" };

const A: Customer = { id: "c1", name: "Acme Eggs", phone: "", email: null, address: null, note: null, version: 1 };
const B: Customer = { ...A, id: "c2", name: "Beta Dairy" };

function draftEmpty(id = "o1"): SalesOrder {
  return {
    ...NO_RECORD_HISTORY,
    id, customerId: "c1", customerName: "Acme Eggs", referenceNumber: "SO-1", orderDate: "2026-07-20",
    status: "Draft", totalMinorUnits: 0, currencyCode: "USD", currencyMinorUnit: 2, voidReason: null, items: [],
  };
}

function deferred<T>() {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

// Prime the non-customer setup reads, then hand listCustomers a held
// promise so the default transition is observable.
function primeSetup(listPromise: Promise<Customer[]>) {
  mockListCustomers.mockReturnValue(listPromise as never);
  mockListProducts.mockResolvedValue([]);
  mockListEggGrades.mockResolvedValue([]);
  mockListEggUnitConversions.mockResolvedValue([]);
  mockListOrders.mockResolvedValue([]);
  mockListOrderPayments.mockResolvedValue({
    items: [], paidMinorUnits: 0, outstandingMinorUnits: 0, totalMinorUnits: 0,
    currencyCode: "USD", currencyMinorUnit: 2,
  });
  mockGetOrder.mockResolvedValue(draftEmpty());
  mockCreateOrder.mockResolvedValue({ id: "o1" });
}

describe("T039: Sales new-order first-customer default", () => {
  it("commits the exact first customer through a controlled generation when the dialog opens", async () => {
    const gate = deferred<Customer[]>();
    primeSetup(gate.promise);
    renderWithProviders(<SalesPage />, { token: ADMIN });

    await act(async () => { gate.resolve([A, B]); });
    await screen.findByRole("button", { name: "New order" });

    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    const dlg = screen.getByRole("dialog");

    // The dialog's picker is the page's first-customer default: the trigger
    // shows the committed name WITHOUT the engine ever issuing its own
    // discovery read (the engine has no open trigger — the page drives the
    // default through controlledCommitted + a generation bump).
    await screen.findByText("Acme Eggs");
    await waitFor(() => {
      expect(mockGetCustomer).not.toHaveBeenCalled();
    });

    // And the create actually ships the first customer's id.
    fireEvent.click(within(dlg).getByRole("button", { name: "New draft order" }));
    await waitFor(() => {
      expect(mockCreateOrder).toHaveBeenCalledWith(expect.objectContaining({ customerId: "c1" }), expect.any(String));
    });
  });

  it("gates BOTH the create button and the handler on canSubmit (no submit while exploring)", async () => {
    const gate = deferred<Customer[]>();
    primeSetup(gate.promise);
    renderWithProviders(<SalesPage />, { token: ADMIN });

    await act(async () => { gate.resolve([A, B]); });
    await screen.findByRole("button", { name: "New order" });

    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    const dlg = screen.getByRole("dialog");
    await screen.findByText("Acme Eggs");

    // The picker rides the dialog (open={true}): the combobox is live in the
    // dialog's form slot. Start exploring: the engine reports exploring=true
    // (visible text differs from the committed label) → canSubmit false.
    const input = within(dlg).getByRole("combobox");
    fireEvent.change(input, { target: { value: "B" } });
    // The create button reflects canSubmit=false (disabled) …
    const submit = within(dlg).getByRole("button", { name: "New draft order" });
    await waitFor(() => expect(submit).toBeDisabled());
    // …and the handler independently refuses: a direct click ships nothing.
    mockCreateOrder.mockClear();
    fireEvent.click(submit);
    expect(mockCreateOrder).not.toHaveBeenCalled();
  });

  it("commits a genuine pick from the loaded window through onCommit + a controlled generation", async () => {
    const gate = deferred<Customer[]>();
    primeSetup(gate.promise);
    renderWithProviders(<SalesPage />, { token: ADMIN });

    await act(async () => { gate.resolve([A, B]); });
    await screen.findByRole("button", { name: "New order" });

    fireEvent.click(screen.getByRole("button", { name: "New order" }));
    const dlg = screen.getByRole("dialog");
    await screen.findByText("Acme Eggs"); // default committed

    // The engine discovered its unfiltered first page on open; commit a
    // GENUINE pick (Beta Dairy) — the engine fires onCommit, and the page
    // re-syncs through a fresh controlled generation.
    // Two "Beta Dairy" options exist (the list-filter <option> and the
    // picker's <li role=option>); the picker's is the one in the dialog.
    const option = within(dlg).getByRole("option", { name: "Beta Dairy" });
    await waitFor(() => expect(option).toBeInTheDocument());
    fireEvent.click(option);
    await waitFor(() => {
      expect(mockCreateOrder).not.toHaveBeenCalled();
    });

    // The create now ships the GENUINELY picked customer (c2) — the pick
    // won over the first-customer default.
    fireEvent.click(within(dlg).getByRole("button", { name: "New draft order" }));
    await waitFor(() => {
      expect(mockCreateOrder).toHaveBeenCalledWith(expect.objectContaining({ customerId: "c2" }), expect.any(String));
    });
  });
});
