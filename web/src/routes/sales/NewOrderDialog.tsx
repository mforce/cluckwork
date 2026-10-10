import { DialogActions, Stack, TextField } from "@mui/material";
import { useTranslation } from "react-i18next";
import { BusyButton } from "../../components/BusyButton";
import { CustomerPicker } from "../../components/CustomerPicker";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import type { SalesAction } from "./salesUi";
import type { NewOrderState } from "./useNewOrder";

export function NewOrderDialog({ newOrder, action, today, onCreateOrder }: {
  newOrder: NewOrderState;
  action: SalesAction;
  today: string;
  onCreateOrder: () => Promise<unknown>;
}) {
  const { t } = useTranslation("sales");
  const { t: tc } = useTranslation("common");
  const { busy, isPending, errors } = action;
  const {
    creatingOrder, closeNewOrder, customer, setCustomer, customerGen, setCustomerGen, setCustomerSnapshot,
    customerSnapshot, newOrderCustomerPickerOpen, setNewOrderCustomerPickerOpen, orderDate, setOrderDate,
  } = newOrder;
  return (
    <Dialog open={creatingOrder} title={t("newOrder")} onClose={closeNewOrder}
      actions={
        <DialogActions>
          <button type="button" className="link" onClick={closeNewOrder}>{tc("cancel")}</button>
          <BusyButton variant="contained" disabled={busy || !customer || !customerSnapshot.canSubmit}
            busy={isPending("create-order")}
            onClick={onCreateOrder}>{t("newDraftOrder")}</BusyButton>
        </DialogActions>
      }>
      <Stack spacing={2}>
        <CustomerPicker
          label={t("customer")}
          required
          // Start open for immediate discovery, then close after commit so
          // the absolutely-positioned listbox cannot cover the remaining
          // dialog controls. The committed trigger can reopen it to change
          // customer without discarding the draft fields.
          open={newOrderCustomerPickerOpen}
          controlledCommitted={customer}
          controlledGeneration={customerGen}
          onSnapshot={setCustomerSnapshot}
          onCommit={(c) => {
            setCustomer(c);
            setCustomerGen((g) => g + 1);
            setNewOrderCustomerPickerOpen(false);
          }}
          onEscape={() => setNewOrderCustomerPickerOpen(false)}
          onOutsideClick={() => setNewOrderCustomerPickerOpen(false)}
          trigger={
            <button type="button" className="named-picker-trigger"
              onClick={() => setNewOrderCustomerPickerOpen(true)}>
              {customer ? customer.name : t("pickCustomerOption")}
            </button>
          }
        />
        <TextField
          type="date"
          label={t("date")}
          value={orderDate}
          slotProps={{ htmlInput: { max: today }, inputLabel: { shrink: true } }}
          onChange={(e) => setOrderDate(e.target.value)}
        />
        {/* #474 — this copy lives INSIDE the dialog, which renders nothing
            while closed, so the page's `!creatingOrder` condition hid the
            message exactly when the dialog it belongs to was up. The scope
            test replaces it: the dialog reports its OWN write, never
            whatever else happened to fail underneath it. role="alert"
            because focus is trapped in the panel and nothing else announces
            the failure. */}
        <DialogError errors={errors} scope="create-order" />
      </Stack>
    </Dialog>
  );
}
