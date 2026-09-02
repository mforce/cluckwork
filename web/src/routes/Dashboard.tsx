// web/src/routes/Dashboard.tsx
import { useEffect, useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import {
  getProductionReport, getStock, listDailyEntries, listFlocks, listOrders,
} from "../api/cluckwork";
import type { DailyEntry, Flock, ProductionReport, SalesOrder, StockRow } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useFormat } from "../farm/useFormat";
import { FarmDate } from "../components/FarmDate";
import { StatusBadge } from "../components/StatusBadge";
import { Sparkline } from "../components/Sparkline";
import { StockBar } from "../components/StockBar";
import { useAuth } from "../auth/useAuth";
import { useFarmToday } from "../farm/useFarm";
import { daysBefore } from "../lib/dates";
import {
  captureTiles, henDayTrend, sparkline, stockBar, todaysEggs, visibleTiles,
} from "../lib/dashboard";
import i18n from "../i18n";
import { statusLabel } from "../i18n/enums";

const RECENT_ORDERS = 5;
// Server clamps list limits at 500. One farm won't exceed that in Phase 1.x;
// past 500 flocks the tail silently drops — revisit with real paging if that
// day comes.
const MAX_PAGE = 500;

// F5 (#41) → #654: the landing page answers the 6 am question — which houses
// have no entry yet, and is lay rate normal? One tile per active flock, the
// missing ones first and at most 12 (a link carries the rest), the last 14
// days as a line with the production report's own hen-day % for the last 7
// complete days against the 7 before, stock as one stacked bar by grade, and
// recent sales as a list.
//
// Composed client-side from existing read endpoints (6 parallel GETs). The
// trend is the production report — computed server-side in one place, so the
// page never sums report rows: two calls, one per 7-day window, and the
// server's periodHenDayPct from each. Panels degrade independently: one
// failed fetch blanks its panel, not the page.
export function Dashboard() {
  const { t } = useTranslation("dashboard");
  const fmt = useFormat();
  const { t: tc } = useTranslation("common");
  // Captured once at mount so the header date always matches the queried day
  // even if the tab stays open across midnight. Farm-local, not browser-local
  // (#123): the entries it queries are stamped in the farm's day.
  const [today] = useState(useFarmToday());
  const [flocks, setFlocks] = useState<Flock[] | null>(null);
  const [entries, setEntries] = useState<DailyEntry[] | null>(null);
  const [stock, setStock] = useState<StockRow[] | null>(null);
  const [orders, setOrders] = useState<SalesOrder[] | null>(null);
  // Both windows or nothing: a line drawn from one week and a delta against
  // a missing one would be a figure nobody can reconcile.
  const [trend, setTrend] = useState<{ current: ProductionReport; previous: ProductionReport } | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // PROTECTED (INV-2, #127) — copied verbatim; do not edit.
  // ReadOnly/Denied can't read customers or orders — the API now returns 403
  // (#127) — so skip those two fetches and hide the sales panel, matching the
  // nav's own gate. Fetching them anyway would blank the panel with an error.
  const { role } = useAuth();
  const canSeeSales = role !== "ReadOnly" && role !== "Denied";
  // END PROTECTED

  useEffect(() => {
    Promise.allSettled([
      listFlocks({ limit: MAX_PAGE }),
      listDailyEntries({ from: today, to: today, limit: MAX_PAGE }),
      getStock(),
      canSeeSales ? listOrders({ limit: RECENT_ORDERS }) : Promise.resolve<SalesOrder[]>([]),
      // The last 7 complete days and the 7 before them — yesterday back, so an
      // unsubmitted today never ends the line in a false dip (owner decision A).
      getProductionReport(daysBefore(today, 7), daysBefore(today, 1)),
      getProductionReport(daysBefore(today, 14), daysBefore(today, 8)),
    ]).then(([f, e, s, o, cur, prev]) => {
      if (f.status === "fulfilled") setFlocks(f.value);
      if (e.status === "fulfilled") setEntries(e.value);
      if (s.status === "fulfilled") setStock(s.value);
      if (o.status === "fulfilled") setOrders(o.value);
      if (cur.status === "fulfilled" && prev.status === "fulfilled") setTrend({ current: cur.value, previous: prev.value });
      // Only the fetches we actually issued count toward "everything failed":
      // the sales read is an inert placeholder when the role can't see it.
      const issued = canSeeSales ? [f, e, s, o, cur, prev] : [f, e, s, cur, prev];
      if (issued.every((r) => r.status === "rejected")) {
        const reason = (issued[0] as PromiseRejectedResult).reason;
        setError(reason instanceof ApiError ? reason.message : i18n.t("dashboard:loadFailed"));
      }
      setLoading(false);
    });
  }, [today, canSeeSales]);

  // #512 US4 — a recent-sales row's own name: the row-owned `customerName` the
  // endpoint's scoped bulk read already resolved, or the translated
  // unavailable label. Never an id fragment.
  const rowCustomerName = (o: { customerName?: string | null }) =>
    o.customerName ?? t("rowCustomerUnavailable");

  if (loading) return <section><h2>{t("title")}</h2><p className="muted">{tc("loading")}</p></section>;
  if (error) return <section><h2>{t("title")}</h2><p className="error">{error}</p></section>;

  const panelError = <p className="error">{t("panelLoadError")}</p>;
  const tiles = flocks !== null && entries !== null ? visibleTiles(captureTiles(flocks, entries)) : null;
  const trendData = trend === null ? null : {
    line: sparkline([...trend.previous.days, ...trend.current.days]),
    henDay: henDayTrend(trend.current, trend.previous),
  };
  const bar = stock === null ? null : stockBar(stock);

  // Every figure is farm-locale formatted before it reaches a catalog string (#650).
  const deltaText = (delta: number | null) =>
    delta === null ? "—"
      : delta < 0 ? t("henDayDeltaDown", { delta: fmt.count(Math.abs(delta), 1) })
        : t("henDayDeltaUp", { delta: fmt.count(delta, 1) });

  return (
    <section>
      <h2>{t("title")}</h2>
      <p className="muted"><FarmDate iso={today} /></p>

      <div className="dash-grid">
        <div className="panel panel-wide">
          <h3><Link to="/daily-entry">{t("todayPanelTitle")}</Link></h3>
          {tiles === null || entries === null ? panelError : (
            <>
              <p className="muted">{t("todayEggsTotal", { total: fmt.count(todaysEggs(entries)) })}</p>
              {tiles.shown.length === 0 ? (
                <p className="muted">{t("noFlocksMessage")}</p>
              ) : (
                <>
                  <div className="capture-grid">
                    {tiles.shown.map(({ flock, entry }) => (
                      <Link
                        key={flock.id}
                        className={entry ? "capture-tile" : "capture-tile is-missing"}
                        to={`/daily-entry?flockId=${flock.id}&date=${today}`}
                        aria-label={entry
                          ? t("tileLinkLabel", { flock: flock.name })
                          : t("tileLinkLabelMissing", { flock: flock.name })}
                        // A house with an entry has nothing to record, so it gets no
                        // hint; `undefined` omits the attribute rather than emptying it.
                        title={entry ? undefined : t("recordTodayHint")}
                      >
                        <div className="capture-tile-name">{flock.name}</div>
                        <div className="capture-tile-eggs">{entry ? fmt.count(entry.totalEggs) : "—"}</div>
                        {entry
                          ? <StatusBadge status={entry.status} label={statusLabel(entry.status)} />
                          : <span className="badge badge-warn">{t("noEntryBadge")}</span>}
                      </Link>
                    ))}
                  </div>
                  {tiles.hidden > 0 && (
                    <Link className="capture-more" to="/daily-entry">
                      {t("moreFlocks", { count: tiles.hidden, total: fmt.count(tiles.hidden) })}
                    </Link>
                  )}
                </>
              )}
            </>
          )}
        </div>

        <div className="panel">
          <h3><Link to="/reports">{t("trendPanelTitle")}</Link></h3>
          {trendData === null ? panelError : (
            <>
              <Sparkline
                data={trendData.line}
                label={t("sparklineLabel", {
                  min: fmt.count(trendData.line.min), max: fmt.count(trendData.line.max), last: fmt.count(trendData.line.last),
                })}
              />
              <p className="muted">
                {t("henDayCaption", {
                  pct: trendData.henDay.current === null ? "—" : `${fmt.count(trendData.henDay.current, 1)}%`,
                  delta: deltaText(trendData.henDay.delta),
                })}
              </p>
            </>
          )}
        </div>

        <div className="panel">
          <h3><Link to="/stock">{t("stockPanelTitle")}</Link></h3>
          {bar === null || stock === null ? panelError : stock.length === 0 ? (
            <p className="muted">{t("noStockMessage")}</p>
          ) : (
            <>
              <StockBar data={bar} />
              <p className="muted">
                {t("eggsAvailableMessage", { count: bar.totalAvailable, total: fmt.count(bar.totalAvailable) })}
                {bar.segments.length > 0 ? ` · ${bar.segments.map((s) => `${s.gradeName} ${fmt.count(s.available)}`).join(" · ")}` : ""}
                {bar.totalRestricted > 0 ? ` · ${t("stockCaptionRestricted", { restricted: fmt.count(bar.totalRestricted) })}` : ""}
              </p>
            </>
          )}
        </div>

        {canSeeSales && (
        <div className="panel">
          <h3><Link to="/sales">{t("salesPanelTitle")}</Link></h3>
          {orders === null ? panelError : orders.length === 0 ? (
            <p className="muted">{t("noOrdersMessage")}</p>
          ) : (
            <ul className="dash-list">
              {orders.map((o) => (
                <li key={o.id} aria-label={o.referenceNumber}>
                  <span>{o.referenceNumber}</span>
                  {/* #512 US5 (FR-045) — authorized (canSeeSales, the gate this
                      whole panel is already behind) link into URL-filtered
                      Sales by canonical id; the name itself is row-owned. */}
                  <Link className="link" to={`/sales?customerId=${o.customerId}`}>{rowCustomerName(o)}</Link>
                  <StatusBadge status={o.status} label={statusLabel(o.status)} />
                  <span className="num">{fmt.money(o.totalMinorUnits, o.currencyCode, o.currencyMinorUnit)}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
        )}
      </div>
    </section>
  );
}
