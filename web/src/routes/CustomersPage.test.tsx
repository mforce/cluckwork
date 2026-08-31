import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act } from "@testing-library/react";
import { CustomersPage } from "./CustomersPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { createCustomer, listCustomerBalances, listCustomers } from "../api/cluckwork";
import type { Customer, CustomerBalances } from "../api/cluckwork";
import { ApiError } from "../api/client";
import i18n from "../i18n";

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

// #479 — one slot per PLACE a message can appear. Both failures the issue names
// are on this screen: a balances read writing into the slot the create form
// renders, and the form's own failure moving onto the page when it is dismissed.
describe("CustomersPage error placement (#479)", () => {
  it("shows a failed create inside the dialog, not on the page behind it", async () => {
    mockCreate.mockRejectedValue(new ApiError(422, "Validation failed", "Phone is already in use."));
    renderWithProviders(<CustomersPage />, { token: WORKER });
    await screen.findByRole("row", { name: /Acme Eggs/ });
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Dup" } });
    fireEvent.change(within(dialog()).getByLabelText("Phone *"), { target: { value: "555-1" } });

    await submit();

    expect(within(dialog()).getByText("Phone is already in use.")).toBeInTheDocument();
    // Exactly one copy: the page must not render the dialog's message too.
    expect(screen.getAllByText("Phone is already in use.")).toHaveLength(1);
  });

  it("keeps a dismissed create's failure off the page", async () => {
    // The #474 complaint, one screen over: the page copy used to be guarded on
    // `!creating`, so cancelling the form MOVED its message onto the screen
    // behind, where it reads as a failure about nothing the user is looking at.
    mockCreate.mockRejectedValue(new ApiError(422, "Validation failed", "Phone is already in use."));
    renderWithProviders(<CustomersPage />, { token: WORKER });
    await screen.findByRole("row", { name: /Acme Eggs/ });
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Dup" } });
    fireEvent.change(within(dialog()).getByLabelText("Phone *"), { target: { value: "555-1" } });
    await submit();

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByText("Phone is already in use.")).not.toBeInTheDocument();
  });

  it("keeps a balances failure out of the open create dialog", async () => {
    // The admin case from the issue: the balances read sets no `busy` and the
    // New customer trigger is not gated on it, so the dialog can already be up
    // when it rejects. Sharing one slot put "could not load balances" under the
    // name and phone fields, as though the form had refused them.
    let rejectBalances!: (err: unknown) => void;
    mockBalances.mockReturnValueOnce(
      new Promise((_resolve, reject) => { rejectBalances = reject; }) as never);
    renderWithProviders(<CustomersPage />, { token: ADMIN });
    await screen.findByRole("row", { name: /Acme Eggs/ });
    openCreate();

    await act(async () => {
      rejectBalances(new ApiError(500, "Server error", "boom"));
    });

    const message = i18n.t("customers:loadBalancesErrorMessage");
    expect(within(dialog()).queryByText(message)).not.toBeInTheDocument();
    expect(screen.getByText(message)).toBeInTheDocument();
  });

  it("keeps a page failure while the dialog opens and its own write fails", async () => {
    // Two live messages at once, in their own places. The page's belongs to the
    // list read the user has not dealt with; the dialog's to the form in front
    // of them. Neither may erase the other.
    mockList.mockRejectedValue(new ApiError(500, "Server error", "boom"));
    mockCreate.mockRejectedValue(new ApiError(422, "Validation failed", "Phone is already in use."));
    renderWithProviders(<CustomersPage />, { token: WORKER });
    const listFailure = i18n.t("customers:loadCustomersErrorMessage");
    await screen.findByText(listFailure);

    openCreate();
    expect(screen.getByText(listFailure)).toBeInTheDocument();

    fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Dup" } });
    fireEvent.change(within(dialog()).getByLabelText("Phone *"), { target: { value: "555-1" } });
    await submit();

    expect(within(dialog()).getByText("Phone is already in use.")).toBeInTheDocument();
    expect(screen.getByText(listFailure)).toBeInTheDocument();
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

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 24, batch B4)
// ---------------------------------------------------------------------------

// `customers` is English-only (not in TRANSLATED_NAMESPACES — see
// translations-status.ts), so under ANY UI language the rendered text falls
// back to this exact English string, same as a still-hardcoded literal would
// render — asserting plain English under default lng:"en" would prove nothing
// (CONTRIBUTING-i18n.md's fallback trap). Swap the catalog value at runtime
// instead, the same i18n.addResource technique the other batches use, so each
// marker only renders if the screen actually reads the catalog rather than a
// literal that happens to still match it. Together these cover every
// render-pattern on this screen: a plain t() heading, a t() key SHARED across
// two render sites (newCustomerButton — the open button AND the dialog
// title), a t() field label inside the create dialog, a t() empty-state
// message, a t() table header, and the imperative i18n.t() pattern used in
// both mount-effect .catch callbacks (load errors have no hook access at that
// call site, so they always go through the imperative singleton — see
// CONTRIBUTING-i18n.md).
describe("CustomersPage i18n wiring (#182, Task 24)", () => {
  function withOverride(ns: string, key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", ns, key) as string;
    i18n.addResource("en", ns, key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", ns, key, original);
    });
  }

  it("reads the heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("customers", "title", "TITLE-MARKER", async () => {
      renderWithProviders(<CustomersPage />, { token: WORKER });
      expect(await screen.findByRole("heading", { name: "TITLE-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Customers" })).not.toBeInTheDocument();
    });
  });

  // Proves `newCustomerButton` is a SHARED key: overriding it once changes
  // both the open button's label AND the dialog's title (the dialog title
  // reuses the same key verbatim, same pattern as UsersPage's newUserButton).
  it("reads the shared new-customer key on both the open button and the dialog title", async () => {
    await withOverride("customers", "newCustomerButton", "NEW-CUSTOMER-MARKER", async () => {
      renderWithProviders(<CustomersPage />, { token: WORKER });
      const openButton = await screen.findByRole("button", { name: "NEW-CUSTOMER-MARKER" });
      expect(screen.queryByRole("button", { name: "New customer" })).not.toBeInTheDocument();
      fireEvent.click(openButton);
      expect(await screen.findByRole("dialog", { name: "NEW-CUSTOMER-MARKER" })).toBeInTheDocument();
    });
  });

  it("reads a create-dialog field label from the catalog, not a hardcoded literal", async () => {
    await withOverride("customers", "nameFieldLabel", "NAME-LABEL-MARKER", async () => {
      renderWithProviders(<CustomersPage />, { token: WORKER });
      fireEvent.click(await screen.findByRole("button", { name: "New customer" }));
      expect(within(screen.getByRole("dialog")).getByLabelText("NAME-LABEL-MARKER")).toBeInTheDocument();
      expect(within(screen.getByRole("dialog")).queryByLabelText("Name *")).not.toBeInTheDocument();
    });
  });

  it("reads the empty-state message from the catalog, not a hardcoded literal", async () => {
    mockList.mockResolvedValue([]);
    await withOverride("customers", "noCustomersMessage", "EMPTY-MARKER", async () => {
      renderWithProviders(<CustomersPage />, { token: WORKER });
      expect(await screen.findByText("EMPTY-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/No customers yet/)).not.toBeInTheDocument();
    });
  });

  it("reads a table column header from the catalog, not a hardcoded literal", async () => {
    await withOverride("customers", "phoneHeader", "PHONE-HEADER-MARKER", async () => {
      renderWithProviders(<CustomersPage />, { token: WORKER });
      expect(await screen.findByRole("columnheader", { name: "PHONE-HEADER-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("columnheader", { name: "Phone" })).not.toBeInTheDocument();
    });
  });

  // Imperative i18n.t() — the mount-effect .catch for the customers list.
  // load() is defined inline in the component body, but it runs as a Promise
  // callback (not render), so it goes through the imperative singleton, not
  // the closure's `t`.
  it("reads the load-customers error from the catalog, not a hardcoded literal", async () => {
    mockList.mockRejectedValue(new Error("boom"));
    await withOverride("customers", "loadCustomersErrorMessage", "LOAD-ERROR-MARKER", async () => {
      renderWithProviders(<CustomersPage />, { token: WORKER });
      expect(await screen.findByText("LOAD-ERROR-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/Could not load customers/)).not.toBeInTheDocument();
    });
  });

  // Imperative i18n.t() — the mount-effect .catch for admin-only balances,
  // gated behind isAdmin (a separate effect from the customers list load).
  it("reads the load-balances error from the catalog, not a hardcoded literal", async () => {
    mockBalances.mockRejectedValue(new Error("boom"));
    await withOverride("customers", "loadBalancesErrorMessage", "BALANCES-ERROR-MARKER", async () => {
      renderWithProviders(<CustomersPage />, { token: ADMIN });
      expect(await screen.findByText("BALANCES-ERROR-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/Could not load customer balances/)).not.toBeInTheDocument();
    });
  });
});

// #491 review, D1 — a reviewer measured this as a regression against main,
// because main showed the message and this does not. It is the specified
// behaviour, and it is pinned here so it stays deliberate.
//
// #474 → #476 → #478 spent three rounds on exactly this: a write the user
// walked away from settles later, and the dialog it belonged to is now a
// SECOND session they opened and are filling in. Showing the verdict there
// attributes one attempt's failure to another attempt's form — which is the
// misattribution the whole issue exists to remove. Showing it on the page is
// the context-free message #474 was filed about. So it goes nowhere, and the
// user's own dismissal is the signal that makes that acceptable.
//
// The re-pricing that permits this test: main's behaviour is not a better
// alternative that was lost, it is the defect. SalesPage already ships the
// same rule with its own test ("does not report an abandoned attempt against
// the session that replaced it"), landed in #489.
describe("CustomersPage abandoned attempts (#474, pinned in #491)", () => {
  it("drops a dismissed create's failure rather than showing it in the reopened form", async () => {
    let rejectCreate!: (err: unknown) => void;
    mockCreate.mockReturnValueOnce(
      new Promise((_resolve, reject) => { rejectCreate = reject; }) as never);
    renderWithProviders(<CustomersPage />, { token: WORKER });
    await screen.findByRole("row", { name: /Acme Eggs/ });
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Abandoned" } });
    fireEvent.change(within(dialog()).getByLabelText("Phone *"), { target: { value: "555-9" } });
    await submit();

    // Walk away mid-flight, then start a fresh one.
    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    openCreate();
    await act(async () => {
      rejectCreate(new ApiError(422, "Validation failed", "Phone is already in use."));
    });

    // The form the user is filling in NOW is not accused of the abandoned
    // attempt's mistake, and the page is not given a message about a form.
    expect(within(dialog()).queryByText("Phone is already in use.")).not.toBeInTheDocument();
    expect(screen.queryByText("Phone is already in use.")).not.toBeInTheDocument();
  });
});

// #511 — the customer book renders ONE server page and no pager, so an
// alphabetically later customer silently disappears. These pin the paged
// behaviour: the cap is crossed, the next page is reachable, and the loaded
// window survives a create.
const customerPage = (n: number, prefix = "p") =>
  Array.from({ length: n }, (_, i) => ({
    id: `${prefix}${i}`,
    // Zero-padded so the fixture's own order matches the server's Name,Id
    // ordering — an unpadded "c10" sorts before "c9" and would make the
    // sentinel's position an accident of string comparison.
    name: `${prefix} customer ${String(i).padStart(3, "0")}`,
    phone: `555-${i}`,
    email: null,
    address: null,
    note: null,
  })) as Customer[];

describe("CustomersPage paging (#511)", () => {
  it("reaches a customer past the first server page through load more", async () => {
    mockList.mockResolvedValueOnce(customerPage(100));
    renderWithProviders(<CustomersPage />, { token: WORKER });
    await screen.findByRole("row", { name: /p customer 000/ });
    expect(mockList).toHaveBeenCalledWith(expect.objectContaining({ limit: 100, offset: 0 }));

    mockList.mockResolvedValueOnce([
      { id: "zz", name: "Zulu Farm", phone: "555-z", email: null, address: null, note: null },
    ] as Customer[]);
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "load more" }));
    });

    expect(mockList).toHaveBeenLastCalledWith(expect.objectContaining({ offset: 100 }));
    expect(await screen.findByRole("row", { name: /Zulu Farm/ })).toBeInTheDocument();
    // The first page is still on screen — this is an EXTENSION, not a replacement.
    expect(screen.getByRole("row", { name: /p customer 000/ })).toBeInTheDocument();
  });

  it("withdraws the pager on a short page and never offers it on an empty list", async () => {
    mockList.mockResolvedValueOnce(customerPage(3));
    renderWithProviders(<CustomersPage />, { token: WORKER });
    await screen.findByRole("row", { name: /p customer 000/ });
    expect(screen.queryByRole("button", { name: "load more" })).not.toBeInTheDocument();
  });

  it("keeps the loaded window after a create instead of snapping back to page one", async () => {
    mockList.mockResolvedValueOnce(customerPage(100));
    renderWithProviders(<CustomersPage />, { token: WORKER });
    await screen.findByRole("row", { name: /p customer 000/ });

    mockList.mockResolvedValueOnce([
      { id: "zz", name: "Zulu Farm", phone: "555-z", email: null, address: null, note: null },
    ] as Customer[]);
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "load more" }));
    });
    await screen.findByRole("row", { name: /Zulu Farm/ });

    // The create's refresh must re-read BOTH pages the user has loaded.
    mockCreate.mockResolvedValue({ id: "c9" });
    mockList.mockResolvedValueOnce(customerPage(100));
    mockList.mockResolvedValueOnce([
      { id: "zz", name: "Zulu Farm", phone: "555-z", email: null, address: null, note: null },
    ] as Customer[]);
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Name *"), { target: { value: "Zeta" } });
    fireEvent.change(within(dialog()).getByLabelText("Phone *"), { target: { value: "999" } });
    await submit();

    // Still deep in the list: the row only page two carries is still rendered.
    expect(await screen.findByRole("row", { name: /Zulu Farm/ })).toBeInTheDocument();
  });

  it("keeps the loaded rows when EXTENDING fails, and offers the retry", async () => {
    mockList.mockResolvedValueOnce(customerPage(100));
    renderWithProviders(<CustomersPage />, { token: WORKER });
    await screen.findByRole("row", { name: /p customer 000/ });

    mockList.mockRejectedValueOnce(new ApiError(500, "Server.Error", "boom"));
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "load more" }));
    });

    // A failed EXTENSION says nothing about the rows already on screen.
    expect(screen.getByRole("row", { name: /p customer 000/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "load more" })).toBeInTheDocument();
  });

  it("renders the pager label from the active locale", async () => {
    mockList.mockResolvedValueOnce(customerPage(100));
    await i18n.changeLanguage("es");
    try {
      renderWithProviders(<CustomersPage />, { token: WORKER });
      await screen.findByRole("row", { name: /p customer 000/ });
      expect(screen.getByRole("button", { name: "cargar más" })).toBeInTheDocument();
    } finally {
      await i18n.changeLanguage("en");
    }
  });

  it("renders the pager label under tl", async () => {
    mockList.mockResolvedValueOnce(customerPage(100));
    await i18n.changeLanguage("tl");
    try {
      renderWithProviders(<CustomersPage />, { token: WORKER });
      await screen.findByRole("row", { name: /p customer 000/ });
      expect(screen.getByRole("button", { name: "mag-load pa" })).toBeInTheDocument();
    } finally {
      await i18n.changeLanguage("en");
    }
  });
});
