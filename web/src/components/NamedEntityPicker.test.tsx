import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, act, fireEvent, waitFor, cleanup } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState, useEffect } from "react";
import { FlockPicker } from "./FlockPicker";
import { CustomerPicker } from "./CustomerPicker";
import type { Flock, Customer } from "../api/cluckwork";
import { listFlocks, listCustomers, getCustomer, getFlock } from "../api/cluckwork";
import i18n from "../i18n";

// #512 US1 — the shared picker engine's discovery discipline, proven through
// the two typed adapters (pages never see the generic engine).
//
// The whole correctness question is REQUEST DISCIPLINE over a bare 50-row
// array response: nothing the user does may emit a request the server would
// answer with the wrong window, and only the newest intent may paint rows.
// So these tests assert against the mocked transport, not the DOM alone — the
// DOM assertions verify what the user must see (rows, Load more, the
// committed label as a sibling of the editable field), the transport
// assertions verify what the server must receive.
//
// US1 scope: debounce, paging/dedupe/final-empty, pointer/Enter commit,
// committed-label retention, eligibility-change rediscovery, disabled
// non-interactivity, loading/no-results visibility, snapshot emission
// stability. Exact/default resolution, Escape/clear, Retry, and the full
// ARIA write-safety surface are US2/US3 (T026–T035).
vi.mock("../api/cluckwork", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/cluckwork")>();
  return { ...actual, listFlocks: vi.fn(), getFlock: vi.fn(), listCustomers: vi.fn(), getCustomer: vi.fn() };
});

const mockListFlocks = vi.mocked(listFlocks);
const mockListCustomers = vi.mocked(listCustomers);
const mockGetCustomer = vi.mocked(getCustomer);
const mockGetFlock = vi.mocked(getFlock);

const F = (id: string, name: string, status = "Active"): Flock => ({
  id,
  farmId: "farm1",
  houseId: "h1",
  name,
  breed: "Lohmann",
  placementDate: "2026-01-01",
  initialCount: 100,
  currentBirds: 90,
  status,
  createdByEmail: null,
  createdAtUtc: null,
  lastChangedByEmail: null,
  lastChangedAtUtc: null,
});

const C = (id: string, name: string): Customer => ({
  id,
  name,
  phone: "555-0100",
  email: null,
  address: null,
  note: null,
  version: 1,
});

// 122 flocks, stably ordered by name the way the server sorts (ORDER BY
// Name, Id).
// Page one (offset 0, limit 50): "Flock 01"–"Flock 50" (ids a0–a49) = 50.
// Page two (offset 50, limit 50): "Flock 51" (ids b0 AND b50 — the
// duplicate-name pair, id tiebreak puts b0 first) + "Flock 52"–"Flock 99"
// (ids b51–b98) = 2 + 48 = 50.
// Page three (offset 100, limit 50): "Flock 100" (id b99) + "Flock 101"–
// "Flock 121" (ids c0–c20) = 1 + 21 = 22 (a short final window).
// Total = 50 + 50 + 22 = 122.
const FLOCKS: Flock[] = [
  ...Array.from({ length: 50 }, (_, i) => F(`a${i}`, `Flock ${String(i + 1).padStart(2, "0")}`)),
  F("b0", "Flock 51"),
  F("b50", "Flock 51"),
  ...Array.from({ length: 48 }, (_, i) => F(`b${i + 51}`, `Flock ${String(i + 52).padStart(2, "0")}`)),
  F("b99", "Flock 100"),
  ...Array.from({ length: 21 }, (_, i) => F(`c${i}`, `Flock ${String(i + 101).padStart(2, "0")}`)),
];
const CUSTOMERS: Customer[] = [
  ...Array.from({ length: 50 }, (_, i) => C(`x${i}`, `Customer ${String(i + 1).padStart(2, "0")}`)),
  ...Array.from({ length: 30 }, (_, i) => C(`y${i}`, `Customer ${String(i + 51).padStart(2, "0")}`)),
];

// Answer /flocks the way the server would: eligibility filter, literal name
// search, stable order, window. The tests assert on the CALLS, so a client
// that asks for the wrong offset, limit, or search gets the wrong window and
// the row assertions fail.
function serveFlocks(params: Parameters<typeof mockListFlocks>[0]): Flock[] {
  const p = params ?? {};
  let rows = FLOCKS;
  if (p.eligibility === "active") rows = FLOCKS.filter((f) => f.status === "Active");
  if (p.search) {
    const needle = p.search.trim().toLowerCase();
    rows = rows.filter((f) => f.name.toLowerCase().includes(needle));
  }
  return rows.slice(p.offset ?? 0, (p.offset ?? 0) + (p.limit ?? 100));
}

function serveCustomers(params: Parameters<typeof mockListCustomers>[0]): Customer[] {
  const p = params ?? {};
  let rows = CUSTOMERS;
  if (p.search) {
    const needle = p.search.trim().toLowerCase();
    rows = rows.filter((c) => c.name.toLowerCase().includes(needle));
  }
  return rows.slice(p.offset ?? 0, (p.offset ?? 0) + (p.limit ?? 100));
}

// The element that shows a committed name (outside the listbox), and proves
// it is not a discovery row.
const committedName = (name: string) => {
  const el = screen.getAllByText(name).find((el) => el.closest("ul") === null);
  expect(el, `a committed label for ${name} outside the listbox`).not.toBeUndefined();
  return el!;
};

beforeEach(() => {
  vi.clearAllMocks();
  mockListFlocks.mockImplementation(async (p) => serveFlocks(p));
  mockListCustomers.mockImplementation(async (p) => serveCustomers(p));
});

afterEach(() => {
  vi.useRealTimers();
});

// --- 250 ms debounce ---------------------------------------------------------

describe("discovery debounce (#512 FR-008)", () => {
  it("issues no request at 249 ms and exactly one at 250 ms", async () => {
    vi.useFakeTimers();
    render(<FlockPicker label="Pick flock" eligibility="active" required open />);
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    expect(mockListFlocks).toHaveBeenCalledTimes(1);
    // Type "f": the 250 ms pause starts. fireEvent.change drives React's
    // onChange directly — no inter-keystroke timers for fake timers to miss.
    const input = screen.getByRole("combobox");
    fireEvent.change(input, { target: { value: "f" } });
    await act(async () => { await vi.advanceTimersByTime(249); });
    expect(mockListFlocks).toHaveBeenCalledTimes(1); // no replacement yet
    await act(async () => { await vi.advanceTimersByTime(1); }); // exactly 250
    await act(async () => {}); // flush the replacement promise
    expect(mockListFlocks).toHaveBeenCalledTimes(2);
    expect(mockListFlocks).toHaveBeenLastCalledWith({ search: "f", eligibility: "active", limit: 50, offset: 0 });
  });

  it("resets the pause on every keystroke and sends the final text", async () => {
    vi.useFakeTimers();
    render(<FlockPicker label="Pick flock" eligibility="active" required open />);
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    expect(mockListFlocks).toHaveBeenCalledTimes(1);
    const input = screen.getByRole("combobox");
    fireEvent.change(input, { target: { value: "f" } });
    await act(async () => { await vi.advanceTimersByTime(240); });
    fireEvent.change(input, { target: { value: "fo" } });
    await act(async () => { await vi.advanceTimersByTime(249); });
    expect(mockListFlocks).toHaveBeenCalledTimes(1); // pause reset
    await act(async () => { await vi.advanceTimersByTime(1); });
    await act(async () => {});
    expect(mockListFlocks).toHaveBeenCalledTimes(2);
    expect(mockListFlocks).toHaveBeenLastCalledWith({ search: "fo", eligibility: "active", limit: 50, offset: 0 });
  });

  it("hides the previous query's rows immediately, before the pause ends", async () => {
    vi.useFakeTimers();
    render(<FlockPicker label="Pick flock" eligibility="active" required open />);
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    expect(screen.getByText("Flock 01")).toBeInTheDocument();
    const input = screen.getByRole("combobox");
    fireEvent.change(input, { target: { value: "z" } });
    // The old-query row is gone the moment the raw text changes.
    expect(screen.queryByText("Flock 01")).not.toBeInTheDocument();
    await act(async () => { await vi.advanceTimersByTimeAsync(300); });
    expect(screen.queryByText("Flock 01")).not.toBeInTheDocument();
  });
});

