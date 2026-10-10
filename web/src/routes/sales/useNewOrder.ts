import { useState } from "react";
import type { Customer } from "../../api/cluckwork";
import type { PickerSnapshot } from "../../components/NamedEntityPicker";
import type { SalesAction } from "./salesUi";

// create-order form
export function useNewOrder(today: string, { openDialog, dismissDialog }: Pick<SalesAction, "openDialog" | "dismissDialog">) {
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

  return {
    creatingOrder, setCreatingOrder, newOrderCustomerPickerOpen, setNewOrderCustomerPickerOpen,
    orderDate, setOrderDate, customer, setCustomer, customerGen, setCustomerGen,
    customerSnapshot, setCustomerSnapshot, openNewOrder, closeNewOrder,
  };
}

export type NewOrderState = ReturnType<typeof useNewOrder>;
