import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { Plus } from "lucide-react";
import {
  activateEggGrade, createEggGrade, deactivateEggGrade, listEggGrades, updateEggGrade,
} from "../api/cluckwork";
import type { EggGrade } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useAuth } from "../auth/useAuth";
import { Dialog } from "../components/Dialog";
import { StatusBadge } from "../components/StatusBadge";

const GRADE_TYPES = ["Size", "Quality", "Custom"];

function errorMessage(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// F6 (#42): manage the farm's egg grades. No hard delete — grade lines, lots,
// and order items reference grades forever; deactivation only removes a grade
// from capture/order pickers while history keeps rendering its name.
export function GradesPage() {
  // The grade catalog is configuration — management is admin-only (#73). The
  // nav link hides for workers; a direct URL just renders the list read-only.
  const { isAdmin } = useAuth();
  const [grades, setGrades] = useState<EggGrade[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // create form (F131: lives in a dialog, not a bar above the table)
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState("");
  const [gradeType, setGradeType] = useState("Size");
  const [sortOrder, setSortOrder] = useState(0);
  const [isSaleable, setIsSaleable] = useState(true);

  // edit form — same dialog treatment, opened from the row's edit button
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editName, setEditName] = useState("");
  const [editSort, setEditSort] = useState(0);
  const [editSaleable, setEditSaleable] = useState(true);

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

  const fetchGrades = () => listEggGrades({ includeInactive: true });

  useEffect(() => {
    fetchGrades()
      .then(setGrades)
      .catch(() => setError("Could not load grades. Is the API up?"));
  }, []);

  async function run(scope: string, action: (key: string) => Promise<unknown>) {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await action(keyFor(scope));
      // The refresh must succeed before the key rotates: if it throws, the key
      // survives and a retry replays the idempotent write instead of repeating it.
      setGrades(await fetchGrades());
      clearKey(scope);
      return true;
    } catch (err) {
      setError(errorMessage(err));
      return false;
    } finally {
      setBusy(false);
    }
  }

  // A dialog opens on a clean form; cancelling keeps whatever was typed until
  // the next open, so a stray Escape does not throw the entry away.
  function openCreate() {
    setError(null);
    setEditingId(null);
    setCreating(true);
  }

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    const ok = await run("create-grade", (key) =>
      createEggGrade({ name, gradeType, sortOrder, isSaleable }, key));
    if (ok) {
      setName("");
      setSortOrder(0);
      setIsSaleable(true);
      setCreating(false);
    }
  }

  function startEdit(g: EggGrade) {
    setError(null);
    setCreating(false);
    setEditingId(g.id);
    setEditName(g.name);
    setEditSort(g.sortOrder);
    setEditSaleable(g.isSaleable);
  }

  async function onSaveEdit(e: FormEvent) {
    e.preventDefault();
    const id = editingId;
    if (id === null) return;
    const ok = await run(`update:${id}`, (key) =>
      updateEggGrade(id, { name: editName, sortOrder: editSort, isSaleable: editSaleable }, key));
    if (ok) setEditingId(null);
  }

  if (error && grades === null) {
    return <section><h2>Grades</h2><p className="error">{error}</p></section>;
  }
  if (grades === null) {
    return <section><h2>Grades</h2><p className="muted">Loading…</p></section>;
  }

  const dialogOpen = creating || editingId !== null;

  return (
    <section>
      <div className="page-head">
        <h2>Egg grades</h2>
        {isAdmin && (
          <button type="button" onClick={openCreate}>
            <Plus size={16} aria-hidden /> New grade
          </button>
        )}
      </div>
      <p className="muted">
        Saleable grades appear in daily-entry and order pickers. Deactivating a grade
        removes it from pickers; existing stock and history are unaffected.
      </p>

      {/* Gated like the inline form was: a role change mid-edit closes it. */}
      <Dialog open={creating && isAdmin} title="New grade" onClose={() => setCreating(false)}>
        <form className="inline-form" onSubmit={onCreate}>
          <label>Name *
            <input value={name} required maxLength={50}
              onChange={(e) => setName(e.target.value)} />
          </label>
          <label>Type
            <select value={gradeType} onChange={(e) => setGradeType(e.target.value)}>
              {GRADE_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
            </select>
          </label>
          <label>Sort
            <input className="cell" type="number" value={sortOrder}
              onChange={(e) => setSortOrder(e.target.valueAsNumber || 0)} />
          </label>
          <label className="muted check">
            <input type="checkbox" checked={isSaleable}
              onChange={(e) => setIsSaleable(e.target.checked)} />
            saleable
          </label>
          {error && <p className="error">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={() => setCreating(false)}>Cancel</button>
            <button type="submit" disabled={busy}>Add grade</button>
          </div>
        </form>
      </Dialog>

      <Dialog open={editingId !== null && isAdmin} title="Edit grade" onClose={() => setEditingId(null)}>
        {/* noValidate: the row's save used to be a plain button, so native
            constraint validation never ran on these fields. */}
        <form className="inline-form" noValidate onSubmit={onSaveEdit}>
          <label>Name
            <input value={editName} maxLength={50}
              onChange={(e) => setEditName(e.target.value)} />
          </label>
          <label>Sort
            <input className="cell" type="number" value={editSort}
              onChange={(e) => setEditSort(e.target.valueAsNumber || 0)} />
          </label>
          <label className="muted check">
            <input type="checkbox" checked={editSaleable}
              onChange={(e) => setEditSaleable(e.target.checked)} />
            saleable
          </label>
          {error && <p className="error">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={() => setEditingId(null)}>Cancel</button>
            <button type="submit" disabled={busy}>Save</button>
          </div>
        </form>
      </Dialog>

      {/* A dialog carries its own error; showing it here too would double it. */}
      {error && !dialogOpen && <p className="error">{error}</p>}

      <table className="data">
        <thead>
          <tr><th>Name</th><th>Type</th><th>Sort</th><th>Saleable</th><th>Status</th><th></th></tr>
        </thead>
        <tbody>
          {grades.map((g) => (
            <tr key={g.id} className={g.active ? undefined : "inactive"}>
              <td>{g.name}</td>
              <td>{g.gradeType}</td>
              <td>{g.sortOrder}</td>
              <td>{g.isSaleable ? <span className="badge badge-ok">yes</span> : "—"}</td>
              <td><StatusBadge status={g.active ? "Active" : "Inactive"} /></td>
              <td>
                {isAdmin && (
                  <>
                    <button className="link" disabled={busy}
                      onClick={() => startEdit(g)}>edit</button>
                    {g.active ? (
                      <button className="link" disabled={busy}
                        onClick={() => void run(`deactivate:${g.id}`, (key) => deactivateEggGrade(g.id, key))}>
                        deactivate
                      </button>
                    ) : (
                      <button className="link" disabled={busy}
                        onClick={() => void run(`activate:${g.id}`, (key) => activateEggGrade(g.id, key))}>
                        activate
                      </button>
                    )}
                  </>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
