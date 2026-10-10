import type { RefObject } from "react";
import { Box, Button, Stack, Table, TableBody, TableCell, TableHead, TableRow } from "@mui/material";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import type { SalesOrder } from "../../api/cluckwork";
import { Dialog } from "../../components/Dialog";
import { FarmDate } from "../../components/FarmDate";
import { CONSOLE_LINK_SX, LedgerTableContainer } from "../../components/FieldConsole";
import { GlossaryLink } from "../../components/GlossaryLink";
import { PhoneDetailsField, PhoneLedgerList, PhoneLedgerRow, PhoneLedgerSummary } from "../../components/PhoneLedger";
import { ProvenanceCell, ProvenanceSummary } from "../../components/ProvenanceCell";
import { useFormat } from "../../farm/useFormat";
import { discountReasonLabel, listPriceBasisLabel } from "../../i18n/enums";
import { orderDiscount, orderIsAtList, orderListPriceBasis, type OrderDiscount } from "./orderMath";
import { LINK_ACTION_SX, NOWRAP, OrderStatus, useDiscountPercent, type PagedOrders } from "./salesUi";

function useOrderSegments() {
  const { t } = useTranslation("sales");
  const fmt = useFormat();
  const discountPercent = useDiscountPercent();

  // #987 — line 2 of a phone order row. Both segments read exactly the facts
  // the desktop Outstanding and Discount cells read, and drop out where those
  // cells would print an em dash: Details carries the full answer.
  const settlementSegment = (o: SalesOrder) => {
    if (o.outstandingMinorUnits === null) return null;
    const amount = fmt.money(o.outstandingMinorUnits, o.currencyCode, o.currencyMinorUnit);
    return o.outstandingMinorUnits === 0
      ? <Box component="span" sx={{ color: "var(--success)" }}>{t("settledBadge")}</Box>
      : <Box component="span" sx={{ color: "var(--error)" }}>{t("dueShort", { amount })}</Box>;
  };

  const discountBadgeText = (o: SalesOrder, discount: OrderDiscount, short: boolean) => {
    if (discount.kind !== "below") return orderIsAtList(o.items) ? t("atListShort") : null;
    const amount = fmt.money(discount.amountMinorUnits, o.currencyCode, o.currencyMinorUnit);
    if (discount.percent === null) return t("discountBadgeNoPct", { amount });
    const percent = discountPercent(discount.percent);
    return short ? t("discountShort", { percent }) : t("discountBadge", { percent, amount });
  };

  const discountDescriptionText = (o: SalesOrder, discount: OrderDiscount) =>
    discount.kind === "unknown"
      ? listPriceBasisLabel(orderListPriceBasis(o.items) === "nonePreDating" ? "ProductUnpriced" : "PreDating")
      : discount.partial ? t("discountPartialNote") : "";

  // The badge does NOT depend on the description: an order with one discounted
  // line and one unpriced one is `below` AND `partial`, and dropping the
  // percentage there hid a real discount (#988 review r1). Only the note stays
  // behind in Details, which is where the desktop cell's own note goes.
  const discountSegment = (o: SalesOrder) => {
    const discount = orderDiscount(o.items);
    const badge = discountBadgeText(o, discount, true);
    return badge === null ? null
      : discount.kind === "below"
        ? <Box component="span" className="discount" sx={{ fontWeight: 750 }}>{badge}</Box>
        : badge;
  };

  const detailsDiscount = (o: SalesOrder) => {
    const discount = orderDiscount(o.items);
    const description = discountDescriptionText(o, discount);
    const badge = discountBadgeText(o, discount, false);
    if (badge === null && !description) return "—";
    return <PhoneLedgerSummary parts={[badge, description && <span className="muted">{description}</span>]} />;
  };

  return { settlementSegment, discountSegment, detailsDiscount };
}