// --- replacement paging ------------------------------------------------------

describe("replacement paging (#512 FR-007)", () => {
  it("requests 50 rows at a time, advancing the offset by the raw server count", async () => {
    render(<FlockPicker label="Pick flock" eligibility="active" required open />);
    const user = userEvent.setup();
    expect(await screen.findByText("Flock 01")).toBeInTheDocument();
    expect(mockListFlocks).toHaveBeenLastCalledWith({ search: undefined, eligibility: "active", limit: 50, offset: 0 });

    await user.click(screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") }));
    // Page two: "Flock 51"–"Flock 99" (50 rows, offset 50).
    expect(await screen.findByText("Flock 99")).toBeInTheDocument();
    expect(mockListFlocks).toHaveBeenLastCalledWith({ search: undefined, eligibility: "active", limit: 50, offset: 50 });

    await user.click(screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") }));
    // Page three: "Flock 100"–"Flock 121" (21 rows, offset 100).
    expect(await screen.findByText("Flock 121")).toBeInTheDocument();
    expect(mockListFlocks).toHaveBeenLastCalledWith({ search: undefined, eligibility: "active", limit: 50, offset: 100 });

    // The last window had only 21 rows: no further Load more.
    expect(screen.queryByRole("button", { name: i18n.t("namedEntityPicker:loadMore") })).toBeNull();
    expect(mockListFlocks).toHaveBeenCalledTimes(3);
  });

  it("appends without duplicating rows and keeps page one on screen", async () => {
    render(<FlockPicker label="Pick flock" eligibility="active" required open />);
    const user = userEvent.setup();
    await screen.findByText("Flock 01");
    await user.click(screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") }));
    // The duplicate-name pair ("Flock 51", ids b0 and b50) lands entirely on
    // page two — both rows, each exactly once (dedup by ID never collapses
    // distinct ids that share a name).
    await screen.findByText("Flock 99");
    expect(screen.getAllByText("Flock 51")).toHaveLength(2);
    expect(screen.getByText("Flock 01")).toBeInTheDocument();
  });

  it("advances the offset by raw server count even when a page repeats all page-one IDs", async () => {
    // Page one: 50 rows (a0–a49). Page two: the SAME 50 rows (all IDs
    // repeated). The cursor must advance by 50 (raw server count), NOT by 0
    // (unique appended count). A mutant that advances by appended-unique
    // count would request offset 50 again (infinite loop), and a mutant that
    // removes ID filtering would render every row twice.
    const pageOne = FLOCKS.slice(0, 50);
    const pageTwo = pageOne.slice(); // identical 50 rows
    expect(pageTwo.length).toBe(50);
    mockListFlocks.mockImplementation(async (p) => {
      const params = p ?? {};
      const offset = params.offset ?? 0;
      if (offset === 0) return pageOne;
      if (offset === 50) return pageTwo;
      return [];
    });
    render(<FlockPicker label="Pick flock" eligibility="active" required open />);
    const user = userEvent.setup();
    await screen.findByText("Flock 50");
    // 50 rows, each exactly once.
    expect(screen.getAllByRole("option")).toHaveLength(50);
    expect(screen.getAllByText("Flock 01")).toHaveLength(1);
    // Load more: page two (same 50 IDs) lands. Dedup means NO new rows —
    // still 50 options. The cursor advanced by 50 (raw count), so the next
    // offset is 100 (not 50, which a unique-count mutant would produce).
    await user.click(screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") }));
    await act(async () => {});
    expect(screen.getAllByRole("option")).toHaveLength(50);
    expect(mockListFlocks).toHaveBeenLastCalledWith({ search: undefined, eligibility: "active", limit: 50, offset: 50 });
    // A second Load more requests offset 100 (not 50).
    await user.click(screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") }));
    await act(async () => {});
    expect(mockListFlocks).toHaveBeenLastCalledWith({ search: undefined, eligibility: "active", limit: 50, offset: 100 });
  });

  it("terminates after a final empty page fetched by a second explicit Load more", async () => {
    const rows = FLOCKS.slice(0, 100);
    mockListFlocks.mockImplementation(async (p) => {
      const params = p ?? {};
      return rows.slice(params.offset ?? 0, (params.offset ?? 0) + (params.limit ?? 100));
    });
    render(<FlockPicker label="Pick flock" eligibility="active" required open />);
    const user = userEvent.setup();
    await screen.findByText("Flock 01");
    await user.click(screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") }));
    await screen.findByText("Flock 99");
    expect(mockListFlocks).toHaveBeenCalledTimes(2);
    expect(screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") }));
    await act(async () => {});
    expect(mockListFlocks).toHaveBeenCalledTimes(3);
    expect(mockListFlocks).toHaveBeenLastCalledWith({ search: undefined, eligibility: "active", limit: 50, offset: 100 });
    expect(screen.queryByRole("button", { name: i18n.t("namedEntityPicker:loadMore") })).toBeNull();
    await act(async () => {});
    expect(mockListFlocks).toHaveBeenCalledTimes(3);
  });

  it("search replaces rows from scratch at offset 0, not an extension", async () => {
    render(<FlockPicker label="Pick flock" eligibility="active" required open />);
    const user = userEvent.setup();
    await screen.findByText("Flock 01");
    await user.click(screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") }));
    await screen.findByText("Flock 99");
    await user.type(screen.getByRole("combobox"), "105");
    expect(await screen.findByText("Flock 105")).toBeInTheDocument();
    expect(mockListFlocks).toHaveBeenLastCalledWith({ search: "105", eligibility: "active", limit: 50, offset: 0 });
    expect(screen.queryByText("Flock 01")).not.toBeInTheDocument();
  });
});

// --- loading and no-results visibility ---------------------------------------

describe("loading and no-results visibility", () => {
  it("shows a loading status during the initial replacement", async () => {
    let resolvePage: (rows: Flock[]) => void;
    const gate = new Promise<Flock[]>((r) => { resolvePage = r; });
    mockListFlocks.mockReturnValue(gate);
    render(<FlockPicker label="Pick flock" eligibility="active" required open />);
    // The initial fetch is in flight: the loading status is visible. The
    // stable aria-live region is a sibling but carries no text of its own, so
    // role="status" resolves to the transient loading span unambiguously.
    // The visible loading span (role=status) is present. The stable live region
    // is visually hidden (aria-live) and ignored by default role queries.
    expect(screen.getByRole("status")).toHaveTextContent(i18n.t("namedEntityPicker:loading"));
    resolvePage!(FLOCKS.slice(0, 50));
    await waitFor(() => expect(screen.queryByRole("status")).toBeNull());
  });

  it("shows no-results when a search matches nothing", async () => {
    render(<FlockPicker label="Pick flock" eligibility="active" required open />);
    await screen.findByText("Flock 01");
    const input = screen.getByRole("combobox");
    fireEvent.change(input, { target: { value: "zzzzz" } });
    await waitFor(() =>
      expect(screen.getAllByText(i18n.t("namedEntityPicker:noResults")).length).toBeGreaterThan(0));
  });
});

// --- eligibility-change rediscovery ------------------------------------------

describe("eligibility change (#512 FR-009)", () => {
  it("hides old rows immediately, invalidates old work, and re-discovers under the new eligibility", async () => {
    vi.useFakeTimers();
    function Wrapper({ eligibility }: { eligibility: "active" | "all" }) {
      return <FlockPicker label="Pick flock" eligibility={eligibility} required open />;
    }
    const { rerender } = render(<Wrapper eligibility="active" />);
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    expect(screen.getByText("Flock 01")).toBeInTheDocument();
    const callsBefore = mockListFlocks.mock.calls.length;

    // Change eligibility to "all": rows hide immediately, a new replacement
    // fires after the 250 ms debounce, and the request carries the new key.
    rerender(<Wrapper eligibility="all" />);
    // Old rows are gone the moment the eligibility changes.
    expect(screen.queryByText("Flock 01")).not.toBeInTheDocument();
    // No new request yet (debounce pending).
    expect(mockListFlocks.mock.calls.length).toBe(callsBefore);
    // After the 250 ms pause, the replacement fires under the new key.
    await act(async () => { await vi.advanceTimersByTime(250); });
    await act(async () => {});
    expect(mockListFlocks).toHaveBeenLastCalledWith({ search: undefined, eligibility: "all", limit: 50, offset: 0 });
    // New rows are visible.
    expect(screen.getByText("Flock 01")).toBeInTheDocument();
  });
});

// --- disabled non-interactivity -----------------------------------------------

describe("disabled guard", () => {
  it("a disabled picker: no option commit via pointer, Load more present+disabled, no key commit", async () => {
    const onSnapshot = vi.fn();
    render(<FlockPicker label="Pick flock" eligibility="active" required open disabled onSnapshot={onSnapshot} />);
    await screen.findByText("Flock 01");
    // The input is disabled.
    expect(screen.getByRole("combobox")).toBeDisabled();
    // Load more is PRESENT and disabled (not conditionally removed).
    const loadMore = screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") });
    expect(loadMore).toBeDisabled();
    // Pointer: clicking an option does nothing.
    fireEvent.click(screen.getByText("Flock 01"));
    await act(async () => {});
    expect(screen.getByRole("combobox")).toHaveValue("");
    // Keyboard: the disabled input's native semantics (no focus, no keydown
    // delivery) plus the defensive onKey early return mean no commit.
    const input = screen.getByRole("combobox") as HTMLInputElement;
    expect(input).toBeDisabled();
    input.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true }));
    await act(async () => {});
    input.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
    await act(async () => {});
    expect(input.value).toBe("");
    const committedSnapshots = onSnapshot.mock.calls.filter((c) => c[0].selectionPhase === "committed");
    expect(committedSnapshots).toHaveLength(0);
  });
});

// --- commit, retention, and customers ----------------------------------------

describe("commit and committed-label retention (#512 FR-018)", () => {
  function Capture({ onCommit }: { onCommit: (f: Flock | null) => void }) {
    const [, setSnap] = useState<0 | 1>(0);
    return (
      <FlockPicker label="Pick flock" eligibility="active" required open
        onSnapshot={(s) => { setSnap(1); onCommit(s.committed); }} />
    );
  }

  it("commits a late-sorting flock by pointer, and the snapshot carries the full entity", async () => {
    const onCommit = vi.fn();
    render(<Capture onCommit={onCommit} />);
    const user = userEvent.setup();
    await screen.findByText("Flock 01");
    await user.click(screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") }));
    await screen.findByText("Flock 60");
    await user.click(screen.getByText("Flock 60"));
    await act(async () => {});
    expect(onCommit).toHaveBeenLastCalledWith(expect.objectContaining({ id: "b59", name: "Flock 60", farmId: "farm1", houseId: "h1" }));
  });

  it("keeps the committed label while typing a different query and browsing other pages", async () => {
    render(<FlockPicker label="Pick flock" eligibility="active" required open />);
    const user = userEvent.setup();
    await screen.findByText("Flock 01");
    await user.click(screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") }));
    await screen.findByText("Flock 60");
    await user.click(screen.getByText("Flock 60"));
    await act(async () => {});
    expect(screen.getByRole("combobox")).toHaveValue("Flock 60");
    await user.clear(screen.getByRole("combobox"));
    await user.type(screen.getByRole("combobox"), "110");
    expect(await screen.findByText("Flock 110")).toBeInTheDocument();
    expect(committedName("Flock 60")).toBeInTheDocument();
  });

  it("commits the active option on Enter", async () => {
    render(<FlockPicker label="Pick flock" eligibility="active" required open />);
    await screen.findByText("Flock 01");
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "105" } });
    await screen.findByText("Flock 105");
    const input = screen.getByRole("combobox") as HTMLInputElement;
    input.focus();
    await act(async () => {
      input.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true }));
    });
    await act(async () => {
      input.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
    });
    await act(async () => {});
    expect(input.value).toBe("Flock 105");
  });

  it("searches customers and commits one by pointer", async () => {
    render(<CustomerPicker label="Pick customer" required open />);
    const user = userEvent.setup();
    await user.type(screen.getByRole("combobox"), "55");
    expect(await screen.findByText("Customer 55")).toBeInTheDocument();
    expect(mockListCustomers).toHaveBeenLastCalledWith({ search: "55", limit: 50, offset: 0 });
    await user.click(screen.getByText("Customer 55"));
    await act(async () => {});
    expect(screen.getByRole("combobox")).toHaveValue("Customer 55");
    expect(mockGetCustomer).not.toHaveBeenCalled();
  });
});

// --- snapshot emission stability ---------------------------------------------

describe("snapshot emission (#512 picker-ui)", () => {
  it("does not re-emit when the parent's callback identity changes but state is unchanged", async () => {
    // A parent that re-renders with a fresh onSnapshot callback must not
    // cause an infinite loop: the engine compares the four snapshot fields
    // against the last emitted values and only calls the callback when they
    // change. A fresh callback identity alone must not trigger emission.
    const onSnapshot = vi.fn();
    function Parent() {
      const [tick, setTick] = useState(0);
      return (
        <>
          <FlockPicker label="Pick flock" eligibility="active" required open
            onSnapshot={(s) => onSnapshot(s)} />
          <button onClick={() => setTick(t => t + 1)}>tick</button>
          <span data-testid="tick">{tick}</span>
        </>
      );
    }
    render(<Parent />);
    await screen.findByText("Flock 01");
    const initialCalls = onSnapshot.mock.calls.length;
    expect(initialCalls).toBeGreaterThan(0);
    // Re-render the parent (fresh callback identity, same state).
    fireEvent.click(screen.getByText("tick"));
    await act(async () => {});
    // No additional emission: the four fields are unchanged.
    expect(onSnapshot.mock.calls.length).toBe(initialCalls);
  });
});

// --- A1: commit-after-paging then Load more ---------------------------------

describe("commit retains discovery query/cursor (#512 FR-018)", () => {
  it("commit after paging: Load more extends under the prior query and cursor", async () => {
    // Load two pages (no search), commit a row from page two, then Load more.
    // The extension must use the PRIOR normalizedQuery (null — no search) and
    // the retained cursor (100), not the committed entity's name.
    render(<FlockPicker label="Pick flock" eligibility="active" required open />);
    const user = userEvent.setup();
    await screen.findByText("Flock 01");
    await user.click(screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") }));
    await screen.findByText("Flock 99");
    // Commit "Flock 60" (page two, id b59).
    await user.click(screen.getByText("Flock 60"));
    await act(async () => {});
    // The input shows the committed name.
    expect(screen.getByRole("combobox")).toHaveValue("Flock 60");
    // Load more: must extend under the prior query (null) at cursor 100.
    await user.click(screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") }));
    await act(async () => {});
    expect(mockListFlocks).toHaveBeenLastCalledWith({ search: undefined, eligibility: "active", limit: 50, offset: 100 });
  });
});

// --- A3: Load more stays mounted and disabled during extension ---------------

describe("Load more focus retention (#512 FR-007)", () => {
  it("Load more stays the same mounted DOM button during 'extending', disabled, focus retained", async () => {
    // Deferred promise: page two is gated. The button must stay the SAME mounted
    // DOM node while the extension is pending (no re-mount), be disabled, and
    // retain document.activeElement. A mutant restoring the `!loading` render
    // gate (unmounting the button during "extending") makes this test red.
    let resolvePage: (rows: Flock[]) => void;
    const gate = new Promise<Flock[]>((r) => { resolvePage = r; });
    mockListFlocks.mockImplementation(async (p) => {
      const params = p ?? {};
      if ((params.offset ?? 0) === 0) return FLOCKS.slice(0, 50);
      return gate; // page two is deferred
    });
    render(<FlockPicker label="Pick flock" eligibility="active" required open />);
    const user = userEvent.setup();
    await screen.findByText("Flock 01");
    const original = screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") }) as HTMLButtonElement;
    original.focus();
    expect(document.activeElement).toBe(original);
    // Click: the extension starts (phase → "extending").
    await user.click(original);
    // While the extension is pending (gate unresolved):
    //  1. The button is the EXACT SAME node (not re-mounted).
    //  2. It is disabled.
    //  3. document.activeElement is still the original node.
    const during = screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") });
    expect(during).toBe(original);
    expect(during).toBeDisabled();
    expect(document.activeElement).toBe(original);
    // Resolve the page: 50 rows (full). The button stays mounted and enables.
    resolvePage!(FLOCKS.slice(50, 100));
    await waitFor(() => expect(screen.getByText("Flock 99")).toBeInTheDocument());
    // After resolution: full page → hasMore true → button present and enabled.
    const after = screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") });
    expect(after).toBe(original);
    expect(after).not.toBeDisabled();
  });
});

// --- A4: eligibility change while closed -------------------------------------

describe("eligibility change while closed (#512 FR-009)", () => {
  it("changing eligibility while closed does not request; next open issues exactly one request under the new key", async () => {
    vi.useFakeTimers();
    function Wrapper({ eligibility, open }: { eligibility: "active" | "all"; open: boolean }) {
      return <FlockPicker label="Pick flock" eligibility={eligibility} required open={open} />;
    }
    // Start open under "active", load page one.
    const { rerender } = render(<Wrapper eligibility="active" open={true} />);
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    expect(screen.getByText("Flock 01")).toBeInTheDocument();
    const callsAfterOpen = mockListFlocks.mock.calls.length;
    expect(callsAfterOpen).toBe(1);

    // Close the picker, then change eligibility while closed.
    rerender(<Wrapper eligibility="active" open={false} />);
    rerender(<Wrapper eligibility="all" open={false} />);
    // No new request while closed.
    expect(mockListFlocks.mock.calls.length).toBe(callsAfterOpen);

    // Re-open: the open effect fires (phase "closed" after the eligibility
    // reset), issues exactly one request under the CURRENT key ("all").
    rerender(<Wrapper eligibility="all" open={true} />);
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    expect(mockListFlocks.mock.calls.length).toBe(callsAfterOpen + 1);
    expect(mockListFlocks).toHaveBeenLastCalledWith({ search: undefined, eligibility: "all", limit: 50, offset: 0 });
    // No second (stale or debounced) request.
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });
    expect(mockListFlocks.mock.calls.length).toBe(callsAfterOpen + 1);
  });

  it("eligibility change + open in the same rerender emit exactly one request under the new eligibility", async () => {
    // Both props change in a single commit. The eligibility effect (declared
    // first) must run before the open effect so the open effect never launches
    // a stale request under the old key. Exactly one request, under "all".
    vi.useFakeTimers();
    function Wrapper({ eligibility, open }: { eligibility: "active" | "all"; open: boolean }) {
      return <FlockPicker label="Pick flock" eligibility={eligibility} required open={open} />;
    }
    // Start closed under "active" (no request yet).
    const { rerender } = render(<Wrapper eligibility="active" open={false} />);
    expect(mockListFlocks).not.toHaveBeenCalled();

    // Simultaneous change: eligibility "all" + open=true in one rerender.
    rerender(<Wrapper eligibility="all" open={true} />);
    // The eligibility effect (declared first) sets phase to "debouncing",
    // signals pendingEligibilityKeyRef, and schedules a 250 ms replacement
    // under the NEW key. The open effect (same commit) sees the pending
    // signal and skips. No request at 50 ms (the debounce hasn't fired yet).
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    expect(mockListFlocks.mock.calls.length).toBe(0);
    // At 250 ms the debounce fires: exactly one request under the new key.
    await act(async () => { await vi.advanceTimersByTimeAsync(200); });
    expect(mockListFlocks.mock.calls.length).toBe(1);
    expect(mockListFlocks).toHaveBeenLastCalledWith({ search: undefined, eligibility: "all", limit: 50, offset: 0 });
    // No second (stale) request.
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });
    expect(mockListFlocks.mock.calls.length).toBe(1);
  });
});

