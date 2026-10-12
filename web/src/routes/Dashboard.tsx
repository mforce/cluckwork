// web/src/routes/Dashboard.tsx
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Box, Container, Typography, useMediaQuery } from "@mui/material";
import {
  getProductionReport, getStock, listDailyEntries, listFlocks,
} from "../api/cluckwork";
import type { DailyEntry, Flock, ProductionReport, StockRow } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useFormat } from "../farm/useFormat";
import { FarmDate } from "../components/FarmDate";
import { ExpandedLayRate } from "../components/ExpandedLayRate";
import { useAuth } from "../auth/useAuth";
import { useFarm, useFarmToday } from "../farm/useFarm";
import { daysBefore } from "../lib/dates";
import { MD_UP_QUERY } from "../lib/breakpoints";
import { captureTiles, dayStrip } from "../lib/dashboard";
import {
  DEFAULT_EXPANDED_RANGE, DEFAULT_RANGE, EXPANDED_RANGE_PRESETS, EXPANDED_RANGE_STORAGE_KEY,
  MAX_EXPANDED_RANGE_DAYS, RANGE_STORAGE_KEY, formatStoredRange, parseStoredRange, trendWindow,
} from "../lib/layRateRange";
import type { CustomRangeError, LayRateRange } from "../lib/layRateRange";
import { readAccountScoped, writeAccountScoped } from "../lib/accountStorage";
import { splitProductionReport } from "../lib/productionReportSplit";
import i18n from "../i18n";
import { drainPages } from "./dashboard/drainPages";
import { MorningBrief } from "./dashboard/MorningBrief";
import { CollectionPanel } from "./dashboard/CollectionPanel";
import { StockPanel } from "./dashboard/StockPanel";
import { SalesPanel } from "./dashboard/SalesPanel";
import { LayRateCard } from "./dashboard/LayRateCard";
import { useRecentOrders } from "./dashboard/useRecentOrders";
import { useTrendStrings } from "./dashboard/trendStrings";

// #915 — a glance panel, not a ledger: eight houses fill the desktop card
// without scrolling it, six fit the phone's. Every house is still reachable,
// one page at a time, so nothing hides behind a link any more.
const HOUSES_PER_PAGE_DESKTOP = 8;
const HOUSES_PER_PAGE_PHONE = 6;

// #916 — the Lay rate card's own scope, independent of every other panel.
export type FlockScope = { kind: "all" } | { kind: "flock"; flock: Flock };

export const DASHBOARD_DELTA_CLASSES = ["trend-delta", "trend-delta is-down", "trend-delta is-up"] as const;

