// web/src/routes/Dashboard.tsx
import { useEffect, useId, useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Bird, Check, ChevronRight, CircleDashed, Egg, ShoppingCart, TriangleAlert } from "lucide-react";
import {
  Alert, Box, Button, Card, Container, LinearProgress, Table, TableBody, TableCell, TableHead, TableRow, Tooltip, Typography, useMediaQuery,
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
import { Dialog } from "../components/Dialog";
import { FlockPicker } from "../components/FlockPicker";
import { useAuth } from "../auth/useAuth";
import { useFarm, useFarmToday } from "../farm/useFarm";
import { daysBefore } from "../lib/dates";
import { MD_UP_QUERY } from "../lib/breakpoints";
import {
  captureTiles, dayStrip, henDayTrend, stockBar, todaysEggs, visibleTiles,
} from "../lib/dashboard";
import type { CaptureTile, DayStripData, DayStripSlot } from "../lib/dashboard";
import { splitProductionReport } from "../lib/productionReportSplit";
import i18n from "../i18n";
import { statusLabel } from "../i18n/enums";

const RECENT_ORDERS = 5;
// Server clamps list limits at 500. One farm won't exceed that in Phase 1.x;
// past 500 flocks the tail silently drops — revisit with real paging if that
// day comes.
const MAX_PAGE = 500;

// #916 — the Lay rate card's own scope, independent of every other panel.
type FlockScope = { kind: "all" } | { kind: "flock"; flock: Flock };

export const DASHBOARD_DELTA_CLASSES = ["trend-delta", "trend-delta is-down", "trend-delta is-up"] as const;

// Six parallel reads; failed panels degrade independently. The server owns
// the hen-day calculation for each seven-day reporting window.
export function Dashboard() {
  const { t } = useTranslation("dashboard");
  const fmt = useFormat();
  const { farm } = useFarm();
  const [openedAt] = useState(() => new Date().toISOString());
  const { t: tc } = useTranslation("common");
  // Retry/loading labels come from the shared picker catalog.
  const { t: tp } = useTranslation("namedEntityPicker");
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
  // Yesterday's farm-wide close, for the Morning collection caption below —
  // deliberately its OWN fetch, never `flockId`-scoped: SELECTION.md's "Other
  // dashboard panels do not change" means this must read the same regardless
  // of the Lay rate card's scope, which `trend` (scoped) cannot give it.
  const [yesterdayClose, setYesterdayClose] = useState<number | null>(null);

  // #916 — `trendLoading` is separate from `loading`: a scope change refetches
  // only the two production-report calls and must not blank the whole page
  // (SELECTION.md). A failed refetch clears `trend` to null, which already
  // renders `panelError` below; the dialog owns its own state, only `open` lives here.
  const [scope, setScope] = useState<FlockScope>({ kind: "all" });
  const [trendLoading, setTrendLoading] = useState(true);
  const [pickerOpen, setPickerOpen] = useState(false);
  // #918 — the flock LIST read can fail alone while the other three panels
  // succeed; "failed" must not read as "0 accessible flocks". `flocksRetrying`
  // holds the unavailable state up until the retried read SETTLES — clearing
  // `flocksFailed` on click showed that same false zero in the gap before.
  const [flocksFailed, setFlocksFailed] = useState(false);
  const [flocksRetrying, setFlocksRetrying] = useState(false);

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

  const scopeLabelId = useId();
  const scopeValueId = useId();

  // #916 — with one accessible flock there is no All-flocks concept (SELECTION.md);
  // its figures must come from the SAME code path as picking that flock from a
  // larger list, never a parallel branch. The trend effect depends on the id, not
  // `soleFlock` itself — the array's reference changes every render, the id does not.
  const soleFlock = flocks !== null && flocks.length === 1 ? flocks[0] : null;
  const soleFlockId = soleFlock?.id ?? null;

  // #918 — "everything failed" spans BOTH effects (panels + trend). Each
  // side tracks its OWN outcome directly rather than through a generation
  // counter: `panelsOutcome` resets to "pending" the instant a new panels
  // batch starts and `trendOutcome` resets the instant a new trend fetch
  // starts, so the derived verdict below only ever reads two CURRENT
  // answers, never a stale one paired with a fresh one. Retry (`fetchFlocks`)
  // updates `panelsOutcome` too — the prior ref-based design left a stale
  // "all four failed" outcome in place after a successful retry, hiding a
  // dashboard that had actually recovered.
  type PanelsOutcome = { state: "pending" } | { state: "someOk" } | { state: "allFailed"; reason: unknown };
  const [panelsOutcome, setPanelsOutcome] = useState<PanelsOutcome>({ state: "pending" });
  const [trendOutcome, setTrendOutcome] = useState<"pending" | "success" | "failure">("pending");
  const errorMessage = panelsOutcome.state === "allFailed" && trendOutcome === "failure"
    ? (panelsOutcome.reason instanceof ApiError ? panelsOutcome.reason.message : i18n.t("dashboard:loadFailed"))
    : null;

  // Retry re-issues ONLY the flock list; the dialog's own discovery is a
  // separate server call and is unaffected either way. A success proves at
  // least one panel now has data, whatever the other three are doing.
  const fetchFlocks = () => {
    setFlocksRetrying(true);
    listFlocks({ limit: MAX_PAGE })
      .then((f) => {
        setFlocks(f); setFlocksFailed(false); setFlocksRetrying(false);
        setPanelsOutcome({ state: "someOk" });
      })
      .catch(() => { setFlocksFailed(true); setFlocksRetrying(false); });
  };

  useEffect(() => {
    let cancelled = false;
    setPanelsOutcome({ state: "pending" }); // a fresh load starts clean, never on a stale verdict
    Promise.allSettled([
      listFlocks({ limit: MAX_PAGE }),
      listDailyEntries({ from: today, to: today, limit: MAX_PAGE }),
      getStock(),
      canSeeSales ? listOrders({ limit: RECENT_ORDERS }) : Promise.resolve<SalesOrder[]>([]),
    ]).then(([f, e, s, o]) => {
      if (cancelled) return;
      if (f.status === "fulfilled") { setFlocks(f.value); setFlocksFailed(false); } else { setFlocksFailed(true); }
      if (e.status === "fulfilled") setEntries(e.value);
      if (s.status === "fulfilled") setStock(s.value);
      if (o.status === "fulfilled") setOrders(o.value);
      // Only the fetches we actually issued count toward "everything failed":
      // the sales read is an inert placeholder when the role can't see it.
      const issued = canSeeSales ? [f, e, s, o] : [f, e, s];
      const rejected = issued.filter((r): r is PromiseRejectedResult => r.status === "rejected");
      setPanelsOutcome(rejected.length === issued.length
        ? { state: "allFailed", reason: rejected[0]?.reason }
        : { state: "someOk" });
      setLoading(false);
    });
    return () => { cancelled = true; };
  }, [today, canSeeSales]);

  // #916 — the production report alone, re-run on scope/today/soleFlockId,
  // separate from the effect above so picking a flock never re-fetches the
  // other four panels (SELECTION.md). `canSeeSales` is in this effect's own
  // deps too: a role change must re-decide the trend outcome even when
  // scope/soleFlockId do not change, or a genuine total failure there goes
  // unreported. #918 — Codex review: a superseded request used to be merely
  // IGNORED, not cancelled, so it could sit in flight and hold one of the
  // account's report-concurrency permits until it timed out on its own; the
  // AbortController below actually cancels it on cleanup.
  //
  // #918 — Codex review: the current and previous weeks used to be two
  // adjacent requests; together with the yesterday-close fetch below, that
  // was three of the account's four shared report-concurrency permits per
  // load (RateLimitingOptions.ReportsConcurrency: PermitLimit 4, QueueLimit
  // 0 — no queue, so a permit past the cap is rejected, not queued). One
  // `daysBefore(today,14)..daysBefore(today,1)` request now covers both
  // weeks, split client-side by `splitProductionReport`.
  useEffect(() => {
    // With exactly one accessible flock there is no All-flocks scope to
    // offer, so this always reports that flock — the SAME `flockId` a picker
    // selection would produce, never a separate "everything" branch (parity
    // requirement, SELECTION.md).
    const flockId = soleFlockId ?? (scope.kind === "flock" ? scope.flock.id : undefined);
    const controller = new AbortController();
    setTrendLoading(true);
    setTrendOutcome("pending"); // re-decided freshly on every dispatch, never frozen at a stale outcome
    // The last 7 complete days and the 7 before them — yesterday back, so an
    // unsubmitted today never ends the line in a false dip (owner decision A).
    getProductionReport(daysBefore(today, 14), daysBefore(today, 1), flockId, controller.signal)
      .then((report) => {
        if (controller.signal.aborted) return;
        const { earlier, later } = splitProductionReport(report, daysBefore(today, 7));
        setTrend({ current: later, previous: earlier });
        setTrendLoading(false);
        setTrendOutcome("success");
      })
      .catch(() => {
        if (controller.signal.aborted) return;
        setTrend(null);
        setTrendLoading(false);
        setTrendOutcome("failure");
      });
    return () => controller.abort();
  }, [today, scope, soleFlockId, canSeeSales]);

  // #918 — Codex review: yesterday's close belongs to the farm-wide Morning
  // collection panel, so it is fetched WITHOUT a flock id and depends only on
  // `today` — picking a flock in the Lay rate card must never change it.
  useEffect(() => {
    let cancelled = false;
    getProductionReport(daysBefore(today, 1), daysBefore(today, 1))
      .then((r) => {
        if (cancelled) return;
        const yesterday = r.days[0] ?? null;
        setYesterdayClose(yesterday && yesterday.recordedFlocks > 0 && yesterday.missingFlocks === 0 ? yesterday.totalEggs : null);
      })
      .catch(() => { if (!cancelled) setYesterdayClose(null); });
    return () => { cancelled = true; };
  }, [today]);

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
  if (errorMessage !== null) {
    return (
      <Container maxWidth={false} disableGutters sx={{ maxWidth: 1120 }}>
        <Typography variant="h2">{t("title")}</Typography>
        <Alert severity="error" className="error">{errorMessage}</Alert>
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

  const scopedFlock = soleFlock ?? (scope.kind === "flock" ? scope.flock : null);
  const scopeName = scopedFlock ? scopedFlock.name : t("allFlocksOption");
  // The context caption's scope text differs from the selector's: unscoped,
  // it reads as a count of accessible flocks, matching the mockup's
  // `.context` line. #918 — Codex review: `listFlocks` is capped at
  // `MAX_PAGE`, so a farm past that cap reads as exactly 500 when it is
  // really more; the "at least" form says so instead of presenting a
  // truncated count as exact.
  const accessibleCount = flocks?.length ?? 0;
  const accessibleCountTruncated = flocks !== null && flocks.length === MAX_PAGE;
  const accessibleCountLabel = accessibleCountTruncated
    ? t("accessibleFlocksCountAtLeast", { count: accessibleCount })
    : t("accessibleFlocksCount", { count: accessibleCount });
  const contextScope = scopedFlock ? scopedFlock.name : accessibleCountLabel;

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
  // #916 — branches on `line.scale`, never a null check: `max` is non-null
  // under BOTH "complete" and "partial", so a null check alone used to say
  // "no peak or average" beside a caption and Peak figure showing a real number.
  const trendLabel = (line: DayStripData) => {
    if (line.scale === "none") {
      // A window where no flock ever owed a filing is not a window of missing
      // ones — a new farm's strip used to draw fourteen blank-but-blameless
      // slots and announce that none had an entry. `partial` is always 0 here:
      // a partial slot requires a recorded figure, which "none" has none of.
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
  const deltaClass = (delta: number | null): (typeof DASHBOARD_DELTA_CLASSES)[number] =>
    delta === null || delta === 0 ? "trend-delta" : delta < 0 ? "trend-delta is-down" : "trend-delta is-up";

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
                {yesterdayClose !== null && <Typography variant="caption" color="text.secondary">{t("yesterdayByClose", { total: fmt.count(yesterdayClose) })}</Typography>}
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
          "& .daystrip": { display: "flex", gap: { xs: "2px", md: "4px" }, height: 80 },
          "& .day": { height: 80 },
          "& .day > i": { maxWidth: { xs: "none", md: 18 } },
          "& .tipdock .tip": { whiteSpace: "normal", overflow: "visible", maxWidth: "100%" },
        }}>
          {/* #916/#918 — matches the approved mockup's DOM order exactly
              (production-flock-selector-v2.html): head, scope, context,
              scale+dock+strip+rule+legend (all inside DayStrip), hen-day KPI
              LAST. */}
          <Box sx={headingSx}>
            <Typography variant="h3" aria-label={t("trendPanelTitle")}><Link to="/reports">{t("layRateTitle")}</Link></Typography>
            <Typography variant="caption" color="text.secondary">{t("trendPanelTitle")}</Typography>
          </Box>

          {flocksFailed ? (
            // The panel-error pattern rather than an empty selector: a failed
            // flock-list read must not read as a genuinely empty farm.
            <Box sx={{ mt: "18px", mb: "8px" }}>
              <Alert severity="error" className="error" action={
                <Button color="inherit" size="small" disabled={flocksRetrying} onClick={fetchFlocks}>
                  {flocksRetrying ? tp("loading") : tp("retry")}
                </Button>
              }>
                {t("flockListUnavailableMessage")}
              </Alert>
            </Box>
          ) : (
            <>
              <Box sx={{ mt: "18px", mb: "8px" }}>
                <Typography
                  id={scopeLabelId} component="span"
                  sx={{ display: "block", textTransform: "uppercase", letterSpacing: "0.08em", fontSize: "10px", color: "text.secondary", mb: "5px" }}
                >
                  {t("flockScopeLabel")}
                </Typography>
                {soleFlock !== null ? (
                  // The mockup's #fixedScope: plain text, no dropdown, no
                  // chevron, no redundant All flocks choice (SELECTION.md).
                  <Box sx={{ fontSize: "16px", fontWeight: 600, minHeight: "44px", display: "flex", alignItems: "center", borderBottom: "1px solid var(--rule)" }}>
                    {soleFlock.name}
                  </Box>
                ) : (
                  <Button
                    aria-labelledby={`${scopeLabelId} ${scopeValueId}`}
                    aria-haspopup="dialog"
                    aria-expanded={pickerOpen}
                    onClick={() => setPickerOpen(true)}
                    sx={{
                      width: "100%", display: "flex", justifyContent: "space-between", alignItems: "center",
                      fontWeight: 600, textAlign: "left", textTransform: "none", gap: 1.5, "&&": { minHeight: 44 },
                    }}
                  >
                    <Box component="span" id={scopeValueId} sx={{ overflowWrap: "anywhere" }}>{scopeName}</Box>
                    <ChevronRight size={18} aria-hidden focusable={false} />
                  </Button>
                )}
              </Box>
              <Typography className="trend-context" variant="caption" color="text.secondary" sx={{ display: "block" }}>
                {t("layRateContext", { scope: contextScope, from: fmt.date(daysBefore(today, 14)), to: fmt.date(daysBefore(today, 1)) })}
              </Typography>

              {soleFlock === null && (
                // #916 review — reuses the shared NamedEntityPicker/FlockPicker
                // (docs/designs/822-mui-revamp.md's NamedEntityPicker↔Autocomplete
                // row) instead of a bespoke dialog. "All flocks" pins above the
                // scrolling results via `pinnedChoice`, the same `slots.paper`
                // mechanism the picker already uses for its Load-more footer.
                // `onEscape`/`onOutsideClick` are no-ops, matching ExpensesPage's
                // own FlockPicker-in-Dialog usage: the keydown still bubbles to
                // the Dialog's own Escape/backdrop close.
                <Dialog open={pickerOpen} title={t("chooseFlockTitle")} onClose={() => setPickerOpen(false)}>
                  {/* #918 P2 review — the shared Dialog unmounts its children
                      on close (MUI's default `keepMounted={false}`), so every
                      reopen is a FRESH engine mount. `controlledCommitted`
                      seeds `state.selection.entity` at that mount from the
                      current scope, driving `aria-selected` on the matching
                      option. `controlledGeneration` is a constant: the sync
                      effect fires once per mount regardless (its own ref
                      starts at -1), and `scope` never changes without the
                      dialog also closing, so no mount ever needs a second
                      sync. This also pre-fills the reopened search field
                      with the committed name — checked against the DOM, not
                      assumed (`Dashboard.test.tsx`) — matching every other
                      `FlockPicker`/`CustomerPicker` caller's own reopen
                      behaviour; the results are not narrowed by it, since the
                      seed touches only display text, never the discovery
                      filter. `onClear` is wired because seeding a non-null
                      `controlledCommitted` also surfaces the footer's own
                      Clear link (previously dead code) — left unwired it
                      would blank the engine's selection without touching
                      `scope`, reopening a version of this same desync. */}
                  {/* #935 — Dialog's Paper has no width on desktop, only a
                      maxWidth, so it shrink-wraps to content; this is the
                      only FlockPicker caller outside a `.form-grid`, so
                      `.dialog .form-grid .named-picker { width: 100% }`
                      never reaches it. A percentage width wouldn't widen a
                      shrink-to-fit ancestor either — `min-width` does. 27rem
                      is the 30rem cap minus DialogContent's 24px each-side
                      padding. Scoped to `md`: below it Dialog.tsx already
                      sets an explicit phone width.

                      #937 — nothing follows the picker, so Paper's real
                      height was just the 44px search row and MUI clipped
                      the results popover (`position: absolute`, out of
                      flow). Reserve height instead of disabling the clip:
                      `overflow: visible` also changes what Popper's `flip`
                      modifier reads as available space, flipping the
                      popover above the search field. `dashboard-flock-
                      picker` (styles.css) caps the listbox at ~6 rows
                      instead of MUI's 40vh default, so the modal stays
                      compact and only the listbox itself scrolls; 22.5rem
                      matches that cap plus the pinned choice button, the
                      meta/footer row and the search row itself. */}
                  <Box className="dashboard-flock-picker" sx={{ minWidth: { md: "27rem" }, minHeight: "22.5rem" }}>
                    <FlockPicker
                      label={t("searchAccessibleFlocksLabel")}
                      eligibility="active-and-depleted"
                      required={false}
                      open={pickerOpen}
                      onEscape={() => {}}
                      onOutsideClick={() => {}}
                      controlledCommitted={scope.kind === "flock" ? scope.flock : null}
                      controlledGeneration={1}
                      onCommit={(f) => { setScope({ kind: "flock", flock: f }); setPickerOpen(false); }}
                      onClear={() => { setScope({ kind: "all" }); setPickerOpen(false); }}
                      pinnedChoice={
                        <Button
                          fullWidth
                          onClick={() => { setScope({ kind: "all" }); setPickerOpen(false); }}
                          aria-pressed={scope.kind === "all"}
                          sx={{ justifyContent: "space-between", textTransform: "none", mx: 2, mt: 1, width: "calc(100% - 32px)", "&&": { minHeight: 44 } }}
                        >
                          <span>{t("allFlocksOption")}</span>
                          <Typography component="span" variant="caption" color="text.secondary">{accessibleCountLabel}</Typography>
                        </Button>
                      }
                    />
                  </Box>
                </Dialog>
              )}
            </>
          )}

          {trendLoading ? <LinearProgress aria-label={t("trendPanelTitle")} sx={{ height: 5, borderRadius: 2, mb: 1 }} />
            : trendData === null ? panelError : <>
              <DayStrip data={trendData.line} label={trendLabel(trendData.line)}
                title={t(
                  trendData.line.scale === "partial" ? "trendScaleTitlePartial"
                    : trendData.line.scale === "none" ? "trendScaleTitleNone"
                      : "trendScaleTitle",
                )}
                peak={t("trendPeak", { total: trendData.line.max === null ? "—" : fmt.count(trendData.line.max) })}
                average={trendData.line.average === null ? t("trendNoCompleteAvg") : t("trendCompleteAvg", { total: fmt.count(trendData.line.average, 1) })}
                legend={{ complete: t("legendComplete"), partial: t("legendPartial"), noEntry: t("legendNoEntry") }}
                tip={trendTip} from={<FarmDate iso={daysBefore(today, 14)} />} to={<FarmDate iso={daysBefore(today, 1)} />} />
              <Typography className="trend-kpi"><span className="trend-fig">{trendData.henDay.current === null ? "—" : `${fmt.count(trendData.henDay.current, 1)}%`}</span><span className={deltaClass(trendData.henDay.delta)}>{deltaText(trendData.henDay.delta)}</span></Typography>
              <Typography className="trend-sub" variant="caption" sx={{ display: "block" }}>{t("henDaySubLabel")}</Typography>
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
        {/* describeChild: the hover hint describes the link, which already
            names itself via `aria-label` above (that wins over anything
            Tooltip would inject, describeChild or not) — this only adds the
            hint as a description rather than trying to replace the name. */}
        <Tooltip title={missing ? t("recordTodayHint") : undefined} describeChild>
          <Typography component={Link} to={href} sx={{ fontWeight: 600 }}
            aria-label={missing ? t("tileLinkLabelMissing", { flock: flock.name }) : t("tileLinkLabel", { flock: flock.name })}
          >{flock.name}</Typography>
        </Tooltip>
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
