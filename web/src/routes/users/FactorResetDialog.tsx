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
import { FACTOR_RESETS } from "./useFactorReset";
import type { FactorReset } from "./useFactorReset";

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
  proof,
  setProof,
}: {
  kind: FactorReset;
  resetUser: User | null;
  closeReset: () => void;
  onReset: (e: FormEvent) => void;
  busy: boolean;
  isPending: (scope: string) => boolean;
  errors: DialogErrors;
  proof: StepUpProof;
  setProof: Dispatch<SetStateAction<StepUpProof>>;
}) {
  const identityId = useId();
  const warningId = useId();
  // Re-renders the dialog when the language changes; FACTOR_RESETS reads i18n.
  useTranslation("users");
  const { t: tc } = useTranslation("common");
  const config = FACTOR_RESETS[kind];
  return (
    <Dialog
      open={resetUser !== null}
      title={resetUser ? config.title(resetUser) : ""}
      onClose={closeReset}
      describedBy={`${identityId} ${warningId}`}
      actions={(
        <DialogActions>
          <button type="button" className="link" onClick={closeReset}>{tc("cancel")}</button>
          <BusyButton variant="contained" type="submit" color="error" disabled={busy}
            busy={resetUser !== null && isPending(`${kind}:${resetUser.id}`)}>
            {config.submit()}
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
          {resetUser && config.warning(resetUser)}
        </Typography>
        {config.note && <Typography color="text.secondary" sx={{ lineHeight: 1.55 }}>{config.note()}</Typography>}
        <ConfirmItsYou value={proof} onChange={setProof} />
        <DialogError errors={errors} scope={kind} />
      </Stack>
    </Dialog>
  );
}
