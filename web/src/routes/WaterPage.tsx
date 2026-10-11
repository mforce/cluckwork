import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { FilterX, Inbox, Plus } from "lucide-react";
import {
  Box, Button, Stack, Table, TableBody, TableCell, TableHead, TableRow, Typography, useMediaQuery,
} from "@mui/material";
import { listFlocks, listWaterUsage, recordWaterUsage, updateWaterUsage } from "../api/cluckwork";
import type { Flock, WaterUsage } from "../api/cluckwork";
import { useFormat } from "../farm/useFormat";
import { FarmDate } from "../components/FarmDate";
import { useAuth } from "../auth/useAuth";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { PhoneDetailsField, PhoneLedgerList, PhoneLedgerRow } from "../components/PhoneLedger";
import { EmptyState } from "../components/EmptyState";
import { FilterBar, FilterDateField, FILTER_PICKER_SX } from "../components/FilterBar";
import { readLastFlockId, rememberFlockId, resolveDefaultFlock } from "../lib/flockDefault";
import { FlockPicker } from "../components/FlockPicker";
import type { PickerSnapshot } from "../components/NamedEntityPicker";
import { usePagedList } from "../components/usePagedList";
import { usePendingAction } from "../components/usePendingAction";
import { useFarmToday } from "../farm/useFarm";
import { FieldConsole, ConsoleSubhead, CONSOLE_LINK_SX, LedgerTableContainer } from "../components/FieldConsole";
import { errText, WaterCaptureForm, useWaterCaptureFields } from "./WaterCaptureForm";
import { newId } from "../lib/ids";
import { MD_UP_QUERY } from "../lib/breakpoints";
import i18n from "../i18n";
import { waterSourceLabel, waterUnitLabel } from "../i18n/enums";

const PAGE = 50;
const NOWRAP = { whiteSpace: "nowrap" as const };

