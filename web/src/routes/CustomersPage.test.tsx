import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act } from "@testing-library/react";
import { CustomersPage } from "./CustomersPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { createCustomer, listCustomerBalances, listCustomers } from "../api/cluckwork";
import type { Customer, CustomerBalances } from "../api/cluckwork";
import { ApiError } from "../api/client";

// Keep the real formatMoney (renders the outstanding column); stub the network.
vi.mock("../api/cluckwork", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/cluckwork")>();
  return {
    ...actual,
    listCustomers: vi.fn(),
    listCustomerBalances: vi.fn(),
    createCustomer: vi.fn(),
  };
});

const mockList = vi.mocked(listCustomers);
const mockBalances = vi.mocked(listCustomerBalances);
const mockCreate = vi.mocked(createCustomer);

const C1: Customer = { id: "c1", name: "Acme Eggs", phone: "555-1", email: "a@x.co", address: "1 St", note: "vip" };
const C2: Customer = { id: "c2", name: "Bravo Co", phone: "555-2", email: null, address: null, note: null };
// c1 owes 500; c2 has no confirmed orders → absent from the balance list.
// KWD (3 decimals) so the assertion pins formatMoney's currency scale — 500
// renders "0.500 KWD", which a hard-coded 2-decimal formatter could not produce.
const BALANCES: CustomerBalances = {
  items: [{ customerId: "c1", confirmedTotalMinorUnits: 1000, paidMinorUnits: 500, outstandingMinorUnits: 500 }],
  currencyCode: "KWD", currencyMinorUnit: 3,
};

const ADMIN = { sub: "u1", role: "Admin" };
const WORKER = { sub: "u1" };

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  mockList.mockResolvedValue([C1, C2]);
  mockBalances.mockResolvedValue(BALANCES);
});

describe("CustomersPage list", () => {
  it("renders customers, dashing missing optional fields", async () => {
    renderWithProviders(<CustomersPage />, { token: WORKER });
    const rowC2 = await screen.findByRole("row", { name: /Bravo Co/ });
    // email/address/note all null on C2 → three em-dashes
    expect(within(rowC2).getAllByText("—")).toHaveLength(3);
  });

  it("shows the empty-state hint when there are no customers", async () => {
    mockList.mockResolvedValue([]);
    renderWithProviders(<CustomersPage />, { token: WORKER });
    expect(await screen.findByText(/No customers yet/)).toBeInTheDocument();
  });
});

// F131: create moved into a dialog — open it, then assert the same behaviour.
const openCreate = () => fireEvent.click(screen.getByRole("button", { name: "New customer" }));
const dialog = () => screen.getByRole("dialog");
const submit = async () => {
  await act(async () => {
    fireEvent.click(within(dialog()).getByRole("button", { name: "Add customer" }));
  });
};

describe("CustomersPage create", () => {
  it("omits blank optional fields from the request and resets the form", async () => {
    mockCreate.mockResolvedValue({ id: "c9" });
    renderWithProviders(<CustomersPage />, { token: WORKER });
    await screen.findByRole("row", { name: /Acme Eggs/ });
    openCreate();

    fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Zeta" } });
    fireEvent.change(within(dialog()).getByLabelText("Phone *"), { target: { value: "999" } });
    await submit();

    const body = mockCreate.mock.calls[0][0];
    expect(body).toMatchObject({ name: "Zeta", phone: "999" });
    expect(body.email).toBeUndefined();
    expect(body.address).toBeUndefined();
    expect(body.note).toBeUndefined();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument(); // success dismisses it
    // both required fields clear on success
    openCreate();
    expect(within(dialog()).getByLabelText("Name *")).toHaveValue("");
    expect(within(dialog()).getByLabelText("Phone *")).toHaveValue("");
  });

  it("replays the SAME create key after a failure, then rotates it after success", async () => {
    mockCreate.mockRejectedValueOnce(new ApiError(500, "Server error", "boom"));
    mockCreate.mockResolvedValue({ id: "c9" });
    renderWithProviders(<CustomersPage />, { token: WORKER });
    await screen.findByRole("row", { name: /Acme Eggs/ });
    openCreate();
    const fill = () => {
      fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Zeta" } });
      fireEvent.change(within(dialog()).getByLabelText("Phone *"), { target: { value: "999" } });
    };

    fill();
    await submit();
    // a failed create keeps the dialog up, error inside it
    expect(within(dialog()).getByText(/Server error|boom/)).toBeInTheDocument();

    fill();
    await submit();

    openCreate(); // success closed it
    fill();
    await submit();

    const k1 = mockCreate.mock.calls[0][1];
    const k2 = mockCreate.mock.calls[1][1];
    const k3 = mockCreate.mock.calls[2][1];
    expect(k2).toBe(k1); // the failed create kept the key → exact replay
    expect(k3).not.toBe(k2); // success rotated it → the next create is a fresh write
  });
});

