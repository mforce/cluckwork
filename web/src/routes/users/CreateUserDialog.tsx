import type { Dispatch, FormEvent, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { DialogActions, Stack, TextField } from "@mui/material";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import { StepUpPasswordField } from "../../components/StepUpPasswordField";
import type { DialogErrors } from "../../components/useDialogErrors";
import { ROLE_VALUES, roleLabel } from "../../i18n/enums";

// The create-user dialog, moved out of UsersPage (#1215). It renders at the
// page's old position, unconditionally; all state and handlers stay on the
// page and arrive as props, and the proof password is the page's own (#308).
export function CreateUserDialog({
  creating,
  closeCreate,
  onCreate,
  busy,
  isPending,
  errors,
  email,
  setEmail,
  password,
  setPassword,
  name,
  setName,
  role,
  setRole,
  createStepUpPassword,
  setCreateStepUpPassword,
}: {
  creating: boolean;
  closeCreate: () => void;
  onCreate: (e: FormEvent) => void;
  busy: boolean;
  isPending: (scope: string) => boolean;
  errors: DialogErrors;
  email: string;
  setEmail: Dispatch<SetStateAction<string>>;
  password: string;
  setPassword: Dispatch<SetStateAction<string>>;
  name: string;
  setName: Dispatch<SetStateAction<string>>;
  role: string;
  setRole: Dispatch<SetStateAction<string>>;
  createStepUpPassword: string;
  setCreateStepUpPassword: Dispatch<SetStateAction<string>>;
}) {
  const { t } = useTranslation("users");
  const { t: tc } = useTranslation("common");
  return (
    <Dialog open={creating} title={t("newUserButton")} onClose={closeCreate}
      actions={(
        <DialogActions>
          <button type="button" className="link" onClick={closeCreate}>{tc("cancel")}</button>
          <BusyButton variant="contained" type="submit" disabled={busy} busy={isPending("create")}>{t("createUserButton")}</BusyButton>
        </DialogActions>
      )}
      formProps={{ onSubmit: onCreate }}
    >
      <Stack spacing={2}>
        <TextField
          label={t("emailFieldLabel")}
          type="email"
          value={email}
          slotProps={{ htmlInput: { required: true, maxLength: 256, autoComplete: "off" } }}
          onChange={(e) => setEmail(e.target.value)}
        />
        <TextField
          label={t("passwordFieldLabel")}
          type="password"
          value={password}
          slotProps={{ htmlInput: { required: true, minLength: 12, maxLength: 256, autoComplete: "new-password" } }}
          onChange={(e) => setPassword(e.target.value)}
        />
        <TextField
          label={t("nameFieldLabel")}
          type="text"
          value={name}
          slotProps={{ htmlInput: { maxLength: 128, autoComplete: "off" } }}
          onChange={(e) => setName(e.target.value)}
        />
        <TextField
          select
          label={t("roleFieldLabel")}
          value={role}
          onChange={(e) => setRole(e.target.value)}
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
        >
          {ROLE_VALUES.map((v) => (
            <option key={v} value={v}>
              {v === "Admin" ? t("adminRoleOption", { label: roleLabel(v) }) : roleLabel(v)}
            </option>
          ))}
        </TextField>
        <p className="muted">{t("stepUpCreateHint")}</p>
        <StepUpPasswordField
          label={t("stepUpFieldLabel")}
          value={createStepUpPassword}
          onChange={setCreateStepUpPassword}
        />
        <DialogError errors={errors} scope="create" />
      </Stack>
    </Dialog>
  );
}
