import type { Dispatch, FormEvent, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { DialogActions, Stack, TextField } from "@mui/material";
import type { User } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import { StepUpPasswordField } from "../../components/StepUpPasswordField";
import type { DialogErrors } from "../../components/useDialogErrors";

// The password-reset dialog (#165), moved out of UsersPage (#1215). It
// renders at the page's old position, unconditionally; all state and
// handlers stay on the page and arrive as props.
export function SetPasswordDialog({
  pwUser,
  closePassword,
  onSetPassword,
  busy,
  isPending,
  errors,
  pwValue,
  setPwValue,
  pwConfirm,
  setPwConfirm,
  pwStepUpPassword,
  setPwStepUpPassword,
}: {
  pwUser: User | null;
  closePassword: () => void;
  onSetPassword: (e: FormEvent) => void;
  busy: boolean;
  isPending: (scope: string) => boolean;
  errors: DialogErrors;
  pwValue: string;
  setPwValue: Dispatch<SetStateAction<string>>;
  pwConfirm: string;
  setPwConfirm: Dispatch<SetStateAction<string>>;
  pwStepUpPassword: string;
  setPwStepUpPassword: Dispatch<SetStateAction<string>>;
}) {
  const { t } = useTranslation("users");
  const { t: tc } = useTranslation("common");
  return (
    <Dialog
      open={pwUser !== null}
      title={t("setPasswordTitle", { email: pwUser?.email ?? "" })}
      onClose={closePassword}
      actions={(
        <DialogActions>
          <button type="button" className="link" onClick={closePassword}>{tc("cancel")}</button>
          <BusyButton variant="contained" type="submit" disabled={busy}
            busy={pwUser !== null && isPending(`set-password:${pwUser.id}`)}>{t("setPasswordButton")}</BusyButton>
        </DialogActions>
      )}
      formProps={{ onSubmit: onSetPassword }}
    >
      <Stack spacing={2}>
        <p className="muted">
          {t("passwordDialogHint")}
        </p>
        <TextField
          label={t("newPasswordFieldLabel")}
          type="password"
          value={pwValue}
          slotProps={{ htmlInput: { required: true, minLength: 12, maxLength: 256, autoComplete: "new-password" } }}
          onChange={(e) => setPwValue(e.target.value)}
        />
        <TextField
          label={t("confirmPasswordFieldLabel")}
          type="password"
          value={pwConfirm}
          slotProps={{ htmlInput: { required: true, maxLength: 256, autoComplete: "new-password" } }}
          onChange={(e) => setPwConfirm(e.target.value)}
        />
        <p className="muted">{t("stepUpResetHint")}</p>
        <StepUpPasswordField
          label={t("stepUpFieldLabel")}
          value={pwStepUpPassword}
          onChange={setPwStepUpPassword}
        />
        <DialogError errors={errors} scope="set-password" />
      </Stack>
    </Dialog>
  );
}
