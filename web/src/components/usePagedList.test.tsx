import { describe, it, expect, vi } from "vitest";
import { useMemo } from "react";
import { act, render, screen, fireEvent, waitFor } from "@testing-library/react";
import { usePagedList } from "./usePagedList";

// #469 — the race rules live here, once, instead of in six screens. Every test
// below is a concrete interleaving that shipped as a real defect on at least one
// screen (SalesPage's permanent error brick, WaterPage's stale filter response,
// FeedPage's submit refresh outranking a newer filter change), reproduced with
// deferred promises so the settle ORDER is the thing under test.

type Row = { id: string; label: string };

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (err: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

const rows = (...ids: string[]): Row[] => ids.map((id) => ({ id, label: `row ${id}` }));

// A realistic host: the screen's whole contract with the hook is what it
// renders and which controls it offers, so the assertions read that, not
// internals.
function Host({
  fetchPage,
  write,
  pageSize = 3,
}: {
  fetchPage: (offset: number, limit: number) => Promise<Row[]>;
  write?: () => Promise<unknown>;
  pageSize?: number;
}) {
  const list = usePagedList<Row>({ fetchPage, pageSize });
  return (
    <div>
      <p data-testid="rows">{list.rows === null ? "null" : list.rows.map((r) => r.id).join(",")}</p>
      <p data-testid="loading">{String(list.loading)}</p>
      <p data-testid="reloading">{String(list.reloading)}</p>
      <p data-testid="error">{list.error ?? ""}</p>
      {list.canLoadMore && (
        <button onClick={() => void list.loadMore()}>more</button>
      )}
      {/* Bypasses canLoadMore so the hook's OWN guard is what gets tested,
          not the host's rendering of it. */}
      <button onClick={() => void list.loadMore()}>force-more</button>
      {write && (
        <button onClick={() => void list.runWrite(write).catch(() => {})}>write</button>
      )}
    </div>
  );
}

const shown = () => screen.getByTestId("rows").textContent;
const errorText = () => screen.getByTestId("error").textContent;
const loading = () => screen.getByTestId("loading").textContent;
const reloading = () => screen.getByTestId("reloading").textContent;

describe("usePagedList — first load and paging", () => {
  it("loads the first page on mount and reports more when the page is full", async () => {
    const fetchPage = vi.fn().mockResolvedValue(rows("a", "b", "c"));
    render(<Host fetchPage={fetchPage} />);

    await waitFor(() => expect(shown()).toBe("a,b,c"));
    expect(fetchPage).toHaveBeenCalledWith(0, 3);
    expect(screen.getByRole("button", { name: "more" })).toBeInTheDocument();
  });

  it("reports no more once a short page lands", async () => {
    render(<Host fetchPage={vi.fn().mockResolvedValue(rows("a", "b"))} />);
    await waitFor(() => expect(shown()).toBe("a,b"));
    expect(screen.queryByRole("button", { name: "more" })).not.toBeInTheDocument();
  });

  it("appends the next page at the loaded-row offset", async () => {
    const fetchPage = vi.fn()
      .mockResolvedValueOnce(rows("a", "b", "c"))
      .mockResolvedValueOnce(rows("d", "e"));
    render(<Host fetchPage={fetchPage} />);
    await waitFor(() => expect(shown()).toBe("a,b,c"));

    fireEvent.click(screen.getByRole("button", { name: "more" }));
    await waitFor(() => expect(shown()).toBe("a,b,c,d,e"));
    expect(fetchPage).toHaveBeenLastCalledWith(3, 3);
  });

  it("advances the cursor past a page that was ENTIRELY duplicates (codex P2)", async () => {
    // Dedupe keeps the rendered list honest but must not drive the cursor: if
    // a whole page of newer records lands between clicks, the next offset
    // page can be 100% rows already shown. Deriving the offset from unique
    // rows then leaves it parked forever and the older records — the ones
    // #465 exists to reach — become unreachable no matter how often the user
    // clicks.
    const fetchPage = vi.fn()
      .mockResolvedValueOnce(rows("a", "b", "c"))
      .mockResolvedValueOnce(rows("a", "b", "c"))   // a full page of duplicates
      .mockResolvedValueOnce(rows("d", "e", "f"));
    render(<Host fetchPage={fetchPage} />);
    await waitFor(() => expect(shown()).toBe("a,b,c"));

    fireEvent.click(screen.getByRole("button", { name: "more" }));
    await waitFor(() => expect(fetchPage).toHaveBeenCalledTimes(2));
    expect(fetchPage).toHaveBeenLastCalledWith(3, 3);
    expect(shown()).toBe("a,b,c"); // nothing new to show, correctly

    fireEvent.click(screen.getByRole("button", { name: "more" }));
    await waitFor(() => expect(shown()).toBe("a,b,c,d,e,f"));
    // The cursor moved by what the SERVER returned, not by what survived
    // dedupe — otherwise this asks for offset 3 again, forever.
    expect(fetchPage).toHaveBeenLastCalledWith(6, 3);
  });

  it("drops rows the next page repeats after a concurrent insert shifted the offset", async () => {
    // A row created between pages pushes everything deeper, so page two
    // re-serves page one's tail. Appending it blind collides React keys.
    const fetchPage = vi.fn()
      .mockResolvedValueOnce(rows("a", "b", "c"))
      .mockResolvedValueOnce(rows("c", "d", "e"));
    render(<Host fetchPage={fetchPage} />);
    await waitFor(() => expect(shown()).toBe("a,b,c"));

    fireEvent.click(screen.getByRole("button", { name: "more" }));
    await waitFor(() => expect(shown()).toBe("a,b,c,d,e"));
  });
});

describe("usePagedList — intent ordering", () => {
  it("ignores a stale page that settles after a newer filter's", async () => {
    // WaterPage today: pick flock A, pick flock B, A lands last and wins.
    const first = deferred<Row[]>();
    const second = deferred<Row[]>();
    const fetchA = vi.fn().mockReturnValue(first.promise);
    const fetchB = vi.fn().mockReturnValue(second.promise);

    const { rerender } = render(<Host fetchPage={fetchA} />);
    rerender(<Host fetchPage={fetchB} />); // filter change = new fetchPage identity

    await act(async () => { second.resolve(rows("b1", "b2", "b3")); });
    expect(shown()).toBe("b1,b2,b3");

    await act(async () => { first.resolve(rows("a1", "a2", "a3")); });
    expect(shown()).toBe("b1,b2,b3");
  });

  it("reloads from the top when the filter changes", async () => {
    const fetchA = vi.fn().mockResolvedValue(rows("a", "b", "c"));
    const fetchB = vi.fn().mockResolvedValue(rows("x"));
    const { rerender } = render(<Host fetchPage={fetchA} />);
    await waitFor(() => expect(shown()).toBe("a,b,c"));

    rerender(<Host fetchPage={fetchB} />);
    await waitFor(() => expect(shown()).toBe("x"));
    expect(fetchB).toHaveBeenCalledWith(0, 3);
    // The previous window's "more" must not survive into the new one.
    expect(screen.queryByRole("button", { name: "more" })).not.toBeInTheDocument();
  });

  it("reports loading during a reload so a screen can blank its own table", async () => {
    // Whether the previous window is hidden mid-reload differs per screen
    // (Audit blanks its table; Feed cannot, its filters live inside the
    // rows-gated region), so the hook reports and the screen decides. What
    // the hook guarantees is that the rows are never REPLACED by a window
    // that has not landed.
    const pending = deferred<Row[]>();
    const fetchA = vi.fn().mockResolvedValue(rows("a", "b", "c"));
    const fetchB = vi.fn().mockReturnValue(pending.promise);
    const { rerender } = render(<Host fetchPage={fetchA} />);
    await waitFor(() => expect(shown()).toBe("a,b,c"));

    rerender(<Host fetchPage={fetchB} />);
    expect(loading()).toBe("true");
    expect(reloading()).toBe("true");
    expect(shown()).toBe("a,b,c");

    await act(async () => { pending.resolve(rows("z")); });
    expect(shown()).toBe("z");
    expect(loading()).toBe("false");
    expect(reloading()).toBe("false");
  });

  it("keeps reporting reloading when a write starts mid-replacement (codex P2)", async () => {
    // `reloading` means "what is on screen may not belong to the current
    // filter". A write starting does not make that untrue — and screens blank
    // their list on it, so clearing it here puts the PREVIOUS filter's rows
    // back on screen for the length of the POST.
    const pendingFilter = deferred<Row[]>();
    const writeGate = deferred<void>();
    const fetchA = vi.fn().mockResolvedValue(rows("a"));
    const fetchB = vi.fn()
      .mockReturnValueOnce(pendingFilter.promise)
      .mockResolvedValue(rows("b"));
    const { rerender } = render(
      <Host fetchPage={fetchA} write={() => writeGate.promise} />);
    await waitFor(() => expect(shown()).toBe("a"));

    rerender(<Host fetchPage={fetchB} write={() => writeGate.promise} />);
    expect(reloading()).toBe("true");

    fireEvent.click(screen.getByRole("button", { name: "write" }));
    expect(reloading()).toBe("true"); // still not the current filter's rows

    await act(async () => { writeGate.resolve(); });
    await waitFor(() => expect(reloading()).toBe("false"));
    // Settled under the CURRENT filter, so blanking can end.
    expect(shown()).toBe("b");
  });

  it("does not report reloading while a load-more is in flight", async () => {
    // The distinction Audit needs: blanking the table for a REPLACE is right,
    // blanking it for an EXTEND would hide the rows being extended.
    const pending = deferred<Row[]>();
    const fetchPage = vi.fn()
      .mockResolvedValueOnce(rows("a", "b", "c"))
      .mockReturnValueOnce(pending.promise);
    render(<Host fetchPage={fetchPage} />);
    await waitFor(() => expect(shown()).toBe("a,b,c"));

    fireEvent.click(screen.getByRole("button", { name: "more" }));
    expect(loading()).toBe("true");
    expect(reloading()).toBe("false");

    await act(async () => { pending.resolve(rows("d")); });
    expect(shown()).toBe("a,b,c,d");
  });

  it("keeps the visible rows while a load-more is in flight", async () => {
    const pending = deferred<Row[]>();
    const fetchPage = vi.fn()
      .mockResolvedValueOnce(rows("a", "b", "c"))
      .mockReturnValueOnce(pending.promise);
    render(<Host fetchPage={fetchPage} />);
    await waitFor(() => expect(shown()).toBe("a,b,c"));

    fireEvent.click(screen.getByRole("button", { name: "more" }));
    expect(shown()).toBe("a,b,c");

    await act(async () => { pending.resolve(rows("d")); });
    expect(shown()).toBe("a,b,c,d");
  });

  it("discards a superseded request's failure instead of raising the error", async () => {
    // The late-rejection-over-a-healthy-view bug: every screen but Audit/Feed.
    const first = deferred<Row[]>();
    const fetchA = vi.fn().mockReturnValue(first.promise);
    const fetchB = vi.fn().mockResolvedValue(rows("b1"));

    const { rerender } = render(<Host fetchPage={fetchA} />);
    rerender(<Host fetchPage={fetchB} />);
    await waitFor(() => expect(shown()).toBe("b1"));

    await act(async () => { first.reject(new Error("stale blew up")); });
    expect(errorText()).toBe("");
    expect(shown()).toBe("b1");
  });

  it("does not let a superseded settle clear the current load's loading flag", async () => {
    const first = deferred<Row[]>();
    const second = deferred<Row[]>();
    const { rerender } = render(<Host fetchPage={vi.fn().mockReturnValue(first.promise)} />);
    rerender(<Host fetchPage={vi.fn().mockReturnValue(second.promise)} />);
    expect(loading()).toBe("true");

    await act(async () => { first.resolve(rows("a")); });
    expect(loading()).toBe("true"); // the NEWER load still owns the flag

    await act(async () => { second.resolve(rows("b")); });
    expect(loading()).toBe("false");
  });

  it("withdraws load-more while a filter load is in flight", async () => {
    const pending = deferred<Row[]>();
    const fetchA = vi.fn().mockResolvedValue(rows("a", "b", "c"));
    const fetchB = vi.fn().mockReturnValue(pending.promise);
    const { rerender } = render(<Host fetchPage={fetchA} />);
    await waitFor(() => expect(shown()).toBe("a,b,c"));
    expect(screen.getByRole("button", { name: "more" })).toBeInTheDocument();

    // The old window's "more" must not survive into the new one — clicking it
    // would append the NEW filter's page onto the OLD filter's rows.
    rerender(<Host fetchPage={fetchB} />);
    expect(screen.queryByRole("button", { name: "more" })).not.toBeInTheDocument();

    await act(async () => { pending.resolve(rows("z")); });
    expect(shown()).toBe("z");
  });

  it("no-ops a load-more issued while a load is already in flight", async () => {
    // The hook's own guard, independent of whether a screen hides the control.
    const pending = deferred<Row[]>();
    const fetchA = vi.fn().mockResolvedValue(rows("a", "b", "c"));
    const fetchB = vi.fn().mockReturnValue(pending.promise);
    const { rerender } = render(<Host fetchPage={fetchA} />);
    await waitFor(() => expect(shown()).toBe("a,b,c"));

    rerender(<Host fetchPage={fetchB} />);
    fireEvent.click(screen.getByRole("button", { name: "force-more" }));
    expect(fetchB).toHaveBeenCalledTimes(1); // the load, not a second page

    await act(async () => { pending.resolve(rows("z")); });
    expect(shown()).toBe("z");
  });
});

describe("usePagedList — page metadata", () => {
  // ExpensesPage's endpoint returns the period total alongside the rows, and
  // that number is the dangerous one: a stale total under the new month's
  // picker reads as a legitimate figure for the wrong period.
  function MetaHost({ fetchPage }: {
    fetchPage: (offset: number, limit: number) => Promise<{ items: Row[]; meta: number }>;
  }) {
    const list = usePagedList<Row, number>({ fetchPage, pageSize: 3 });
    return (
      <>
        <p data-testid="rows">{list.rows === null ? "null" : list.rows.map((r) => r.id).join(",")}</p>
        <p data-testid="meta">{list.meta === null ? "none" : String(list.meta)}</p>
        <p data-testid="error">{list.error ?? ""}</p>
      </>
    );
  }
  const meta = () => screen.getByTestId("meta").textContent;

  it("exposes the landed page's metadata", async () => {
    render(<MetaHost fetchPage={vi.fn().mockResolvedValue({ items: rows("a"), meta: 1200 })} />);
    await waitFor(() => expect(meta()).toBe("1200"));
  });

  it("refuses a superseded page's metadata, not just its rows", async () => {
    const stale = deferred<{ items: Row[]; meta: number }>();
    const fetchJuly = vi.fn().mockReturnValue(stale.promise);
    const fetchAugust = vi.fn().mockResolvedValue({ items: rows("b"), meta: 300 });

    const { rerender } = render(<MetaHost fetchPage={fetchJuly} />);
    rerender(<MetaHost fetchPage={fetchAugust} />);
    await waitFor(() => expect(meta()).toBe("300"));

    await act(async () => { stale.resolve({ items: rows("a"), meta: 1200 }); });
    expect(meta()).toBe("300"); // July's total must not describe August
  });

  it("drops the metadata when the load fails, like the rows", async () => {
    const fetchA = vi.fn().mockResolvedValue({ items: rows("a"), meta: 1200 });
    const fetchB = vi.fn().mockRejectedValue(new Error("boom"));
    const { rerender } = render(<MetaHost fetchPage={fetchA} />);
    await waitFor(() => expect(meta()).toBe("1200"));

    rerender(<MetaHost fetchPage={fetchB} />);
    await waitFor(() => expect(screen.getByTestId("error").textContent).toBe("boom"));
    expect(meta()).toBe("none");
  });
});

describe("usePagedList — error lifecycle", () => {
  it("clears the rows and shows the error when the current filter fails", async () => {
    // The chosen strategy: empty is never mistaken for stale.
    const fetchA = vi.fn().mockResolvedValue(rows("a", "b", "c"));
    const fetchB = vi.fn().mockRejectedValue(new Error("boom"));
    const { rerender } = render(<Host fetchPage={fetchA} />);
    await waitFor(() => expect(shown()).toBe("a,b,c"));

    rerender(<Host fetchPage={fetchB} />);
    await waitFor(() => expect(errorText()).toBe("boom"));
    expect(shown()).toBe("");
    expect(screen.queryByRole("button", { name: "more" })).not.toBeInTheDocument();
  });

  it("keeps the loaded rows when only a load-more fails (codex P2)", async () => {
    // A failed EXTENSION says nothing about the rows already on screen —
    // they still belong to the current filter. Emptying them turned a
    // transient paging blip into "no records at all".
    const fetchPage = vi.fn()
      .mockResolvedValueOnce(rows("a", "b", "c"))
      .mockRejectedValueOnce(new Error("boom"));
    render(<Host fetchPage={fetchPage} />);
    await waitFor(() => expect(shown()).toBe("a,b,c"));

    fireEvent.click(screen.getByRole("button", { name: "more" }));
    await waitFor(() => expect(errorText()).toBe("boom"));
    expect(shown()).toBe("a,b,c");
    // The window is intact, so the next page may still be there to retry for.
    expect(screen.getByRole("button", { name: "more" })).toBeInTheDocument();
  });

  it("heals the error on the next successful load", async () => {
    // SalesPage's brick: an error nothing ever cleared replaced the screen for
    // the rest of the session.
    const fetchA = vi.fn().mockRejectedValue(new Error("boom"));
    const fetchB = vi.fn().mockResolvedValue(rows("a"));
    const { rerender } = render(<Host fetchPage={fetchA} />);
    await waitFor(() => expect(errorText()).toBe("boom"));

    rerender(<Host fetchPage={fetchB} />);
    await waitFor(() => expect(shown()).toBe("a"));
    expect(errorText()).toBe("");
  });

  it("uses the screen's own error wording when it supplies a formatter", async () => {
    // Most screens show a fixed translated sentence rather than the server's
    // text (FeedPage's "Could not load feed records." is pinned by its tests).
    function FixedMessageHost() {
      const list = usePagedList<Row>({
        fetchPage: useMemo(() => vi.fn().mockRejectedValue(new Error("raw detail")), []),
        pageSize: 3,
        errorText: () => "Could not load feed records.",
      });
      return <p data-testid="error">{list.error ?? ""}</p>;
    }
    render(<FixedMessageHost />);
    await waitFor(() => expect(errorText()).toBe("Could not load feed records."));
  });

  it("releases the loading flag when the load fails", async () => {
    render(<Host fetchPage={vi.fn().mockRejectedValue(new Error("boom"))} />);
    await waitFor(() => expect(errorText()).toBe("boom"));
    expect(loading()).toBe("false");
  });
});

describe("usePagedList — writes", () => {
  it("refreshes the list after a write", async () => {
    const fetchPage = vi.fn()
      .mockResolvedValueOnce(rows("a"))
      .mockResolvedValueOnce(rows("a", "b"));
    const write = vi.fn().mockResolvedValue(undefined);
    render(<Host fetchPage={fetchPage} write={write} />);
    await waitFor(() => expect(shown()).toBe("a"));

    fireEvent.click(screen.getByRole("button", { name: "write" }));
    await waitFor(() => expect(shown()).toBe("a,b"));
    expect(write).toHaveBeenCalledOnce();
  });

  it("re-walks the whole loaded window after a write instead of collapsing to page one", async () => {
    // #467 learned this on StockPage: a user who paged deeper to reach an old
    // row, then corrected it, must not have the list snap back to the newest
    // page — the row they were working on vanishes from under them.
    const fetchPage = vi.fn()
      .mockResolvedValueOnce(rows("a", "b", "c"))   // page 1
      .mockResolvedValueOnce(rows("d", "e", "f"))   // page 2 via load-more
      .mockResolvedValueOnce(rows("a", "b", "c"))   // refresh, page 1
      .mockResolvedValueOnce(rows("d", "e", "F"));  // refresh, page 2 (f corrected)
    render(<Host fetchPage={fetchPage} write={() => Promise.resolve()} />);
    await waitFor(() => expect(shown()).toBe("a,b,c"));
    fireEvent.click(screen.getByRole("button", { name: "more" }));
    await waitFor(() => expect(shown()).toBe("a,b,c,d,e,f"));

    fireEvent.click(screen.getByRole("button", { name: "write" }));
    await waitFor(() => expect(shown()).toBe("a,b,c,d,e,F"));
    expect(fetchPage.mock.calls.slice(2).map(([offset]) => offset)).toEqual([0, 3]);
  });

  it("dedupes rows the post-write walk re-serves across its own pages", async () => {
    // The walk issues its pages sequentially, so an insert landing between
    // them shifts the offsets and re-serves a row it already collected —
    // the same drift load-more has, on a path that also has to survive it.
    const fetchPage = vi.fn()
      .mockResolvedValueOnce(rows("a", "b", "c"))
      .mockResolvedValueOnce(rows("d", "e", "f"))
      .mockResolvedValueOnce(rows("a", "b", "c"))   // refresh page 1
      .mockResolvedValueOnce(rows("c", "d", "e"))   // refresh page 2 re-serves c
      .mockResolvedValueOnce(rows("g", "h", "i"));
    render(<Host fetchPage={fetchPage} write={() => Promise.resolve()} />);
    await waitFor(() => expect(shown()).toBe("a,b,c"));
    fireEvent.click(screen.getByRole("button", { name: "more" }));
    await waitFor(() => expect(shown()).toBe("a,b,c,d,e,f"));

    fireEvent.click(screen.getByRole("button", { name: "write" }));
    await waitFor(() => expect(shown()).toBe("a,b,c,d,e"));

    // The walk consumed SIX server rows to render five, so paging resumes at
    // 6. Rebuilding the cursor from the rendered rows would ask for 5 and
    // re-serve a row for every duplicate the walk absorbed.
    fireEvent.click(screen.getByRole("button", { name: "more" }));
    await waitFor(() => expect(shown()).toBe("a,b,c,d,e,g,h,i"));
    expect(fetchPage).toHaveBeenLastCalledWith(6, 3);
  });

  it("stops the post-write walk as soon as a newer intent supersedes it", async () => {
    // THREE pages loaded, so the walk still has a page left to fetch when it
    // is superseded — with only two, the loop ends on its own and a missing
    // bail is indistinguishable (a mutant survived exactly that way).
    const secondPage = deferred<Row[]>();
    const fetchA = vi.fn()
      .mockResolvedValueOnce(rows("a", "b", "c"))
      .mockResolvedValueOnce(rows("d", "e", "f"))
      .mockResolvedValueOnce(rows("g", "h", "i"))
      .mockResolvedValueOnce(rows("a", "b", "c"))     // refresh page 1
      .mockReturnValueOnce(secondPage.promise)        // refresh page 2, hangs
      .mockResolvedValue(rows("g", "h", "i"));        // page 3 — must NOT be asked for
    const fetchB = vi.fn().mockResolvedValue(rows("z"));
    const { rerender } = render(
      <Host fetchPage={fetchA} write={() => Promise.resolve()} />);
    await waitFor(() => expect(shown()).toBe("a,b,c"));
    fireEvent.click(screen.getByRole("button", { name: "more" }));
    await waitFor(() => expect(shown()).toBe("a,b,c,d,e,f"));
    fireEvent.click(screen.getByRole("button", { name: "more" }));
    await waitFor(() => expect(shown()).toBe("a,b,c,d,e,f,g,h,i"));

    fireEvent.click(screen.getByRole("button", { name: "write" }));
    await waitFor(() => expect(fetchA).toHaveBeenCalledTimes(5));
    rerender(<Host fetchPage={fetchB} write={() => Promise.resolve()} />);
    await waitFor(() => expect(shown()).toBe("z"));

    await act(async () => { secondPage.resolve(rows("d", "e", "f")); });
    expect(shown()).toBe("z"); // the abandoned walk never lands
    // ...and it STOPPED rather than finishing a walk whose result it would
    // only discard: no sixth call was issued for the window's third page.
    expect(fetchA).toHaveBeenCalledTimes(5);
  });

  it("lets a filter change made during the write supersede the write's refresh", async () => {
    // FeedPage's live bug: the submit's refresh claimed its ticket AFTER the
    // POST resolved, so it outranked — and overwrote — the newer filter.
    const writeGate = deferred<void>();
    const fetchA = vi.fn().mockResolvedValue(rows("a"));
    const fetchB = vi.fn().mockResolvedValue(rows("b"));
    const { rerender } = render(
      <Host fetchPage={fetchA} write={() => writeGate.promise} />);
    await waitFor(() => expect(shown()).toBe("a"));

    fireEvent.click(screen.getByRole("button", { name: "write" }));
    // The user changes the filter while the POST is still in flight.
    rerender(<Host fetchPage={fetchB} write={() => writeGate.promise} />);
    await waitFor(() => expect(shown()).toBe("b"));

    await act(async () => { writeGate.resolve(); });
    // The write landed, but the newer filter owns the view — no revert to A.
    expect(shown()).toBe("b");
    expect(fetchA).toHaveBeenCalledTimes(1); // never refetched for the refresh
  });

  it("re-reads the newest filter when a write's refresh was superseded (codex P1)", async () => {
    // Request START order is not database COMMIT order: a filter GET issued
    // during the write can also COMPLETE before the write's transaction
    // commits, so its rows do not contain the mutation. Skipping the refresh
    // then leaves the screen showing a successful write whose effect is
    // missing. The newer filter still owns the view — it is re-read, not
    // overridden.
    const writeGate = deferred<void>();
    const fetchA = vi.fn().mockResolvedValue(rows("a"));
    const fetchB = vi.fn()
      .mockResolvedValueOnce(rows("b"))              // the racing filter GET
      .mockResolvedValueOnce(rows("b", "written"));  // the re-read, post-commit
    const { rerender } = render(
      <Host fetchPage={fetchA} write={() => writeGate.promise} />);
    await waitFor(() => expect(shown()).toBe("a"));

    fireEvent.click(screen.getByRole("button", { name: "write" }));
    rerender(<Host fetchPage={fetchB} write={() => writeGate.promise} />);
    await waitFor(() => expect(shown()).toBe("b"));

    await act(async () => { writeGate.resolve(); });
    await waitFor(() => expect(shown()).toBe("b,written"));
    // Re-read under the NEWEST filter, never the one the write started under.
    expect(fetchA).toHaveBeenCalledTimes(1);
  });

  it("surfaces a failed write's error and leaves the rows alone", async () => {
    const fetchPage = vi.fn().mockResolvedValue(rows("a"));
    const write = vi.fn().mockRejectedValue(new Error("write died"));
    render(<Host fetchPage={fetchPage} write={write} />);
    await waitFor(() => expect(shown()).toBe("a"));

    fireEvent.click(screen.getByRole("button", { name: "write" }));
    await waitFor(() => expect(loading()).toBe("false"));
    // The list is untouched by a write that never landed; the screen decides
    // how to report the failure (runWrite rethrows).
    expect(shown()).toBe("a");
  });

  it("reissues the read that a FAILED write invalidated (codex P1)", async () => {
    // Claiming the ticket at submit invalidates whatever read is in flight.
    // On success the refresh replaces it — but on failure there was no
    // replacement, so the discarded response left the screen with nothing:
    // stuck on its loading state until the user happened to change a filter.
    const initial = deferred<Row[]>();
    const fetchPage = vi.fn()
      .mockReturnValueOnce(initial.promise)
      .mockResolvedValueOnce(rows("a", "b"));
    render(<Host fetchPage={fetchPage} write={() => Promise.reject(new Error("nope"))} />);
    expect(shown()).toBe("null");

    fireEvent.click(screen.getByRole("button", { name: "write" }));
    await waitFor(() => expect(shown()).toBe("a,b"));

    // The abandoned first response is still stale and must stay dropped.
    await act(async () => { initial.resolve(rows("z")); });
    expect(shown()).toBe("a,b");
  });

  it("re-reads the NEWEST filter when a superseded write rejects (codex P2)", async () => {
    // A rejection does not mean nothing committed: a screen's write callback
    // can POST successfully and then fail on a follow-up read (FeedPage
    // refreshes its inventory figures inside the same callback). If a filter
    // change superseded the write and its GET completed before the POST
    // committed, that list is missing the record — so the catch path must
    // re-read too, and under the NEWEST filter, never the write's old one.
    const writeGate = deferred<void>();
    const fetchA = vi.fn().mockResolvedValue(rows("a"));
    const fetchB = vi.fn()
      .mockResolvedValueOnce(rows("b"))              // the racing filter GET
      .mockResolvedValueOnce(rows("b", "committed"));
    const { rerender } = render(
      <Host fetchPage={fetchA} write={() => writeGate.promise} />);
    await waitFor(() => expect(shown()).toBe("a"));

    fireEvent.click(screen.getByRole("button", { name: "write" }));
    rerender(<Host fetchPage={fetchB} write={() => writeGate.promise} />);
    await waitFor(() => expect(shown()).toBe("b"));

    await act(async () => { writeGate.reject(new Error("nope")); });
    await waitFor(() => expect(shown()).toBe("b,committed"));
    expect(fetchA).toHaveBeenCalledTimes(1); // never under the old filter
  });

  it("rethrows the write's error so the screen can render it", async () => {
    const seen: unknown[] = [];
    function WriteHost() {
      const list = usePagedList<Row>({
        fetchPage: vi.fn().mockResolvedValue(rows("a")),
        pageSize: 3,
      });
      return (
        <button onClick={() => void list.runWrite(() => Promise.reject(new Error("nope")))
          .catch((e) => seen.push(e))}>go</button>
      );
    }
    render(<WriteHost />);
    fireEvent.click(screen.getByRole("button", { name: "go" }));
    await waitFor(() => expect(seen).toHaveLength(1));
    expect((seen[0] as Error).message).toBe("nope");
  });
});
