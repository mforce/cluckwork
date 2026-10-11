import type { Dispatch, FormEvent, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { DialogActions, Stack, TextField } from "@mui/material";
import { createInventoryItem, updateInventoryItem } from "../api/cluckwork";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { DialogError } from "../components/DialogError";
import type { DialogErrors } from "../components/useDialogErrors";
import { inventoryCategoryLabel } from "../i18n/enums";
import i18n from "../i18n";

// Feed first (spec §12); the rest of the categories get their features later.
const CATEGORIES = [
  "Feed", "Supplement", "Additive", "Medication", "Vaccine",
  "Packaging", "Bedding", "Sanitation", "EquipmentPart", "Other",
];

// The inventory catalog's create and edit dialogs and their submit handlers.
// All dialog state, the flight guard and the write discipline stay on
// InventoryPage — this renders from it, so the dialogs share the page's one
// useDialogAction session and message slots (#703) and each renders its own
// failure through its own slot (#479).
export function InventoryCatalogDialogs({
  creating, setCreating,
  editingId, setEditingId,
  isAdmin,
  busy,
  isPending,
  errors,
  run,
  commit,
  setMessage,
  name, setName,
  category, setCategory,
  unit, setUnit,
  defaultCost, setDefaultCost,
  editName, setEditName,
  editUnit, setEditUnit,
  editCost, setEditCost,
  closeCreate,
  closeEdit,
  toMinorUnits,
  costStep,
}: {
  creating: boolean; setCreating: Dispatch<SetStateAction<boolean>>;
  editingId: string | null; setEditingId: Dispatch<SetStateAction<string | null>>;
  isAdmin: boolean;
  busy: boolean;
  isPending: (scope: string) => boolean;
  errors: DialogErrors;
  run: <T>(scope: string, action: (current: () => boolean) => Promise<T>) => Promise<T | undefined>;
  commit: (keyScope: string, action: (key: string) => Promise<unknown>, touchesLedger?: boolean) => Promise<void>;
  setMessage: Dispatch<SetStateAction<string | null>>;
  name: string; setName: Dispatch<SetStateAction<string>>;
  category: string; setCategory: Dispatch<SetStateAction<string>>;
  unit: string; setUnit: Dispatch<SetStateAction<string>>;
  defaultCost: string; setDefaultCost: Dispatch<SetStateAction<string>>;
  editName: string; setEditName: Dispatch<SetStateAction<string>>;
  editUnit: string; setEditUnit: Dispatch<SetStateAction<string>>;
  editCost: string; setEditCost: Dispatch<SetStateAction<string>>;
  closeCreate: () => void;
  closeEdit: () => void;
  toMinorUnits: (text: string) => number | null;
  costStep: number;
}) {
  const { t } = useTranslation("inventory");
  const { t: tc } = useTranslation("common");

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    await run("create", async (current) => {
      await commit("create-item", (key) =>
        createInventoryItem({
          name, category, unit,
          defaultUnitCostMinorUnits: toMinorUnits(defaultCost),
        }, key));
      // Superseded (#703): the item exists and the list shows it, but the form
      // reset, the message and the close belong to the session on screen now.
      if (!current()) return;
      setName("");
      setDefaultCost("");
      setMessage(i18n.t("inventory:itemCreatedMessage"));
      setCreating(false);
    });
  }

  async function onSaveEdit(e: FormEvent) {
    e.preventDefault();
    const id = editingId;
    if (id === null) return;
    // The run scope is the dialog's; the idempotency key stays per item.
    await run("edit", async (current) => {
      await commit(`update:${id}`, (key) =>
        updateInventoryItem(id, {
          name: editName, unit: editUnit,
          defaultUnitCostMinorUnits: toMinorUnits(editCost),
        }, key));
      // Edit's superseded case is unreachable through the UI (the row's edit
      // button is `disabled={busy}`); the gate stays for INV-1 and against a
      // future change that enables the button — pinned by the wiring test
      // "a successful edit closes its dialog and refreshes the list".
      if (!current()) return;
      setEditingId(null);
    });
  }

  return (
    <>
      {/* Gated like the inline form was: a role change mid-edit closes it. */}
      <Dialog open={creating && isAdmin} title={t("newItemDialogTitle")} onClose={closeCreate}
        actions={(
          <DialogActions>
            <button type="button" className="link" onClick={closeCreate}>{tc("cancel")}</button>
            <BusyButton variant="contained" type="submit" busy={isPending("create")} disabled={busy}>{t("addItemButton")}</BusyButton>
          </DialogActions>
        )}
        formProps={{ onSubmit: onCreate }}
      >
        <Stack spacing={2}>
          <TextField
            label={t("itemNameLabel")}
            value={name}
            slotProps={{ htmlInput: { required: true, maxLength: 200 } }}
            onChange={(e) => setName(e.target.value)}
          />
          <TextField
            select
            label={t("categoryLabel")}
            value={category}
            slotProps={{ select: { native: true } }}
            onChange={(e) => setCategory(e.target.value)}
          >
            {CATEGORIES.map((c) => <option key={c} value={c}>{inventoryCategoryLabel(c)}</option>)}
          </TextField>
          <TextField
            label={t("unitLabel")}
            value={unit}
            slotProps={{ htmlInput: { required: true, maxLength: 20 } }}
            onChange={(e) => setUnit(e.target.value)}
          />
          <TextField
            type="number"
            label={t("defaultCostLabel")}
            value={defaultCost}
            slotProps={{ htmlInput: { min: 0, step: costStep } }}
            onChange={(e) => setDefaultCost(e.target.value)}
          />
          <DialogError errors={errors} scope="create" />
        </Stack>
      </Dialog>

      <Dialog open={editingId !== null && isAdmin} title={t("editItemDialogTitle")} onClose={closeEdit}
        actions={(
          <DialogActions>
            <button type="button" className="link" onClick={closeEdit}>{tc("cancel")}</button>
            <BusyButton variant="contained" type="submit" busy={isPending("edit")} disabled={busy}>
              {tc("save")}
            </BusyButton>
          </DialogActions>
        )}
        formProps={{ onSubmit: onSaveEdit, noValidate: true }}
      >
        {/* noValidate: the row's save used to be a plain button, so the browser
            never enforced min/step — toMinorUnits' own message did. */}
        <Stack spacing={2}>
          <TextField
            label={t("editItemNameLabel")}
            value={editName}
            slotProps={{ htmlInput: { maxLength: 200 } }}
            onChange={(e) => setEditName(e.target.value)}
          />
          <TextField
            label={t("editUnitLabel")}
            value={editUnit}
            slotProps={{ htmlInput: { maxLength: 20 } }}
            onChange={(e) => setEditUnit(e.target.value)}
          />
          <TextField
            type="number"
            label={t("defaultCostLabel")}
            value={editCost}
            slotProps={{ htmlInput: { min: 0, step: costStep } }}
            onChange={(e) => setEditCost(e.target.value)}
          />
          <DialogError errors={errors} scope="edit" />
        </Stack>
      </Dialog>
    </>
  );
}
