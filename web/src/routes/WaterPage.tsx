import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { listFlocks, listWaterUsage, recordWaterUsage, updateWaterUsage } from "../api/cluckwork";
import type { Flock, WaterUsage } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useAuth } from "../auth/useAuth";
import { BusyButton } from "../components/BusyButton";
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
  const [flockId, setFlockId] = useState("");
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
  const [flockFilter, setFlockFilter] = useState(initialParams.get("flockId") ?? "");
  const [from, setFrom] = useState(initialParams.get("from") ?? "");
  const [to, setTo] = useState(initialParams.get("to") ?? "");

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

  useEffect(() => {
    listFlocks({ includeArchived: true })
      .then((all) => {
        // Capture: active + depleted (backfill). Display names need archived too,
        // so keep the full list and filter in the picker render.
        setFlocks(all);
        const firstActive = all.find((f) => f.status === "Active")
          ?? all.find((f) => f.status === "Depleted");
        if (firstActive) setFlockId(firstActive.id);
      })
      .catch(() => setError(i18n.t("water:loadFlocksFailed")));
  }, []);


  const flockName = (id: string) => flocks.find((f) => f.id === id)?.name ?? id.slice(0, 8);
  const pickableFlocks = flocks.filter((f) => f.status !== "Archived");

  // Restores CAPTURE defaults completely — startEdit overwrote flock/date/
  // source/unit with the historical record's values (possibly an archived
  // flock absent from the picker), and leaving them would misdirect the next
  // capture (codex review of PR #76).
  function resetForm() {
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
    const firstActive = flocks.find((f) => f.status === "Active")
      ?? flocks.find((f) => f.status === "Depleted");
    if (firstActive) setFlockId(firstActive.id);
  }

  function startEdit(r: WaterUsage) {
    setEditingId(r.id);
    setEditingVersion(r.version);
    setFlockId(r.flockId);
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
    if (busy) return;
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
        <label>{t("flockLabel")}
          <select value={flockId} disabled={editingId !== null}
            onChange={(e) => setFlockId(e.target.value)}>
            {pickableFlocks.map((f) => (
              <option key={f.id} value={f.id}>
                {f.name}{f.status === "Depleted" ? t("depletedFlockSuffix") : ""}
              </option>
            ))}
          </select>
        </label>
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
        <BusyButton type="submit" busy={busy} disabled={!flockId}>
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
        <label>{t("flockLabel")}
          <select value={flockFilter} onChange={(e) => setFlockFilter(e.target.value)}>
            <option value="">{tc("all")}</option>
            {flocks.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}
          </select>
        </label>
        <label>{t("fromLabel")}
          <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label>{t("toLabel")}
          <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
      </div>

      {usage.error && <p className="error">{usage.error}</p>}

      {usage.rows.length === 0 ? (
        <p className="muted">{t("noRecordsMatch")}</p>
      ) : (
        <>
          <table className="data">
            <thead>
              <tr><th>{t("dateHeader")}</th><th>{t("flockHeader")}</th><th>{t("amountHeader")}</th><th>{t("sourceHeader")}</th><th>{t("metersHeader")}</th><th>{t("noteHeader")}</th><th></th></tr>
            </thead>
            <tbody>
              {usage.rows.map((r) => (
                <tr key={r.id}>
                  <td>{r.date}</td>
                  <td>{flockName(r.flockId)}</td>
                  <td>{r.quantity} {waterUnitLabel(r.unit)}</td>
                  <td>{waterSourceLabel(r.source)}</td>
                  <td>{r.meterStart !== null ? `${r.meterStart} → ${r.meterEnd}` : "—"}</td>
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
