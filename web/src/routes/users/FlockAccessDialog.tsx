import type { Dispatch, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { DialogActions, Stack } from "@mui/material";
import type { Flock, FlockAssignment, User } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { CONSOLE_LINK_SX } from "../../components/FieldConsole";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import { FlockPicker } from "../../components/FlockPicker";
import { StepUpPasswordField } from "../../components/StepUpPasswordField";
import type { PickerSnapshot } from "../../components/NamedEntityPicker";
import type { DialogErrors } from "../../components/useDialogErrors";

// The flock-access dialog (#103/#606/#612), moved out of UsersPage (#1215).
// It renders at the page's old position, unconditionally; all state and
// handlers stay on the page and arrive as props, including the controlled
// picker state and the shared proof password.
export function FlockAccessDialog({
  openUser,
  users,
  assignments,
  closeAssignments,
  flockWriteInFlight,
  onAssign,
  onUnassign,
  busy,
  isPending,
  errors,
  assignFlock,
  setAssignFlock,
  assignFlockGen,
  setAssignFlockGen,
  assignFlockSnapshot,
  setAssignFlockSnapshot,
  assignPickerOpen,
  setAssignPickerOpen,
  flockStepUpPassword,
  setFlockStepUpPassword,
}: {
  openUser: string | null;
  users: User[];
  assignments: FlockAssignment[];
  closeAssignments: () => void;
  flockWriteInFlight: boolean;
  onAssign: () => Promise<void>;
  onUnassign: (a: FlockAssignment) => Promise<void>;
  busy: boolean;
  isPending: (scope: string) => boolean;
  errors: DialogErrors;
  assignFlock: Flock | null;
  setAssignFlock: Dispatch<SetStateAction<Flock | null>>;
  assignFlockGen: number;
  setAssignFlockGen: Dispatch<SetStateAction<number>>;
  assignFlockSnapshot: PickerSnapshot<Flock>;
  setAssignFlockSnapshot: Dispatch<SetStateAction<PickerSnapshot<Flock>>>;
  assignPickerOpen: boolean;
  setAssignPickerOpen: Dispatch<SetStateAction<boolean>>;
  flockStepUpPassword: string;
  setFlockStepUpPassword: Dispatch<SetStateAction<string>>;
}) {
  const { t } = useTranslation("users");

  // #612 — only a plain Worker is ever narrowed by flock assignments; the
  // flock-access dialog reads this to disable adding a new one and to mark
  // any retained rows on an elevated user as inactive.
  const openUserIsWorker = users.find((u) => u.id === openUser)?.role === "Worker";

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


  return (
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
      actions={(
        <DialogActions>
          <button type="button" className="link" disabled={flockWriteInFlight} onClick={closeAssignments}>{t("doneButton")}</button>
        </DialogActions>
      )}
    >
      <Stack spacing={2}>
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
                <BusyButton variant="text" sx={CONSOLE_LINK_SX} disabled={busy || !flockStepUpPassword}
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
          <Stack direction="row" spacing={1} sx={{ alignItems: "center", flexWrap: "wrap" }}>
            {/* #512 (T028/T037) — the assignment flock commits through
                FlockPicker; its own call and props are untouched (#826
                owns the picker's internals). Optional: the blank means "no
                assignments yet" (account-wide) and is submittable; a
                committed flock — possibly an archived retained identity,
                resolved through the picker's exact read — is the write
                payload. Disabled during any flight: the assign scope
                embeds the selected flock id, so changing the selection
                mid-flight would re-point isPending at a scope nobody is
                running and drop the spinner while the request is still
                open (#242 review). */}
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
            <BusyButton variant="contained" disabled={busy || !assignFlock || !assignFlockSnapshot.canSubmit || !flockStepUpPassword}
              busy={openUser !== null && isPending(`assign:${openUser}:${assignFlock?.id ?? ""}`)}
              onClick={() => void onAssign()}>
              {t("assignFlockButton")}
            </BusyButton>
          </Stack>
        ) : (
          <p className="hint">{t("assignmentsWorkerOnlyHint")}</p>
        )}
        <p className="muted">{t("stepUpFlockHint")}</p>
        <StepUpPasswordField
          label={t("stepUpFieldLabel")}
          value={flockStepUpPassword}
          onChange={setFlockStepUpPassword}
        />
        <DialogError errors={errors} scope="flock-access" />
      </Stack>
    </Dialog>
  );
}
