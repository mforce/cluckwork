import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { KeyRound, Pencil, Plus } from "lucide-react";
import {
  assignFlock, createUser, listFlockAssignments, listFlocks, listUsers, setUserPassword,
  unassignFlock, updateUser,
} from "../api/cluckwork";
import type { Flock, FlockAssignment, User } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { Dialog } from "../components/Dialog";
import { newId } from "../lib/ids";
import i18n from "../i18n";
import { ROLE_VALUES, roleLabel } from "../i18n/enums";

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// #73 — minimal user management: create a worker (or another admin) and see
// who exists. The full user-administration UI belongs to the RBAC slice.
export function UsersPage() {
  const { t } = useTranslation("users");
  const { t: tc } = useTranslation("common");

  const [users, setUsers] = useState<User[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

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
    if (!target || !assignFlockId || busy) return;
    setBusy(true);
    setError(null);
    const scope = `assign:${target}:${assignFlockId}`;
    try {
      await assignFlock(target, assignFlockId, keyFor(scope));
      const fresh = await listFlockAssignments(target);
      clearKey(scope);
      if (activeUser.current === target) setAssignments(fresh);
    } catch (err) {
      if (activeUser.current === target) setError(errText(err));
    } finally {
      setBusy(false);
    }
  }

  async function onUnassign(assignmentId: string) {
    const target = openUser;
    if (!target || busy) return;
    setBusy(true);
    setError(null);
    const scope = `unassign:${assignmentId}`;
    try {
      await unassignFlock(target, assignmentId, keyFor(scope));
      const fresh = await listFlockAssignments(target);
      clearKey(scope);
      if (activeUser.current === target) setAssignments(fresh);
    } catch (err) {
      if (activeUser.current === target) setError(errText(err));
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
      await createUser(
        { email: email.trim(), password, role, name: name.trim() || undefined },
        keyFor(scope));
      // Clear the key the instant the WRITE is confirmed — before the refresh —
      // so a later edit of the just-created user (a changed payload) can't replay
      // this cached response if the refresh below fails (#163 review).
      clearKey(scope);
      setUsers(await listUsers());
      setMessage(i18n.t("users:createSuccessMessage", { role: roleLabel(role), email: email.trim() }));
      setEmail("");
      setPassword("");
      setRole("Worker");
      setName("");
      setCreating(false);
    } catch (err) {
      setError(errText(err));
    } finally {
      setBusy(false);
    }
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
    if (!target || busy) return;
    setError(null);
    setMessage(null);
    if (pwValue !== pwConfirm) {
      setError(i18n.t("users:passwordMismatchMessage"));
      return;
    }
    setBusy(true);
    const scope = `password:${target.id}`;
    try {
      await setUserPassword(target.id, { newPassword: pwValue }, keyFor(scope));
      clearKey(scope); // write confirmed before any refresh (#163 review)
      if (activePw.current !== target.id) return; // dialog moved on
      setMessage(i18n.t("users:passwordSetMessage", { email: target.email }));
      closePassword();
    } catch (err) {
      if (activePw.current === target.id) setError(errText(err));
    } finally {
      setBusy(false);
    }
  }

  async function onUpdate(e: FormEvent) {
    e.preventDefault();
    const target = editUser;
    if (!target || busy) return;
    setBusy(true);
    setError(null);
    setMessage(null);
    const scope = `update:${target.id}`;
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
      setMessage(i18n.t("users:updatedMessage", { email: target.email }));
      closeEdit();
    } catch (err) {
      if (activeEdit.current === target.id) setError(errText(err));
    } finally {
      setBusy(false);
    }
  }

  if (error && users === null) return <section><h2>{t("heading")}</h2><p className="error">{error}</p></section>;
  if (users === null) return <section><h2>{t("heading")}</h2><p className="muted">{tc("loading")}</p></section>;

  return (
    <section>
      <div className="page-head">
        <h2>{t("heading")}</h2>
        <button type="button" onClick={() => { setError(null); setMessage(null); setCreating(true); }}>
          <Plus size={16} aria-hidden /> {t("newUserButton")}
        </button>
      </div>
      <p className="muted">
        {t("roleDescription")}
      </p>

      <Dialog open={creating} title={t("newUserButton")} onClose={() => setCreating(false)}>
        <form className="inline-form" onSubmit={onCreate}>
          <label>{t("emailFieldLabel")}
            <input type="email" value={email} required maxLength={256}
              autoComplete="off"
              onChange={(e) => setEmail(e.target.value)} />
          </label>
          <label>{t("passwordFieldLabel")}
            <input type="password" value={password}
              required minLength={12} autoComplete="new-password"
              onChange={(e) => setPassword(e.target.value)} />
          </label>
          <label>{t("nameFieldLabel")}
            <input type="text" value={name} maxLength={128} autoComplete="off"
              onChange={(e) => setName(e.target.value)} />
          </label>
          <label>{t("roleFieldLabel")}
            <select value={role} onChange={(e) => setRole(e.target.value)}>
              {ROLE_VALUES.map((v) => (
                <option key={v} value={v}>
                  {v === "Admin" ? t("adminRoleOption", { label: roleLabel(v) }) : roleLabel(v)}
                </option>
              ))}
            </select>
          </label>
          {error && <p className="error">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={() => setCreating(false)}>{tc("cancel")}</button>
            <button type="submit" disabled={busy}>{t("createUserButton")}</button>
          </div>
        </form>
      </Dialog>

      {/* Each dialog carries its own error copy while it is up. */}
      {error && !creating && openUser === null && editUser === null && pwUser === null
        && <p className="error">{error}</p>}
      {message && <p className="success">{message}</p>}

      <table className="data">
        <thead>
          <tr>
            <th>{t("emailColumnHeader")}</th>
            <th>{t("nameColumnHeader")}</th>
            <th>{t("roleColumnHeader")}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {users.map((u) => (
            <tr key={u.id}>
              <td>{u.email}</td>
              <td>{u.displayName ?? "—"}</td>
              <td>{roleLabel(u.role)}</td>
              <td>
                <button className="link" onClick={() => openEdit(u)}>
                  <Pencil size={14} aria-hidden /> {t("editButton")}
                </button>
                <button className="link" onClick={() => openPassword(u)}>
                  <KeyRound size={14} aria-hidden /> {t("resetPasswordButton")}
                </button>
                {u.role === "Worker" && (
                  <button className="link" onClick={() => void openAssignments(u.id)}>
                    {t("flocksButton")}
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      <Dialog
        open={openUser !== null}
        title={t("flockAccessTitle", { email: users.find((u) => u.id === openUser)?.email ?? "" })}
        onClose={closeAssignments}
      >
        <p className="muted">
          {t("flockAccessHint")}
        </p>
        {assignments.length === 0 ? (
          <p className="muted">{t("noAssignmentsMessage")}</p>
        ) : (
          <ul>
            {assignments.map((a) => (
              <li key={a.id}>
                {flockName(a.flockId)}{" "}
                <button className="link" disabled={busy} onClick={() => void onUnassign(a.id)}>
                  {t("removeAssignmentButton")}
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
            {t("assignFlockButton")}
          </button>
        </div>
        {error && <p className="error">{error}</p>}
        <div className="dialog-foot">
          <button type="button" className="link" onClick={closeAssignments}>{t("doneButton")}</button>
        </div>
      </Dialog>

      <Dialog
        open={editUser !== null}
        title={t("editUserTitle", { email: editUser?.email ?? "" })}
        onClose={closeEdit}
      >
        <form className="inline-form" onSubmit={onUpdate}>
          <label>{t("nameFieldLabel")}
            <input type="text" value={editName} maxLength={128} autoComplete="off"
              onChange={(e) => setEditName(e.target.value)} />
          </label>
          <p className="muted">{t("clearNameHint")}</p>
          {error && <p className="error">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeEdit}>{tc("cancel")}</button>
            <button type="submit" disabled={busy}>{tc("save")}</button>
          </div>
        </form>
      </Dialog>

      <Dialog
        open={pwUser !== null}
        title={t("setPasswordTitle", { email: pwUser?.email ?? "" })}
        onClose={closePassword}
      >
        <form className="inline-form" onSubmit={onSetPassword}>
          <p className="muted">
            {t("passwordDialogHint")}
          </p>
          <label>{t("newPasswordFieldLabel")}
            <input type="password" value={pwValue} required minLength={12}
              autoComplete="new-password"
              onChange={(e) => setPwValue(e.target.value)} />
          </label>
          <label>{t("confirmPasswordFieldLabel")}
            <input type="password" value={pwConfirm} required
              autoComplete="new-password"
              onChange={(e) => setPwConfirm(e.target.value)} />
          </label>
          {error && <p className="error">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closePassword}>{tc("cancel")}</button>
            <button type="submit" disabled={busy}>{t("setPasswordButton")}</button>
          </div>
        </form>
      </Dialog>
    </section>
  );
}
