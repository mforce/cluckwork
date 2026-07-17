import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import {
  archiveFlock, createFlock, depleteFlock, listBirdMovements, listFlocks,
  recordBirdMovement, updateFlock,
} from "../api/cluckwork";
import type { BirdMovement, Flock } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { todayIso } from "../lib/dates";

function errorMessage(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

function ageWeeks(placementDate: string): number {
  const placed = new Date(placementDate + "T00:00:00");
  const days = (Date.now() - placed.getTime()) / 86_400_000;
  return Math.max(0, Math.floor(days / 7));
}

// F7 (#47): manage flocks — create, correct identity fields, deplete, archive.
// Archived flocks leave pickers and the dashboard; this screen still shows them
// behind a toggle. Current bird count math is the mortality slice, not this one.
export function FlocksPage() {
  const [flocks, setFlocks] = useState<Flock[] | null>(null);
  const [showArchived, setShowArchived] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // create form
  const [name, setName] = useState("");
  const [breed, setBreed] = useState("");
  const [placed, setPlaced] = useState(todayIso());
  const [count, setCount] = useState(100);

  // inline edit
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editName, setEditName] = useState("");
  const [editBreed, setEditBreed] = useState("");
  const [editPlaced, setEditPlaced] = useState("");
  const [editCount, setEditCount] = useState(0);

  // bird ledger (#54): one flock's movements open at a time
  const [ledgerFlockId, setLedgerFlockId] = useState<string | null>(null);
  const [movements, setMovements] = useState<BirdMovement[] | null>(null);
  const [mvDate, setMvDate] = useState(todayIso());
  const [mvType, setMvType] = useState("Cull");
  const [mvQty, setMvQty] = useState(1);
  const [mvNote, setMvNote] = useState("");

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

  const fetchFlocks = useCallback(
    () => listFlocks({ includeArchived: true, limit: 500 }),
    [],
  );

  useEffect(() => {
    fetchFlocks()
      .then(setFlocks)
      .catch(() => setError("Could not load flocks. Is the API up?"));
  }, [fetchFlocks]);

  async function run(scope: string, action: (key: string) => Promise<unknown>) {
    if (busy) return false;
    setBusy(true);
    setError(null);
    try {
      await action(keyFor(scope));
      // Refresh must succeed before the key rotates (grade-management review
      // lesson): if it throws, a retry replays the idempotent write.
      setFlocks(await fetchFlocks());
      clearKey(scope);
      return true;
    } catch (err) {
      setError(errorMessage(err));
      return false;
    } finally {
      setBusy(false);
    }
  }

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    const ok = await run("create-flock", (key) =>
      createFlock({ name, breed, placementDate: placed, initialCount: count }, key));
    if (ok) {
      setName("");
      setBreed("");
      setPlaced(todayIso());
      setCount(100);
    }
  }

  function startEdit(f: Flock) {
    setEditingId(f.id);
    setEditName(f.name);
    setEditBreed(f.breed);
    setEditPlaced(f.placementDate);
    setEditCount(f.initialCount);
  }

  async function onSaveEdit(id: string) {
    const ok = await run(`update:${id}`, (key) =>
      updateFlock(id, {
        name: editName, breed: editBreed,
        placementDate: editPlaced, initialCount: editCount,
      }, key));
    if (ok) setEditingId(null);
  }

  // Guards the async fetch: only the ledger currently open may write state,
  // so a slow response for flock A can't render under flock B's heading.
  const ledgerRequest = useRef<string | null>(null);

  async function openLedger(id: string) {
    if (ledgerFlockId === id) {
      setLedgerFlockId(null);
      ledgerRequest.current = null;
      return;
    }
    setLedgerFlockId(id);
    setMovements(null);
    setMvDate(todayIso());
    ledgerRequest.current = id;
    try {
      const rows = await listBirdMovements(id, { limit: 50 });
      if (ledgerRequest.current === id) setMovements(rows);
    } catch {
      if (ledgerRequest.current === id) setError("Could not load movements.");
    }
  }

  async function onRecordMovement(e: FormEvent) {
    e.preventDefault();
    if (!ledgerFlockId) return;
    const id = ledgerFlockId;
    const ok = await run(`movement:${id}`, async (key) => {
      await recordBirdMovement(id, {
        date: mvDate, type: mvType, quantity: mvQty,
        note: mvNote || undefined,
      }, key);
      const rows = await listBirdMovements(id, { limit: 50 });
      if (ledgerRequest.current === id) setMovements(rows);
    });
    if (ok) {
      setMvQty(1);
      setMvNote("");
    }
  }

  if (error && flocks === null) {
    return <section><h2>Flocks</h2><p className="error">{error}</p></section>;
  }
  if (flocks === null) {
    return <section><h2>Flocks</h2><p className="muted">Loading…</p></section>;
  }

  const visible = flocks.filter((f) => showArchived || f.status !== "Archived");
  const archivedCount = flocks.filter((f) => f.status === "Archived").length;

  return (
    <section>
      <h2>Flocks</h2>
      <p className="muted">
        Deplete when the birds are gone; archive to hide a flock from pickers and
        the dashboard. History keeps resolving archived flocks' names.
      </p>

      <form className="inline-form" onSubmit={onCreate}>
        <input placeholder="Name *" value={name} required maxLength={100}
          onChange={(e) => setName(e.target.value)} />
        <input placeholder="Breed *" value={breed} required maxLength={100}
          onChange={(e) => setBreed(e.target.value)} />
        <label className="muted">Placed
          <input type="date" value={placed} max={todayIso()} required
            onChange={(e) => setPlaced(e.target.value)} />
        </label>
        <label className="muted">Birds
          <input className="cell" type="number" min={1} value={count} required
            onChange={(e) => setCount(Math.max(1, e.target.valueAsNumber || 1))} />
        </label>
        <button type="submit" disabled={busy}>Add flock</button>
      </form>

      {error && <p className="error">{error}</p>}

      {archivedCount > 0 && (
        <label className="muted check">
          <input type="checkbox" checked={showArchived}
            onChange={(e) => setShowArchived(e.target.checked)} />
          show {archivedCount} archived
        </label>
      )}

      {visible.length === 0 ? (
        <p className="muted">No flocks yet.</p>
      ) : (
        <table className="data">
          <thead>
            <tr>
              <th>Name</th><th>Breed</th><th>Placed</th><th>Age</th>
              <th>Birds</th><th>Status</th><th></th>
            </tr>
          </thead>
          <tbody>
            {visible.map((f) => (
              <tr key={f.id} className={f.status === "Archived" ? "inactive" : undefined}>
                {editingId === f.id ? (
                  <>
                    <td>
                      <input value={editName} maxLength={100}
                        onChange={(e) => setEditName(e.target.value)} />
                    </td>
                    <td>
                      <input value={editBreed} maxLength={100}
                        onChange={(e) => setEditBreed(e.target.value)} />
                    </td>
                    <td>
                      <input type="date" value={editPlaced} max={todayIso()}
                        onChange={(e) => setEditPlaced(e.target.value)} />
                    </td>
                    <td>—</td>
                    <td>
                      <input className="cell" type="number" min={1} value={editCount}
                        onChange={(e) => setEditCount(Math.max(1, e.target.valueAsNumber || 1))} />
                    </td>
                    <td>{f.status}</td>
                    <td>
                      <button className="link" disabled={busy}
                        onClick={() => void onSaveEdit(f.id)}>save</button>
                      <button className="link" disabled={busy}
                        onClick={() => setEditingId(null)}>cancel</button>
                    </td>
                  </>
                ) : (
                  <>
                    <td>{f.name}</td>
                    <td>{f.breed}</td>
                    <td>{f.placementDate}</td>
                    <td>{ageWeeks(f.placementDate)} wk</td>
                    <td>
                      {f.currentBirds}
                      {f.currentBirds !== f.initialCount &&
                        <span className="muted"> / {f.initialCount}</span>}
                    </td>
                    <td>
                      {f.status === "Active" ? "Active"
                        : <span className="warn">{f.status}</span>}
                    </td>
                    <td>
                      <button className="link" disabled={busy}
                        onClick={() => void openLedger(f.id)}>
                        {ledgerFlockId === f.id ? "close" : "birds"}
                      </button>
                      <button className="link" disabled={busy}
                        onClick={() => startEdit(f)}>edit</button>
                      {f.status === "Active" && (
                        <button className="link" disabled={busy}
                          onClick={() => {
                            // One-way until reactivate ships (#57/#59).
                            if (window.confirm(`Deplete "${f.name}"? The flock stops accepting new entries (backfill for past dates still works).`))
                              void run(`deplete:${f.id}`, (key) => depleteFlock(f.id, key));
                          }}>
                          deplete
                        </button>
                      )}
                      {f.status !== "Archived" && (
                        <button className="link" disabled={busy}
                          onClick={() => {
                            if (window.confirm(`Archive "${f.name}"? It disappears from pickers and the dashboard and accepts nothing new.`))
                              void run(`archive:${f.id}`, (key) => archiveFlock(f.id, key));
                          }}>
                          archive
                        </button>
                      )}
                    </td>
                  </>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {ledgerFlockId && (
        <div className="order-panel">
          <h3>
            Bird ledger — {flocks.find((f) => f.id === ledgerFlockId)?.name ?? ""}
          </h3>
          <p className="muted">
            Mortality rows come from submitted daily entries. Record culls here;
            use a negative adjustment to correct a miscount.
          </p>

          <form className="inline-form" onSubmit={onRecordMovement}>
            <label className="muted">Date
              <input type="date" value={mvDate} max={todayIso()}
                onChange={(e) => setMvDate(e.target.value)} />
            </label>
            <select value={mvType} onChange={(e) => setMvType(e.target.value)}>
              <option value="Cull">Cull</option>
              <option value="Adjustment">Adjustment</option>
            </select>
            <label className="muted">Birds
              <input className="cell" type="number" value={mvQty}
                min={mvType === "Cull" ? 1 : undefined}
                onChange={(e) => setMvQty(e.target.valueAsNumber || 0)} />
            </label>
            <input placeholder="Note" value={mvNote} maxLength={500}
              onChange={(e) => setMvNote(e.target.value)} />
            <button type="submit" disabled={busy || mvQty === 0}>Record</button>
          </form>

          {movements === null ? (
            <p className="muted">Loading…</p>
          ) : movements.length === 0 ? (
            <p className="muted">No movements yet — the flock is at its initial count.</p>
          ) : (
            <table className="data">
              <thead>
                <tr><th>Date</th><th>Type</th><th>Birds</th><th>Note</th></tr>
              </thead>
              <tbody>
                {movements.map((m) => (
                  <tr key={m.id}>
                    <td>{m.date}</td>
                    <td>{m.type}</td>
                    <td>{m.quantity > 0 ? `−${m.quantity}` : `+${-m.quantity}`}</td>
                    <td>{m.note ?? "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}
    </section>
  );
}
