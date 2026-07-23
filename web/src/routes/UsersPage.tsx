import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { Plus } from "lucide-react";
import {
  assignFlock, createUser, listFlockAssignments, listFlocks, listUsers, unassignFlock,
} from "../api/cluckwork";
import type { Flock, FlockAssignment, User } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { Dialog } from "../components/Dialog";
import { newId } from "../lib/ids";

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// #73 — minimal user management: create a worker (or another admin) and see
// who exists. The full user-administration UI belongs to the RBAC slice.
export function UsersPage() {
  const [users, setUsers] = useState<User[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const [creating, setCreating] = useState(false); // F131: create moved into a dialog
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState("Worker");

  // #103 flock scoping: expand a worker row to manage assignments.
  const [openUser, setOpenUser] = useState<string | null>(null);
  const [assignments, setAssignments] = useState<FlockAssignment[]>([]);
  const [flocks, setFlocks] = useState<Flock[]>([]);
  const [assignFlockId, setAssignFlockId] = useState("");

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

  useEffect(() => {
    Promise.all([listUsers(), listFlocks()])
      .then(([u, f]) => {
        setUsers(u);
        const active = f.filter((x) => x.status === "Active");
        setFlocks(active);
        // Initialize from the ACTIVE list the dropdown shows — an inactive
        // first flock would preselect an id no option carries, and Assign
        // would 404 (conventions review of #104).
        if (active.length > 0) setAssignFlockId(active[0].id);
      })
      .catch((err) => setError(errText(err)));
  }, []);

  // F133: flock scoping is a per-worker action, so it opens in the shared dialog
  // like the other per-row surfaces (#131) — the row button opens it, the dialog
  // closes it. Load the assignments before opening so the panel is never empty
  // mid-flight; a load failure surfaces on the page and the dialog stays shut.
  async function openAssignments(userId: string) {
    try {
      setAssignments(await listFlockAssignments(userId));
      setError(null);
      setOpenUser(userId);
    } catch (err) {
      setError(errText(err));
    }
  }

  function closeAssignments() {
    setOpenUser(null);
    setError(null);
  }

  async function onAssign() {
    if (!openUser || !assignFlockId || busy) return;
    setBusy(true);
    setError(null);
    const scope = `assign:${openUser}:${assignFlockId}`;
    try {
      await assignFlock(openUser, assignFlockId, keyFor(scope));
      setAssignments(await listFlockAssignments(openUser));
      clearKey(scope);
    } catch (err) {
      setError(errText(err));
    } finally {
      setBusy(false);
    }
  }

  async function onUnassign(assignmentId: string) {
    if (!openUser || busy) return;
    setBusy(true);
    setError(null);
    const scope = `unassign:${assignmentId}`;
    try {
      await unassignFlock(openUser, assignmentId, keyFor(scope));
      setAssignments(await listFlockAssignments(openUser));
      clearKey(scope);
    } catch (err) {
      setError(errText(err));
    } finally {
      setBusy(false);
    }
  }

  const flockName = (id: string | null) =>
    flocks.find((f) => f.id === id)?.name ?? (id ? id.slice(0, 8) : "farm-wide");

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    if (busy) return;
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      const scope = `create:${email.trim().toLowerCase()}`;
      await createUser({ email: email.trim(), password, role }, keyFor(scope));
      setUsers(await listUsers());
      clearKey(scope);
      setMessage(`${role} account created for ${email.trim()}.`);
      setEmail("");
      setPassword("");
      setRole("Worker");
      setCreating(false);
    } catch (err) {
      setError(errText(err));
    } finally {
      setBusy(false);
    }
  }

  if (error && users === null) return <section><h2>Users</h2><p className="error">{error}</p></section>;
  if (users === null) return <section><h2>Users</h2><p className="muted">Loading…</p></section>;

  return (
    <section>
      <div className="page-head">
        <h2>Users</h2>
        <button type="button" onClick={() => { setError(null); setMessage(null); setCreating(true); }}>
          <Plus size={16} aria-hidden /> New user
        </button>
      </div>
      <p className="muted">
        Workers record the day&apos;s work (optionally narrowed to assigned
        flocks). Managers additionally correct, void, and configure. Sales
        handles customers, orders, and payments. Read-only sees stock, history,
        and reports. Admin (owner) does everything, including managing users.
      </p>

      <Dialog open={creating} title="New user" onClose={() => setCreating(false)}>
        <form className="inline-form" onSubmit={onCreate}>
          <label>Email *
            <input type="email" value={email} required maxLength={256}
              autoComplete="off"
              onChange={(e) => setEmail(e.target.value)} />
          </label>
          <label>Password (min 12 chars) *
            <input type="password" value={password}
              required minLength={12} autoComplete="new-password"
              onChange={(e) => setPassword(e.target.value)} />
          </label>
          <label>Role
            <select value={role} onChange={(e) => setRole(e.target.value)}>
              <option value="Worker">Worker</option>
              <option value="Admin">Admin (owner)</option>
              <option value="Manager">Manager</option>
              <option value="Sales">Sales</option>
              <option value="ReadOnly">Read-only</option>
            </select>
          </label>
          {error && <p className="error">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={() => setCreating(false)}>Cancel</button>
            <button type="submit" disabled={busy}>Create user</button>
          </div>
        </form>
      </Dialog>

      {/* Each dialog carries its own error copy while it is up. */}
      {error && !creating && openUser === null && <p className="error">{error}</p>}
      {message && <p className="success">{message}</p>}

      <table className="data">
        <thead>
          <tr><th>Email</th><th>Name</th><th>Role</th><th></th></tr>
        </thead>
        <tbody>
          {users.map((u) => (
            <tr key={u.id}>
              <td>{u.email}</td>
              <td>{u.displayName ?? "—"}</td>
              <td>{u.role}</td>
              <td>
                {u.role === "Worker" && (
                  <button className="link" onClick={() => void openAssignments(u.id)}>
                    flocks
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      <Dialog
        open={openUser !== null}
        title={`Flock access — ${users.find((u) => u.id === openUser)?.email ?? ""}`}
        onClose={closeAssignments}
      >
        <p className="muted">
          No assignments = the worker can record for any flock. The first
          assignment narrows them to the listed flocks only.
        </p>
        {assignments.length === 0 ? (
          <p className="muted">No assignments — account-wide access.</p>
        ) : (
          <ul>
            {assignments.map((a) => (
              <li key={a.id}>
                {flockName(a.flockId)}{" "}
                <button className="link" disabled={busy} onClick={() => void onUnassign(a.id)}>
                  remove
                </button>
              </li>
            ))}
          </ul>
        )}
        <div className="inline-form">
          <select value={assignFlockId} onChange={(e) => setAssignFlockId(e.target.value)}>
            {flocks.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}
          </select>
          <button disabled={busy || !assignFlockId} onClick={() => void onAssign()}>
            Assign flock
          </button>
        </div>
        {error && <p className="error">{error}</p>}
        <div className="dialog-foot">
          <button type="button" className="link" onClick={closeAssignments}>Done</button>
        </div>
      </Dialog>
    </section>
  );
}
