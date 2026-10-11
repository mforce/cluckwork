import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { Egg, FilterX, TriangleAlert } from "lucide-react";
import {
  Box, Button, List, ListItem, LinearProgress, Table, TableBody, TableCell, TableHead, TableRow, Typography,
} from "@mui/material";
import {
  getStock, recordEggLotMovement,
} from "../api/cluckwork";
import type { EggLotRow, StockRow } from "../api/cluckwork";
import { useFormat } from "../farm/useFormat";
import { FarmDate } from "../components/FarmDate";
import { useAuth } from "../auth/useAuth";
import { EmptyState } from "../components/EmptyState";
import { FilterBar, FilterDateField } from "../components/FilterBar";
import { GlossaryLink } from "../components/GlossaryLink";
import { useDialogAction } from "../components/useDialogAction";
import i18n from "../i18n";
import { stockMovementLabel } from "../i18n/enums";
import { FieldConsole, LedgerTableContainer, ConsoleSummary } from "../components/FieldConsole";
import { newId } from "../lib/ids";
import { WriteOffDialog } from "./WriteOffDialog";
import { useStockLots } from "./useStockLots";

const NOWRAP = { whiteSpace: "nowrap" as const };

// The scope that owns a dialog (#703). `run` routes a failure by this and gates
// a success by it; nothing else on this screen goes through `run`.
const DIALOG_SCOPES = ["write-off"] as const;

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
  // #703 — the flight guard (#236), the per-place message slots (#479) and the
  // dialog-session generation (#477 part 2) come from one shared hook; this
  // screen keeps only its idempotency-key and refresh discipline below, and
  // says which scope owns a dialog.
  const { busy, isPending, errors, run, openDialog, dismissDialog } = useDialogAction(DIALOG_SCOPES);
  const [rows, setRows] = useState<StockRow[] | null>(null);
  const setPageError = errors.setPage;
  const [message, setMessage] = useState<string | null>(null);
  // The lot browser: paging, filter and ledger state plus their ticket
  // discipline, moved to useStockLots (#1215).
  const {
    openGrade, openLot, lots, hasMoreLots, lotsLoading, lotsFrom, lotsTo, movements,
    setLots, setLotsLoading, setLotsFrom, setLotsTo,
    lotsReq, ledgerReq, appliedFilter, writePatches,
    toggleGrade, changeLotsFilter, loadMoreLots, toggleLot, refreshAfterWriteOff,
  } = useStockLots({ setPageError, setRows });
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
  }, [setPageError]);

  function openWriteOff(lot: EggLotRow) {
    setWoType("Discard");
    setWoDirection("remove");
    setWoQty(0);
    setWoReason("");
    // Opening is a session edge (#703). A fresh open, or a DIFFERENT lot's
    // write-off opened over this one — it DISPLACES the dialog without any
    // close running, reachable behind the backdrop via a screen reader's
    // virtual cursor (#480; pi review of #491) and, on this screen, by the
    // mouse, because the trigger carries no `disabled={busy}` — ends whatever
    // session was on screen: the displaced lot's verdict cannot render under
    // the new lot's date, and its success cannot close the new lot's form.
    // Same-lot re-entry (a reseed of THIS lot) is spared, as it always was:
    // the session is still about this lot, so its in-flight write still
    // closes it on success and its failed verdict survives the reseed.
    if (writeOffLot === null || writeOffLot.id !== lot.id) openDialog("write-off");
    setWriteOffLot(lot);
  }

  // Dismissal is the other edge: it mutes the attempt still out, so a late
  // failure from an in-flight write-off is not reported against a session the
  // user reopened, and ends the session, so a late success cannot act on it.
  const closeWriteOff = () => {
    setWriteOffLot(null);
    dismissDialog("write-off");
  };

  // #703 review r2 (PR 2) — the dialog is admin-gated (`open={isAdmin}`), so a
  // role change HIDES it without firing onClose: an in-flight write-off would
  // stay `current()` and could close the dialog a re-promotion restores. End
  // the session on the isAdmin edge (dismiss mutes + advances the generation;
  // setWriteOffLot hides it), so a re-promotion reopens a fresh one.
  useEffect(() => {
    if (!isAdmin) { setWriteOffLot(null); dismissDialog("write-off"); }
  }, [isAdmin, dismissDialog]);

  // The signed delta the API receives: only a reconciliation may add back.
  const woDelta = woType === "Reconciliation" && woDirection === "add" ? woQty : -woQty;

  async function onWriteOff(e: FormEvent) {
    e.preventDefault();
    // #703 review r2 (PR 2) — a skipped-while-busy submit must not un-mute:
    // Enter bypasses the disabled submit button, `run` would skip the second
    // attempt, but the pre-run `beginAttempt` below would still un-mute an
    // abandoned attempt and let its late failure into the reopened dialog.
    if (busy) return;
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
    // The run scope is the dialog's; the idempotency key stays per lot.
    await run("write-off", async (current) => {
      setMessage(null);
      // A failure here is the hook's to report into the dialog's slot, and the
      // key is not rotated on it — a retry replays rather than repeats. (A 4xx
      // stores no idempotency record, so an edited resubmit under the same key
      // is safe too.)
      const res = await recordEggLotMovement(lot.id, {
        movementType: woType, quantityDelta: woDelta, reason: woReason.trim(),
      }, keyFor(scope));
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
      // The write-off trigger has no `disabled={busy}` gate (an admin can act
      // on another lot's ledger while this submit is out), so the dialog may
      // already be a DIFFERENT lot's by now — or a fresh session of THIS lot,
      // reopened after a dismissal. Closing it here would be a success message
      // about the abandoned attempt slamming shut the form the admin is typing
      // into (adversarial review of #491; #703). `current()` is the hook's
      // answer to "is this still my session?": a different lot's open or a
      // dismissal ended it; a same-lot reseed did not, and still closes.
      if (!current()) return;
      setMessage(i18n.t("stock:writeOffRecordedMessage", { available: fmt.count(res.quantityAvailable) }));
      setWriteOffLot(null);
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
  }

  if (errors.page && rows === null) {
    return <FieldConsole><Typography variant="h2">{t("title")}</Typography><p className="error">{errors.page}</p></FieldConsole>;
  }
  if (rows === null) return <FieldConsole><Typography variant="h2">{t("title")}</Typography><p className="muted">{tc("loading")}</p></FieldConsole>;

  const totalAvailable = rows.reduce((a, r) => a + r.available, 0);
  // #911 — the rows the board-level band summarises. `belowFloor` is the
  // server's own comparison against available stock; the shortfall is derived
  // from the same two numbers the row already shows.
  const belowFloor = rows.filter((r) => r.belowFloor);
  // The two numbers every below-floor sentence carries, formatted once.
  const shortfall = (row: StockRow) => {
    const floor = row.lowStockFloor ?? 0;
    return { short: fmt.count(floor - row.available), floor: fmt.count(floor) };
  };
  // Largest available across the loaded rows scales every meter fill so the bars
  // read as relative stock. Guard the divide-by-zero when all rows are empty.
  const maxAvailable = rows.reduce((m, r) => Math.max(m, r.available), 0);

  return (
    <FieldConsole>
      <Typography variant="h2">{t("title")}</Typography>
      {errors.page && <p className="error" role="alert">{errors.page}</p>}
      {message && <p className="success" role="status">{message}</p>}
      {rows.length === 0 ? (
        // No page-head create action on this screen — stock is derived from
        // submitted daily entries, not directly creatable here.
        <EmptyState icon={Egg} message={t("noStockMessage")} />
      ) : (
        <>
          {belowFloor.length > 0 && (
            <Box role="status" sx={{
              display: "flex", gap: 1.25, alignItems: "flex-start", mb: 2, p: 1.5,
              bgcolor: "var(--tint-warn)", color: "var(--warn)",
              border: "1px solid currentColor", borderRadius: "var(--r-panel)",
              fontSize: ".85rem", fontWeight: 650, lineHeight: 1.4,
            }}>
              <TriangleAlert size={18} aria-hidden style={{ flexShrink: 0, marginTop: 2 }} />
              <Box>
                <Box component="span" sx={{ display: "block" }}>
                  {t("floorBandTitle", { count: belowFloor.length })}
                </Box>
                {/* Below 600px the band is its count line alone: the per-grade
                    detail is on every flagged row a thumb-scroll away. */}
                <Box component="span" sx={{ display: { xs: "none", sm: "block" }, fontWeight: 400 }}>
                  {belowFloor
                    .map((r) => t("floorBandItem", { grade: r.gradeName, ...shortfall(r) }))
                    .join(" · ")}
                </Box>
              </Box>
            </Box>
          )}
          <ConsoleSummary label={t("totalAvailableMessage", { available: fmt.count(totalAvailable), grades: rows.length })} items={[
            { label: t("availableHeader"), value: fmt.count(totalAvailable) },
            { label: t("gradesLabel"), value: fmt.count(rows.length) },
            { label: t("restrictedHeader"), value: fmt.count(rows.reduce((total, row) => total + row.restricted, 0)) },
          ]} />
          <List aria-label={t("title")} disablePadding sx={{ borderTop: "2px solid var(--ink)" }}>
            {rows.map((r) => (
              <ListItem key={r.eggGradeId} disablePadding sx={{
                borderBottom: "1px solid var(--rule)",
                ...(r.belowFloor && {
                  bgcolor: "var(--tint-warn)",
                  boxShadow: "inset 4px 0 var(--warn)",
                }),
              }}>
                <Box role="region" aria-label={r.gradeName} sx={{ width: "100%", display: "grid", gridTemplateColumns: { xs: "80px 75px minmax(0, 1fr)", md: "110px 100px minmax(80px, 1fr) 180px 120px" }, gap: { xs: 1, md: 1.5 }, alignItems: "center", py: { xs: 1, md: 2 }, px: 1 }}>
                  <Typography component="strong" sx={{ fontFamily: "Georgia, serif", fontSize: "1.1rem", fontWeight: 700 }}>{r.gradeName}</Typography>
                  <Box>
                    <Typography sx={{ fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>{fmt.count(r.available)}</Typography>
                    <Typography variant="body2" sx={{ fontSize: ".65rem", color: "text.secondary" }}>{t("availableHeader")}</Typography>
                  </Box>
                  <Box sx={{ position: "relative" }}>
                    <LinearProgress variant="determinate" value={maxAvailable > 0 ? r.available / maxAvailable * 100 : 0} aria-label={r.gradeName} sx={{ height: 8, borderRadius: "var(--r-pill)", bgcolor: "var(--surface-2)", "& .MuiLinearProgress-bar": { bgcolor: r.belowFloor ? "var(--warn)" : "var(--stat-accent)" } }} />
                    {/* Where the floor sits on the same scale. Decorative: the
                        floor and the shortfall are both stated in words beside it. */}
                    {r.lowStockFloor !== null && maxAvailable > 0 && r.lowStockFloor <= maxAvailable && (
                      <Box aria-hidden sx={{
                        position: "absolute", top: -3, bottom: -3, width: "2px",
                        left: `${r.lowStockFloor / maxAvailable * 100}%`, bgcolor: "var(--ink)", opacity: 0.55,
                      }} />
                    )}
                  </Box>
                  <Box sx={{ gridColumn: { xs: "2 / 4", md: "auto" }, fontSize: ".75rem" }}>
                    {r.belowFloor ? (
                      <>
                        {/* Icon AND word, with the whole fact as the accessible
                            name — the tint alone never carries the warning. */}
                        <Box component="span" className="badge badge-warn" sx={{ border: "1px solid currentColor" }} role="img" aria-label={t("belowFloorTagLabel", { available: fmt.count(r.available), ...shortfall(r) })}>
                          <TriangleAlert size={12} aria-hidden /> {t("belowFloorTag")}
                        </Box>
                        <Box sx={{ mt: 0.5, color: "var(--warn)", fontWeight: 650 }}>
                          {t("shortfallLine", shortfall(r))}
                        </Box>
                      </>
                    ) : (
                      <Box sx={{ color: "text.secondary", fontStyle: r.lowStockFloor === null ? "italic" : "normal" }}>
                        {r.lowStockFloor === null
                          ? t("floorNotSet")
                          : t("floorAbove", {
                            floor: fmt.count(r.lowStockFloor),
                            over: fmt.count(r.available - r.lowStockFloor),
                          })}
                      </Box>
                    )}
                    <Box sx={{ mt: 0.5, color: r.restricted > 0 ? "var(--warn)" : "text.secondary" }}>
                      {r.restricted > 0 ? <><span>{fmt.count(r.restricted)}</span>{" "}{t("restrictedHeader")}</> : t("noRestrictions")}
                    </Box>
                  </Box>
                  <Button variant="outlined" color="inherit" aria-expanded={openGrade === r.eggGradeId} sx={{ gridColumn: { xs: "2 / 4", md: "auto" }, height: { xs: 44, md: "auto" } }} onClick={() => void toggleGrade(r.eggGradeId)}>
                    {openGrade === r.eggGradeId ? t("hideLotsButton") : t("lotsButton")}
                  </Button>
                </Box>
              </ListItem>
            ))}
          </List>
          <Typography component="p" sx={{ fontSize: ".7rem", color: "text.secondary", mt: 1 }}>{t("restrictionPolicy")}<GlossaryLink term="WithdrawalRestriction" />{" "}{t("floorPolicy")}<GlossaryLink term="LowStockFloor" /></Typography>

          {openGrade !== null && (
            <>
              <h3>{t("lotsHeading")}</h3>
              {/* Filter on the server so older lots are reachable without paging through history. */}
              <FilterBar>
                <FilterDateField label={t("fromLabel")} value={lotsFrom}
                  onChange={(e) => void changeLotsFilter(e.target.value, lotsTo)} />
                <FilterDateField label={t("toLabel")} value={lotsTo}
                  onChange={(e) => void changeLotsFilter(lotsFrom, e.target.value)} />
                <Button variant="outlined" color="inherit" sx={{ borderRadius: "4px" }} onClick={() => void changeLotsFilter("", "")}>{tc("clearFiltersButton")}</Button>
              </FilterBar>
              {lots.length === 0 ? (
                (lotsFrom || lotsTo)
                  ? <EmptyState icon={FilterX} message={t("noLotsMatch")} />
                  : <EmptyState icon={Egg} message={t("noLotsMessage")} />
              ) : (
                <LedgerTableContainer>
                  <Table size="small">
                    <TableHead>
                      <TableRow>
                        <TableCell>{t("producedOnHeader")}</TableCell>
                        <TableCell align="right">{t("producedHeader")}</TableCell>
                        <TableCell align="right">{t("availableHeader")}</TableCell>
                        <TableCell></TableCell>
                      </TableRow>
                    </TableHead>
                    <TableBody>
                      {lots.map((l) => (
                        <TableRow key={l.id}>
                          <TableCell sx={NOWRAP}><FarmDate iso={l.productionDate} /></TableCell>
                          <TableCell align="right">{fmt.count(l.quantityProduced)}</TableCell>
                          <TableCell align="right">{fmt.count(l.quantityAvailable)}</TableCell>
                          <TableCell sx={NOWRAP}>
                            {/* Lot audit records only manual adjustments; production and sales audit their own entities. */}
                            {/* The audit endpoint is AdminOnly. */}
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
                          </TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                </LedgerTableContainer>
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
                  <LedgerTableContainer>
                    <Table size="small">
                      <TableHead>
                        <TableRow>
                          <TableCell>{t("ledgerWhenHeader")}</TableCell>
                          <TableCell>{t("ledgerTypeHeader")}<GlossaryLink term="EggMovementLedger" /></TableCell>
                          <TableCell align="right">{t("ledgerChangeHeader")}</TableCell>
                          <TableCell>{t("ledgerReasonHeader")}</TableCell>
                        </TableRow>
                      </TableHead>
                      <TableBody>
                        {movements.map((m) => (
                          <TableRow key={m.id}>
                            <TableCell sx={NOWRAP}>{m.createdAtUtc.replace("T", " ").slice(0, 19)}</TableCell>
                            <TableCell>{stockMovementLabel(m.movementType)}</TableCell>
                            <TableCell align="right">{m.quantityDelta > 0 ? `+${fmt.count(m.quantityDelta)}` : fmt.count(m.quantityDelta)}</TableCell>
                            <TableCell>{m.reason ?? "—"}</TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  </LedgerTableContainer>
                </>
              )}
            </>
          )}
        </>
      )}

      {/* #406 — the write-off dialog. Also gated on isAdmin so a mid-session
          demotion can't leave a stale dialog open. */}
      {writeOffLot !== null && (
        <WriteOffDialog
          writeOffLot={writeOffLot}
          isAdmin={isAdmin}
          busy={busy}
          isPending={isPending}
          errors={errors}
          closeWriteOff={closeWriteOff}
          onWriteOff={onWriteOff}
          woType={woType} setWoType={setWoType}
          woDirection={woDirection} setWoDirection={setWoDirection}
          woQty={woQty} setWoQty={setWoQty}
          woReason={woReason} setWoReason={setWoReason}
          woDelta={woDelta}
        />
      )}
    </FieldConsole>
  );
}
