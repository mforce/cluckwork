import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act, waitFor } from "@testing-library/react";
import { ExpensesPage } from "./ExpensesPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { findRowByCellText, getRowByCellText } from "../test/rows";
import { account, NO_RECORD_HISTORY, RECORD_HISTORY } from "../test/fixtures";
import {
  adjustExpense, createExpense, createExpenseCategory, getExpense, getFlock,
  listExpenseCategories, listExpenses, listFlocks, updateExpenseCategory,
} from "../api/cluckwork";
import type { Expense, ExpenseCategory, ExpenseList, Flock } from "../api/cluckwork";
import { ApiError } from "../api/client";
import i18n from "../i18n";

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
    getFlock: vi.fn(),
  getCustomer: vi.fn(),
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
const mockGetFlock = vi.mocked(getFlock);

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
  ...NO_RECORD_HISTORY,
  id: "f1", farmId: "farm1", houseId: "h1", name: "Hen House 1", breed: "ISA",
  placementDate: "2026-01-01", initialCount: 100, currentBirds: 98, status: "Active",
};

// A BHD (3-decimal) expense whose snapshot currency differs from the current
// account/list currency — the exact case the code comments call out. 1500 minor
// units @ 3dp renders "BHD 1.500" (a 2dp formatter could not produce it).
const EXP_BHD: Expense = {
  ...NO_RECORD_HISTORY,
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
  // #512 T038 — the correction dialog resolves an out-of-window row-owned flock
  // through the exact GET; the stub must resolve (never reject) by default so
  // a row naming a listed flock still commits exactly.
  mockGetFlock.mockResolvedValue(FLOCK);
  mockListExpenses.mockResolvedValue(emptyList("USD", 2));
});

// The add form's Category select shares its "Category" label with the filter
// and edit panels, so pick it by an option unique to it ("— pick —" only in
// the add-category select; "All categories" only in the filter) rather than by
// an ambiguous label or a positional index. The flock is a FlockPicker now
// (#512 T038): `pickAddFlock` opens its trigger and commits the option.
const comboWithOption = (name: RegExp) =>
  screen.getAllByRole("combobox").find((el) => within(el).queryByRole("option", { name }) !== null)!;
const pickAddFlock = async (name: RegExp) => {
  fireEvent.click(screen.getByRole("button", { name: /— none —/ }));
  fireEvent.click(await screen.findByRole("option", { name }));
};

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
    expect(within(row).getByText("BHD 1.500")).toBeInTheDocument(); // 1500 @ 3dp, not "15.00"
    // month total is its own value (12345), rendered at 3dp → "BHD 12.345"; a
    // hard-coded 2dp formatter would read "123.45", so this pins the scale.
    expect(screen.getByText(/Month total: BHD 12\.345/)).toBeInTheDocument();
  });

  it("shows the empty-state hint when the month has no expenses", async () => {
    mockListExpenses.mockResolvedValue(emptyList("USD", 2));
    renderWithProviders(<ExpensesPage />, { token: ADMIN });
    expect(await screen.findByText("No expenses for this month.")).toBeInTheDocument();
  });

  // #512 US4 (T043/T051) — a row's own flockName is null (the flock left the
  // caller's tenant/flock scope between reads), even though the SAME id is
  // present in the page's own capped `flocks` list (default fixture: "f1" /
  // "Hen House 1"). The row must show the translated unavailable label,
  // never that catalog substitution and never a raw id fragment.
  it("a row whose own flockName is null shows the translated unavailable label — never the catalog's name for that id, never an id fragment", async () => {
    const EXP_GONE_NAME: Expense = { ...EXP_BHD, id: "e-gone", flockId: "f1", flockName: null };
    mockListExpenses.mockResolvedValue({ items: [EXP_GONE_NAME], totalMinorUnits: 1500, currencyCode: "BHD", currencyMinorUnit: 3 });
    renderWithProviders(<ExpensesPage />, { token: ADMIN });

    const row = await screen.findByRole("row", { name: /Layer feed/ });
    expect(within(row).getByText(i18n.t("expenses:flockUnavailable"))).toBeInTheDocument();
    expect(within(row).queryByText("Hen House 1")).not.toBeInTheDocument();
    expect(within(row).queryByText("f1")).not.toBeInTheDocument();
  });
});

// #494 — the record-history column is a shared component, well tested on its
// own; what is NOT tested by that unit suite is the per-page WIRING that hands
// it the CORRECT row's history object. A page passing the wrong variable (a
// different row, or a stray constant) would go uncaught otherwise.
describe("ExpensesPage record history column (#494)", () => {
  it("shows the record history column for the row that has one", async () => {
    const EXP_HISTORY: Expense = { ...EXP_BHD, ...RECORD_HISTORY, id: "e-hist", description: "Provenance expense" };
    mockListExpenses.mockResolvedValue({
      items: [EXP_BHD, EXP_HISTORY], totalMinorUnits: 3000, currencyCode: "BHD", currencyMinorUnit: 3,
    });
    renderWithProviders(<ExpensesPage />, { token: ADMIN });

    const historyRow = await screen.findByRole("row", { name: /Provenance expense/ });
    // #653 — the visible line shows the CHANGER (the more recent event);
    // both facts still live in the title, unchanged from #494.
    expect(within(historyRow).getByText(/bo/)).toBeInTheDocument();
    expect((historyRow.querySelector("td.provenance-cell") as HTMLElement).title).toBe(
      "Created by ana@farm.test on 2026-05-01 08:00:00\nLast changed by bo@farm.test on 2026-05-03 14:30:00",
    );

    // The OTHER row must not carry the history row's data — this is what
    // catches every row being wired to the same object.
    const otherRow = screen.getByRole("row", { name: /Layer feed/ });
    expect(otherRow.querySelector("td.provenance-cell")).toBeNull();
  });
});

