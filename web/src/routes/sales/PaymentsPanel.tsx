import { Box, Button, DialogActions, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Tooltip, Typography } from "@mui/material";
import { Trans, useTranslation } from "react-i18next";
import type { OrderPayments } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import { FarmDate } from "../../components/FarmDate";
import { CONSOLE_LINK_SX, LedgerTableContainer } from "../../components/FieldConsole";
import { PhoneDetailsField, PhoneLedgerList, PhoneLedgerRow, PhoneLedgerSummary } from "../../components/PhoneLedger";
import { useFormat } from "../../farm/useFormat";
import { statusLabel } from "../../i18n/enums";
import type { PaymentMethod } from "./orderMath";
import { NOWRAP, RAIL_PHONE_LIST_SX, type SalesAction } from "./salesUi";
import type { PaymentsState } from "./usePayments";

export function PaymentsPanel({ pay, payments, paymentDetails, isDesktop, isAdmin, action, today }: {
  pay: PaymentsState;
  payments: OrderPayments;
  paymentDetails: OrderPayments["items"][number] | null;
  isDesktop: boolean;
  isAdmin: boolean;
  action: SalesAction;
  today: string;
}) {
  const { t } = useTranslation("sales");
  const { t: tc } = useTranslation("common");
  const fmt = useFormat();
  const { busy, isPending, errors } = action;
  const {
    setPaymentDetailsId, onVoidPayment, openPayment, paying, closePayment, onRecordPayment,
    payDate, setPayDate, payAmount, setPayAmount, payMethod, setPayMethod, payRef, setPayRef, payNote, setPayNote,
  } = pay;
  return (
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
                  <TableCell align="right">{fmt.money(p.amountMinorUnits, p.currencyCode, p.currencyMinorUnit)}</TableCell>
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
              trailing={<strong>{fmt.money(p.amountMinorUnits, p.currencyCode, p.currencyMinorUnit)}</strong>}
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
          <PhoneDetailsField label={t("amount")}>{fmt.money(paymentDetails.amountMinorUnits, paymentDetails.currencyCode, paymentDetails.currencyMinorUnit)}</PhoneDetailsField>
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
            paid: fmt.money(payments.paidMinorUnits, payments.currencyCode, payments.currencyMinorUnit),
            outstanding: fmt.money(payments.outstandingMinorUnits, payments.currencyCode, payments.currencyMinorUnit),
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
}
