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
      // Empty, not stale: the rows that are still on screen belong to a window
      // the user has already navigated away from. The metadata goes with them
      // — a total that outlived its rows is the worst of both.
      rowsRef.current = [];
      setRows([]);
      setMeta(null);
      setHasMore(false);
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

  const loadMore = useCallback(async () => {
    if (loadingRef.current) return;
    const seq = ++req.current;
    await load(rowsRef.current?.length ?? 0, seq);
  }, [load]);

  const runWrite = useCallback(async <R,>(write: () => Promise<R>): Promise<R> => {
    // Claimed BEFORE the write: everything the user does from here on is
    // newer intent than this refresh, and every read issued before it is
    // older. Owning `loading` from the same moment means a write that throws
    // cannot strand a spinner it invalidated.
    const seq = ++req.current;
    setLoadingOwned(seq, true);
    try {
      const result = await write();
      // Superseded while the write was in flight — the newer view stands, and
      // it was fetched after this write reached the server anyway.
      if (seq === req.current) await load(0, seq);
      return result;
    } finally {
      setLoadingOwned(seq, false);
    }
  }, [load]);

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
