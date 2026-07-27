import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { KeyRound, Pencil, Plus } from "lucide-react";
import {
  assignFlock, createUser, listFlockAssignments, listFlocks, listUsers, setUserPassword,
  unassignFlock, updateUser,
} from "../api/cluckwork";
import type { Flock, FlockAssignment, User } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { usePendingAction } from "../components/usePendingAction";
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
  // #236 — the shared flight guard replaces the old `busy` state. `busy`
  // still inerts every trigger; isPending(scope) spins only the clicked one.
  // Pending scopes are composite where the action is payload-bound
  // (assign/unassign), and independent of the idempotency-key scopes.
  const { busy, isPending, run } = usePendingAction();

  const [creating, setCreating] = useState(false); // F131: create moved into a dialog
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState("Worker");
  const [name, setName] = useState(""); // #163 optional display name at creation

  // #163 edit: the user whose name is being edited, and the working value.
  const [editUser, setEditUser] = useState<User | null>(null);
  const [editName, setEditName] = useState("");
  // Synchronous target of the edit dialog — like activeUser for flock scoping,
  // so a save that resolves after the dialog was closed/reopened for another
  // user doesn't splice its result into the wrong dialog.
  const activeEdit = useRef<string | null>(null);

  // #165 password reset: kept in its own dialog rather than folded into the name
  // edit — setting someone's password is a different, higher-consequence action
  // and shouldn't be one stray keystroke away from a typo fix.
  const [pwUser, setPwUser] = useState<User | null>(null);
  const [pwValue, setPwValue] = useState("");
  const [pwConfirm, setPwConfirm] = useState("");
  const activePw = useRef<string | null>(null);

  // #103 flock scoping: expand a worker row to manage assignments.
  const [openUser, setOpenUser] = useState<string | null>(null);
  const [assignments, setAssignments] = useState<FlockAssignment[]>([]);
  // The user the dialog is CURRENTLY for, tracked synchronously (before state
  // commits). An assign/remove refresh, or a load, that resolves after the
  // dialog was closed and reopened for a different worker must not splice the
  // old worker's list or error into the new one (#154 review) — post-await
  // writes commit only while this still matches the request's target.
  const activeUser = useRef<string | null>(null);
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
    activeUser.current = userId;
    try {
      const list = await listFlockAssignments(userId);
      if (activeUser.current !== userId) return; // superseded by another open/close
      setAssignments(list);
      // Start every worker's dialog on the first active flock. Without this the
      // dropdown keeps the last worker's pick — open A, choose fl2, close, open
      // B, and B shows fl2 — so a distracted admin could assign the wrong flock.
      setAssignFlockId(flocks[0]?.id ?? "");
      setError(null);
      setOpenUser(userId);
    } catch (err) {
      if (activeUser.current !== userId) return;
      activeUser.current = null; // load failed → no dialog; surface on the page
      setError(errText(err));
    }
  }

  function closeAssignments() {
    activeUser.current = null;
    setOpenUser(null);
    setError(null);
  }

  async function onAssign() {
    const target = openUser;
    if (!target || !assignFlockId) return;
    // One string serves as both the pending scope and the idempotency-key
    // scope here — payload-bound either way.
    const scope = `assign:${target}:${assignFlockId}`;
    await run(scope, async () => {
      setError(null);
      try {
        await assignFlock(target, assignFlockId, keyFor(scope));
        const fresh = await listFlockAssignments(target);
        clearKey(scope);
        if (activeUser.current === target) setAssignments(fresh);
      } catch (err) {
        if (activeUser.current === target) setError(errText(err));
      }
    });
  }

  async function onUnassign(a: FlockAssignment) {
    const target = openUser;
    if (!target) return;
    // The KEY scope stays bound to the assignment id (the exact write being
    // retried); the PENDING scope is user:flock so the row's spinner matches
    // what the admin sees themselves removing.
    const keyScope = `unassign:${a.id}`;
    await run(`unassign:${target}:${a.flockId}`, async () => {
      setError(null);
      try {
        await unassignFlock(target, a.id, keyFor(keyScope));
        const fresh = await listFlockAssignments(target);
        clearKey(keyScope);
        if (activeUser.current === target) setAssignments(fresh);
      } catch (err) {
        if (activeUser.current === target) setError(errText(err));
      }
    });
  }

  const flockName = (id: string | null) =>
    flocks.find((f) => f.id === id)?.name ?? (id ? id.slice(0, 8) : "farm-wide");

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    await run("create", async () => {
      setError(null);
      setMessage(null);
      try {
        const scope = `create:${email.trim().toLowerCase()}`;
        await createUser(
          { email: email.trim(), password, role, name: name.trim() || undefined },
          keyFor(scope));
        // Clear the key the instant the WRITE is confirmed — before the refresh —
        // so a later edit of the just-created user (a changed payload) can't replay
        // this cached response if the refresh below fails (#163 review).
        clearKey(scope);
        setUsers(await listUsers());
        setMessage(`${role} account created for ${email.trim()}.`);
        setEmail("");
        setPassword("");
        setRole("Worker");
        setName("");
        setCreating(false);
      } catch (err) {
        setError(errText(err));
      }
    });
  }

  // #163 — open the edit dialog seeded with the user's current name.
  function openEdit(u: User) {
    setError(null);
    setMessage(null);
    setEditName(u.displayName ?? "");
    activeEdit.current = u.id;
    setEditUser(u);
  }

  function closeEdit() {
    activeEdit.current = null;
    setEditUser(null);
  }

  // #165 — open/close the password dialog for a user.
  function openPassword(u: User) {
    setError(null);
    setMessage(null);
    setPwValue("");
    setPwConfirm("");
    activePw.current = u.id;
    setPwUser(u);
  }

  function closePassword() {
    activePw.current = null;
    // Don't leave the typed plaintext sitting in component state after the
    // dialog is gone (#165 review).
    setPwValue("");
    setPwConfirm("");
    setPwUser(null);
  }

  async function onSetPassword(e: FormEvent) {
    e.preventDefault();
    const target = pwUser;
    // The mismatch check stays OUTSIDE the flight (it is validation, not
    // work), so it keeps the old busy guard alongside the hook's.
    if (!target || busy) return;
    setError(null);
    setMessage(null);
    if (pwValue !== pwConfirm) {
      setError("The passwords don't match.");
      return;
    }
    const keyScope = `password:${target.id}`;
    await run(`set-password:${target.id}`, async () => {
      try {
        await setUserPassword(target.id, { newPassword: pwValue }, keyFor(keyScope));
        clearKey(keyScope); // write confirmed before any refresh (#163 review)
        if (activePw.current !== target.id) return; // dialog moved on
        setMessage(`Password set for ${target.email}. They have been signed out everywhere.`);
        closePassword();
      } catch (err) {
        if (activePw.current === target.id) setError(errText(err));
      }
    });
  }

  async function onUpdate(e: FormEvent) {
    e.preventDefault();
    const target = editUser;
    if (!target) return;
    const scope = `update:${target.id}`;
    await run(scope, async () => {
      setError(null);
      setMessage(null);
      try {
        // Blank clears the name back to "—" (null); the server normalizes too.
        await updateUser(target.id, { name: editName.trim() || null }, keyFor(scope));
        // Clear the key once the WRITE is confirmed (before the refresh), so a
        // follow-up edit isn't replayed against this cached response (#163 review).
        clearKey(scope);
        await listUsers().then(setUsers);
        // The dialog may have been dismissed/reopened for another user while this
        // was in flight; only touch the UI if it's still this edit (#163 review).
        if (activeEdit.current !== target.id) return;
        setMessage(`Updated ${target.email}.`);
        closeEdit();
      } catch (err) {
        if (activeEdit.current === target.id) setError(errText(err));
      }
    });
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
          <label>Name
            <input type="text" value={name} maxLength={128} autoComplete="off"
              onChange={(e) => setName(e.target.value)} />
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
            <BusyButton type="submit" disabled={busy} busy={isPending("create")}>Create user</BusyButton>
          </div>
        </form>
      </Dialog>

      {/* Each dialog carries its own error copy while it is up. */}
      {error && !creating && openUser === null && editUser === null && pwUser === null
        && <p className="error">{error}</p>}
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
                <button className="link" onClick={() => openEdit(u)}>
                  <Pencil size={14} aria-hidden /> edit
                </button>
                <button className="link" onClick={() => openPassword(u)}>
                  <KeyRound size={14} aria-hidden /> password
                </button>
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
                <BusyButton className="link" disabled={busy}
                  busy={openUser !== null && isPending(`unassign:${openUser}:${a.flockId}`)}
                  onClick={() => void onUnassign(a)}>
                  remove
                </BusyButton>
              </li>
            ))}
          </ul>
        )}
        <div className="inline-form">
          <select value={assignFlockId} onChange={(e) => setAssignFlockId(e.target.value)}>
            {flocks.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}
          </select>
          <BusyButton disabled={busy || !assignFlockId}
            busy={openUser !== null && isPending(`assign:${openUser}:${assignFlockId}`)}
            onClick={() => void onAssign()}>
            Assign flock
          </BusyButton>
        </div>
        {error && <p className="error">{error}</p>}
        <div className="dialog-foot">
          <button type="button" className="link" onClick={closeAssignments}>Done</button>
        </div>
      </Dialog>

      <Dialog
        open={editUser !== null}
        title={`Edit user — ${editUser?.email ?? ""}`}
        onClose={closeEdit}
      >
        <form className="inline-form" onSubmit={onUpdate}>
          <label>Name
            <input type="text" value={editName} maxLength={128} autoComplete="off"
              onChange={(e) => setEditName(e.target.value)} />
          </label>
          <p className="muted">Leave blank to clear the name.</p>
          {error && <p className="error">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeEdit}>Cancel</button>
            <BusyButton type="submit" disabled={busy}
              busy={editUser !== null && isPending(`update:${editUser.id}`)}>Save</BusyButton>
          </div>
        </form>
      </Dialog>

      <Dialog
        open={pwUser !== null}
        title={`Set password — ${pwUser?.email ?? ""}`}
        onClose={closePassword}
      >
        <form className="inline-form" onSubmit={onSetPassword}>
          <p className="muted">
            You don&apos;t need their current password. Setting it signs them out
            of every device — tell them the new password directly.
          </p>
          <label>New password (min 12 chars) *
            <input type="password" value={pwValue} required minLength={12}
              autoComplete="new-password"
              onChange={(e) => setPwValue(e.target.value)} />
          </label>
          <label>Confirm new password *
            <input type="password" value={pwConfirm} required
              autoComplete="new-password"
              onChange={(e) => setPwConfirm(e.target.value)} />
          </label>
          {error && <p className="error">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closePassword}>Cancel</button>
            <BusyButton type="submit" disabled={busy}
              busy={pwUser !== null && isPending(`set-password:${pwUser.id}`)}>Set password</BusyButton>
          </div>
        </form>
      </Dialog>
    </section>
  );
}
