import { useEffect, useId, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Ban, KeyRound, Mail, Pencil, Plus, RotateCcw, ShieldCheck } from "lucide-react";
import {
  assignFlock as apiAssignFlock, changeUserEmail, changeUserRole, createUser, disableUser, enableUser, listFlockAssignments,
  listUsers, setUserPassword, unassignFlock, updateUser,
} from "../api/cluckwork";
import type { Flock, FlockAssignment, User } from "../api/cluckwork";
import { ApiError, stepUp } from "../api/client";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { FlockPicker } from "../components/FlockPicker";
import type { PickerSnapshot } from "../components/NamedEntityPicker";
import { DialogError } from "../components/DialogError";
import { StatusBadge } from "../components/StatusBadge";
import { useDialogAction } from "../components/useDialogAction";
import { newId } from "../lib/ids";
import i18n from "../i18n";
import { ROLE_VALUES, roleLabel } from "../i18n/enums";
import { useAuth } from "../auth/useAuth";


function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// The dialogs on this screen (#703). Every write below keeps its per-record
// run scope (`update:<id>`, `assign:<user>:<flock>`, …) so `isPending` spins
// the one control it always did, and names its dialog through `run`'s
// `{ dialog }` option for the message slot and the session; `create` is its
// own dialog name. flock-access loads before it opens, so its open edge is
// `startLoad` + `openDialog` rather than `openDialog` alone.
const DIALOG_SCOPES = ["flock-access", "create", "edit-user", "set-password", "change-role", "change-email", "disable-enable"] as const;

