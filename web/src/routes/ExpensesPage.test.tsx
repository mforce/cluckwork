import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act, waitFor } from "@testing-library/react";
import { ExpensesPage } from "./ExpensesPage";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  adjustExpense, createExpense, createExpenseCategory, getExpense,
  listExpenseCategories, listExpenses, listFlocks, updateExpenseCategory,
} from "../api/cluckwork";
import type { Expense, ExpenseCategory, ExpenseList, Flock } from "../api/cluckwork";
import { ApiError } from "../api/client";

// Keep the REAL formatMoney (renders the row amounts + the month total) via
// importOriginal; stub only the network seam. Every network fn the screen can
// reach is stubbed — including updateExpenseCategory, which no test triggers —
// so a future edit that clicks "deactivate" can't silently hit the real fetch
// client. ApiError stays real (../api/client, unmocked) so errText's instanceof
// checks and the 409 branch keep working. useAuth + router ride on
// renderWithProviders. NB: this screen parses money with its OWN local
// toMinorUnits (regex + integer math), not parseMoneyToMinorUnits — the tests
// pin the parsed integer at the currency's true scale so a hard-coded ×100 dies.
vi.mock("../api/cluckwork", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/cluckwork")>();
  return {
    ...actual,
    listExpenseCategories: vi.fn(),
    listFlocks: vi.fn(),
    listExpenses: vi.fn(),
    getExpense: vi.fn(),
    createExpense: vi.fn(),
    adjustExpense: vi.fn(),
    createExpenseCategory: vi.fn(),
    updateExpenseCategory: vi.fn(),
  };
});

const mockListCategories = vi.mocked(listExpenseCategories);
const mockListFlocks = vi.mocked(listFlocks);
const mockListExpenses = vi.mocked(listExpenses);
const mockGetExpense = vi.mocked(getExpense);
const mockCreateExpense = vi.mocked(createExpense);
const mockAdjustExpense = vi.mocked(adjustExpense);
const mockCreateCategory = vi.mocked(createExpenseCategory);
const mockUpdateCategory = vi.mocked(updateExpenseCategory);

// A promise the test resolves by hand — holds a request open so the busy
// window is asserted deterministically, no timing guesses (client.test.ts idiom).
function deferred<T>() {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

const CAT_FEED: ExpenseCategory = { id: "cat-feed", farmId: "farm1", name: "Feed", active: true };
const CAT_UTIL: ExpenseCategory = { id: "cat-util", farmId: "farm1", name: "Utilities", active: true };
const CAT_OLD: ExpenseCategory = { id: "cat-old", farmId: "farm1", name: "Legacy", active: false };
const FLOCK: Flock = {
  id: "f1", farmId: "farm1", houseId: "h1", name: "Hen House 1", breed: "ISA",
  placementDate: "2026-01-01", initialCount: 100, currentBirds: 98, status: "Active",
};

// A BHD (3-decimal) expense whose snapshot currency differs from the current
// account/list currency — the exact case the code comments call out. 1500 minor
// units @ 3dp renders "1.500 BHD" (a 2dp formatter could not produce it).
const EXP_BHD: Expense = {
  id: "e1", farmId: "farm1", expenseCategoryId: "cat-feed", date: "2026-07-05",
  description: "Layer feed", amountMinorUnits: 1500, currencyCode: "BHD",
  currencyMinorUnit: 3, flockId: null, note: null, version: 1,
};
const EXP_OLD: Expense = { ...EXP_BHD, id: "e9", description: "Generator diesel", version: 7 };

const emptyList = (currencyCode: string, currencyMinorUnit: number): ExpenseList =>
  ({ items: [], totalMinorUnits: 0, currencyCode, currencyMinorUnit });

const ADMIN = { sub: "u1", role: "Admin" };
const WORKER = { sub: "u1" };

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  // jsdom has no layout engine; keep the stub so any scroll a control triggers
  // (e.g. a browser autoscroll on focus) can't throw mid-test.
  Element.prototype.scrollIntoView = vi.fn();
  // Mount-load defaults (both effects): categories + flocks, then expenses.
  mockListCategories.mockResolvedValue([CAT_FEED, CAT_UTIL, CAT_OLD]);
  mockListFlocks.mockResolvedValue([FLOCK]);
  mockListExpenses.mockResolvedValue(emptyList("USD", 2));
});

