import { Fragment, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Ban, KeyRound, Mail, Pencil, Plus, RotateCcw, ShieldCheck } from "lucide-react";
import { Stack, Table, TableBody, TableCell, TableHead, TableRow } from "@mui/material";
import { listUsers } from "../api/cluckwork";
import type { Flock, User } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { BusyButton } from "../components/BusyButton";
import {
  CONSOLE_DESTRUCTIVE_LINK_SX, LedgerTableContainer, ListInspectorPane, RecordInspector,
  STICKY_TABLE_HEAD_SX, selectableRowProps, useClampSelection,
} from "../components/FieldConsole";
import type { PickerSnapshot } from "../components/NamedEntityPicker";
import { StatusBadge } from "../components/StatusBadge";
import { useDialogAction } from "../components/useDialogAction";
import { newId } from "../lib/ids";
import { roleLabel, statusLabel } from "../i18n/enums";
import { useAuth } from "../auth/useAuth";
import { ChangeEmailDialog } from "./users/ChangeEmailDialog";
import { ChangeRoleDialog } from "./users/ChangeRoleDialog";
import { CreateUserDialog } from "./users/CreateUserDialog";
import { DisableEnableDialog } from "./users/DisableEnableDialog";
import { EditUserDialog } from "./users/EditUserDialog";
import { FlockAccessDialog } from "./users/FlockAccessDialog";
import { SetPasswordDialog } from "./users/SetPasswordDialog";
import { useChangeEmail } from "./users/useChangeEmail";
import { useChangeRole } from "./users/useChangeRole";
import { useCreateUser } from "./users/useCreateUser";
import { useDisableEnable } from "./users/useDisableEnable";
import { useEditUser } from "./users/useEditUser";
import { useFlockAccess } from "./users/useFlockAccess";
import { useSetPassword } from "./users/useSetPassword";

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

// MUI's auto table layout shrinks any wrappable cell below its content width,
// so short values (names, numbers, chips, actions) are pinned; free text wraps.
const NOWRAP = { whiteSpace: "nowrap" as const };

