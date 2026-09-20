// web/src/routes/Dashboard.tsx
import { useEffect, useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Bird, Check, CircleDashed, Egg, ShoppingCart, TriangleAlert } from "lucide-react";
import {
  Alert, Box, Button, Card, Container, LinearProgress, Table, TableBody, TableCell, TableHead, TableRow, Typography, useMediaQuery,
} from "@mui/material";
import {
  getProductionReport, getStock, listDailyEntries, listFlocks, listOrders,
} from "../api/cluckwork";
import type { DailyEntry, Flock, ProductionReport, SalesOrder, StockRow } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useFormat } from "../farm/useFormat";
import { FarmDate } from "../components/FarmDate";
import { EmptyState } from "../components/EmptyState";
import { DayStrip } from "../components/DayStrip";
import { StockBar } from "../components/StockBar";
import { FlockPicker } from "../components/FlockPicker";
import { useAuth } from "../auth/useAuth";
import { useFarm, useFarmToday } from "../farm/useFarm";
import { daysBefore } from "../lib/dates";
import { MD_UP_QUERY } from "../lib/breakpoints";
import {
  captureTiles, dayStrip, henDayTrend, stockBar, todaysEggs, visibleTiles,
} from "../lib/dashboard";
import type { CaptureTile, DayStripData, DayStripSlot } from "../lib/dashboard";
import i18n from "../i18n";
import { statusLabel } from "../i18n/enums";

const RECENT_ORDERS = 5;
// Server clamps list limits at 500. One farm won't exceed that in Phase 1.x;
// past 500 flocks the tail silently drops — revisit with real paging if that
// day comes.
const MAX_PAGE = 500;

// #916 — the Lay rate card's own scope, independent of every other panel.
// Plain page state (not derived from anything that resets on reload) so
// #914's range control can sit beside it later without clearing the choice.
type ProductionScope = { kind: "all" } | { kind: "flock"; flock: Flock };

