import { useId } from "react";
import type { Dispatch, FormEvent, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { DialogActions, Stack, Typography } from "@mui/material";
import type { StepUpProof } from "../../api/client";
import type { User } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import { StepUpProofFields } from "../../components/StepUpProofFields";
import type { DialogErrors } from "../../components/useDialogErrors";

// #1280 — the two-factor reset's confirmation, laid out like the disable
// dialog: the warning says what the reset ends, then the step-up proof.
export function ResetTwoFactorDialog({
  resetUser,
  closeReset,
  onReset,
  busy,
  isPending,
  errors,
  resetStepUpProof,
  setResetStepUpProof,
}: {
  resetUser: User | null;
  closeReset: () => void;
  onReset: (e: FormEvent) => void;
  busy: boolean;
  isPending: (scope: string) => boolean;
  errors: DialogErrors;
  resetStepUpProof: StepUpProof;
  setResetStepUpProof: Dispatch<SetStateAction<StepUpProof>>;
}) {
  const warningId = useId();
  const { t } = useTranslation("users");
  const { t: tc } = useTranslation("common");
  return (
    <Dialog
      open={resetUser !== null}
      title={t("resetTwoFactorTitle", { email: resetUser?.email ?? "" })}
      onClose={closeReset}
      describedBy={warningId}
      actions={(
        <DialogActions>
          <button type="button" className="link" onClick={closeReset}>{tc("cancel")}</button>
          <BusyButton variant="contained" type="submit" color="error" disabled={busy}
            busy={resetUser !== null && isPending(`reset-two-factor:${resetUser.id}`)}>
            {t("resetTwoFactorSubmitButton")}
          </BusyButton>
        </DialogActions>
      )}
      formProps={{ onSubmit: onReset }}
    >
      <Stack spacing={2}>
        <Typography id={warningId} color="text.secondary" sx={{ lineHeight: 1.55 }}>
          {t("resetTwoFactorWarningBody")}
        </Typography>
        <p className="muted">{t("stepUpResetTwoFactorHint")}</p>
        <StepUpProofFields
          label={t("stepUpFieldLabel")}
          codeLabel={t("stepUpCodeFieldLabel")}
          value={resetStepUpProof}
          onChange={setResetStepUpProof}
        />
        <DialogError errors={errors} scope="reset-two-factor" />
      </Stack>
    </Dialog>
  );
}