// #493 — full audit trail, distinct from the two-point summary above.
describe("ExpensesPage audit history link (#493)", () => {
  it("links each row to its own entity-scoped audit history", async () => {
    mockListExpenses.mockResolvedValue({
      items: [EXP_BHD], totalMinorUnits: 1500, currencyCode: "BHD", currencyMinorUnit: 3,
    });
    renderWithProviders(<ExpensesPage />, { token: ADMIN });
    const row = await screen.findByRole("row", { name: /Layer feed/ });
    expect(within(row).getByRole("link", { name: "Audit history" }))
      .toHaveAttribute("href", "/audit?entityId=e1");
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
    await pickAddFlock(/Hen House 1/);
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
    await findRowByCellText("First page sentinel");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "load more" }));
    });

    // second fetch pages in at the page boundary (offset 100), same month/filter
    expect(mockListExpenses.mock.calls.at(-1)![0]).toMatchObject({ offset: 100, limit: 100 });
    // appended, not replaced: a first-page AND a second-page row now coexist
    expect(await findRowByCellText("Second page alpha")).toBeInTheDocument();
    expect(getRowByCellText("First page sentinel")).toBeInTheDocument();
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

// #479 — one slot per PLACE a message can appear. The add-category and
// correction dialogs each get their own; the mount read and the
// category-toggle writes (neither behind a dialog) share the page's.
describe("ExpensesPage error placement (#479)", () => {
  it("shows a failed category create inside the dialog, not on the page behind it", async () => {
    mockCreateCategory.mockRejectedValue(new ApiError(422, "Validation failed", "Name already in use."));
    renderWithProviders(<ExpensesPage />, { token: ADMIN });

    fireEvent.click(await screen.findByRole("button", { name: "manage categories" }));
    fireEvent.click(screen.getByRole("button", { name: "New category" }));
    const dlg = screen.getByRole("dialog");
    fireEvent.change(within(dlg).getByLabelText("Category name"), { target: { value: "Feed" } });
    await act(async () => {
      fireEvent.click(within(dlg).getByRole("button", { name: "Add category" }));
    });

    expect(within(dlg).getByText("Name already in use.")).toBeInTheDocument();
    // Exactly one copy: the page must not render the dialog's message too.
    expect(screen.getAllByText("Name already in use.")).toHaveLength(1);
  });

  it("shows a failed correction inside the dialog, not on the page behind it", async () => {
    mockListExpenses.mockResolvedValue({ items: [EXP_OLD], totalMinorUnits: 1500, currencyCode: "JPY", currencyMinorUnit: 0 });
    mockAdjustExpense.mockRejectedValue(new ApiError(500, "Server error", "Correction failed."));
    renderWithProviders(<ExpensesPage />, { token: ADMIN });

    const row = await screen.findByRole("row", { name: /Generator diesel/ });
    fireEvent.click(within(row).getByRole("button", { name: "correct" }));
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save correction" }));
    });

    const dlg = screen.getByRole("dialog");
    expect(within(dlg).getByText("Correction failed.")).toBeInTheDocument();
    expect(screen.getAllByText("Correction failed.")).toHaveLength(1);
  });

  // The mount-time categories/flocks read is this screen's only background
  // READ — held open so it can still be pending once a dialog is up, the same
  // shape as CustomersPage's balances race.
  it("keeps a background categories/flocks load failure out of an open add-category dialog", async () => {
    let rejectLoad!: (err: unknown) => void;
    mockListCategories.mockReturnValueOnce(
      new Promise((_resolve, reject) => { rejectLoad = reject; }) as never);
    renderWithProviders(<ExpensesPage />, { token: ADMIN });
    await screen.findByRole("heading", { name: "Expenses" });

    fireEvent.click(screen.getByRole("button", { name: "manage categories" }));
    fireEvent.click(screen.getByRole("button", { name: "New category" }));
    const dlg = screen.getByRole("dialog");

    await act(async () => {
      rejectLoad(new ApiError(500, "Server error", "Categories failed to load."));
    });

    expect(within(dlg).queryByText("Categories failed to load.")).not.toBeInTheDocument();
    expect(screen.getByText("Categories failed to load.")).toBeInTheDocument();
  });

  it("keeps a page failure while the correction dialog opens and its own write fails", async () => {
    mockListExpenses.mockResolvedValue({ items: [EXP_OLD], totalMinorUnits: 1500, currencyCode: "JPY", currencyMinorUnit: 0 });
    mockUpdateCategory.mockRejectedValue(new ApiError(500, "Server error", "Category toggle failed."));
    mockAdjustExpense.mockRejectedValue(new ApiError(500, "Server error", "Correction failed."));
    renderWithProviders(<ExpensesPage />, { token: ADMIN });

    fireEvent.click(await screen.findByRole("button", { name: "manage categories" }));
    const rows = screen.getAllByRole("listitem");
    const feedRow = rows.find((li) => li.textContent?.includes("Feed"))!;
    await act(async () => {
      fireEvent.click(within(feedRow).getByRole("button", { name: "deactivate" }));
    });
    expect(screen.getByText("Category toggle failed.")).toBeInTheDocument();

    const row = screen.getByRole("row", { name: /Generator diesel/ });
    fireEvent.click(within(row).getByRole("button", { name: "correct" }));
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save correction" }));
    });

    const dlg = screen.getByRole("dialog");
    expect(within(dlg).getByText("Correction failed.")).toBeInTheDocument();
    expect(within(dlg).queryByText("Category toggle failed.")).not.toBeInTheDocument();
    expect(screen.getByText("Category toggle failed.")).toBeInTheDocument(); // still there
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

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 23, batch B4)
// ---------------------------------------------------------------------------

