import { useId } from "react";
import type { Dispatch, FormEvent, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { DialogActions, Divider, Stack, Typography } from "@mui/material";
import type { StepUpProof } from "../../api/client";
import type { User } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import { StepUpProofFields } from "../../components/StepUpProofFields";
import type { DialogErrors } from "../../components/useDialogErrors";
import { useMe } from "../../session/SessionContext";

// #1280 — the two-factor reset's confirmation, the #1275 "Users reset" design:
// who is reset and what that ends, then who confirms it with the step-up proof.
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
  const me = useMe();
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
        <Typography sx={{ fontWeight: 600 }}>
          {[resetUser?.displayName, resetUser?.email].filter(Boolean).join(" · ")}
        </Typography>
        <Typography id={warningId} color="text.secondary" sx={{ lineHeight: 1.55 }}>
          {t("resetTwoFactorWarningBody")}
        </Typography>
        <Divider />
        <Typography component="h3" variant="h3">{t("confirmItsYouHeading")}</Typography>
        <p className="muted">{t("stepUpResetTwoFactorHint")}</p>
        {me && <Typography variant="caption">{t("signedInAsCaption", { email: me.email })}</Typography>}
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