export function OrderList({
  orders, rows, isDesktop, canSettle, isAdmin, busy, fieldId, rowCustomerName, onOpen, setDetailsId,
}: {
  orders: PagedOrders;
  rows: SalesOrder[];
  isDesktop: boolean;
  canSettle: boolean;
  isAdmin: boolean;
  busy: boolean;
  fieldId: string;
  rowCustomerName: (o: { customerName?: string | null }) => string;
  onOpen: (id: string) => void;
  setDetailsId: (id: string | null) => void;
}) {
  const { t } = useTranslation("sales");
  const { t: tc } = useTranslation("common");
  const fmt = useFormat();
  const discountPercent = useDiscountPercent();
  const { settlementSegment, discountSegment } = useOrderSegments();
  return (
    <>
      {isDesktop ? <LedgerTableContainer>
        <Table size="small" sx={{ "& .MuiTableCell-root": { whiteSpace: "nowrap", px: .75, py: .75 } }}>
          <TableHead>
            <TableRow>
              <TableCell sx={NOWRAP}>{t("reference")}</TableCell>
              <TableCell sx={NOWRAP}>{t("date")}</TableCell>
              <TableCell sx={NOWRAP}>{t("customer")}</TableCell>
              <TableCell sx={NOWRAP}>{t("status")}<GlossaryLink term="ConfirmOrder" /></TableCell>
              <TableCell align="right" sx={NOWRAP}>{t("discount")}</TableCell>
              <TableCell align="right" sx={NOWRAP}>{t("total")}</TableCell>
              {canSettle && <TableCell align="right" sx={NOWRAP}>{t("outstanding")}<GlossaryLink term="Outstanding" /></TableCell>}
              <TableCell sx={NOWRAP}>{tc("recordHistoryHeader")}</TableCell>
              <TableCell></TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {rows.map((o) => {
              const discount = orderDiscount(o.items);
              const discountDescription = discount.kind === "unknown"
                ? listPriceBasisLabel(orderListPriceBasis(o.items) === "nonePreDating" ? "ProductUnpriced" : "PreDating")
                : discount.partial ? t("discountPartialNote") : "";
              const reason = o.discountReasonCode ? discountReasonLabel(o.discountReasonCode) : "";
              const atList = orderIsAtList(o.items);
              const partlyPaid = o.outstandingMinorUnits !== null && o.outstandingMinorUnits > 0
                && o.outstandingMinorUnits < o.totalMinorUnits;
              const rowId = `${fieldId}-${o.id}`;
              return (
                <TableRow key={o.id}>
                  <TableCell sx={NOWRAP}>{o.referenceNumber}</TableCell>
                  <TableCell sx={NOWRAP}><FarmDate iso={o.orderDate} /></TableCell>
                  <TableCell sx={NOWRAP}>{rowCustomerName(o)}</TableCell>
                  <TableCell sx={NOWRAP}><OrderStatus status={o.status} /></TableCell>
                  <TableCell align="right" sx={NOWRAP}
                    title={[discountDescription, reason].filter(Boolean).join(". ") || undefined}
                    aria-describedby={[
                      discountDescription && `${rowId}-discount`, reason && `${rowId}-reason`,
                    ].filter(Boolean).join(" ") || undefined}>
                    {discount.kind === "below" ? (
                      <Box component="span" className="discount" sx={{ ...NOWRAP, fontWeight: 750 }}>
                        {discount.percent === null
                          ? t("discountBadgeNoPct", { amount: fmt.money(discount.amountMinorUnits, o.currencyCode, o.currencyMinorUnit) })
                          : t("discountBadge", {
                              amount: fmt.money(discount.amountMinorUnits, o.currencyCode, o.currencyMinorUnit),
                              percent: discountPercent(discount.percent),
                            })}
                      </Box>
                    ) : !discountDescription && <span>{atList ? t("atListShort") : "—"}</span>}
                    {discountDescription && <>{" "}<span id={`${rowId}-discount`} className="muted discount-note">{discountDescription}</span></>}
                    {reason && <span id={`${rowId}-reason`} className="sr-only" aria-hidden="true" data-testid="row-discount-reason">{reason}</span>}
                  </TableCell>
                  <TableCell align="right" sx={NOWRAP}>{fmt.money(o.totalMinorUnits, o.currencyCode, o.currencyMinorUnit)}</TableCell>
                  {canSettle && (
                    <TableCell align="right" sx={NOWRAP}
                      title={partlyPaid ? t("partlyPaidNote") : undefined}
                      aria-describedby={partlyPaid ? `${rowId}-payment` : undefined}>
                      {o.outstandingMinorUnits === null ? "—"
                        : o.outstandingMinorUnits === 0
                          ? <span>{t("settledBadge")}</span>
                          : fmt.money(o.outstandingMinorUnits, o.currencyCode, o.currencyMinorUnit)}
                      {partlyPaid && <span id={`${rowId}-payment`} className="sr-only" aria-hidden="true">{t("partlyPaidNote")}</span>}
                    </TableCell>
                  )}
                  <ProvenanceCell history={o} official="confirmed" />
                  <TableCell sx={NOWRAP}>
                    <Stack direction="row" spacing={1} sx={{ alignItems: "center", flexWrap: "nowrap" }}>
                      {isAdmin && (
                        <Link className="link" to={`/audit?entityId=${o.id}`}>
                          {tc("recordHistory.viewHistoryLink")}
                        </Link>
                      )}
                      <Button variant="text" size="small" sx={LINK_ACTION_SX}
                        disabled={busy} onClick={() => onOpen(o.id)}>{t("open")}</Button>
                    </Stack>
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </LedgerTableContainer> : <PhoneLedgerList label={t("ordersHeading")}>
        {rows.map((o) => <li key={o.id}>
          <PhoneLedgerRow rowId={o.id} onClick={() => setDetailsId(o.id)} disabled={busy} date={<FarmDate iso={o.orderDate} />}
            primary={rowCustomerName(o)}
            trailing={<strong>{fmt.money(o.totalMinorUnits, o.currencyCode, o.currencyMinorUnit)}</strong>}
            summary={<PhoneLedgerSummary parts={[
              <strong><OrderStatus status={o.status} /></strong>,
              canSettle && settlementSegment(o),
              discountSegment(o),
              o.referenceNumber,
            ]} />} />
        </li>)}
      </PhoneLedgerList>}
      {orders.canLoadMore && (
        // A guarded READ — the hook withdraws this control for the
        // duration of any load, so it cannot mix two windows (#469).
        <button className="link" disabled={busy}
          onClick={() => void orders.loadMore()}>{t("loadMore")}</button>
      )}
    </>
  );
}

export function OrderDetailsDialog({
  details, canSettle, isAdmin, busy, focusAfterWrite, rowCustomerName, onOpen, setDetailsId,
}: {
  details: SalesOrder | null;
  canSettle: boolean;
  isAdmin: boolean;
  busy: boolean;
  focusAfterWrite: RefObject<string | null>;
  rowCustomerName: (o: { customerName?: string | null }) => string;
  onOpen: (id: string) => void;
  setDetailsId: (id: string | null) => void;
}) {
  const { t } = useTranslation("sales");
  const { t: tc } = useTranslation("common");
  const fmt = useFormat();
  const { settlementSegment, detailsDiscount } = useOrderSegments();
  return (
    <Dialog open={details !== null} compactTitle
      title={details
        ? t("orderDetailsDialogTitle", { reference: details.referenceNumber, customer: rowCustomerName(details) })
        : t("title")}
      onClose={() => setDetailsId(null)}
      actions={details && <Box sx={{ display: "flex", alignItems: "center", gap: 1, borderTop: "1px solid var(--rule)", "& .MuiButtonBase-root": { minHeight: 44 } }}>
        {isAdmin && <Button component={Link} size="small" sx={CONSOLE_LINK_SX} to={`/audit?entityId=${details.id}`}>
          {tc("recordHistory.viewHistoryLink")}
        </Button>}
        <Button variant="contained" sx={{ ml: "auto", borderRadius: "4px" }} disabled={busy}
          onClick={() => {
            const id = details.id;
            focusAfterWrite.current = id;
            setDetailsId(null);
            onOpen(id);
          }}>{t("open")}</Button>
      </Box>}>
      {details && <Box component="dl" sx={{ m: 0 }}>
        <PhoneDetailsField label={t("date")}><FarmDate iso={details.orderDate} /></PhoneDetailsField>
        <PhoneDetailsField label={t("status")}><OrderStatus status={details.status} /></PhoneDetailsField>
        <PhoneDetailsField label={t("linesHeader")}>{fmt.count(details.items.length)}</PhoneDetailsField>
        <PhoneDetailsField label={t("discount")}>{detailsDiscount(details)}</PhoneDetailsField>
        <PhoneDetailsField label={t("total")}>{fmt.money(details.totalMinorUnits, details.currencyCode, details.currencyMinorUnit)}</PhoneDetailsField>
        {canSettle && <PhoneDetailsField label={t("outstanding")}>
          {details.outstandingMinorUnits === null ? "—" : settlementSegment(details)}
        </PhoneDetailsField>}
        <PhoneDetailsField label={tc("recordHistoryHeader")}>
          <ProvenanceSummary history={details} official="confirmed" />
        </PhoneDetailsField>
      </Box>}
    </Dialog>
  );
}