// `expenses` is English-only (not in TRANSLATED_NAMESPACES — see
// translations-status.ts), so under ANY UI language the rendered text falls
// back to this exact English string, same as a still-hardcoded literal would
// render — asserting it, even under a non-English locale, would prove nothing
// (CONTRIBUTING-i18n.md's fallback trap). Swap the catalog value at runtime
// instead, the same i18n.addResource technique the other batches use, so each
// marker only renders if the screen actually reads the catalog rather than a
// literal that happens to still match it. Together these cover every
// render-pattern on this screen: a plain t() heading, a t() key interpolating
// DATA (formatMoney's total, the expense's own date/description, a category's
// free-form name), a t() key SHARED across several render sites
// (deactivatedSuffix), and the imperative i18n.t() pattern in all three of
// its shapes here — a plain success message, a message interpolating DATA,
// and a message thrown from a plain (non-hook) helper (toMinorUnits) and a
// catch block (the 409 rebind).
describe("ExpensesPage i18n wiring (#182, Task 23)", () => {
  function withOverride(ns: string, key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", ns, key) as string;
    i18n.addResource("en", ns, key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", ns, key, original);
    });
  }

  it("reads the heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("expenses", "title", "TITLE-MARKER", async () => {
      renderWithProviders(<ExpensesPage />, { token: ADMIN });
      expect(await screen.findByRole("heading", { name: "TITLE-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Expenses" })).not.toBeInTheDocument();
    });
  });

  // Proves the month-total copy template reads from the catalog while still
  // interpolating formatMoney's already-formatted total (farm-locale DATA) —
  // a hardcoded literal, or one that dropped the interpolation, would fail
  // this even though "BHD 12.345" itself is unaffected by the marker.
  it("interpolates formatMoney's total into the month-total label from the catalog", async () => {
    mockListExpenses.mockResolvedValue({ items: [], totalMinorUnits: 12345, currencyCode: "BHD", currencyMinorUnit: 3 });
    await withOverride("expenses", "monthTotalLabel", "TOTAL-MARKER {{amount}} END", async () => {
      renderWithProviders(<ExpensesPage />, { token: ADMIN });
      expect(await screen.findByText("TOTAL-MARKER BHD 12.345 END")).toBeInTheDocument();
      expect(screen.queryByText(/Month total:/)).not.toBeInTheDocument();
    });
  });

  // Proves the correction dialog's title reads the COPY template from the
  // catalog while still interpolating the expense's own free-form date and
  // description (DATA).
  it("interpolates the expense's date and description into the correction dialog title from the catalog", async () => {
    mockListExpenses.mockResolvedValue({ items: [EXP_OLD], totalMinorUnits: 1500, currencyCode: "JPY", currencyMinorUnit: 0 });
    await withOverride(
      "expenses", "correctExpenseDialogTitleWithExpense", "CORRECT-MARKER {{date}}/{{description}} END",
      async () => {
        renderWithProviders(<ExpensesPage />, { token: ADMIN });
        const row = await screen.findByRole("row", { name: /Generator diesel/ });
        fireEvent.click(within(row).getByRole("button", { name: "correct" }));
        expect(
          await screen.findByRole("dialog", { name: "CORRECT-MARKER 2026-07-05/Generator diesel END" }),
        ).toBeInTheDocument();
      },
    );
  });

  // Proves `deactivatedSuffix` is a SHARED key: overriding it once changes the
  // rendered text at the category-list row (asserted here), which is the same
  // key read by the filter option and the edit-form's category picker.
  it("reads the deactivated-category suffix from the catalog on the category-list row", async () => {
    await withOverride("expenses", "deactivatedSuffix", " SUFFIX-MARKER", async () => {
      renderWithProviders(<ExpensesPage />, { token: ADMIN });
      fireEvent.click(await screen.findByRole("button", { name: "manage categories" }));
      const rows = screen.getAllByRole("listitem");
      const legacyRow = rows.find((li) => li.textContent?.includes("Legacy"));
      expect(legacyRow?.textContent).toContain("SUFFIX-MARKER");
      expect(legacyRow?.textContent).not.toContain("(deactivated)");
    });
  });

  // Imperative i18n.t() — a plain success message set from an event handler
  // (onAdd), not render.
  it("reads the expense-recorded success message from the catalog, not a hardcoded literal", async () => {
    mockCreateExpense.mockResolvedValue({ id: "e-new" });
    await withOverride("expenses", "expenseRecordedMessage", "RECORDED-MARKER", async () => {
      await renderReady("USD");
      fireEvent.change(comboWithOption(/pick/), { target: { value: "cat-feed" } });
      fireEvent.change(screen.getByLabelText("Description"), { target: { value: "Feed" } });
      fireEvent.change(screen.getByLabelText(/Amount \(USD\)/), { target: { value: "1.00" } });
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "Record expense" }));
      });
      expect(await screen.findByText("RECORDED-MARKER")).toBeInTheDocument();
      expect(screen.queryByText("Expense recorded.")).not.toBeInTheDocument();
    });
  });

  // Imperative i18n.t() interpolating DATA — the category's free-form NAME —
  // into a message built from an event handler (onToggleCategory).
  it("interpolates the category's name into the deactivated message from the catalog", async () => {
    mockUpdateCategory.mockResolvedValue(undefined);
    await withOverride("expenses", "categoryDeactivatedMessage", "DEACT-MARKER {{name}} END", async () => {
      renderWithProviders(<ExpensesPage />, { token: ADMIN });
      fireEvent.click(await screen.findByRole("button", { name: "manage categories" }));
      const rows = screen.getAllByRole("listitem");
      const feedRow = rows.find((li) => li.textContent?.includes("Feed"))!;
      await act(async () => {
        fireEvent.click(within(feedRow).getByRole("button", { name: "deactivate" }));
      });
      expect(await screen.findByText("DEACT-MARKER Feed END")).toBeInTheDocument();
    });
  });

  // Imperative i18n.t() thrown from toMinorUnits — a plain (non-hook) helper
  // called synchronously inside the add-form's submit handler, never a
  // rejected mount promise, so it's safe to exercise directly (same shape as
  // ProductsPage's price-precision wiring test).
  it("reads the amount-precision validation message from the catalog, not a hardcoded literal", async () => {
    mockListExpenses.mockResolvedValue(emptyList("BHD", 3));
    await withOverride("expenses", "atMostDecimals", "AT-MOST-MARKER {{count}} END", async () => {
      await renderReady("BHD");
      fireEvent.change(comboWithOption(/pick/), { target: { value: "cat-feed" } });
      fireEvent.change(screen.getByLabelText("Description"), { target: { value: "Feed" } });
      fireEvent.change(screen.getByLabelText(/Amount \(BHD\)/), { target: { value: "1.2345" } }); // 4dp > BHD's 3dp
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "Record expense" }));
      });
      expect(await screen.findByRole("alert")).toHaveTextContent("AT-MOST-MARKER 3 END");
    });
  });

  // Imperative i18n.t() thrown from the 409 catch branch (onSaveEdit) — the
  // version-conflict rebind message.
  it("reads the version-conflict rebind message from the catalog, not a hardcoded literal", async () => {
    mockListExpenses.mockResolvedValue({ items: [EXP_OLD], totalMinorUnits: 1500, currencyCode: "JPY", currencyMinorUnit: 0 });
    mockAdjustExpense.mockRejectedValue(new ApiError(409, "Conflict", "stale"));
    mockGetExpense.mockResolvedValue({ ...EXP_OLD, description: "Diesel (recount)", amountMinorUnits: 1800, version: 8 });
    await withOverride("expenses", "conflictRebindMessage", "CONFLICT-MARKER", async () => {
      renderWithProviders(<ExpensesPage />, { token: ADMIN });
      const row = await screen.findByRole("row", { name: /Generator diesel/ });
      fireEvent.click(within(row).getByRole("button", { name: "correct" }));
      fireEvent.change(await screen.findByLabelText("Amount (BHD)"), { target: { value: "2.250" } });
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "Save correction" }));
      });
      expect(await screen.findByRole("alert")).toHaveTextContent("CONFLICT-MARKER");
      expect(screen.queryByText(/changed by someone else/)).not.toBeInTheDocument();
    });
  });
});

