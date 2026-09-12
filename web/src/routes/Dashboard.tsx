// web/src/routes/Dashboard.tsx
import { useEffect, useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Bird, Egg, ShoppingCart } from "lucide-react";
import {
  getProductionReport, getStock, listDailyEntries, listFlocks, listOrders,
} from "../api/cluckwork";
import type { DailyEntry, Flock, ProductionReport, SalesOrder, StockRow } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useFormat } from "../farm/useFormat";
import { FarmDate } from "../components/FarmDate";
import { EmptyState } from "../components/EmptyState";
import { StatusBadge } from "../components/StatusBadge";
import { DayStrip } from "../components/DayStrip";
import { StockBar } from "../components/StockBar";
import { useAuth } from "../auth/useAuth";
import { useFarmToday } from "../farm/useFarm";
import { daysBefore } from "../lib/dates";
import {
  captureTiles, dayStrip, henDayTrend, stockBar, todaysEggs, visibleTiles,
} from "../lib/dashboard";
import type { DayStripData, DayStripSlot } from "../lib/dashboard";
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
    line: dayStrip({
      days: [...trend.previous.days, ...trend.current.days],
      recentCount: trend.current.days.length,
    }),
    henDay: henDayTrend(trend.current, trend.previous),
  };
  const bar = stock === null ? null : stockBar(stock);

  // Every figure is farm-locale formatted before it reaches a catalog string (#650).
  const deltaText = (delta: number | null) =>
    delta === null ? "—"
      : delta < 0 ? t("henDayDeltaDown", { delta: fmt.count(Math.abs(delta), 1) })
        : t("henDayDeltaUp", { delta: fmt.count(delta, 1) });
  // Lay rate falling IS the bad direction here, so the delta carries the
  // semantic colour. An unknown delta stays neutral rather than reading as good.
  // The strip's whole change is that a day with nothing recorded is an empty
  // slot, so the accessible name has to say how many there are — otherwise a
  // screen-reader user still gets "lowest 0", the conflation the redraw
  // removed for everyone else. Two complete sentences rather than one built by
  // concatenation, so each locale can order its own clauses.
  const trendLabel = (line: DayStripData) => {
    // Four states, none of which may report a figure it does not have. max and
    // average are null together — both come from the COMPLETE days — so a
    // window with none gets a sentence rather than a formatted 0, and which
    // sentence depends on whether anything was recorded at all.
    if (line.max === null || line.average === null) {
      return line.partial === 0 ? t("trendStripLabelNone") : t("trendStripLabelNoComplete");
    }
    const figures = { max: fmt.count(line.max), avg: fmt.count(line.average, 1) };
    const gaps = line.partial + line.unrecorded;
    return gaps === 0
      ? t("trendStripLabel", figures)
      : t("trendStripLabelBlanks", { ...figures, count: gaps, blank: fmt.count(gaps) });
  };
  // One day's readout, and the accessible name of its slot. Every figure is
  // farm-locale formatted before it reaches a catalog string (#650). The
  // unrecorded arm carries no count at all — that is the point of #780 — and
  // the partial arm says its total is a floor rather than the day's output.
  const trendTip = (slot: DayStripSlot) => {
    const date = fmt.date(slot.date);
    switch (slot.kind) {
      case "none":
        return t("trendDayTipNoFlocks", { date });
      case "unrecorded":
        return t("trendDayTipNone", { date });
      case "partial":
        // Plural on the EGG count, which is what the noun beside it is. It
        // selected on the flock count, so a partly recorded day with one egg
        // rendered "1 eggs". The flock noun stays plural unconditionally and is
        // safe there: `partial` requires 1 <= recorded < expected, so `expected`
        // is never below 2.
        return t("trendDayTipPartial", {
          date, count: slot.eggs, total: fmt.count(slot.eggs),
          recorded: fmt.count(slot.recordedFlocks), expected: fmt.count(slot.expectedFlocks),
        });
      case "recorded":
        return t("trendDayTip", { date, count: slot.eggs, total: fmt.count(slot.eggs) });
      default: {
        const _exhaustive: never = slot;
        return _exhaustive;
      }
    }
  };
  const deltaClass = (delta: number | null) =>
    delta === null || delta === 0 ? "trend-delta" : delta < 0 ? "trend-delta is-down" : "trend-delta is-up";

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
                // No action here: the panel's own h3 already links to
                // /daily-entry, and there is no panel-local create handler to
                // reuse without duplicating one.
                <EmptyState icon={Bird} message={t("noFlocksMessage")} />
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
              <DayStrip
                data={trendData.line}
                label={trendLabel(trendData.line)}
                title={t("trendScaleTitle")}
                peak={trendData.line.max === null ? "—" : t("trendPeak", { total: fmt.count(trendData.line.max) })}
                average={trendData.line.average === null
                  ? null
                  : t("trendAvg", { total: fmt.count(trendData.line.average, 1) })}
                tip={trendTip}
                from={<FarmDate iso={daysBefore(today, 14)} />}
                to={<FarmDate iso={daysBefore(today, 1)} />}
              />
              {/* #777 — hen-day is what this panel measures, so it is a figure.
                  It used to be the smallest text on the panel, inside a muted
                  sentence describing a quantity the chart above did not plot. */}
              <p className="trend-kpi">
                <span className="trend-fig">
                  {trendData.henDay.current === null ? "—" : `${fmt.count(trendData.henDay.current, 1)}%`}
                </span>
                <span className={deltaClass(trendData.henDay.delta)}>{deltaText(trendData.henDay.delta)}</span>
              </p>
              <p className="trend-sub">{t("henDaySubLabel")}</p>
            </>
          )}
        </div>

        <div className="panel">
          <h3><Link to="/stock">{t("stockPanelTitle")}</Link></h3>
          {bar === null || stock === null ? panelError : stock.length === 0 ? (
            <EmptyState icon={Egg} message={t("noStockMessage")} />
          ) : (
            <>
              {/* #777 — the total is the whole the bar divides, so it leads. */}
              <p className="stock-total">
                <span className="stock-fig">{fmt.count(bar.totalAvailable)}</span>
                {/* The space matters: .stock-fig is display:block so the two
                    never touch on screen, but the paragraph's text is what a
                    copy-paste and any text consumer gets, and without it that
                    reads "1egg available". */}
                {" "}{t("eggsAvailableLabel", { count: bar.totalAvailable })}
              </p>
              <StockBar data={bar} />
              {/* The ledger is the bar's text of record (the track itself is
                  aria-hidden). It replaces the grade run-on the caption used to
                  carry, which asked the reader to count segments and trust the
                  order matched, and it carries each grade's share so a grade
                  worth well under a percent is readable as a number. */}
              {bar.segments.length > 0 && (
                <ul className="stock-ledger" aria-label={t("stockLedgerLabel")}>
                  {bar.segments.map((s) => (
                    <li key={s.eggGradeId}>
                      <span className={`swatch grade-${s.colorIndex}`} aria-hidden="true" />
                      <span className="name">{s.gradeName}</span>
                      <span className="count">{fmt.count(s.available)}</span>
                      <span className="share">{`${fmt.count(s.pct, 1)}%`}</span>
                    </li>
                  ))}
                </ul>
              )}
              {bar.totalRestricted > 0 && (
                <p className="muted">{t("stockCaptionRestricted", { restricted: fmt.count(bar.totalRestricted) })}</p>
              )}
            </>
          )}
        </div>

        {canSeeSales && (
        <div className="panel">
          <h3><Link to="/sales">{t("salesPanelTitle")}</Link></h3>
          {orders === null ? panelError : orders.length === 0 ? (
            <EmptyState icon={ShoppingCart} message={t("noOrdersMessage")} />
          ) : (
            <ul className="dash-list">
              {orders.map((o) => (
                <li key={o.id} aria-label={o.referenceNumber}>
                  <span className="ref">{o.referenceNumber}</span>
                  {/* #512 US5 (FR-045) — authorized (canSeeSales, the gate this
                      whole panel is already behind) link into URL-filtered
                      Sales by canonical id; the name itself is row-owned. */}
                  <Link className="link cust" to={`/sales?customerId=${o.customerId}`}>{rowCustomerName(o)}</Link>
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