// --- US2: stale-state guard (T023/T024/T025) ---------------------------------

describe("US2 stale-state: commit B → external reset → explore → Escape (no resurrection)", () => {
  it("commit B, page resets to A/blank, explore, Escape: picker and page stay synchronized, B does not resurrect", async () => {
    vi.useFakeTimers();
    const snapshots: Array<{ committed: { id: string; name: string } | null; exploring: boolean; canSubmit: boolean }> = [];
    let pageId = "b1"; // page-owned ID

    const A_FLOCK = { id: "a1", name: "Flock A", farmId: "farm1", houseId: "h1", breed: "L", placementDate: "2026-01-01", initialCount: 100, currentBirds: 90, status: "Active" as const, createdByEmail: null, createdAtUtc: null, lastChangedByEmail: null, lastChangedAtUtc: null };

    function Wrapper({ pageResetSignal }: { pageResetSignal: number }) {
      const [pickerOpen, setPickerOpen] = useState(true);
      // The page's committed entity: starts as A, becomes B on commit,
      // resets to A on external signal.
      const [pageCommitted, setPageCommitted] = useState<Flock | null>(A_FLOCK as Flock);
      const [gen, setGen] = useState(0);
      useEffect(() => {
        if (pageResetSignal > 0) {
          pageId = "a1";
          setPageCommitted(A_FLOCK); // page resets to A
          setGen((g) => g + 1); // signal the picker to sync
        }
      }, [pageResetSignal]);

      return (
        <FlockPicker
          label="Pick flock"
          eligibility="active"
          required
          open={pickerOpen}
          controlledCommitted={pageCommitted}
          controlledGeneration={gen}
          onSnapshot={(snap) => {
            snapshots.push({ committed: snap.committed ? { id: snap.committed.id, name: snap.committed.name } : null, exploring: snap.exploring, canSubmit: snap.canSubmit });
            if (snap.committed && !snap.exploring) {
              pageId = snap.committed.id;
              setPageCommitted(snap.committed);
              setGen((g) => g + 1);
            }
          }}
          onEscape={() => setPickerOpen(false)}
        />
      );
    }

    const { rerender } = render(<Wrapper pageResetSignal={0} />);
    // Wait for discovery.
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    // Commit "Flock 60" (B). Load page 2 first.
    fireEvent.click(screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") }));
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    fireEvent.click(screen.getByText("Flock 60"));
    await act(async () => {});
    // Verify: page ID is now B.
    expect(pageId).toBe("b59"); // Flock 60's id

    // Now: the page resets to A (external transition — e.g. a deep link,
    // a dialog reset, or a URL change). The picker's committed state is
    // stale. The page's ID is A.
    rerender(<Wrapper pageResetSignal={1} />);
    await act(async () => {});
    expect(pageId).toBe("a1");

    // Now: the user types (explores) in the picker. The old committed entity
    // (B) is still in the picker's internal state, but exploring=true means
    // canSubmit=false. The page must NOT receive B via onSnapshot.
    const beforeExplore = snapshots.length;
    const input = screen.getByRole("combobox") as HTMLInputElement;
    fireEvent.change(input, { target: { value: "zzz" } });
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    // During exploration: no snapshot with committed=B and exploring=false.
    const postExploreSnaps = snapshots.slice(beforeExplore);
    const staleBCommits = postExploreSnaps.filter((s) => s.committed?.id === "b59" && !s.exploring);
    expect(staleBCommits).toHaveLength(0);

    // Escape: restores the committed label (if any) or blanks. Since the page
    // reset cleared the selection, the picker should NOT resurrect B.
    fireEvent.keyDown(input, { key: "Escape" });
    await act(async () => {});
    // After Escape: no new commit of B.
    const afterEscapeSnaps = snapshots.slice(beforeExplore);
    const resurrectedB = afterEscapeSnaps.filter((s) => s.committed?.id === "b59");
    expect(resurrectedB).toHaveLength(0);
    // The page ID is still A.
    expect(pageId).toBe("a1");
  });
});

// --- US2: outside-click closes the picker (T023) ------------------------------

describe("US2 outside-click: picker closes, exploration cancelled", () => {
  it("clicking outside the picker closes it (onOutsideClick called)", async () => {
    vi.useFakeTimers();
    const onOutsideClick = vi.fn();
    render(
      <FlockPicker
        label="Pick flock"
        eligibility="active"
        required
        open
        onOutsideClick={onOutsideClick}
      />,
    );
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    // Click outside the picker container.
    const outside = document.createElement("div");
    document.body.appendChild(outside);
    fireEvent.mouseDown(outside);
    expect(onOutsideClick).toHaveBeenCalledTimes(1);
    outside.remove();
  });

  it("clicking inside the picker does NOT call onOutsideClick", async () => {
    vi.useFakeTimers();
    const onOutsideClick = vi.fn();
    render(
      <FlockPicker
        label="Pick flock"
        eligibility="active"
        required
        open
        onOutsideClick={onOutsideClick}
      />,
    );
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    // Click inside the picker (on an option).
    const option = screen.getByText("Flock 01");
    fireEvent.mouseDown(option);
    expect(onOutsideClick).not.toHaveBeenCalled();
  });
});

// --- US2: Escape restores and closes (T023) ----------------------------------

describe("US2 Escape: restores committed label, closes picker", () => {
  it("Escape after exploration restores the committed name and calls onEscape", async () => {
    vi.useFakeTimers();
    const onEscape = vi.fn();
    render(
      <FlockPicker
        label="Pick flock"
        eligibility="active"
        required
        open
        onEscape={onEscape}
      />,
    );
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    // Commit "Flock 01".
    fireEvent.click(screen.getByText("Flock 01"));
    await act(async () => {});
    // Explore: type something different.
    const input = screen.getByRole("combobox") as HTMLInputElement;
    fireEvent.change(input, { target: { value: "zzz" } });
    await act(async () => { await vi.advanceTimersByTimeAsync(300); });
    // The input shows the exploration text.
    expect(input).toHaveValue("zzz");
    // Escape: restore committed label.
    fireEvent.keyDown(input, { key: "Escape" });
    await act(async () => {});
    // The input shows the committed name again.
    expect(input).toHaveValue("Flock 01");
    // onEscape was called (the page will close the picker).
    expect(onEscape).toHaveBeenCalledTimes(1);
  });

  it("Escape with no commit blanks the input", async () => {
    vi.useFakeTimers();
    const onEscape = vi.fn();
    render(
      <FlockPicker
        label="Pick flock"
        eligibility="active"
        required
        open
        onEscape={onEscape}
      />,
    );
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    // Explore without committing.
    const input = screen.getByRole("combobox") as HTMLInputElement;
    fireEvent.change(input, { target: { value: "hello" } });
    await act(async () => { await vi.advanceTimersByTimeAsync(300); });
    expect(input).toHaveValue("hello");
    // Escape: no commit, so blank.
    fireEvent.keyDown(input, { key: "Escape" });
    await act(async () => {});
    expect(input).toHaveValue("");
    expect(onEscape).toHaveBeenCalledTimes(1);
  });
});

// ─── T023: ARIA contract, stable IDs, native behavior, Down-extension, clear ───

describe("T023: ARIA contract and interaction semantics", () => {
  const fiveFlocks = Array.from({ length: 5 }, (_, i) => ({
    id: `f${i}`, name: `Flock ${i}`, farmId: "fm", houseId: "h",
    breed: "L", placementDate: "2026-01-01", initialCount: 10, currentBirds: 9,
    status: "Active" as const, createdByEmail: null, createdAtUtc: null,
    lastChangedByEmail: null, lastChangedAtUtc: null,
  }));

  beforeEach(() => {
    vi.useFakeTimers();
    mockListFlocks.mockImplementation(async (params: any) => {
      const offset = params.offset ?? 0;
      return fiveFlocks.slice(offset, offset + 5) as any;
    });
  });

  afterEach(() => { vi.useRealTimers(); });

  function openPicker(required = true, disabled = false) {
    render(
      <FlockPicker label="Pick" eligibility="active" required={required} disabled={disabled} open trigger={<button>open</button>} />
    );
    return { input: screen.getByRole("combobox") };
  }

  it("T023-1: aria-expanded=true and aria-controls point to the listbox when open", async () => {
    const { input } = openPicker();
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    expect(input).toHaveAttribute("aria-expanded", "true");
    const controls = input.getAttribute("aria-controls")!;
    expect(controls).toBeTruthy();
    expect(screen.getByRole("listbox", { hidden: true }).id).toBe(controls);
  });

  it("T023-2: options carry aria-selected only on the committed entity", async () => {
    openPicker();
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    const options = screen.getAllByRole("option");
    expect(options.length).toBe(5);
    // No commit yet → no aria-selected
    options.forEach((o) => expect(o).toHaveAttribute("aria-selected", "false"));
    // Commit the first option
    fireEvent.click(options[0]);
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    const optionsAfter = screen.getAllByRole("option");
    expect(optionsAfter[0]).toHaveAttribute("aria-selected", "true");
    optionsAfter.slice(1).forEach((o) => expect(o).toHaveAttribute("aria-selected", "false"));
  });

  it("T023-3: aria-activedescendant tracks arrow navigation", async () => {
    const { input } = openPicker();
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    expect(input).not.toHaveAttribute("aria-activedescendant");
    // Derive the ID prefix from the listbox's aria-controls reference
    const listboxId = input.getAttribute("aria-controls")!;
    const prefix = listboxId.replace("-listbox", "");
    fireEvent.keyDown(input, { key: "ArrowDown" });
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    expect(input.getAttribute("aria-activedescendant")).toBe(`${prefix}-opt-f0`);
    fireEvent.keyDown(input, { key: "ArrowDown" });
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    expect(input.getAttribute("aria-activedescendant")).toBe(`${prefix}-opt-f1`);
  });

  it("T023-4: aria-required is present for required pickers, absent for optional", async () => {
    const { input: reqInput } = openPicker(true);
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    expect(reqInput).toHaveAttribute("aria-required", "true");
    cleanup();
    const { input: optInput } = openPicker(false);
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    expect(optInput).not.toHaveAttribute("aria-required");
  });

  // cleanup is provided by @testing-library/react's auto-cleanup; the explicit
  // call above unmounts before the second render in the same test.

  it("T023-5: disabled picker: input is disabled, options are not clickable", async () => {
    const { input } = openPicker(true, true);
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    expect(input).toBeDisabled();
    const options = screen.getAllByRole("option");
    // Clicking an option on a disabled picker does not commit
    fireEvent.click(options[0]);
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    // No commit occurred (input value unchanged)
    expect(input).toHaveValue("");
  });

  it("T023-6: Home/End are NOT intercepted (FR-031: native input behavior)", async () => {
    const { input } = openPicker();
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    // Navigate to middle so activeDescendant is set
    fireEvent.keyDown(input, { key: "ArrowDown" });
    fireEvent.keyDown(input, { key: "ArrowDown" });
    fireEvent.keyDown(input, { key: "ArrowDown" });
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    const activeBefore = input.getAttribute("aria-activedescendant");
    expect(activeBefore).toBeTruthy();
    // Dispatch Home: the engine must NOT preventDefault (native behavior)
    const homeEvent = new KeyboardEvent("keydown", { key: "Home", bubbles: true, cancelable: true });
    input.dispatchEvent(homeEvent);
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    // Not intercepted: defaultPrevented is false
    expect(homeEvent.defaultPrevented).toBe(false);
    // Active descendant unchanged (no list navigation)
    expect(input.getAttribute("aria-activedescendant")).toBe(activeBefore);
    // Same for End
    const endEvent = new KeyboardEvent("keydown", { key: "End", bubbles: true, cancelable: true });
    input.dispatchEvent(endEvent);
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    expect(endEvent.defaultPrevented).toBe(false);
    expect(input.getAttribute("aria-activedescendant")).toBe(activeBefore);
  });

  it("T023-7: Down Arrow at loaded end with hasMore loads next page", async () => {
    // Override mock: 50 items per page (limit=50), 2 pages
    const page0 = Array.from({ length: 50 }, (_, i) => ({ ...fiveFlocks[0], id: `p0-${i}`, name: `Page0 Flock ${i}` }));
    const page1 = Array.from({ length: 50 }, (_, i) => ({ ...fiveFlocks[0], id: `p1-${i}`, name: `Page1 Flock ${i}` }));
    mockListFlocks.mockImplementation(async (params: any) => {
      const offset = params.offset ?? 0;
      if (offset === 0) return page0 as any;
      if (offset === 50) return page1 as any;
      return [] as any;
    });
    const { input } = openPicker();
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    // Navigate to the last option (50 items)
    for (let i = 0; i < 50; i++) {
      fireEvent.keyDown(input, { key: "ArrowDown" });
      await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    }
    // At the end: next ArrowDown should trigger loadMore (not move activation)
    const callsBefore = mockListFlocks.mock.calls.length;
    fireEvent.keyDown(input, { key: "ArrowDown" });
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    // A second fetch was issued for offset 50
    expect(mockListFlocks.mock.calls.length).toBeGreaterThan(callsBefore);
    const lastCall = mockListFlocks.mock.calls[mockListFlocks.mock.calls.length - 1];
    expect(lastCall[0]).toMatchObject({ offset: 50 });
  });

  it("T023-8: optional clear button commits blank and fires onClear", async () => {
    const onClear = vi.fn();
    const onSnapshot = vi.fn();
    render(
      <FlockPicker label="Pick" eligibility="active" required={false} open trigger={<button>open</button>} onSnapshot={onSnapshot} onClear={onClear} />
    );
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    // Commit an option
    fireEvent.click(screen.getAllByRole("option")[0]);
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    // Clear button is now visible
    const clearBtn = screen.getByRole("button", { name: i18n.t("namedEntityPicker:clear") });
    fireEvent.click(clearBtn);
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    expect(onClear).toHaveBeenCalledTimes(1);
    // Input is now blank
    expect(screen.getByRole("combobox")).toHaveValue("");
  });

  it("T023-9: required picker has no clear button", async () => {
    openPicker(true);
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    fireEvent.click(screen.getAllByRole("option")[0]);
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    expect(screen.queryByRole("button", { name: i18n.t("namedEntityPicker:clear") })).toBeNull();
  });

  it("T023-10: outside mousedown on a write control cancels exploration and fires onOutsideClick", async () => {
    const onOutsideClick = vi.fn();
    render(
      <FlockPicker label="Pick" eligibility="active" required open trigger={<button>open</button>} onOutsideClick={onOutsideClick} />
    );
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    const input = screen.getByRole("combobox");
    // Type to explore
    fireEvent.change(input, { target: { value: "Flo" } });
    await act(async () => { await vi.advanceTimersByTimeAsync(100); });
    // Click outside
    fireEvent.mouseDown(document.body);
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    expect(onOutsideClick).toHaveBeenCalledTimes(1);
    // Text is restored (committed or blank)
    expect(input).toHaveValue("");
  });

  it("T023-11: stable IDs across re-renders (no identity churn)", async () => {
    const { input } = openPicker();
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    const id1 = input.id;
    const listboxId = input.getAttribute("aria-controls")!;
    // Trigger a re-render by typing
    fireEvent.change(input, { target: { value: "" } });
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    expect(screen.getByRole("combobox").id).toBe(id1);
    expect(screen.getByRole("combobox").getAttribute("aria-controls")).toBe(listboxId);
  });

  it("T023-12: closed state: label associated to trigger via htmlFor/id; open swaps trigger for combobox", () => {
    render(
      <FlockPicker label="Pick Flock" eligibility="active" required open={false} trigger={<button type="button">My Trigger</button>} />
    );
    // The label is programmatically associated: getByLabelText returns the ACTUAL trigger
    const labeledTrigger = screen.getByLabelText("Pick Flock");
    expect(labeledTrigger.tagName).toBe("BUTTON");
    expect(labeledTrigger).toHaveTextContent("My Trigger");
    // The association is the cloned trigger's stable id — the label's htmlFor
    // points at it, and the trigger carries that exact id.
    const labelEl = screen.getByText("Pick Flock");
    expect(labelEl).toHaveAttribute("for", labeledTrigger.id);
    expect(labeledTrigger.id).toBeTruthy();
    // The trigger's accessible name is label + current value via aria-labelledby
    // referencing [label-id, value-id] (the value child wrapped in a stable span).
    const labelledby = labeledTrigger.getAttribute("aria-labelledby")!.split(" ");
    expect(labelledby).toHaveLength(2);
    expect(labelledby[0]).toBe(labelEl.id);
    expect(document.getElementById(labelledby[1])?.textContent).toBe("My Trigger");
    expect(labeledTrigger.getAttribute("aria-labelledby")).toBe(`${labelledby[0]} ${labelledby[1]}`);
    // Accessible role name includes BOTH the label and the current value
    expect(screen.getByRole("button", { name: /Pick Flock/ })).toBe(labeledTrigger);
    expect(screen.getByRole("button", { name: /My Trigger/ })).toBe(labeledTrigger);
    // Exactly one control (the trigger)
    const buttons = screen.getAllByRole("button");
    expect(buttons.length).toBe(1);
    // No combobox or listbox in closed state
    expect(screen.queryByRole("combobox")).toBeNull();
    expect(screen.queryByRole("listbox")).toBeNull();
    // Open state: label + combobox in same slot, NO trigger button (swap)
    cleanup();
    render(
      <FlockPicker label="Pick Flock" eligibility="active" required open trigger={<button type="button">My Trigger</button>} />
    );
    expect(screen.getByText("Pick Flock")).toBeInTheDocument();
    const combo = screen.getByRole("combobox");
    expect(combo).toBeInTheDocument();
    // The label is now associated to the combobox (htmlFor = input id)
    expect(screen.getByLabelText("Pick Flock")).toBe(combo);
    // The trigger button is ABSENT in open state (swapped, not duplicated)
    expect(screen.queryByRole("button", { name: "My Trigger" })).toBeNull();
  });

  it("T023-13: closed state with no trigger element renders no orphan htmlFor", () => {
    // FlockPicker's trigger is optional on the typed adapter; the engine must
    // never render a <label htmlFor> pointing at a trigger id that does not
    // exist. The valid-trigger association (T023-12) is unchanged.
    render(
      <FlockPicker label="Pick Flock" eligibility="active" required open={false} />
    );
    // Inspect the closed picker DOM directly: native <label> has no implicit
    // ARIA role named "label", so a role query would be vacuous here — the
    // accessible association proof stays in T023-12.
    const labelEl = screen.getByText("Pick Flock");
    const container = labelEl.closest(".named-picker");
    expect(container).not.toBeNull();
    expect(container!.querySelector("label")).toBeNull();
    // The visible label text is still rendered, as a non-label element with
    // the same styling hook, and no element carries an orphan htmlFor.
    expect(labelEl.tagName).toBe("SPAN");
    expect(labelEl).toHaveClass("named-picker-label");
    expect(container!.querySelector("[for]")).toBeNull();
  });
});

// ─── T030: US3 — stale async completion, retry, focus, announcements ───

describe("T030: US3 stale completion and recovery (FR-023/024/027/028)", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    mockListFlocks.mockImplementation(async (p) => serveFlocks(p));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("T030-1: a late success from a SUPERSEDED replacement never paints rows (generation ownership, success path)", async () => {
    let releaseFirst!: (rows: Flock[]) => void;
    mockListFlocks.mockImplementationOnce(
      () => new Promise<Flock[]>((r) => { releaseFirst = r; }),
    );
    render(<FlockPicker label="Pick" eligibility="active" required open />);
    // Initial (unfiltered) replacement is in flight; user types immediately.
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "Flock 99" } });
    // The debounced replacement for "Flock 99" goes out under the mock impl.
    await act(async () => { await vi.advanceTimersByTimeAsync(300); });
    expect(mockListFlocks).toHaveBeenLastCalledWith(expect.objectContaining({ search: "Flock 99" }));
    // The FIRST (superseded, unfiltered) response lands LATE: its rows must not
    // replace the newer intent's state.
    await act(async () => { releaseFirst([F("a0", "Flock 01")]); });
    await act(async () => {});
    // The visible rows belong to the newest intent only.
    expect(screen.queryByText("Flock 01")).not.toBeInTheDocument();
  });

  it("T030-2: a late FAILURE from a superseded replacement never paints an error (generation ownership, catch path)", async () => {
    let rejectFirst!: (err: Error) => void;
    mockListFlocks.mockImplementationOnce(
      () => new Promise<Flock[]>((_r, rej) => { rejectFirst = rej; }),
    );
    render(<FlockPicker label="Pick" eligibility="active" required open />);
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "Flock 99" } });
    await act(async () => { await vi.advanceTimersByTimeAsync(300); });
    // The superseded request fails LATE — the error must not surface.
    await act(async () => { rejectFirst(new Error("boom")); });
    await act(async () => {});
    expect(screen.queryByText(i18n.t("namedEntityPicker:searchFailed"))).not.toBeInTheDocument();
    // The newest intent's rows are what the user sees.
    expect(screen.getByText("Flock 99")).toBeInTheDocument();
  });

  it("T030-5: a stale extension failure never paints an error after a NEWER intent (generation ownership, extension catch path)", async () => {
    vi.useRealTimers();
    mockListFlocks.mockReset();
    let rejectExt1!: (err: Error) => void;
    mockListFlocks
      .mockResolvedValueOnce(FLOCKS.slice(0, 50))
      .mockReturnValueOnce(new Promise<Flock[]>((_r, rej) => { rejectExt1 = rej; })); // extension 1 (slow, failing)
    render(<FlockPicker label="Pick" eligibility="active" required open />);
    await waitFor(() => { expect(screen.getByText("Flock 01")).toBeInTheDocument(); });
    fireEvent.click(screen.getByRole("button", { name: i18n.t("namedEntityPicker:loadMore") })); // extension 1 starts
    await waitFor(() => { expect(mockListFlocks).toHaveBeenCalledTimes(2); });
    // A NEWER intent: typing a new query hides the rows and claims a newer
    // generation; the debounced replacement then succeeds.
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "Flock 99" } });
    mockListFlocks.mockResolvedValueOnce([F("z1", "Flock 99")]);
    await act(async () => { await new Promise((r) => setTimeout(r, 300)); });
    await act(async () => {});
    // The superseded extension's LATE failure must not surface an error.
    await act(async () => { rejectExt1(new Error("net down")); });
    await act(async () => {});
    expect(screen.queryByText(i18n.t("namedEntityPicker:loadMoreFailed"))).toBeNull();
    // The newest intent's rows are what the user sees.
    expect(screen.getByRole("option", { name: "Flock 99" })).toBeInTheDocument();
  });

  it("T030-7: an external reset (controlled transition) that lands after exploration commits the NEWER entity — the explored query never resurrects (selection generation race)", async () => {
    vi.useRealTimers();
    mockListFlocks.mockReset();
    mockListFlocks.mockImplementation(async (p) => serveFlocks(p));
    const onSnapshot = vi.fn();
    // The page contract (picker-ui.md): a caller passes a FULL entity once
    // resolved — the controlled path carries the typed entity, never a bare
    // ID. The race this drives: a newer external transition (page reset /
    // dialog reset / create) lands after exploration began; it must win and
    // the stale explored text must not commit.
    const view = ({ gen }: { gen: number }) => (
      <FlockPicker label="Pick" eligibility="active" required open
        controlledCommitted={gen === 1 ? F("a0", "Flock 01") : F("a5", "Flock 06")}
        controlledGeneration={gen} onSnapshot={onSnapshot} />
    );
    const r = render(view({ gen: 1 }));
    // Wait for the controlled commit + discovery to settle.
    await waitFor(() => { expect(onSnapshot.mock.calls.at(-1)?.[0]?.committed?.id).toBe("a0"); });
    // Explore (typing never commits).
    const input = screen.getByRole("combobox");
    fireEvent.change(input, { target: { value: "zzz" } });
    expect(onSnapshot.mock.calls.at(-1)?.[0]?.exploring).toBe(true);
    // A NEWER external transition arrives (page reset / dialog reset): it must
    // win — the committed entity becomes the newer one and the stale explored
    // text cannot commit.
    r.rerender(view({ gen: 2 }));
    await waitFor(() => { expect(onSnapshot.mock.calls.at(-1)?.[0]?.committed?.id).toBe("a5"); });
    expect(onSnapshot.mock.calls.at(-1)?.[0]?.exploring).toBe(false);
    // canSubmit reflects the newer committed state, not the stale exploration.
    expect(onSnapshot.mock.calls.at(-1)?.[0]?.canSubmit).toBe(true);
  });
});

