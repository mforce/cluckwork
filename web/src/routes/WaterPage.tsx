import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { listFlocks, listWaterUsage, recordWaterUsage, updateWaterUsage } from "../api/cluckwork";
import type { Flock, WaterUsage } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { todayIso } from "../lib/dates";

const PAGE = 50;
const SOURCES = ["Well", "Municipal", "Tank", "Other"];
const UNITS = ["L", "gal"];

function errText(err: unknown): string {
  // Concurrent-edit conflicts get a human message instead of raw problem text.
  if (err instanceof ApiError && err.status === 409)
    return "This record was just changed elsewhere — reload the list and retry.";
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// F16 (#67): water consumed per flock per day — direct quantity or meter
// readings (quantity derives from the delta). Records are editable (no
// stock behind them); flock and date stay fixed once recorded.
export function WaterPage() {
  const [rows, setRows] = useState<WaterUsage[] | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [flocks, setFlocks] = useState<Flock[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // capture form; editingId switches it to update mode. editingVersion is the
  // base Version the row was loaded with — sent back so a concurrent edit
  // surfaces as a 409 instead of being silently overwritten.
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editingVersion, setEditingVersion] = useState(0);
  const [flockId, setFlockId] = useState("");
  const [date, setDate] = useState(todayIso());
  const [source, setSource] = useState("Well");
  const [unit, setUnit] = useState("L");
  const [useMeters, setUseMeters] = useState(false);
  const [quantity, setQuantity] = useState("");
  const [meterStart, setMeterStart] = useState("");
  const [meterEnd, setMeterEnd] = useState("");
  const [note, setNote] = useState("");

  // list filters
  const [flockFilter, setFlockFilter] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");

  // Stable idempotency keys per logical mutation, rotated only after the full
  // action (write + refresh) succeeds — same contract as the other screens.
  const keys = useRef(new Map<string, string>());
  const keyFor = (scope: string) => {
    const existing = keys.current.get(scope);
    if (existing) return existing;
    const fresh = crypto.randomUUID();
    keys.current.set(scope, fresh);
    return fresh;
  };
  const clearKey = (scope: string) => keys.current.delete(scope);

  const load = useCallback(async (offset = 0) => {
    const page = await listWaterUsage({
      flockId: flockFilter || undefined,
      from: from || undefined,
      to: to || undefined,
      limit: PAGE,
      offset,
    });
    setHasMore(page.length === PAGE);
    setRows((prev) => (offset === 0 ? page : [...(prev ?? []), ...page]));
  }, [flockFilter, from, to]);

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
      .catch(() => setError("Could not load flocks. Is the API up?"));
  }, []);

  useEffect(() => {
    load().catch(() => setError("Could not load water records."));
  }, [load]);

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
    setDate(todayIso());
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
    if (busy) return;
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      const body = {
        source,
        unit,
        quantity: useMeters ? undefined : parseFloat(quantity),
        meterStart: useMeters ? parseFloat(meterStart) : undefined,
        meterEnd: useMeters ? parseFloat(meterEnd) : undefined,
        note: note.trim() || undefined,
      };
      if (!useMeters && (!Number.isFinite(body.quantity!) || body.quantity! <= 0)) {
        setError("Quantity must be a positive number.");
        return;
      }
      if (useMeters && (!Number.isFinite(body.meterStart!) || !Number.isFinite(body.meterEnd!))) {
        setError("Both meter readings are required.");
        return;
      }
      const scope = editingId ? `update:${editingId}` : `record:${flockId}:${date}`;
      if (editingId) {
        await updateWaterUsage(editingId, { ...body, version: editingVersion }, keyFor(scope));
        setMessage("Water record corrected.");
      } else {
        await recordWaterUsage({ ...body, flockId, date }, keyFor(scope));
        setMessage("Water recorded.");
      }
      await load();
      clearKey(scope);
      resetForm();
    } catch (err) {
      setError(errText(err));
    } finally {
      setBusy(false);
    }
  }

  if (error && rows === null) return <section><h2>Water</h2><p className="error">{error}</p></section>;
  if (rows === null) return <section><h2>Water</h2><p className="muted">Loading…</p></section>;

  return (
    <section>
      <h2>Water</h2>
      <p className="muted">
        Record what each flock drank — a direct amount, or meter readings (the
        amount is the meter delta). Records can be corrected later; flock and
        date are fixed.
      </p>

      <form className="form-grid" onSubmit={onSubmit}>
        <label>Flock
          <select value={flockId} disabled={editingId !== null}
            onChange={(e) => setFlockId(e.target.value)}>
            {pickableFlocks.map((f) => (
              <option key={f.id} value={f.id}>
                {f.name}{f.status === "Depleted" ? " — depleted, backfill only" : ""}
              </option>
            ))}
          </select>
        </label>
        <label>Date
          <input type="date" value={date} max={todayIso()} required disabled={editingId !== null}
            onChange={(e) => setDate(e.target.value)} />
        </label>
        <label>Source
          <select value={source} onChange={(e) => setSource(e.target.value)}>
            {SOURCES.map((s) => <option key={s} value={s}>{s}</option>)}
          </select>
        </label>
        <label>Unit
          <select value={unit} onChange={(e) => setUnit(e.target.value)}>
            {UNITS.map((u) => <option key={u} value={u}>{u}</option>)}
          </select>
        </label>
        <label className="muted check">
          <input type="checkbox" checked={useMeters}
            onChange={(e) => setUseMeters(e.target.checked)} />
          from meter readings
        </label>
        {useMeters ? (
          <>
            <label>Meter start
              <input type="number" min={0} step={0.001} value={meterStart} required
                onChange={(e) => setMeterStart(e.target.value)} />
            </label>
            <label>Meter end
              <input type="number" min={0} step={0.001} value={meterEnd} required
                onChange={(e) => setMeterEnd(e.target.value)} />
            </label>
          </>
        ) : (
          <label>Quantity ({unit})
            <input type="number" min={0.001} step={0.001} value={quantity} required
              onChange={(e) => setQuantity(e.target.value)} />
          </label>
        )}
        <label>Note
          <input value={note} maxLength={500} onChange={(e) => setNote(e.target.value)} />
        </label>
        <button type="submit" disabled={busy || !flockId}>
          {editingId ? "Save correction" : "Record water"}
        </button>
        {editingId && (
          <button type="button" className="link" onClick={resetForm}>cancel edit</button>
        )}
      </form>

      {error && <p className="error">{error}</p>}
      {message && <p className="success">{message}</p>}

      <h3>Records</h3>
      <div className="form-grid">
        <label>Flock
          <select value={flockFilter} onChange={(e) => setFlockFilter(e.target.value)}>
            <option value="">All</option>
            {flocks.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}
          </select>
        </label>
        <label>From
          <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label>To
          <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
      </div>

      {rows.length === 0 ? (
        <p className="muted">No water records match.</p>
      ) : (
        <>
          <table className="data">
            <thead>
              <tr><th>Date</th><th>Flock</th><th>Amount</th><th>Source</th><th>Meters</th><th>Note</th><th></th></tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.id}>
                  <td>{r.date}</td>
                  <td>{flockName(r.flockId)}</td>
                  <td>{r.quantity} {r.unit}</td>
                  <td>{r.source}</td>
                  <td>{r.meterStart !== null ? `${r.meterStart} → ${r.meterEnd}` : "—"}</td>
                  <td>{r.note ?? ""}</td>
                  <td>
                    <button className="link" disabled={busy} onClick={() => startEdit(r)}>correct</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {hasMore && (
            <button className="link" disabled={busy}
              onClick={() => void load(rows.length).catch(() => setError("Could not load more."))}>
              load more
            </button>
          )}
        </>
      )}
    </section>
  );
}
