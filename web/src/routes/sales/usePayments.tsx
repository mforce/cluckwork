import { useEffect, useState, type RefObject } from "react";
import { Box, Button, DialogActions, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Tooltip, Typography } from "@mui/material";
import { Trans, useTranslation } from "react-i18next";
import { listOrderPayments, recordPayment, voidPayment } from "../../api/cluckwork";
import type { OrderPayments, SalesOrder } from "../../api/cluckwork";
import { ApiError } from "../../api/client";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import { FarmDate } from "../../components/FarmDate";
import { CONSOLE_LINK_SX, LedgerTableContainer } from "../../components/FieldConsole";
import { PhoneDetailsField, PhoneLedgerList, PhoneLedgerRow, PhoneLedgerSummary } from "../../components/PhoneLedger";
import type { useConfirm } from "../../components/useConfirm";
import { useFormat } from "../../farm/useFormat";
import i18n from "../../i18n";
import { statusLabel } from "../../i18n/enums";
import { toMinor, type PaymentMethod } from "./orderMath";
import { money, NOWRAP, RAIL_PHONE_LIST_SX, type PagedOrders, type SalesAction } from "./salesUi";

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
  isDesktop: boolean;
  isAdmin: boolean;
}

// Payments (#89, admin-only money data) — settlement state of the open
// confirmed order, and the rail section that shows and records it.
export function usePayments({
  active, activeIdRef, canSettle, today, orders, action, keyFor, clearKey, setMessage, askReason, isDesktop, isAdmin,
}: PaymentDeps) {
  const { t } = useTranslation("sales");
  const { t: tc } = useTranslation("common");
  const fmt = useFormat();
  const { run, openDialog, dismissDialog, errors, busy, isPending } = action;
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


  // Adjusted during render, like the order peek: a payment that left the list
  // retires its peek, so a later load cannot silently reopen it.
  if (paymentDetailsId !== null && payments !== null
    && !payments.items.some((p) => p.id === paymentDetailsId)) setPaymentDetailsId(null);
  const paymentDetails = payments?.items.find((p) => p.id === paymentDetailsId) ?? null;

  const panel = payments && (
    <>
      <Typography component="h4" sx={{ fontFamily: 'Georgia, "Times New Roman", serif', fontSize: "1.125rem", my: 2 }}>{t("payments")}</Typography>
      {payments.items.length > 0 && (isDesktop ?
        <LedgerTableContainer
          // The settlement rail stays narrow on desktop too (#831).
          alwaysShowSwipeCue>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>{t("date")}</TableCell>
                <TableCell align="right">{t("amount")}</TableCell>
                <TableCell>{t("method")}</TableCell>
                <TableCell>{t("reference")}</TableCell>
                <TableCell></TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {payments.items.map((p) => (
                <TableRow key={p.id} sx={p.voided ? { color: "var(--muted)" } : undefined}>
                  <TableCell sx={NOWRAP}>
                    <Tooltip title={p.note ?? undefined} describeChild>
                      <Box component="span"><FarmDate iso={p.paymentDate} /></Box>
                    </Tooltip>
                  </TableCell>
                  <TableCell align="right">{money(fmt, p.amountMinorUnits, p)}</TableCell>
                  <TableCell>{t(`method${p.method as PaymentMethod}`)}</TableCell>
                  <TableCell sx={NOWRAP}>{p.referenceNumber ?? "—"}</TableCell>
                  <TableCell sx={NOWRAP}>
                    {p.voided
                      ? (
                        <Tooltip title={p.voidReason ?? undefined} describeChild>
                          <span className="badge badge-danger">{statusLabel("Voided")}</span>
                        </Tooltip>
                      )
                      : isAdmin ? (
                        <BusyButton variant="text" size="small" sx={{ color: "#ffb4a2" }} disabled={busy} busy={isPending(`void-payment:${p.id}`)}
                          onClick={() => void onVoidPayment(p.id, p.version)}>{t("voidPaymentButton")}</BusyButton>
                      ) : null}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </LedgerTableContainer>
        : <Box sx={RAIL_PHONE_LIST_SX}><PhoneLedgerList label={t("payments")}>
          {payments.items.map((p) => <li key={p.id}>
            <PhoneLedgerRow muted={p.voided} disabled={busy} onClick={() => setPaymentDetailsId(p.id)}
              date={<FarmDate iso={p.paymentDate} />}
              primary={t(`method${p.method as PaymentMethod}`)}
              trailing={<strong>{money(fmt, p.amountMinorUnits, p)}</strong>}
              summary={<PhoneLedgerSummary parts={p.voided
                ? [<strong>{statusLabel("Voided")}</strong>, p.referenceNumber, p.voidReason]
                : [p.note]} />} />
          </li>)}
        </PhoneLedgerList></Box>
      )}
      {!isDesktop && <Dialog open={paymentDetails !== null} compactTitle
        title={paymentDetails
          ? t("paymentDetailsDialogTitle", { date: fmt.date(paymentDetails.paymentDate), method: t(`method${paymentDetails.method as PaymentMethod}`) })
          : t("payments")}
        onClose={() => setPaymentDetailsId(null)}
        actions={paymentDetails && !paymentDetails.voided && isAdmin && (
          <Box sx={{ display: "flex", borderTop: "1px solid var(--rule)", "& .MuiButtonBase-root": { minHeight: 44 } }}>
            {/* Not the rail's #ffb4a2: that salmon is picked for the
                dark settlement panel and measures far under AA on
                the dialog's own paper. --error carries a value per
                theme. */}
            <BusyButton variant="text" size="small" sx={{ ...CONSOLE_LINK_SX, color: "var(--error)" }} disabled={busy}
              busy={isPending(`void-payment:${paymentDetails.id}`)}
              onClick={() => {
                const { id, version } = paymentDetails;
                setPaymentDetailsId(null);
                void onVoidPayment(id, version);
              }}>{t("voidPaymentButton")}</BusyButton>
          </Box>
        )}>
        {paymentDetails && <Box component="dl" sx={{ m: 0 }}>
          <PhoneDetailsField label={t("amount")}>{money(fmt, paymentDetails.amountMinorUnits, paymentDetails)}</PhoneDetailsField>
          <PhoneDetailsField label={t("method")}>{t(`method${paymentDetails.method as PaymentMethod}`)}</PhoneDetailsField>
          <PhoneDetailsField label={t("reference")}>{paymentDetails.referenceNumber ?? "—"}</PhoneDetailsField>
          <PhoneDetailsField label={t("noteHeader")}>{paymentDetails.note ?? "—"}</PhoneDetailsField>
          <PhoneDetailsField label={t("status")}>
            {paymentDetails.voided ? statusLabel("Voided") : t("paymentRecordedStatus")}
          </PhoneDetailsField>
          {paymentDetails.voided && <PhoneDetailsField label={t("voidReasonHeader")}>{paymentDetails.voidReason ?? "—"}</PhoneDetailsField>}
        </Box>}
      </Dialog>}
      <p>
        <Trans
          ns="sales"
          i18nKey="paymentsSummary"
          values={{
            paid: money(fmt, payments.paidMinorUnits, payments),
            outstanding: money(fmt, payments.outstandingMinorUnits, payments),
          }}
          components={{ strong: <strong /> }}
        />
      </p>
      {payments.outstandingMinorUnits > 0 && (
        <div className="panel-actions">
          <Button variant="contained" type="button" onClick={openPayment}>
            {t("recordPayment")}
          </Button>
        </div>
      )}

      <Dialog open={paying} title={t("recordPayment")} onClose={closePayment}
        actions={
          <DialogActions>
            <button type="button" className="link" onClick={closePayment}>{tc("cancel")}</button>
            <BusyButton variant="contained" disabled={busy || !payAmount} busy={isPending("record-payment")}
              onClick={onRecordPayment}>
              {t("recordPayment")}
            </BusyButton>
          </DialogActions>
        }>
        <Stack spacing={2}>
          <TextField
            type="date"
            label={t("date")}
            value={payDate}
            slotProps={{ htmlInput: { max: today }, inputLabel: { shrink: true } }}
            onChange={(e) => setPayDate(e.target.value)}
          />
          <TextField
            type="number"
            label={t("amountWithCurrency", { code: payments.currencyCode })}
            value={payAmount}
            slotProps={{ htmlInput: {
              min: (1 / 10 ** payments.currencyMinorUnit).toFixed(payments.currencyMinorUnit),
              step: "any",
            } }}
            onChange={(e) => setPayAmount(e.target.value)}
          />
          <TextField
            select
            label={t("method")}
            value={payMethod}
            slotProps={{ select: { native: true } }}
            onChange={(e) => setPayMethod(e.target.value)}
          >
            {(["Cash", "Check", "Card", "BankTransfer", "MobilePayment", "Other"] as const).map((m) => (
              <option key={m} value={m}>{t(`method${m}`)}</option>
            ))}
          </TextField>
          <TextField
            label={t("referenceOptional")}
            value={payRef}
            slotProps={{ htmlInput: { maxLength: 50 } }}
            onChange={(e) => setPayRef(e.target.value)}
          />
          <TextField
            label={t("noteOptional")}
            value={payNote}
            slotProps={{ htmlInput: { maxLength: 500 } }}
            onChange={(e) => setPayNote(e.target.value)}
          />
          {/* A payment void can fail while this form is open; show only this dialog's error. */}
          <DialogError errors={errors} scope="record-payment" />
        </Stack>
      </Dialog>
    </>
  );

  return { payments, panel };
}