describe("ExpensesPage list failures (#469)", () => {
  // The money-screen version of the stale-window bug: this list had no
  // request sequencing, so a failed month change used to leave the PREVIOUS
  // month's rows AND its total on screen under the new month's picker — a
  // figure that reads as legitimate while describing a period it never
  // covered. The total now travels with the rows as page metadata, so it
  // lands and clears with them.
  it("does not keep the previous month's total when the month change fails", async () => {
    mockListExpenses.mockResolvedValueOnce({
      items: [EXP_OLD], totalMinorUnits: 99900, currencyCode: "USD", currencyMinorUnit: 2,
    });
    await renderReady();
    expect(screen.getByText(/Month total: \$999\.00/)).toBeInTheDocument();

    mockListExpenses.mockRejectedValueOnce(new Error("boom"));
    await act(async () => {
      // A month the picker is not already on — it defaults to the current one.
      fireEvent.change(screen.getByLabelText("Month"), { target: { value: "2026-05" } });
    });

    // Neither the old month's rows nor its money may describe the new one.
    expect(screen.queryByText(/\$999\.00/)).not.toBeInTheDocument();
    expect(screen.getByRole("alert")).toBeInTheDocument();
  });

  it("ignores a stale month response that lands after a newer one", async () => {
    await renderReady();

    let releaseStale!: (v: ExpenseList) => void;
    mockListExpenses.mockReturnValueOnce(new Promise((r) => { releaseStale = r; }));
    fireEvent.change(screen.getByLabelText("Month"), { target: { value: "2026-06" } });
    mockListExpenses.mockResolvedValueOnce({
      items: [], totalMinorUnits: 500, currencyCode: "USD", currencyMinorUnit: 2,
    });
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Month"), { target: { value: "2026-05" } });
    });
    expect(screen.getByText(/Month total: \$5\.00/)).toBeInTheDocument();

    await act(async () => {
      releaseStale({ items: [], totalMinorUnits: 88800, currencyCode: "USD", currencyMinorUnit: 2 });
    });
    expect(screen.getByText(/Month total: \$5\.00/)).toBeInTheDocument();
    expect(screen.queryByText(/\$888\.00/)).not.toBeInTheDocument();
  });
});