// F16 (#67): water consumed per flock per day — direct quantity or meter
// readings (quantity derives from the delta). Records are editable (no
// stock behind them); flock and date stay fixed once recorded.
export function WaterPage() {
  const { t } = useTranslation("water");
  const isDesktop = useMediaQuery(MD_UP_QUERY);
  const fmt = useFormat();
  const { t: tc } = useTranslation("common");
  // Farm-local, not browser-local: since #35 the API judges "is this date in
  // the future?" against the FARM's day, so the pickers must agree (#123).
  const today = useFarmToday();
  // Recording is open to everyone; correcting a record is admin-only (#73).
  const { isAdmin } = useAuth();
  const [flocks, setFlocks] = useState<Flock[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const { busy, run } = usePendingAction();
  const [formOpen, setFormOpen] = useState(false);
  const [filterOpen, setFilterOpen] = useState(false);
  const [details, setDetails] = useState<WaterUsage | null>(null);

  const fields = useWaterCaptureFields(today);
  const {
    editingId, setEditingId, editingVersion, setEditingVersion,
    captureFlock, setCaptureFlock, setCaptureFlockGen, setCaptureFlockRequestId,
    captureFlockSnapshot, date, setDate, source, setSource, unit, setUnit, useMeters, setUseMeters,
    quantity, setQuantity, meterStart, setMeterStart, meterEnd, setMeterEnd, note, setNote,
  } = fields;

  // list filters — initialized from the URL (?flockId=&from=&to=) so the
  // Daily Entry strip's "Water: N records" link lands on exactly the day it
  // was describing. window.location, not a router hook, on purpose — this
  // page has no other router dependency (DailyEntryPage's own deep-link
  // precedent).
  const [initialParams] = useState(() => new URLSearchParams(window.location.search));
  // #512 (T037) — the URL-named flock is a ROW-OWNED identity: the list is
  // filtered by its EXACT id, and its name is resolved through the picker's
  // exact GET (never substituted with the first discovery result). A scoped
  // 404/transport failure enters the explicit `unavailable` state.
  const [flockFilter, setFlockFilter] = useState(initialParams.get("flockId") ?? "");
  const [flockFilterEntity, setFlockFilterEntity] = useState<Flock | null>(null);
  const [flockFilterSnapshot, setFlockFilterSnapshot] = useState<PickerSnapshot<Flock>>({
    committed: null, selectionPhase: "uninitialized", exploring: false, canSubmit: true,
  });
  const [filterPickerOpen, setFilterPickerOpen] = useState(false);
  const [from, setFrom] = useState(initialParams.get("from") ?? "");
  const [to, setTo] = useState(initialParams.get("to") ?? "");

  // #512 (P2) — mount-default generation token. The mount-time listFlocks
  // effect commits the first Active/Depleted flock as the capture default.
  // If the user started editing a row (startEdit) or reset the form
  // (resetForm) BEFORE that promise resolved, the late default must NOT
  // overwrite the row-owned transition. A useRef token (not state) avoids
  // the closure-staleness problem: the effect's .then() reads the ref's
  // current value at resolution time, not the mount-time value.
  const defaultGenRef = useRef(0);
  // #646 — what the mount-time resolution settled on, so the synchronous
  // resetForm can restore the same flock without a round trip.
  const defaultFlockRef = useRef<Flock | null>(null);

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

  // #469 — this list had no request sequencing at all: two quick flock picks
  // let the older response win, a stale rejection painted an error over a
  // healthy view, and a failed reload left the previous filter's rows under
  // the new filter's controls. usePagedList owns all of that now.
  const usage = usePagedList({
    fetchPage: useCallback(
      (offset: number, limit: number) => listWaterUsage({
        flockId: flockFilter || undefined,
        from: from || undefined,
        to: to || undefined,
        limit,
        offset,
      }),
      [flockFilter, from, to],
    ),
    pageSize: PAGE,
    errorText: () => i18n.t("water:loadRecordsFailed"),
  });

  // #512 (T037) — display-only name while the row-owned capture id's exact
  // GET is in flight: the ROW's own flockName, never another flock's
  // metadata. Computed BEFORE the null-row early returns below so the
  // trigger's fallback reads the live window (TS 7 narrows `usage.rows`
  // to null past those returns, so the read goes through this local).
  const editingRow = editingId !== null
    ? usage.rows?.find((r) => r.id === editingId) ?? null
    : null;

  useEffect(() => {
    const gen = defaultGenRef.current;
    listFlocks({ includeArchived: true })
      .then(async (all) => {
        // Display names need archived too, so keep the full list for the
        // records table; the picker's discovery owns the capture options.
        setFlocks(all);
        // The capture default is committed as a full typed entity through the
        // picker's controlled sync — the engine admits an entity that is in
        // the discovery window as-is (no spurious exact GET). This effect's
        // closure is empty (mount-time), so it cannot see a later edit/reset:
        // if the user started correcting a row WHILE this load was pending,
        // startEdit has already committed the row-owned transition (possibly
        // the off-list requestedId path) and this late default must NOT
        // overwrite it — committing it would replace the row's flock with the
        // first Active one. The gen token (a ref, not state) is read at
        // resolution time: if startEdit or resetForm incremented it, this
        // default is stale and must stand down.
        if (defaultGenRef.current !== gen) return;
        // #646 — the default is resolved rather than scanned off this page:
        // `listFlocks` answers 100 rows ordered by NAME, so on a big farm the
        // old scan defaulted to whatever happened to be alphabetically early.
        // resolveDefaultFlock prefers the flock this user last recorded
        // against (shared with Daily entry), then the first ACTIVE one, and
        // only then a depleted one.
        const resolved = await resolveDefaultFlock(all);
        // The gen token is re-read AFTER the await, not just before it: the
        // resolution can now take a round trip, and startEdit or resetForm
        // may have committed a different flock inside that window. Checking
        // only on entry would let this late default overwrite the row the
        // user is correcting — the exact failure the pre-await check exists
        // to prevent, reintroduced by making the work asynchronous.
        if (defaultGenRef.current !== gen) return;
        defaultFlockRef.current = resolved;
        if (resolved) {
          setCaptureFlock(resolved);
          setCaptureFlockGen((g) => g + 1);
        }
      })
      .catch(() => setError(i18n.t("water:loadFlocksFailed")));
  }, [setCaptureFlock, setCaptureFlockGen]);


  // #512 (T037) — the filter's displayed name prefers the EXACT committed
  // entity (the row-owned identity, resolved by GET even when it is outside
  // the capped list); a not-yet-resolved id falls back to the full list's
  // name, and an unresolved one shows the explicit unavailable label.
  const flockName = (id: string) => {
    if (id === flockFilter && flockFilterSnapshot.selectionPhase === "unavailable")
      return t("filterFlockUnavailable");
    if (id === flockFilter && flockFilterEntity)
      return flockFilterEntity.name;
    return flocks.find((f) => f.id === id)?.name ?? id.slice(0, 8);
  };

  // Restores CAPTURE defaults completely — startEdit overwrote flock/date/
  // source/unit with the historical record's values (possibly an archived
  // flock absent from the picker), and leaving them would misdirect the next
  // capture (codex review of PR #76). #512 (T037): the flock reset is a FRESH
  // active/default controlled transition (bumped gen re-syncs the engine),
  // never a resurrection of the row-owned value.
  function resetForm() {
    defaultGenRef.current += 1;
    setEditingId(null);
    setEditingVersion(0);
    setQuantity("");
    setMeterStart("");
    setMeterEnd("");
    setNote("");
    setUseMeters(false);
    setDate(today);
    setSource("Well");
    setUnit("L");
    // Fresh capture: drop any row-owned id still in exact resolution so a
    // late GET for the row's flock can never commit over the new default.
    setCaptureFlockRequestId(null);
    // #646 — the SAME flock the mount-time resolution chose, held in a ref.
    // resetForm is synchronous by design (it runs inside the save/cancel
    // gesture), so it reuses that answer rather than starting a round trip
    // inside a user action; the page-scan fallback is what remains for the
    // window before the mount resolution lands.
    const remembered = readLastFlockId();
    const restored = (remembered ? flocks.find((f) => f.id === remembered) : null)
      ?? defaultFlockRef.current
      ?? flocks.find((f) => f.status === "Active")
      ?? flocks.find((f) => f.status === "Depleted");
    if (restored) {
      setCaptureFlock(restored);
      setCaptureFlockGen((g) => g + 1);
    }
  }

  function startEdit(r: WaterUsage) {
    defaultGenRef.current += 1;
    setEditingId(r.id);
    setEditingVersion(r.version);
    // The row-owned flock is committed EXACT — including an Archived row whose
    // id is absent from the active/depleted discovery window. If the row's
    // flock is in the loaded list, admit that exact full entity as-is (no
    // spurious GET); otherwise pass ONLY its id through the picker's
    // `requestedId` exact resolution and keep `captureFlock` null — never
    // fabricate an entity by splicing the row's name onto an unrelated flock
    // (the trigger falls back to the row's own flockName for display only).
    const rowFlock = flocks.find((f) => f.id === r.flockId);
    if (rowFlock) {
      setCaptureFlock(rowFlock);
      setCaptureFlockRequestId(null);
      setCaptureFlockGen((g) => g + 1);
    } else {
      setCaptureFlock(null);
      setCaptureFlockRequestId(r.flockId);
      setCaptureFlockGen((g) => g + 1);
    }
    setDate(r.date);
    setSource(r.source);
    setUnit(r.unit);
    const meters = r.meterStart !== null;
    setUseMeters(meters);
    setQuantity(meters ? "" : String(r.quantity));
    setMeterStart(meters ? String(r.meterStart) : "");
    setMeterEnd(meters ? String(r.meterEnd) : "");
    setNote(r.note ?? "");
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    // State check first so an Enter-key re-submit mid-flight cannot clear the
    // messages; the hook's ref closes the same-tick window the state misses.
    // #512 (T027) — the picker's canSubmit is the write guard, not the
    // button's disabled attribute: an exploring/unavailable picker must not
    // submit a stale committed flock even if the control is bypassed.
    if (busy || !captureFlockSnapshot.canSubmit || !captureFlock) return;
    setError(null);
    setMessage(null);
    const body = {
      source,
      unit,
      quantity: useMeters ? undefined : parseFloat(quantity),
      meterStart: useMeters ? parseFloat(meterStart) : undefined,
      meterEnd: useMeters ? parseFloat(meterEnd) : undefined,
      note: note.trim() || undefined,
    };
    // Validated before the flight opens: a rejected form never reads as busy.
    if (!useMeters && (!Number.isFinite(body.quantity!) || body.quantity! <= 0)) {
      setError(i18n.t("water:quantityMustBePositive"));
      return;
    }
    if (useMeters && (!Number.isFinite(body.meterStart!) || !Number.isFinite(body.meterEnd!))) {
      setError(i18n.t("water:bothMeterReadingsRequired"));
      return;
    }
    // The idempotency-key scope doubles as the pending scope — same string,
    // independent lifecycles (the key survives a transport failure; the
    // pending flight never does).
    const flockId = captureFlock.id;
    const scope = editingId ? `update:${editingId}` : `record:${flockId}:${date}`;
    await run(scope, async () => {
      try {
        // The list ticket is claimed BEFORE the write, so a filter change made
        // while it is in flight keeps the view and this refresh stands down
        // (#469).
        await usage.runWrite(async () => {
          if (editingId) {
            await updateWaterUsage(editingId, { ...body, version: editingVersion }, keyFor(scope));
            setMessage(i18n.t("water:recordCorrectedMessage"));
          } else {
            await recordWaterUsage({ ...body, flockId, date }, keyFor(scope));
            setMessage(i18n.t("water:recordedMessage"));
          }
        });
        clearKey(scope);
        // #646 — "most recently used" has to include THIS screen, or a worker
        // who only records water would keep getting the alphabetically first
        // active flock forever. Written after the save succeeds, so a refused
        // write never changes what the next capture opens on. Daily entry
        // reads the same key.
        //
        // #699 review — two things this has to get right. An ARCHIVED flock is
        // never a default (a correction of an archived row commits one as the
        // capture flock), so it is neither remembered nor cached. And the
        // just-saved entity is cached for resetForm: `rememberFlockId` stores
        // only an ID, and resetForm resolves an ID against the capped page —
        // so a flock the user reached through the picker from OUTSIDE that
        // page would be remembered and then immediately not restored.
        if (captureFlock && captureFlock.status !== "Archived") {
          rememberFlockId(flockId);
          defaultFlockRef.current = captureFlock;
        }
        resetForm();
        setFormOpen(false);
      } catch (err) {
        setError(errText(err));
      }
    });
  }

  if (error && usage.rows === null) return <FieldConsole><Typography variant="h2">{t("title")}</Typography><p className="error">{error}</p></FieldConsole>;
  if (usage.rows === null) return <FieldConsole><Typography variant="h2">{t("title")}</Typography><p className="muted">{tc("loading")}</p></FieldConsole>;

  const captureForm = (
    <WaterCaptureForm
      isDesktop={isDesktop} fields={fields} today={today}
      editingId={editingId} editingRow={editingRow} busy={busy}
      onSubmit={onSubmit} onReset={() => { resetForm(); setFormOpen(false); }}
      error={error} message={message}
    />
  );

  const filters = (
      <FilterBar>
        <Box sx={FILTER_PICKER_SX}>
          <FlockPicker
            label={t("filterFlockLabel")}
            eligibility="all"
            required={false}
            open={filterPickerOpen}
            requestedId={flockFilter || null}
            onSnapshot={(snap) => {
              setFlockFilterSnapshot(snap);
              // The engine's requestedId effect commits the resolved entity
              // (or enters the unavailable phase). Track the committed
              // entity so the trigger and the records table's flock column
              // can render the EXACT name (never a first-result
              // substitution).
              if (snap.committed) setFlockFilterEntity(snap.committed);
            }}
            onCommit={(f) => {
              setFlockFilter(f.id);
              setFlockFilterEntity(f);
              setFilterPickerOpen(false);
            }}
            onClear={() => {
              setFlockFilter("");
              setFlockFilterEntity(null);
            }}
            onEscape={() => setFilterPickerOpen(false)}
            onOutsideClick={() => setFilterPickerOpen(false)}
            trigger={
              <button type="button" className="named-picker-trigger"
                onClick={() => setFilterPickerOpen(true)}>
                {flockFilter === "" ? tc("all") : flockName(flockFilter)}
              </button>
            }
          />
        </Box>
        <FilterDateField label={t("fromLabel")} value={from} onChange={(e) => setFrom(e.target.value)} />
        <FilterDateField label={t("toLabel")} value={to} onChange={(e) => setTo(e.target.value)} />
        <Button variant="outlined" color="inherit" sx={{ borderRadius: "4px" }} onClick={() => { setFlockFilter(""); setFlockFilterEntity(null); setFilterPickerOpen(false); setFrom(""); setTo(""); }}>{tc("clearFiltersButton")}</Button>
      </FilterBar>
  );

  return (
    <FieldConsole>
      {isDesktop ? <Typography variant="h2">{t("title")}</Typography> :
        <Stack direction="row" sx={{ alignItems: "flex-start", justifyContent: "space-between", gap: 1 }}>
          <Typography variant="h2">{t("title")}</Typography>
          <Button variant="contained" startIcon={<Plus size={16} aria-hidden="true" />}
            sx={{ minHeight: 44, borderRadius: "4px", flexShrink: 0 }} onClick={() => setFormOpen(true)}>{t("logButton")}</Button>
        </Stack>}
      <p className="muted">
        {t("intro")}
      </p>

      {isDesktop ? captureForm : (
        <Dialog open={formOpen} title={editingId ? t("correctFormTitle") : t("logFormTitle")} onClose={() => { resetForm(); setFormOpen(false); }} closeDisabled={busy}
          actions={<Stack direction="row" sx={{ justifyContent: "flex-end", gap: 1 }}>
            <Button size="small" sx={CONSOLE_LINK_SX} disabled={busy} onClick={() => { resetForm(); setFormOpen(false); }}>{tc("cancel")}</Button>
            <BusyButton variant="contained" type="submit" form="water-entry-form" busy={busy}
              disabled={!captureFlock || !captureFlockSnapshot.canSubmit}>{editingId ? t("saveCorrectionButton") : t("recordWaterButton")}</BusyButton>
          </Stack>}>
          {captureForm}
        </Dialog>
      )}
      {!isDesktop && !formOpen && error && <p className="error">{error}</p>}
      {!isDesktop && message && <p className="success">{message}</p>}

      {!isDesktop && <>
        <Stack direction="row" sx={{ gap: 1, mb: 1.5, flexWrap: "wrap" }}>
          <Button variant="outlined" color="inherit" sx={{ borderRadius: "100px", minHeight: 36, fontSize: ".75rem" }} onClick={() => setFilterOpen(true)}>{flockFilter ? flockName(flockFilter) : t("allFlocksChip")}</Button>
          <Button variant="outlined" color="inherit" sx={{ borderRadius: "100px", minHeight: 36, fontSize: ".75rem" }} onClick={() => setFilterOpen(true)}>{from || to ? t("rangeChip", { from: from ? fmt.date(from) : "…", to: to ? fmt.date(to) : "…" }) : t("allDatesChip")}</Button>
        </Stack>
        <Dialog open={filterOpen} title={t("filtersTitle")} onClose={() => setFilterOpen(false)}>{filters}</Dialog>
        <Dialog open={details !== null} compactTitle
          title={details ? t("detailsDialogTitle", { date: fmt.date(details.date), flock: details.flockName ?? t("rowFlockUnavailable") }) : t("title")}
          onClose={() => setDetails(null)}
          actions={details && isAdmin && <Button size="small" sx={{ ...CONSOLE_LINK_SX, minHeight: 44 }} disabled={busy}
            onClick={() => { startEdit(details); setDetails(null); setFormOpen(true); }}>{t("correctButton")}</Button>}>
          {details && <Box component="dl" sx={{ m: 0 }}>
            <PhoneDetailsField label={t("amountHeader")}>{fmt.count(details.quantity)} {waterUnitLabel(details.unit)}</PhoneDetailsField>
            <PhoneDetailsField label={t("sourceHeader")}>{waterSourceLabel(details.source)}</PhoneDetailsField>
            <PhoneDetailsField label={t("metersHeader")}>{details.meterStart !== null ? `${fmt.count(details.meterStart)} → ${details.meterEnd === null ? "" : fmt.count(details.meterEnd)}` : "—"}</PhoneDetailsField>
            <PhoneDetailsField label={t("noteHeader")}>{details.note ?? "—"}</PhoneDetailsField>
          </Box>}
        </Dialog>
      </>}

      <ConsoleSubhead title={t("recordsHeading")} caption={t("recordsCaption")} />
      {isDesktop && filters}

      {usage.error && <p className="error">{usage.error}</p>}

      {/* One window's rows must never sit under another window's controls,
          not even for the length of the request (#469). */}
      {usage.reloading ? (
        <p className="muted">{tc("loading")}</p>
      ) : usage.rows.length === 0 ? (
        (flockFilter || from || to)
          ? <EmptyState icon={FilterX} message={t("noRecordsMatch")} />
          : <EmptyState icon={Inbox} message={t("noRecordsMessage")} />
      ) : (
        <>
          {isDesktop ? <LedgerTableContainer>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell>{t("dateHeader")}</TableCell>
                  <TableCell>{t("flockHeader")}</TableCell>
                  <TableCell align="right">{t("amountHeader")}</TableCell>
                  <TableCell>{t("sourceHeader")}</TableCell>
                  <TableCell align="right">{t("metersHeader")}</TableCell>
                  <TableCell>{t("noteHeader")}</TableCell>
                  <TableCell></TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {usage.rows.map((r) => (
                  <TableRow key={r.id}>
                    <TableCell sx={NOWRAP}><FarmDate iso={r.date} /></TableCell>
                    <TableCell>{r.flockName ?? t("rowFlockUnavailable")}</TableCell>
                    <TableCell align="right">{fmt.count(r.quantity)} {waterUnitLabel(r.unit)}</TableCell>
                    <TableCell>{waterSourceLabel(r.source)}</TableCell>
                    <TableCell align="right">{r.meterStart !== null ? `${fmt.count(r.meterStart)} → ${r.meterEnd === null ? "" : fmt.count(r.meterEnd)}` : "—"}</TableCell>
                    <TableCell>{r.note ?? ""}</TableCell>
                    <TableCell sx={NOWRAP}>
                      {isAdmin && (
                        <Button sx={CONSOLE_LINK_SX} size="small" disabled={busy} onClick={() => startEdit(r)}>{t("correctButton")}</Button>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </LedgerTableContainer> : <PhoneLedgerList label={t("recordsHeading")}>
            {usage.rows.map((r) => <li key={r.id}>
              <PhoneLedgerRow onClick={() => setDetails(r)} date={<FarmDate iso={r.date} />}
                primary={r.flockName ?? t("rowFlockUnavailable")}
                trailing={<strong>{fmt.count(r.quantity)} {waterUnitLabel(r.unit)}</strong>}
                summary={<><strong>{waterSourceLabel(r.source)}</strong>{r.meterStart !== null && ` · ${fmt.count(r.meterStart)} → ${r.meterEnd === null ? "" : fmt.count(r.meterEnd)}`}{r.note && ` · ${r.note}`}</>} />
            </li>)}
          </PhoneLedgerList>}
          {usage.canLoadMore && (
            <button className="link" disabled={busy}
              onClick={() => void usage.loadMore()}>
              {t("loadMoreButton")}
            </button>
          )}
        </>
      )}
    </FieldConsole>
  );
}
