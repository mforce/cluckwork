import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { KeyRound, Pencil, Plus, ShieldCheck } from "lucide-react";
import {
  assignFlock, changeUserRole, createUser, listFlockAssignments, listFlocks, listUsers,
  setUserPassword, unassignFlock, updateUser,
} from "../api/cluckwork";
import type { Flock, FlockAssignment, User } from "../api/cluckwork";
import { ApiError, stepUp } from "../api/client";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { DialogError } from "../components/DialogError";
import { useDialogErrors } from "../components/useDialogErrors";
import { usePendingAction } from "../components/usePendingAction";
import { newId } from "../lib/ids";
import i18n from "../i18n";
import { ROLE_VALUES, roleLabel } from "../i18n/enums";
import { useAuth } from "../auth/useAuth";

// #308 — the one Owner-level role name gated by step-up, shared by the create
// and reset-password flows below. Matches Cluckwork.Domain.Accounts.Roles.Owner.
const OWNER_ROLE = "Admin";

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
  // #479 — one slot per PLACE a message can appear. Five dialogs on this
  // screen, and each used to render the one shared string unconditionally,
  // so whichever failure happened last appeared inside every open form.
  // Scopes are fixed per dialog rather than per row: a dialog is bound to
  // one user at a time, the `active*` refs below already drop a verdict
  // whose target moved on, and a fixed vocabulary keeps the mute set bounded.
  const errors = useDialogErrors();
  const setPageError = errors.setPage;
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

  // #308 — the caller's OWN current password, entered only when the operation
  // needs a step-up grant (creating an Owner; resetting an Owner's password).
  // Transient: read into a local const and cleared from state the instant it
  // is sent to /auth/step-up (onCreate/onSetPassword below), never held for
  // the write that follows. See the logout-clearing effect further down.
  const [createStepUpPassword, setCreateStepUpPassword] = useState("");
  const [pwStepUpPassword, setPwStepUpPassword] = useState("");
  const [roleStepUpPassword, setRoleStepUpPassword] = useState("");

  const { isAuthenticated } = useAuth();

  // #308 — belt-and-braces: logout already navigates away (unmounting this
  // page in the normal flow) and every dialog's own close path already clears
  // these, but this makes "proof state never survives logout" an explicit,
  // independently-testable guarantee rather than an incidental side effect of
  // unmounting.
  useEffect(() => {
    if (!isAuthenticated) {
      setCreateStepUpPassword("");
      setPwStepUpPassword("");
      setRoleStepUpPassword("");
    }
  }, [isAuthenticated]);

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

  // #355 — promote/demote an existing user's role, own dialog for the same
  // reason as password reset above: a higher-consequence action than a name
  // edit, not one stray keystroke away from a typo.
  const [roleUser, setRoleUser] = useState<User | null>(null);
  const [roleValue, setRoleValue] = useState("Worker");
  const activeRole = useRef<string | null>(null);

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
      .catch((err) => setPageError(errText(err)));
  }, [setPageError]);

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
      setOpenUser(userId);
    } catch (err) {
      if (activeUser.current !== userId) return;
      activeUser.current = null; // load failed → no dialog; surface on the page
      errors.setPage(errText(err));
    }
  }

  function closeAssignments() {
    activeUser.current = null;
    setOpenUser(null);
    errors.abandon("flock-access");
  }

  async function onAssign() {
    const target = openUser;
    if (!target || !assignFlockId) return;
    // One string serves as both the pending scope and the idempotency-key
    // scope here — payload-bound either way.
    const scope = `assign:${target}:${assignFlockId}`;
    await run(scope, async () => {
      errors.beginAttempt("flock-access");
      try {
        await assignFlock(target, assignFlockId, keyFor(scope));
        const fresh = await listFlockAssignments(target);
        clearKey(scope);
        if (activeUser.current === target) setAssignments(fresh);
      } catch (err) {
        if (activeUser.current === target) errors.report("flock-access", errText(err));
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
      errors.beginAttempt("flock-access");
      try {
        await unassignFlock(target, a.id, keyFor(keyScope));
        const fresh = await listFlockAssignments(target);
        clearKey(keyScope);
        if (activeUser.current === target) setAssignments(fresh);
      } catch (err) {
        if (activeUser.current === target) errors.report("flock-access", errText(err));
      }
    });
  }

  const flockName = (id: string | null) =>
    flocks.find((f) => f.id === id)?.name ?? (id ? id.slice(0, 8) : "farm-wide");

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    await run("create", async () => {
      errors.beginAttempt("create");
      setMessage(null);
      try {
        // #308 — creating another Owner needs a fresh step-up grant. Read the
        // entered password into a local const and clear it from state BEFORE
        // the network call, so it never sits in React state across the await.
        let stepUpToken: string | undefined;
        if (role === OWNER_ROLE) {
          const enteredPassword = createStepUpPassword;
          setCreateStepUpPassword("");
          stepUpToken = (await stepUp(enteredPassword)).token;
        }

        const scope = `create:${email.trim().toLowerCase()}`;
        await createUser(
          { email: email.trim(), password, role, name: name.trim() || undefined },
          keyFor(scope), stepUpToken);
        // Clear the key the instant the WRITE is confirmed — before the refresh —
        // so a later edit of the just-created user (a changed payload) can't replay
        // this cached response if the refresh below fails (#163 review).
        clearKey(scope);
        setUsers(await listUsers());
        setMessage(i18n.t("users:createSuccessMessage", { role: roleLabel(role), email: email.trim() }));
        // #336 review — close through closeCreate() rather than repeating the
        // field resets here. The duplicated list had already drifted: it never
        // cleared createStepUpPassword, so switching Owner -> Worker after
        // typing the proof password (the Owner branch above then never runs)
        // left the operator's own account password in state, visible on the
        // next reopen. One reset path means new dialog state can only be
        // forgotten in one place, not two — the #314 lesson, relearned.
        closeCreate();
      } catch (err) {
        errors.report("create", errText(err));
      }
    });
  }

  // #314 — close the create dialog from any path (Cancel, X, Escape, overlay).
  // Don't leave the typed plaintext password sitting in component state after
  // the dialog is gone (same pattern as closePassword's #165 fix). Role resets
  // too: a stale "Admin" from an abandoned attempt would otherwise still be
  // selected on reopen, so an operator who thinks they're starting fresh can
  // grant admin by accident. Matches the full reset onCreate does on success.
  function closeCreate() {
    setCreating(false);
    setEmail("");
    setPassword("");
    setRole("Worker");
    setName("");
    setCreateStepUpPassword(""); // #308 — never leave a typed proof password behind
    errors.abandon("create");
  }

  // #163 — open the edit dialog seeded with the user's current name.
  function openEdit(u: User) {
    setMessage(null);
    setEditName(u.displayName ?? "");
    activeEdit.current = u.id;
    setEditUser(u);
  }

  function closeEdit() {
    activeEdit.current = null;
    setEditUser(null);
    errors.abandon("edit-user");
  }

  // #165 — open/close the password dialog for a user.
  function openPassword(u: User) {
    setMessage(null);
    setPwValue("");
    setPwConfirm("");
    setPwStepUpPassword("");
    activePw.current = u.id;
    setPwUser(u);
  }

  function closePassword() {
    activePw.current = null;
    // Don't leave the typed plaintext sitting in component state after the
    // dialog is gone (#165 review; #308 for the step-up field).
    setPwValue("");
    setPwConfirm("");
    setPwStepUpPassword("");
    setPwUser(null);
    errors.abandon("set-password");
  }

  async function onSetPassword(e: FormEvent) {
    e.preventDefault();
    const target = pwUser;
    // The mismatch check stays OUTSIDE the flight (it is validation, not
    // work), so it keeps the old busy guard alongside the hook's.
    if (!target || busy) return;
    // Before the validation below, not only inside run(): a mismatch never
    // reaches run(), so without this the slot would still hold the previous
    // attempt's verdict — and a mute left by a dismissal would swallow this.
    errors.beginAttempt("set-password");
    setMessage(null);
    if (pwValue !== pwConfirm) {
      errors.report("set-password", i18n.t("users:passwordMismatchMessage"));
      return;
    }
    const keyScope = `password:${target.id}`;
    await run(`set-password:${target.id}`, async () => {
      try {
        // #308 — resetting an OWNER's password needs a fresh step-up grant.
        // Same read-then-clear-before-await pattern as onCreate above.
        let stepUpToken: string | undefined;
        if (target.role === OWNER_ROLE) {
          const enteredPassword = pwStepUpPassword;
          setPwStepUpPassword("");
          stepUpToken = (await stepUp(enteredPassword)).token;
        }

        await setUserPassword(target.id, { newPassword: pwValue }, keyFor(keyScope), stepUpToken);
        clearKey(keyScope); // write confirmed before any refresh (#163 review)
        if (activePw.current !== target.id) return; // dialog moved on
        setMessage(i18n.t("users:passwordSetMessage", { email: target.email }));
        closePassword();
      } catch (err) {
        if (activePw.current === target.id) errors.report("set-password", errText(err));
      }
    });
  }

  // #355 — open/close the role dialog for a user, seeded with their current role.
  function openRole(u: User) {
    setMessage(null);
    setRoleValue(u.role);
    setRoleStepUpPassword("");
    activeRole.current = u.id;
    setRoleUser(u);
  }

  function closeRole() {
    activeRole.current = null;
    setRoleStepUpPassword(""); // #308 — never leave a typed proof password behind
    setRoleUser(null);
    errors.abandon("change-role");
  }

  async function onChangeRole(e: FormEvent) {
    e.preventDefault();
    const target = roleUser;
    if (!target || busy) return;
    errors.beginAttempt("change-role");
    setMessage(null);
    const keyScope = `role:${target.id}`;
    await run(`change-role:${target.id}`, async () => {
      try {
        // #308 — promoting to OWNER needs a fresh step-up grant. Same
        // read-then-clear-before-await pattern as onCreate/onSetPassword.
        let stepUpToken: string | undefined;
        if (roleValue === OWNER_ROLE) {
          const enteredPassword = roleStepUpPassword;
          setRoleStepUpPassword("");
          stepUpToken = (await stepUp(enteredPassword)).token;
        }

        await changeUserRole(target.id, { role: roleValue }, keyFor(keyScope), stepUpToken);
        clearKey(keyScope); // write confirmed before any refresh (#163 review)
        setUsers(await listUsers());
        if (activeRole.current !== target.id) return; // dialog moved on
        setMessage(i18n.t("users:roleChangedMessage", { email: target.email, role: roleLabel(roleValue) }));
        closeRole();
      } catch (err) {
        if (activeRole.current === target.id) errors.report("change-role", errText(err));
      }
    });
  }

  async function onUpdate(e: FormEvent) {
    e.preventDefault();
    const target = editUser;
    if (!target) return;
    const scope = `update:${target.id}`;
    await run(scope, async () => {
      errors.beginAttempt("edit-user");
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
        setMessage(i18n.t("users:updatedMessage", { email: target.email }));
        closeEdit();
      } catch (err) {
        if (activeEdit.current === target.id) errors.report("edit-user", errText(err));
      }
    });
  }

  // The list read failed and there is nothing to show: a fatal page state,
  // never a dialog's (no dialog can be open before the screen renders).
  if (errors.page && users === null) return <section><h2>{t("heading")}</h2><p className="error" role="alert">{errors.page}</p></section>;
  if (users === null) return <section><h2>{t("heading")}</h2><p className="muted">{tc("loading")}</p></section>;

  return (
    <section>
      <div className="page-head">
        <h2>{t("heading")}</h2>
        <button type="button" onClick={() => { setMessage(null); setCreating(true); }}>
          <Plus size={16} aria-hidden /> {t("newUserButton")}
        </button>
      </div>
      <p className="muted">
        {t("roleDescription")}
      </p>

      <Dialog open={creating} title={t("newUserButton")} onClose={closeCreate}>
        <form className="inline-form" onSubmit={onCreate}>
          <label>{t("emailFieldLabel")}
            <input type="email" value={email} required maxLength={256}
              autoComplete="off"
              onChange={(e) => setEmail(e.target.value)} />
          </label>
          <label>{t("passwordFieldLabel")}
            <input type="password" value={password}
              required minLength={12} maxLength={256} autoComplete="new-password"
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
          {/* #308 — prompts ONLY for the sensitive case (creating another
              Owner); every other role stays exactly as it was. */}
          {role === OWNER_ROLE && (
            <>
              <p className="muted">{t("stepUpCreateHint")}</p>
              <label>{t("stepUpFieldLabel")}
                <input type="password" value={createStepUpPassword} required maxLength={256}
                  autoComplete="current-password"
                  onChange={(e) => setCreateStepUpPassword(e.target.value)} />
              </label>
            </>
          )}
          <DialogError errors={errors} scope="create" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeCreate}>{tc("cancel")}</button>
            <BusyButton type="submit" disabled={busy} busy={isPending("create")}>{t("createUserButton")}</BusyButton>
          </div>
        </form>
      </Dialog>

      {/* Each dialog carries its own error copy while it is up. */}
      {/* Unconditional since #479. The five-way guard this replaces existed
          because every dialog rendered the same string, so the page had to
          suppress itself whenever any of them was up. Each dialog now reads
          a slot of its own and there is nothing here to double up on. */}
      {errors.page && <p className="error" role="alert">{errors.page}</p>}
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
                <button className="link" onClick={() => openRole(u)}>
                  <ShieldCheck size={14} aria-hidden /> {t("changeRoleButton")}
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
                <BusyButton className="link" disabled={busy}
                  busy={openUser !== null && isPending(`unassign:${openUser}:${a.flockId}`)}
                  onClick={() => void onUnassign(a)}>
                  {t("removeAssignmentButton")}
                </BusyButton>
              </li>
            ))}
          </ul>
        )}
        <div className="inline-form">
          {/* Disabled during any flight: the assign scope embeds the selected
              flock id, so changing the selection mid-flight would re-point
              isPending at a scope nobody is running and drop the spinner
              while the request is still open (#242 review). */}
          <select value={assignFlockId} disabled={busy}
            onChange={(e) => setAssignFlockId(e.target.value)}>
            {flocks.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}
          </select>
          <BusyButton disabled={busy || !assignFlockId}
            busy={openUser !== null && isPending(`assign:${openUser}:${assignFlockId}`)}
            onClick={() => void onAssign()}>
            {t("assignFlockButton")}
          </BusyButton>
        </div>
        <DialogError errors={errors} scope="flock-access" />
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
          <DialogError errors={errors} scope="edit-user" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeEdit}>{tc("cancel")}</button>
            <BusyButton type="submit" disabled={busy}
              busy={editUser !== null && isPending(`update:${editUser.id}`)}>{tc("save")}</BusyButton>
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
            <input type="password" value={pwValue} required minLength={12} maxLength={256}
              autoComplete="new-password"
              onChange={(e) => setPwValue(e.target.value)} />
          </label>
          <label>{t("confirmPasswordFieldLabel")}
            <input type="password" value={pwConfirm} required maxLength={256}
              autoComplete="new-password"
              onChange={(e) => setPwConfirm(e.target.value)} />
          </label>
          {/* #308 — prompts ONLY when the TARGET currently holds Owner; every
              other role's reset stays exactly as it was (#165). */}
          {pwUser?.role === OWNER_ROLE && (
            <>
              <p className="muted">{t("stepUpResetHint")}</p>
              <label>{t("stepUpFieldLabel")}
                <input type="password" value={pwStepUpPassword} required maxLength={256}
                  autoComplete="current-password"
                  onChange={(e) => setPwStepUpPassword(e.target.value)} />
              </label>
            </>
          )}
          <DialogError errors={errors} scope="set-password" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closePassword}>{tc("cancel")}</button>
            <BusyButton type="submit" disabled={busy}
              busy={pwUser !== null && isPending(`set-password:${pwUser.id}`)}>{t("setPasswordButton")}</BusyButton>
          </div>
        </form>
      </Dialog>

      <Dialog
        open={roleUser !== null}
        title={t("changeRoleTitle", { email: roleUser?.email ?? "" })}
        onClose={closeRole}
      >
        <form className="inline-form" onSubmit={onChangeRole}>
          <p className="muted">
            {t("roleDialogHint")}
          </p>
          <label>{t("roleFieldLabel")}
            <select value={roleValue} onChange={(e) => setRoleValue(e.target.value)}>
              {ROLE_VALUES.map((v) => (
                <option key={v} value={v}>
                  {v === "Admin" ? t("adminRoleOption", { label: roleLabel(v) }) : roleLabel(v)}
                </option>
              ))}
            </select>
          </label>
          {/* #308 — prompts ONLY when the REQUESTED role is Owner; every
              other target role stays exactly as it was. */}
          {roleValue === OWNER_ROLE && (
            <>
              <p className="muted">{t("stepUpRoleHint")}</p>
              <label>{t("stepUpFieldLabel")}
                <input type="password" value={roleStepUpPassword} required maxLength={256}
                  autoComplete="current-password"
                  onChange={(e) => setRoleStepUpPassword(e.target.value)} />
              </label>
            </>
          )}
          <DialogError errors={errors} scope="change-role" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeRole}>{tc("cancel")}</button>
            <BusyButton type="submit" disabled={busy}
              busy={roleUser !== null && isPending(`change-role:${roleUser.id}`)}>{t("changeRoleSubmitButton")}</BusyButton>
          </div>
        </form>
      </Dialog>
    </section>
  );
}