describe("ExpensesPage currency scale (#469, codex P1)", () => {
  // The form used to take its decimal scale from the LIST response, so a
  // failed load cleared it and silently fell back to 2 decimals while the
  // form stayed enabled. On a 3-decimal currency that converts BHD 1.000 to
  // 100 minor units instead of 1000 — a wrong number stored against the
  // account's real scale. The account is the authority; the list is not.
  it("converts at the account's scale even after the list load fails", async () => {
    mockListExpenses.mockRejectedValue(new Error("boom"));
    renderWithProviders(<ExpensesPage />, {
      token: ADMIN, farm: account({ currencyCode: "BHD", currencyMinorUnit: 3 }),
    });
    await screen.findByLabelText(/Amount \(BHD\)/);
    await waitFor(() => expect(screen.getByRole("button", { name: "Record expense" })).toBeEnabled());

    fireEvent.change(comboWithOption(/— pick —/), { target: { value: CAT_FEED.id } });
    fireEvent.change(screen.getByLabelText("Description"), { target: { value: "Diesel" } });
    fireEvent.change(screen.getByLabelText(/Amount \(BHD\)/), { target: { value: "1.000" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Record expense" }));
    });

    expect(mockCreateExpense).toHaveBeenCalledWith(
      expect.objectContaining({ amountMinorUnits: 1000 }), expect.any(String));
  });
});

describe("ExpensesPage currency scale without an account (#469, codex P1)", () => {
  // The account read can fail while the screen still renders (FarmProvider
  // supplies farm === null and AppLayout carries on). The list response is
  // then the only place a scale has ever come from — and the hook clears it
  // on the next failed load. Guessing 2 decimals there is how a 3-decimal
  // farm stores 1.000 as 100 minor units.
  it("retains the last observed scale when a later load fails and no account is available", async () => {
    mockListExpenses
      .mockResolvedValueOnce({ items: [], totalMinorUnits: 0, currencyCode: "BHD", currencyMinorUnit: 3 })
      .mockRejectedValue(new Error("boom"));
    mockCreateExpense.mockResolvedValue({ id: "e-new" });
    renderWithProviders(<ExpensesPage />, { token: ADMIN }); // no farm
    await screen.findByLabelText(/Amount \(BHD\)/);
    await waitFor(() => expect(screen.getByRole("button", { name: "Record expense" })).toBeEnabled());

    await act(async () => {
      fireEvent.change(screen.getByLabelText("Month"), { target: { value: "2026-05" } });
    });
    expect(screen.getByRole("alert")).toBeInTheDocument();

    fireEvent.change(comboWithOption(/— pick —/), { target: { value: CAT_FEED.id } });
    fireEvent.change(screen.getByLabelText("Description"), { target: { value: "Diesel" } });
    fireEvent.change(screen.getByLabelText(/Amount \(BHD\)/), { target: { value: "1.000" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Record expense" }));
    });

    expect(mockCreateExpense).toHaveBeenCalledWith(
      expect.objectContaining({ amountMinorUnits: 1000 }), expect.any(String));
  });

  it("refuses to record at all when no scale has ever been observed", async () => {
    // Nothing authoritative has ever loaded: recording would have to GUESS
    // the denomination. Refusing beats storing a wrong number.
    mockListExpenses.mockRejectedValue(new Error("boom"));
    renderWithProviders(<ExpensesPage />, { token: ADMIN }); // no farm

    await screen.findByRole("alert");
    expect(screen.getByRole("button", { name: "Record expense" })).toBeDisabled();
    expect(mockCreateExpense).not.toHaveBeenCalled();
  });
});

describe("ExpensesPage currency scale freshness (#469, codex P1)", () => {
  // The list envelope carries the ACCOUNT'S CURRENT currency (the endpoint
  // reads accounts.GetCurrentAsync per request), whereas `farm` is the
  // bootstrap snapshot this tab loaded with. So a currency change made
  // elsewhere reaches this screen through the list first, and preferring the
  // snapshot converts at a scale the server no longer uses.
  it("prefers the freshly loaded list scale over a stale farm snapshot", async () => {
    mockListExpenses.mockResolvedValue(emptyList("JPY", 0)); // server: 0 decimals now
    mockCreateExpense.mockResolvedValue({ id: "e-new" });
    renderWithProviders(<ExpensesPage />, {
      token: ADMIN, farm: account({ currencyCode: "USD", currencyMinorUnit: 2 }), // stale
    });
    await screen.findByLabelText(/Amount \(JPY\)/);
    await waitFor(() => expect(screen.getByRole("button", { name: "Record expense" })).toBeEnabled());

    fireEvent.change(comboWithOption(/— pick —/), { target: { value: CAT_FEED.id } });
    fireEvent.change(screen.getByLabelText("Description"), { target: { value: "Diesel" } });
    fireEvent.change(screen.getByLabelText(/Amount \(JPY\)/), { target: { value: "1" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Record expense" }));
    });

    // 1 JPY is 1 minor unit. Converting at the stale 2-decimal snapshot would
    // post 100 — stored as 100 JPY against the server's own currency.
    expect(mockCreateExpense).toHaveBeenCalledWith(
      expect.objectContaining({ amountMinorUnits: 1 }), expect.any(String));
  });
});

describe("ExpensesPage cross-period display while loading (#469, codex P2)", () => {
  // The whole point of this change was that a total must never describe a
  // period the picker does not show. A PENDING month change is that same
  // defect with a shorter fuse: the hook deliberately keeps the previous
  // window until the replacement lands, so the screen has to blank it.
  it("hides the previous month's total and rows while the new month is loading", async () => {
    mockListExpenses.mockResolvedValueOnce({
      items: [EXP_OLD], totalMinorUnits: 99900, currencyCode: "USD", currencyMinorUnit: 2,
    });
    await renderReady();
    expect(screen.getByText(/Month total: \$999\.00/)).toBeInTheDocument();

    // The replacement hangs: nothing about the old month may still show.
    mockListExpenses.mockReturnValueOnce(new Promise(() => {}));
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Month"), { target: { value: "2026-05" } });
    });

    expect(screen.queryByText(/\$999\.00/)).not.toBeInTheDocument();
    expect(screen.queryByText("Generator diesel")).not.toBeInTheDocument();
    expect(screen.getByText("Loading…")).toBeInTheDocument();
  });
});

describe("ExpensesPage conflict reload is issued once (#469)", () => {
  // runWrite already re-read the loaded WINDOW before rethrowing the 409, so
  // a second read here is redundant — and worse than redundant: reload() is
  // page-one only, so for a user who had paged deeper it collapses the very
  // window runWrite just restored, and if it transiently fails it clears the
  // rows that refresh had recovered.
  it("does not issue a second replacement read after a 409", async () => {
    mockListExpenses
      .mockResolvedValueOnce({ items: [EXP_OLD], totalMinorUnits: 1500, currencyCode: "BHD", currencyMinorUnit: 3 })
      .mockResolvedValueOnce({ items: [EXP_OLD], totalMinorUnits: 1500, currencyCode: "BHD", currencyMinorUnit: 3 })
      .mockRejectedValue(new Error("boom")); // a third read would wipe the rows
    mockAdjustExpense.mockRejectedValue(new ApiError(409, "Conflict", "stale"));
    mockGetExpense.mockResolvedValue({ ...EXP_OLD, description: "Diesel (recount)", version: 8 });
    renderWithProviders(<ExpensesPage />, { token: ADMIN });

    const row = await screen.findByRole("row", { name: /Generator diesel/ });
    fireEvent.click(within(row).getByRole("button", { name: "correct" }));
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save correction" }));
    });

    expect(mockListExpenses).toHaveBeenCalledTimes(2);
    expect(await screen.findByRole("alert")).toHaveTextContent(/changed by someone else/);
    // The window runWrite restored is still on screen.
    expect(screen.getByRole("row", { name: /Generator diesel/ })).toBeInTheDocument();
  });
});

