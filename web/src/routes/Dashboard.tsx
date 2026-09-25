// web/src/routes/Dashboard.tsx
import { useCallback, useEffect, useId, useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Bird, Check, ChevronRight, CircleDashed, Egg, ShoppingCart, TriangleAlert } from "lucide-react";
import {
  Alert, Box, Button, Card, Container, LinearProgress, Table, TableBody, TableCell, TableHead, TableRow, TextField, Tooltip, Typography, useMediaQuery,
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
import { FilterDateField } from "../components/FilterBar";
import { FlockPicker } from "../components/FlockPicker";
import { PanelPager } from "../components/PanelPager";
import { usePagedList } from "../components/usePagedList";
import { useAuth } from "../auth/useAuth";
import { useFarm, useFarmToday } from "../farm/useFarm";
import { daysBefore } from "../lib/dates";
import { MD_UP_QUERY } from "../lib/breakpoints";
import {
  captureTiles, dayStrip, henDayTrend, panelPage, stockBar, todaysEggs,
} from "../lib/dashboard";
import type { CaptureTile, DayStripData, DayStripSlot } from "../lib/dashboard";
import {
  DEFAULT_RANGE, MAX_RANGE_DAYS, RANGE_PRESETS, RANGE_STORAGE_KEY, customRangeError,
  formatStoredRange, parseStoredRange, trendWindow,
} from "../lib/layRateRange";
import type { CustomRangeError, LayRateRange } from "../lib/layRateRange";
import { readAccountScoped, writeAccountScoped } from "../lib/accountStorage";
import { splitProductionReport } from "../lib/productionReportSplit";
import i18n from "../i18n";
import { statusLabel } from "../i18n/enums";

const RECENT_ORDERS = 5;
// #915 — a glance panel, not a ledger: eight houses fill the desktop card
// without scrolling it, six fit the phone's. Every house is still reachable,
// one page at a time, so nothing hides behind a link any more.
const HOUSES_PER_PAGE_DESKTOP = 8;
const HOUSES_PER_PAGE_PHONE = 6;
// The server clamps a list request at 500 rows.
const MAX_PAGE = 500;
// #915 — both of this panel's lists are DRAINED rather than truncated: the
// progress bar and the "N of M houses in" caption answer for the farm, which
// a first page cannot tell them, and every house has to be reachable now that
// no link carries the remainder. Below 500 houses this costs no extra request
// at all. The ceiling only has to make the loop terminate; at it the counts
// say "at least" instead of a figure they cannot stand behind.
const MAX_DRAIN_PAGES = 20;

// `signal` stops the WALK as well as the request in flight: leaving the
// Dashboard mid-drain used to leave the loop asking for every later page,
// dozens of reads nobody was waiting for.
async function drainPages<T>(
  fetchPage: (offset: number, limit: number, signal: AbortSignal) => Promise<T[]>,
  signal: AbortSignal,
): Promise<{ rows: T[]; truncated: boolean }> {
  const rows: T[] = [];
  for (let page = 0; page < MAX_DRAIN_PAGES; page += 1) {
    if (signal.aborted) return { rows, truncated: false };
    const batch = await fetchPage(page * MAX_PAGE, MAX_PAGE, signal);
    rows.push(...batch);
    if (batch.length < MAX_PAGE) return { rows, truncated: false };
  }
  return { rows, truncated: true };
}

// #916 — the Lay rate card's own scope, independent of every other panel.
type FlockScope = { kind: "all" } | { kind: "flock"; flock: Flock };

// The native select's own control box carries the 44px phone target; MUI sizes
// it from the font otherwise.
const RANGE_FIELD_SX = { "& .MuiInputBase-input": { minHeight: 44, boxSizing: "border-box" } };

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
  const ordersFailed = !canSeeSales || orders.error !== null;
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
      .catch(() => { if (!cancelled) setStockFailed(true); }).finally(settle);

    // The page-level verdict still needs every answer, because "everything
    // failed" is only true once nothing is outstanding.
    Promise.allSettled([flockRead, entryRead, stockRead]).then((issued) => {
      if (cancelled) return;
      const rejected = issued.filter((r): r is PromiseRejectedResult => r.status === "rejected");
      setPanelsOutcome(rejected.length === issued.length
        ? { state: "allFailed", reason: rejected[0]?.reason }
        : { state: "someOk" });
    });
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
    // With exactly one accessible flock there is no All-flocks scope to
    // offer, so this always reports that flock — the SAME `flockId` a picker
    // selection would produce, never a separate "everything" branch (parity
    // requirement, SELECTION.md).
    const flockId = soleFlockId ?? (scope.kind === "flock" ? scope.flock.id : undefined);
    const controller = new AbortController();
    setTrendLoading(true);
    setTrendOutcome("pending"); // re-decided freshly on every dispatch, never frozen at a stale outcome
    // #914 — the chosen window and the window of the same length before it, in
    // ONE contiguous request that `splitProductionReport` divides at the
    // chosen window's first day. Only the later half is drawn; the earlier
    // half exists for the hen-day comparison alone.
    getProductionReport(previousFrom, to, flockId, controller.signal)
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
  }, [from, to, previousFrom, scope, soleFlockId, canSeeSales]);

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
  const housePage = panelPage(allTiles ?? [], housesPage, housesPerPage);
  const housesPagerLabel = housesIncomplete
    ? t("pagerHousesAtLeast", {
        first: fmt.count(housePage.first), last: fmt.count(housePage.last), total: fmt.count(housePage.total),
      })
    : t("pagerHouses", {
        first: fmt.count(housePage.first), last: fmt.count(housePage.last), total: fmt.count(housePage.total),
      });

  const scopedFlock = soleFlock ?? (scope.kind === "flock" ? scope.flock : null);
  const scopeName = scopedFlock ? scopedFlock.name : t("allFlocksOption");
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

  const trendData = trend === null ? null : {
    line: dayStrip({ days: trend.current.days }),
    henDay: henDayTrend(trend.current, trend.previous),
  };
  const bar = stock === null ? null : stockBar(stock);
  // The window's own dates, which every sentence about the card's range uses
  // rather than a preset's wording: a custom range has no preset to name, and
  // one string cannot be built from a stem plus a suffix (#650).
  const rangeSpan = t("rangeSpan", { from: fmt.date(from), to: fmt.date(to) });
  const presetLabel = (days: number) => t("rangePresetOption", { count: days, total: fmt.count(days) });
  const rangeSelection = range.kind === "custom" || customOpen ? "custom" : String(range.days);
  const selectRange = (value: string) => {
    setRangeError(null);
    const preset = RANGE_PRESETS.find((days) => String(days) === value);
    if (preset === undefined) {
      setDraftFrom(from);
      setDraftTo(to);
      setCustomOpen(true);
      return;
    }
    setCustomOpen(false);
    chooseRange({ kind: "preset", days: preset });
  };
  // Rejected in the form, never silently truncated: a window the card quietly
  // shortened would leave the reader comparing a period they did not ask for.
  const applyCustomRange = () => {
    const error = customRangeError(draftFrom, draftTo, latestDay);
    setRangeError(error);
    if (error === null) chooseRange({ kind: "custom", from: draftFrom, to: draftTo });
  };
  const rangeErrorText = (error: CustomRangeError) => {
    switch (error) {
      case "order": return t("rangeErrorOrder");
      case "future": return t("rangeErrorFuture", { date: fmt.date(latestDay) });
      case "tooLong": return t("rangeErrorTooLong", { count: MAX_RANGE_DAYS, days: fmt.count(MAX_RANGE_DAYS) });
      case "incomplete": return t("rangeErrorIncomplete");
      case "beforeCalendar": return t("rangeErrorBeforeCalendar");
    }
  };

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
        ? t("trendStripLabelNoFlocks", { range: rangeSpan })
        : t("trendStripLabelNone", { range: rangeSpan });
    }
    if (line.scale === "partial") {
      // The fallback peak: no complete day exists, so there is still no
      // average (that stays complete-day-only), but Peak is real and the
      // sentence must say so, not fall back to "no peak or average".
      return t("trendStripLabelPartialScale", { range: rangeSpan, max: fmt.count(line.max!) });
    }
    const figures = { range: rangeSpan, max: fmt.count(line.max!), avg: fmt.count(line.average!, 1) };
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
  const belowFloorIds = new Set(belowFloorRows.map((r) => r.eggGradeId));
  const belowFloorFact = (key: "attentionGradeBelowFloor" | "stockCaptionBelowFloor") => {
    if (belowFloorRows.length !== 1) {
      return t(key === "attentionGradeBelowFloor" ? "attentionGradesBelowFloor" : "stockCaptionBelowFloorMany",
        { grades: fmt.count(belowFloorRows.length) });
    }
    const [row] = belowFloorRows;
    const floor = row.lowStockFloor ?? 0;
    return t(key, { grade: row.gradeName, short: fmt.count(floor - row.available), floor: fmt.count(floor) });
  };
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
          <Box sx={{ display: "flex", flexWrap: "wrap", columnGap: 2, rowGap: 0.5 }}>
            {allTiles === null ? <Typography>{t("panelLoadError")}</Typography> : missingHouses.length > 0 ? (
              <>
                {attentionShown.map((flock) => (
                  <Typography key={flock.id} sx={{ display: "flex", alignItems: "center", gap: 1 }}>
                    <TriangleAlert size={15} aria-hidden />{t("attentionHouseNotRecorded", { flock: flock.name })}
                  </Typography>
                ))}
                {attentionMore > 0 && <Typography component={Link} to="/daily-entry" sx={{ color: "inherit" }}>{t("attentionMore", { count: attentionMore })}</Typography>}
              </>
            ) : <Typography>{t(allTiles.length === 0 ? "noFlocksMessage"
              : entryDataIncomplete ? "entriesIncompleteBrief" : "allHousesRecorded")}</Typography>}
            {belowFloorRows.length > 0 && (
              <Typography component={Link} to="/stock" sx={{ color: "inherit", display: "flex", alignItems: "center", gap: 1 }}>
                <TriangleAlert size={15} aria-hidden />{belowFloorFact("attentionGradeBelowFloor")}
              </Typography>
            )}
          </Box>
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
            {allTiles !== null && <Typography variant="caption" color="text.secondary" sx={{ textAlign: "right" }}>
              {housesIncomplete
                ? t("todayInCountAtLeast", { in: recordedHouses, count: allTiles.length })
                : t("todayInCount", { in: recordedHouses, count: allTiles.length })}
            </Typography>}
          </Box>
          {allTiles === null || entries === null
            ? (flocksFailed || entriesFailed ? panelError
              : <LinearProgress aria-label={t("collectionTitle")} sx={{ height: 5, borderRadius: 2 }} />)
            : allTiles.length === 0 ? (
            <EmptyState icon={Bird} message={t("noFlocksMessage")} />
          ) : (
            <>
              {/* A share of an unknown whole is not a share. With the list
                  incomplete the bar carries no value at all rather than one
                  measured against a ceiling. */}
              <LinearProgress
                variant={housesIncomplete ? "indeterminate" : "determinate"}
                value={housesIncomplete ? undefined : recordedHouses / allTiles.length * 100}
                aria-label={t("collectionTitle")}
                sx={{ height: 5, borderRadius: 2, mb: 1, bgcolor: "var(--surface-2)", "& .MuiLinearProgress-bar": { bgcolor: "var(--success)" } }} />
              {housePage.items.map((tile) => <TodayRow key={tile.flock.id} tile={tile} today={today} fmt={fmt} t={t} />)}
              {housePage.pageCount > 1 && <PanelPager
                label={housesPagerLabel}
                previousLabel={t("pagerPrevious", { panel: t("collectionTitle") })}
                nextLabel={t("pagerNext", { panel: t("collectionTitle") })}
                hasPrevious={housePage.page > 0}
                hasNext={housePage.page + 1 < housePage.pageCount}
                onPrevious={() => setHousesPage(housePage.page - 1)}
                onNext={() => setHousesPage(housePage.page + 1)}
              />}
              <Box sx={{ bgcolor: "var(--surface-2)", mx: { xs: -2, md: -2.25 }, mb: { xs: -2, md: -2.25 }, mt: 1.5, px: 2.25, py: 1.5 }}>
                <Box sx={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
                  <Typography>{t("collectedToday")}</Typography>
                  <Typography className="num" sx={{ fontFamily: "Georgia, serif", fontWeight: 600, fontSize: "1.8rem" }}>{fmt.count(todaysEggs(entries))}</Typography>
                </Box>
                {yesterdayClose !== null && <Typography variant="caption" color="text.secondary">{t("yesterdayByClose", { total: fmt.count(yesterdayClose) })}</Typography>}
                {housesIncomplete && <Typography variant="caption" color="text.secondary" sx={{ display: "block" }}>
                  {t("housesIncompleteNotice", { count: allTiles.length, total: fmt.count(allTiles.length) })}
                </Typography>}
                {entryDataIncomplete && <Typography variant="caption" color="text.secondary" sx={{ display: "block" }}>
                  {t("entriesIncompleteNotice")}
                </Typography>}
              </Box>
            </>
          )}
        </Card>

        <Card component="section" sx={{ ...sectionSx, gridColumn: { md: 2 }, gridRow: { md: 1 } }}>
          <Box sx={headingSx}>
            <Typography variant="h3" aria-label={t("stockPanelTitle")}><Link to="/stock">{t("availableStockTitle")}</Link></Typography>
          </Box>
          {bar === null || stock === null
            ? (stockFailed ? panelError
              : <LinearProgress aria-label={t("stockPanelTitle")} sx={{ height: 5, borderRadius: 2 }} />)
            : stock.length === 0 ? <EmptyState icon={Egg} message={t("noStockMessage")} /> : (
            <>
              <Typography className="stock-total" sx={{ display: "flex", alignItems: "baseline", justifyContent: "space-between", gap: 1, mb: 2,
                "& .stock-fig": { fontFamily: "Georgia, serif", fontSize: "2.5rem" } }}>
                <span className="stock-fig">{fmt.count(bar.totalAvailable)}</span>{" "}{t("eggsAvailableLabel", { count: bar.totalAvailable })}
              </Typography>
              <StockBar data={bar} />
              {bar.ledger.length > 0 && (
                <Table aria-label={t("stockLedgerLabel")} size="small" sx={{ mt: 1.5, tableLayout: "fixed", "& th, & td": { px: 0.75, py: 1, overflowWrap: "anywhere" } }}>
                  <TableHead><TableRow>
                    <TableCell sx={{ width: "46%" }}>{t("gradeColumn")}</TableCell>
                    <TableCell align="right">{t("countColumn")}</TableCell>
                    <TableCell align="right">{t("shareColumn")}</TableCell>
                  </TableRow></TableHead>
                  <TableBody>{bar.ledger.map((s) => (
                    <TableRow key={s.eggGradeId} tabIndex={0} sx={{
                      "&:hover, &:focus": { bgcolor: "var(--surface-2)", outline: "2px solid var(--focus)", outlineOffset: -2 },
                      "&:hover th, &:focus th": { textDecoration: "underline", textUnderlineOffset: "3px" },
                    }}>
                      <TableCell component="th" scope="row"><Box component="span" sx={{ display: "inline-flex", alignItems: "center", gap: 1 }}><span className={`swatch grade-${s.colorIndex}`} aria-hidden="true" />{s.gradeName}{belowFloorIds.has(s.eggGradeId) && (
                      /* #911 — a mark, not a tint: the icon carries the row's
                         own sentence as its accessible name. */
                      <Box component="span" role="img" aria-label={t("stockBelowFloorRowLabel", { grade: s.gradeName })} sx={{ display: "inline-flex", color: "var(--warn)" }}>
                        <TriangleAlert size={13} aria-hidden />
                      </Box>
                    )}</Box></TableCell>
                      <TableCell align="right">{fmt.count(s.available)}</TableCell>
                      <TableCell align="right">{`${fmt.count(s.pct, 1)}%`}</TableCell>
                    </TableRow>
                  ))}</TableBody>
                </Table>
              )}
              {bar.totalRestricted > 0 && <Typography color="text.secondary" variant="caption" sx={{ display: "block", mt: 1 }}>{t("stockCaptionRestricted", { restricted: fmt.count(bar.totalRestricted) })}</Typography>}
              {belowFloorRows.length > 0 && <Typography variant="caption" sx={{
                display: "block", mt: 1, px: 1, py: 0.75, borderRadius: "var(--r-input)",
                bgcolor: "var(--tint-warn)", color: "var(--warn)", fontWeight: 650,
              }}>{belowFloorFact("stockCaptionBelowFloor")}</Typography>}
            </>
          )}
        </Card>

        {canSeeSales && <Card component="section" sx={{ ...sectionSx, gridColumn: { md: 1 } }}>
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
              LAST. #914 adds the range control after the scope and drops the
              head's fixed "Last 14 days" caption, which the control replaces. */}
          <Box sx={headingSx}>
            <Typography variant="h3" aria-label={t("trendPanelTitle")}><Link to="/reports">{t("layRateTitle")}</Link></Typography>
          </Box>

          {flocksFailed ? (
            // The panel-error pattern rather than an empty selector: a failed
            // flock-list read must not read as a genuinely empty farm.
            <Box sx={{ mt: "18px", mb: "8px" }}>
              <Alert severity="error" className="error" action={
                <Button color="inherit" size="small" disabled={flocksRetrying} onClick={retryFlocks}>
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
              {/* #914 — a native select and two plain date fields, the shape
                  every other filter on the app already uses. The MUI date
                  range picker is a paid @mui/x package and stays out of the
                  822 plan's dependency set. */}
              <Box sx={{ mb: "8px" }}>
                <TextField
                  select
                  fullWidth
                  size="small"
                  label={t("rangeLabel")}
                  value={rangeSelection}
                  slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
                  onChange={(e) => selectRange(e.target.value)}
                  sx={RANGE_FIELD_SX}
                >
                  {RANGE_PRESETS.map((days) => <option key={days} value={String(days)}>{presetLabel(days)}</option>)}
                  <option value="custom">{t("rangeCustomOption")}</option>
                </TextField>
                {customOpen && (
                  <Box sx={{ display: "flex", flexWrap: "wrap", gap: 1, mt: 1, alignItems: "flex-end" }}>
                    <FilterDateField
                      label={t("rangeFromLabel")} value={draftFrom}
                      slotProps={{ htmlInput: { max: latestDay } }}
                      onChange={(e) => setDraftFrom(e.target.value)}
                      sx={{ flex: "1 1 8rem", maxWidth: "none" }}
                    />
                    <FilterDateField
                      label={t("rangeToLabel")} value={draftTo}
                      slotProps={{ htmlInput: { max: latestDay } }}
                      onChange={(e) => setDraftTo(e.target.value)}
                      sx={{ flex: "1 1 8rem", maxWidth: "none" }}
                    />
                    <Button variant="outlined" color="inherit" onClick={applyCustomRange} sx={{ "&&": { minHeight: 44 } }}>
                      {t("rangeApply")}
                    </Button>
                  </Box>
                )}
                {rangeError !== null && (
                  <Alert severity="error" className="error" sx={{ mt: 1 }}>{rangeErrorText(rangeError)}</Alert>
                )}
              </Box>
              <Typography className="trend-context" variant="caption" color="text.secondary" sx={{ display: "block" }}>
                {t("layRateContext", { scope: contextScope, from: fmt.date(from), to: fmt.date(to) })}
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
                tip={trendTip} from={<FarmDate iso={from} />} to={<FarmDate iso={to} />} />
              <Typography className="trend-kpi"><span className="trend-fig">{trendData.henDay.current === null ? "—" : `${fmt.count(trendData.henDay.current, 1)}%`}</span><span className={deltaClass(trendData.henDay.delta)}>{deltaText(trendData.henDay.delta)}</span></Typography>
              <Typography className="trend-sub" variant="caption" sx={{ display: "block" }}>
                {t("henDaySubLabel", { range: rangeSpan, count: plotted.days, days: fmt.count(plotted.days) })}
              </Typography>
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
