import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { listFlocks, listWaterUsage, recordWaterUsage, updateWaterUsage } from "../api/cluckwork";
import type { Flock, WaterUsage } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useFormat } from "../farm/useFormat";
import { FarmDate } from "../components/FarmDate";
import { useAuth } from "../auth/useAuth";
import { BusyButton } from "../components/BusyButton";
import { FlockPicker } from "../components/FlockPicker";
import type { PickerSnapshot } from "../components/NamedEntityPicker";
import { usePagedList } from "../components/usePagedList";
import { usePendingAction } from "../components/usePendingAction";
import { useFarmToday } from "../farm/useFarm";
import { newId } from "../lib/ids";
import i18n from "../i18n";
import { waterSourceLabel, waterUnitLabel } from "../i18n/enums";

const PAGE = 50;
const SOURCES = ["Well", "Municipal", "Tank", "Other"];
const UNITS = ["L", "gal"];

function errText(err: unknown): string {
  // Concurrent-edit conflicts get a human message instead of raw problem text.
  if (err instanceof ApiError && err.status === 409)
    return i18n.t("water:concurrentEditError");
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// F16 (#67): water consumed per flock per day — direct quantity or meter
// readings (quantity derives from the delta). Records are editable (no
// stock behind them); flock and date stay fixed once recorded.
export function WaterPage() {
  const { t } = useTranslation("water");
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

  // capture form; editingId switches it to update mode. editingVersion is the
  // base Version the row was loaded with — sent back so a concurrent edit
  // surfaces as a 409 instead of being silently overwritten.
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editingVersion, setEditingVersion] = useState(0);
  // #512 (T027/T037) — the capture flock is committed through FlockPicker.
  // `captureFlock` is the page-controlled committed entity; bumping
  // `captureFlockGen` re-syncs the engine's committed state after an external
  // reset (the mount-time default, a post-edit reset, or a startEdit
  // row-owned value — possibly an archived flock absent from discovery).
  // `captureFlockSnapshot.canSubmit` gates BOTH the submit button and
  // onSubmit itself.
  const [captureFlock, setCaptureFlock] = useState<Flock | null>(null);
  const [captureFlockGen, setCaptureFlockGen] = useState(0);
  // #512 (T037) — a row-owned capture flock id whose FULL entity is not in the
  // loaded list (typically an Archived flock the active/depleted discovery
  // never carries). While set, `captureFlock` stays null — nothing fabricated
  // can render or submit — and the id flows through the picker's `requestedId`
  // exact GET, which commits the true entity (status/breed/etc. included) or
  // enters `unavailable` (canSubmit false → no save).
  const [captureFlockRequestId, setCaptureFlockRequestId] = useState<string | null>(null);
  const [captureFlockSnapshot, setCaptureFlockSnapshot] = useState<PickerSnapshot<Flock>>({
    committed: null, selectionPhase: "uninitialized", exploring: false, canSubmit: false,
  });
  const [capturePickerOpen, setCapturePickerOpen] = useState(false);
  const [date, setDate] = useState(today);
  const [source, setSource] = useState("Well");
  const [unit, setUnit] = useState("L");
  const [useMeters, setUseMeters] = useState(false);
  const [quantity, setQuantity] = useState("");
  const [meterStart, setMeterStart] = useState("");
  const [meterEnd, setMeterEnd] = useState("");
  const [note, setNote] = useState("");

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
      .then((all) => {
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
        const firstActive = all.find((f) => f.status === "Active")
          ?? all.find((f) => f.status === "Depleted");
        if (firstActive) {
          setCaptureFlock(firstActive);
          setCaptureFlockGen((g) => g + 1);
        }
      })
      .catch(() => setError(i18n.t("water:loadFlocksFailed")));
  }, []);


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
    const firstActive = flocks.find((f) => f.status === "Active")
      ?? flocks.find((f) => f.status === "Depleted");
    if (firstActive) {
      setCaptureFlock(firstActive);
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
        resetForm();
      } catch (err) {
        setError(errText(err));
      }
    });
  }

  if (error && usage.rows === null) return <section><h2>{t("title")}</h2><p className="error">{error}</p></section>;
  if (usage.rows === null) return <section><h2>{t("title")}</h2><p className="muted">{tc("loading")}</p></section>;

  return (
    <section>
      <h2>{t("title")}</h2>
      <p className="muted">
        {t("intro")}
      </p>

      <form className="form-grid" onSubmit={onSubmit}>
        <FlockPicker
          label={t("flockLabel")}
          eligibility="active-and-depleted"
          required
          disabled={editingId !== null}
          open={capturePickerOpen}
          controlledCommitted={captureFlock}
          controlledGeneration={captureFlockGen}
          requestedId={captureFlockRequestId}
          onSnapshot={(snap) => {
            setCaptureFlockSnapshot(snap);
            // #512 (P2) — only adopt the engine's committed entity when it
            // resolves the page's own requestedId exact GET (the row-owned
            // id the loaded list never carried). Every other snapshot —
            // including the engine's internal re-emission after a controlled
            // sync — carries the engine's PREVIOUS committed entity, which
            // can be STALE relative to a concurrent page-side commit
            // (startEdit / resetForm). Blindly adopting it overwrites the
            // page's fresh row-owned entity with the old default.
            if (
              snap.committed &&
              captureFlockRequestId &&
              snap.committed.id === captureFlockRequestId
            ) {
              setCaptureFlock(snap.committed);
              setCaptureFlockRequestId(null);
            }
          }}
          onCommit={(f) => {
            setCaptureFlock(f);
            setCaptureFlockRequestId(null);
            setCaptureFlockGen((g) => g + 1);
            setCapturePickerOpen(false);
          }}
          onEscape={() => setCapturePickerOpen(false)}
          onOutsideClick={() => setCapturePickerOpen(false)}
          trigger={
            <button
              type="button"
              className="named-picker-trigger"
              disabled={editingId !== null}
              onClick={() => setCapturePickerOpen(true)}
            >
              {captureFlock
                ? `${captureFlock.name}${captureFlock.status === "Depleted" ? t("depletedFlockSuffix") : ""}`
                // #512 (T037) — while the row-owned id's exact GET is in
                // flight (or the list doesn't carry it), the trigger shows the
                // ROW's own flockName for display only — never another flock's
                // metadata. Once the exact read commits, `captureFlock` wins.
                : (editingRow && captureFlockRequestId ? editingRow.flockName : null)
                  ?? t("selectFlockOption")}
            </button>
          }
        />
        <label>{t("dateLabel")}
          <input type="date" value={date} max={today} required disabled={editingId !== null}
            onChange={(e) => setDate(e.target.value)} />
        </label>
        <label>{t("sourceLabel")}
          <select value={source} onChange={(e) => setSource(e.target.value)}>
            {SOURCES.map((s) => <option key={s} value={s}>{waterSourceLabel(s)}</option>)}
          </select>
        </label>
        <label>{t("unitLabel")}
          <select value={unit} onChange={(e) => setUnit(e.target.value)}>
            {UNITS.map((u) => <option key={u} value={u}>{waterUnitLabel(u)}</option>)}
          </select>
        </label>
        <label className="muted check">
          <input type="checkbox" checked={useMeters}
            onChange={(e) => setUseMeters(e.target.checked)} />
          {t("fromMeterReadingsLabel")}
        </label>
        {useMeters ? (
          <>
            <label>{t("meterStartLabel")}
              <input type="number" min={0} step={0.001} value={meterStart} required
                onChange={(e) => setMeterStart(e.target.value)} />
            </label>
            <label>{t("meterEndLabel")}
              <input type="number" min={0} step={0.001} value={meterEnd} required
                onChange={(e) => setMeterEnd(e.target.value)} />
            </label>
          </>
        ) : (
          <label>{t("quantityLabelWithUnit", { unit: waterUnitLabel(unit) })}
            <input type="number" min={0.001} step={0.001} value={quantity} required
              onChange={(e) => setQuantity(e.target.value)} />
          </label>
        )}
        <label>{t("noteLabel")}
          <input value={note} maxLength={500} onChange={(e) => setNote(e.target.value)} />
        </label>
        <BusyButton type="submit" busy={busy}
          disabled={!captureFlock || !captureFlockSnapshot.canSubmit}>
          {editingId ? t("saveCorrectionButton") : t("recordWaterButton")}
        </BusyButton>
        {editingId && (
          <button type="button" className="link" onClick={resetForm}>{t("cancelEditButton")}</button>
        )}
      </form>

      {error && <p className="error">{error}</p>}
      {message && <p className="success">{message}</p>}

      <h3>{t("recordsHeading")}</h3>
      <div className="form-grid">
        <div className="filter-flock">
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
        </div>
        <label>{t("fromLabel")}
          <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label>{t("toLabel")}
          <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
      </div>

      {usage.error && <p className="error">{usage.error}</p>}

      {/* One window's rows must never sit under another window's controls,
          not even for the length of the request (#469). */}
      {usage.reloading ? (
        <p className="muted">{tc("loading")}</p>
      ) : usage.rows.length === 0 ? (
        <p className="muted">{t("noRecordsMatch")}</p>
      ) : (
        <>
          <table className="data">
            <thead>
              <tr><th>{t("dateHeader")}</th><th>{t("flockHeader")}</th><th className="num">{t("amountHeader")}</th><th>{t("sourceHeader")}</th><th className="num">{t("metersHeader")}</th><th>{t("noteHeader")}</th><th></th></tr>
            </thead>
            <tbody>
              {usage.rows.map((r) => (
                <tr key={r.id}>
                  <td className="nowrap"><FarmDate iso={r.date} /></td>
                  <td>{r.flockName ?? t("rowFlockUnavailable")}</td>
                  <td className="num">{fmt.count(r.quantity)} {waterUnitLabel(r.unit)}</td>
                  <td>{waterSourceLabel(r.source)}</td>
                  <td className="num">{r.meterStart !== null ? `${fmt.count(r.meterStart)} → ${r.meterEnd === null ? "" : fmt.count(r.meterEnd)}` : "—"}</td>
                  <td>{r.note ?? ""}</td>
                  <td>
                    {isAdmin && (
                      <button className="link" disabled={busy} onClick={() => startEdit(r)}>{t("correctButton")}</button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {usage.canLoadMore && (
            <button className="link" disabled={busy}
              onClick={() => void usage.loadMore()}>
              {t("loadMoreButton")}
            </button>
          )}
        </>
      )}
    </section>
  );
}
