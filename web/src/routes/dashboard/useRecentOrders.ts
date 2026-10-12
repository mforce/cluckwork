import { useCallback, useEffect, useState } from "react";
import { listOrders } from "../../api/cluckwork";
import type { SalesOrder } from "../../api/cluckwork";
import { usePagedList } from "../../components/usePagedList";
import i18n from "../../i18n";

export const RECENT_ORDERS = 5;

// The recent-orders read of the Dashboard (#1215): the page's own paged list,
// so the Recent orders panel can page it (#915). A role that cannot see sales
// issues no request at all (INV-2, #127).
export function useRecentOrders(canSeeSales: boolean) {
  // #915 — Recent orders pages through the shared list hook rather than a
  // second homegrown one (#469): its ticket discipline keeps a superseded page
  // from painting over a newer one. A role that cannot see sales issues no
  // request at all (INV-2, #127).
  const orders = usePagedList<SalesOrder, never>({
    fetchPage: useCallback(
      (offset: number, limit: number) =>
        canSeeSales ? listOrders({ limit, offset }) : Promise.resolve<SalesOrder[]>([]),
      [canSeeSales],
    ),
    pageSize: RECENT_ORDERS,
    errorText: () => i18n.t("dashboard:panelLoadError"),
  });
  const [ordersPage, setOrdersPage] = useState(0);
  const [ordersExtending, setOrdersExtending] = useState(false);
  const [ordersExtendFailed, setOrdersExtendFailed] = useState(false);
  const orderRows = orders.rows ?? [];
  const ordersFrom = ordersPage * RECENT_ORDERS;
  const orderSlice = orderRows.slice(ordersFrom, ordersFrom + RECENT_ORDERS);
  const { hasMore: ordersHasMore, loadMore: ordersLoadMore } = orders;
  // #915 — one request per tap, and the page number moves only once its rows
  // are here. An effect reconciling the page number against the loaded rows
  // re-issued a refused page forever and moved the reader onto an empty one.
  const showNextOrders = async () => {
    const next = ordersPage + 1;
    if (next * RECENT_ORDERS < orderRows.length) { setOrdersPage(next); return; }
    if (!ordersHasMore) return;
    setOrdersExtending(true);
    setOrdersExtendFailed(false);
    // Whether the page in view was already complete decides where this lands.
    const pageWasFull = orderSlice.length === RECENT_ORDERS;
    const outcome = await ordersLoadMore();
    setOrdersExtending(false);
    // A page of nothing is the end of the list, which is an answer rather than
    // a failure, and a dropped page is one nobody is waiting for. Only a
    // refusal is the reader's to see.
    if (outcome.status === "refused") { setOrdersExtendFailed(true); return; }
    if (outcome.status !== "loaded" || outcome.rows === 0) return;
    // A page that arrived PARTLY new leaves the page in view short, and those
    // rows belong to it — moving on would step over them. A FULL page plus a
    // new row is also the destination having one, since the list already
    // reaches this page's last index, so no second check earns its place.
    if (pageWasFull) setOrdersPage(next);
  };
  // A refetch starts the reader at the first page again (#915): the page they
  // were on described a list that no longer exists.
  useEffect(() => {
    setOrdersPage(0);
    setOrdersExtendFailed(false);
  }, [canSeeSales]);
  return { orders, ordersPage, setOrdersPage, ordersExtending, ordersExtendFailed, orderRows, ordersFrom, orderSlice, ordersHasMore, showNextOrders };
}
