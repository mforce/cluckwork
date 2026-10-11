import { useRef, useState } from "react";
import type { Dispatch, SetStateAction } from "react";
import { getStock, listEggLotMovements, listEggLots } from "../api/cluckwork";
import type { EggLotRow, EggMovementRow, StockRow } from "../api/cluckwork";
import i18n from "../i18n";

// Matches the API's default page size — a full page means there may be more.
const LOT_PAGE = 50;

// The stock screen's lot browser (#465): the open grade's paged lot list, its
// date filter and each lot's movement ledger, moved out of StockPage (#1215).
// Every read here is event-driven — a grade toggle, filter change, load-more
// or ledger toggle from the page — never an effect. The page keeps `rows`
// (getStock) and the write-off form; this hook takes those two setters because
// refreshAfterWriteOff re-reads stock and every failure paints through the
// page's own error slot.
export function useStockLots({ setPageError, setRows }: {
  setPageError: (text: string | null) => void;
  setRows: Dispatch<SetStateAction<StockRow[] | null>>;
}) {
  const [openGrade, setOpenGrade] = useState<string | null>(null);
  const [lots, setLots] = useState<EggLotRow[]>([]);
  // #465 — the drill-down pages server-side (the API caps a page at 50) and
  // filters by production date, so lots older than the newest page stay
  // reachable for history and write-off.
  const [hasMoreLots, setHasMoreLots] = useState(false);
  // While any interactive lot-list load is in flight, load-more is hidden —
  // clicking it mid-filter-load would supersede the page-0 request and
  // append the new window's page onto the old window's rows (codex review).
  const [lotsLoading, setLotsLoading] = useState(false);
  const [lotsFrom, setLotsFrom] = useState("");
  const [lotsTo, setLotsTo] = useState("");
  const [openLot, setOpenLot] = useState<string | null>(null);
  const [movements, setMovements] = useState<EggMovementRow[] | null>(null);

  // One page of lots under a filter; empty date strings mean "no bound".
  function fetchLotPage(gradeId: string, from: string, to: string, offset: number) {
    return listEggLots({
      gradeId, from: from || undefined, to: to || undefined,
      limit: LOT_PAGE, offset,
    });
  }

  // Monotonic ticket per lot-list load: two quick filter changes race their
  // responses, and the broader (older) one can settle LAST — without this it
  // would overwrite the narrower view the user actually asked for.
  const lotsReq = useRef(0);
  // The filter the visible rows actually came from — the rollback target for
  // a failed change. The optimistic input values are NOT it: with two
  // overlapping changes, the first may never have applied (codex review).
  const appliedFilter = useRef({ from: "", to: "" });
  // Ledger loads get their own ticket: a pending history request must not
  // resurrect the ledger after a filter/grade change cleared it.
  const ledgerReq = useRef(0);
  // Durable write results that in-flight reads may predate: a GET issued
  // while a write-off POST was pending can snapshot the OLD balance yet
  // settle after the patch. Each entry records the ticket at the moment the
  // write completed; a load claimed at seq <= tick may carry stale data for
  // that lot and re-applies the patch, a later load is proven fresh and
  // retires the entry (codex review).
  const writePatches = useRef(new Map<string, { available: number; tick: number }>());

  function reconcileWrites(page: EggLotRow[], seq: number): EggLotRow[] {
    const patches = writePatches.current;
    if (patches.size === 0) return page;
    const out = page.map((l) => {
      const p = patches.get(l.id);
      return p !== undefined && seq <= p.tick
        ? { ...l, quantityAvailable: p.available } : l;
    });
    for (const [id, p] of patches) if (seq > p.tick) patches.delete(id);
    return out;
  }

  async function toggleGrade(gradeId: string) {
    setOpenLot(null);
    setMovements(null);
    ledgerReq.current++;
    if (openGrade === gradeId) {
      // Collapsing invalidates any pending lot load too: its late failure
      // would otherwise paint loadLotsFailed under a closed panel, and a
      // late success would mutate hidden paging state (codex review).
      lotsReq.current++;
      setLotsLoading(false);
      setOpenGrade(null);
      return;
    }
    const seq = ++lotsReq.current;
    setLotsLoading(true);
    try {
      // The filter is scoped to one grade's panel, so the new grade loads
      // unfiltered — but the inputs are only CLEARED on success: a failed
      // switch leaves the old grade's filtered rows visible, and blank
      // inputs would misdescribe them (codex review).
      const page = await fetchLotPage(gradeId, "", "", 0);
      if (seq !== lotsReq.current) return;
      setLotsFrom("");
      setLotsTo("");
      appliedFilter.current = { from: "", to: "" };
      setLots(reconcileWrites(page, seq));
      setHasMoreLots(page.length === LOT_PAGE);
      setLotsLoading(false);
      setOpenGrade(gradeId);
      // A ledger opened while this load was in flight claimed a NEWER
      // ledger ticket than the entry-time invalidation — the committing
      // page replaces the window wholesale, so clear again (codex review).
      setOpenLot(null);
      setMovements(null);
      ledgerReq.current++;
      setPageError(null);
    } catch {
      if (seq === lotsReq.current) {
        // The still-visible rows are the old grade's applied window — and if
        // this switch superseded an unfinished filter change, the inputs
        // still hold that never-applied value; restore both from the applied
        // snapshot (codex review).
        setLotsFrom(appliedFilter.current.from);
        setLotsTo(appliedFilter.current.to);
        setLotsLoading(false);
        setPageError(i18n.t("stock:loadLotsFailed"));
      }
    }
  }

  // Filter changes restart from the top; the values are passed explicitly
  // because the state set on the previous line hasn't rendered yet.
  async function changeLotsFilter(from: string, to: string) {
    if (openGrade === null) return;
    setLotsFrom(from);
    setLotsTo(to);
    // The expanded lot may not be in the filtered page at all — its ledger
    // must not linger under an unrelated list, and a PENDING history load
    // must not resurrect it after this clear (codex review).
    setOpenLot(null);
    setMovements(null);
    ledgerReq.current++;
    const seq = ++lotsReq.current;
    setLotsLoading(true);
    try {
      const page = await fetchLotPage(openGrade, from, to, 0);
      if (seq !== lotsReq.current) return;
      appliedFilter.current = { from, to };
      setLots(reconcileWrites(page, seq));
      setHasMoreLots(page.length === LOT_PAGE);
      setLotsLoading(false);
      // Same wholesale-replace rule as the grade-switch commit: a ledger
      // opened during this load outlived the entry-time clear (codex review).
      setOpenLot(null);
      setMovements(null);
      ledgerReq.current++;
      setPageError(null);
    } catch {
      // Roll the inputs back to the window the still-visible rows actually
      // came from — NOT the previous optimistic value, which with two
      // overlapping changes may itself never have applied (codex review).
      if (seq === lotsReq.current) {
        setLotsFrom(appliedFilter.current.from);
        setLotsTo(appliedFilter.current.to);
        setLotsLoading(false);
        setPageError(i18n.t("stock:loadLotsFailed"));
      }
    }
  }

  async function loadMoreLots() {
    if (openGrade === null) return;
    const seq = ++lotsReq.current;
    setLotsLoading(true);
    try {
      const page = await fetchLotPage(openGrade, lotsFrom, lotsTo, lots.length);
      if (seq !== lotsReq.current) return;
      // A lot created between page loads shifts every offset, so the next
      // page can re-serve rows already shown — dedupe by id on append. (The
      // shifted-in newest lot itself appears on the next full reload; plain
      // offset paging has no cursor to catch it mid-scroll.)
      const fresh = reconcileWrites(page, seq);
      setLots((prev) => {
        const seen = new Set(prev.map((l) => l.id));
        return [...prev, ...fresh.filter((l) => !seen.has(l.id))];
      });
      setHasMoreLots(page.length === LOT_PAGE);
      setLotsLoading(false);
      setPageError(null);
    } catch {
      if (seq === lotsReq.current) {
        setLotsLoading(false);
        setPageError(i18n.t("stock:loadLotsFailed"));
      }
    }
  }

  async function toggleLot(lotId: string) {
    if (openLot === lotId) {
      setOpenLot(null);
      setMovements(null);
      ledgerReq.current++;
      return;
    }
    const seq = ++ledgerReq.current;
    try {
      const list = await listEggLotMovements(lotId);
      // Cleared or replaced while in flight (filter change, grade switch,
      // close): a late settle must not resurrect the ledger (codex review).
      if (seq !== ledgerReq.current) return;
      setMovements(list);
      setOpenLot(lotId);
      setPageError(null);
    } catch {
      if (seq === ledgerReq.current) setPageError(i18n.t("stock:loadMovementsFailed"));
    }
  }

  // Everything the write-off changed, refetched together so the by-grade
  // totals, the lot row and an open ledger never disagree with each other.
  // The lot list is re-walked page-by-page over the WHOLE loaded window under
  // the active filter — one default-sized request would silently collapse a
  // "load more"-extended view back to the newest page mid-correction (#465).
  // `seq` is the ticket claimed when the write-off was SUBMITTED — not here.
  // Claiming inside this function (after the record POST, or worse after
  // getStock()) would retroactively invalidate any grade/filter load the
  // user started in between, then paint the old grade's rows under the new
  // panel (codex review). Tickets follow intent order: anything the user
  // does after submit outranks this refresh, which then only updates the
  // grade-independent totals and skips the lot walk.
  async function refreshAfterWriteOff(lot: EggLotRow, seq: number, ledgerSeq: number) {
    setRows(await getStock());
    if (openGrade !== null && seq === lotsReq.current) {
      const target = Math.max(lots.length, 1);
      // Keyed by id: an insert between the walk's own page fetches shifts the
      // offsets and can re-serve a row (same drift as loadMoreLots).
      const window = new Map<string, EggLotRow>();
      let lastPageFull = false;
      for (let offset = 0; offset < target; offset += LOT_PAGE) {
        let page: EggLotRow[];
        try {
          page = await fetchLotPage(openGrade, lotsFrom, lotsTo, offset);
        } catch (err) {
          // A superseded page's failure is moot — the newer load already
          // owns the view; surfacing it would paint loadStockFailed over a
          // healthy screen (codex review). A current-ticket failure still
          // propagates to the caller's stale-view handler.
          if (seq !== lotsReq.current) return;
          throw err;
        }
        // Superseded mid-walk (a filter change, a grade switch): stop issuing
        // page requests whose results the final check would only discard.
        if (seq !== lotsReq.current) return;
        for (const l of page) window.set(l.id, l);
        lastPageFull = page.length === LOT_PAGE;
        if (!lastPageFull) break;
      }
      if (seq === lotsReq.current) {
        setLots(reconcileWrites([...window.values()], seq));
        setHasMoreLots(lastPageFull);
        // The walk fetched under the CURRENT inputs (which may be a filter
        // this write-off superseded mid-flight): those values are now the
        // window on screen, so commit them as applied — a later failure
        // must roll back to them, not to an older snapshot (codex review).
        appliedFilter.current = { from: lotsFrom, to: lotsTo };
      }
    }
    // Refresh the open ledger only while the submit-time ledger intent still
    // stands — checked again after the fetch, since the user can open another
    // lot's History while this request is in flight (codex review).
    if (openLot === lot.id && ledgerSeq === ledgerReq.current) {
      let list: EggMovementRow[];
      try {
        list = await listEggLotMovements(lot.id);
      } catch (err) {
        // Same rule as the walk pages: a superseded fetch's failure is moot —
        // a newer History already owns the ledger; only a current-intent
        // failure reaches the caller's stale-view handler (codex review).
        if (ledgerSeq !== ledgerReq.current) return;
        throw err;
      }
      if (ledgerSeq === ledgerReq.current) setMovements(list);
    }
  }

  return {
    openGrade, lots, hasMoreLots, lotsLoading, lotsFrom, lotsTo, openLot, movements,
    setLots, setLotsLoading, setLotsFrom, setLotsTo,
    lotsReq, ledgerReq, appliedFilter, writePatches,
    toggleGrade, changeLotsFilter, loadMoreLots, toggleLot, refreshAfterWriteOff,
  };
}