// The add form's Category / Flock selects share their "Category"/"Flock" labels
// with the filter and edit panels, so pick a combobox by an option unique to it
// ("— pick —" only in the add-category select; "All categories" only in the
// filter) rather than by an ambiguous label or a positional index.
const comboWithOption = (name: RegExp) =>
  screen.getAllByRole("combobox").find((el) => within(el).queryByRole("option", { name }) !== null)!;

// Ready = both mount effects settled: the expenses load stamps the currency into
// the amount label, and the categories load enables the (else-disabled) submit.
async function renderReady(currencyCode = "USD", token: Record<string, unknown> = ADMIN) {
  renderWithProviders(<ExpensesPage />, { token });
  await screen.findByLabelText(new RegExp(`Amount \\(${currencyCode}\\)`));
  await waitFor(() => expect(screen.getByRole("button", { name: "Record expense" })).toBeEnabled());
}

describe("ExpensesPage list + totals", () => {
  it("shows the loading placeholder, then rows and the month total via formatMoney at the currency scale", async () => {
    let resolve!: (v: ExpenseList) => void;
    mockListExpenses.mockReturnValue(new Promise((r) => (resolve = r)));
    renderWithProviders(<ExpensesPage />, { token: ADMIN });

    expect(screen.getByText("Loading…")).toBeInTheDocument(); // items === null
    await act(async () => resolve({ items: [EXP_BHD], totalMinorUnits: 12345, currencyCode: "BHD", currencyMinorUnit: 3 }));

    const row = await screen.findByRole("row", { name: /Layer feed/ });
    expect(within(row).getByText("1.500 BHD")).toBeInTheDocument(); // 1500 @ 3dp, not "15.00"
    // month total is its own value (12345), rendered at 3dp → "12.345 BHD"; a
    // hard-coded 2dp formatter would read "123.45", so this pins the scale.
    expect(screen.getByText(/Month total: 12\.345 BHD/)).toBeInTheDocument();
  });

  it("shows the empty-state hint when the month has no expenses", async () => {
    mockListExpenses.mockResolvedValue(emptyList("USD", 2));
    renderWithProviders(<ExpensesPage />, { token: ADMIN });
    expect(await screen.findByText("No expenses for this month.")).toBeInTheDocument();
  });
});

