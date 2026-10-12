import { useId } from "react";
import type { Dispatch, FormEvent, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { DialogActions, Stack, TextField } from "@mui/material";
import type { User } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import { StepUpPasswordField } from "../../components/StepUpPasswordField";
import type { DialogErrors } from "../../components/useDialogErrors";

// The change-email dialog (#357), moved out of UsersPage (#1215). It renders
// at the page's old position, unconditionally; all state and handlers stay
// on the page and arrive as props, and the described-by ids are this
// component's own (their values are invisible to the user; the linkage is
// unchanged).
export function ChangeEmailDialog({
  emailUser,
  closeEmail,
  onChangeEmail,
  busy,
  isPending,
  errors,
  clearKey,
  emailValue,
  setEmailValue,
  emailFieldError,
  setEmailFieldError,
  emailStepUpPassword,
  setEmailStepUpPassword,
}: {
  emailUser: User | null;
  closeEmail: () => void;
  onChangeEmail: (e: FormEvent) => void;
  busy: boolean;
  isPending: (scope: string) => boolean;
  errors: DialogErrors;
  clearKey: (scope: string) => void;
  emailValue: string;
  setEmailValue: Dispatch<SetStateAction<string>>;
  emailFieldError: string | null;
  setEmailFieldError: Dispatch<SetStateAction<string | null>>;
  emailStepUpPassword: string;
  setEmailStepUpPassword: Dispatch<SetStateAction<string>>;
}) {
  const emailHintId = useId();
  const emailErrorId = useId();
  const { t } = useTranslation("users");
  const { t: tc } = useTranslation("common");
  return (
    <Dialog
      open={emailUser !== null}
      title={t("changeEmailTitle", { email: emailUser?.email ?? "" })}
      onClose={closeEmail}
      actions={(
        <DialogActions>
          <button type="button" className="link" onClick={closeEmail}>{tc("cancel")}</button>
          <BusyButton variant="contained" type="submit" disabled={busy}
            busy={emailUser !== null && isPending(`change-email:${emailUser.id}`)}>
            {t("changeEmailSubmitButton")}
          </BusyButton>
        </DialogActions>
      )}
      formProps={{ onSubmit: onChangeEmail }}
    >
      <Stack spacing={2}>
        <p className="muted" id={emailHintId}>{t("changeEmailHint")}</p>
        <TextField
          label={t("loginEmailFieldLabel")}
          type="email"
          value={emailValue}
          disabled={emailUser !== null && isPending(`change-email:${emailUser.id}`)}
          error={emailFieldError !== null}
          helperText={emailFieldError ?? undefined}
          slotProps={{
            // Same field carries both ids the raw markup did: the hint
            // stays the description at rest, and the error paragraph takes
            // over once one exists — `role="alert"` on it is what #479's
            // pattern requires (`DialogError` carries the same contract).
            htmlInput: {
              required: true, maxLength: 256, autoComplete: "off",
              "aria-describedby": emailFieldError ? emailErrorId : emailHintId,
            },
            formHelperText: { id: emailErrorId, role: "alert" },
          }}
          onChange={(e) => {
            setEmailValue(e.target.value);
            setEmailFieldError(null);
            if (emailUser !== null) clearKey(`change-email:${emailUser.id}`);
          }}
        />
        <p className="muted">{t("stepUpEmailHint")}</p>
        <StepUpPasswordField
          label={t("stepUpFieldLabel")}
          value={emailStepUpPassword}
          disabled={emailUser !== null && isPending(`change-email:${emailUser.id}`)}
          onChange={setEmailStepUpPassword}
        />
        <DialogError errors={errors} scope="change-email" />
      </Stack>
    </Dialog>
  );
}
