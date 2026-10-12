import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Alert, Box, Button, Card, LinearProgress, Typography } from "@mui/material";
import { ShoppingCart } from "lucide-react";
import type { SalesOrder } from "../../api/cluckwork";
import type { ReactNode } from "react";
import { useFormat } from "../../farm/useFormat";
import { EmptyState } from "../../components/EmptyState";
import { PanelPager } from "../../components/PanelPager";
import type { usePagedList } from "../../components/usePagedList";
import { statusLabel } from "../../i18n/enums";
import { RECENT_ORDERS } from "./useRecentOrders";
import { StatusDot } from "./StatusDot";
import { headingSx, sectionSx } from "./panelSx";

type OrderPagedList = ReturnType<typeof usePagedList<SalesOrder, never>>;

// The recent-orders panel of the Dashboard (#1215): the paged sales list with
// its pager and next-page retry. It renders at exactly the page's old
// position in the panel grid, under the page's own `canSeeSales` gate; the
// paged-list state and its paging ticket stay on the page and are passed in.
export function SalesPanel({
  orders,
  ordersPage, setOrdersPage,
  ordersExtending,
  ordersExtendFailed,
  orderRows, ordersFrom, orderSlice, ordersHasMore,
  showNextOrders,
  panelError,
  t, tp, fmt,
}: {
  orders: OrderPagedList;
  ordersPage: number; setOrdersPage: (page: number) => void;
  ordersExtending: boolean;
  ordersExtendFailed: boolean;
  orderRows: SalesOrder[];
  ordersFrom: number;
  orderSlice: SalesOrder[];
  ordersHasMore: boolean;
  showNextOrders: () => Promise<void>;
  panelError: ReactNode;
  t: ReturnType<typeof useTranslation<"dashboard">>["t"];
  tp: ReturnType<typeof useTranslation<"namedEntityPicker">>["t"];
  fmt: ReturnType<typeof useFormat>;
}) {
  // #512 US4 — a recent-sales row's own name: the row-owned `customerName` the
  // endpoint's scoped bulk read already resolved, or the translated
  // unavailable label. Never an id fragment.
  const rowCustomerName = (o: { customerName?: string | null }) =>
    o.customerName ?? t("rowCustomerUnavailable");
  return (
    <Card component="section" sx={{ ...sectionSx, gridColumn: { md: 1 } }}>
      <Box sx={headingSx}><Typography variant="h3" aria-label={t("salesPanelTitle")}><Link to="/sales">{t("recentOrdersTitle")}</Link></Typography></Box>
      {/* the panel error belongs to a read that left
          NOTHING on screen. A next page that failed says nothing about the
          page the reader is already looking at, so those rows stay and the
          refusal is offered back as a retry under them. */}
      {orderRows.length === 0 && orders.error !== null ? panelError
        : orders.rows === null ? <LinearProgress aria-label={t("salesPanelTitle")} sx={{ height: 5, borderRadius: 2 }} />
          : orderRows.length === 0 ? <EmptyState icon={ShoppingCart} message={t("noOrdersMessage")} /> : (
        <>
          <Box component="ul" role="list" aria-label={t("salesPanelTitle")} className="dash-sales-list" sx={{ listStyle: "none", m: 0, p: 0 }}>
            {orderSlice.map((o) => <Box component="li" key={o.id} aria-label={o.referenceNumber} sx={{
              display: "grid", gridTemplateColumns: "minmax(0,1fr) auto", gap: 1.5, py: 1.25, borderTop: "1px solid var(--rule)",
            }}>
              <Box sx={{ minWidth: 0, overflowWrap: "anywhere" }}>
                <Typography component={Link} to={`/sales?customerId=${o.customerId}`} sx={{ fontWeight: 600 }}>{rowCustomerName(o)}</Typography>
                <Typography variant="caption" color="text.secondary" sx={{ display: "block" }}>{o.referenceNumber}</Typography>
                {o.items[0] && <Typography variant="caption" color="text.secondary" sx={{ display: "block" }}>
                  {fmt.count(o.items[0].quantity)}{o.items[0].eggGradeName ? ` ${o.items[0].eggGradeName}` : ""}{o.items.length > 1 ? ` +${fmt.count(o.items.length - 1)}` : ""}
                </Typography>}
              </Box>
              <Box sx={{ textAlign: "right" }}>
                <Typography className="num" sx={{ fontWeight: 600 }}>{fmt.money(o.totalMinorUnits, o.currencyCode, o.currencyMinorUnit)}</Typography>
                <StatusDot status={o.status} label={statusLabel(o.status)} />
                {o.status === "Draft" && <Typography component={Link} to={`/sales?customerId=${o.customerId}`} variant="body2" sx={{ width: "100%", justifyContent: "flex-end" }}>{t("salesRowConfirmAction")}</Typography>}
              </Box>
            </Box>)}
          </Box>
        {/* A full first page means paging is a live concept here, so the
            pager stays once the end is found rather than vanishing under
            the tap that found it — with the total it just settled. */}
        {(ordersPage > 0 || ordersHasMore || orderRows.length >= RECENT_ORDERS) && <PanelPager
          label={ordersHasMore
            ? t("pagerOrdersOpen", { first: fmt.count(ordersFrom + 1), last: fmt.count(ordersFrom + orderSlice.length) })
            : t("pagerOrders", { first: fmt.count(ordersFrom + 1), last: fmt.count(ordersFrom + orderSlice.length), total: fmt.count(orderRows.length) })}
          previousLabel={t("pagerPrevious", { panel: t("recentOrdersTitle") })}
          nextLabel={t("pagerNext", { panel: t("recentOrdersTitle") })}
          hasPrevious={ordersPage > 0 && !ordersExtending}
          hasNext={!ordersExtending && (ordersFrom + RECENT_ORDERS < orderRows.length || ordersHasMore)}
          onPrevious={() => setOrdersPage(ordersPage - 1)}
          onNext={() => { void showNextOrders(); }}
        />}
        {ordersExtendFailed && <Alert severity="error" className="error" sx={{ mt: 1 }} action={
          <Button
            color="inherit" size="small"
            aria-label={t("pagerRetryNext", { panel: t("recentOrdersTitle") })}
            onClick={() => { void showNextOrders(); }}
            sx={{ "&&": { minHeight: 44 } }}
          >
            {tp("retry")}
          </Button>
        }>{t("pagerNextFailed")}</Alert>}
        </>
      )}
    </Card>
  );
}