describe("ExpensesPage record expense", () => {
  // Different scales prove the parse honours the loaded currency's minor unit:
  // "5" is 5 in JPY (0dp) but would be 500 at 2dp; "1.5" is 1500 in BHD (3dp).
  it.each([
    { code: "USD", minor: 2, typed: "1.50", expected: 150 },
    { code: "JPY", minor: 0, typed: "5", expected: 5 },
    { code: "BHD", minor: 3, typed: "1.5", expected: 1500 },
  ])("parses the amount into $code minor units and posts the full body ($typed → $expected)", async ({ code, minor, typed, expected }) => {
    mockListExpenses.mockResolvedValue(emptyList(code, minor));
    mockCreateExpense.mockResolvedValue({ id: "e-new" });
    await renderReady(code);

    // Drive EVERY field off its default (date ≠ today, an explicit category, a
    // chosen flock, a real note) so a dropped/renamed/swapped field can't ride
    // through on a default value — the toEqual below then pins the whole body.
    fireEvent.change(screen.getByLabelText("Date"), { target: { value: "2026-07-05" } });
    fireEvent.change(comboWithOption(/pick/), { target: { value: "cat-feed" } });
    fireEvent.change(screen.getByLabelText("Description"), { target: { value: "Layer feed" } });
    fireEvent.change(screen.getByLabelText(new RegExp(`Amount \\(${code}\\)`)), { target: { value: typed } });
    fireEvent.change(comboWithOption(/none/), { target: { value: "f1" } });
    fireEvent.change(screen.getByLabelText("Note (optional)"), { target: { value: "Bulk buy" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Record expense" }));
    });

    const [body, key] = mockCreateExpense.mock.calls[0];
    // COMPLETE body via toEqual — every field the component sends, with the
    // amount at the loaded currency's true scale. Any extra/missing/renamed key
    // (or a field silently left at its default) now fails the assertion.
    expect(body).toEqual({
      expenseCategoryId: "cat-feed", date: "2026-07-05", description: "Layer feed",
      amountMinorUnits: expected, flockId: "f1", note: "Bulk buy",
    });
    expect(typeof key).toBe("string");
    expect(key).toBeTruthy(); // an idempotency key accompanies the write
  });

  it("replays the SAME create key after a transport failure, then rotates it after a server-acknowledged success", async () => {
    // A transport failure (non-ApiError) KEEPS the key for an exact replay; any
    // server response — 500 or success — rotates it. So reject with a bare Error,
    // not an ApiError, to exercise the replay path.
    mockCreateExpense.mockRejectedValueOnce(new TypeError("Failed to fetch"));
    mockCreateExpense.mockResolvedValue({ id: "e-new" });
    await renderReady("USD");

    const fill = () => {
      fireEvent.change(comboWithOption(/pick/), { target: { value: "cat-feed" } });
      fireEvent.change(screen.getByLabelText("Description"), { target: { value: "Feed" } });
      fireEvent.change(screen.getByLabelText(/Amount \(USD\)/), { target: { value: "1.00" } });
    };

    fill();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Record expense" })); });
    expect(await screen.findByRole("alert")).toHaveTextContent(/Failed to fetch/); // transport error surfaced

    // failure did NOT reset the form → resubmit reuses the retained key
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Record expense" })); });

    // success reset description/amount → refill so the third write can submit
    fill();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Record expense" })); });

    const k1 = mockCreateExpense.mock.calls[0][1];
    const k2 = mockCreateExpense.mock.calls[1][1];
    const k3 = mockCreateExpense.mock.calls[2][1];
    expect(k2).toBe(k1); // transport failure kept the key → exact replay
    expect(k3).not.toBe(k2); // success rotated it → the next write is fresh
  });
});

describe("ExpensesPage category filter", () => {
  it("drives listExpenses with the selected month range + categoryId (full param object)", async () => {
    mockListExpenses.mockResolvedValue(emptyList("USD", 2));
    await renderReady("USD");

    // initial mount load: "All categories" → categoryId omitted
    expect(mockListExpenses.mock.calls[0][0]).toMatchObject({ limit: 100, offset: 0 });
    expect(mockListExpenses.mock.calls[0][0]!.categoryId).toBeUndefined();

    // Pin a known month: February 2026 (non-leap) → the component must derive the
    // exact inclusive boundaries 2026-02-01 … 2026-02-28, not just a YYYY-MM prefix.
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Month"), { target: { value: "2026-02" } });
    });
    await act(async () => {
      fireEvent.change(comboWithOption(/All categories/), { target: { value: "cat-util" } });
    });
    await waitFor(() =>
      // whole param object: from/to month boundaries + categoryId + limit + offset
      expect(mockListExpenses.mock.calls.at(-1)![0]).toEqual({
        from: "2026-02-01", to: "2026-02-28",
        categoryId: "cat-util", limit: 100, offset: 0,
      }),
    );
  });
});

describe("ExpensesPage pagination", () => {
  it("appends the next page when 'load more' is clicked (offset advances by a full page)", async () => {
    // A full PAGE-length first page (items.length === PAGE) is what makes the
    // component set hasMore and render "load more". A short second page clears it.
    const page1: Expense[] = Array.from({ length: 100 }, (_, i) => ({
      ...EXP_BHD, id: `p1-${i}`,
      description: i === 0 ? "First page sentinel" : `First page ${i}`,
    }));
    const page2: Expense[] = [
      { ...EXP_BHD, id: "p2-a", description: "Second page alpha" },
      { ...EXP_BHD, id: "p2-b", description: "Second page beta" },
    ];
    mockListExpenses.mockResolvedValueOnce({ items: page1, totalMinorUnits: 0, currencyCode: "BHD", currencyMinorUnit: 3 });
    mockListExpenses.mockResolvedValueOnce({ items: page2, totalMinorUnits: 0, currencyCode: "BHD", currencyMinorUnit: 3 });
    renderWithProviders(<ExpensesPage />, { token: ADMIN });

    // full first page loaded → hasMore (100 === PAGE) surfaces "load more"
    await screen.findByRole("row", { name: /First page sentinel/ });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "load more" }));
    });

    // second fetch pages in at the page boundary (offset 100), same month/filter
    expect(mockListExpenses.mock.calls.at(-1)![0]).toMatchObject({ offset: 100, limit: 100 });
    // appended, not replaced: a first-page AND a second-page row now coexist
    expect(await screen.findByRole("row", { name: /Second page alpha/ })).toBeInTheDocument();
    expect(screen.getByRole("row", { name: /First page sentinel/ })).toBeInTheDocument();
  });
});

