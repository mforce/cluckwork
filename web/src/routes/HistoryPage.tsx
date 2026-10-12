import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { FilterX, Inbox } from "lucide-react";
import { Typography } from "@mui/material";
import {
  listDailyEntries, listEggGrades, listEggUnitConversions,
  listFlocks, voidDailyEntry,
} from "../api/cluckwork";
import type { DailyEntry, EggGrade, EggUnitConversion, Flock } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useFormat } from "../farm/useFormat";
import { useAuth } from "../auth/useAuth";
import { EmptyState } from "../components/EmptyState";
import { FieldConsole, ConsoleSummary } from "../components/FieldConsole";
import { useConfirm } from "../components/useConfirm";
import { useDialogAction } from "../components/useDialogAction";
import { usePagedList } from "../components/usePagedList";
import { useFarm } from "../farm/useFarm";
import { newId } from "../lib/ids";
import { resolveStepperUnit } from "../lib/stepperUnit";
import { useMe } from "../session/SessionContext";
import i18n from "../i18n";
import { HistoryAdjustDialog } from "./history/HistoryAdjustDialog";
import { HistoryDetailsDialog } from "./history/HistoryDetailsDialog";
import { HistoryFilters } from "./history/HistoryFilters";
import { HistoryLedgers } from "./history/HistoryLedgers";
import { useHistoryAdjust } from "./history/useHistoryAdjust";

const PAGE = 50;

// The scope that owns a dialog (#703). `run` routes a failure by this and gates
// a success by it; `void:<id>` from the row button reports to the page and is
// never superseded.
const DIALOG_SCOPES = ["adjust"] as const;

