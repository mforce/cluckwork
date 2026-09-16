// web/src/routes/Dashboard.tsx
import { useEffect, useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Bird, Egg, ShoppingCart } from "lucide-react";
import {
  Alert, Box, Button, Container, Stack, Typography,
} from "@mui/material";
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
import type { CaptureTile, DayStripData, DayStripSlot } from "../lib/dashboard";
import i18n from "../i18n";
import { statusLabel } from "../i18n/enums";

const RECENT_ORDERS = 5;
// Server clamps list limits at 500. One farm won't exceed that in Phase 1.x;
// past 500 flocks the tail silently drops — revisit with real paging if that
// day comes.
const MAX_PAGE = 500;
// The attention line never wraps: two items fit at 1280 before folding into
// "+N more" (DIRECTION.md, amended on #864: ruled separators, not a middle
// dot). Phone gets the same cap here — see the note by ATTENTION_SHOWN below
// for why the direction's "one item at 390" is not implemented this slice.
const ATTENTION_SHOWN = 2;

// F5 (#41) → #654 → #829: the landing page answers the 6 am question — which
// houses have no entry yet, and is lay rate normal? A ruled Today list, the
// missing ones first and at most 12 (a link carries the rest), the last 14
// days as a bar strip with the production report's own hen-day % for the
// last 7 complete days against the 7 before, stock as one stacked bar by
// grade, and recent sales as a ruled list. #829 converts the shell to MUI
// (DIRECTION.md, the confirmed mockup at docs/designs/864-visual-language/)
// without changing any of the data pipeline below.
//
// Composed client-side from existing read endpoints (6 parallel GETs). The
// trend is the production report — computed server-side in one place, so the
// page never sums report rows: two calls, one per 7-day window, and the
// server's periodHenDayPct from each. Panels degrade independently: one
// failed fetch blanks its section, not the page.
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

  if (loading) {
    return (
      <Container maxWidth={false} sx={{ maxWidth: 1120 }}>
        <Typography variant="h2">{t("title")}</Typography>
        <Typography className="muted">{tc("loading")}</Typography>
      </Container>
    );
  }
  if (error) {
    return (
      <Container maxWidth={false} sx={{ maxWidth: 1120 }}>
        <Typography variant="h2">{t("title")}</Typography>
        <Alert severity="error" className="error">{error}</Alert>
      </Container>
    );
  }

  const panelError = <Alert severity="error" className="error">{t("panelLoadError")}</Alert>;
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
      // A window where no flock ever owed a filing is not a window of missing
      // ones. The day-level fix for that landed without this, so a new farm's
      // strip drew fourteen blank-but-blameless slots and then announced that
      // none of them had an entry.
      if (line.partial === 0 && line.unrecorded === 0) return t("trendStripLabelNoFlocks");
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

  // Owner amendment on #864 (2026-09-16): a caption under "Today so far" that
  // gives the running total a reference — "Yesterday by close: N" — sourced
  // from the SAME data the 14-day strip already reads (its last slot, since
  // the strip runs oldest-first and ends on yesterday), never a second fetch.
  // Only when yesterday was a COMPLETE day: a partial or unrecorded yesterday
  // has no figure honest enough to caption "by close".
  const yesterdaySlot = trendData?.line.slots.at(-1) ?? null;
  const yesterdayByClose = yesterdaySlot?.kind === "recorded" ? yesterdaySlot.eggs : null;

  // The attention line (D3.3, #829): missing houses only — the desktop-only
  // "Needs attention" list combining a second data source (stock floors) was
  // proposed on #864 and the owner did not take it, so this line has exactly
  // one source. Nothing renders when every house is in.
  const missingHouses = tiles === null ? [] : tiles.shown.filter((c) => c.entry === null).map((c) => c.flock);
  const attentionShown = missingHouses.slice(0, ATTENTION_SHOWN);
  const attentionMore = missingHouses.length - attentionShown.length;

  return (
    <Container maxWidth={false} sx={{ maxWidth: 1120, py: { xs: 3, md: 4.5 } }}>
      <Typography variant="h2">{t("title")}</Typography>
      <Typography className="muted"><FarmDate iso={today} /></Typography>

      {/* #829/#864 — a plain text line, not a boxed `Alert`: DIRECTION.md's
          confirmed mockup renders this as a hairline-weight status line (a
          dot mark, ruled separators between items), which an `Alert`'s fill
          and padding would read heavier than. This amends D3.3's "Alert
          severity='warning'" row — the owner confirmed the lighter mockup
          later (#864 issue comments, 2026-09-16) — D3.3 is amended in the PR
          body per AGENTS.md's "DIRECTION.md wins" rule. */}
      {missingHouses.length > 0 && (
        <Box sx={{ display: "flex", alignItems: "center", gap: 1.5, mt: 1.5, minHeight: 24, overflow: "hidden", whiteSpace: "nowrap" }}>
          <Box component="span" aria-hidden sx={{ width: 8, height: 8, borderRadius: "50%", bgcolor: "var(--warn)", flexShrink: 0 }} />
          {attentionShown.map((flock, i) => (
            <Box key={flock.id} sx={{ display: "flex", alignItems: "center", gap: 1.5 }}>
              {i > 0 && <Box component="span" aria-hidden sx={{ width: "1px", height: 12, bgcolor: "var(--rule-strong)" }} />}
              <Typography component="span" variant="body1">{t("attentionHouseNotRecorded", { flock: flock.name })}</Typography>
            </Box>
          ))}
          {attentionMore > 0 && (
            <Typography component={Link} to="/daily-entry" variant="body1">
              {t("attentionMore", { count: attentionMore })}
            </Typography>
          )}
        </Box>
      )}

      <Box sx={{
        display: "grid",
        gridTemplateColumns: { xs: "1fr", md: "minmax(0, 1fr) 320px" },
        columnGap: 5, mt: { xs: 4, md: 4.5 },
      }}
      >
        <Stack spacing={5} sx={{ minWidth: 0 }}>
          {/* -------------------------------------------------------- Today */}
          <Box component="section">
            <Box sx={{
              display: "flex", justifyContent: "space-between", alignItems: "baseline",
              pb: 1, borderBottom: "1px solid var(--rule-strong)",
            }}
            >
              <Typography variant="h3"><Link to="/daily-entry">{t("todayPanelTitle")}</Link></Typography>
              {tiles !== null && entries !== null && (
                <Typography variant="caption" className="muted">
                  {t("todayInCount", { in: tiles.shown.length - missingHouses.length, total: tiles.shown.length })}
                </Typography>
              )}
            </Box>
            {tiles === null || entries === null ? panelError : (
              tiles.shown.length === 0 ? (
                <EmptyState icon={Bird} message={t("noFlocksMessage")} />
              ) : (
                <>
                  {tiles.shown.map((tile) => <TodayRow key={tile.flock.id} tile={tile} today={today} fmt={fmt} t={t} />)}
                  <Box sx={{
                    display: "flex", justifyContent: "space-between", mt: -0.125,
                    borderTop: "3px double var(--rule-strong)", pt: 1.25, fontWeight: 600,
                  }}
                  >
                    <Typography component="span" sx={{ fontWeight: 600 }}>{t("todayEggsTotal", { total: fmt.count(todaysEggs(entries)) })}</Typography>
                    <Typography component="span" className="num" sx={{ fontWeight: 600, fontSize: "1.25rem" }}>{fmt.count(todaysEggs(entries))}</Typography>
                  </Box>
                  {yesterdayByClose !== null && (
                    <Typography variant="caption" className="muted" sx={{ display: "block", mt: 0.5 }}>
                      {t("yesterdayByClose", { total: fmt.count(yesterdayByClose) })}
                    </Typography>
                  )}
                  {tiles.hidden > 0 && (
                    <Typography component={Link} to="/daily-entry" variant="body2" sx={{ display: "inline-block", mt: 1 }}>
                      {t("moreFlocks", { count: tiles.hidden, total: fmt.count(tiles.hidden) })}
                    </Typography>
                  )}
                </>
              )
            )}
          </Box>

          {/* --------------------------------------------------- Recent sales */}
          {canSeeSales && (
            <Box component="section">
              <Box sx={{
                display: "flex", justifyContent: "space-between", alignItems: "baseline",
                pb: 1, borderBottom: "1px solid var(--rule-strong)",
              }}
              >
                <Typography variant="h3"><Link to="/sales">{t("salesPanelTitle")}</Link></Typography>
              </Box>
              {orders === null ? panelError : orders.length === 0 ? (
                <EmptyState icon={ShoppingCart} message={t("noOrdersMessage")} />
              ) : (
                <Box component="ul" role="list" sx={{ listStyle: "none", m: 0, p: 0 }}>
                  {orders.map((o) => (
                    <Box component="li" key={o.id} aria-label={o.referenceNumber} sx={{
                      display: "flex", justifyContent: "space-between", alignItems: "center", gap: 1.5,
                      py: 1, borderBottom: "1px solid var(--rule)",
                    }}
                    >
                      <Box sx={{ minWidth: 0 }}>
                        <Typography component={Link} className="link cust" to={`/sales?customerId=${o.customerId}`} sx={{ display: "block", fontWeight: 500 }}>
                          {rowCustomerName(o)}
                        </Typography>
                        <Typography variant="caption" className="muted">{o.referenceNumber}</Typography>
                      </Box>
                      <StatusBadge status={o.status} label={statusLabel(o.status)} />
                      <Typography component="span" className="num">{fmt.money(o.totalMinorUnits, o.currencyCode, o.currencyMinorUnit)}</Typography>
                    </Box>
                  ))}
                </Box>
              )}
            </Box>
          )}
        </Stack>

        <Stack spacing={5} sx={{ borderLeft: { md: "1px solid var(--rule)" }, pl: { md: 5 }, mt: { xs: 4, md: 0 } }}>
          {/* ------------------------------------------------------- Stock */}
          <Box component="section">
            <Box sx={{
              display: "flex", justifyContent: "space-between", alignItems: "baseline",
              pb: 1, borderBottom: "1px solid var(--rule-strong)",
            }}
            >
              <Typography variant="h3"><Link to="/stock">{t("stockPanelTitle")}</Link></Typography>
            </Box>
            {bar === null || stock === null ? panelError : stock.length === 0 ? (
              <EmptyState icon={Egg} message={t("noStockMessage")} />
            ) : (
              <>
                {/* #777 — the total is the whole the bar divides, so it leads. */}
                <Typography className="stock-total" sx={{ mt: 1.5 }}>
                  <span className="stock-fig">{fmt.count(bar.totalAvailable)}</span>
                  {/* The space matters: .stock-fig is display:block so the two
                      never touch on screen, but the paragraph's text is what a
                      copy-paste and any text consumer gets, and without it that
                      reads "1egg available". */}
                  {" "}{t("eggsAvailableLabel", { count: bar.totalAvailable })}
                </Typography>
                <StockBar data={bar} />
                {/* The ledger is the bar's text of record (the track itself is
                    aria-hidden). It replaces the grade run-on the caption used to
                    carry, which asked the reader to count segments and trust the
                    order matched, and it carries each grade's share so a grade
                    worth well under a percent is readable as a number. */}
                {bar.segments.length > 0 && (
                  <Box component="ul" role="list" aria-label={t("stockLedgerLabel")} sx={{ listStyle: "none", m: "12px 0 0", p: 0 }}>
                    {bar.segments.map((s) => (
                      <Box component="li" key={s.eggGradeId} sx={{
                        display: "flex", alignItems: "center", gap: 1.25,
                        py: 1, borderBottom: "1px solid var(--rule)",
                      }}
                      >
                        <span className={`swatch grade-${s.colorIndex}`} aria-hidden="true" />
                        <Typography component="span" sx={{ flexGrow: 1 }}>{s.gradeName}</Typography>
                        <Typography component="span" className="num">{fmt.count(s.available)}</Typography>
                        <Typography component="span" className="num muted" sx={{ width: "3.5rem", textAlign: "right" }}>{`${fmt.count(s.pct, 1)}%`}</Typography>
                      </Box>
                    ))}
                  </Box>
                )}
                {bar.totalRestricted > 0 && (
                  <Typography className="muted" variant="caption" sx={{ display: "block", mt: 1 }}>
                    {t("stockCaptionRestricted", { restricted: fmt.count(bar.totalRestricted) })}
                  </Typography>
                )}
              </>
            )}
          </Box>

          {/* -------------------------------------------------- Last 14 days */}
          <Box component="section">
            <Box sx={{
              display: "flex", justifyContent: "space-between", alignItems: "baseline",
              pb: 1, borderBottom: "1px solid var(--rule-strong)",
            }}
            >
              <Typography variant="h3"><Link to="/reports">{t("trendPanelTitle")}</Link></Typography>
            </Box>
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
                <Typography className="trend-kpi">
                  <span className="trend-fig">
                    {trendData.henDay.current === null ? "—" : `${fmt.count(trendData.henDay.current, 1)}%`}
                  </span>
                  <span className={deltaClass(trendData.henDay.delta)}>{deltaText(trendData.henDay.delta)}</span>
                </Typography>
                <Typography className="trend-sub" variant="caption">{t("henDaySubLabel")}</Typography>
              </>
            )}
          </Box>
        </Stack>
      </Box>
    </Container>
  );
}

