import { useEffect, useMemo, useState } from "react";
import type { FormEvent } from "react";
import {
  createFlock, listEggGrades, listFlocks, recordDailyEntry, submitDailyEntry,
} from "../api/cluckwork";
import type { EggGrade, Flock } from "../api/cluckwork";
import { ApiError } from "../api/client";

function todayIso(): string {
  return new Date().toISOString().slice(0, 10);
}

// F1 (#21): record the day's production by grade, then submit — submitting
// turns grade lines into egg lots (stock).
export function DailyEntryPage() {
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

  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  // inline flock creation
  const [showNewFlock, setShowNewFlock] = useState(false);
  const [newFlockName, setNewFlockName] = useState("");
  const [newFlockBreed, setNewFlockBreed] = useState("");
  const [newFlockCount, setNewFlockCount] = useState(100);

  useEffect(() => {
    Promise.all([listFlocks(), listEggGrades()])
      .then(([f, g]) => {
        setFlocks(f);
        setGrades(g.filter((x) => x.isSaleable));
        if (f.length > 0) setFlockId(f[0].id);
      })
      .catch(() => setLoadError("Could not load flocks/grades. Is the API up?"));
  }, []);

  const gradesSum = useMemo(
    () => Object.values(gradeQty).reduce((a, b) => a + (b || 0), 0),
    [gradeQty],
  );
  const sellable = totalEggs - cracked - dirty - discarded;
  const selectedFlock = flocks.find((f) => f.id === flockId);

  async function onCreateFlock(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      const created = await createFlock({
        name: newFlockName,
        breed: newFlockBreed,
        placementDate: todayIso(),
        initialCount: newFlockCount,
      });
      const refreshed = await listFlocks();
      setFlocks(refreshed);
      setFlockId(created.id);
      setShowNewFlock(false);
      setNewFlockName("");
      setNewFlockBreed("");
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not create flock.");
    }
  }

  async function saveEntry(): Promise<string> {
    if (!selectedFlock) throw new Error("Pick a flock first.");
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
      grades: lines,
    });
    return created.id;
  }

  async function onSave(submit: boolean) {
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      const id = await saveEntry();
      if (submit) {
        const result = await submitDailyEntry(id);
        setMessage(`Submitted — ${result.eggLotIds.length} egg lot(s) created.`);
      } else {
        setMessage("Draft saved.");
      }
    } catch (err) {
      setError(err instanceof ApiError ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

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
          <input type="number" min={1} value={newFlockCount} required
            onChange={(e) => setNewFlockCount(e.target.valueAsNumber || 0)} />
          <button type="submit">Create flock</button>
        </form>
      )}

      <div className="form-grid">
        <label>Date
          <input type="date" value={date} max={todayIso()}
            onChange={(e) => setDate(e.target.value)} />
        </label>
        <label>Total eggs
          <input type="number" min={0} value={totalEggs}
            onChange={(e) => setTotalEggs(e.target.valueAsNumber || 0)} />
        </label>
        <label>Cracked
          <input type="number" min={0} value={cracked}
            onChange={(e) => setCracked(e.target.valueAsNumber || 0)} />
        </label>
        <label>Dirty
          <input type="number" min={0} value={dirty}
            onChange={(e) => setDirty(e.target.valueAsNumber || 0)} />
        </label>
        <label>Discarded
          <input type="number" min={0} value={discarded}
            onChange={(e) => setDiscarded(e.target.valueAsNumber || 0)} />
        </label>
        <label>Mortality
          <input type="number" min={0} value={mortality}
            onChange={(e) => setMortality(e.target.valueAsNumber || 0)} />
        </label>
      </div>

      <h3>Sellable production by grade</h3>
      <div className="form-grid">
        {grades.map((g) => (
          <label key={g.id}>{g.name}
            <input type="number" min={0} value={gradeQty[g.id] ?? 0}
              onChange={(e) =>
                setGradeQty((prev) => ({ ...prev, [g.id]: e.target.valueAsNumber || 0 }))} />
          </label>
        ))}
      </div>
      <p className={gradesSum > sellable ? "error" : "muted"}>
        Graded {gradesSum} of {Math.max(sellable, 0)} sellable
        (total − cracked − dirty − discarded).
      </p>

      {error && <p className="error">{error}</p>}
      {message && <p className="success">{message}</p>}

      <div className="actions">
        <button disabled={busy || !flockId} onClick={() => onSave(false)}>Save draft</button>
        <button disabled={busy || !flockId || gradesSum > sellable} onClick={() => onSave(true)}>
          Save &amp; submit (creates egg lots)
        </button>
      </div>
    </section>
  );
}
