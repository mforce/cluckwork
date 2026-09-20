// web/src/components/FlockPickerDialog.test.tsx
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
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

  // #918 — Codex review, round 3 finding 3 and round 4 finding 5. Restored
  // after the round-4 extraction dropped it down to the disabled-boundary
  // case alone: search-to-results handoff (ArrowDown from the search box),
  // the ordinary Arrow/Home/End sweep, a disabled "Load more" excluded from
  // it, and Escape returning focus to the DASHBOARD trigger — MUI's own
  // restore-focus behavior, proved end to end through a real trigger button
  // rather than assumed. Mutation-verified: removing `onKeyDown={onBodyKeyDown}`
  // from the results container turns the Arrow/Home/End assertions red
  // (focus never leaves the search box), confirmed locally then reverted.
  it("moves focus through the picker with Arrow/Home/End from the search box, skips a disabled Load more, and returns focus to the trigger on Escape", async () => {
    const user = userEvent.setup();
    const firstPage = Array.from({ length: 50 }, (_, i) => flock(`p${i}`));
    let resolveSecondPage: ((f: Flock[]) => void) | null = null;
    mockFlocks.mockImplementation((params) => {
      if (params?.offset === 50) return new Promise<Flock[]>((resolve) => { resolveSecondPage = resolve; });
      return Promise.resolve(firstPage);
    });

    function Harness() {
      const [open, setOpen] = useState(false);
      return (
        <>
          <button onClick={() => setOpen(true)}>Open picker</button>
          <FlockPickerDialog
            open={open} onClose={() => setOpen(false)} scope={{ kind: "all" }} accessibleCount={50}
            onPickAll={vi.fn()} onPickFlock={vi.fn()}
          />
        </>
      );
    }
    render(<Harness />);
    const trigger = screen.getByRole("button", { name: "Open picker" });
    await user.click(trigger);

    const results = await screen.findByRole("list", { name: "Accessible flocks" });
    await waitFor(() => expect(within(results).queryAllByRole("button").length).toBe(50));
    const allFlocks = screen.getByRole("button", { name: /^All flocks/ });
    const f0 = within(results).getByRole("button", { name: "Flock p0" });
    const lastResult = within(results).getByRole("button", { name: "Flock p49" });

    // Search-to-results: ArrowDown from the search box lands on the FIRST
    // choice — "All flocks", pinned above the results, first in DOM order.
    const search = screen.getByRole("searchbox", { name: "Search accessible flocks" });
    search.focus();
    await user.keyboard("{ArrowDown}");
    expect(allFlocks).toHaveFocus();

    await user.keyboard("{ArrowDown}");
    expect(f0).toHaveFocus();

    await user.keyboard("{Home}");
    expect(allFlocks).toHaveFocus();

    // A disabled "Load more" (mid-fetch) is excluded from the sweep, so End
    // lands on the last ENABLED result, not the button that cannot activate.
    // Refocus f0 first — a keyboard user navigating the results would never
    // land focus ON the disabled control itself; only a mouse click would,
    // and a disabled element does not dispatch keydown at all once it holds
    // focus, which would prove nothing about the roving-focus logic.
    await user.click(screen.getByRole("button", { name: "Load more" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Load more" })).toBeDisabled());
    f0.focus();
    await user.keyboard("{End}");
    expect(lastResult).toHaveFocus();
    resolveSecondPage!([]);
    await waitFor(() => expect(screen.queryByRole("button", { name: "Load more" })).not.toBeInTheDocument());

    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(trigger).toHaveFocus();
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

  // #918 — Codex review, round 5, finding 1. The race test above covers a
  // REPLACEMENT search only; a `loadMore` EXTENSION left pending across a
  // query change was unguarded before this fix (component line 104).
  // Mutation-verified: deleting that guard turns this red (the stale rows
  // land, appended onto the new query's own results), confirmed locally
  // then reverted.
  it("drops a pending Load more response when the search query changes before it resolves, and the new query's own cursor is never corrupted", async () => {
    const user = userEvent.setup();
    const firstPage = Array.from({ length: 50 }, (_, i) => flock(`p${i}`));
    const xyzPage = Array.from({ length: 50 }, (_, i) => flock(`x${i}`));
    const pendingExtension: { resolve: (f: Flock[]) => void } = { resolve: () => {} };
    mockFlocks.mockImplementation((params) => {
      if (params?.offset === 50) return new Promise<Flock[]>((resolve) => { pendingExtension.resolve = resolve; });
      return Promise.resolve(params?.search === "xyz" ? xyzPage : firstPage);
    });

    renderDialog({ accessibleCount: 50 });
    const results = await screen.findByRole("list", { name: "Accessible flocks" });
    await waitFor(() => expect(within(results).queryAllByRole("button").length).toBe(50));

    await user.click(screen.getByRole("button", { name: "Load more" })); // pending, tied to the empty-search query

    await user.type(screen.getByRole("searchbox", { name: "Search accessible flocks" }), "xyz");
    await waitFor(() => expect(within(results).getByRole("button", { name: "Flock x0" })).toBeInTheDocument());

    pendingExtension.resolve([flock("STALE-A")]);
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(within(results).queryByRole("button", { name: "Flock STALE-A" })).not.toBeInTheDocument();

    // xyz's own cursor (50, from ITS OWN page) is what the next Load More
    // carries — never a value left over from the abandoned empty-search one.
    await user.click(screen.getByRole("button", { name: "Load more" }));
    expect(mockFlocks).toHaveBeenLastCalledWith(expect.objectContaining({ search: "xyz", offset: 50 }));
  });

  // #918 — Codex review, round 5, finding 1 (close-time half). Same
  // unguarded `loadMore` extension, abandoned by the dialog closing rather
  // than a query change. Mutation-verified alongside the test above:
  // deleting the guard at component line 104 turns this red too.
  it("drops a pending Load more response when the dialog closes before it resolves, and reopening starts with a clean cursor", async () => {
    const user = userEvent.setup();
    const firstPage = Array.from({ length: 50 }, (_, i) => flock(`p${i}`));
    const reopenPage = Array.from({ length: 50 }, (_, i) => flock(`r${i}`));
    let opens = 0;
    const pendingExtension: { resolve: (f: Flock[]) => void } = { resolve: () => {} };
    mockFlocks.mockImplementation((params) => {
      if (params?.offset === 50) return new Promise<Flock[]>((resolve) => { pendingExtension.resolve = resolve; });
      opens += 1;
      return Promise.resolve(opens === 1 ? firstPage : reopenPage);
    });

    const dialogProps = {
      onClose: vi.fn(), scope: { kind: "all" as const }, accessibleCount: 50, onPickAll: vi.fn(), onPickFlock: vi.fn(),
    };
    const { rerender } = render(<FlockPickerDialog open {...dialogProps} />);
    const results = await screen.findByRole("list", { name: "Accessible flocks" });
    await waitFor(() => expect(within(results).queryAllByRole("button").length).toBe(50));

    await user.click(screen.getByRole("button", { name: "Load more" })); // pending, never resolved before close

    rerender(<FlockPickerDialog open={false} {...dialogProps} />);
    rerender(<FlockPickerDialog open {...dialogProps} />);
    await waitFor(() => expect(within(results).getByRole("button", { name: "Flock r0" })).toBeInTheDocument());

    pendingExtension.resolve([flock("STALE-B")]);
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(within(results).queryByRole("button", { name: "Flock STALE-B" })).not.toBeInTheDocument();

    // The reopened session's own cursor (50, from ITS OWN page) is what the
    // next Load More carries — never a value left over from the closed one.
    await user.click(screen.getByRole("button", { name: "Load more" }));
    expect(mockFlocks).toHaveBeenLastCalledWith(expect.objectContaining({ offset: 50 }));
  });
});
