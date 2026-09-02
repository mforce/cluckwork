// T039: Sales new-order first-customer default (FR-019) — engine-level test.
//
// The engine's controlled-sync admits a committed entity as-is. The page's
// first-customer-default behavior (picking `customers[0]` on dialog open) is
// a PAGE concern, not an engine concern — this test verifies the ENGINE
// contract: a controlledCommitted that IS in the discovery window is admitted
// without a spurious exact GET, and the committed entity survives.
import { describe, it, expect, vi } from "vitest";
import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { CustomerPicker } from "../components/CustomerPicker";
import { listCustomers, getCustomer } from "../api/cluckwork";
import type { Customer } from "../api/cluckwork";
import type { PickerSnapshot } from "../components/NamedEntityPicker";
import i18n from "../i18n";

vi.mock("../api/cluckwork", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/cluckwork")>();
  return { ...actual, listCustomers: vi.fn(), getCustomer: vi.fn() };
});
const mockListCustomers = vi.mocked(listCustomers);
const mockGetCustomer = vi.mocked(getCustomer);

describe("T039: first-customer default via controlled admission (FR-019)", () => {
  it("a controlledCommitted that IS in the discovery window is admitted without a spurious exact GET", async () => {
    const acme: Customer = { id: "c1", name: "Acme Eggs", phone: "", email: null, address: null, note: null, version: 1 };
    const beta: Customer = { ...acme, id: "c2", name: "Beta Dairy" };
    mockListCustomers.mockResolvedValue([acme, beta]);
    mockGetCustomer.mockResolvedValue(acme);

    render(<CustomerPicker label="Customer" required
      open
      trigger={<button className="named-picker-trigger link">Acme Eggs</button>}
      controlledCommitted={acme}
      controlledGeneration={1} />);

    // Acme is in the discovery window → admitted as-is, NO exact GET.
    await screen.findByText("Acme Eggs");
    await waitFor(() => {
      expect(mockGetCustomer).not.toHaveBeenCalled();
    });
  });

  it("a requestedId NOT in the window resolves via the exact GET (row-owned identity)", async () => {
    const gamma: Customer = { id: "c3", name: "Gamma Farm", phone: "", email: null, address: null, note: null, version: 1 };
    const other: Customer = { ...gamma, id: "c1", name: "Other Co" };
    mockListCustomers.mockResolvedValue([other]);
    mockGetCustomer.mockResolvedValue(gamma);

    render(<CustomerPicker label="Customer" required
      open
      trigger={<button className="named-picker-trigger link">Gamma Farm</button>}
      requestedId="c3"
      controlledGeneration={1} />);

    // Gamma is NOT in the window → resolved via the exact GET.
    await waitFor(() => {
      expect(mockGetCustomer).toHaveBeenCalledWith("c3");
    });
  });

  // FR-019: a requestedId whose exact GET fails must go unavailable, never
  // substitute the first discovery result, and block canSubmit — and the
  // adjacent Retry must repeat ONLY that GET, recovering on success.
  it("a requestedId GET failure enters unavailable — never substitutes the first discovery result — and Retry repeats the GET only, recovering on success", async () => {
    // This file has no beforeEach/mock reset; earlier tests in this describe
    // also call getCustomer("c3"), so start this test's call-count math clean.
    mockGetCustomer.mockClear();
    const other: Customer = { id: "c1", name: "Other Co", phone: "", email: null, address: null, note: null, version: 1 };
    const gamma: Customer = { ...other, id: "c3", name: "Gamma Farm" };
    mockListCustomers.mockResolvedValue([other]);
    mockGetCustomer.mockRejectedValueOnce(new Error("not found"));

    const snapshots: PickerSnapshot<Customer>[] = [];
    render(<CustomerPicker label="Customer" required
      open
      trigger={<button className="named-picker-trigger link">pick</button>}
      requestedId="c3"
      controlledGeneration={1}
      onSnapshot={(s) => snapshots.push(s)} />);

    await waitFor(() => expect(mockGetCustomer).toHaveBeenCalledWith("c3"));
    const unavailableLabel = i18n.t("namedEntityPicker:unavailable");
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent(unavailableLabel));
    // Never a first-result substitution: "Other Co" may still list as a
    // discovery ROW (an option), but it is never the COMMITTED entity.
    expect(document.querySelector(".named-picker-committed")).not.toBeInTheDocument();
    await waitFor(() => {
      const last = snapshots[snapshots.length - 1];
      expect(last.committed).toBeNull();
      expect(last.canSubmit).toBe(false);
    });

    // Retry re-issues ONLY the exact GET.
    mockGetCustomer.mockResolvedValue(gamma);
    const retryLabel = i18n.t("namedEntityPicker:retry");
    fireEvent.click(screen.getByRole("button", { name: retryLabel }));

    await waitFor(() => expect(mockGetCustomer).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.getByRole("combobox")).toHaveValue("Gamma Farm"));
    await waitFor(() => {
      const last = snapshots[snapshots.length - 1];
      expect(last.committed?.id).toBe("c3");
      expect(last.canSubmit).toBe(true);
    });
  });
});