// One Today row: name, entry state, action, count. The missing house carries
// a 3px `--warn` left rule and its action is the page's single filled
// button (owner amendment on #864, 2026-09-16 — amends DIRECTION.md's
// "ruled text at 1280" for this one row); a Draft entry gets a ruled-text
// "Continue" action; a submitted/locked/voided entry has no action cell,
// only its name links through.
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

  return (
    <Box role="group" aria-label={flock.name} sx={{
      display: "flex", alignItems: "center", gap: 1.5, minHeight: 36, py: 0.5,
      borderBottom: "1px solid var(--rule)",
      borderLeft: missing ? "3px solid var(--warn)" : "3px solid transparent",
      pl: missing ? 1 : 0,
    }}
    >
      <Typography component={Link} to={href} sx={{ fontWeight: 500, minWidth: "9rem" }}
        aria-label={missing
          ? t("tileLinkLabelMissing", { flock: flock.name })
          : t("tileLinkLabel", { flock: flock.name })}
        title={missing ? t("recordTodayHint") : undefined}
      >
        {flock.name}
      </Typography>
      <Box sx={{ flexGrow: 1 }}>
        {missing
          ? <span className="badge badge-warn">{t("noEntryBadge")}</span>
          : <StatusBadge status={entry.status} label={statusLabel(entry.status)} />}
      </Box>
      <Box sx={{ minWidth: "8rem", textAlign: "right" }}>
        {missing && (
          <Button component={Link} to={href} variant="contained" size="small">
            {t("recordHouseAction", { flock: flock.name })}
          </Button>
        )}
        {draft && (
          <Button component={Link} to={href} variant="text" size="small">
            {t("continueHouseAction", { flock: flock.name })}
          </Button>
        )}
      </Box>
      <Typography component="span" className="num" sx={{ minWidth: "3rem", textAlign: "right" }}>
        {entry ? fmt.count(entry.totalEggs) : "—"}
      </Typography>
    </Box>
  );
}