describe("ExpensesPage total is never a guess (#469, codex P2)", () => {
  // meta is cleared when a replacement fails, and `?? 0` then rendered a
  // definitive "Month total: 0.00" next to the error — stating that a period
  // whose figure is UNKNOWN is zero. On a money screen that is not a
  // degraded display, it is a wrong number.
  it("shows no total at all when the month load failed", async () => {
    mockListExpenses
      .mockResolvedValueOnce({ items: [EXP_OLD], totalMinorUnits: 99900, currencyCode: "USD", currencyMinorUnit: 2 })
      .mockRejectedValue(new Error("boom"));
    await renderReady();
    expect(screen.getByText(/Month total: \$999\.00/)).toBeInTheDocument();

    await act(async () => {
      fireEvent.change(screen.getByLabelText("Month"), { target: { value: "2026-05" } });
    });

    expect(screen.getByRole("alert")).toBeInTheDocument();
    expect(screen.queryByText(/Month total:/)).not.toBeInTheDocument();
    expect(screen.queryByText(/0\.00/)).not.toBeInTheDocument();
  });
});

// #491 review — two ways a message could reach a slot nobody is rendering.
// Both were found by reviewers, both reproduced here before being fixed.
describe("ExpensesPage messages that had nowhere to land (#491)", () => {
  it("explains the reopened correction even when it was dismissed mid-flight", async () => {
    // The correction's Cancel is gated on busy, but Escape and the X are not,
    // and onClose runs the same abandon. Dismiss during the 409's re-read and
    // the panel springs back open on the winner's values; without un-muting,
    // it does so with no word of why.
    mockListExpenses.mockResolvedValue({
      items: [EXP_OLD], totalMinorUnits: 1500, currencyCode: "BHD", currencyMinorUnit: 3,
    });
    mockAdjustExpense.mockRejectedValue(new ApiError(409, "Conflict", "stale"));
    let resolveReread!: (e: Expense) => void;
    mockGetExpense.mockReturnValueOnce(
      new Promise((resolve) => { resolveReread = resolve; }) as never);
    await renderReady("BHD");

    const row = await screen.findByRole("row", { name: /Generator diesel/ });
    fireEvent.click(within(row).getByRole("button", { name: "correct" }));
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save correction" }));
    });

    fireEvent.keyDown(screen.getByRole("dialog"), { key: "Escape" });
    await act(async () => {
      resolveReread({ ...EXP_OLD, description: "Diesel (recount)", version: 8 });
    });

    expect(screen.getByRole("dialog")).toBeInTheDocument();
    expect(await screen.findByText(/changed by someone else/)).toBeInTheDocument();
  });

  it("puts a post-create category refresh failure on the page, not in the closed dialog", async () => {
    // The write succeeded and the dialog closed; the list refresh then failed.
    // Reported to the dialog's scope it renders nowhere at all, leaving a stale
    // category list and no explanation.
    mockCreateCategory.mockResolvedValue({ id: "cat-9" });
    await renderReady();
    // Queued AFTER the mount load, so it is the post-create refresh that fails
    // and not the screen's own first read.
    mockListCategories.mockRejectedValueOnce(new ApiError(500, "Server error", "Could not reload categories."));

    fireEvent.click(screen.getByRole("button", { name: "manage categories" }));
    fireEvent.click(screen.getByRole("button", { name: "New category" }));
    fireEvent.change(within(screen.getByRole("dialog")).getByLabelText("Category name"), { target: { value: "Bedding" } });
    await act(async () => {
      fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Add category" }));
    });

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.getByText("Could not reload categories.")).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// #512 T028/T038 — the flock is a FlockPicker now (not a native select):
// canSubmit gates both write controls AND their submit handlers (a disabled
// button is not the write-safety boundary), the edit dialog holds the
// ROW-OWNED identity exactly (archived / outside the discovery window), and
// a failed exact read enters the explicit unavailable state with a Retry.
// ---------------------------------------------------------------------------

describe("ExpensesPage flock picker (T028/T038)", () => {
  const FLOCK2 = { ...FLOCK, id: "f2", name: "Old Coop", status: "Archived" } as Flock;

  it("record expense: a valid BLANK optional selection still submits (flockId null)", async () => {
    mockCreateExpense.mockResolvedValue({ id: "e-new" });
    await renderReady("USD");
    // No flock committed — the optional picker's blank IS the account-wide
    // choice and must not be blocked.
    fireEvent.change(comboWithOption(/pick/), { target: { value: "cat-feed" } });
    fireEvent.change(screen.getByLabelText("Description"), { target: { value: "Feed" } });
    fireEvent.change(screen.getByLabelText(/Amount \(USD\)/), { target: { value: "1.00" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Record expense" }));
    });
    expect(mockCreateExpense).toHaveBeenCalledWith(
      expect.objectContaining({ expenseCategoryId: "cat-feed", flockId: null }),
      expect.any(String));
  });

  it("record expense: a direct handler bypass (form submit event) is rejected while the picker is not safe to submit", async () => {
    // The visible button is disabled while the picker is not ready; this test
    // bypasses it by dispatching the form submit event DIRECTLY — the handler
    // guard is the real boundary, and it must still refuse.
    mockCreateExpense.mockResolvedValue({ id: "e-new" });
    renderWithProviders(<ExpensesPage />, { token: ADMIN });
    // Before ANY snapshot has landed (uninitialized), the guard is closed.
    const form = screen.getByRole("button", { name: "Record expense" }).closest("form")!;
    fireEvent.submit(form);
    expect(mockCreateExpense).not.toHaveBeenCalled();
  });

  it("correct: holds the row-owned flock EXACTLY, including an archived flock absent from the discovery window", async () => {
    // Only f1 is in the mount list; the row names archived f2 — the picker's
    // exact GET must resolve it (never substitute f1) and the body must carry
    // f2.
    mockListFlocks.mockResolvedValue([FLOCK]);
    mockGetFlock.mockResolvedValue(FLOCK2);
    const EXP_F2: Expense = { ...EXP_BHD, id: "e2", flockId: "f2", note: null };
    mockListExpenses.mockResolvedValue({ items: [EXP_F2], totalMinorUnits: 1500, currencyCode: "BHD", currencyMinorUnit: 3 });
    mockAdjustExpense.mockResolvedValue({ ...EXP_F2, version: 2 });
    renderWithProviders(<ExpensesPage />, { token: ADMIN });

    const row = await screen.findByRole("row", { name: /Layer feed/ });
    fireEvent.click(within(row).getByRole("button", { name: "correct" }));
    // The exact GET resolved the row-owned identity (f2, not the first result).
    await waitFor(() => expect(mockGetFlock).toHaveBeenCalledWith("f2"));
    // Save is withheld until the picker's snapshot commits the identity.
    const save = await screen.findByRole("button", { name: "Save correction" });
    await waitFor(() => expect(save).toBeEnabled());
    await act(async () => { fireEvent.click(save); });
    expect(mockAdjustExpense).toHaveBeenCalledWith(
      "e2", expect.objectContaining({ version: 1, flockId: "f2" }), expect.any(String));
  });

  // #512 T038 — when the row-owned flock is ALREADY a full entity in the
  // page's own mount list (in-window, not archived-out), the picker admits it
  // as-is (controlledCommitted). No exact GET is owed — issuing one anyway
  // would be spurious network traffic for data the page already has.
  it("correct: a row-owned flock already present as a full entity in the mount list is admitted as-is — no spurious exact GET", async () => {
    mockListFlocks.mockResolvedValue([FLOCK]); // f1, in-window
    const EXP_F1: Expense = { ...EXP_BHD, id: "e6", flockId: "f1", note: null };
    mockListExpenses.mockResolvedValue({ items: [EXP_F1], totalMinorUnits: 1500, currencyCode: "BHD", currencyMinorUnit: 3 });
    mockAdjustExpense.mockResolvedValue({ ...EXP_F1, version: 2 });
    renderWithProviders(<ExpensesPage />, { token: ADMIN });

    const row = await screen.findByRole("row", { name: /Layer feed/ });
    fireEvent.click(within(row).getByRole("button", { name: "correct" }));

    // Admitted straight from the mount list — the trigger shows it immediately.
    const dialog = screen.getByRole("dialog");
    await screen.findByText("Hen House 1");
    const save = within(dialog).getByRole("button", { name: "Save correction" });
    await waitFor(() => expect(save).toBeEnabled());
    await act(async () => { fireEvent.click(save); });
    expect(mockAdjustExpense).toHaveBeenCalledWith(
      "e6", expect.objectContaining({ version: 1, flockId: "f1" }), expect.any(String));
    // No exact GET was ever issued for data the page already had.
    expect(mockGetFlock).not.toHaveBeenCalled();
  });

  it("correct: a row-owned flock that 404s on the exact GET enters explicit unavailable — Save disabled, no raw ID, Retry recovers", async () => {
    mockListFlocks.mockResolvedValue([FLOCK]);
    mockGetFlock.mockRejectedValueOnce(new Error("not found"));
    const EXP_GONE: Expense = { ...EXP_BHD, id: "e3", flockId: "f-gone", note: null };
    mockListExpenses.mockResolvedValue({ items: [EXP_GONE], totalMinorUnits: 1500, currencyCode: "BHD", currencyMinorUnit: 3 });
    mockAdjustExpense.mockResolvedValue({ ...EXP_GONE, version: 2 });
    renderWithProviders(<ExpensesPage />, { token: ADMIN });

    const row = await screen.findByRole("row", { name: /Layer feed/ });
    fireEvent.click(within(row).getByRole("button", { name: "correct" }));
    // Explicit unavailable state: the translated alert, never the raw ID or a
    // first-result substitution, and Save withheld.
    const dialog = screen.getByRole("dialog");
    await waitFor(() =>
      expect(within(dialog).getAllByRole("alert").some((el) => /no longer available/i.test(el.textContent ?? ""))).toBe(true));
    expect(within(dialog).queryByText(/f-gone/)).not.toBeInTheDocument();
    const save = within(dialog).getByRole("button", { name: "Save correction" });
    expect(save).toBeDisabled();
    // The handler guard too: bypass the disabled button and the write is
    // refused.
    fireEvent.submit(within(dialog).getByRole("button", { name: "Save correction" }).closest("form")!);
    expect(mockAdjustExpense).not.toHaveBeenCalled();
    // Retry re-issues ONLY the exact GET; success commits the exact entity
    // and re-enables Save.
    const getFlockBefore = mockGetFlock.mock.calls.length;
    mockGetFlock.mockResolvedValueOnce(FLOCK2);
    await act(async () => { fireEvent.click(within(dialog).getByRole("button", { name: /retry/i })); });
    expect(mockGetFlock.mock.calls.length).toBe(getFlockBefore + 1);
    await waitFor(() => expect(within(dialog).getByRole("button", { name: "Save correction" })).toBeEnabled());
  });

  it("correct: a blank (account-wide) row still saves with flockId null — the guard never blocks a valid blank selection", async () => {
    const EXP_NONE: Expense = { ...EXP_BHD, id: "e4", flockId: null, note: null };
    mockListExpenses.mockResolvedValue({ items: [EXP_NONE], totalMinorUnits: 1500, currencyCode: "BHD", currencyMinorUnit: 3 });
    mockAdjustExpense.mockResolvedValue({ ...EXP_NONE, version: 2 });
    renderWithProviders(<ExpensesPage />, { token: ADMIN });

    const row = await screen.findByRole("row", { name: /Layer feed/ });
    fireEvent.click(within(row).getByRole("button", { name: "correct" }));
    // No exact read is owed for a blank row (no id to resolve).
    const save = await screen.findByRole("button", { name: "Save correction" });
    await waitFor(() => expect(save).toBeEnabled());
    await act(async () => { fireEvent.click(save); });
    expect(mockAdjustExpense).toHaveBeenCalledWith(
      "e4", expect.objectContaining({ version: 1, flockId: null }), expect.any(String));
    expect(mockGetFlock).not.toHaveBeenCalled();
  });

  it("switching directly from an unavailable flock correction to an account-wide row clears the previous picker state", async () => {
    const EXP_F1: Expense = { ...EXP_BHD, id: "e-flock", description: "Flock expense", flockId: "f-gone" };
    const EXP_NONE: Expense = { ...EXP_BHD, id: "e-none", description: "Farm expense", flockId: null };
    mockListExpenses.mockResolvedValue({
      items: [EXP_F1, EXP_NONE], totalMinorUnits: 3000, currencyCode: "BHD", currencyMinorUnit: 3,
    });
    mockAdjustExpense.mockResolvedValue({ ...EXP_NONE, version: 2 });
    mockGetFlock.mockRejectedValueOnce(new Error("not found"));
    renderWithProviders(<ExpensesPage />, { token: ADMIN });

    const flockRow = await screen.findByRole("row", { name: /Flock expense/ });
    const farmRow = screen.getByRole("row", { name: /Farm expense/ });
    fireEvent.click(within(flockRow).getByRole("button", { name: "correct" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Save correction" })).toBeDisabled());

    fireEvent.click(within(farmRow).getByRole("button", { name: "correct" }));
    const save = screen.getByRole("button", { name: "Save correction" });
    await waitFor(() => expect(save).toBeEnabled());
    await act(async () => { fireEvent.click(save); });

    expect(mockAdjustExpense).toHaveBeenCalledWith(
      "e-none", expect.objectContaining({ flockId: null }), expect.any(String));
  });

  it("renders the row's OWN flock name from the record's carried name, not the picker results", async () => {
    // The row carries its own current name (the endpoint's per-page scoped
    // read); even if the mount flock list resolves a DIFFERENT name for the
    // same id, the row must show what IT carries.
    const EXP_NAMED: Expense = { ...EXP_BHD, id: "e5", flockId: "f1", note: null, flockName: "Renamed Coop" };
    mockListExpenses.mockResolvedValue({ items: [EXP_NAMED], totalMinorUnits: 1500, currencyCode: "BHD", currencyMinorUnit: 3 });
    renderWithProviders(<ExpensesPage />, { token: ADMIN });

    const row = await screen.findByRole("row", { name: /Layer feed/ });
    expect(within(row).getByText("Renamed Coop")).toBeInTheDocument();
    expect(within(row).queryByText("Hen House 1")).not.toBeInTheDocument();
  });
});
