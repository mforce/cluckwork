import type { Dispatch, FormEvent, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { DialogActions, Stack, TextField } from "@mui/material";
import type { User } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import type { DialogErrors } from "../../components/useDialogErrors";

// The edit-user dialog (#163), moved out of UsersPage (#1215). It renders at
// the page's old position, unconditionally; all state and handlers stay on
// the page and arrive as props.
export function EditUserDialog({
  editUser,
  closeEdit,
  onUpdate,
  busy,
  isPending,
  errors,
  editName,
  setEditName,
}: {
  editUser: User | null;
  closeEdit: () => void;
  onUpdate: (e: FormEvent) => void;
  busy: boolean;
  isPending: (scope: string) => boolean;
  errors: DialogErrors;
  editName: string;
  setEditName: Dispatch<SetStateAction<string>>;
}) {
  const { t } = useTranslation("users");
  const { t: tc } = useTranslation("common");
  return (
    <Dialog
      open={editUser !== null}
      title={t("editUserTitle", { email: editUser?.email ?? "" })}
      onClose={closeEdit}
      actions={(
        <DialogActions>
          <button type="button" className="link" onClick={closeEdit}>{tc("cancel")}</button>
          <BusyButton variant="contained" type="submit" disabled={busy}
            busy={editUser !== null && isPending(`update:${editUser.id}`)}>{tc("save")}</BusyButton>
        </DialogActions>
      )}
      formProps={{ onSubmit: onUpdate }}
    >
      <Stack spacing={2}>
        <TextField
          label={t("nameFieldLabel")}
          type="text"
          value={editName}
          slotProps={{ htmlInput: { maxLength: 128, autoComplete: "off" } }}
          onChange={(e) => setEditName(e.target.value)}
        />
        <p className="muted">{t("clearNameHint")}</p>
        <DialogError errors={errors} scope="edit-user" />
      </Stack>
    </Dialog>
  );
}
