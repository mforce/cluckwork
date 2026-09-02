import { useEffect, useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import {
  formatMoney, getStock, listDailyEntries, listFlocks, listOrders,
} from "../api/cluckwork";
import type { DailyEntry, Flock, SalesOrder, StockRow } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { StatusBadge } from "../components/StatusBadge";
import { useAuth } from "../auth/useAuth";
import { useFarmToday } from "../farm/useFarm";
import i18n from "../i18n";
import { statusLabel } from "../i18n/enums";

const RECENT_ORDERS = 5;
// Server clamps list limits at 500. One farm won't exceed that in Phase 1.x;
// past 500 flocks/customers the tail silently drops — revisit with real paging
// if that day comes.
const MAX_PAGE = 500;

// F5 (#41): landing page — today's production per flock, stock by grade, and
// recent sales, each linking to its full screen. Composed client-side from
// existing read endpoints (5 parallel GETs; aggregate endpoint not warranted).
// Panels degrade independently: one failed fetch blanks its panel, not the page.
export function Dashboard() {
  const { t } = useTranslation("dashboard");
  const { t: tc } = useTranslation("common");
  // Captured once at mount so the header date always matches the queried day
  // even if the tab stays open across midnight. Farm-local, not browser-local
  // (#123): the entries it queries are stamped in the farm's day.
  const [today] = useState(useFarmToday());
  const [flocks, setFlocks] = useState<Flock[] | null>(null);
  const [entries, setEntries] = useState<DailyEntry[] | null>(null);
  const [stock, setStock] = useState<StockRow[] | null>(null);
  const [orders, setOrders] = useState<SalesOrder[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // ReadOnly/Denied can't read customers or orders — the API now returns 403
  // (#127) — so skip those two fetches and hide the sales panel, matching the
  // nav's own gate. Fetching them anyway would blank the panel with an error.
  const { role } = useAuth();
  const canSeeSales = role !== "ReadOnly" && role !== "Denied";

  useEffect(() => {
    Promise.allSettled([
      listFlocks({ limit: MAX_PAGE }),
      listDailyEntries({ from: today, to: today, limit: MAX_PAGE }),
      getStock(),
      canSeeSales ? listOrders({ limit: RECENT_ORDERS }) : Promise.resolve<SalesOrder[]>([]),
    ]).then(([f, e, s, o]) => {
      if (f.status === "fulfilled") setFlocks(f.value);
      if (e.status === "fulfilled") setEntries(e.value);
      if (s.status === "fulfilled") setStock(s.value);
      if (o.status === "fulfilled") setOrders(o.value);
      // #512 US5 (T058) — recent-sales rows render their OWN row-owned
      // `customerName` (see rowCustomerName below); the 500-customer catalog
      // fetch that used to name them is gone (page-adoption.md: "Dashboard no
      // longer loads a 500-customer catalog solely to name Sales rows").
      // Only the fetches we actually issued count toward "everything failed":
      // the sales read is an inert placeholder when the role can't see it.
      const issued = canSeeSales ? [f, e, s, o] : [f, e, s];
      if (issued.every((r) => r.status === "rejected")) {
        const reason = (issued[0] as PromiseRejectedResult).reason;
        setError(reason instanceof ApiError ? reason.message : i18n.t("dashboard:loadFailed"));
      }
      setLoading(false);
    });
  }, [today, canSeeSales]);

  // Voided entries vacate their day (#82): a voided row must not stand in for
  // the flock's entry — a day with only voided rows counts as "no entry yet".
  const entryFor = (flockId: string) =>
    entries?.find((e) => e.flockId === flockId && e.status !== "Voided");
  // #512 US4 (T048/T052) — a recent-sales row's own name, independent of the
  // 500-customer catalog fetch below (that fetch's removal is US5/T056):
  // the row-owned `customerName` the endpoint's scoped bulk read already
  // resolved, or the translated unavailable label. Never an id fragment
  // (contracts/http-api.md: "Required names are never replaced with
  // identifier fragments").
  const rowCustomerName = (o: { customerName?: string | null }) =>
    o.customerName ?? t("rowCustomerUnavailable");
  // "no entry" is a missed-capture flag — only meaningful for active flocks.
  // Depleted/archived flocks stay visible only if they do have an entry today.
  const visibleFlocks = (flocks ?? []).filter((f) => f.status === "Active" || entryFor(f.id));
  const totalAvailable = (stock ?? []).reduce((a, r) => a + r.available, 0);

  // Headline figures for the stat row — null when that fetch failed (the card
  // shows "—" rather than a misleading zero, matching the panels' degrade-alone
  // behaviour).
  const todaysEggs = entries === null ? null
    : entries.filter((e) => e.status !== "Voided").reduce((a, e) => a + e.totalEggs, 0);
  const eggsAvailable = stock === null ? null : totalAvailable;
  const activeFlocks = flocks === null ? null : flocks.filter((f) => f.status === "Active").length;

  if (loading) return <section><h2>{t("title")}</h2><p className="muted">{tc("loading")}</p></section>;
  if (error) return <section><h2>{t("title")}</h2><p className="error">{error}</p></section>;

  const panelError = <p className="error">{t("panelLoadError")}</p>;

  return (
    <section>
      <h2>{t("title")}</h2>
      <p className="muted">{today}</p>

      <div className="stat-grid">
        <div className="stat">
          <div className="stat-value">{todaysEggs ?? "—"}</div>
          <div className="stat-label">{t("statEggsCollectedToday")}</div>
        </div>
        <div className="stat">
          <div className="stat-value">{eggsAvailable ?? "—"}</div>
          <div className="stat-label">{t("statEggsAvailable")}</div>
        </div>
        <div className="stat">
          <div className="stat-value">{activeFlocks ?? "—"}</div>
          <div className="stat-label">{t("statActiveFlocks")}</div>
        </div>
      </div>

      <div className="dash-grid">
        <div className="panel">
          <h3><Link to="/daily-entry">{t("todayPanelTitle")}</Link></h3>
          {flocks === null || entries === null ? panelError : visibleFlocks.length === 0 ? (
            <p className="muted">{t("noFlocksMessage")}</p>
          ) : (
            <table className="data">
              <thead>
                <tr><th>{t("flockHeader")}</th><th>{t("statusHeader")}</th><th>{t("eggsHeader")}</th><th>{t("lossesHeader")}</th><th>{t("mortalityHeader")}</th></tr>
              </thead>
              <tbody>
                {visibleFlocks.map((f) => {
                  const e = entryFor(f.id);
                  return (
                    <tr key={f.id}>
                      <td>{f.name}</td>
                      <td>{e ? <StatusBadge status={e.status} label={statusLabel(e.status)} /> : <span className="badge badge-warn">{t("noEntryBadge")}</span>}</td>
                      <td>{e ? e.totalEggs : "—"}</td>
                      <td>{e ? e.crackedEggs + e.dirtyEggs + e.discardedEggs : "—"}</td>
                      <td>{e ? e.mortalityCount : "—"}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          )}
        </div>

        <div className="panel">
          <h3><Link to="/stock">{t("stockPanelTitle")}</Link></h3>
          {stock === null ? panelError : stock.length === 0 ? (
            <p className="muted">{t("noStockMessage")}</p>
          ) : (
            <>
              <table className="data">
                <thead>
                  <tr><th>{t("gradeHeader")}</th><th>{t("availableHeader")}</th><th>{t("restrictedHeader")}</th></tr>
                </thead>
                <tbody>
                  {stock.map((r) => (
                    <tr key={r.eggGradeId}>
                      <td>{r.gradeName}</td>
                      <td>{r.available}</td>
                      <td>{r.restricted > 0 ? <span className="warn">{r.restricted}</span> : "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <p className="muted">{t("eggsAvailableMessage", { count: totalAvailable })}</p>
            </>
          )}
        </div>

        {canSeeSales && (
        <div className="panel">
          <h3><Link to="/sales">{t("salesPanelTitle")}</Link></h3>
          {orders === null ? panelError : orders.length === 0 ? (
            <p className="muted">{t("noOrdersMessage")}</p>
          ) : (
            <table className="data">
              <thead>
                <tr><th>{t("refHeader")}</th><th>{t("customerHeader")}</th><th>{t("statusHeader")}</th><th>{t("totalHeader")}</th></tr>
              </thead>
              <tbody>
                {orders.map((o) => (
                  <tr key={o.id}>
                    <td>{o.referenceNumber}</td>
                    <td>
                      {/* #512 US5 (T058, FR-045) — authorized (canSeeSales,
                          the gate this whole panel is already behind) link
                          into URL-filtered Sales by canonical id; the name
                          itself is always row-owned (rowCustomerName). */}
                      <Link className="link" to={`/sales?customerId=${o.customerId}`}>
                        {rowCustomerName(o)}
                      </Link>
                    </td>
                    <td><StatusBadge status={o.status} label={statusLabel(o.status)} /></td>
                    <td>{formatMoney(o.totalMinorUnits, o.currencyCode, o.currencyMinorUnit)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
        )}
      </div>
    </section>
  );
}