// ─── T035: US3 exact-ID resolution and admission rules ───

describe("T035: exact-ID transitions and admission (FR-019/FR-033)", () => {
  beforeEach(() => { vi.useRealTimers(); mockListFlocks.mockImplementation(async (p) => serveFlocks(p)); });

  it("a row-owned exact identity NOT in the discovery window resolves via GET and commits the exact entity — never a first-result substitution", async () => {
    // The page's listFlocks (discovery) returns only FLOCK[0]; the row owns a
    // DIFFERENT flock that only the exact GET can resolve. The picker must
    // commit the exact entity — not silently substitute the first result.
    const exact = F("f-exact", "Late Row Flock", "Archived");
    vi.mocked(getFlock).mockReset();
    vi.mocked(getFlock).mockResolvedValue(exact);
    mockListFlocks.mockReset();
    mockListFlocks.mockResolvedValue([FLOCKS[0]]); // discovery never contains f-exact
    const onSnapshot = vi.fn();
    render(<FlockPicker label="Pick" eligibility="all" required open
      requestedId="f-exact" controlledGeneration={1} onSnapshot={onSnapshot} />);
    // The exact GET was used (a read) — the list never contained it.
    await waitFor(() => { expect(vi.mocked(getFlock)).toHaveBeenCalledWith("f-exact"); });
    // The committed entity is the EXACT resolved identity.
    await waitFor(() => { expect(onSnapshot.mock.calls.at(-1)?.[0]?.committed?.id).toBe("f-exact"); });
    expect(onSnapshot.mock.calls.at(-1)?.[0]?.committed?.name).toBe("Late Row Flock");
  });

  it("an unavailable exact ID (scoped 404 on the GET) enters the unavailable phase — never the first result", async () => {
    vi.mocked(getFlock).mockReset();
    vi.mocked(getFlock).mockRejectedValue(new Error("not found"));
    mockListFlocks.mockReset();
    mockListFlocks.mockResolvedValue([FLOCKS[0]]);
    const onSnapshot = vi.fn();
    render(<FlockPicker label="Pick" eligibility="all" required open
      requestedId="f-missing" controlledGeneration={1} onSnapshot={onSnapshot} />);
    // The phase is unavailable; the committed entity is NOT the first result.
    await waitFor(() => { expect(onSnapshot.mock.calls.at(-1)?.[0]?.selectionPhase).toBe("unavailable"); });
    const last = onSnapshot.mock.calls.at(-1)?.[0];
    expect(last.committed).toBeNull();
    expect(last.canSubmit).toBe(false);
  });

  it("clearing requestedId with the same controlled generation invalidates the exact read and resets unavailable state", async () => {
    let rejectExact!: (reason?: unknown) => void;
    mockGetFlock.mockReturnValue(new Promise((_resolve, reject) => { rejectExact = reject; }));
    const onSnapshot = vi.fn();
    const view = (requestedId: string | null) => (
      <FlockPicker label="Pick" eligibility="all" open={false}
        requestedId={requestedId} controlledGeneration={1} onSnapshot={onSnapshot}
        trigger={<button type="button">trigger</button>} />
    );
    const r = render(view("f-stale"));
    await waitFor(() => expect(mockGetFlock).toHaveBeenCalledWith("f-stale"));
    await waitFor(() => {
      const last = onSnapshot.mock.calls.at(-1)?.[0];
      expect(last.selectionPhase).toBe("resolving");
      expect(last.canSubmit).toBe(false);
    });

    r.rerender(view(null));
    await waitFor(() => {
      const last = onSnapshot.mock.calls.at(-1)?.[0];
      expect(last.selectionPhase).toBe("blank");
      expect(last.committed).toBeNull();
      expect(last.canSubmit).toBe(true);
    });
    expect(screen.queryByRole("alert")).toBeNull();

    await act(async () => { rejectExact(new Error("late 404")); });
    expect(onSnapshot.mock.calls.at(-1)?.[0]?.selectionPhase).toBe("blank");
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("an initial requestedId exact success commits with the resolved name visible and no exploration — the write identity is the resolved entity", async () => {
    // Causal guard: the requestedId success path must synchronize the discovery
    // window to the resolved identity. If it only sets committedText and
    // selection.entity (the shipped bug), rawQuery still carries the STALE
    // typed text, so exploring stays true and canSubmit stays false even
    // though the exact identity is committed — the page's write guard blocks
    // a perfectly good save, and Escape would resurrect the stale text.
    vi.mocked(getFlock).mockReset();
    const exact = F("f-exact2", "Row Owned House", "Archived");
    mockGetFlock.mockResolvedValue(exact);
    mockListFlocks.mockReset();
    mockListFlocks.mockResolvedValue([FLOCKS[0]]); // discovery never contains f-exact2
    const onSnapshot = vi.fn();
    render(<FlockPicker label="Pick" eligibility="all" required open
      requestedId="f-exact2" controlledGeneration={1} onSnapshot={onSnapshot} />);
    await waitFor(() => { expect(mockGetFlock).toHaveBeenCalledWith("f-exact2"); });
    // The EXACT resolved entity is committed — never a first-result substitution.
    await waitFor(() => { expect(onSnapshot.mock.calls.at(-1)?.[0]?.committed?.id).toBe("f-exact2"); });
    const last = onSnapshot.mock.calls.at(-1)?.[0];
    // The same exact entity, committed, with NO exploration in progress.
    expect(last.committed).toBe(exact);
    expect(last.selectionPhase).toBe("committed");
    expect(last.exploring).toBe(false);
    expect(last.canSubmit).toBe(true);
    // The input shows the resolved name — no stale typed text, no raw ID.
    expect(screen.getByRole("combobox")).toHaveValue("Row Owned House");
    expect(screen.queryByText("f-exact2")).toBeNull();
    // No stale write identity: the only committed identity is the resolved one.
    expect(screen.getByRole("combobox")).toHaveValue(exact.name);
  });

  it("an unavailable exact Retry success commits with the resolved name visible and no exploration — the write identity is the resolved entity", async () => {
    // Causal guard for the retryUnavailable success path: it must do the SAME
    // discovery synchronization as the initial requestedId success. A Retry
    // that only flips selection to committed leaves rawQuery stale, so the
    // picker wedges on exploring=true / canSubmit=false after a RECOVERED read
    // — the page keeps withholding a save that is now safe to make.
    vi.mocked(getFlock).mockReset();
    const recovered = F("f-retry", "Recovered House", "Archived");
    mockGetFlock.mockRejectedValueOnce(new Error("not found"));
    mockGetFlock.mockResolvedValue(recovered);
    mockListFlocks.mockReset();
    mockListFlocks.mockResolvedValue([FLOCKS[0]]);
    const onSnapshot = vi.fn();
    render(<FlockPicker label="Pick" eligibility="all" required open
      requestedId="f-retry" controlledGeneration={1} onSnapshot={onSnapshot} />);
    // The first read 404s → explicit unavailable (never a first result).
    await waitFor(() => { expect(onSnapshot.mock.calls.at(-1)?.[0]?.selectionPhase).toBe("unavailable"); });
    expect(onSnapshot.mock.calls.at(-1)?.[0]?.committed).toBeNull();
    expect(onSnapshot.mock.calls.at(-1)?.[0]?.canSubmit).toBe(false);
    // Retry re-issues the exact GET; it now succeeds.
    const getFlockBefore = mockGetFlock.mock.calls.length;
    fireEvent.click(screen.getByRole("button", { name: i18n.t("namedEntityPicker:retry") }));
    await waitFor(() => { expect(mockGetFlock.mock.calls.length).toBe(getFlockBefore + 1); });
    // The SAME exact entity is committed — not the first result, not the raw ID.
    await waitFor(() => { expect(onSnapshot.mock.calls.at(-1)?.[0]?.committed?.id).toBe("f-retry"); });
    const last = onSnapshot.mock.calls.at(-1)?.[0];
    expect(last.committed).toBe(recovered);
    expect(last.selectionPhase).toBe("committed");
    expect(last.exploring).toBe(false);
    expect(last.canSubmit).toBe(true);
    // The input shows the resolved name — no stale text, no raw ID.
    expect(screen.getByRole("combobox")).toHaveValue("Recovered House");
    expect(screen.queryByText("f-retry")).toBeNull();
    expect(screen.getByRole("combobox")).toHaveValue(recovered.name);
  });

  // #512 US3 remediation — the engine used to render unavailable/Retry ONLY
  // in the open branch (`if (!open) return trigger-only`). A page whose
  // picker stays CLOSED (History/Feed row-owned ids resolved without opening
  // the combobox) or DISABLED (Water's edit-locked capture picker) got no
  // recovery affordance at all when the exact GET failed — the trigger just
  // showed its blank/fallback text forever, with no way back in.
  it("a requestedId GET failure while CLOSED still renders the translated unavailable status and an adjacent Retry — Retry re-issues the GET only and can recover", async () => {
    vi.mocked(getFlock).mockReset();
    vi.mocked(getFlock).mockRejectedValueOnce(new Error("not found"));
    mockListFlocks.mockReset();
    mockListFlocks.mockResolvedValue([FLOCKS[0]]);
    const recovered = F("f-closed", "Recovered Closed House", "Archived");
    const onSnapshot = vi.fn();
    render(<FlockPicker label="Pick" eligibility="all" required
      open={false}
      requestedId="f-closed" controlledGeneration={1} onSnapshot={onSnapshot}
      trigger={<button type="button">trigger</button>} />);

    await waitFor(() => { expect(onSnapshot.mock.calls.at(-1)?.[0]?.selectionPhase).toBe("unavailable"); });
    // Translated unavailable status, adjacent to the trigger, no raw ID.
    const alert = screen.getByRole("alert");
    expect(alert).toHaveTextContent(/unavailable/i);
    expect(screen.queryByText("f-closed")).toBeNull();
    // The Retry is keyboard-reachable (a real <button>) beside the status.
    const retryBtn = screen.getByRole("button", { name: i18n.t("namedEntityPicker:retry") });

    // Retry re-issues ONLY the exact GET — never a discovery request, never
    // the trigger's own onClick (the page owns that separately).
    const getFlockBefore = mockGetFlock.mock.calls.length;
    mockGetFlock.mockResolvedValueOnce(recovered);
    fireEvent.click(retryBtn);
    await waitFor(() => { expect(mockGetFlock.mock.calls.length).toBe(getFlockBefore + 1); });
    await waitFor(() => { expect(onSnapshot.mock.calls.at(-1)?.[0]?.selectionPhase).toBe("committed"); });
    const last = onSnapshot.mock.calls.at(-1)?.[0];
    expect(last.committed).toBe(recovered);
    expect(last.canSubmit).toBe(true);
    // Recovery clears the unavailable status.
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("a requestedId GET failure while DISABLED still renders unavailable + Retry, and Retry works — the exact re-resolution is not ordinary discovery/selection", async () => {
    vi.mocked(getFlock).mockReset();
    vi.mocked(getFlock).mockRejectedValueOnce(new Error("not found"));
    mockListFlocks.mockReset();
    mockListFlocks.mockResolvedValue([FLOCKS[0]]);
    const recovered = F("f-disabled", "Recovered Disabled House", "Archived");
    const onSnapshot = vi.fn();
    render(<FlockPicker label="Pick" eligibility="all" required
      open disabled
      requestedId="f-disabled" controlledGeneration={1} onSnapshot={onSnapshot} />);

    await waitFor(() => { expect(onSnapshot.mock.calls.at(-1)?.[0]?.selectionPhase).toBe("unavailable"); });
    expect(screen.getByRole("alert")).toHaveTextContent(/unavailable/i);
    // Ordinary interaction stays disabled: the combobox input itself is inert.
    expect(screen.getByRole("combobox")).toBeDisabled();

    // Retry is exempt from `disabled` — it re-resolves the FIXED identity,
    // not ordinary discovery/selection.
    const getFlockBefore = mockGetFlock.mock.calls.length;
    mockGetFlock.mockResolvedValueOnce(recovered);
    fireEvent.click(screen.getByRole("button", { name: i18n.t("namedEntityPicker:retry") }));
    await waitFor(() => { expect(mockGetFlock.mock.calls.length).toBe(getFlockBefore + 1); });
    await waitFor(() => { expect(onSnapshot.mock.calls.at(-1)?.[0]?.selectionPhase).toBe("committed"); });
    expect(onSnapshot.mock.calls.at(-1)?.[0]?.committed).toBe(recovered);
  });
});