// Module-level helper — outside the hook's render context, so it always uses
// the imperative i18n.t() singleton (see CONTRIBUTING-i18n.md).
function errText(err: unknown): string {
  // Concurrent-correction conflicts get a human message instead of raw problem text.
  if (err instanceof ApiError && err.status === 409)
    return i18n.t("history:concurrentConflictMessage");
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// #24 (entries half): browse recorded daily entries, newest first, with
// flock + date-range filters and offset paging. #69 (part 2): admins can
// adjust or void submitted/locked entries from here — the API reconciles
// stock and the bird ledger and enforces the role either way.
export function HistoryPage() {
  const { t } = useTranslation("history");
  const fmt = useFormat();
  const { t: tc } = useTranslation("common");
  const { isAdmin } = useAuth();
  const { askReason, confirmDialog } = useConfirm();
  const [flocks, setFlocks] = useState<Flock[]>([]);
  const [grades, setGrades] = useState<EggGrade[]>([]);
  // #444 — the adjust dialog's steppers use the same resolved pack unit as
  // the capture screen (user override ?? farm default ?? Individual).
  const [eggUnitConversions, setEggUnitConversions] = useState<EggUnitConversion[]>([]);
  const { farm } = useFarm();
  const me = useMe();
  const stepperUnit = resolveStepperUnit(
    farm?.defaultStepperUnit, me?.preferredStepperUnit, eggUnitConversions);
  const stepSize = stepperUnit.eggsPerUnit;
  const [flockFilter, setFlockFilter] = useState("");
  // #512 (T038) — the filter's committed identity: always a genuine user
  // pick (onCommit already hands over the full entity) — there is no
  // external/deep-link source for this filter, so no unavailable state is
  // reachable here (Opus review).
  const [flockFilterEntity, setFlockFilterEntity] = useState<Flock | null>(null);
  const [filterPickerOpen, setFilterPickerOpen] = useState(false);
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  // #703 — the flight guard (#236), the per-place message slots (#479: the
  // setup read and the void write — a row button, not behind a dialog —
  // belong to the page; the adjust dialog's failures belong to that form) and
  // the dialog-session generation (#477 part 2) come from one shared hook.
  // The adjust dialog runs under its own fixed scope and `void:<id>` per row:
  // exactly one control spins while `busy` keeps every other mutating control
  // on the screen inert.
  const dialog = useDialogAction(DIALOG_SCOPES);
  const { busy, isPending, errors, run, dismissDialog } = dialog;
  const setPageError = errors.setPage;
  const [message, setMessage] = useState<string | null>(null);

  // #980 — the phone details dialog, held the same way `adjusting` is: one
  // entry at a time, closed before either correction opens, so the two are
  // never on screen together.
  const [details, setDetails] = useState<DailyEntry | null>(null);

  // Stable idempotency keys per logical mutation; see settleKey for the
  // rotation rules on this screen.
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
    // includeInactive/includeArchived: historical entries may reference
    // deactivated grades or archived flocks and their names must still resolve.
    Promise.all([
      listFlocks({ includeArchived: true }),
      listEggGrades({ includeInactive: true }),
      listEggUnitConversions(),
    ])
      .then(([f, g, units]) => { setFlocks(f); setGrades(g); setEggUnitConversions(units); })
      .catch(() => setPageError(i18n.t("history:loadFlocksGradesFailed")));
  }, [setPageError]);

  // #469 — six call sites (the filter effect, load-more, the adjust and void
  // refreshes, the 409 refresh, and the conflict rebind) shared one load with
  // no request sequencing at all: whichever response landed last won, a stale
  // rejection painted an error over a healthy table, and a load-more during a
  // filter change appended the new window's page onto the old rows.
  const entries = usePagedList({
    fetchPage: useCallback(
      (offset: number, limit: number) => listDailyEntries({
        flockId: flockFilter || undefined,
        from: from || undefined,
        to: to || undefined,
        limit,
        offset,
      }),
      [flockFilter, from, to],
    ),
    pageSize: PAGE,
    errorText: () => i18n.t("history:loadEntriesFailed"),
  });


  // #512 US4 (T051) — a row's own name, independent of the picker/catalog
  // entirely: the row-owned `flockName` the endpoint's scoped bulk read
  // already resolved, or the translated unavailable label. Never the capped
  // `flocks` list and never an id fragment (contracts/http-api.md: "Required
  // names are never replaced with identifier fragments").
  const rowFlockName = (e: { flockId: string; flockName?: string | null }) => {
    if (e.flockName !== undefined)
      return e.flockName ?? t("rowFlockUnavailable");
    return flocks.find((f) => f.id === e.flockId)?.name ?? t("rowFlockUnavailable");
  };
  const gradeName = (id: string) => grades.find((g) => g.id === id)?.name ?? id.slice(0, 8);
  const correctable = (e: DailyEntry) =>
    e.status === "Submitted" || e.status === "Locked" || e.status === "ManagerAdjusted";
  // #396 — how many of this day's cracked/dirty eggs became stock. Read from
  // the ENTRY's snapshot, never from the current grade catalog: a farm that
  // switched Cracked off after recording a day must still see that day as it
  // was recorded, and one that switched it on must not see past losses
  // retroactively turn into stock.
  //
  // A draft has resolved nothing yet, so it shows an em dash rather than 0 —
  // "not decided" and "decided to be a loss" are different facts, and 0 would
  // state the second while the first is true.
  const conditionStock = (e: DailyEntry) => {
    if (e.status === "Draft") return "—";
    return fmt.count((e.crackedGradeId ? e.crackedEggs : 0) + (e.dirtyGradeId ? e.dirtyEggs : 0));
  };
  const gradeList = (e: DailyEntry) =>
    e.grades.length === 0
      ? "—"
      : e.grades.map((g) => `${gradeName(g.eggGradeId)} ${fmt.count(g.quantity)}`).join(", ");

  const {
    adjusting, setAdjusting, closeAdjust, startAdjust, onAdjustSubmit, panelGrades,
    setLine, assignRest, lineQty, total, setTotal, cracked, setCracked, dirty, setDirty,
    discarded, setDiscarded, mortality, setMortality, reason, setReason, armed, canAssign,
    remaining, grading, gradesSum, gradesReconciled, lossesExceedTotal, sellable, setAssigning,
  } = useHistoryAdjust({ grades, entries, keyFor, settleKey, setMessage, setPageError, dialog, correctable });
  // The Daily entry screen can't target archived flocks (capture excludes
  // them), so an edit link for one would silently fall back to a different
  // flock — worse than no link (codex review of #86).
  // #512 US4 (T045/T051) — the ROW's own `flockStatus` is authoritative
  // (page-adoption.md: "editability uses row flockStatus"): the capped
  // `flocks` list can miss an out-of-window flock entirely (silently
  // denying an edit that should be offered) or, for a scope-lost flock,
  // can't say anything at all — the row already carries the CURRENT truth
  // from the same scoped bulk read as its name. `flockStatus === undefined`
  // (an older server that never sent the field) is the one case that falls
  // back to the catalog; an explicit `null` (out of scope) is decided,
  // fail-safe, without a guess.
  const flockEditable = (e: { flockId: string; flockStatus?: string | null }) => {
    if (e.flockStatus !== undefined) return e.flockStatus !== null && e.flockStatus !== "Archived";
    const f = flocks.find((x) => x.id === e.flockId);
    return f !== undefined && f.status !== "Archived";
  };

  // #980 — who may do what to a row, resolved once. The desktop Actions cell
  // and the phone details dialog render different layouts from this same
  // answer, so the two surfaces cannot drift apart on a role gate.
  const rowActions = (e: DailyEntry) => ({
    viewHistory: isAdmin,
    edit: e.status === "Draft" && flockEditable(e),
    correct: isAdmin && correctable(e),
  });

  // Key lifecycle differs from the create screens (codex review of PR #81):
  // a SERVER response — success or rejection — is a definite outcome, so the
  // key rotates immediately and an edited retry is a fresh request (the
  // version base already guards against double-apply). Only a transport
  // failure (no response) keeps the key for an exact replay.
  function settleKey(scope: string, err?: unknown) {
    if (err === undefined || err instanceof ApiError) clearKey(scope);
  }

  async function onVoid(e: DailyEntry) {
    // F13-style: the reason ask doubles as the confirmation. F135: it is the
    // app's own dialog, so the required check is inline and the typed text
    // survives it — window.prompt validated only after it had closed.
    const voidReason = await askReason({
      title: i18n.t("history:voidConfirmTitle", { date: fmt.date(e.date), flock: rowFlockName(e) }),
      body: i18n.t("history:voidConfirmBody"),
      confirmLabel: i18n.t("history:voidConfirmLabel"),
      destructive: true,
    });
    if (voidReason === null) return;
    // The reason dialog settled BEFORE this flight opens (useConfirm
    // contract), so the originating row's void button is the pending
    // indicator from here to settle.
    const scope = `void:${e.id}`;
    await run(scope, async () => {
      // Void is a row action, not behind the adjust dialog — its failure is
      // the PAGE's.
      setPageError(null);
      setMessage(null);
      try {
        await entries.runWrite(async () => {
          await voidDailyEntry(e.id, { version: e.version, reason: voidReason }, keyFor(scope));
          settleKey(scope);
          // A stale adjust panel for the now-voided entry would only 409.
          // Dismiss it (not a plain close): this panel wasn't dismissed by the
          // user, so its slot must not keep a stale error, or later be
          // written into after the user has moved on to a different entry —
          // and its session ends, so nothing of it can act on the dialog the
          // user opens next. A fact about the world reaching into another
          // scope's dialog, run ungated (#703, owner decision 2026-09-07).
          if (adjusting?.id === e.id) { setAdjusting(null); dismissDialog("adjust"); }
          setMessage(i18n.t("history:entryVoidedMessage"));
        });
      } catch (err) {
        settleKey(scope, err);
        if (err instanceof ApiError && err.status === 409) {
          // The void lost a race — show what actually stands now. Also close
          // a stale adjust panel for this entry: the 409 path used to leave
          // it bound to pre-conflict values while the success path closed it.
          if (adjusting?.id === e.id) { setAdjusting(null); dismissDialog("adjust"); }
          // No reload of our own — runWrite already re-read in its rejection
          // path — and no claim about how that read went: the message states
          // the conflict, and the list reports its own health through the
          // hook's error banner. Saying more needed the screen to know an
          // outcome it cannot reliably observe (#469).
          setPageError(i18n.t("history:voidConflictMessage"));
        } else {
          setPageError(errText(err));
        }
      }
    });
  }

  // The setup read (flocks + grades) failing with nothing to show is the one
  // fatal case: without those, every row renders unresolvable ids. `entries`
  // is the hook's handle, so the emptiness test is on its rows.
  const loadedEntries = entries.rows;

  if (errors.page && entries.rows === null)
    return <FieldConsole><Typography variant="h2">{t("loadingTitle")}</Typography><p className="error">{errors.page}</p></FieldConsole>;

  return (
    <FieldConsole>
      <Typography variant="h2">{t("title")}</Typography>
      {isAdmin && (
        <p className="muted">
          {t("intro")}
        </p>
      )}

      {loadedEntries !== null && (
        <ConsoleSummary label={t("contextLabel")} items={[
          { label: t("windowLabel"), value: from || to ? `${from ? fmt.date(from) : "…"} – ${to ? fmt.date(to) : "…"}` : t("allDates") },
          { label: t("loadedRecords"), value: entries.reloading ? "—" : fmt.count(loadedEntries.length) },
          ...([
            ["Submitted", "statusSubmitted"], ["Draft", "statusDraft"],
            ["Locked", "statusLocked"], ["ManagerAdjusted", "statusAdjusted"], ["Voided", "statusVoided"],
          ] as const).map(([status, label]) => ({
            label: t(label), value: entries.reloading ? "—" : fmt.count(loadedEntries.filter((entry) => entry.status === status).length),
          })),
        ]} />
      )}

      <HistoryFilters
        flockFilter={flockFilter} setFlockFilter={setFlockFilter}
        flockFilterEntity={flockFilterEntity} setFlockFilterEntity={setFlockFilterEntity}
        filterPickerOpen={filterPickerOpen} setFilterPickerOpen={setFilterPickerOpen}
        from={from} setFrom={setFrom} to={to} setTo={setTo} flocks={flocks}
      />

      <HistoryDetailsDialog details={details} setDetails={setDetails}
        rowActions={rowActions} rowFlockName={rowFlockName} conditionStock={conditionStock} gradeList={gradeList}
        busy={busy} startAdjust={startAdjust} onVoid={onVoid} />

      <HistoryAdjustDialog
        adjusting={adjusting} rowFlockName={rowFlockName} closeAdjust={closeAdjust}
        busy={busy} isPending={isPending} errors={errors} onAdjustSubmit={onAdjustSubmit}
        gradesReconciled={gradesReconciled} lossesExceedTotal={lossesExceedTotal}
        sellable={sellable} gradesSum={gradesSum} grading={grading}
        total={total} setTotal={setTotal} cracked={cracked} setCracked={setCracked}
        dirty={dirty} setDirty={setDirty} discarded={discarded} setDiscarded={setDiscarded}
        mortality={mortality} setMortality={setMortality} stepSize={stepSize} stepperUnit={stepperUnit}
        panelGrades={panelGrades} lineQty={lineQty} setLine={setLine} armed={armed}
        canAssign={canAssign} remaining={remaining} assignRest={assignRest}
        reason={reason} setReason={setReason} setAssigning={setAssigning}
      />

      {/* #479 — unconditional: the adjust dialog's own failures live in their
          own slot now (see DialogError above), so there is nothing here for a
          dialog message to double up on, whether the dialog is open or not. */}
      {errors.page && <p className="error" role="alert">{errors.page}</p>}
      {message && <p className="success" role="status">{message}</p>}

      {/* Same reasoning: the list's own health report is the page's, and
          muting it while the dialog was open used to hide a background
          failure the dialog had nothing to do with. */}
      {entries.error && (
        <p className="error" role="alert">{entries.error}</p>
      )}

      {/* Blanked while a replacement is in flight too: one window's rows must
          never sit under another window's filters (#469). */}
      {entries.rows === null || entries.reloading ? (
        <p className="muted">{tc("loading")}</p>
      ) : entries.rows.length === 0 ? (
        // No page-head create action on this screen (entries come from Daily
        // Entry); with a filter set, the fix is clearing it back to "All".
        (flockFilter || from || to)
          ? <EmptyState icon={FilterX} message={t("noEntriesMatch")} />
          : <EmptyState icon={Inbox} message={t("noEntriesMessage")} />
      ) : (
          <HistoryLedgers entries={entries} rowFlockName={rowFlockName} conditionStock={conditionStock} gradeList={gradeList} rowActions={rowActions} busy={busy}
            isPending={isPending} startAdjust={startAdjust} onVoid={onVoid} setDetails={setDetails} />
      )}

      {confirmDialog}
    </FieldConsole>
  );
}
