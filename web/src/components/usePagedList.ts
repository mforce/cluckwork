import { useCallback, useEffect, useRef, useState } from "react";
import { errText } from "../lib/errText";

// #469 — one implementation of the async discipline PR #467 arrived at over 11
// review rounds, instead of the four homegrown variants (StockPage, AuditPage,
// FeedPage, ReportsPage) and the four screens that had none.
//
// The rule everything below serves: THE VIEW BELONGS TO THE NEWEST USER
// INTENT. Every load claims a monotonic ticket at the moment the user asks for
// it, and touches state only while it still holds the current one — checked
// after EVERY await, in the failure path as much as the success path, because
// a superseded request's rejection is exactly as stale as its response and
// painting it over a healthy view is the bug users actually reported.
//
// Consequences that are easy to get wrong and are pinned by tests:
//   * `loading` belongs to the ticket holder, so a superseded settle cannot
//     clear a flag it no longer owns (a stuck spinner is the failure mode).
//   * `error` is raised only by the current ticket and cleared by ANY
//     successful load, so an error can never outlive the condition that
//     caused it (SalesPage bricked its whole screen this way).
//   * A failed load empties the rows rather than leaving the previous
//     window's under the new filter — empty is honest, stale is not.
//   * `runWrite` claims its ticket BEFORE the write starts, so a filter change
//     made while the write is in flight is newer intent and wins; the write's
//     own refresh stands down (FeedPage had this backwards and repainted the
//     old filter's rows over the new ones).
//
// Filter changes are expressed as a new `fetchPage` identity: screens build it
// with useCallback over their filter state, exactly as AuditPage and FeedPage
// already did, so "the filter changed" and "reload from the top" are the same
// event and cannot drift apart.
// Some endpoints answer with an envelope rather than a bare array — the
// expenses list carries the period total and its currency. That figure needs
// the SAME ticket protection as the rows: a stale total under the new month's
// picker reads as a legitimate number for the wrong period.
export type PageResult<T, M> = T[] | { items: T[]; meta: M };

