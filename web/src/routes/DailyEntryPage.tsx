import { useEffect, useMemo, useRef, useState } from "react";
import type { FormEvent } from "react";
import {
  createFlock, listDailyEntries, listEggGrades, listFlocks,
  recordDailyEntry, submitDailyEntry,
} from "../api/cluckwork";
import type { EggGrade, Flock } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { todayIso } from "../lib/dates";

const LAST_FLOCK_KEY = "cluckwork.lastFlockId";

// Capture targets live flocks only — depleted/archived can't lay (#47); the
// server enforces the same rule. Every flock refresh on this page (initial
// load AND inline create) must go through this filter.
const activeOnly = (flocks: Flock[]) => flocks.filter((x) => x.status === "Active");

function errorMessage(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// F1 (#21): record the day's production by grade, then submit — submitting
// turns grade lines into egg lots (stock).
export function DailyEntryPage() {
  const [loading, setLoading] = useState(true);
  const [flocks, setFlocks] = useState<Flock[]>([]);
  const [grades, setGrades] = useState<EggGrade[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);

  const [flockId, setFlockId] = useState("");
  const [date, setDate] = useState(todayIso());
  const [totalEggs, setTotalEggs] = useState(0);
  const [cracked, setCracked] = useState(0);
  const [dirty, setDirty] = useState(0);
  const [discarded, setDiscarded] = useState(0);
  const [mortality, setMortality] = useState(0);
  const [gradeQty, setGradeQty] = useState<Record<string, number>>({});
  // Grades are only sent when the user (or prefill) touched them: the server
  // treats [] as "explicitly clear all lines" and omitted as "leave unchanged",
  // so an untouched re-save must not wipe an existing entry's grading.
  const [gradesTouched, setGradesTouched] = useState(false);
  const [existingStatus, setExistingStatus] = useState<string | null>(null);

  const [busy, setBusy] = useState(false);
  const inFlight = useRef(false);
  // Stable idempotency keys per logical mutation: regenerated only after a
  // definitive success, so a retry after an ambiguous network failure dedupes
  // server-side instead of repeating the write.
  const saveKey = useRef<string>(crypto.randomUUID());
  const flockKey = useRef<string>(crypto.randomUUID());
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  // inline flock creation
  const [showNewFlock, setShowNewFlock] = useState(false);
  const [newFlockName, setNewFlockName] = useState("");
  const [newFlockBreed, setNewFlockBreed] = useState("");
  const [newFlockPlaced, setNewFlockPlaced] = useState(todayIso());
  const [newFlockCount, setNewFlockCount] = useState(100);

  useEffect(() => {
    Promise.all([listFlocks(), listEggGrades()])
      .then(([all, g]) => {
        const f = activeOnly(all);
        setFlocks(f);
        setGrades(g.filter((x) => x.isSaleable));
        const remembered = localStorage.getItem(LAST_FLOCK_KEY);
        if (remembered && f.some((x) => x.id === remembered)) setFlockId(remembered);
        else if (f.length > 0) setFlockId(f[0].id);
      })
      .catch(() => setLoadError("Could not load flocks/grades. Is the API up?"))
      .finally(() => setLoading(false));
  }, []);

  // Edit-awareness: when flock+date match an existing entry, prefill the form
  // so a re-save updates what's really there instead of clobbering it.
  useEffect(() => {
    if (!flockId || !date) return;
    let cancelled = false;
    listDailyEntries({ flockId, from: date, to: date, limit: 1 })
      .then((entries) => {
        if (cancelled) return;
        const existing = entries.find((e) => e.date === date);
        if (existing) {
          setTotalEggs(existing.totalEggs);
          setCracked(existing.crackedEggs);
          setDirty(existing.dirtyEggs);
          setDiscarded(existing.discardedEggs);
          setMortality(existing.mortalityCount);
          setGradeQty(Object.fromEntries(existing.grades.map((g) => [g.eggGradeId, g.quantity])));
          setGradesTouched(existing.grades.length > 0);
          setExistingStatus(existing.status);
        } else {
          setTotalEggs(0); setCracked(0); setDirty(0); setDiscarded(0); setMortality(0);
          setGradeQty({});
          setGradesTouched(false);
          setExistingStatus(null);
        }
      })
      .catch(() => { /* prefill is best-effort; save still validates server-side */ });
    return () => { cancelled = true; };
  }, [flockId, date]);

  useEffect(() => {
    if (flockId) localStorage.setItem(LAST_FLOCK_KEY, flockId);
  }, [flockId]);

  const gradesSum = useMemo(
    () => Object.values(gradeQty).reduce((a, b) => a + (b || 0), 0),
    [gradeQty],
  );
  const losses = cracked + dirty + discarded;
  const sellable = totalEggs - losses;
  const lossesExceedTotal = losses > totalEggs;
  const selectedFlock = flocks.find((f) => f.id === flockId);
  const entryLocked = existingStatus !== null && existingStatus !== "Draft";

  const clamp0 = (v: number) => Math.max(0, v || 0);

  async function onCreateFlock(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      const created = await createFlock({
        name: newFlockName,
        breed: newFlockBreed,
        placementDate: newFlockPlaced,
        initialCount: newFlockCount,
      }, flockKey.current);
      flockKey.current = crypto.randomUUID();
      const refreshed = activeOnly(await listFlocks());
      setFlocks(refreshed);
      setFlockId(created.id);
      setShowNewFlock(false);
      setNewFlockName("");
      setNewFlockBreed("");
      setNewFlockPlaced(todayIso());
      setNewFlockCount(100);
    } catch (err) {
      setError(errorMessage(err));
    }
  }

  async function onSave(submit: boolean) {
    if (inFlight.current || !selectedFlock) return;   // sync re-entry guard
    inFlight.current = true;
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      const lines = grades
        .filter((g) => (gradeQty[g.id] ?? 0) > 0)
        .map((g) => ({ eggGradeId: g.id, quantity: gradeQty[g.id] }));
      const created = await recordDailyEntry({
        farmId: selectedFlock.farmId,
        houseId: selectedFlock.houseId,
        flockId: selectedFlock.id,
        date,
        totalEggs,
        crackedEggs: cracked,
        dirtyEggs: dirty,
        discardedEggs: discarded,
        mortalityCount: mortality,
        grades: gradesTouched ? lines : undefined,
      }, saveKey.current);
      if (submit) {
        const result = await submitDailyEntry(created.id, saveKey.current);
        setExistingStatus(result.status);
        setMessage(`Submitted — ${result.eggLotIds.length} egg lot(s) created.`);
      } else {
        setMessage("Draft saved.");
      }
      saveKey.current = crypto.randomUUID();
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      inFlight.current = false;
      setBusy(false);
    }
  }

  if (loading) return <section><h2>Daily entry</h2><p className="muted">Loading…</p></section>;
  if (loadError) return <section><h2>Daily entry</h2><p className="error">{loadError}</p></section>;

  return (
    <section>
      <h2>Daily entry</h2>

      <div className="form-grid">
        <label>
          Flock
          <select value={flockId} onChange={(e) => setFlockId(e.target.value)}>
            {flocks.length === 0 && <option value="">— no flocks yet —</option>}
            {flocks.map((f) => (
              <option key={f.id} value={f.id}>{f.name} ({f.breed})</option>
            ))}
          </select>
        </label>
        <button className="link" type="button" onClick={() => setShowNewFlock((v) => !v)}>
          {showNewFlock ? "cancel" : "+ new flock"}
        </button>
      </div>

      {showNewFlock && (
        <form className="inline-form" onSubmit={onCreateFlock}>
          <input placeholder="Name" value={newFlockName} required
            onChange={(e) => setNewFlockName(e.target.value)} />
          <input placeholder="Breed" value={newFlockBreed} required
            onChange={(e) => setNewFlockBreed(e.target.value)} />
          <label className="muted">Placed
            <input type="date" value={newFlockPlaced} max={todayIso()} required
              onChange={(e) => setNewFlockPlaced(e.target.value)} />
          </label>
          <input type="number" min={1} value={newFlockCount} required
            onChange={(e) => setNewFlockCount(Math.max(1, e.target.valueAsNumber || 1))} />
          <button type="submit">Create flock</button>
        </form>
      )}

      <div className="form-grid">
        <label>Date
          <input type="date" value={date} max={todayIso()}
            onChange={(e) => setDate(e.target.value)} />
        </label>
        <label>Total eggs
          <input type="number" min={0} value={totalEggs} disabled={entryLocked}
            onChange={(e) => setTotalEggs(clamp0(e.target.valueAsNumber))} />
        </label>
        <label>Cracked
          <input type="number" min={0} value={cracked} disabled={entryLocked}
            onChange={(e) => setCracked(clamp0(e.target.valueAsNumber))} />
        </label>
        <label>Dirty
          <input type="number" min={0} value={dirty} disabled={entryLocked}
            onChange={(e) => setDirty(clamp0(e.target.valueAsNumber))} />
        </label>
        <label>Discarded
          <input type="number" min={0} value={discarded} disabled={entryLocked}
            onChange={(e) => setDiscarded(clamp0(e.target.valueAsNumber))} />
        </label>
        <label>Mortality
          <input type="number" min={0} value={mortality} disabled={entryLocked}
            onChange={(e) => setMortality(clamp0(e.target.valueAsNumber))} />
        </label>
      </div>

      {entryLocked && (
        <p className="warn">
          This day is already {existingStatus?.toLowerCase()} — its egg lots exist.
          Corrections need a manager adjustment (coming later).
        </p>
      )}

      <h3>Sellable production by grade</h3>
      <div className="form-grid">
        {grades.map((g) => (
          <label key={g.id}>{g.name}
            <input type="number" min={0} value={gradeQty[g.id] ?? 0} disabled={entryLocked}
              onChange={(e) => {
                setGradesTouched(true);
                setGradeQty((prev) => ({ ...prev, [g.id]: clamp0(e.target.valueAsNumber) }));
              }} />
          </label>
        ))}
      </div>
      <p className={gradesSum > sellable || lossesExceedTotal ? "error" : "muted"}>
        {lossesExceedTotal
          ? `Cracked + dirty + discarded (${losses}) exceed total eggs (${totalEggs}).`
          : `Graded ${gradesSum} of ${sellable} sellable (total − cracked − dirty − discarded).`}
      </p>

      {error && <p className="error">{error}</p>}
      {message && <p className="success">{message}</p>}

      <div className="actions">
        <button disabled={busy || !flockId || lossesExceedTotal || entryLocked}
          onClick={() => onSave(false)}>Save draft</button>
        <button
          disabled={busy || !flockId || lossesExceedTotal || gradesSum > sellable || entryLocked}
          onClick={() => onSave(true)}>
          Save &amp; submit (creates egg lots)
        </button>
      </div>
    </section>
  );
}