// Six parallel reads; failed panels degrade independently. The server owns
// the hen-day calculation for each seven-day reporting window.
export function Dashboard() {
  const { t } = useTranslation("dashboard");
  const fmt = useFormat();
  const { farm } = useFarm();
  const [openedAt] = useState(() => new Date().toISOString());
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

  // #916 — the Lay rate card's own scope. `trendLoading` is separate from the
  // page's `loading` above: a scope change refetches ONLY the two
  // production-report calls (never the other panels), and must not blank the
  // whole page while that refetch is in flight — "Other dashboard panels do
  // not change" (SELECTION.md). A failed refetch clears `trend` to null,
  // which already renders `panelError` below — no separate error flag needed.
  const [scope, setScope] = useState<ProductionScope>({ kind: "all" });
  const [trendLoading, setTrendLoading] = useState(true);
  const [flockPickerOpen, setFlockPickerOpen] = useState(false);
  // Mirrors the engine's own committed entity so the CLOSED-state trigger can
  // show it (same T036 pattern DailyEntryPage's FlockPicker uses), and so an
  // external reset (the All flocks button) can clear the engine's retained
  // name via `controlledCommitted`/`controlledGeneration` without it lingering
  // the next time the search reopens.
  const [pickerFlock, setPickerFlock] = useState<Flock | null>(null);
  const [pickerFlockGen, setPickerFlockGen] = useState(0);

  // PROTECTED (INV-2, #127) — copied verbatim; do not edit.
  // ReadOnly/Denied can't read customers or orders — the API now returns 403
  // (#127) — so skip those two fetches and hide the sales panel, matching the
  // nav's own gate. Fetching them anyway would blank the panel with an error.
  const { role } = useAuth();
  const canSeeSales = role !== "ReadOnly" && role !== "Denied";
  // END PROTECTED

  // Match the navigation breakpoint when folding the missing-house summary.
  const isDesktop = useMediaQuery(MD_UP_QUERY);
  const attentionCap = isDesktop ? 2 : 1;

  // #916 — a PRIMITIVE, not the `flocks` array itself: the trend effect below
  // depends on this so a farm with more than one flock (the common case)
  // re-renders `flocks` exactly once (on load) without re-triggering a second,
  // wasted report fetch — the array's reference changes every time, but this
  // value only changes on the one transition that actually matters (null →
  // an id, when the farm turns out to have exactly one accessible flock).
  const soleFlockId = flocks !== null && flocks.length === 1 ? flocks[0].id : null;

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
      // Only the fetches we actually issued count toward "everything failed":
      // the sales read is an inert placeholder when the role can't see it.
      const issued = canSeeSales ? [f, e, s, o] : [f, e, s];
      const rejected = issued.filter((r): r is PromiseRejectedResult => r.status === "rejected");
      const firstRejected = rejected[0];
      if (rejected.length === issued.length && firstRejected) {
        const reason = firstRejected.reason;
        setError(reason instanceof ApiError ? reason.message : i18n.t("dashboard:loadFailed"));
      }
      setLoading(false);
    });
  }, [today, canSeeSales]);

  // #916 — the production report alone, re-run on every scope change (and on
  // `today`/`soleFlockId`, exactly like the effect above). Separate from the
  // effect above so choosing a flock never re-fetches the other four panels
  // ("Other dashboard panels do not change", SELECTION.md). `cancelled` drops
  // a superseded response: a slow "all flocks" request landing after a faster
  // later scope change must not overwrite it.
  useEffect(() => {
    // With exactly one accessible flock there is no All-flocks scope to
    // offer, so this always reports that flock — the SAME `flockId` a picker
    // selection would produce, never a separate "everything" branch (parity
    // requirement, SELECTION.md).
    const flockId = soleFlockId ?? (scope.kind === "flock" ? scope.flock.id : undefined);
    let cancelled = false;
    setTrendLoading(true);
    Promise.allSettled([
      // The last 7 complete days and the 7 before them — yesterday back, so an
      // unsubmitted today never ends the line in a false dip (owner decision A).
      getProductionReport(daysBefore(today, 7), daysBefore(today, 1), flockId),
      getProductionReport(daysBefore(today, 14), daysBefore(today, 8), flockId),
    ]).then(([cur, prev]) => {
      if (cancelled) return;
      setTrend(cur.status === "fulfilled" && prev.status === "fulfilled"
        ? { current: cur.value, previous: prev.value }
        : null);
      setTrendLoading(false);
    });
    return () => { cancelled = true; };
  }, [today, scope, soleFlockId]);

  // #512 US4 — a recent-sales row's own name: the row-owned `customerName` the
  // endpoint's scoped bulk read already resolved, or the translated
  // unavailable label. Never an id fragment.
  const rowCustomerName = (o: { customerName?: string | null }) =>
    o.customerName ?? t("rowCustomerUnavailable");

  if (loading) {
    return (
      <Container maxWidth={false} disableGutters sx={{ maxWidth: 1120 }}>
        <Typography variant="h2">{t("title")}</Typography>
        <Typography className="muted">{tc("loading")}</Typography>
      </Container>
    );
  }
  if (error) {
    return (
      <Container maxWidth={false} disableGutters sx={{ maxWidth: 1120 }}>
        <Typography variant="h2">{t("title")}</Typography>
        <Alert severity="error" className="error">{error}</Alert>
      </Container>
    );
  }

  const panelError = <Alert severity="error" className="error">{t("panelLoadError")}</Alert>;
  // The FULL capture-status list, uncapped — the attention line and the "N of
  // M houses in" caption must count every active flock, not only the 12
  // `visibleTiles` caps the RENDERED row list at. A farm with more than 12
  // missing houses undercounted both on the capped list (CodeRabbit, #883).
  const allTiles = flocks !== null && entries !== null ? captureTiles(flocks, entries) : null;
  const tiles = allTiles === null ? null : visibleTiles(allTiles);

  // #916 — with exactly one accessible flock there is no All-flocks concept
  // to offer (SELECTION.md), and its figures must come from the SAME code
  // path as picking that flock out of a larger list — so `soleFlock` folds
  // into the same `flockId` the fetch effect and every render below read,
  // rather than being a second, parallel "just show everything" branch.
  const soleFlock = flocks !== null && flocks.length === 1 ? flocks[0] : null;
  const scopedFlock = soleFlock ?? (scope.kind === "flock" ? scope.flock : null);
  const scopeName = scopedFlock ? scopedFlock.name : t("allFlocksOption");

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
  // A missing day is not zero production; its accessible label must say so.
  // #916 — branches on `line.scale`, never on a null check: `max` is non-null
  // under BOTH "complete" and the "partial" fallback, so a null check alone
  // used to say "no peak or average" over a window whose caption and Peak
  // figure, right beside it, were showing a real number.
  const trendLabel = (line: DayStripData) => {
    if (line.scale === "none") {
      // A window where no flock ever owed a filing is not a window of missing
      // ones. The day-level fix for that landed without this, so a new farm's
      // strip drew fourteen blank-but-blameless slots and then announced that
      // none of them had an entry. `partial` is always 0 here — a partial slot
      // requires a recorded figure, which is exactly what "none" has none of.
      return line.partial === 0 && line.unrecorded === 0
        ? t("trendStripLabelNoFlocks")
        : t("trendStripLabelNone");
    }
    if (line.scale === "partial") {
      // The fallback peak: no complete day exists, so there is still no
      // average (that stays complete-day-only), but Peak is real and the
      // sentence must say so, not fall back to "no peak or average".
      return t("trendStripLabelPartialScale", { max: fmt.count(line.max!) });
    }
    const figures = { max: fmt.count(line.max!), avg: fmt.count(line.average!, 1) };
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
        return t("trendDayTipPartial", {
          date, count: slot.eggs, total: fmt.count(slot.eggs),
          recorded: fmt.count(slot.filedFlocks), expected: fmt.count(slot.expectedFlocks),
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

  // Only a complete yesterday can be described as its closing total.
  const yesterdaySlot = trendData?.line.slots.at(-1) ?? null;
  const yesterdayByClose = yesterdaySlot?.kind === "recorded" ? yesterdaySlot.eggs : null;

  // The attention line (D3.3, #829): missing houses only — the desktop-only
  // "Needs attention" list combining a second data source (stock floors) was
  // proposed on #864 and the owner did not take it, so this line has exactly
  // one source. Nothing renders when every house is in.
  const missingHouses = allTiles === null ? [] : allTiles.filter((c) => c.entry === null).map((c) => c.flock);
  const attentionShown = missingHouses.slice(0, attentionCap);
  const attentionMore = missingHouses.length - attentionShown.length;

  const recordedHouses = allTiles === null ? 0 : allTiles.length - missingHouses.length;
  const sectionSx = { p: { xs: 2, md: 2.25 }, minWidth: 0, borderColor: "var(--rule)", borderRadius: "var(--r-panel)" };
  const headingSx = { "& h3": { fontSize: "0.9rem", fontWeight: 700 }, display: "flex", alignItems: "center", justifyContent: "space-between", gap: 1, mb: 1.5 };

  return (
    <Container maxWidth={false} disableGutters sx={{
      maxWidth: 1120, py: { xs: 2.5, md: 3 },
      "& a": { minHeight: { xs: 44, md: "auto" }, display: "inline-flex", alignItems: "center" },
    }}>
      <Box component="header" sx={{ display: "flex", justifyContent: "space-between", alignItems: "end", gap: 2, mb: 2.5 }}>
        <Box>
          <Typography variant="caption" sx={{ color: "text.secondary", fontWeight: 600 }}>{farm?.name ?? t("title")}</Typography>
          <Typography variant="h2" aria-label={t("title")} sx={{ maxWidth: 650, mt: 0.5, fontSize: { xs: "1.75rem", md: "1.9rem" }, lineHeight: 1.15 }}>{t("morningHeading")}</Typography>
        </Box>
        <Typography variant="caption" color="text.secondary" sx={{ textAlign: "right", flexShrink: 0, display: { xs: "none", md: "block" } }}>
          <FarmDate iso={today} /><br />{t("farmTime", { time: fmt.time(openedAt) ?? "—" })}
        </Typography>
      </Box>
      <Typography variant="caption" color="text.secondary" sx={{ display: { xs: "block", md: "none" }, mb: 2 }}>
        <FarmDate iso={today} /> · {t("farmTime", { time: fmt.time(openedAt) ?? "—" })}
      </Typography>

      <Box component="section" aria-label={t("morningBrief")} sx={{
        display: "grid", gridTemplateColumns: { xs: "1fr", md: "minmax(0,1fr) auto" }, gap: 2,
        p: 2.5, mb: 3, borderRadius: "var(--r-panel)", bgcolor: "#2b2328", color: "#fffaf4",
      }}>
        <Box>
          <Typography variant="h3" sx={{ fontFamily: "Georgia, serif", fontSize: "1.4rem", mb: 1 }}>{t("morningBrief")}</Typography>
          {allTiles === null ? <Typography>{t("panelLoadError")}</Typography> : missingHouses.length > 0 ? (
            <Box sx={{ display: "flex", flexWrap: "wrap", columnGap: 2, rowGap: 0.5 }}>
              {attentionShown.map((flock) => (
                <Typography key={flock.id} sx={{ display: "flex", alignItems: "center", gap: 1 }}>
                  <TriangleAlert size={15} aria-hidden />{t("attentionHouseNotRecorded", { flock: flock.name })}
                </Typography>
              ))}
              {attentionMore > 0 && <Typography component={Link} to="/daily-entry" sx={{ color: "inherit" }}>{t("attentionMore", { count: attentionMore })}</Typography>}
            </Box>
          ) : <Typography>{t(allTiles.length === 0 ? "noFlocksMessage" : "allHousesRecorded")}</Typography>}
        </Box>
        {allTiles !== null && entries !== null && <Box sx={{ borderLeft: { md: "1px solid #62535e" }, borderTop: { xs: "1px solid #62535e", md: 0 }, pl: { md: 2.5 }, pt: { xs: 1.5, md: 0 }, minWidth: 150 }}>
          <Typography variant="caption">{t("todaySoFarLabel")}</Typography>
          <Typography className="num" sx={{ fontFamily: "Georgia, serif", fontSize: "2rem", fontWeight: 600, lineHeight: 1.15 }}>
            {entries === null ? "—" : fmt.count(todaysEggs(entries))}
          </Typography>
        </Box>}
      </Box>

      <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", md: "minmax(0,1.6fr) minmax(300px,1fr)" }, gap: 2.5, alignItems: "start" }}>
        <Card component="section" sx={sectionSx}>
          <Box sx={headingSx}>
            <Typography variant="h3" aria-label={t("todayPanelTitle")}><Link to="/daily-entry">{t("collectionTitle")}</Link></Typography>
            {allTiles !== null && <Typography variant="caption" color="text.secondary" sx={{ textAlign: "right" }}>{t("todayInCount", { in: recordedHouses, count: allTiles.length })}</Typography>}
          </Box>
          {tiles === null || entries === null ? panelError : tiles.shown.length === 0 ? (
            <EmptyState icon={Bird} message={t("noFlocksMessage")} />
          ) : (
            <>
              <LinearProgress variant="determinate" value={recordedHouses / (tiles.shown.length + tiles.hidden) * 100} aria-label={t("collectionTitle")}
                sx={{ height: 5, borderRadius: 2, mb: 1, bgcolor: "var(--surface-2)", "& .MuiLinearProgress-bar": { bgcolor: "var(--success)" } }} />
              {tiles.shown.map((tile) => <TodayRow key={tile.flock.id} tile={tile} today={today} fmt={fmt} t={t} />)}
              <Box sx={{ bgcolor: "var(--surface-2)", mx: { xs: -2, md: -2.25 }, mb: { xs: -2, md: -2.25 }, mt: 1.5, px: 2.25, py: 1.5 }}>
                <Box sx={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
                  <Typography>{t("collectedToday")}</Typography>
                  <Typography className="num" sx={{ fontFamily: "Georgia, serif", fontWeight: 600, fontSize: "1.8rem" }}>{fmt.count(todaysEggs(entries))}</Typography>
                </Box>
                {yesterdayByClose !== null && <Typography variant="caption" color="text.secondary">{t("yesterdayByClose", { total: fmt.count(yesterdayByClose) })}</Typography>}
                {tiles.hidden > 0 && <Typography component={Link} to="/daily-entry" variant="body2" sx={{ display: "block" }}>{t("moreFlocks", { count: tiles.hidden, total: fmt.count(tiles.hidden) })}</Typography>}
              </Box>
            </>
          )}
        </Card>

        <Card component="section" sx={{ ...sectionSx, gridColumn: { md: 2 }, gridRow: { md: 1 } }}>
          <Box sx={headingSx}>
            <Typography variant="h3" aria-label={t("stockPanelTitle")}><Link to="/stock">{t("availableStockTitle")}</Link></Typography>
          </Box>
          {bar === null || stock === null ? panelError : stock.length === 0 ? <EmptyState icon={Egg} message={t("noStockMessage")} /> : (
            <>
              <Typography className="stock-total" sx={{ display: "flex", alignItems: "baseline", justifyContent: "space-between", gap: 1, mb: 2,
                "& .stock-fig": { fontFamily: "Georgia, serif", fontSize: "2.5rem" } }}>
                <span className="stock-fig">{fmt.count(bar.totalAvailable)}</span>{" "}{t("eggsAvailableLabel", { count: bar.totalAvailable })}
              </Typography>
              <StockBar data={bar} />
              {bar.segments.length > 0 && (
                <Table aria-label={t("stockLedgerLabel")} size="small" sx={{ mt: 1.5, tableLayout: "fixed", "& th, & td": { px: 0.75, py: 1, overflowWrap: "anywhere" } }}>
                  <TableHead><TableRow>
                    <TableCell sx={{ width: "46%" }}>{t("gradeColumn")}</TableCell>
                    <TableCell align="right">{t("countColumn")}</TableCell>
                    <TableCell align="right">{t("shareColumn")}</TableCell>
                  </TableRow></TableHead>
                  <TableBody>{bar.segments.map((s) => (
                    <TableRow key={s.eggGradeId} tabIndex={0} sx={{
                      "&:hover, &:focus": { bgcolor: "var(--surface-2)", outline: "2px solid var(--focus)", outlineOffset: -2 },
                      "&:hover th, &:focus th": { textDecoration: "underline", textUnderlineOffset: "3px" },
                    }}>
                      <TableCell component="th" scope="row"><Box component="span" sx={{ display: "inline-flex", alignItems: "center", gap: 1 }}><span className={`swatch grade-${s.colorIndex}`} aria-hidden="true" />{s.gradeName}</Box></TableCell>
                      <TableCell align="right">{fmt.count(s.available)}</TableCell>
                      <TableCell align="right">{`${fmt.count(s.pct, 1)}%`}</TableCell>
                    </TableRow>
                  ))}</TableBody>
                </Table>
              )}
              {bar.totalRestricted > 0 && <Typography color="text.secondary" variant="caption" sx={{ display: "block", mt: 1 }}>{t("stockCaptionRestricted", { restricted: fmt.count(bar.totalRestricted) })}</Typography>}
            </>
          )}
        </Card>

        {canSeeSales && <Card component="section" sx={{ ...sectionSx, gridColumn: { md: 1 } }}>
          <Box sx={headingSx}><Typography variant="h3" aria-label={t("salesPanelTitle")}><Link to="/sales">{t("recentOrdersTitle")}</Link></Typography></Box>
          {orders === null ? panelError : orders.length === 0 ? <EmptyState icon={ShoppingCart} message={t("noOrdersMessage")} /> : (
            <Box component="ul" role="list" aria-label={t("salesPanelTitle")} className="dash-sales-list" sx={{ listStyle: "none", m: 0, p: 0 }}>
              {orders.map((o) => <Box component="li" key={o.id} aria-label={o.referenceNumber} sx={{
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
          )}
        </Card>}

        <Card component="section" sx={{ ...sectionSx, gridColumn: { md: 2 }, gridRow: { md: 2 },
          mx: { xs: "calc(7px - 1.15rem)", md: 0 },
          "& .trend-fig": { fontFamily: "Georgia, serif", fontSize: "2.5rem" },
          "& .trend-kpi": { mt: 0, mb: 1 },
          "& .daystrip": { display: "flex", gap: { xs: "2px", md: "4px" }, height: 80 },
          "& .day": { height: 80 },
          "& .day > i": { maxWidth: { xs: "none", md: 18 } },
          "& .tipdock .tip": { whiteSpace: "normal", overflow: "visible", maxWidth: "100%" },
        }}>
          <Box sx={headingSx}>
            <Typography variant="h3" aria-label={t("trendPanelTitle")}><Link to="/reports">{t("layRateTitle")}</Link></Typography>
            <Typography variant="caption" color="text.secondary">{t("trendPanelTitle")}</Typography>
          </Box>
          {/* #916 — the card's own flock scope. Exactly one accessible flock:
              plain text, no dropdown (SELECTION.md). More than one: a fast
              "All flocks" toggle beside the searchable picker, so All stays
              reachable without opening the search surface at all — which
              also satisfies SELECTION.md's "All flocks stays available above
              the scrolling results" without adding a synthetic option inside
              the shared picker's own results list. */}
          {soleFlock !== null ? (
            <Typography sx={{ fontWeight: 600, mb: 1.5 }}>{soleFlock.name}</Typography>
          ) : flocks !== null && flocks.length > 1 ? (
            <Box sx={{ display: "flex", alignItems: "center", gap: 1, mb: 1.5, flexWrap: "wrap" }}>
              <Button
                type="button" size="small" color="inherit"
                variant={scope.kind === "all" ? "contained" : "outlined"}
                aria-pressed={scope.kind === "all"}
                onClick={() => {
                  setScope({ kind: "all" });
                  setPickerFlock(null);
                  setPickerFlockGen((g) => g + 1);
                }}
                sx={{ "&&": { minHeight: 44 } }}
              >
                {t("allFlocksOption")}
              </Button>
              <Box sx={{ minWidth: 160, flexGrow: 1 }}>
                <FlockPicker
                  label={t("flockScopeLabel")}
                  eligibility="active-and-depleted"
                  open={flockPickerOpen}
                  controlledCommitted={pickerFlock}
                  controlledGeneration={pickerFlockGen}
                  onSnapshot={() => {}}
                  onCommit={(f) => {
                    setScope({ kind: "flock", flock: f });
                    setPickerFlock(f);
                    setPickerFlockGen((g) => g + 1);
                    setFlockPickerOpen(false);
                  }}
                  onEscape={() => setFlockPickerOpen(false)}
                  onOutsideClick={() => setFlockPickerOpen(false)}
                  trigger={
                    <button
                      type="button"
                      className="named-picker-trigger"
                      onClick={() => setFlockPickerOpen(true)}
                    >
                      {scopeName}
                    </button>
                  }
                />
              </Box>
            </Box>
          ) : null}
          {trendLoading ? <LinearProgress aria-label={t("trendPanelTitle")} sx={{ height: 5, borderRadius: 2, mb: 1 }} />
            : trendData === null ? panelError : <>
              <Typography className="trend-kpi"><span className="trend-fig">{trendData.henDay.current === null ? "—" : `${fmt.count(trendData.henDay.current, 1)}%`}</span><span className={deltaClass(trendData.henDay.delta)}>{deltaText(trendData.henDay.delta)}</span></Typography>
              <Typography className="trend-sub" variant="caption" sx={{ display: "block", mb: 1.5 }}>{t("henDaySubLabel")}</Typography>
              <DayStrip data={trendData.line} label={trendLabel(trendData.line)}
                title={t(trendData.line.scale === "partial" ? "trendScaleTitlePartial" : "trendScaleTitle")}
                peak={trendData.line.max === null ? "—" : t("trendPeak", { total: fmt.count(trendData.line.max) })}
                average={trendData.line.average === null ? null : t("trendAvg", { total: fmt.count(trendData.line.average, 1) })}
                tip={trendTip} from={<FarmDate iso={daysBefore(today, 14)} />} to={<FarmDate iso={daysBefore(today, 1)} />} />
          </>}
        </Card>
      </Box>
    </Container>
  );
}

function TodayRow({ tile, today, fmt, t }: {
  tile: CaptureTile;
  today: string;
  fmt: ReturnType<typeof useFormat>;
  t: ReturnType<typeof useTranslation<"dashboard">>["t"];
}) {
  const { flock, entry } = tile;
  const href = `/daily-entry?flockId=${flock.id}&date=${today}`;
  const missing = entry === null;
  const draft = entry !== null && entry.status === "Draft";

  // Drafts use their last save; official entries use their submission time.
  // Older records without provenance retain the bare status label.
  const stateTime = missing
    ? null
    : fmt.time(draft ? (entry.lastChangedAtUtc ?? entry.createdAtUtc) : (entry.madeOfficialAtUtc ?? null));
  const stateLabel = missing || stateTime === null
    ? (missing ? undefined : statusLabel(entry.status))
    : t(draft ? "entryStateDraftTime" : "entryStateRecordedTime", { time: stateTime });

  return (
    <Box role="group" aria-label={flock.name} sx={{
      display: "grid", gridTemplateColumns: "28px minmax(0,1fr) auto", gap: 1.25,
      alignItems: "center", minHeight: 66, py: 1, borderTop: "1px solid var(--rule)",
    }}>
      <Box aria-hidden sx={{ display: "grid", placeItems: "center", width: 26, height: 26, borderRadius: "50%",
        color: missing ? "var(--warn)" : draft ? "var(--muted)" : "var(--success)",
        bgcolor: missing ? "var(--tint-warn)" : draft ? "var(--surface-2)" : "var(--tint-ok)",
      }}>{missing ? <TriangleAlert size={15} /> : draft ? <CircleDashed size={15} /> : <Check size={15} />}</Box>
      <Box sx={{ minWidth: 0, overflowWrap: "anywhere" }}>
        <Typography component={Link} to={href} sx={{ fontWeight: 600 }}
          aria-label={missing ? t("tileLinkLabelMissing", { flock: flock.name }) : t("tileLinkLabel", { flock: flock.name })}
          title={missing ? t("recordTodayHint") : undefined}>{flock.name}</Typography>
        <Box>{missing ? <StatusDot label={t("noEntryBadge")} forceColor="var(--warn)" /> : <StatusDot status={entry.status} label={stateLabel} />}</Box>
        {draft && <Typography component={Link} to={href} variant="body2">{t("continueHouseAction", { flock: flock.name })}</Typography>}
      </Box>
      {missing ? <Button component={Link} to={href} variant="outlined" color="inherit" size="small"
        aria-label={t("recordHouseAction", { flock: flock.name })}
        sx={{ gridColumn: "3", gridRow: "1", "&&": { minHeight: 44 } }}>{t("recordAction")}</Button> : <Box sx={{ textAlign: "right" }}>
        <Typography component="span" className="num" sx={{ fontFamily: "Georgia, serif", fontSize: "1.4rem", fontWeight: 600 }}>{fmt.count(entry.totalEggs)}</Typography>
      </Box>}
    </Box>
  );
}

// A hollow dot marks states without a semantic colour.
const STATUS_DOT_COLOR: Record<string, string> = {
  active: "var(--success)", submitted: "var(--success)", confirmed: "var(--success)",
  saleable: "var(--success)", paid: "var(--success)",
  locked: "var(--stat-accent)",
  manageradjusted: "var(--warn)", adjusted: "var(--warn)", partial: "var(--warn)",
  voided: "var(--error)", cancelled: "var(--error)", inactive: "var(--error)", denied: "var(--error)",
};

function StatusDot({ status, label, forceColor }: { status?: string; label?: string; forceColor?: string }) {
  const color = forceColor ?? (status ? STATUS_DOT_COLOR[status.toLowerCase()] : undefined);
  return (
    <Box component="span" sx={{ display: "inline-flex", alignItems: "center", gap: 0.75 }}>
      <Box component="span" aria-hidden sx={color
        ? { width: 8, height: 8, borderRadius: "50%", bgcolor: color, flexShrink: 0 }
        : { width: 8, height: 8, borderRadius: "50%", border: "1.5px solid var(--muted)", boxSizing: "border-box", flexShrink: 0 }}
      />
      <Typography component="span" variant="body1">{label ?? status}</Typography>
    </Box>
  );
}