export function usePagedList<T extends { id: string }, M = never>({
  fetchPage,
  pageSize,
  // Most screens show a fixed translated sentence instead of the server's
  // text; the default keeps the server detail for those that want it.
  errorText: formatError = errText,
}: {
  fetchPage: (offset: number, limit: number) => Promise<PageResult<T, M>>;
  pageSize: number;
  errorText?: (err: unknown) => string;
}): {
  rows: T[] | null;
  meta: M | null;
  hasMore: boolean;
  canLoadMore: boolean;
  loading: boolean;
  reloading: boolean;
  error: string | null;
  loadMore: () => Promise<void>;
  runWrite: <R>(write: () => Promise<R>) => Promise<R>;
  reload: () => Promise<void>;
} {
  const [rows, setRows] = useState<T[] | null>(null);
  const [meta, setMeta] = useState<M | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(false);
  // A REPLACE is in flight, as opposed to an EXTEND: screens that hide the
  // previous window mid-change need to tell the two apart, because blanking
  // during a load-more would hide the very rows being extended.
  const [reloading, setReloading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const req = useRef(0);
  // Mirrors of state that callbacks must read WITHOUT re-subscribing: a
  // useCallback closed over `rows`/`loading` would be stale by the time an
  // event handler fires, and adding them as deps would rebuild `loadMore` on
  // every page — which the load effect below would read as a filter change.
  const rowsRef = useRef<T[] | null>(null);
  const loadingRef = useRef(false);
  // Held in a ref, deliberately NOT a dep of `load`: screens pass an inline
  // arrow (`errorText: () => t("…")`) whose identity changes every render, and
  // a dep would rebuild `load`, which the effect below reads as a filter
  // change — an infinite reload loop.
  const formatErrorRef = useRef(formatError);
  formatErrorRef.current = formatError;

  const setLoadingOwned = (seq: number, value: boolean, replace = false) => {
    if (seq !== req.current) return;
    loadingRef.current = value;
    setLoading(value);
    // Only a replace raises it; any settle clears it, so an extend that
    // follows a replace cannot leave it stuck on.
    setReloading(value && replace);
  };

  // `seq` is claimed by the CALLER, before anything awaits, so the ticket
  // order matches the order the user asked in — not the order the network
  // happened to answer in.
  const load = useCallback(async (offset: number, seq: number) => {
    setLoadingOwned(seq, true, offset === 0);
    try {
      const result = await fetchPage(offset, pageSize);
      if (seq !== req.current) return;
      const page = Array.isArray(result) ? result : result.items;
      if (!Array.isArray(result)) setMeta(result.meta);
      setRows((prev) => {
        const next = offset === 0 || prev === null
          ? page
          // A row inserted between pages shifts every later offset, so the
          // next page can re-serve one already on screen — appending it blind
          // duplicates a React key.
          : [...prev, ...page.filter((p) => !prev.some((x) => x.id === p.id))];
        rowsRef.current = next;
        return next;
      });
      setHasMore(page.length === pageSize);
      setError(null);
    } catch (err) {
      if (seq !== req.current) return;
      setError(formatErrorRef.current(err));
      // Only a failed REPLACEMENT empties the list: the rows still on screen
      // belong to a window the user has navigated away from, and the metadata
      // goes with them (a total that outlived its rows is the worst of both).
      // A failed EXTENSION says nothing about what is already shown — those
      // rows are still the current filter's, so they stay, and hasMore stays
      // put so the page can be retried (codex review).
      if (offset === 0) {
        rowsRef.current = [];
        setRows([]);
        setMeta(null);
        setHasMore(false);
      }
    } finally {
      setLoadingOwned(seq, false);
    }
  }, [fetchPage, pageSize]);

  const reload = useCallback(async () => {
    const seq = ++req.current;
    // The rows stay put for the duration and are replaced only when the new
    // page lands. Whether the previous window should be HIDDEN while it is
    // in flight is a presentation choice, not a correctness one, and it
    // differs per screen: AuditPage blanks its table ("no mislabeled rows"),
    // FeedPage must not, because its whole page — filter controls included —
    // is gated on having rows. Screens read `loading` and decide; blanking
    // here took Feed's own filters off the screen mid-change.
    //
    // No setHasMore(false) either: `canLoadMore` already folds in `loading`,
    // so the pager is withdrawn for the whole reload. A mutation check proved
    // an explicit reset changed no observable behaviour, and an unpinned line
    // reads as a guarantee nothing is holding.
    await load(0, seq);
  }, [load]);

  // A new `fetchPage` identity IS a filter change.
  useEffect(() => { void reload(); }, [reload]);

  // Always the CURRENT render's reload, for the one caller that must re-read
  // under whatever filter is newest rather than the one it closed over.
  const reloadRef = useRef(reload);
  reloadRef.current = reload;

  const loadMore = useCallback(async () => {
    if (loadingRef.current) return;
    const seq = ++req.current;
    await load(rowsRef.current?.length ?? 0, seq);
  }, [load]);

  // Re-fetch every page the user currently has, not just the newest one. A
  // reader who paged deeper to reach an old row and then corrected it must
  // not have the list snap back to page one, taking that row off screen
  // (StockPage learned this in #467). Bails the moment a newer intent claims
  // the ticket, so an abandoned walk stops issuing requests instead of
  // finishing them for nothing.
  const refreshWindow = useCallback(async (seq: number) => {
    const target = Math.max(rowsRef.current?.length ?? 0, 1);
    const window = new Map<string, T>();
    let lastPageFull = false;
    for (let offset = 0; offset < target; offset += pageSize) {
      let result: PageResult<T, M>;
      try {
        result = await fetchPage(offset, pageSize);
      } catch (err) {
        // Same rule as `load`: a superseded page's failure is moot.
        if (seq !== req.current) return;
        setError(formatErrorRef.current(err));
        rowsRef.current = [];
        setRows([]);
        setMeta(null);
        setHasMore(false);
        return;
      }
      if (seq !== req.current) return;
      const page = Array.isArray(result) ? result : result.items;
      if (!Array.isArray(result)) setMeta(result.meta);
      // Keyed by id: an insert between the walk's own fetches shifts the
      // offsets and can re-serve a row already collected.
      for (const row of page) window.set(row.id, row);
      lastPageFull = page.length === pageSize;
      if (!lastPageFull) break;
    }
    if (seq !== req.current) return;
    const next = [...window.values()];
    rowsRef.current = next;
    setRows(next);
    setHasMore(lastPageFull);
    setError(null);
  }, [fetchPage, pageSize]);

  const runWrite = useCallback(async <R,>(write: () => Promise<R>): Promise<R> => {
    // Claimed BEFORE the write: everything the user does from here on is
    // newer intent than this refresh, and every read issued before it is
    // older. Owning `loading` from the same moment means a write that throws
    // cannot strand a spinner it invalidated.
    const seq = ++req.current;
    setLoadingOwned(seq, true);
    try {
      const result = await write();
      if (seq === req.current) {
        await refreshWindow(seq);
      } else {
        // Superseded mid-write. The newer filter owns the view — but its GET
        // may have both started AND completed before this write's transaction
        // committed, in which case its rows do not contain the mutation and
        // nothing would ever correct them (request start order is not commit
        // order). Re-read under whatever filter is newest NOW, which is the
        // current render's reload, not the one this call closed over.
        await reloadRef.current();
      }
      return result;
    } catch (err) {
      // A rejection does not mean nothing committed: a screen's callback can
      // POST successfully and then fail on a follow-up read. So this path
      // needs BOTH re-reads the success path has —
      //   * still holding the ticket: replace the read the claim invalidated,
      //     or the discarded response leaves the screen stuck on its loading
      //     state until the user happens to change a filter;
      //   * superseded: re-read under the NEWEST filter, because that GET may
      //     have completed before the write committed and would otherwise
      //     omit the record for good (codex review).
      if (seq === req.current) await refreshWindow(seq);
      else await reloadRef.current();
      throw err;
    } finally {
      setLoadingOwned(seq, false);
    }
  }, [refreshWindow]);

  return {
    rows,
    meta,
    hasMore,
    // Screens render the control from this, so "a load is running" and "the
    // pager is offered" cannot disagree.
    canLoadMore: hasMore && !loading,
    loading,
    reloading,
    error,
    loadMore,
    runWrite,
    reload,
  };
}
