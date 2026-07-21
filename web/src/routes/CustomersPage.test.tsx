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

describe("CustomersPage create", () => {
  it("omits blank optional fields from the request and resets the form", async () => {
    mockCreate.mockResolvedValue({ id: "c9" });
    renderWithProviders(<CustomersPage />, { token: WORKER });
    await screen.findByRole("row", { name: /Acme Eggs/ });

    fireEvent.change(screen.getByPlaceholderText("Name *"), { target: { value: "Zeta" } });
    fireEvent.change(screen.getByPlaceholderText("Phone *"), { target: { value: "999" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Add customer" }));
    });

    const body = mockCreate.mock.calls[0][0];
    expect(body).toMatchObject({ name: "Zeta", phone: "999" });
    expect(body.email).toBeUndefined();
    expect(body.address).toBeUndefined();
    expect(body.note).toBeUndefined();
    // both required fields clear on success
    expect(screen.getByPlaceholderText("Name *")).toHaveValue("");
    expect(screen.getByPlaceholderText("Phone *")).toHaveValue("");
  });

  it("replays the SAME create key after a failure, then rotates it after success", async () => {
    mockCreate.mockRejectedValueOnce(new ApiError(500, "Server error", "boom"));
    mockCreate.mockResolvedValue({ id: "c9" });
    renderWithProviders(<CustomersPage />, { token: WORKER });
    await screen.findByRole("row", { name: /Acme Eggs/ });
    const fill = () => {
      fireEvent.change(screen.getByPlaceholderText("Name *"), { target: { value: "Zeta" } });
      fireEvent.change(screen.getByPlaceholderText("Phone *"), { target: { value: "999" } });
    };

    fill();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Add customer" })); });
    expect(await screen.findByText(/Server error|boom/)).toBeInTheDocument();

    fill();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Add customer" })); });

    fill();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Add customer" })); });

    const k1 = mockCreate.mock.calls[0][1];
    const k2 = mockCreate.mock.calls[1][1];
    const k3 = mockCreate.mock.calls[2][1];
    expect(k2).toBe(k1); // the failed create kept the key → exact replay
    expect(k3).not.toBe(k2); // success rotated it → the next create is a fresh write
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