// #73 — minimal user management: create a worker (or another admin) and see
// who exists. The full user-administration UI belongs to the RBAC slice.
export function UsersPage() {
  const { t } = useTranslation("users");
  const { t: tc } = useTranslation("common");

  const [users, setUsers] = useState<User[] | null>(null);
  // #908 — the bottom inspector's selection.
  const [selectedId, setSelectedId] = useState<string | null>(null);
  // #908 — clears the selection if the row it names ever leaves the roster
  // (this screen never removes a user today, but the same shape is wired
  // uniformly across all five setup lists).
  useClampSelection(users?.map((u) => u.id) ?? [], selectedId, setSelectedId);

  // #703 — the flight guard (#236), the per-place message slots (#479: the
  // page, and each dialog by its own name) and the dialog-session generation
  // (#477 part 2) come from one shared hook. Pending scopes stay per record —
  // composite where the action is payload-bound (assign/unassign) — so
  // `isPending(scope)` spins only the clicked control; each write names its
  // dialog. Idempotency-key scopes are separate from all of these.

  const dialog = useDialogAction(DIALOG_SCOPES);
  const { busy, isPending, errors, dismissDialog } = dialog;
  const setPageError = errors.setPage;
  const [message, setMessage] = useState<string | null>(null);

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

  const [emailUser, setEmailUser] = useState<User | null>(null);
  const [emailValue, setEmailValue] = useState("");
  const [emailStepUpPassword, setEmailStepUpPassword] = useState("");
  const [emailFieldError, setEmailFieldError] = useState<string | null>(null);

  const [stepUpPassword, setStepUpPassword] = useState("");

  // #103 flock scoping: expand a worker row to manage assignments.
  // #606 — assign/remove each require step-up, same as every other durable
  // user-access mutation on this page. One controlled password serves both
  // actions in this dialog; require re-entry for each action (cleared before
  // every await, same read-then-clear-before-issuance pattern as the other
  // dialogs) rather than caching a grant across assign/remove.

  const [flockStepUpPassword, setFlockStepUpPassword] = useState("");

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



  // One hook per dialog feature (#1215): each owns its dialog's state and
  // handlers. The flight guard, the message slots and the idempotency keys
  // stay here (#703), as do the proof passwords and the picker state the
  // effects above clear and seed (#308).
  const create = useCreateUser({ dialog, setUsers, keyFor, clearKey, setMessage, createStepUpPassword, setCreateStepUpPassword });
  const { openCreate } = create;
  const edit = useEditUser({ dialog, setUsers, keyFor, clearKey, setMessage });
  const { openEdit } = edit;
  const password = useSetPassword({ dialog, keyFor, clearKey, setMessage, pwStepUpPassword, setPwStepUpPassword });
  const { openPassword } = password;
  const role = useChangeRole({ dialog, setUsers, keyFor, clearKey, setMessage, roleStepUpPassword, setRoleStepUpPassword });
  const { openRole } = role;
  const email = useChangeEmail({
    dialog, setUsers, keyFor, clearKey, setMessage, myId,
    emailUser, setEmailUser, emailValue, setEmailValue, emailStepUpPassword, setEmailStepUpPassword, setEmailFieldError,
  });
  const { openEmail } = email;
  const disable = useDisableEnable({ dialog, setUsers, keyFor, clearKey, setMessage, stepUpPassword, setStepUpPassword });
  const { openStepUp } = disable;
  const flock = useFlockAccess({
    dialog, keyFor, clearKey,
    assignFlock, setAssignFlock, setAssignFlockGen, assignFlockSnapshot, setAssignPickerOpen,
    flockStepUpPassword, setFlockStepUpPassword,
  });
  const { openAssignments } = flock;

  // The list read failed and there is nothing to show: a fatal page state,
  // never a dialog's (no dialog can be open before the screen renders).
  if (errors.page && users === null) return <section><h2>{t("heading")}</h2><p className="error" role="alert">{errors.page}</p></section>;
  if (users === null) return <section><h2>{t("heading")}</h2><p className="muted">{tc("loading")}</p></section>;

  // #908 — the bottom inspector's selection.
  const selectedUser = users.find((u) => u.id === selectedId) ?? null;

  function renderActions(u: User, location: "row" | "inspector") {
    return {
      primary: <button className="link" onClick={() => openEdit(u)}>
        <Pencil size={14} aria-hidden /> {t("editButton")}
      </button>,
      secondary: <>
        <button className="link" onClick={() => openRole(u)}>
          <ShieldCheck size={14} aria-hidden /> {t("changeRoleButton")}
        </button>
        {location === "inspector" && <>
          <button className="link" onClick={() => openPassword(u)}>
            <KeyRound size={14} aria-hidden /> {t("resetPasswordButton")}
          </button>
          <button className="link" onClick={() => openEmail(u)}>
            <Mail size={14} aria-hidden /> {t("changeEmailButton")}
          </button>
          {/* #612 — promoted users retain assignments that can still be removed. */}
          <button className="link" onClick={() => void openAssignments(u.id)}>
            {t("flocksButton")}
          </button>
          {myId !== u.id && u.disabledAt && (
            <button className="link" disabled={busy} onClick={() => openStepUp(u, "enable")}>
              <RotateCcw size={14} aria-hidden /> {t("enableButton")}
            </button>
          )}
        </>}
      </>,
      destructive: location === "inspector" && myId !== u.id && !u.disabledAt && (
        <BusyButton variant="text" sx={CONSOLE_DESTRUCTIVE_LINK_SX} disabled={busy} onClick={() => openStepUp(u, "disable")}>
          <Ban size={14} aria-hidden /> {t("disableButton")}
        </BusyButton>
      ),
    };
  }

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



      <CreateUserDialog {...create} {...{ busy, isPending, errors, createStepUpPassword, setCreateStepUpPassword }} />

      {/* Unconditional since #479. The five-way guard this replaces existed
          because every dialog rendered the same string, so the page had to
          suppress itself whenever any of them was up. Each dialog now reads
          a slot of its own and there is nothing here to double up on. */}
      {errors.page && <p className="error" role="alert">{errors.page}</p>}
      {message && <p className="success">{message}</p>}

      <ListInspectorPane
        tableLabel={t("heading")}
        onClearSelection={() => setSelectedId(null)}
        table={(
          <LedgerTableContainer scrollHint="columnsAndRows">
            <Table size="small" sx={{
              "& .MuiTableCell-root": { pr: { md: 1.5, lg: 2 } },
              "& :is(th, td):last-child": { display: { xs: "none", md: "table-cell" } },
            }}>
              <TableHead>
                <TableRow>
                  <TableCell sx={STICKY_TABLE_HEAD_SX}>{t("emailColumnHeader")}</TableCell>
                  <TableCell sx={STICKY_TABLE_HEAD_SX}>{t("nameColumnHeader")}</TableCell>
                  <TableCell sx={STICKY_TABLE_HEAD_SX}>{t("roleColumnHeader")}</TableCell>
                  <TableCell sx={STICKY_TABLE_HEAD_SX}>{t("statusColumnHeader")}</TableCell>
                  <TableCell sx={STICKY_TABLE_HEAD_SX}>{tc("actions")}</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {/* #356 — a disabled row renders muted (ProductsPage's active/inactive
                    precedent), and offers Enable in place of Disable. Neither action
                    appears on the caller's own row: the server 400s a self-target
                    (Users.CannotDisableSelf/CannotEnableSelf), so presenting the
                    button here would only ever fail. */}
                {users.map((u) => (
                  <TableRow key={u.id} className={u.disabledAt ? "muted" : undefined}
                    {...selectableRowProps(u.id === selectedId, () => setSelectedId(u.id))}>
                    <TableCell sx={NOWRAP}>{u.email}</TableCell>
                    <TableCell sx={NOWRAP}>{u.displayName ?? "—"}</TableCell>
                    <TableCell sx={NOWRAP}>{roleLabel(u.role)}</TableCell>
                    <TableCell sx={NOWRAP}>
                      {u.disabledAt
                        ? <StatusBadge status="Inactive" label={t("disabledBadge")} />
                        : <StatusBadge status="Active" label={statusLabel("Active")} />}
                    </TableCell>
                    <TableCell sx={NOWRAP}>
                      <Stack direction="row" spacing={1} sx={{ flexWrap: "nowrap", alignItems: "center" }}>
                        {Object.entries(renderActions(u, "row")).map(([key, action]) => <Fragment key={key}>{action}</Fragment>)}
                      </Stack>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </LedgerTableContainer>
        )}
        inspector={(
          <RecordInspector
            ariaLabel={tc("inspectorLabel", { entity: t("entitySingular") })}
            title={selectedUser?.email}
            emptyMessage={tc("inspectorEmptyPrompt")}
            fields={selectedUser ? [
              { label: t("roleColumnHeader"), value: roleLabel(selectedUser.role) },
              { label: t("nameColumnHeader"), value: selectedUser.displayName ?? "—" },
              {
                label: t("statusColumnHeader"),
                value: selectedUser.disabledAt
                  ? <StatusBadge status="Inactive" label={t("disabledBadge")} />
                  : <StatusBadge status="Active" label={statusLabel("Active")} />,
              },
            ] : undefined}
            actions={selectedUser ? renderActions(selectedUser, "inspector") : undefined}
          />
        )}
      />



      <FlockAccessDialog {...flock} {...{ users, busy, isPending, errors, assignFlock, setAssignFlock, assignFlockGen, setAssignFlockGen, assignFlockSnapshot, setAssignFlockSnapshot, assignPickerOpen, setAssignPickerOpen, flockStepUpPassword, setFlockStepUpPassword }} />

      <EditUserDialog {...edit} {...{ busy, isPending, errors }} />

      <SetPasswordDialog {...password} {...{ busy, isPending, errors, pwStepUpPassword, setPwStepUpPassword }} />

      <ChangeRoleDialog {...role} {...{ busy, isPending, errors, roleStepUpPassword, setRoleStepUpPassword }} />

      <ChangeEmailDialog {...email} {...{ emailUser, busy, isPending, errors, clearKey, emailValue, setEmailValue, emailFieldError, setEmailFieldError, emailStepUpPassword, setEmailStepUpPassword }} />

      <DisableEnableDialog {...disable} {...{ busy, isPending, errors, stepUpPassword, setStepUpPassword }} />
    </section>
  );
}
