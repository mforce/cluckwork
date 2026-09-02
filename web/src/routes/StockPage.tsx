import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { Egg, FilterX } from "lucide-react";
import {
  getStock, listEggLotMovements, listEggLots, recordEggLotMovement,
} from "../api/cluckwork";
import type { EggLotRow, EggMovementRow, StockRow } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useFormat } from "../farm/useFormat";
import { FarmDate } from "../components/FarmDate";
import { useAuth } from "../auth/useAuth";
import { BusyButton } from "../components/BusyButton";
import { EmptyState } from "../components/EmptyState";
import { GlossaryLink } from "../components/GlossaryLink";
import { Dialog } from "../components/Dialog";
import { DialogError } from "../components/DialogError";
import { NumberField } from "../components/NumberField";
import { useDialogErrors } from "../components/useDialogErrors";
import { usePendingAction } from "../components/usePendingAction";
import i18n from "../i18n";
import { stockMovementLabel } from "../i18n/enums";
import { newId } from "../lib/ids";

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// Matches the API's default page size — a full page means there may be more.
const LOT_PAGE = 50;

// F2 (#22): current sellable stock by grade; withdrawal-restricted quantities
// are shown separately — they exist but cannot be sold yet.
// #101: each grade expands into its lots, each lot into its movement ledger —
// the explicit rows behind every cached balance.
// #406: admins write off lost stock (or apply a recount) per lot — available
// moves, the daily entry's production figures never do.
export function StockPage() {
  const { t } = useTranslation("stock");
  const fmt = useFormat();
  const { t: tc } = useTranslation("common");
  // UI visibility only (#73/#103) — the endpoint re-checks the role.
  const { isAdmin } = useAuth();
  const { busy, isPending, run: runPending } = usePendingAction();
  const [rows, setRows] = useState<StockRow[] | null>(null);
  // #479 — one slot per PLACE a message can appear. This screen already kept
  // the write-off dialog's own failures in a separate hand-rolled
  // `dialogError` state, so this conversion is for uniformity with the other
  // screens, not a bug fix — the one gap the shared hook closes for free is
  // muting a late failure from an attempt the dialog abandoned mid-flight.
  const errors = useDialogErrors();
  const setPageError = errors.setPage;
  const [message, setMessage] = useState<string | null>(null);
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

  // #406 write-off dialog: the lot being corrected + its form fields.
  const [writeOffLot, setWriteOffLot] = useState<EggLotRow | null>(null);
  const [woType, setWoType] = useState("Discard");
  // Reconciliation only: a recount can find eggs as well as lose them.
  const [woDirection, setWoDirection] = useState("remove");
  const [woQty, setWoQty] = useState(0);
  const [woReason, setWoReason] = useState("");

  // Stable idempotency keys per logical mutation, rotated only after the full
  // action (write + refresh) succeeds — same contract as the other screens.
  const keys = useRef(new Map<string, string>());
  const keyFor = (scope: string) => {
    const existing = keys.current.get(scope);
    if (existing) return existing;
    const fresh = newId();
    keys.current.set(scope, fresh);
    return fresh;
  };
  const clearKey = (scope: string) => keys.current.delete(scope);

  useEffect(() => {
    getStock()
      .then(setRows)
      .catch(() => setPageError(i18n.t("stock:loadStockFailed")));
  }, []);

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

  // Mirrors `writeOffLot` synchronously. `onWriteOff` is an async function
  // whose closure captures `writeOffLot` as it read at SUBMIT time — reading
  // the state itself after an `await` would silently see that stale value
  // forever, not the lot the dialog has since rebound to. The ref is updated
  // everywhere `writeOffLot` is, so a post-`await` read reflects reality
  // (adversarial review of #491).
  const activeWriteOffLotId = useRef<string | null>(null);

  function openWriteOff(lot: EggLotRow) {
    setWoType("Discard");
    setWoDirection("remove");
    setWoQty(0);
    setWoReason("");
    // #479 — no reset for a plain open. Every DISMISSAL goes through
    // closeWriteOff, whose `abandon` cleared the slot and muted any attempt
    // still in flight on the way out. The success path closes with a bare
    // setWriteOffLot(null), where the slot is empty by construction: the
    // attempt did not fail. The one door left: a DIFFERENT lot's write-off
    // opened over this one DISPLACES it without any close running, and the
    // scope is fixed — so the displaced lot's verdict would render under the
    // new lot's date. Reachable behind the backdrop via a screen reader's
    // virtual cursor (#480; pi review of #491).
    if (writeOffLot !== null && writeOffLot.id !== lot.id) errors.abandon("write-off");
    activeWriteOffLotId.current = lot.id;
    setWriteOffLot(lot);
  }

  // Dismissal empties the dialog's slot and mutes the attempt still out, so a
  // late failure from an in-flight write-off is not reported against a
  // session the user reopened.
  const closeWriteOff = () => {
    activeWriteOffLotId.current = null;
    setWriteOffLot(null);
    errors.abandon("write-off");
  };

  // The signed delta the API receives: only a reconciliation may add back.
  const woDelta = woType === "Reconciliation" && woDirection === "add" ? woQty : -woQty;

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

  async function onWriteOff(e: FormEvent) {
    e.preventDefault();
    const lot = writeOffLot;
    if (lot === null) return;
    // Clears and un-mutes the slot up front — before the validation writes
    // below, not just the network write's — so a validation failure right
    // after a reopened dialog is never dropped as belonging to an abandoned
    // session (#479).
    errors.beginAttempt("write-off");
    if (woQty <= 0) {
      errors.report("write-off", i18n.t("stock:writeOffQuantityRequired"));
      return;
    }
    if (!woReason.trim()) {
      errors.report("write-off", i18n.t("stock:writeOffReasonRequired"));
      return;
    }
    // Coarse pre-check only — the server additionally caps a positive
    // reconciliation at the lot's cumulative write-off total (which this
    // screen doesn't know); its 422 renders through the write-off dialog slot below.
    const result = lot.quantityAvailable + woDelta;
    if (result < 0 || result > lot.quantityProduced) {
      errors.report("write-off", i18n.t("stock:writeOffOutOfRangeMessage", { produced: lot.quantityProduced }));
      return;
    }
    const scope = `write-off:${lot.id}`;
    // The refresh's ticket, claimed at submit time: any grade/filter action
    // the user takes from here on is newer intent and outranks the refresh.
    // The claim also assumes ownership of the loading flag — a superseded
    // filter load can no longer clear it, so this operation must release it
    // when it settles, whatever the outcome (codex review).
    const lotSeq = ++lotsReq.current;
    // Snapshot (not claim) of the ledger intent at submit: the refresh only
    // re-fetches the ledger that was open THEN, and stands down if any newer
    // ledger action has happened by the time it runs (codex review).
    const ledgerSeq = ledgerReq.current;
    setLotsLoading(true);
    const outcome = await runPending(scope, async () => {
      setMessage(null);
      let res;
      try {
        res = await recordEggLotMovement(lot.id, {
          movementType: woType, quantityDelta: woDelta, reason: woReason.trim(),
        }, keyFor(scope));
      } catch (err) {
        // No definitive success — keep the key so a retry replays rather than
        // repeats. (A 4xx stores no idempotency record, so an edited resubmit
        // under the same key is safe too.)
        errors.report("write-off", errText(err));
        return undefined;
      }
      // The write is durable the moment the server answers — rotate NOW.
      // Holding the key past this point would hash-conflict a later submit
      // with edited values while the dialog is still open (codex review).
      clearKey(scope);
      // Patch the visible row from the write's own response, ticket-blind:
      // if a newer filter/page GET raced this POST, its data predates the
      // mutation, and the superseded refresh below will skip its walk — this
      // is the only correction that always lands (codex review). A refresh
      // that does run replaces it with the same server truth.
      setLots((prev) => prev.map((l) =>
        l.id === lot.id ? { ...l, quantityAvailable: res.quantityAvailable } : l));
      // Remember the durable result so a pre-mutation GET that settles
      // AFTER this patch re-applies it instead of restoring the stale
      // balance (codex review; see writePatches).
      writePatches.current.set(lot.id,
        { available: res.quantityAvailable, tick: lotsReq.current });
      try {
        await refreshAfterWriteOff(lot, lotSeq, ledgerSeq);
      } catch {
        // Only the view is stale; the correction itself landed.
        setPageError(i18n.t("stock:loadStockFailed"));
      }
      return res;
    });
    // Release the loading flag claimed at submit — success, failure, or
    // skipped walk alike — unless something newer has taken ownership. The
    // inputs re-sync to the applied snapshot at the same moment: after a
    // successful walk that committed its window this is a no-op, and on any
    // failure it rolls back a filter this operation superseded before that
    // filter could apply or roll itself back (codex review).
    if (lotSeq === lotsReq.current) {
      setLotsLoading(false);
      setLotsFrom(appliedFilter.current.from);
      setLotsTo(appliedFilter.current.to);
    }
    // The write-off trigger has no `disabled={busy}` gate (an admin can act
    // on another lot's ledger while this submit is out), so the dialog may
    // already be a DIFFERENT lot's by now. Closing it here would be a
    // success message about lot A slamming shut lot B's still-open form —
    // typed quantity and all (adversarial review of #491). Same-lot
    // re-entry (a reseed of THIS lot) still closes normally. Read via the
    // ref, not the `writeOffLot` state this closure captured at submit time
    // — that value is frozen at lot A and would never see the switch.
    if (outcome && activeWriteOffLotId.current === lot.id) {
      activeWriteOffLotId.current = null;
      setMessage(i18n.t("stock:writeOffRecordedMessage", { available: fmt.count(outcome.quantityAvailable) }));
      setWriteOffLot(null);
    }
  }

  if (errors.page && rows === null) {
    return <section><h2>{t("title")}</h2><p className="error">{errors.page}</p></section>;
  }
  if (rows === null) return <section><h2>{t("title")}</h2><p className="muted">{tc("loading")}</p></section>;

  const totalAvailable = rows.reduce((a, r) => a + r.available, 0);
  // Largest available across the loaded rows scales every meter fill so the bars
  // read as relative stock. Guard the divide-by-zero when all rows are empty.
  const maxAvailable = rows.reduce((m, r) => Math.max(m, r.available), 0);

  return (
    <section>
      <h2>{t("title")}</h2>
      {errors.page && <p className="error" role="alert">{errors.page}</p>}
      {message && <p className="success" role="status">{message}</p>}
      {rows.length === 0 ? (
        // No page-head create action on this screen — stock is derived from
        // submitted daily entries, not directly creatable here.
        <EmptyState icon={Egg} message={t("noStockMessage")} />
      ) : (
        <>
          <table className="data">
            <thead>
              <tr><th>{t("gradeHeader")}</th><th className="num">{t("availableHeader")}</th><th className="num">{t("restrictedHeader")}<GlossaryLink term="WithdrawalRestriction" /></th><th></th></tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.eggGradeId}>
                  <td>{r.gradeName}</td>
                  <td className="num">
                    {fmt.count(r.available)}
                    <div className="meter" aria-hidden="true">
                      <span style={{ width: (maxAvailable > 0 ? (r.available / maxAvailable) * 100 : 0) + "%" }} />
                    </div>
                  </td>
                  <td className="num">{r.restricted > 0 ? <span className="badge badge-warn">{fmt.count(r.restricted)}</span> : "—"}</td>
                  <td>
                    <button className="link" onClick={() => void toggleGrade(r.eggGradeId)}>
                      {openGrade === r.eggGradeId ? t("hideLotsButton") : t("lotsButton")}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="muted">{t("totalAvailableMessage", { available: fmt.count(totalAvailable), grades: rows.length })}</p>

          {openGrade !== null && (
            <>
              <h3>{t("lotsHeading")}</h3>
              {/* #465 — a server-side production-date window, so an old lot is
                  findable without paging the whole history to it. */}
              <div className="filters">
                <label>{t("fromLabel")}
                  <input type="date" value={lotsFrom}
                    onChange={(e) => void changeLotsFilter(e.target.value, lotsTo)} />
                </label>
                <label>{t("toLabel")}
                  <input type="date" value={lotsTo}
                    onChange={(e) => void changeLotsFilter(lotsFrom, e.target.value)} />
                </label>
              </div>
              {lots.length === 0 ? (
                // lotsFrom/lotsTo are this section's own filter, unrelated to
                // any page-head action — "filtered to nothing" offers Clear
                // filters only when a filter is actually set.
                (lotsFrom || lotsTo)
                  ? <EmptyState icon={FilterX} message={t("noLotsMessage")}
                      action={{ label: tc("clearFiltersButton"), onClick: () => void changeLotsFilter("", "") }} />
                  : <EmptyState icon={Egg} message={t("noLotsMessage")} />
              ) : (
                <table className="data">
                  <thead>
                    <tr><th>{t("producedOnHeader")}</th><th className="num">{t("producedHeader")}</th><th className="num">{t("availableHeader")}</th><th></th></tr>
                  </thead>
                  <tbody>
                    {lots.map((l) => (
                      <tr key={l.id}>
                        <td className="nowrap"><FarmDate iso={l.productionDate} /></td>
                        <td className="num">{fmt.count(l.quantityProduced)}</td>
                        <td className="num">{fmt.count(l.quantityAvailable)}</td>
                        <td>
                          {/* #493 — the audit trail for MANUAL ADJUSTMENTS to
                              this lot (write-offs, recounts), distinct from
                              the button below: that one toggles the
                              inventory MOVEMENT ledger in place (quantities
                              in/out), a different and older trail. Two
                              affordances on the same row on purpose — kept
                              visibly separate by label so they don't read as
                              the same thing.
                              Deliberately NOT "Audit history"/viewHistoryLink
                              (codex review of #516): the only audit action
                              ever written against an EggLot's own entityId is
                              a manual write-off/recount
                              (RecordEggLotMovementHandler) — creation is
                              recorded against the Daily Entry that produced
                              the lot, allocation/restoration against the
                              Sales Order, so
                              a normal never-adjusted lot would show nothing
                              under the generic "full audit trail" label the
                              other five screens use accurately. */}
                          {/* Admin-gated: /api/v1/audit is AdminOnly, and
                              this screen is readable by non-admins too
                              (codex review of #516). */}
                          {isAdmin && (
                            <Link className="link" to={`/audit?entityId=${l.id}`}>
                              {tc("recordHistory.viewAdjustmentHistoryLink")}
                            </Link>
                          )}
                          <button className="link" onClick={() => void toggleLot(l.id)}>
                            {openLot === l.id ? t("hideHistoryButton") : t("historyButton")}
                          </button>
                          {isAdmin && (
                            <button className="link" onClick={() => openWriteOff(l)}>
                              {t("writeOffButton")}
                            </button>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
              {hasMoreLots && !lotsLoading && (
                <button className="link" onClick={() => void loadMoreLots()}>
                  {t("loadMoreButton")}
                </button>
              )}
              {/* Why the action is unavailable, in the place it would be. */}
              {!isAdmin && lots.length > 0 && (
                <p className="muted">{t("writeOffNeedsAdminMessage")}</p>
              )}

              {openLot !== null && movements !== null && (
                <>
                  <h4>{t("movementLedgerHeading")}</h4>
                  <p className="muted">
                    {t("movementLedgerIntro")}
                  </p>
                  <table className="data">
                    <thead>
                      <tr><th>{t("ledgerWhenHeader")}</th><th>{t("ledgerTypeHeader")}<GlossaryLink term="EggMovementLedger" /></th><th className="num">{t("ledgerChangeHeader")}</th><th>{t("ledgerReasonHeader")}</th></tr>
                    </thead>
                    <tbody>
                      {movements.map((m) => (
                        <tr key={m.id}>
                          <td className="nowrap">{m.createdAtUtc.replace("T", " ").slice(0, 19)}</td>
                          <td>{stockMovementLabel(m.movementType)}</td>
                          <td className="num">{m.quantityDelta > 0 ? `+${fmt.count(m.quantityDelta)}` : fmt.count(m.quantityDelta)}</td>
                          <td>{m.reason ?? "—"}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </>
              )}
            </>
          )}
        </>
      )}

      {/* #406 — the write-off dialog. Also gated on isAdmin so a mid-session
          demotion can't leave a stale dialog open. */}
      {writeOffLot !== null && (
        <Dialog open={isAdmin} title={t("writeOffDialogTitle", { date: fmt.date(writeOffLot.productionDate) })}
          onClose={closeWriteOff}>
          <form className="form-grid" onSubmit={(e) => void onWriteOff(e)}>
            <label>{t("writeOffTypeLabel")}
              <select value={woType} onChange={(e) => setWoType(e.target.value)}>
                <option value="Discard">{stockMovementLabel("Discard")}</option>
                <option value="InternalUse">{stockMovementLabel("InternalUse")}</option>
                <option value="Reconciliation">{stockMovementLabel("Reconciliation")}</option>
              </select>
            </label>
            {woType === "Reconciliation" && (
              <label>{t("writeOffDirectionLabel")}
                <select value={woDirection} onChange={(e) => setWoDirection(e.target.value)}>
                  <option value="remove">{t("writeOffDirectionRemoveOption")}</option>
                  <option value="add">{t("writeOffDirectionAddOption")}</option>
                </select>
              </label>
            )}
            {/* Sibling label, not wrapping — the stepper carries two buttons
                and a <label> may not contain interactive content other than
                its own control (#250). */}
            <div className="numfield-field">
              <label htmlFor="write-off-qty">{t("writeOffQuantityLabel")}</label>
              <NumberField id="write-off-qty" label={t("writeOffQuantityLabel").toLowerCase()}
                value={woQty} onChange={setWoQty} min={0} />
            </div>
            <label>{t("writeOffReasonLabel")}
              <input value={woReason} maxLength={500} required
                onChange={(e) => setWoReason(e.target.value)} />
            </label>
            {woQty > 0 && (
              <p className="muted">
                {t("writeOffPreviewMessage", {
                  current: writeOffLot.quantityAvailable,
                  result: writeOffLot.quantityAvailable + woDelta,
                })}
              </p>
            )}
            {/* #479 — the "write-off" scope; DialogError adds role="alert",
                which this bare paragraph never carried (deliberate
                improvement, not a behavior this conversion is fixing). */}
            <DialogError errors={errors} scope="write-off" />
            <div className="dialog-foot">
              <button type="button" className="link" onClick={closeWriteOff}>{tc("cancel")}</button>
              <BusyButton type="submit" busy={isPending(`write-off:${writeOffLot.id}`)} disabled={busy}>
                {t("writeOffSubmitButton")}
              </BusyButton>
            </div>
          </form>
        </Dialog>
      )}
    </section>
  );
}
