import type { Dispatch, FormEvent, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { DialogActions, Stack, TextField } from "@mui/material";
import type { EggLotRow } from "../api/cluckwork";
import { useFormat } from "../farm/useFormat";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { DialogError } from "../components/DialogError";
import { NumberField } from "../components/NumberField";
import type { DialogErrors } from "../components/useDialogErrors";
import { stockMovementLabel } from "../i18n/enums";

// The write-off dialog of the stock screen (#406), moved out of StockPage
// (#1215). It renders at exactly the page's old position and condition — the
// page keeps the `writeOffLot !== null` mount and the `open={isAdmin}` gate —
// from the page's own dialog state, flight guard and message slots (#703);
// nothing here owns state or issues a fetch.
export function WriteOffDialog({
  writeOffLot,
  isAdmin,
  busy,
  isPending,
  errors,
  closeWriteOff,
  onWriteOff,
  woType, setWoType,
  woDirection, setWoDirection,
  woQty, setWoQty,
  woReason, setWoReason,
  woDelta,
}: {
  writeOffLot: EggLotRow;
  isAdmin: boolean;
  busy: boolean;
  isPending: (scope: string) => boolean;
  errors: DialogErrors;
  closeWriteOff: () => void;
  onWriteOff: (e: FormEvent) => void;
  woType: string; setWoType: Dispatch<SetStateAction<string>>;
  woDirection: string; setWoDirection: Dispatch<SetStateAction<string>>;
  woQty: number; setWoQty: Dispatch<SetStateAction<number>>;
  woReason: string; setWoReason: Dispatch<SetStateAction<string>>;
  woDelta: number;
}) {
  const { t } = useTranslation("stock");
  const { t: tc } = useTranslation("common");
  const fmt = useFormat();

  return (
    <Dialog open={isAdmin} title={t("writeOffDialogTitle", { date: fmt.date(writeOffLot.productionDate) })}
      onClose={closeWriteOff}
      actions={(
        <DialogActions>
          <button type="button" className="link" onClick={closeWriteOff}>{tc("cancel")}</button>
          <BusyButton variant="contained" type="submit" busy={isPending("write-off")} disabled={busy}>
            {t("writeOffSubmitButton")}
          </BusyButton>
        </DialogActions>
      )}
      formProps={{ onSubmit: (e) => void onWriteOff(e) }}
    >
      <Stack spacing={2}>
        <TextField
          select
          label={t("writeOffTypeLabel")}
          value={woType}
          slotProps={{ select: { native: true } }}
          onChange={(e) => setWoType(e.target.value)}
        >
          <option value="Discard">{stockMovementLabel("Discard")}</option>
          <option value="InternalUse">{stockMovementLabel("InternalUse")}</option>
          <option value="Reconciliation">{stockMovementLabel("Reconciliation")}</option>
        </TextField>
        {woType === "Reconciliation" && (
          <TextField
            select
            label={t("writeOffDirectionLabel")}
            value={woDirection}
            slotProps={{ select: { native: true } }}
            onChange={(e) => setWoDirection(e.target.value)}
          >
            <option value="remove">{t("writeOffDirectionRemoveOption")}</option>
            <option value="add">{t("writeOffDirectionAddOption")}</option>
          </TextField>
        )}
        {/* A wrapping label would contain the stepper buttons as well as its input. */}
        <div className="numfield-field">
          <label htmlFor="write-off-qty">{t("writeOffQuantityLabel")}</label>
          <NumberField id="write-off-qty" label={t("writeOffQuantityLabel").toLowerCase()}
            value={woQty} onChange={setWoQty} min={0} />
        </div>
        <TextField
          label={t("writeOffReasonLabel")}
          value={woReason}
          slotProps={{ htmlInput: { maxLength: 500, required: true } }}
          onChange={(e) => setWoReason(e.target.value)}
        />
        {woQty > 0 && (
          <p className="muted">
            {t("writeOffPreviewMessage", {
              current: writeOffLot.quantityAvailable,
              result: writeOffLot.quantityAvailable + woDelta,
            })}
          </p>
        )}
        {/* #479 — the "write-off" scope; DialogError adds role="alert",
            which this bare paragraph never carried (deliberate
            improvement, not a behavior this conversion is fixing). */}
        <DialogError errors={errors} scope="write-off" />
      </Stack>
    </Dialog>
  );
}
