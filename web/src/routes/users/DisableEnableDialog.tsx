import { useId } from "react";
import type { Dispatch, FormEvent, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { DialogActions, Stack, TextField, Typography } from "@mui/material";
import type { User } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import { StepUpPasswordField } from "../../components/StepUpPasswordField";
import type { DialogErrors } from "../../components/useDialogErrors";

// The shared disable/enable dialog (#356), moved out of UsersPage (#1215).
// It renders at the page's old position, unconditionally; all state and
// handlers stay on the page and arrive as props, and the destructive
// warning's described-by id is this component's own (the linkage is
// unchanged).
export function DisableEnableDialog({
  stepUpUser,
  stepUpMode,
  closeStepUp,
  onSubmitStepUp,
  busy,
  isPending,
  errors,
  disableReason,
  setDisableReason,
  stepUpPassword,
  setStepUpPassword,
}: {
  stepUpUser: User | null;
  stepUpMode: "disable" | "enable" | null;
  closeStepUp: () => void;
  onSubmitStepUp: (e: FormEvent) => void;
  busy: boolean;
  isPending: (scope: string) => boolean;
  errors: DialogErrors;
  disableReason: string;
  setDisableReason: Dispatch<SetStateAction<string>>;
  stepUpPassword: string;
  setStepUpPassword: Dispatch<SetStateAction<string>>;
}) {
  const disableWarningId = useId();
  const { t } = useTranslation("users");
  const { t: tc } = useTranslation("common");
  return (
    /* #356 — one dialog is the whole disable/enable flow: it IS the
        confirmation (no separate askReason step), collects the optional
        reason (disable only — the API's reason is nullable, never
        mandatory), and always collects the step-up password
        UNCONDITIONALLY (#308) regardless of the target's role. */
    <Dialog
      open={stepUpUser !== null}
      title={stepUpMode === "disable"
        ? t("disableStepUpTitle", { email: stepUpUser?.email ?? "" })
        : t("enableStepUpTitle", { email: stepUpUser?.email ?? "" })}
      onClose={closeStepUp}
      describedBy={stepUpMode === "disable" ? disableWarningId : undefined}
      actions={(
        <DialogActions>
          <button type="button" className="link" onClick={closeStepUp}>{tc("cancel")}</button>
          <BusyButton variant="contained" type="submit" disabled={busy}
            color={stepUpMode === "disable" ? "error" : undefined}
            busy={stepUpUser !== null && stepUpMode !== null
              && isPending(`${stepUpMode}:${stepUpUser.id}`)}>
            {stepUpMode === "disable" ? t("disableSubmitButton") : t("enableSubmitButton")}
          </BusyButton>
        </DialogActions>
      )}
      formProps={{ onSubmit: onSubmitStepUp }}
    >
      <Stack spacing={2}>
        {stepUpMode === "disable" && (
          <>
            {/* #832 — `.dialog .confirm-body` was this app's one CSS rule
                for confirmation prose; useConfirm.tsx dropped it for MUI's
                own DialogContentText treatment in #827, but this raw <p>
                never got swept along because it lives on a screen, not in
                the shared hook. A Typography with the same reading-leading
                treatment (`--muted`) replaces it, and the class (and its
                now-dead CSS rule) go in this PR — confirmed as the class's
                only consumer left in the whole repo (git grep). */}
            <Typography id={disableWarningId} color="text.secondary" sx={{ lineHeight: 1.55 }}>
              {t("disableWarningBody")}
            </Typography>
            <TextField
              label={t("disableReasonFieldLabel")}
              value={disableReason}
              multiline
              rows={3}
              slotProps={{ htmlInput: { maxLength: 200 } }}
              onChange={(e) => setDisableReason(e.target.value)}
            />
          </>
        )}
        <p className="muted">
          {stepUpMode === "disable" ? t("stepUpDisableHint") : t("stepUpEnableHint")}
        </p>
        <StepUpPasswordField
          label={t("stepUpFieldLabel")}
          value={stepUpPassword}
          onChange={setStepUpPassword}
        />
        <DialogError errors={errors} scope="disable-enable" />
      </Stack>
    </Dialog>
  );
}