describe("ExpensesPage correct (version-guarded adjust)", () => {
  it("sends the version-guarded body with the amount at the expense's own currency scale + a key", async () => {
    // list currency JPY (0dp), item snapshot BHD (3dp): the edit amount must
    // parse at the ITEM's scale (target.currencyMinorUnit), not the list's.
    mockListExpenses.mockResolvedValue({ items: [EXP_OLD], totalMinorUnits: 1500, currencyCode: "JPY", currencyMinorUnit: 0 });
    mockAdjustExpense.mockResolvedValue({ ...EXP_OLD, amountMinorUnits: 2250, version: 8 });
    renderWithProviders(<ExpensesPage />, { token: ADMIN });

    const row = await screen.findByRole("row", { name: /Generator diesel/ });
    fireEvent.click(within(row).getByRole("button", { name: "correct" }));

    // prefilled from the item at 3dp: (1500 / 10**3) → "1.500" → 1.5
    const editAmount = await screen.findByLabelText("Amount (BHD)");
    expect(editAmount).toHaveValue(1.5);

    fireEvent.change(editAmount, { target: { value: "2.250" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save correction" }));
    });

    const [id, body, key] = mockAdjustExpense.mock.calls[0];
    expect(id).toBe("e9");
    // version 7 is the stale-guard base; 2250 = "2.250" @ 3dp (a 2dp parse would
    // REJECT the third decimal, so this pins the item scale too).
    expect(body).toMatchObject({
      version: 7, expenseCategoryId: "cat-feed", date: "2026-07-05",
      description: "Generator diesel", amountMinorUnits: 2250, flockId: null, note: null,
    });
    expect(typeof key).toBe("string");
    expect(key).toBeTruthy();
  });

  it("on a 409 reloads, rebinds the panel to the server's fetched-by-id latest, and warns to re-apply", async () => {
    mockListExpenses.mockResolvedValue({ items: [EXP_OLD], totalMinorUnits: 1500, currencyCode: "JPY", currencyMinorUnit: 0 });
    mockAdjustExpense.mockRejectedValue(new ApiError(409, "Conflict", "stale"));
    const fresh: Expense = { ...EXP_OLD, description: "Diesel (recount)", amountMinorUnits: 1800, version: 8 };
    mockGetExpense.mockResolvedValue(fresh);
    renderWithProviders(<ExpensesPage />, { token: ADMIN });

    const row = await screen.findByRole("row", { name: /Generator diesel/ });
    fireEvent.click(within(row).getByRole("button", { name: "correct" }));
    fireEvent.change(await screen.findByLabelText("Amount (BHD)"), { target: { value: "2.250" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save correction" }));
    });

    expect(mockAdjustExpense.mock.calls[0][1]).toMatchObject({ version: 7 }); // attempted on the stale base
    expect(mockGetExpense).toHaveBeenCalledWith("e9"); // refetched by id (a winning correction may have moved it)
    expect(await screen.findByRole("alert")).toHaveTextContent(/changed by someone else/);
    // panel now shows the server's latest values
    expect(await screen.findByRole("heading", { name: /Diesel \(recount\)/ })).toBeInTheDocument();
  });
});

describe("ExpensesPage categories", () => {
  it("creates a category with a keyed request, clears the field, and refreshes the list", async () => {
    mockCreateCategory.mockResolvedValue({ id: "cat-new" });
    renderWithProviders(<ExpensesPage />, { token: ADMIN });

    fireEvent.click(await screen.findByRole("button", { name: "manage categories" }));
    // F131: the category form is a dialog opened from the panel.
    const openNewCategory = () => fireEvent.click(screen.getByRole("button", { name: "New category" }));
    const dialog = () => screen.getByRole("dialog");
    openNewCategory();
    fireEvent.change(within(dialog()).getByLabelText("Category name"), { target: { value: "Utilities" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Add category" }));
    });

    const [body, key] = mockCreateCategory.mock.calls[0];
    expect(body).toEqual({ name: "Utilities" });
    expect(key).toBeTruthy();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument(); // success dismisses it
    openNewCategory();
    expect(within(dialog()).getByLabelText("Category name")).toHaveValue(""); // reset on success
    expect(mockListCategories).toHaveBeenCalledTimes(2); // mount load + post-create refresh
  });
});

describe("ExpensesPage pending states (#236)", () => {
  it("category toggle: the toggled row spins while held, everything else disables without spinning", async () => {
    const gate = deferred<void>();
    mockUpdateCategory.mockReturnValue(gate.promise);
    await renderReady();

    fireEvent.click(screen.getByRole("button", { name: "manage categories" }));
    // Feed first, Utilities second — the categories render in fixture order.
    const [feedToggle, utilToggle] = screen.getAllByRole("button", { name: "deactivate" });
    await act(async () => {
      fireEvent.click(feedToggle);
    });

    // Exactly one control spins — the toggled row's own scope…
    expect(feedToggle).toBeDisabled();
    expect(feedToggle).toHaveAttribute("aria-busy", "true");
    // …while the sibling category and the main submit merely disable.
    expect(utilToggle).toBeDisabled();
    expect(utilToggle).not.toHaveAttribute("aria-busy");
    const record = screen.getByRole("button", { name: "Record expense" });
    expect(record).toBeDisabled();
    expect(record).not.toHaveAttribute("aria-busy");

    await act(async () => {
      gate.resolve();
    });
    await waitFor(() => expect(screen.getByText('Category "Feed" deactivated.')).toBeInTheDocument());
    expect(document.querySelector('[aria-busy="true"]')).toBeNull();
    expect(screen.getAllByRole("button", { name: "deactivate" })[0]).toBeEnabled();
  });

  it("locks the category-name input while its create is held — the pending scope is derived from it", async () => {
    const gate = deferred<{ id: string }>();
    mockCreateCategory.mockReturnValue(gate.promise);
    await renderReady();

    fireEvent.click(screen.getByRole("button", { name: "manage categories" }));
    fireEvent.click(screen.getByRole("button", { name: "New category" }));
    const dialog = () => screen.getByRole("dialog");
    fireEvent.change(within(dialog()).getByLabelText("Category name"), { target: { value: "Fuel" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Add category" }));
    });

    // The submit spins on the name-derived scope (addCategoryScope)…
    expect(within(dialog()).getByRole("button", { name: "Add category" }))
      .toHaveAttribute("aria-busy", "true");
    // …so the name input locks with the flight: editing it mid-flight would
    // re-point isPending at a scope nobody is running and drop the spinner
    // while the request is still open (#242 review).
    expect(within(dialog()).getByLabelText("Category name")).toBeDisabled();

    await act(async () => {
      gate.resolve({ id: "cat-new" });
    });
    // Success dismisses the dialog; nothing is left spinning.
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(document.querySelector('[aria-busy="true"]')).toBeNull();
  });
});

describe("ExpensesPage dialog dismissal", () => {
  it("closes the correction dialog on Cancel without writing", async () => {
    mockListExpenses.mockResolvedValue({ items: [EXP_OLD], totalMinorUnits: 1500, currencyCode: "JPY", currencyMinorUnit: 0 });
    renderWithProviders(<ExpensesPage />, { token: ADMIN });

    const row = await screen.findByRole("row", { name: /Generator diesel/ });
    fireEvent.click(within(row).getByRole("button", { name: "correct" }));

    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockAdjustExpense).not.toHaveBeenCalled();
  });
});

describe("ExpensesPage access (admin-only money data)", () => {
  // ExpensesPage has NO in-component role gate: it renders the same tree and
  // loads on mount for any authenticated session. The Admin restriction lives in
  // AppLayout (the Expenses nav link is `{isAdmin && …}`) and is enforced by the
  // server on every expenses endpoint (each returns 403 to a non-admin). So the
  // only fact observable at THIS component's boundary is that it does not
  // self-gate — asserted for both an Admin and a plain Worker. (Its mount-effect
  // error branches are intentionally not tested — Vitest 3 + React 19 flags them
  // as a false positive.)
  it.each([
    { label: "Admin", token: ADMIN },
    { label: "Worker (no role claim)", token: WORKER },
  ])("$label — does not self-gate: renders the screen and fetches expenses for any authenticated role", async ({ token }) => {
    mockListExpenses.mockResolvedValue(emptyList("USD", 2));
    renderWithProviders(<ExpensesPage />, { token });

    expect(await screen.findByRole("heading", { name: "Expenses" })).toBeInTheDocument();
    await waitFor(() => expect(mockListExpenses).toHaveBeenCalled());
    expect(mockListCategories).toHaveBeenCalled();
  });
});