describe("CustomersPage double-submit guard (#236)", () => {
  it("sends exactly one create when the form is submitted twice while the first is still in flight", async () => {
    // Held promise: the guard under test is DURING the flight, not after settle.
    let resolveCreate!: (v: { id: string }) => void;
    mockCreate.mockReturnValue(new Promise((r) => (resolveCreate = r)));
    renderWithProviders(<CustomersPage />, { token: WORKER });
    await screen.findByRole("row", { name: /Acme Eggs/ });
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Zeta" } });
    fireEvent.change(within(dialog()).getByLabelText("Phone *"), { target: { value: "999" } });

    // Submit the FORM twice in the same tick: the disabled button already
    // swallows clicks after a re-render, so only driving the handler directly
    // proves the handler's own re-entry guard (state cannot, a ref can).
    const form = within(dialog()).getByRole("button", { name: "Add customer" }).closest("form")!;
    await act(async () => {
      fireEvent.submit(form);
      fireEvent.submit(form);
    });
    expect(mockCreate).toHaveBeenCalledTimes(1);

    // The trigger is the visible pending state while the flight is open.
    expect(within(dialog()).getByRole("button", { name: "Add customer" })).toBeDisabled();

    await act(async () => resolveCreate({ id: "c9" }));
    expect(mockCreate).toHaveBeenCalledTimes(1); // still exactly one after settle
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument(); // the one create succeeded
  });
});

describe("CustomersPage dialog dismissal", () => {
  it("closes the create dialog on Cancel without writing", async () => {
    renderWithProviders(<CustomersPage />, { token: WORKER });
    await screen.findByRole("row", { name: /Acme Eggs/ });
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Abandoned" } });

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockCreate).not.toHaveBeenCalled();
  });
});

describe("CustomersPage outstanding balances (admin)", () => {
  it("shows each customer's outstanding via formatMoney, with an explicit zero for one with no orders", async () => {
    renderWithProviders(<CustomersPage />, { token: ADMIN });

    const rowC1 = await screen.findByRole("row", { name: /Acme Eggs/ });
    // balances load in a separate effect → await the cell (not a sync getByText)
    expect(await within(rowC1).findByText("0.500 KWD")).toBeInTheDocument(); // 500 @ scale 3
    // c2 is absent from the balance list → outstandingFor returns an explicit 0
    const rowC2 = screen.getByRole("row", { name: /Bravo Co/ });
    expect(await within(rowC2).findByText("0.000 KWD")).toBeInTheDocument();
  });

  it("shows a placeholder in the outstanding cell until balances load", async () => {
    let resolve!: (b: CustomerBalances) => void;
    mockBalances.mockReturnValue(new Promise((r) => (resolve = r)));
    renderWithProviders(<CustomersPage />, { token: ADMIN });

    const rowC1 = await screen.findByRole("row", { name: /Acme Eggs/ });
    expect(within(rowC1).getByText("…")).toBeInTheDocument();
    await act(async () => resolve(BALANCES));
    // placeholder → real value once balances resolve
    expect(await within(rowC1).findByText("0.500 KWD")).toBeInTheDocument();
  });
});

describe("CustomersPage role gating", () => {
  it("hides the outstanding column and skips the balances fetch for a non-admin", async () => {
    renderWithProviders(<CustomersPage />, { token: WORKER });
    await screen.findByRole("row", { name: /Acme Eggs/ });

    expect(screen.queryByRole("columnheader", { name: "Outstanding" })).not.toBeInTheDocument();
    expect(mockBalances).not.toHaveBeenCalled();
  });
});
