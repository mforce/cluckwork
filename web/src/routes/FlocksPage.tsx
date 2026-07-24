import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { Plus } from "lucide-react";
import {
  archiveFlock, createFlock, depleteFlock, listBirdMovements, listFlocks, reactivateFlock,
  recordBirdMovement, updateFlock,
} from "../api/cluckwork";
import type { BirdMovement, Flock } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { Dialog } from "../components/Dialog";
import { StatusBadge } from "../components/StatusBadge";
import { useConfirm } from "../components/useConfirm";
import { useAuth } from "../auth/useAuth";
import { ageWeeks } from "../lib/dates";
import { useFarmToday } from "../farm/useFarm";
import { newId } from "../lib/ids";

function errorMessage(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// F7 (#47): manage flocks — create, correct identity fields, deplete, archive.
// Archived flocks leave pickers and the dashboard; this screen still shows them
// behind a toggle. Current bird count math is the mortality slice, not this one.
export function FlocksPage() {
  // Farm-local, not browser-local: since #35 the API judges "is this date in
  // the future?" against the FARM's day, so the pickers must agree (#123).
  const today = useFarmToday();
  // Creating a flock records the day's work (birds arrived); corrections,
  // lifecycle changes, and manual movements are admin-only (#73).
  const { isAdmin } = useAuth();
  const { confirm, confirmDialog } = useConfirm();
  const [flocks, setFlocks] = useState<Flock[] | null>(null);
  const [showArchived, setShowArchived] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // create form (F131: in a dialog)
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState("");
  const [breed, setBreed] = useState("");
  const [placed, setPlaced] = useState(today);
  const [count, setCount] = useState(100);

  // edit — dialog seeded from the row
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editName, setEditName] = useState("");
  const [editBreed, setEditBreed] = useState("");
  const [editPlaced, setEditPlaced] = useState("");
  const [editCount, setEditCount] = useState(0);

  // bird ledger (#54): one flock's movements open at a time
  const [ledgerFlockId, setLedgerFlockId] = useState<string | null>(null);
  const [movements, setMovements] = useState<BirdMovement[] | null>(null);
  const [mvDate, setMvDate] = useState(today);
  const [mvType, setMvType] = useState("Cull");
  const [mvQty, setMvQty] = useState(1);
  const [mvNote, setMvNote] = useState("");
  const [recording, setRecording] = useState(false); // F131: movement capture in a dialog

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

  // F135: the two lifecycle changes ask first. Named handlers rather than the
  // inline row lambdas they replace, because the ask is now awaited.
  async function onDeplete(f: Flock) {
    const ok = await confirm({
      title: `Deplete "${f.name}"?`,
      body: "The flock stops accepting new entries. Backfilling past dates still works.",
      confirmLabel: "Deplete flock",
      destructive: true,
    });
    if (ok) await run(`deplete:${f.id}`, (key) => depleteFlock(f.id, key));
  }

  async function onArchive(f: Flock) {
    const ok = await confirm({
      title: `Archive "${f.name}"?`,
      body: "It disappears from pickers and the dashboard, and accepts nothing new.",
      confirmLabel: "Archive flock",
      destructive: true,
    });
    if (ok) await run(`archive:${f.id}`, (key) => archiveFlock(f.id, key));
  }

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    const ok = await run("create-flock", (key) =>
      createFlock({ name, breed, placementDate: placed, initialCount: count }, key));
    if (ok) {
      setName("");
      setBreed("");
      setPlaced(today);
      setCount(100);
      setCreating(false);
    }
  }

  function startEdit(f: Flock) {
    setError(null);
    setCreating(false);
    setEditingId(f.id);
    setEditName(f.name);
    setEditBreed(f.breed);
    setEditPlaced(f.placementDate);
    setEditCount(f.initialCount);
  }

  async function onSaveEdit(e: FormEvent) {
    e.preventDefault();
    const id = editingId;
    if (id === null) return;
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
    setRecording(false); // a movement dialog belongs to the ledger that opened it
    if (ledgerFlockId === id) {
      setLedgerFlockId(null);
      ledgerRequest.current = null;
      return;
    }
    setLedgerFlockId(id);
    setMovements(null);
    setMvDate(today);
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
      setRecording(false);
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
      <div className="page-head">
        <h2>Flocks</h2>
        <button type="button" onClick={() => { setError(null); setEditingId(null); setCreating(true); }}>
          <Plus size={16} aria-hidden /> New flock
        </button>
      </div>
      <p className="muted">
        Deplete when the birds are gone; archive to hide a flock from pickers and
        the dashboard. History keeps resolving archived flocks' names.
      </p>

      <Dialog open={creating} title="New flock" onClose={() => setCreating(false)}>
        <form className="inline-form" onSubmit={onCreate}>
          <label>Name *
            <input value={name} required maxLength={100}
              onChange={(e) => setName(e.target.value)} />
          </label>
          <label>Breed *
            <input value={breed} required maxLength={100}
              onChange={(e) => setBreed(e.target.value)} />
          </label>
          <label>Placed
            <input type="date" value={placed} max={today} required
              onChange={(e) => setPlaced(e.target.value)} />
          </label>
          <label>Birds
            <input className="cell" type="number" min={1} value={count} required
              onChange={(e) => setCount(Math.max(1, e.target.valueAsNumber || 1))} />
          </label>
          {error && <p className="error">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={() => setCreating(false)}>Cancel</button>
            <button type="submit" disabled={busy}>Add flock</button>
          </div>
        </form>
      </Dialog>

      {/* Editing is admin-only, so a role change mid-edit closes it. */}
      <Dialog open={editingId !== null && isAdmin} title="Edit flock" onClose={() => setEditingId(null)}>
        {/* noValidate: the row's save used to be a plain button — native
            constraint validation never ran on these fields. */}
        <form className="inline-form" noValidate onSubmit={onSaveEdit}>
          <label>Edit name
            <input value={editName} maxLength={100}
              onChange={(e) => setEditName(e.target.value)} />
          </label>
          <label>Edit breed
            <input value={editBreed} maxLength={100}
              onChange={(e) => setEditBreed(e.target.value)} />
          </label>
          <label>Edit placement date
            <input type="date" value={editPlaced} max={today}
              onChange={(e) => setEditPlaced(e.target.value)} />
          </label>
          <label>Edit bird count
            <input className="cell" type="number" min={1} value={editCount}
              onChange={(e) => setEditCount(Math.max(1, e.target.valueAsNumber || 1))} />
          </label>
          {error && <p className="error">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={() => setEditingId(null)}>Cancel</button>
            <button type="submit" disabled={busy}>Save</button>
          </div>
        </form>
      </Dialog>

      {/* A dialog renders its own copy of the error; don't double it. */}
      {error && !creating && editingId === null && !recording && <p className="error">{error}</p>}

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
                <td>{f.name}</td>
                <td>{f.breed}</td>
                <td>{f.placementDate}</td>
                <td>{ageWeeks(f.placementDate)} wk</td>
                <td>
                  {f.currentBirds}
                  {f.currentBirds !== f.initialCount &&
                    <span className="muted"> / {f.initialCount}</span>}
                </td>
                <td><StatusBadge status={f.status} /></td>
                <td>
                  <button className="link" disabled={busy}
                    onClick={() => void openLedger(f.id)}>
                    {ledgerFlockId === f.id ? "close" : "birds"}
                  </button>
                  {isAdmin && (
                    <button className="link" disabled={busy}
                      onClick={() => startEdit(f)}>edit</button>
                  )}
                  {isAdmin && f.status === "Active" && (
                    <button className="link" disabled={busy}
                      onClick={() => void onDeplete(f)}>
                      deplete
                    </button>
                  )}
                  {isAdmin && f.status !== "Archived" && (
                    <button className="link" disabled={busy}
                      onClick={() => void onArchive(f)}>
                      archive
                    </button>
                  )}
                  {isAdmin && f.status !== "Active" && (
                    // The undo (#57): back to Active, full capture restored.
                    <button className="link" disabled={busy}
                      onClick={() => void run(`reactivate:${f.id}`, (key) => reactivateFlock(f.id, key))}>
                      reactivate
                    </button>
                  )}
                </td>
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
            Mortality rows come from submitted daily entries.
            {isAdmin
              ? " Record culls here; use a negative adjustment to correct a miscount."
              : " Recording culls and adjustments needs an admin."}
          </p>

          {isAdmin && (
            <button type="button" onClick={() => { setError(null); setRecording(true); }}>
              <Plus size={16} aria-hidden /> Record movement
            </button>
          )}

          <Dialog open={recording && isAdmin} title="Record bird movement" onClose={() => setRecording(false)}>
            <form className="inline-form" onSubmit={onRecordMovement}>
              <label>Date
                <input type="date" value={mvDate} max={today}
                  onChange={(e) => setMvDate(e.target.value)} />
              </label>
              <label>Type
                <select value={mvType} onChange={(e) => setMvType(e.target.value)}>
                  <option value="Cull">Cull</option>
                  <option value="Adjustment">Adjustment</option>
                </select>
              </label>
              <label>Birds
                <input className="cell" type="number" value={mvQty}
                  min={mvType === "Cull" ? 1 : undefined}
                  onChange={(e) => setMvQty(e.target.valueAsNumber || 0)} />
              </label>
              <label>Note
                <input value={mvNote} maxLength={500}
                  onChange={(e) => setMvNote(e.target.value)} />
              </label>
              {error && <p className="error">{error}</p>}
              <div className="dialog-foot">
                <button type="button" className="link" onClick={() => setRecording(false)}>Cancel</button>
                <button type="submit" disabled={busy || mvQty === 0}>Record</button>
              </div>
            </form>
          </Dialog>

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

      {confirmDialog}
    </section>
  );
}
