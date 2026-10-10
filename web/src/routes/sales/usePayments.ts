import { useEffect, useState, type RefObject } from "react";
import { listOrderPayments, recordPayment, voidPayment } from "../../api/cluckwork";
import type { OrderPayments, SalesOrder } from "../../api/cluckwork";
import { ApiError } from "../../api/client";
import type { useConfirm } from "../../components/useConfirm";
import i18n from "../../i18n";
import { toMinor } from "./orderMath";
import type { PagedOrders, SalesAction } from "./salesUi";

interface PaymentDeps {
  active: SalesOrder | null;
  activeIdRef: RefObject<string | null>;
  canSettle: boolean;
  today: string;
  orders: PagedOrders;
  action: SalesAction;
  keyFor: (scope: string) => string;
  clearKey: (scope: string) => void;
  setMessage: (message: string | null) => void;
  askReason: ReturnType<typeof useConfirm>["askReason"];
}

// Payments (#89, admin-only money data) — settlement state of the open
// confirmed order.
export function usePayments({
  active, activeIdRef, canSettle, today, orders, action, keyFor, clearKey, setMessage, askReason,
}: PaymentDeps) {
  const { run, openDialog, dismissDialog, errors } = action;
  // Pulled out for the payments effect's dependency list: it is stable, and
  // naming it is what lets that effect declare its real dependencies
  // (`dismissDialog` is stable by the hook's own construction).
  const { setPage: setPageError } = errors;
  const [paying, setPaying] = useState(false);
  // #987 — the payment row's read-only phone peek, an ID like the order peek.
  const [paymentDetailsId, setPaymentDetailsId] = useState<string | null>(null);
  const [payments, setPayments] = useState<OrderPayments | null>(null);
  const [payDate, setPayDate] = useState(today);
  const [payAmount, setPayAmount] = useState("");
  // Named once: the initial value and the new-session reset below must not
  // drift apart, which is exactly what two "Cash" literals would allow.
  const DEFAULT_PAY_METHOD = "Cash";
  const [payMethod, setPayMethod] = useState(DEFAULT_PAY_METHOD);
  const [payRef, setPayRef] = useState("");
  const [payNote, setPayNote] = useState("");

  const activeId = active?.id ?? null;
  const activeStatus = active?.status ?? null;
  useEffect(() => {
    // Cleared FIRST, unconditionally: while the new order's payments load (or
    // if the load fails), stale rows from the previous order must never stay
    // actionable — their Void buttons would target the wrong order's money
    // (codex review of #90).
    setPayments(null);
    // …and so does the payment form, for the same reason one layer up: it
    // belongs to the order that was open, not to the screen. Left open,
    // `paying` would reopen it on the NEXT order unasked, showing that order's
    // money under the previous order's failure — its key says "record-payment",
    // not which order (codex review of #481).
    //
    // The slot is emptied here, not merely closed over. Under #478 the trigger
    // cleared the entry on the way back in, so a clear here killed no mutant;
    // #479 moved that clear onto the dismissal, and THIS path is not one — it
    // is the screen closing the form out from under the user. Without the line
    // below, a 422 about the previous order's money survives the switch and is
    // waiting inside the form when they open it on this order's.
    //
    // `dismissDialog`, not `clearDialog`: a payment write can still be out
    // when this runs, so the slot is emptied, that attempt muted, AND its
    // session ended, together (#703 round 1 — every edge does both). Clearing
    // alone would let the rejection settle into the slot afterwards, to be
    // found by whoever opens a payment form next; muting without ending the
    // session would let the write's success act on a form opened later.
    //
    // An earlier version used `clearDialog` and argued the mute was
    // unreachable, because the row's open button is `disabled={busy}`. That
    // enumeration of what can change these deps was wrong twice over — the
    // panel's own Close is ungated (and #480 already established the backdrop
    // stops a mouse, not a screen reader's virtual cursor), and `canSettle`
    // flips with no button at all when a transparent 401 refresh re-derives the
    // role mid-write. Two misses of one shape means the method is wrong, so
    // this stops reasoning about reachability: `abandon` is correct whether or
    // not a write is out, and the next `beginAttempt` un-mutes so a later form
    // can still fail normally.
    //
    // Stated honestly, because a mutation says so: swapping this back to
    // `clearDialog` breaks NO test, and no test can be written that it would
    // break — every route back into a payment form re-runs this effect, which
    // clears the slot on the way in regardless. The choice buys the removal of
    // an argument that has been wrong twice, not an observable fix. Deleting
    // the line altogether IS caught, by the reopen test below.
    setPaying(false);
    dismissDialog("record-payment");
    // #987 — the phone peek belongs to the order that was open, for the same
    // reason the form above does: its Void targets one payment by id.
    setPaymentDetailsId(null);
    if (activeId === null || activeStatus !== "Confirmed" || !canSettle) return;
    let cancelled = false;
    listOrderPayments(activeId)
      .then((p) => { if (!cancelled) setPayments(p); })
      .catch(() => {
        if (!cancelled) setPageError(i18n.t("sales:loadPaymentsFailed"));
      });
    return () => { cancelled = true; };
    // `setPageError` is destructured above and `dismissDialog` is stable in
    // the hook; both are listed here rather than depending on `errors` — that
    // object is rebuilt every render, so naming it would re-run this on every
    // render and re-fetch the payments. There is no eslint in this package to
    // have caught either.
  }, [activeId, activeStatus, canSettle, dismissDialog, setPageError]);

  const closePayment = () => {
    setPaying(false);
    dismissDialog("record-payment");
  };

  const refreshPayments = async (orderId: string) => {
    const refreshed = await listOrderPayments(orderId);
    if (activeIdRef.current === orderId) setPayments(refreshed);
  };

  const onRecordPayment = () => void run("record-payment", async (current) => {
    if (!active || !payments) return;
    const minorUnits = toMinor(payAmount, payments.currencyMinorUnit);
    const scope = `pay:${active.id}`;
    // #769 put a payment-derived figure on the Orders list, which made this
    // handler a writer of that list. runWrite claims the list ticket before
    // the POST and re-walks every loaded page after it, so the row's
    // outstanding amount and the unpaid filter agree with the money that has
    // just moved (#469).
    await orders.runWrite(async () => {
      await recordPayment(active.id, {
        paymentDate: payDate,
        amountMinorUnits: minorUnits,
        method: payMethod,
        referenceNumber: payRef.trim() || null,
        note: payNote.trim() || null,
      }, keyFor(scope));
      // The key rotates the moment the WRITE lands — if it survived until the
      // refresh below succeeded, a failed refresh would make the NEXT payment
      // reuse it and silently replay this 201 instead of recording new money
      // (codex review of #90). The form reset before the refresh (#88 review)
      // covers the duplicate-resubmit direction.
      clearKey(scope);
      // The resets and the refresh keep the order #88 put them in. A SUPERSEDED
      // attempt skips the resets, because the fields now belong to a session the
      // user is typing into; opening the dialog clears them instead, so the spent
      // values cannot be resubmitted under a fresh key (codex review).
      if (current()) {
        setPayAmount("");
        setPayRef("");
        setPayNote("");
      }
      await refreshPayments(active.id);
      // NOT gated, deliberately, and this is where #477's own wording is wrong:
      // it calls the message "stray". The money was recorded. Withholding the
      // confirmation because the user closed the dialog leaves them believing it
      // did not happen, and the likely next act is paying twice. The message is
      // page-owned and renders outside the dialog, so it has somewhere honest to
      // land whether or not that session still exists (codex review).
      setMessage(i18n.t("sales:paymentRecorded"));
      if (!current()) return;
      setPaying(false); // only on success — a throw keeps the dialog up
    });
  });

  const onVoidPayment = async (paymentId: string, version: number) => {
    const reason = await askReason({
      title: i18n.t("sales:voidPaymentTitle"),
      body: i18n.t("sales:voidPaymentBody"),
      confirmLabel: i18n.t("sales:voidPaymentConfirmLabel"),
      destructive: true,
    });
    if (reason === null) return;
    void run(`void-payment:${paymentId}`, async () => {
      if (!active) return;
      const id = active.id;
      const scope = `void-payment:${paymentId}`;
      // A void moves the same figure the Orders list carries, in the other
      // direction, so it is a list write for the same reason a payment is.
      await orders.runWrite(async () => {
        try {
          await voidPayment(paymentId, { version, reason }, keyFor(scope));
          clearKey(scope);
        } catch (err) {
          // Version-guarded: any SERVER response settles the attempt (the base
          // version prevents double-apply); only transport failures keep the key.
          if (err instanceof ApiError) clearKey(scope);
          throw err;
        }
        await refreshPayments(id);
        setMessage(i18n.t("sales:paymentVoided"));
      });
    });
  };

  const openPayment = () => {
    setPayDate(today);
    // Reset the abandoned payment so a fresh key cannot resubmit it.
    setPayAmount("");
    setPayRef("");
    setPayNote("");

    setPayMethod(DEFAULT_PAY_METHOD);
    openDialog("record-payment"); // a new session — see #477
    setPaying(true);
  };

  return {
    payments, paying, closePayment, openPayment, onRecordPayment, onVoidPayment,
    paymentDetailsId, setPaymentDetailsId,
    payDate, setPayDate, payAmount, setPayAmount, payMethod, setPayMethod,
    payRef, setPayRef, payNote, setPayNote,
  };
}

export type PaymentsState = ReturnType<typeof usePayments>;
