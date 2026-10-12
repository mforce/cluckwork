import { useTranslation } from "react-i18next";
import { Box, DialogActions, Stack, TextField } from "@mui/material";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import { FlockPicker } from "../../components/FlockPicker";
import type { DialogAction } from "../../components/useDialogAction";
import type { useExpenseEdit } from "./useExpenseEdit";

// The correction dialog of the expenses screen, moved out of ExpensesPage
// (#1215). It renders at exactly the page's old position — always mounted, so
// MUI keeps its exit transition and its internal picker state — with all
// dialog state in useExpenseEdit and the flight guard on the page (#703).
export function ExpenseEditDialog({
  edit,
  dialog,
  today,
}: {
  edit: ReturnType<typeof useExpenseEdit>;
  dialog: DialogAction<"edit" | "add-category">;
  today: string;
}) {
  const { busy, isPending, errors } = dialog;
  const { t } = useTranslation("expenses");
  const { t: tc } = useTranslation("common");
  const {
    editing,
    editDate, setEditDate,
    editCategory, setEditCategory,
    editDescription, setEditDescription,
    editAmount, setEditAmount,
    editFlockPickerOpen, setEditFlockPickerOpen,
    editFlockEntity, editFlockId,
    editRequestedId, editFlockGen,
    setEditFlockEntity, setEditFlockId, setEditRequestedId, setEditFlockGen,
    editFlockSnapshot, setEditFlockSnapshot,
    editNote, setEditNote,
    editCategories,
    closeEdit,
    onSaveEdit,
  } = edit;

  return (
      <Dialog
        open={editing !== null}
        title={editing
          ? t("correctExpenseDialogTitleWithExpense", { date: editing.date, description: editing.description })
          : t("correctExpenseDialogTitle")}
        onClose={closeEdit}
        // A 409 rebinds this dialog to the server's newer row; the record
        // identity changing pulls focus back to the first field rather than
        // swapping the form out from under the user's cursor.
        focusKey={editing}
        actions={editing && (
          <DialogActions>
            <button type="button" className="link" disabled={busy}
              onClick={closeEdit}>{tc("cancel")}</button>
            {/* #512 (T028): canSubmit also gates the visible control; the
                handler guard above is the real boundary. */}
            <BusyButton variant="contained" type="submit" busy={isPending("edit")}
              disabled={busy || !editFlockSnapshot.canSubmit}>
              {t("saveCorrectionButton")}
            </BusyButton>
          </DialogActions>
        )}
        formProps={{ onSubmit: onSaveEdit }}
      >
        {editing && (
          <Stack spacing={2}>
            <TextField
              type="date"
              label={t("dateLabel")}
              value={editDate}
              slotProps={{ htmlInput: { max: today, required: true }, inputLabel: { shrink: true } }}
              onChange={(e) => setEditDate(e.target.value)}
            />
            <TextField
              select
              label={t("categoryLabel")}
              value={editCategory}
              slotProps={{ select: { native: true }, htmlInput: { required: true } }}
              onChange={(e) => setEditCategory(e.target.value)}
            >
              {editCategories.map((c) => (
                <option key={c.id} value={c.id}>{c.name}{c.active ? "" : t("deactivatedSuffix")}</option>
              ))}
            </TextField>
            <TextField
              label={t("descriptionLabel")}
              value={editDescription}
              slotProps={{ htmlInput: { required: true, maxLength: 200 } }}
              onChange={(e) => setEditDescription(e.target.value)}
            />
            <TextField
              type="number"
              label={t("amountLabel", { code: editing.currencyCode })}
              value={editAmount}
              slotProps={{ htmlInput: {
                min: (1 / 10 ** editing.currencyMinorUnit).toFixed(editing.currencyMinorUnit),
                step: "any", required: true,
              } }}
              onChange={(e) => setEditAmount(e.target.value)}
            />
            {/* Resolve the saved flock by ID, including archived flocks outside discovery. */}
            <Box onKeyDown={(event) => {
              // Let the picker cancel exploration before suppressing the dialog's Escape.
              if (event.key === "Escape" && editFlockPickerOpen) event.stopPropagation();
            }}>
              <FlockPicker
                label={t("flockOptionalLabel")}
                eligibility="all"
                required={false}
                // #512: exact-ID resolution works while collapsed; only discovery needs an open list.
                open={editFlockPickerOpen}
                controlledCommitted={editFlockEntity}
                controlledGeneration={editFlockGen}
                requestedId={editRequestedId}
                onSnapshot={(snap) => {
                  setEditFlockSnapshot(snap);
                  if (snap.committed) {
                    setEditFlockEntity(snap.committed);
                    setEditFlockId(null);
                  }
                }}
                onCommit={(f) => {
                  setEditFlockPickerOpen(false);
                  setEditFlockEntity(f);
                  setEditRequestedId(f.id);
                  setEditFlockId(null);
                  setEditFlockGen((g) => g + 1);
                }}
                onClear={() => {
                  setEditFlockPickerOpen(false);
                  setEditRequestedId(null);
                  setEditFlockEntity(null);
                  setEditFlockId(null);
                  setEditFlockGen((g) => g + 1);
                }}
                onEscape={() => setEditFlockPickerOpen(false)}
                onOutsideClick={() => setEditFlockPickerOpen(false)}
                trigger={
                  <button type="button" className="named-picker-trigger"
                    onClick={() => setEditFlockPickerOpen(true)}>{editFlockEntity
                      ? editFlockEntity.name
                      : editFlockId !== null && editFlockSnapshot.selectionPhase === "unavailable"
                        ? t("flockUnavailable")
                        : t("noneOption")}</button>
                }
              />
            </Box>
            <TextField
              label={t("noteOptionalLabel")}
              value={editNote}
              slotProps={{ htmlInput: { maxLength: 500 } }}
              onChange={(e) => setEditNote(e.target.value)}
            />
            {/* The 409 rebind reports through here, so the conflict banner stays
                next to the form it is telling you to re-apply. */}
            <DialogError errors={errors} scope="edit" />
          </Stack>
        )}
      </Dialog>
  );
}
