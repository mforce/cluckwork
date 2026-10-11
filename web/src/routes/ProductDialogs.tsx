import { useId } from "react";
import type { Dispatch, FormEvent, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { Checkbox, DialogActions, FormControlLabel, Stack, TextField } from "@mui/material";
import type { EggGrade, EggUnitConversion, Product } from "../api/cluckwork";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { DialogError } from "../components/DialogError";
import { NumberField } from "../components/NumberField";
import type { DialogErrors } from "../components/useDialogErrors";

// Spec §10.1 default_unit values usable for egg products; packed units resolve
// through the conversions below at sale time (part 2 of #97).
// "Other" is deliberately absent: it has no packed-unit conversion row this
// phase, so an Other product could never resolve to eggs (codex review of #98).
const EGG_UNITS = ["Egg", "Dozen", "Flat", "Tray", "Carton", "Case"];

// The three write dialogs of the product catalog (#97/#908): create, edit and
// edit-conversion. All state stays on ProductsPage — this renders the dialogs
// from it, so the dialogs share the page's one useDialogAction flight guard
// and message slots (#703) and each renders its own failure through its own
// slot (#479).
export function ProductDialogs({
  creating,
  editingProduct,
  editingConv,
  isAdmin,
  busy,
  isPending,
  errors,
  grades,
  currency,
  name, setName,
  unit, setUnit,
  gradeId, setGradeId,
  price, setPrice,
  notes, setNotes,
  editName, setEditName,
  editUnit, setEditUnit,
  editGradeId, setEditGradeId,
  editPrice, setEditPrice,
  editEggs, setEditEggs,
  editConvActive, setEditConvActive,
  closeCreate,
  closeEdit,
  closeEditConversion,
  onCreate,
  onSaveEdit,
  onSaveConversion,
}: {
  creating: boolean;
  editingProduct: Product | null;
  editingConv: EggUnitConversion | null;
  isAdmin: boolean;
  busy: boolean;
  isPending: (scope: string) => boolean;
  errors: DialogErrors;
  grades: EggGrade[];
  currency: { code: string; minor: number };
  name: string; setName: Dispatch<SetStateAction<string>>;
  unit: string; setUnit: Dispatch<SetStateAction<string>>;
  gradeId: string; setGradeId: Dispatch<SetStateAction<string>>;
  price: string; setPrice: Dispatch<SetStateAction<string>>;
  notes: string; setNotes: Dispatch<SetStateAction<string>>;
  editName: string; setEditName: Dispatch<SetStateAction<string>>;
  editUnit: string; setEditUnit: Dispatch<SetStateAction<string>>;
  editGradeId: string; setEditGradeId: Dispatch<SetStateAction<string>>;
  editPrice: string; setEditPrice: Dispatch<SetStateAction<string>>;
  editEggs: number; setEditEggs: Dispatch<SetStateAction<number>>;
  editConvActive: boolean; setEditConvActive: Dispatch<SetStateAction<boolean>>;
  closeCreate: () => void;
  closeEdit: () => void;
  closeEditConversion: () => void;
  onCreate: (e: FormEvent) => void;
  onSaveEdit: (e: FormEvent) => void;
  onSaveConversion: (e: FormEvent) => void;
}) {
  const { t } = useTranslation("products");
  const { t: tc } = useTranslation("common");
  // NumberField owns its own input, so the label points at it by id (#250).
  const eggsFieldId = useId();
  return (
    <>
      {/* Gated like the inline form was: a role change mid-edit closes it. */}
      <Dialog open={creating && isAdmin} title={t("newProductDialogTitle")} onClose={closeCreate}
        actions={(
          <DialogActions>
            <button type="button" className="link" onClick={closeCreate}>{tc("cancel")}</button>
            <BusyButton variant="contained" type="submit" disabled={busy} busy={isPending("create")}>{t("addProductButton")}</BusyButton>
          </DialogActions>
        )}
        formProps={{ onSubmit: (e) => void onCreate(e) }}
      >
        <Stack spacing={2}>
          <TextField
            label={t("nameLabel")}
            value={name}
            onChange={(e) => setName(e.target.value)}
            slotProps={{ htmlInput: { required: true, maxLength: 100 } }}
          />
          <TextField
            select
            label={t("gradeLabel")}
            value={gradeId}
            onChange={(e) => setGradeId(e.target.value)}
            slotProps={{
              select: { native: true }, htmlInput: { required: true },
              // The placeholder option shows text while `value` is "", so MUI
              // would leave the label resting on top of it.
              inputLabel: { shrink: true },
            }}
          >
            <option value="">{t("pickGradeOption")}</option>
            {grades.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
          </TextField>
          <TextField
            select
            label={t("soldPerLabel")}
            value={unit}
            onChange={(e) => setUnit(e.target.value)}
            slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
          >
            {EGG_UNITS.map((u) => <option key={u} value={u}>{u}</option>)}
          </TextField>
          <TextField
            label={currency.code ? t("defaultPriceWithCurrencyLabel", { code: currency.code }) : t("defaultPriceLabel")}
            type="number"
            value={price}
            onChange={(e) => setPrice(e.target.value)}
            placeholder={t("priceOptionalPlaceholder")}
            slotProps={{ htmlInput: { min: "0", step: (1 / 10 ** currency.minor).toFixed(currency.minor) } }}
          />
          <TextField
            label={t("notesLabel")}
            value={notes}
            onChange={(e) => setNotes(e.target.value)}
            slotProps={{ htmlInput: { maxLength: 500 } }}
          />
          <DialogError errors={errors} scope="create" />
        </Stack>
      </Dialog>

      <Dialog open={editingProduct !== null && isAdmin} title={t("editProductDialogTitle")} onClose={closeEdit}
        actions={(
          <DialogActions>
            <button type="button" className="link" onClick={closeEdit}>{tc("cancel")}</button>
            <BusyButton variant="contained" type="submit" disabled={busy}
              busy={isPending("edit")}>{tc("save")}</BusyButton>
          </DialogActions>
        )}
        formProps={{ onSubmit: (e) => void onSaveEdit(e), noValidate: true }}
      >
        {/* noValidate: the row's save used to be a plain button, so the browser
            never enforced min/step — the price parser's own message
            ("At most N decimal places for this currency") did. */}
        <Stack spacing={2}>
          <TextField
            label={t("nameLabel")}
            value={editName}
            onChange={(e) => setEditName(e.target.value)}
            slotProps={{ htmlInput: { maxLength: 100 } }}
          />
          <TextField
            select
            label={t("gradeLabel")}
            value={editGradeId}
            onChange={(e) => setEditGradeId(e.target.value)}
            slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
          >
            {grades.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
          </TextField>
          <TextField
            select
            label={t("soldPerLabel")}
            value={editUnit}
            onChange={(e) => setEditUnit(e.target.value)}
            slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
          >
            {EGG_UNITS.map((u) => <option key={u} value={u}>{u}</option>)}
          </TextField>
          {/* Stepped by THIS product's snapshot precision, not the account's. */}
          <TextField
            label={editingProduct ? t("defaultPriceWithCurrencyLabel", { code: editingProduct.currencyCode }) : t("defaultPriceLabel")}
            type="number"
            value={editPrice}
            onChange={(e) => setEditPrice(e.target.value)}
            slotProps={{
              htmlInput: {
                min: "0",
                step: editingProduct ? (1 / 10 ** editingProduct.currencyMinorUnit).toFixed(editingProduct.currencyMinorUnit) : "0.01",
              },
            }}
          />
          {/* No notes field: the inline edit had none, and #131 changes shape,
              not capability. editNotes stays seeded so the body round-trips. */}
          <DialogError errors={errors} scope="edit" />
        </Stack>
      </Dialog>

      <Dialog
        open={editingConv !== null && isAdmin}
        title={editingConv ? t("eggsPerUnit", { unitCode: editingConv.unitCode }) : t("packedUnitDialogTitle")}
        onClose={closeEditConversion}
        actions={(
          <DialogActions>
            <button type="button" className="link" onClick={closeEditConversion}>{tc("cancel")}</button>
            <BusyButton variant="contained" type="submit" disabled={busy}
              busy={isPending("edit-conversion")}>{tc("save")}</BusyButton>
          </DialogActions>
        )}
        formProps={{ onSubmit: (e) => void onSaveConversion(e), noValidate: true }}
      >
        <Stack spacing={2}>
          {/* #250: sibling label, not wrapping — a <label> may not contain
              interactive content other than its own control, and the stepper
              carries two buttons. NumberField itself is out of this slice's
              scope (pair 3, #828). */}
          <div className="numfield-field">
            <label htmlFor={eggsFieldId}>{t("eggsPerUnitFieldLabel")}</label>
            <NumberField id={eggsFieldId} label={t("eggsPerUnitFieldLabel").toLowerCase()}
              value={editEggs} onChange={setEditEggs} min={1} />
          </div>
          <FormControlLabel
            label={t("activeCheckboxLabel")}
            control={<Checkbox checked={editConvActive} onChange={(e) => setEditConvActive(e.target.checked)} />}
          />
          <DialogError errors={errors} scope="edit-conversion" />
        </Stack>
      </Dialog>
    </>
  );
}