// Six parallel reads; failed panels degrade independently. The server owns
// the hen-day calculation for each seven-day reporting window.
export function Dashboard() {
  const { t } = useTranslation("dashboard");
  const fmt = useFormat();
  const { farm } = useFarm();
  const [openedAt] = useState(() => new Date().toISOString());
  const { stripStrings } = useTrendStrings();
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
  // #914 — the plotted window, remembered per device and per farm. The card
  // never plots today (owner decision A), so `latest` is the farm's yesterday
  // and a remembered custom range is re-validated against it rather than
  // trusted: storage outlives every rule this build knows.
  const latestDay = daysBefore(today, 1);
  const [range, setRange] = useState<LayRateRange>(
    () => parseStoredRange(readAccountScoped(RANGE_STORAGE_KEY), latestDay) ?? DEFAULT_RANGE,
  );
  const chooseRange = (next: LayRateRange) => {
    setRange(next);
    writeAccountScoped(RANGE_STORAGE_KEY, formatStoredRange(next));
  };
  const plotted = trendWindow(range, today);
  const { from, to, previousFrom } = plotted;
  // The custom form's own draft, separate from the applied range so typing a
  // date never refetches and a rejected span leaves the plotted window alone.
  const [customOpen, setCustomOpen] = useState(range.kind === "custom");
  const [draftFrom, setDraftFrom] = useState(plotted.from);
  const [draftTo, setDraftTo] = useState(plotted.to);
  const [rangeError, setRangeError] = useState<CustomRangeError | null>(null);
  // #941 — the expanded chart asks a different question (a month or a quarter,
  // not a fortnight), so it holds its own window, its own memory and its own
  // read. Opening it never moves the card's window, and closing it never
  // leaves the card refetching.
  const [expanded, setExpanded] = useState(false);
  const [expandedRange, setExpandedRange] = useState<LayRateRange>(
    () => parseStoredRange(
      readAccountScoped(EXPANDED_RANGE_STORAGE_KEY), latestDay,
      EXPANDED_RANGE_PRESETS, MAX_EXPANDED_RANGE_DAYS,
    ) ?? DEFAULT_EXPANDED_RANGE,
  );
  const [expandedReport, setExpandedReport] = useState<ProductionReport | null>(null);
  const [expandedFailed, setExpandedFailed] = useState(false);
  const expandRef = useRef<HTMLButtonElement>(null);
  const expandedPlotted = trendWindow(expandedRange, today);
  // Shaped by the same `dayStrip` the card uses, so a day means one thing on
  // both. Memoised, and declared up here above the loading and failure returns
  // rather than beside the card's own line: the chart keys its
  // scroll-to-the-newest-day effect on this identity, and rebuilt every render
  // that effect would re-run on every render and the window could never move.
  const expandedLine = useMemo(
    () => dayStrip({ days: expandedReport?.days ?? [] }), [expandedReport]);
  // #918 — the flock LIST read can fail alone while the other three panels
  // succeed; "failed" must not read as "0 accessible flocks". `flocksRetrying`
  // holds the unavailable state up until the retried read SETTLES — clearing
  // `flocksFailed` on click showed that same false zero in the gap before.
  const [flocksFailed, setFlocksFailed] = useState(false);
  const [flocksTruncated, setFlocksTruncated] = useState(false);
  // Told apart from "not here yet": a panel that has not answered shows its own
  // loading state, never the error of a read that has not failed.
  const [entriesFailed, setEntriesFailed] = useState(false);
  const [stockFailed, setStockFailed] = useState(false);
  const [entriesTruncated, setEntriesTruncated] = useState(false);
  const [flocksRetrying, setFlocksRetrying] = useState(false);
  // Bumped by Retry so the panels effect re-runs under its own ownership.
  const [panelsGeneration, setPanelsGeneration] = useState(0);

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
  const housesPerPage = isDesktop ? HOUSES_PER_PAGE_DESKTOP : HOUSES_PER_PAGE_PHONE;
  const [housesPage, setHousesPage] = useState(0);

  const recentOrders = useRecentOrders(canSeeSales);

  // #916 — with one accessible flock there is no All-flocks concept (SELECTION.md);
  // its figures must come from the SAME code path as picking that flock from a
  // larger list, never a parallel branch. The trend effect depends on the id, not
  // `soleFlock` itself — the array's reference changes every render, the id does not.
  const soleFlock = flocks !== null && flocks.length === 1 ? flocks[0] : null;
  const soleFlockId = soleFlock?.id ?? null;
  // With exactly one accessible flock there is no All-flocks scope to offer,
  // so both production reads always report that flock — the SAME `flockId` a
  // picker selection would produce, never a separate "everything" branch
  // (parity requirement, SELECTION.md).
  const trendFlockId = soleFlockId ?? (scope.kind === "flock" ? scope.flock.id : undefined);

  // #918 — "everything failed" spans BOTH effects (panels + trend). Each
  // side tracks its OWN outcome directly rather than through a generation
  // counter: `panelsOutcome` resets to "pending" the instant a new panels
  // batch starts and `trendOutcome` resets the instant a new trend fetch
  // starts, so the derived verdict below only ever reads two CURRENT
  // answers, never a stale one paired with a fresh one. Retry re-runs that
  // same batch, so it updates `panelsOutcome` too — the prior ref-based
  // design left a stale "all four failed" outcome in place after a successful
  // retry, hiding a dashboard that had actually recovered.
  type PanelsOutcome = { state: "pending" } | { state: "someOk" } | { state: "allFailed"; reason: unknown };
  const [panelsOutcome, setPanelsOutcome] = useState<PanelsOutcome>({ state: "pending" });
  const [trendOutcome, setTrendOutcome] = useState<"pending" | "success" | "failure">("pending");
  // #915 — Recent orders left the parallel batch for the paged list hook, so
  // its verdict now comes from that hook. A role without the sales panel
  // contributes nothing either way, exactly as its skipped fetch did before.
  const ordersFailed = !canSeeSales || recentOrders.orders.error !== null;
  const errorMessage = panelsOutcome.state === "allFailed" && trendOutcome === "failure" && ordersFailed
    ? (panelsOutcome.reason instanceof ApiError ? panelsOutcome.reason.message : i18n.t("dashboard:loadFailed"))
    : null;

  // Retry re-runs the effect below rather than issuing a read of its own: a
  // second request needs a second copy of the cancellation and generation
  // guards, and the copy it had carried neither — it could answer after a
  // newer role's batch and kept draining after the reader left.
  const retryFlocks = () => {
    setFlocksRetrying(true);
    setPanelsGeneration((n) => n + 1);
  };

  useEffect(() => {
    let cancelled = false;
    const controller = new AbortController();
    setPanelsOutcome({ state: "pending" }); // a fresh load starts clean, never on a stale verdict
    setEntriesFailed(false);
    setStockFailed(false);
    const flockRead = drainPages((offset, limit, signal) => listFlocks({ limit, offset }, signal), controller.signal);
    const entryRead = drainPages((offset, limit, signal) => listDailyEntries({ from: today, to: today, limit, offset }, signal), controller.signal);
    const stockRead = getStock();

    // Each panel commits when its OWN read settles. Behind the batch, a
    // recovered flock list waited on a stock request that may never answer
    // (`fetch` carries no timeout), leaving Retry disabled on a list already
    // back. One generation and one controller still cover all three.
    // The first answer ends the full-page gate. One stalled read — `getStock`
    // carries no timeout — used to hold the whole screen, hiding the panels
    // that HAD answered along with their own errors and Retry.
    const settle = () => { if (!cancelled) setLoading(false); };
    flockRead.then(({ rows, truncated }) => {
      if (cancelled) return;
      setFlocks(rows); setFlocksTruncated(truncated); setFlocksFailed(false);
      setFlocksRetrying(false); setHousesPage(0);
    }).catch(() => {
      if (cancelled) return;
      setFlocksFailed(true); setFlocksRetrying(false);
    }).finally(settle);
    entryRead.then(({ rows, truncated }) => {
      if (cancelled) return;
      setEntries(rows); setEntriesTruncated(truncated);
    }).catch(() => { if (!cancelled) setEntriesFailed(true); }).finally(settle);
    stockRead.then((rows) => { if (!cancelled) setStock(rows); })
      // Unlike the other two panels, a failed stock read DROPS what it had: a
      // stale count reads as old, but a stale below-floor warning reads as
      // true. The panel shows its error, the brief's fact goes with it, and
      // the house facts stand on their own read.
      .catch(() => { if (!cancelled) { setStock(null); setStockFailed(true); } }).finally(settle);

    // The page-level verdict still needs every answer, because "everything
    // failed" is only true once nothing is outstanding.
    Promise.allSettled([flockRead, entryRead, stockRead]).then((issued) => {
      if (cancelled) return;
      const rejected = issued.filter((r): r is PromiseRejectedResult => r.status === "rejected");
      setPanelsOutcome(rejected.length === issued.length
        ? { state: "allFailed", reason: rejected[0]?.reason }
        : { state: "someOk" });
    }).catch((err: unknown) => console.error("dashboard: panels verdict failed", err));
    return () => { cancelled = true; controller.abort(); };
    // #918 — `canSeeSales` is a dep even though none of these three reads is
    // role-gated: `panelsOutcome` is half the "everything failed" verdict, and
    // a role change starts a new generation whose verdict must be decided
    // freshly rather than inherited from the previous role's healthy one.
  }, [today, canSeeSales, panelsGeneration]);

  // #916 — the production report alone, re-run on scope/today/soleFlockId,
  // separate from the effect above so picking a flock never re-fetches the
  // other four panels (SELECTION.md). `canSeeSales` is in this effect's own
  // deps too: a role change must re-decide the trend outcome even when
  // scope/soleFlockId do not change, or a genuine total failure there goes
  // unreported. #918 — Codex review: a superseded request used to be merely
  // IGNORED, not cancelled, so it could sit in flight and hold one of the
  // account's report-concurrency permits until it timed out on its own; the
  // AbortController below actually cancels it on cleanup.
  // #918 — Codex review: the current and previous weeks used to be two
  // adjacent requests; together with the yesterday-close fetch below, that
  // was three of the account's four shared report-concurrency permits per
  // load (RateLimitingOptions.ReportsConcurrency: PermitLimit 4, QueueLimit
  // 0 — no queue, so a permit past the cap is rejected, not queued). One
  // `daysBefore(today,14)..daysBefore(today,1)` request now covers both
  // weeks, split client-side by `splitProductionReport`.
  useEffect(() => {
    const controller = new AbortController();
    setTrendLoading(true);
    setTrendOutcome("pending"); // re-decided freshly on every dispatch, never frozen at a stale outcome
    // #914 — the chosen window and the window of the same length before it, in
    // ONE contiguous request that `splitProductionReport` divides at the
    // chosen window's first day. Only the later half is drawn; the earlier
    // half exists for the hen-day comparison alone.
    getProductionReport(previousFrom, to, trendFlockId, controller.signal)
      .then((report) => {
        if (controller.signal.aborted) return;
        const { earlier, later } = splitProductionReport(report, from);
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
  }, [from, to, previousFrom, trendFlockId, canSeeSales]);

  // #941 — focus returns to the control that opened the chart, AFTER the
  // overlay is gone: called from `onClose` it lands while the frame is still
  // mounted, whose focus trap pulls it straight back and then drops it on
  // <body>. The ref keeps the first render from stealing focus to a control
  // nobody has touched.
  const wasExpanded = useRef(false);
  useEffect(() => {
    if (wasExpanded.current && !expanded) expandRef.current?.focus();
    wasExpanded.current = expanded;
  }, [expanded]);

  // #941 — the expanded chart's own read, issued only while it is open, so a
  // reader who never expands pays nothing for a year-long report. It needs the
  // plotted window alone: the hen-day comparison against the previous equal
  // window is the CARD's measure and stays there.
  useEffect(() => {
    if (!expanded) return;
    const controller = new AbortController();
    setExpandedReport(null);
    setExpandedFailed(false);
    getProductionReport(expandedPlotted.from, expandedPlotted.to, trendFlockId, controller.signal)
      .then((report) => { if (!controller.signal.aborted) setExpandedReport(report); })
      .catch(() => { if (!controller.signal.aborted) setExpandedFailed(true); });
    return () => controller.abort();
  }, [expanded, expandedPlotted.from, expandedPlotted.to, trendFlockId]);

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
  // The FULL capture-status list — the attention line, the progress bar and
  // the "N of M houses in" caption count every active flock, never only the
  // page on screen. A farm with more missing houses than the old cap
  // undercounted both (#883).
  const allTiles = flocks !== null && entries !== null ? captureTiles(flocks, entries) : null;
  // Two different gaps, and conflating them made the panel disown a count it
  // actually had. A truncated FLOCK list means the farm's size is unknown, so
  // every figure over it is a lower bound. A truncated ENTRY list leaves that
  // count exact and makes the STATUS side incomplete instead: houses whose
  // entry was never read show as missing and their eggs never reach the total.
  const housesIncomplete = flocksTruncated;
  const entryDataIncomplete = entriesTruncated;
  const scopedFlock = soleFlock ?? (scope.kind === "flock" ? scope.flock : null);
  // The context caption's scope text differs from the selector's: unscoped,
  // it reads as a count of accessible flocks, matching the mockup's
  // `.context` line. #918 — Codex review: `listFlocks` is capped at
  // `MAX_PAGE`, so a farm past that cap reads as exactly 500 when it is
  // really more; the "at least" form says so instead of presenting a
  // truncated count as exact.
  const accessibleCount = flocks?.length ?? 0;
  const accessibleCountTruncated = flocksTruncated;
  const accessibleCountLabel = accessibleCountTruncated
    ? t("accessibleFlocksCountAtLeast", { count: accessibleCount })
    : t("accessibleFlocksCount", { count: accessibleCount });
  const contextScope = scopedFlock ? scopedFlock.name : accessibleCountLabel;
  // The window's own dates for the expanded chart, which every sentence about
  // its range uses rather than a preset's wording (#650).
  const expandedSpan = t("rangeSpan", {
    from: fmt.date(expandedPlotted.from), to: fmt.date(expandedPlotted.to),
  });

  // The attention line (D3.3, #829): missing houses only. #911 adds the
  // low-stock fact BESIDE it, from its own read, rather than into it: the
  // stock request can fail while the flock read succeeds, and each fact then
  // stands or vanishes on its own.
  const missingHouses = allTiles === null ? [] : allTiles.filter((c) => c.entry === null).map((c) => c.flock);
  const attentionShown = missingHouses.slice(0, attentionCap);
  const attentionMore = missingHouses.length - attentionShown.length;

  const recordedHouses = allTiles === null ? 0 : allTiles.length - missingHouses.length;

  // #911 — the brief's low-stock fact, and the ledger's mark. Named when one
  // grade is short (the farm can act on it directly), counted when several are.
  // `stock === null` (unread or failed) simply means no fact.
  const belowFloorRows = stock === null ? [] : stock.filter((r) => r.belowFloor);
  const belowFloorFact = (key: "attentionGradeBelowFloor" | "stockCaptionBelowFloor") => {
    if (belowFloorRows.length !== 1) {
      return t(key === "attentionGradeBelowFloor" ? "attentionGradesBelowFloor" : "stockCaptionBelowFloorMany",
        { grades: fmt.count(belowFloorRows.length) });
    }
    const [row] = belowFloorRows;
    const floor = row.lowStockFloor ?? 0;
    return t(key, { grade: row.gradeName, short: fmt.count(floor - row.available), floor: fmt.count(floor) });
  };

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

      <MorningBrief
        allTiles={allTiles} missingHouses={missingHouses} attentionShown={attentionShown}
        attentionMore={attentionMore} belowFloorRows={belowFloorRows} belowFloorFact={belowFloorFact}
        entryDataIncomplete={entryDataIncomplete} entries={entries} t={t} fmt={fmt}
      />

      <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", md: "minmax(0,1.6fr) minmax(300px,1fr)" }, gap: 2.5, alignItems: "start" }}>
        <CollectionPanel
          allTiles={allTiles} entries={entries} flocksFailed={flocksFailed} entriesFailed={entriesFailed}
          panelError={panelError} housesIncomplete={housesIncomplete} entryDataIncomplete={entryDataIncomplete}
          recordedHouses={recordedHouses} housesPage={housesPage} setHousesPage={setHousesPage}
          housesPerPage={housesPerPage} today={today} t={t} fmt={fmt} yesterdayClose={yesterdayClose}
        />

        <StockPanel
          stock={stock} stockFailed={stockFailed} panelError={panelError}
          t={t} fmt={fmt} belowFloorRows={belowFloorRows} belowFloorFact={belowFloorFact}
        />

        {canSeeSales && <SalesPanel {...recentOrders}
          panelError={panelError} t={t} tp={tp} fmt={fmt}
        />}

        <LayRateCard
          scopedFlock={scopedFlock} soleFlock={soleFlock} scope={scope} setScope={setScope}
          pickerOpen={pickerOpen} setPickerOpen={setPickerOpen} accessibleCountLabel={accessibleCountLabel}
          flocksFailed={flocksFailed} flocksRetrying={flocksRetrying} retryFlocks={retryFlocks}
          range={range} chooseRange={chooseRange} customOpen={customOpen} setCustomOpen={setCustomOpen}
          draftFrom={draftFrom} setDraftFrom={setDraftFrom} draftTo={draftTo} setDraftTo={setDraftTo}
          rangeError={rangeError} setRangeError={setRangeError} latestDay={latestDay}
          contextScope={contextScope} plotted={plotted} trend={trend} trendLoading={trendLoading}
          panelError={panelError} expandRef={expandRef} setExpanded={setExpanded} t={t} tp={tp} fmt={fmt}
        />
      </Box>
      {expanded && (
        <ExpandedLayRate
          {...stripStrings(expandedLine, expandedSpan)}
          data={expandedReport === null ? null : expandedLine} failed={expandedFailed}
          scopeName={contextScope} from={expandedPlotted.from} to={expandedPlotted.to}
          latestDay={latestDay} range={expandedRange}
          onRangeChange={(next) => {
            setExpandedRange(next);
            writeAccountScoped(EXPANDED_RANGE_STORAGE_KEY, formatStoredRange(next));
          }}
          onClose={() => setExpanded(false)}
        />
      )}
    </Container>
  );
}
