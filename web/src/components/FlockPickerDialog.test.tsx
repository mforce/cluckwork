// web/src/components/FlockPickerDialog.test.tsx
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ComponentProps } from "react";
import { FlockPickerDialog } from "./FlockPickerDialog";
import { listFlocks } from "../api/cluckwork";
import type { Flock } from "../api/cluckwork";
import { NO_RECORD_HISTORY } from "../test/fixtures";

vi.mock("../api/cluckwork", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/cluckwork")>();
  return { ...actual, listFlocks: vi.fn() };
});
const mockFlocks = vi.mocked(listFlocks);

const flock = (id: string): Flock => ({
  ...NO_RECORD_HISTORY,
  id, farmId: "f", houseId: "h", name: `Flock ${id}`, breed: "ISA",
  placementDate: "2026-01-01", initialCount: 100, currentBirds: 98, status: "Active",
});

function renderDialog(props: Partial<ComponentProps<typeof FlockPickerDialog>> = {}) {
  return render(
    <FlockPickerDialog
      open onClose={vi.fn()} scope={{ kind: "all" }} accessibleCount={3}
      onPickAll={vi.fn()} onPickFlock={vi.fn()}
      {...props}
    />,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  mockFlocks.mockResolvedValue([]);
});
afterEach(() => {
  vi.useRealTimers();
});

