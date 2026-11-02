import type { Dispatch, FormEvent, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { DialogActions, Stack, TextField } from "@mui/material";
import type { StepUpProof } from "../../api/client";
import type { User } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import { StepUpProofFields } from "../../components/StepUpProofFields";
import type { DialogErrors } from "../../components/useDialogErrors";
import { ROLE_VALUES, roleLabel } from "../../i18n/enums";

// The role-change dialog (#355), moved out of UsersPage (#1215). It renders
// at the page's old position, unconditionally; all state and handlers stay
// on the page and arrive as props.
export function ChangeRoleDialog({
  roleUser,
  closeRole,
  onChangeRole,
  busy,
  isPending,
  errors,
  roleValue,
  setRoleValue,
  roleStepUpProof,
  setRoleStepUpProof,
}: {
  roleUser: User | null;
  closeRole: () => void;
  onChangeRole: (e: FormEvent) => void;
  busy: boolean;
  isPending: (scope: string) => boolean;
  errors: DialogErrors;
  roleValue: string;
  setRoleValue: Dispatch<SetStateAction<string>>;
  roleStepUpProof: StepUpProof;
  setRoleStepUpProof: Dispatch<SetStateAction<StepUpProof>>;
}) {
  const { t } = useTranslation("users");
  const { t: tc } = useTranslation("common");
  return (
    <Dialog
      open={roleUser !== null}
      title={t("changeRoleTitle", { email: roleUser?.email ?? "" })}
      onClose={closeRole}
      actions={(
        <DialogActions>
          <button type="button" className="link" onClick={closeRole}>{tc("cancel")}</button>
          <BusyButton variant="contained" type="submit" disabled={busy}
            busy={roleUser !== null && isPending(`change-role:${roleUser.id}`)}>{t("changeRoleSubmitButton")}</BusyButton>
        </DialogActions>
      )}
      formProps={{ onSubmit: onChangeRole }}
    >
      <Stack spacing={2}>
        <p className="muted">
          {t("roleDialogHint")}
        </p>
        <TextField
          select
          label={t("roleFieldLabel")}
          value={roleValue}
          onChange={(e) => setRoleValue(e.target.value)}
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
        >
          {ROLE_VALUES.map((v) => (
            <option key={v} value={v}>
              {v === "Admin" ? t("adminRoleOption", { label: roleLabel(v) }) : roleLabel(v)}
            </option>
          ))}
        </TextField>
        <p className="muted">{t("stepUpRoleHint")}</p>
        <StepUpProofFields
          label={t("stepUpFieldLabel")}
          value={roleStepUpProof}
          onChange={setRoleStepUpProof}
        />
        <DialogError errors={errors} scope="change-role" />
      </Stack>
    </Dialog>
  );
}