// #73 — minimal user management: create a worker (or another admin) and see
// who exists. The full user-administration UI belongs to the RBAC slice.
export function UsersPage() {
  const { t } = useTranslation("users");
  const { t: tc } = useTranslation("common");

  const [users, setUsers] = useState<User[] | null>(null);
  // #703 — the flight guard (#236), the per-place message slots (#479: the
  // page, and each dialog by its own name) and the dialog-session generation
  // (#477 part 2) come from one shared hook. Pending scopes stay per record —
  // composite where the action is payload-bound (assign/unassign) — so
  // `isPending(scope)` spins only the clicked control; each write names its
  // dialog. Idempotency-key scopes are separate from all of these.
  const { busy, isPending, errors, run, openDialog, dismissDialog, startLoad } = useDialogAction(DIALOG_SCOPES);
  const setPageError = errors.setPage;
  const [message, setMessage] = useState<string | null>(null);

  const [creating, setCreating] = useState(false); // F131: create moved into a dialog
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState("Worker");
  const [name, setName] = useState(""); // #163 optional display name at creation

  // #308/#360 — the caller's OWN current password. All three local password
  // states serve every create/reset/role operation. Each is transient: read
  // into a local const and cleared from state before awaiting /auth/step-up,
  // never held for the write that follows. See the logout-clearing effect below.
  const [createStepUpPassword, setCreateStepUpPassword] = useState("");
  const [pwStepUpPassword, setPwStepUpPassword] = useState("");
  const [roleStepUpPassword, setRoleStepUpPassword] = useState("");

  const { isAuthenticated, userId: myId } = useAuth();
  // #356 — this screen's own identity, so the disable/enable actions can be
  // withheld from the caller's own row (the server 400s a self-target, but the
  // UI should not present a button that can only fail). Read from the TOKEN
  // (useAuth's userId), not the separate /me fetch (useMe): SessionProvider
  // deliberately keeps the shell up with me === null when /me fails, which
  // made `me?.id !== u.id` read true for every row — including the caller's
  // own — exposing a self-target action that consumes a step-up password
  // confirmation only to 400 (codex review of #492 round 10).

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
      setStepUpPassword("");
      setFlockStepUpPassword("");
      // `emailStepUpPassword` is cleared by the change-email effect below,
      // which also ends that dialog's session.
    }
  }, [isAuthenticated]);

  // #163 edit: the user whose name is being edited, and the working value.
  const [editUser, setEditUser] = useState<User | null>(null);
  const [editName, setEditName] = useState("");

  // #165 password reset: kept in its own dialog rather than folded into the name
  // edit — setting someone's password is a different, higher-consequence action
  // and shouldn't be one stray keystroke away from a typo fix.
  const [pwUser, setPwUser] = useState<User | null>(null);
  const [pwValue, setPwValue] = useState("");
  const [pwConfirm, setPwConfirm] = useState("");

  // #355 — promote/demote an existing user's role, own dialog for the same
  // reason as password reset above: a higher-consequence action than a name
  // edit, not one stray keystroke away from a typo.
  const [roleUser, setRoleUser] = useState<User | null>(null);
  const [roleValue, setRoleValue] = useState("Worker");

  const [emailUser, setEmailUser] = useState<User | null>(null);
  const [emailValue, setEmailValue] = useState("");
  const [emailStepUpPassword, setEmailStepUpPassword] = useState("");
  const [emailFieldError, setEmailFieldError] = useState<string | null>(null);
  const emailHintId = useId();
  const emailErrorId = useId();

  // #356 — disable/enable a user, both behind ONE dialog that is itself the
  // confirmation: a destructive warning body, an OPTIONAL reason (disable
  // only — the API's DisableUserCommand.Reason is nullable, capped at 200
  // chars, and the product call was explicit that a mandatory reason just
  // gets typed "x"), and the step-up password DisableUser/EnableUser always
  // require (#308) regardless of the target's role. "mode" picks which
  // endpoint + copy applies.
  const [stepUpUser, setStepUpUser] = useState<User | null>(null);
  const [stepUpMode, setStepUpMode] = useState<"disable" | "enable" | null>(null);
  // #356 (codex review of #492 round 7) — wired into the Dialog below via
  // describedBy so a screen reader announces the destructive warning right
  // after the title, not just whatever field focus happens to land on first.
  const disableWarningId = useId();
  const [disableReason, setDisableReason] = useState("");
  const [stepUpPassword, setStepUpPassword] = useState("");

  // #103 flock scoping: expand a worker row to manage assignments.
  // #606 — assign/remove each require step-up, same as every other durable
  // user-access mutation on this page. One controlled password serves both
  // actions in this dialog; require re-entry for each action (cleared before
  // every await, same read-then-clear-before-issuance pattern as the other
  // dialogs) rather than caching a grant across assign/remove.
  const [openUser, setOpenUser] = useState<string | null>(null);
  const [assignments, setAssignments] = useState<FlockAssignment[]>([]);
  const [flockStepUpPassword, setFlockStepUpPassword] = useState("");
  // #609 review — narrower than `busy`: true only while the actual
  // assign/unassign request and its post-write refresh are in flight, NOT
  // during the earlier step-up issuance wait (closing/reopening THEN is
  // already safe — see the stale-continuation tests below, which close mid
  // step-up on purpose). Gates the dialog's own closability so a write in
  // this window cannot be orphaned by a close+reopen of the SAME worker.
  const [flockWriteInFlight, setFlockWriteInFlight] = useState(false);
  // #512 (T028/T037) — the assignment flock is committed through FlockPicker.
  // `assignFlock` is the page-controlled committed entity (a full typed flock,
  // so a retained archived identity is preserved EXACTLY); bumping
  // `assignFlockGen` is the FRESH transition every open issues (the mount-time
  // active default, or null for the account-wide blank) — a reopen must never
  // retain the previous open's exploration/selection. `assignFlockSnapshot.canSubmit`
  // gates BOTH the Assign button and onAssign itself (US2 write guard); the
  // picker is optional, so the blank (no assignments) is submittable.
  const [assignFlock, setAssignFlock] = useState<Flock | null>(null);
  const [assignFlockGen, setAssignFlockGen] = useState(0);
  const [assignFlockSnapshot, setAssignFlockSnapshot] = useState<PickerSnapshot<Flock>>({
    committed: null, selectionPhase: "uninitialized", exploring: false, canSubmit: false,
  });
  const [assignPickerOpen, setAssignPickerOpen] = useState(false);

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
    if (!isAuthenticated) {
      if (emailUser !== null) keys.current.delete(`change-email:${emailUser.id}`);
      setEmailStepUpPassword("");
      setEmailFieldError(null);
      setEmailUser(null);
      // Closed out from under the user by the screen itself — the same edge as
      // a dismissal (#703): the attempt still out is muted and its session
      // ends, so it can neither report into nor act on the next open.
      dismissDialog("change-email");
    }
  }, [isAuthenticated, emailUser, dismissDialog]);

  useEffect(() => {
    // #646 — this screen no longer lists flocks at all. The list existed only
    // to seed the assignment dialog's default; the assignment ROWS carry their
    // own scoped flockName from the API, and the picker does its own
    // eligibility-scoped discovery. One fewer request per open.
    listUsers()
      .then((u) => {
        setUsers(u);
        // #512 (T037) — the capture default is committed as a full typed entity
        // through the picker's controlled sync: the first ACTIVE flock, same
        // choice the old dropdown initialized from (an inactive first flock
        // would preselect an identity Assign refuses — conventions review of
        // #104). The picker's own discovery is eligibility-scoped, so the
        // display list here may be the full (incl. archived) one — display
        // only; the picker owns which identities are selectable.
        // #646 — NO default flock here, deliberately (owner decision,
        // 2026-09-05). This dialog grants a user scope over the flock it
        // names, and the default used to be "whichever active flock sorts
        // first on the capped page" — an arbitrary house that a distracted
        // admin can grant without ever choosing it. The picker admits a blank
        // (account-wide) and Assign arms only once something is chosen, so
        // making the admin pick costs one interaction and removes a silent
        // permission decision. The other two screens keep a default because
        // there a wrong guess is a mis-typed reading, not a grant.
        setAssignFlock(null);
        setAssignFlockGen((g) => g + 1);
      })
      .catch((err) => setPageError(errText(err)));
  }, [setPageError]);

  // F133: flock scoping is a per-worker action, so it opens in the shared dialog
  // like the other per-row surfaces (#131) — the row button opens it, the dialog
  // closes it. Load the assignments before opening so the panel is never empty
  // mid-flight; a load failure surfaces on the page and the dialog stays shut.
  async function openAssignments(userId: string) {
    // The latest click wins (#703): a new session for this dialog now, so a
    // load still out — or an attempt from the dialog on screen, a reopen of
    // the SAME worker included (#606) — is superseded. The slot is left alone
    // until the load lands: a load is not an attempt, and a load that fails
    // reports to the page and changes nothing else. The try covers the load
    // alone: once the dialog rebinds below, this load's `current()` is over.
    const current = startLoad("flock-access");
    let list: FlockAssignment[];
    try {
      list = await listFlockAssignments(userId);
    } catch (err) {
      if (!current()) return;
      errors.setPage(errText(err)); // load failed → no dialog; surface on the page
      return;
    }
    if (!current()) return; // superseded by another open/close
    setAssignments(list);
    // Start every worker's dialog on a FRESH controlled generation, never
    // retaining the previous open's exploration or selection — open A, pick
    // fl2, close, open B, and B would otherwise still show fl2, so a
    // distracted admin could assign the wrong flock. The default is the
    // first active flock when one is loaded; until the load resolves (or
    // the account has none) it is a fresh BLANK (account-wide) — the
    // optional picker admits the blank, so Assign is only ever armed once
    // a real default exists.
    // #646 — blank, for the reason above: a role grant should not carry a
    // flock the admin never picked.
    setAssignFlock(null);
    setAssignFlockGen((g) => g + 1);
    // The SLOT's edge (the mute and the clear that `openDialog` carries)
    // only once the load actually succeeds and the dialog is about to
    // rebind: a failed load opens nothing, so nothing is cleared for it — the
    // session itself began at `startLoad` above. (Another worker's row is
    // inert behind an open dialog since #480, so a displacement reaches here
    // only after a dismissal; the test `keeps worker A's dialog and its
    // message when worker B's load fails` drives the row directly.)
    // Rebind: this ends the displaced dialog's session and drops its verdict
    // — a same-worker re-entry included, exactly as every other dialog on
    // this screen (#703).
    openDialog("flock-access");
    setOpenUser(userId);
    setFlockStepUpPassword(""); // fresh re-entry per open, including same-worker reopen
  }

  function closeAssignments() {
    setOpenUser(null);
    setFlockStepUpPassword(""); // #308 — never leave a typed proof password behind
    // A closed dialog must not keep a picker armed for a write that no dialog
    // can present: the next open issues its own fresh generation (above), but
    // until then the engine would still report canSubmit for whatever the
    // last open committed.
    setAssignPickerOpen(false);
    setAssignFlock(null);
    setAssignFlockGen((g) => g + 1);
    dismissDialog("flock-access");
  }

  async function onAssign() {
    const target = openUser;
    const selectedFlock = assignFlock;
    // US2 (T028) — canSubmit is the write-safety boundary, not the button:
    // a disabled button alone is bypassable (a stale click, a suppressed
    // render), so the handler refuses an uncommitted/exploring/unavailable
    // selection itself.
    if (!target || !selectedFlock || !assignFlockSnapshot.canSubmit || busy) return;
    // One string serves as both the pending scope and the idempotency-key
    // scope here — payload-bound either way; the dialog is named for the
    // slot and the session (#703).
    const scope = `assign:${target}:${selectedFlock.id}`;
    await run(scope, async (current) => {
      // #606/#308 — read then clear before awaiting issuance; the grant is
      // spent on this write only, never cached across assign/remove.
      const enteredPassword = flockStepUpPassword;
      setFlockStepUpPassword("");
      const stepUpToken = (await stepUp(enteredPassword)).token;
      if (!current()) return;

      setFlockWriteInFlight(true);
      try {
        await apiAssignFlock(target, selectedFlock.id, keyFor(scope), stepUpToken);
        const fresh = await listFlockAssignments(target);
        clearKey(scope);
        if (current()) setAssignments(fresh);
      } finally {
        setFlockWriteInFlight(false);
      }
    }, { dialog: "flock-access" });
  }

  async function onUnassign(a: FlockAssignment) {
    const target = openUser;
    if (!target || busy) return;
    // The KEY scope stays bound to the assignment id (the exact write being
    // retried); the PENDING scope is user:flock so the row's spinner matches
    // what the admin sees themselves removing.
    const keyScope = `unassign:${a.id}`;
    await run(`unassign:${target}:${a.flockId}`, async (current) => {
      const enteredPassword = flockStepUpPassword;
      setFlockStepUpPassword("");
      const stepUpToken = (await stepUp(enteredPassword)).token;
      if (!current()) return;

      setFlockWriteInFlight(true);
      try {
        await unassignFlock(target, a.id, keyFor(keyScope), stepUpToken);
        const fresh = await listFlockAssignments(target);
        clearKey(keyScope);
        if (current()) setAssignments(fresh);
      } finally {
        setFlockWriteInFlight(false);
      }
    }, { dialog: "flock-access" });
  }

  // #512 US4 (T047/T051) — a retained assignment's name comes ONLY from the
  // ROW's OWN flockName (the scoped left join): a capped picker/catalog
  // list is never consulted for a row label (it can substitute the WRONG
  // flock, or simply not carry an archived/out-of-scope identity), and an id
  // is never shown as a fragment. `flockId === null` is the deliberate
  // farm-wide choice; a non-null `flockId` with a null `flockName` is the
  // defensive out-of-scope case (see contracts/http-api.md) — both get their
  // own translated label, never a raw id.
  const flockName = (a: { flockId: string | null; flockName: string | null }) =>
    a.flockId === null
      ? t("farmWideAssignmentLabel")
      : a.flockName ?? t("assignmentFlockUnavailable");

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    // The hook clears this dialog's slot, claims its session and reports a
    // failure into it (#703); `current()` says whether the session that
    // started this attempt is still the one on screen.
    await run("create", async (current) => {
      setMessage(null);
      // #308/#360 — every interactive creation establishes a durable login,
      // regardless of role. Read then clear the Owner's current password
      // before awaiting issuance; the grant is spent on this write only.
      const enteredPassword = createStepUpPassword;
      setCreateStepUpPassword("");
      const stepUpToken = (await stepUp(enteredPassword)).token;
      if (!current()) return;

      const scope = `create:${email.trim().toLowerCase()}`;
      await createUser(
        { email: email.trim(), password, role, name: name.trim() || undefined },
        keyFor(scope), stepUpToken);
      // Clear the key the instant the WRITE is confirmed — before the refresh —
      // so a later edit of the just-created user (a changed payload) can't replay
      // this cached response if the refresh below fails (#163 review).
      clearKey(scope);
      setUsers(await listUsers());
      // Superseded: the user exists and the list shows them, but the message
      // and the close belong to the session on screen now (#703).
      if (!current()) return;
      setMessage(i18n.t("users:createSuccessMessage", { role: roleLabel(role), email: email.trim() }));
      // #336 review — close through closeCreate() rather than repeating the
      // field resets here. The duplicated list had already drifted: it never
      // cleared createStepUpPassword, so a successful create could leave the
      // operator's own account password in state, visible on the next reopen.
      // One reset path means new dialog state can only be forgotten in one
      // place, not two — the #314 lesson, relearned.
      closeCreate();
    });
  }

  function openCreate() {
    setMessage(null);
    openDialog("create");
    setCreating(true);
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
    dismissDialog("create");
  }

  // #163 — open the edit dialog seeded with the user's current name.
  // Every open handler below ends the session on screen (#703), the same
  // user's included: each open reseeds its form, so an attempt still out
  // belongs to a session that is over — its failure lands nowhere and its
  // success cannot close or reset the form the user is looking at now — and
  // a displaced user's verdict is dropped rather than rendered under the new
  // user's email in the title. Everything behind the topmost dialog is inert
  // (`Dialog.tsx`, #480), so a displacement reaches an open handler only
  // after a dismissal; both edges end the session all the same.
  function openEdit(u: User) {
    openDialog("edit-user");
    setMessage(null);
    setEditName(u.displayName ?? "");
    setEditUser(u);
  }

  function closeEdit() {
    setEditUser(null);
    dismissDialog("edit-user");
  }

  // #165 — open/close the password dialog for a user.
  function openPassword(u: User) {
    openDialog("set-password");
    setMessage(null);
    setPwValue("");
    setPwConfirm("");
    setPwStepUpPassword("");
    setPwUser(u);
  }

  function closePassword() {
    // Don't leave the typed plaintext sitting in component state after the
    // dialog is gone (#165 review; #308 for the step-up field).
    setPwValue("");
    setPwConfirm("");
    setPwStepUpPassword("");
    setPwUser(null);
    dismissDialog("set-password");
  }

  async function onSetPassword(e: FormEvent) {
    e.preventDefault();
    const target = pwUser;
    // The mismatch check stays OUTSIDE the flight (it is validation, not
    // work), so it keeps the old busy guard alongside the hook's — and that
    // guard is load-bearing for the un-mute below: without it a second submit
    // while an abandoned attempt is still out would un-mute that attempt's
    // failure into this dialog (#703 PR 4 review).
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
    await run(`set-password:${target.id}`, async (current) => {
      // #308/#360 — every administrative reset replaces an authenticator,
      // regardless of target role. Read then clear before awaiting issuance.
      const enteredPassword = pwStepUpPassword;
      setPwStepUpPassword("");
      const stepUpToken = (await stepUp(enteredPassword)).token;
      if (!current()) return;

      await setUserPassword(target.id, { newPassword: pwValue }, keyFor(keyScope), stepUpToken);
      clearKey(keyScope); // write confirmed before any refresh (#163 review)
      if (!current()) return;
      setMessage(i18n.t("users:passwordSetMessage", { email: target.email }));
      closePassword();
    }, { dialog: "set-password" });
  }

  // #355 — open/close the role dialog for a user, seeded with their current role.
  function openRole(u: User) {
    openDialog("change-role");
    setMessage(null);
    setRoleValue(u.role);
    setRoleStepUpPassword("");
    setRoleUser(u);
  }

  function closeRole() {
    setRoleStepUpPassword(""); // #308 — never leave a typed proof password behind
    setRoleUser(null);
    dismissDialog("change-role");
  }

  async function onChangeRole(e: FormEvent) {
    e.preventDefault();
    const target = roleUser;
    if (!target || busy) return;
    setMessage(null);
    const keyScope = `role:${target.id}`;
    await run(`change-role:${target.id}`, async (current) => {
      // #355/#360 — every role mutation changes durable authorization. Read
      // then clear before awaiting issuance; the grant is spent once below.
      const enteredPassword = roleStepUpPassword;
      setRoleStepUpPassword("");
      const stepUpToken = (await stepUp(enteredPassword)).token;
      if (!current()) return;

      await changeUserRole(target.id, { role: roleValue }, keyFor(keyScope), stepUpToken);
      clearKey(keyScope); // write confirmed before any refresh (#163 review)
      setUsers(await listUsers());
      if (!current()) return;
      setMessage(i18n.t("users:roleChangedMessage", { email: target.email, role: roleLabel(roleValue) }));
      closeRole();
    }, { dialog: "change-role" });
  }

  function openEmail(u: User) {
    // A displaced user's key is spent with their session; the same user's is
    // kept, so a retry of an unchanged email replays rather than re-issues.
    if (emailUser !== null && emailUser.id !== u.id) clearKey(`change-email:${emailUser.id}`);
    openDialog("change-email");
    setMessage(null);
    setEmailValue(u.email);
    setEmailStepUpPassword("");
    setEmailFieldError(null);
    setEmailUser(u);
  }

  function closeEmail() {
    if (emailUser !== null) clearKey(`change-email:${emailUser.id}`);
    setEmailValue("");
    setEmailStepUpPassword("");
    setEmailFieldError(null);
    setEmailUser(null);
    dismissDialog("change-email");
  }

  async function onChangeEmail(e: FormEvent) {
    e.preventDefault();
    const target = emailUser;
    if (!target || busy) return;
    const targetId = target.id;
    const scope = `change-email:${targetId}`;
    setEmailFieldError(null);
    setMessage(null);
    await run(scope, async (current) => {
      // This dialog maps three outcomes itself, so it keeps its own catch and
      // gates every branch on `current()` — a superseded attempt lands nowhere,
      // exactly as the hook's own report would. The order below (gate before
      // the key clears and the list refreshes) is this dialog's own and is
      // kept as is (#703 PR 4, owner decision 2026-09-07).
      try {
        const password = emailStepUpPassword;
        setEmailStepUpPassword("");
        const grant = await stepUp(password);
        if (!current()) return;
        const trimmedEmail = emailValue.trim();
        await changeUserEmail(targetId, { email: trimmedEmail }, keyFor(scope), grant.token);
        if (!current()) return;
        clearKey(scope);
        const fresh = await listUsers();
        if (!current()) return;
        setUsers(fresh);
        setMessage(i18n.t("users:emailChangedMessage", { email: trimmedEmail }));
        closeEmail();
      } catch (err) {
        if (!current()) return;
        if (targetId === myId && err instanceof ApiError
          && err.status === 401 && err.title === "Auth.CredentialsSuperseded") {
          return;
        } else if (err instanceof ApiError && err.status === 409 && err.title === "Users.DuplicateEmail") {
          setEmailFieldError(i18n.t("users:duplicateEmailMessage"));
        } else if (err instanceof ApiError && err.title === "Users.LastOwner") {
          errors.report("change-email", i18n.t("users:lastOwnerEmailMessage"));
        } else {
          errors.report("change-email", errText(err));
        }
      }
    }, { dialog: "change-email" });
  }

  // #356 — open/close the shared disable/enable dialog. The Disable and Enable
  // row buttons call this identically; "mode" is the only difference. The
  // reason is not a parameter: the dialog collects it itself, seeded blank,
  // through a controlled textarea.
  function openStepUp(u: User, mode: "disable" | "enable") {
    // Every open ends the session on screen (#703) — a MODE change included:
    // both modes share one error scope (one dialog, one title swap), and the
    // row's Disable/Enable button flips with u.disabledAt, so a stale
    // "Cannot disable the sole remaining owner" from a failed disable attempt
    // must not still be showing when this reopens in enable mode for the
    // same user (local review of #492's merge-driven conversion to
    // useDialogErrors) — a message about the wrong operation entirely.
    openDialog("disable-enable");
    setMessage(null);
    setStepUpPassword("");
    setDisableReason("");
    setStepUpMode(mode);
    setStepUpUser(u);
  }

  function closeStepUp() {
    setStepUpPassword(""); // #308 — never leave a typed proof password behind
    setDisableReason("");
    setStepUpMode(null);
    setStepUpUser(null);
    dismissDialog("disable-enable");
  }

  async function onSubmitStepUp(e: FormEvent) {
    e.preventDefault();
    const target = stepUpUser;
    const mode = stepUpMode;
    if (!target || !mode || busy) return;
    setMessage(null);
    const scope = `${mode}:${target.id}`;
    await run(scope, async (current) => {
      // #308 — read-then-clear-before-await, same pattern as every other
      // step-up site on this screen: the proof password never sits in state
      // across the network call that consumes it.
      const enteredPassword = stepUpPassword;
      setStepUpPassword("");

      // #356 — grouped here with the password for readability, NOT because
      // reading it after the await would be a bug. An earlier revision of
      // this comment claimed exactly that ("defence in depth" against a
      // late read filing one dialog's reason against another user), and a
      // review probe disproved it: `disableReason` is a `const` this
      // closure captured at THIS render, not a live ref. Retyping the
      // textarea triggers a new render with a new `onSubmitStepUp` closure
      // over a new `disableReason` binding — it cannot reach back and
      // mutate the one this already-running invocation holds. So a read
      // before or after the await, within one invocation, is provably the
      // same value; unlike the password above, there is no state-exposure
      // reason to move it either, since a reason is not a secret.
      //
      // Reason is optional: empty or whitespace sends null, never "".
      const enteredReason = disableReason.trim() || null;
      const token = (await stepUp(enteredPassword)).token;
      // Dismissed while the grant was being issued: like every other step-up
      // write on this screen, the write is refused rather than made — the
      // grant is dropped unspent (#703 PR 4 review; pinned by `does not let a
      // dismissed disable continuation write`).
      if (!current()) return;

      if (mode === "disable") {
        await disableUser(target.id, { reason: enteredReason }, keyFor(scope), token);
      } else {
        await enableUser(target.id, keyFor(scope), token);
      }
      clearKey(scope); // write confirmed before any refresh (#163 review)
      setUsers(await listUsers());
      // Dismissed, or reopened for another user, the same user, or the other
      // MODE, while this was in flight. The old guard compared user ids, so a
      // same-user reopen — a disable followed by an enable of the same person
      // included — passed and this stale success closed the dialog the user
      // had just reopened (#703); the session generation tells them apart.
      // A mid-flight REOPEN is not reachable through the UI (both row
      // triggers are disabled for the whole flight — pinned by `keeps both
      // row triggers disabled…`); a mid-flight DISMISSAL is, pinned by
      // `supersedes the disable on dismissal alone…`; the close itself by
      // `a successful disable closes its dialog…`.
      if (!current()) return;
      setMessage(i18n.t(mode === "disable" ? "users:userDisabledMessage" : "users:userEnabledMessage",
        { email: target.email }));
      closeStepUp();
    }, { dialog: "disable-enable" });
  }

  async function onUpdate(e: FormEvent) {
    e.preventDefault();
    const target = editUser;
    if (!target) return;
    const scope = `update:${target.id}`;
    await run(scope, async (current) => {
      setMessage(null);
      // Blank clears the name back to "—" (null); the server normalizes too.
      await updateUser(target.id, { name: editName.trim() || null }, keyFor(scope));
      // Clear the key once the WRITE is confirmed (before the refresh), so a
      // follow-up edit isn't replayed against this cached response (#163 review).
      clearKey(scope);
      await listUsers().then(setUsers);
      // The dialog may have been dismissed, or reopened — for another user OR
      // the same one — while this was in flight. The old guard compared user
      // ids, so a same-user reopen passed and this stale success closed the
      // dialog the user had just reopened (#703); the session generation
      // tells the two apart.
      if (!current()) return;
      setMessage(i18n.t("users:updatedMessage", { email: target.email }));
      closeEdit();
    }, { dialog: "edit-user" });
  }

  // The list read failed and there is nothing to show: a fatal page state,
  // never a dialog's (no dialog can be open before the screen renders).
  if (errors.page && users === null) return <section><h2>{t("heading")}</h2><p className="error" role="alert">{errors.page}</p></section>;
  if (users === null) return <section><h2>{t("heading")}</h2><p className="muted">{tc("loading")}</p></section>;

  // #612 — only a plain Worker is ever narrowed by flock assignments; the
  // flock-access dialog reads this to disable adding a new one and to mark
  // any retained rows on an elevated user as inactive.
  const openUserIsWorker = users.find((u) => u.id === openUser)?.role === "Worker";

  return (
    <section>
      <div className="page-head">
        <h2>{t("heading")}</h2>
        <button type="button" onClick={openCreate}>
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
          <p className="muted">{t("stepUpCreateHint")}</p>
          <label>{t("stepUpFieldLabel")}
            <input type="password" value={createStepUpPassword} required maxLength={256}
              autoComplete="current-password"
              onChange={(e) => setCreateStepUpPassword(e.target.value)} />
          </label>
          <DialogError errors={errors} scope="create" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeCreate}>{tc("cancel")}</button>
            <BusyButton type="submit" disabled={busy} busy={isPending("create")}>{t("createUserButton")}</BusyButton>
          </div>
        </form>
      </Dialog>

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
            <th>{t("statusColumnHeader")}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {/* #356 — a disabled row renders muted (ProductsPage's active/inactive
              precedent), and offers Enable in place of Disable. Neither action
              appears on the caller's own row: the server 400s a self-target
              (Users.CannotDisableSelf/CannotEnableSelf), so presenting the
              button here would only ever fail. */}
          {users.map((u) => (
            <tr key={u.id} className={u.disabledAt ? "muted" : undefined}>
              <td>{u.email}</td>
              <td>{u.displayName ?? "—"}</td>
              <td>{roleLabel(u.role)}</td>
              <td>{u.disabledAt && <StatusBadge status="Inactive" label={t("disabledBadge")} />}</td>
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
                <button className="link" onClick={() => openEmail(u)}>
                  <Mail size={14} aria-hidden /> {t("changeEmailButton")}
                </button>
                {/* #612 — shown for every role, not just Worker: a promoted
                    user keeps their retained rows (inert, but still visible
                    and removable) even though a NEW assignment is Worker-only. */}
                <button className="link" onClick={() => void openAssignments(u.id)}>
                  {t("flocksButton")}
                </button>
                {myId !== u.id && (
                  u.disabledAt ? (
                    <button className="link" disabled={busy} onClick={() => openStepUp(u, "enable")}>
                      <RotateCcw size={14} aria-hidden /> {t("enableButton")}
                    </button>
                  ) : (
                    <button className="link" disabled={busy} onClick={() => openStepUp(u, "disable")}>
                      <Ban size={14} aria-hidden /> {t("disableButton")}
                    </button>
                  )
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
        // #609 review — an in-flight assign/unassign for the open worker must
        // finish before this dialog can be closed/reopened: escaping mid-write
        // is exactly the close/reopen race that leaves the reopened dialog
        // showing data the write's own completion can no longer reach (the
        // session generation — `current()` — correctly discards a write that
        // no longer belongs to the current dialog instance).
        closeDisabled={flockWriteInFlight}
      >
        <p className="muted">
          {t("flockAccessHint")}
        </p>
        {/* #612 — only a plain Worker is ever narrowed by these rows; a
            promoted user's retained rows are inert until (or unless) they
            are demoted back to Worker. */}
        {!openUserIsWorker && assignments.length > 0 && (
          <p className="hint">{t("retainedAssignmentsHint")}</p>
        )}
        {assignments.length === 0 ? (
          <p className="muted">{t("noAssignmentsMessage")}</p>
        ) : (
          <ul>
            {assignments.map((a) => (
              <li key={a.id}>
                {flockName(a)}
                {!openUserIsWorker && <span className="muted"> ({t("inactiveAssignmentLabel")})</span>}{" "}
                <BusyButton className="link" disabled={busy || !flockStepUpPassword}
                  busy={openUser !== null && isPending(`unassign:${openUser}:${a.flockId}`)}
                  onClick={() => void onUnassign(a)}>
                  {t("removeAssignmentButton")}
                </BusyButton>
              </li>
            ))}
          </ul>
        )}
        {/* #612 — a live assignment write is refused server-side for a
            non-Worker target (Users.FlockAssignmentsWorkerOnly); the add
            control is disabled here rather than letting the user discover
            that as a 422. Removal above stays available regardless. */}
        {openUserIsWorker ? (
          <div className="inline-form">
            {/* #512 (T028/T037) — the assignment flock commits through
                FlockPicker. Optional: the blank means "no assignments yet"
                (account-wide) and is submittable; a committed flock —
                possibly an archived retained identity, resolved through the
                picker's exact read — is the write payload. Disabled during
                any flight: the assign scope embeds the selected flock id, so
                changing the selection mid-flight would re-point isPending at
                a scope nobody is running and drop the spinner while the
                request is still open (#242 review). */}
            <FlockPicker
              label={t("flockLabel")}
              eligibility="active"
              required={false}
              disabled={busy}
              open={assignPickerOpen}
              controlledCommitted={assignFlock}
              controlledGeneration={assignFlockGen}
              onSnapshot={setAssignFlockSnapshot}
              onCommit={(f) => {
                setAssignFlock(f);
                setAssignFlockGen((g) => g + 1);
                setAssignPickerOpen(false);
              }}
              onClear={() => {
                setAssignFlock(null);
                setAssignFlockGen((g) => g + 1);
              }}
              onEscape={() => setAssignPickerOpen(false)}
              onOutsideClick={() => setAssignPickerOpen(false)}
              trigger={
                <button type="button" className="named-picker-trigger"
                  disabled={busy}
                  onClick={() => setAssignPickerOpen(true)}>
                  {assignFlock ? assignFlock.name : t("selectFlockOption")}
                </button>
              }
            />
            <BusyButton disabled={busy || !assignFlock || !assignFlockSnapshot.canSubmit || !flockStepUpPassword}
              busy={openUser !== null && isPending(`assign:${openUser}:${assignFlock?.id ?? ""}`)}
              onClick={() => void onAssign()}>
              {t("assignFlockButton")}
            </BusyButton>
          </div>
        ) : (
          <p className="hint">{t("assignmentsWorkerOnlyHint")}</p>
        )}
        <p className="muted">{t("stepUpFlockHint")}</p>
        <label>{t("stepUpFieldLabel")}
          <input type="password" value={flockStepUpPassword} required maxLength={256}
            autoComplete="current-password"
            onChange={(e) => setFlockStepUpPassword(e.target.value)} />
        </label>
        <DialogError errors={errors} scope="flock-access" />
        <div className="dialog-foot">
          <button type="button" className="link" disabled={flockWriteInFlight} onClick={closeAssignments}>{t("doneButton")}</button>
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
          <p className="muted">{t("stepUpResetHint")}</p>
          <label>{t("stepUpFieldLabel")}
            <input type="password" value={pwStepUpPassword} required maxLength={256}
              autoComplete="current-password"
              onChange={(e) => setPwStepUpPassword(e.target.value)} />
          </label>
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
          <p className="muted">{t("stepUpRoleHint")}</p>
          <label>{t("stepUpFieldLabel")}
            <input type="password" value={roleStepUpPassword} required maxLength={256}
              autoComplete="current-password"
              onChange={(e) => setRoleStepUpPassword(e.target.value)} />
          </label>
          <DialogError errors={errors} scope="change-role" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeRole}>{tc("cancel")}</button>
            <BusyButton type="submit" disabled={busy}
              busy={roleUser !== null && isPending(`change-role:${roleUser.id}`)}>{t("changeRoleSubmitButton")}</BusyButton>
          </div>
        </form>
      </Dialog>

      <Dialog
        open={emailUser !== null}
        title={t("changeEmailTitle", { email: emailUser?.email ?? "" })}
        onClose={closeEmail}
      >
        <form className="inline-form" onSubmit={onChangeEmail}>
          <p className="muted" id={emailHintId}>{t("changeEmailHint")}</p>
          <label>{t("loginEmailFieldLabel")}
            <input type="email" value={emailValue} required maxLength={256}
              autoComplete="off"
              disabled={emailUser !== null && isPending(`change-email:${emailUser.id}`)}
              aria-invalid={emailFieldError !== null}
              aria-describedby={emailFieldError ? emailErrorId : emailHintId}
              onChange={(e) => {
                setEmailValue(e.target.value);
                setEmailFieldError(null);
                if (emailUser !== null) clearKey(`change-email:${emailUser.id}`);
              }} />
          </label>
          {emailFieldError && (
            <p className="error" role="alert" id={emailErrorId}>{emailFieldError}</p>
          )}
          <p className="muted">{t("stepUpEmailHint")}</p>
          <label>{t("stepUpFieldLabel")}
            <input type="password" value={emailStepUpPassword} required maxLength={256}
              autoComplete="current-password"
              disabled={emailUser !== null && isPending(`change-email:${emailUser.id}`)}
              onChange={(e) => setEmailStepUpPassword(e.target.value)} />
          </label>
          <DialogError errors={errors} scope="change-email" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeEmail}>{tc("cancel")}</button>
            <BusyButton type="submit" disabled={busy}
              busy={emailUser !== null && isPending(`change-email:${emailUser.id}`)}>
              {t("changeEmailSubmitButton")}
            </BusyButton>
          </div>
        </form>
      </Dialog>

      {/* #356 — one dialog is the whole disable/enable flow: it IS the
          confirmation (no separate askReason step), collects the optional
          reason (disable only — the API's reason is nullable, never
          mandatory), and always collects the step-up password
          UNCONDITIONALLY (#308) regardless of the target's role. */}
      <Dialog
        open={stepUpUser !== null}
        title={stepUpMode === "disable"
          ? t("disableStepUpTitle", { email: stepUpUser?.email ?? "" })
          : t("enableStepUpTitle", { email: stepUpUser?.email ?? "" })}
        onClose={closeStepUp}
        describedBy={stepUpMode === "disable" ? disableWarningId : undefined}
      >
        <form className="inline-form" onSubmit={onSubmitStepUp}>
          {stepUpMode === "disable" && (
            <>
              <p id={disableWarningId} className="confirm-body">{t("disableWarningBody")}</p>
              <label>{t("disableReasonFieldLabel")}
                <textarea value={disableReason} maxLength={200} rows={3}
                  onChange={(e) => setDisableReason(e.target.value)} />
              </label>
            </>
          )}
          <p className="muted">
            {stepUpMode === "disable" ? t("stepUpDisableHint") : t("stepUpEnableHint")}
          </p>
          <label>{t("stepUpFieldLabel")}
            <input type="password" value={stepUpPassword} required maxLength={256}
              autoComplete="current-password"
              onChange={(e) => setStepUpPassword(e.target.value)} />
          </label>
          <DialogError errors={errors} scope="disable-enable" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeStepUp}>{tc("cancel")}</button>
            <BusyButton type="submit" disabled={busy}
              className={stepUpMode === "disable" ? "btn-danger" : undefined}
              busy={stepUpUser !== null && stepUpMode !== null
                && isPending(`${stepUpMode}:${stepUpUser.id}`)}>
              {stepUpMode === "disable" ? t("disableSubmitButton") : t("enableSubmitButton")}
            </BusyButton>
          </div>
        </form>
      </Dialog>
    </section>
  );
}