describe("FlockPickerDialog (#918 round 4 — extracted from Dashboard.tsx)", () => {
  it("pins All flocks above the scrolling results", async () => {
    mockFlocks.mockResolvedValue([flock("f1"), flock("f2")]);
    renderDialog();
    const results = await screen.findByRole("list", { name: "Accessible flocks" });
    await waitFor(() => expect(within(results).queryAllByRole("button").length).toBeGreaterThan(0));
    expect(within(results).queryByRole("button", { name: /^All flocks/ })).toBeNull();
    expect(screen.getByRole("button", { name: /^All flocks/ })).toBeInTheDocument();
  });

  // #918 — Codex review, round 3, finding 1. The dialog's search used to
  // filter only the first 500 already-loaded flocks, so a flock past that
  // page was unreachable on a large farm. The unfiltered first page here is
  // a fixed 50-row fixture that never contains the 501st-equivalent flock,
  // only reachable once the dialog issues a real server call carrying the
  // search term.
  it("reaches a flock beyond the first page by searching the server, not by filtering an already-loaded list", async () => {
    const user = userEvent.setup();
    const firstPage = Array.from({ length: 50 }, (_, i) => flock(`p${i}`));
    const flock501 = flock("f501");
    mockFlocks.mockImplementation((params) =>
      Promise.resolve(params?.search === "f501" ? [flock501] : firstPage));
    renderDialog();
    const results = await screen.findByRole("list", { name: "Accessible flocks" });
    await waitFor(() => expect(within(results).queryAllByRole("button").length).toBeGreaterThan(0));
    expect(within(results).queryByRole("button", { name: "Flock f501" })).not.toBeInTheDocument();

    await user.type(screen.getByRole("searchbox", { name: "Search accessible flocks" }), "f501");
    await waitFor(() => expect(within(results).getByRole("button", { name: "Flock f501" })).toBeInTheDocument());
    expect(mockFlocks).toHaveBeenLastCalledWith(expect.objectContaining({ search: "f501", offset: 0, limit: 50 }));
  });

  // #918 — Codex review, round 4, finding 5. A disabled "Load more" (mid
  // fetch) used to sit inside the arrow/Home/End roving-focus set, so End
  // landed on a button that could not be activated and focus never moved.
  it("keyboard navigation skips a disabled Load more, landing on the last enabled result", async () => {
    const user = userEvent.setup();
    const firstPage = Array.from({ length: 50 }, (_, i) => flock(`p${i}`));
    let resolveSecondPage: ((f: Flock[]) => void) | null = null;
    mockFlocks.mockImplementation((params) => {
      if (params?.offset === 50) return new Promise<Flock[]>((resolve) => { resolveSecondPage = resolve; });
      return Promise.resolve(firstPage);
    });
    renderDialog();
    const results = await screen.findByRole("list", { name: "Accessible flocks" });
    await waitFor(() => expect(within(results).queryAllByRole("button").length).toBe(50));

    await user.click(screen.getByRole("button", { name: "Load more" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Load more" })).toBeDisabled());

    const lastResult = within(results).getByRole("button", { name: "Flock p49" });
    screen.getByRole("button", { name: /^All flocks/ }).focus();
    await user.keyboard("{End}");
    expect(lastResult).toHaveFocus();

    resolveSecondPage!([]);
  });

  // #918 — Codex review, round 4, finding 3. A failed discovery (search or
  // page) request used to render as "No matching flocks" — indistinguishable
  // from a real empty result.
  it("shows a discovery-unavailable state with Retry, not a false empty result, when search fails", async () => {
    const user = userEvent.setup();
    mockFlocks.mockRejectedValueOnce(new Error("down"));
    renderDialog();
    expect(await screen.findByText("Could not search flocks.")).toBeInTheDocument();
    expect(screen.queryByText("No matching flocks. Try another name.")).not.toBeInTheDocument();

    mockFlocks.mockResolvedValueOnce([flock("f1")]);
    await user.click(screen.getByRole("button", { name: "Retry" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Flock f1" })).toBeInTheDocument());
    expect(screen.queryByText("Could not search flocks.")).not.toBeInTheDocument();
  });

  // #918 — Codex review, round 4, finding 6 (debounce half). Nothing in the
  // suite enforced the 250ms debounce itself — a search that fired
  // immediately on every keystroke would have passed every other test here.
  it("waits out the debounce before issuing a search request", async () => {
    vi.useFakeTimers();
    renderDialog();
    await act(async () => { await vi.advanceTimersByTimeAsync(250); }); // the initial, empty-search query
    mockFlocks.mockClear();

    // fireEvent.change drives React's onChange directly — no inter-keystroke
    // timers for fake timers to miss (matches NamedEntityPicker.test.tsx's
    // own debounce convention).
    const input = screen.getByRole("searchbox", { name: "Search accessible flocks" });
    fireEvent.change(input, { target: { value: "abc" } });
    expect(mockFlocks).not.toHaveBeenCalled();
    await act(async () => { await vi.advanceTimersByTime(249); });
    expect(mockFlocks).not.toHaveBeenCalled();
    await act(async () => { await vi.advanceTimersByTimeAsync(1); }); // exactly 250
    expect(mockFlocks).toHaveBeenCalledWith(expect.objectContaining({ search: "abc" }));
  });

  // #918 — Codex review, round 4, finding 6 (overlapping-generation half),
  // and round 4 finding 1's own guard. A `loadMore` extension tied to the
  // WRONG query, or a stale query's response landing after a newer one,
  // could append onto or replace the current results with a different
  // query's answer. Mutation-verified: dropping the `gen !== queryGenRef` a
  // guard in the debounced effect turns this red (the stale, unrelated
  // flocks land and overwrite the real match), confirmed locally then
  // reverted.
  it("rejects an older query's response when it resolves after a newer query already answered", async () => {
    const user = userEvent.setup();
    const pending: { search: string | undefined; resolve: (f: Flock[]) => void }[] = [];
    mockFlocks.mockImplementation((params) =>
      new Promise((resolve) => pending.push({ search: params?.search, resolve })));

    renderDialog();
    await waitFor(() => expect(pending).toHaveLength(1)); // the initial, empty-search query

    const results = await screen.findByRole("list", { name: "Accessible flocks" });
    await user.type(screen.getByRole("searchbox", { name: "Search accessible flocks" }), "abc");
    await waitFor(() => expect(pending).toHaveLength(2), { timeout: 2000 });

    // The NEWER query (search="abc") resolves first, as it would for an
    // ordinary fast response landing after a slower stale one is in flight.
    pending[1]!.resolve([flock("abcMatch")]);
    await waitFor(() => expect(within(results).getByRole("button", { name: "Flock abcMatch" })).toBeInTheDocument());

    // The STALE, older (unfiltered) query resolves late, with an entirely
    // different flock set that would be obviously wrong if it landed.
    pending[0]!.resolve([flock("stale1"), flock("stale2")]);
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(within(results).getByRole("button", { name: "Flock abcMatch" })).toBeInTheDocument();
    expect(within(results).queryByRole("button", { name: "Flock stale1" })).not.toBeInTheDocument();
  });
});
