import { useState } from "react";
import { DialogActions, Stack, TextField } from "@mui/material";
import { useTranslation } from "react-i18next";
import { createOrder, getOrder } from "../../api/cluckwork";
import type { Customer, SalesOrder } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { CustomerPicker } from "../../components/CustomerPicker";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import type { PickerSnapshot } from "../../components/NamedEntityPicker";
import type { PagedOrders, SalesAction } from "./salesUi";

interface NewOrderDeps {
  today: string;
  action: SalesAction;
  orders: PagedOrders;
  keyFor: (scope: string) => string;
  clearKey: (scope: string) => void;
  setActive: (order: SalesOrder | null) => void;
}

// create-order form: its state, its write, and its dialog
export function useNewOrder({ today, action, orders, keyFor, clearKey, setActive }: NewOrderDeps) {
  const { t } = useTranslation("sales");
  const { t: tc } = useTranslation("common");
  const { run, openDialog, dismissDialog, busy, isPending, errors } = action;
  // F131: starting an order and taking a payment are discrete actions, not the
  // order builder itself — they open dialogs. Adding lines stays inline: the
  // draft panel IS the work surface.
  const [creatingOrder, setCreatingOrder] = useState(false);
  const [newOrderCustomerPickerOpen, setNewOrderCustomerPickerOpen] = useState(false);
  const [orderDate, setOrderDate] = useState(today);
  // #512 (T039) — the new-order customer is committed through CustomerPicker.
  // `customer` is the page-controlled committed entity; bumping
  // `customerGen` makes the engine re-sync its committed state (the explicit
  // first-customer default, a genuine pick, or a clear) so an Escape or later
  // exploration can never resurrect a stale id. `customerSnapshot.canSubmit`
  // gates BOTH the visible create control AND the create handler — a
  // disabled button alone is not the write-safety boundary. INITIAL STATE:
  // `canSubmit: false` — the dialog is inert until the engine's first
  // snapshot (or the mount-time default's controlled generation) commits an
  // entity, so a click in the brief pre-snapshot window ships nothing.
  const [customer, setCustomer] = useState<Customer | null>(null);
  const [customerGen, setCustomerGen] = useState(0);
  const [customerSnapshot, setCustomerSnapshot] = useState<PickerSnapshot<Customer>>({
    committed: null, selectionPhase: "uninitialized", exploring: false, canSubmit: false,
  });
  // The dialog's picker is open-driven like the dialog's own focus: while the
  // dialog is up the picker is live (discovery + the requestedId seam for an
  // out-of-window external id); on close the form unmounts, so nothing
  // lingers (US3, same contract as the Expenses edit picker).

  // Re-seeded on open, not only at mount: a tab left open across
  // farm-midnight would otherwise offer yesterday as the order date while the
  // picker's own ceiling had already moved on (codex review of #123). Nothing
  // to clear on the way in: #479 moved that onto the dismissal.
  const openNewOrder = () => {
    setOrderDate(today);
    setNewOrderCustomerPickerOpen(true);
    openDialog("create-order"); // a new session — see #477
    setCreatingOrder(true);
  };

  const closeNewOrder = () => {
    setNewOrderCustomerPickerOpen(false);
    setCreatingOrder(false);
    dismissDialog("create-order");
  };


  const onCreateOrder = () => run("create-order", async (current) => {
    // #512 (T039) — the handler's own guard: canSubmit is the write-safety
    // boundary (a disabled button alone is not). An exploring/uninitialized
    // or unavailable picker must not ship a stale committed id.
    if (!customer || !customerSnapshot.canSubmit) return;
    // runWrite claims the list ticket before the POST, so a filter change
    // made while it is in flight keeps the view (#469).
    await orders.runWrite(async () => {
      const created = await createOrder({ customerId: customer.id, orderDate }, keyFor("create-order"));
      // The key rotates the moment the WRITE lands — the same rule the payment
      // path states (#90). Releasing it after the follow-up read instead left a
      // spent key stranded whenever that read failed, and the next order then
      // replayed this one, so the customer the user actually chose never got
      // an order (codex review of this branch).
      clearKey("create-order");
      // Superseded: the order exists and the list write stands, but the panel
      // belongs to whatever session is on screen now (#477).
      if (!current()) return;
      const loaded = await getOrder(created.id);
      // Checked AGAIN, after the second await. The first version of this fix
      // checked once and then wrote the result of a further round trip, which
      // is the same hijack one hop later: the POST lands while the user is
      // still here, the GET is issued, and only THEN do they cancel and reopen.
      // A gate before an await says nothing about the state after it.
      if (!current()) return;
      setActive(loaded);
    });
    if (!current()) return;
    setNewOrderCustomerPickerOpen(false);
    setCreatingOrder(false); // only on success — a throw keeps the dialog up
  });

  const dialog = (
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

  return { setCustomer, setCustomerGen, openNewOrder, dialog };
}
