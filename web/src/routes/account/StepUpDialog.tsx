import { useId } from "react";
import type { FormEvent, ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { DialogActions, Stack, Typography } from "@mui/material";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import { ConfirmItsYou } from "../../components/StepUpProofFields";
import type { DialogAction } from "../../components/useDialogAction";
import type { en } from "../../i18n/en";
import type { StepUpDialogState } from "./useStepUpDialog";

type AccountKey = keyof typeof en.account;

// The lab's proof() block in a dialog (#1275): what the action is about
// (target), what it does, anything it adds (children), then the step-up proof.
export function StepUpDialog<K extends string>({ stepUp, dialog, copy, danger = false, onSubmit, target, children }: {
  stepUp: StepUpDialogState<K>;
  dialog: Pick<DialogAction<K>, "busy" | "isPending" | "errors">;
  // The account catalog's keys for the open action's words.
  copy: (action: K) => { title: AccountKey; body: AccountKey; submit: AccountKey };
  danger?: boolean;
  onSubmit: (e: FormEvent) => void;
  target?: ReactNode;
  children?: ReactNode;
}) {
  const bodyId = useId();
  const { t } = useTranslation("account");
  const { t: tc } = useTranslation("common");
  const { action, proof, setProof, close } = stepUp;
  const words = action && copy(action);
  const { busy, isPending, errors } = dialog;
  return (
    <Dialog
      open={action !== null}
      title={words ? t(words.title) : ""}
      onClose={close}
      closeDisabled={busy}
      describedBy={bodyId}
      actions={(
        <DialogActions>
          <button type="button" className="link" onClick={close} disabled={busy}>{tc("cancel")}</button>
          <BusyButton variant="contained" type="submit" color={danger ? "error" : "primary"} disabled={busy}
            busy={action !== null && isPending(action)}>
            {words && t(words.submit)}
          </BusyButton>
        </DialogActions>
      )}
      formProps={{ onSubmit }}
    >
      <Stack spacing={2}>
        {target && <Typography sx={{ fontWeight: 600, overflowWrap: "anywhere" }}>{target}</Typography>}
        <Typography id={bodyId} sx={{ lineHeight: 1.55 }}>{words && t(words.body)}</Typography>
        {children}
        <ConfirmItsYou value={proof} onChange={setProof} disabled={busy} />
        {action && <DialogError errors={errors} scope={action} />}
      </Stack>
    </Dialog>
  );
}
