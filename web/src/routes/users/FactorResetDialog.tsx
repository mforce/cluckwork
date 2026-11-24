import { useId } from "react";
import type { Dispatch, FormEvent, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { DialogActions, Stack, Typography } from "@mui/material";
import type { StepUpProof } from "../../api/client";
import type { User } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import { ConfirmItsYou } from "../../components/StepUpProofFields";
import type { DialogErrors } from "../../components/useDialogErrors";
import type { FactorReset } from "./useFactorReset";

const COPY = {
  "reset-two-factor": { title: "resetTwoFactorTitle", warning: "resetTwoFactorWarningBody", note: null, submit: "resetTwoFactorSubmitButton" },
  "remove-passkeys": { title: "removePasskeysTitle", warning: "removePasskeysWarningBody", note: "removePasskeysNote", submit: "removePasskeysSubmitButton" },
} as const;

// #1280 — the two-factor reset's confirmation, the #1275 "Users reset" design:
// who is reset and what that ends, then who confirms it with the step-up proof.
// #1284 — the passkey removal's, the #1283 "Users A" design, the same way.
export function FactorResetDialog({
  kind,
  resetUser,
  closeReset,
  onReset,
  busy,
  isPending,
  errors,
  resetStepUpProof,
  setResetStepUpProof,
}: {
  kind: FactorReset;
  resetUser: User | null;
  closeReset: () => void;
  onReset: (e: FormEvent) => void;
  busy: boolean;
  isPending: (scope: string) => boolean;
  errors: DialogErrors;
  resetStepUpProof: StepUpProof;
  setResetStepUpProof: Dispatch<SetStateAction<StepUpProof>>;
}) {
  const identityId = useId();
  const warningId = useId();
  const { t } = useTranslation("users");
  const { t: tc } = useTranslation("common");
  const copy = COPY[kind];
  return (
    <Dialog
      open={resetUser !== null}
      title={t(copy.title, { name: resetUser?.displayName ?? resetUser?.email ?? "" })}
      onClose={closeReset}
      describedBy={`${identityId} ${warningId}`}
      actions={(
        <DialogActions>
          <button type="button" className="link" onClick={closeReset}>{tc("cancel")}</button>
          <BusyButton variant="contained" type="submit" color="error" disabled={busy}
            busy={resetUser !== null && isPending(`${kind}:${resetUser.id}`)}>
            {t(copy.submit)}
          </BusyButton>
        </DialogActions>
      )}
      formProps={{ onSubmit: onReset }}
    >
      <Stack spacing={2}>
        <Typography id={identityId} sx={{ fontWeight: 600, overflowWrap: "anywhere" }}>
          {[resetUser?.displayName, resetUser?.email].filter(Boolean).join(" · ")}
        </Typography>
        <Typography id={warningId} color="text.secondary" sx={{ lineHeight: 1.55 }}>
          {t(copy.warning, { count: resetUser?.passkeyCount ?? 0 })}
        </Typography>
        {copy.note && <Typography color="text.secondary" sx={{ lineHeight: 1.55 }}>{t(copy.note)}</Typography>}
        <ConfirmItsYou value={resetStepUpProof} onChange={setResetStepUpProof} />
        <DialogError errors={errors} scope={kind} />
      </Stack>
    </Dialog>
  );
}
