// web/src/routes/Dashboard.tsx
import { useEffect, useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Bird, Egg, ShoppingCart } from "lucide-react";
import {
  Alert, Box, Button, Container, Stack, Typography, useMediaQuery,
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
import { useAuth } from "../auth/useAuth";
import { useFarmToday } from "../farm/useFarm";
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

  // The attention line never wraps: DIRECTION.md's fold point is two items at
  // 1280 and ONE at 390 — the same md (900px) boundary the sidebar/tab-bar
  // switch uses, not a flat cap at every width (#883 round 2, finding 1: the
  // desktop count was overcounting on a phone, wrapping or truncating a line
  // that must stay one line).
  const isDesktop = useMediaQuery(MD_UP_QUERY);
  const attentionCap = isDesktop ? 2 : 1;

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
  const missingHouses = allTiles === null ? [] : allTiles.filter((c) => c.entry === null).map((c) => c.flock);
  const attentionShown = missingHouses.slice(0, attentionCap);
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
              {allTiles !== null && entries !== null && (
                <Typography variant="caption" className="muted">
                  {t("todayInCount", { in: allTiles.length - missingHouses.length, count: allTiles.length })}
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
                    <Typography component="span" sx={{ fontWeight: 600 }}>{t("todaySoFarLabel")}</Typography>
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
                // #883 round 5 (owner's read of the #883 screenshots): a
                // shared grid, not a flex row per `<li>` — a flex row lets
                // each row's cells take whatever width their own content
                // needs, so the amount sat at a different x position on every
                // row. `display: grid` on the LIST plus `subgrid` on each row
                // (mockup: `.rows.sales`/`.sale`) makes every row share the
                // same column tracks, so amounts align down the page the same
                // way the mockup's table does. DIRECTION.md line 9's row is
                // customer/order number, eggs and grade, amount, status,
                // action — the eggs-and-grade column is still not rendered:
                // `listOrders`'s `OrderItem`s carry a line's `quantity` but
                // only an `eggGradeId`, never a grade NAME, and resolving one
                // needs a `listEggGrades()` fetch this screen does not
                // otherwise make (recorded on the PR and on #829; not built
                // in this round), so the grid below has one fewer column than
                // the mockup's until that lands.
                <Box component="ul" role="list" aria-label={t("salesPanelTitle")} className="dash-sales-list" sx={{
                  listStyle: "none", m: 0, p: 0,
                  display: "grid",
                  gridTemplateColumns: { xs: "1fr auto", md: "minmax(0,1fr) auto auto minmax(150px,auto)" },
                  columnGap: 1.5,
                }}
                >
                  {orders.map((o) => (
                    <Box component="li" key={o.id} aria-label={o.referenceNumber} sx={{
                      display: "grid",
                      gridColumn: "1 / -1",
                      gridTemplateColumns: "subgrid",
                      // At 390 there is no shared subgrid (the list itself
                      // reverts to a plain 2-column track above), so this
                      // stacks: customer + amount on one line, status +
                      // action on the next — the phone shape DIRECTION.md's
                      // mockup draws for `.sale`, minus the qty row this
                      // screen does not render yet.
                      gridTemplateAreas: { xs: '"who amt" "state act"', md: '"who amt state act"' },
                      columnGap: 1.5, rowGap: { xs: 0.25, md: 0 },
                      alignItems: "center",
                      py: 1, borderBottom: "1px solid var(--rule)",
                    }}
                    >
                      <Box sx={{ gridArea: "who", minWidth: 0 }}>
                        {/* Not `.cust` (styles.css: `overflow:hidden;
                            white-space:nowrap;text-overflow:ellipsis`) — that
                            truncated a real customer name to "KC…" once four
                            cells were forced onto one 390px line (#883 round
                            5 finding). The row now stacks at 390, so the name
                            gets its own full-width line and truncation is no
                            longer needed there; kept nowrap+ellipsis at
                            desktop, where the column is genuinely narrow. */}
                        <Typography component={Link} className="link" to={`/sales?customerId=${o.customerId}`} sx={{
                          display: "block", fontWeight: 500,
                          whiteSpace: { xs: "normal", md: "nowrap" },
                          overflow: { xs: "visible", md: "hidden" },
                          textOverflow: { xs: "clip", md: "ellipsis" },
                        }}
                        >
                          {rowCustomerName(o)}
                        </Typography>
                        <Typography variant="caption" className="muted">{o.referenceNumber}</Typography>
                      </Box>
                      {/* Right-aligned with tabular numerals so every row's
                          amount lines up on its ones digit — `.num` itself
                          stays un-right-aligned (styles.css, #829: scoped
                          narrow on purpose), so the alignment is this cell's
                          own, not a widened class. */}
                      <Typography component="span" className="num" sx={{ gridArea: "amt", textAlign: "right" }}>
                        {fmt.money(o.totalMinorUnits, o.currencyCode, o.currencyMinorUnit)}
                      </Typography>
                      <Box sx={{ gridArea: "state" }}>
                        <StatusDot status={o.status} label={statusLabel(o.status)} />
                      </Box>
                      {/* A draft order's row action (#883 round 2, finding
                          5; wording tightened in round 2's own Codex re-
                          review, finding 3). There is no per-order deep link
                          into Sales yet, so this lands on the customer's
                          WHOLE filtered order list, not the one order — that
                          list can hold several drafts for the same customer,
                          so the label says "review", never "confirm": this
                          control does not confirm anything itself, and a
                          word that claimed it did would be a real behavior
                          mismatch, not just one extra expected click (unlike
                          the Today row's Record/Continue, which land on the
                          one exact form for that flock and date). */}
                      {o.status === "Draft" && (
                        <Typography component={Link} to={`/sales?customerId=${o.customerId}`} variant="body2" sx={{ gridArea: "act", justifySelf: { md: "end" } }}>
                          {t("salesRowConfirmAction")}
                        </Typography>
                      )}
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

  // DIRECTION.md line 6 — the entry state with its time ("Recorded 06:40",
  // "Draft, saved 06:52"), farm-local (#883 round 2, finding 4). A Draft's
  // time is the last save (lastChangedAtUtc, falling back to createdAtUtc for
  // a Draft that has never been edited since); a submitted/locked/adjusted
  // entry's time is when it became official — madeOfficialAtUtc, sent only
  // for a record that has actually reached that step. A record with neither
  // timestamp (data predating #494, or a fixture that doesn't care) falls
  // back to the bare status word, exactly as before this slice.
  const stateTime = missing
    ? null
    : fmt.time(draft ? (entry.lastChangedAtUtc ?? entry.createdAtUtc) : (entry.madeOfficialAtUtc ?? null));
  const stateLabel = missing || stateTime === null
    ? (missing ? undefined : statusLabel(entry.status))
    : t(draft ? "entryStateDraftTime" : "entryStateRecordedTime", { time: stateTime });

  // A CSS grid, not a flex row: the DIRECTION.md phone layout reflows the
  // SAME four pieces (name, state, action, count) into three lines instead
  // of shrinking them onto one — a flex row with fixed minWidths overflowed
  // a 390px viewport (measured: 475px, phone.spec.ts's viewport-overflow
  // walk). `gridTemplateAreas` names the reflow directly rather than
  // reordering flex children with `order`.
  return (
    <Box role="group" aria-label={flock.name} sx={{
      display: "grid",
      // The mockup's own model (dashboard.html `.house`): name 150px, status
      // 1fr, action auto, count 110px. #883 round 5's owner read: a fixed
      // 200px action column (this row's previous shape) squeezed the status
      // column so "Draft, saved 05:26" wrapped onto two lines. `auto` is safe
      // here because the two actions that can render are no longer
      // budget-hungry: Record only ever renders for the seeder's never-filing
      // catalog flocks, whose names are short and fixed, and Continue is now
      // ruled text with no button chrome to balloon (see below) — an
      // arbitrarily long flock name racing a real action is a residual risk
      // this model accepts, same as the mockup does.
      gridTemplateColumns: { xs: "1fr auto", md: "minmax(0,1fr) auto auto 110px" },
      gridTemplateAreas: { xs: '"name num" "meta meta" "act act"', md: '"name meta act num"' },
      columnGap: 1.5, rowGap: { xs: 0.25, md: 0 },
      alignItems: "center",
      minHeight: { xs: 52, md: 36 }, py: { xs: 1, md: 0.5 },
      borderBottom: "1px solid var(--rule)",
      borderLeft: missing ? "3px solid var(--warn)" : "3px solid transparent",
      pl: missing ? 1 : 0,
      bgcolor: missing ? { xs: "var(--tint-warn)", md: "transparent" } : "transparent",
    }}
    >
      <Typography component={Link} to={href} sx={{ gridArea: "name", fontWeight: 500, overflow: { md: "hidden" }, textOverflow: { md: "ellipsis" }, whiteSpace: { md: "nowrap" } }}
        aria-label={missing
          ? t("tileLinkLabelMissing", { flock: flock.name })
          : t("tileLinkLabel", { flock: flock.name })}
        title={missing ? t("recordTodayHint") : undefined}
      >
        {flock.name}
      </Typography>
      {/* `white-space: nowrap` (#883 round 5): the status cell is the one
          piece of this row that must never wrap — "Draft, saved 05:26" onto a
          second line is the defect this fix pins. */}
      <Box sx={{ gridArea: "meta", whiteSpace: "nowrap" }}>
        {missing
          ? <StatusDot label={t("noEntryBadge")} forceColor="var(--warn)" />
          : <StatusDot status={entry.status} label={stateLabel} />}
      </Box>
      {(missing || draft) && (
        <Box sx={{ gridArea: "act", textAlign: { xs: "stretch", md: "right" }, whiteSpace: { md: "nowrap" } }}>
          {missing && (
            // The single filled button on the page at 1280 (owner amendment,
            // #864) and the 48px full-width phone action (DIRECTION.md).
            <Button component={Link} to={href} variant="contained" size="small"
              sx={{ width: { xs: "100%", md: "auto" }, minHeight: { xs: 48, md: "auto" } }}
            >
              {t("recordHouseAction", { flock: flock.name })}
            </Button>
          )}
          {draft && (
            // Ruled text (DIRECTION.md line 7), not a filled/text Button:
            // `Button variant="text"` rendered bold and brand-coloured, the
            // #883 round 5 owner finding — the same ruled-text Typography+Link
            // pattern the sales row's "Review to confirm" action already
            // uses below, so this row and that one share one convention.
            <Typography component={Link} to={href} variant="body2"
              sx={{ display: { xs: "inline-block", md: "inline" } }}
            >
              {t("continueHouseAction", { flock: flock.name })}
            </Typography>
          )}
        </Box>
      )}
      <Typography component="span" className="num" sx={{ gridArea: "num", textAlign: "right" }}>
        {entry ? fmt.count(entry.totalEggs) : "—"}
      </Typography>
    </Box>
  );
}

// DIRECTION.md line 15: status is a word with an 8px dot, never a filled
// badge — success (recorded, paid), --stat-accent (allocated), a hollow ring
// (draft and anything else with no mapped colour), --warn (not recorded,
// low). Dashboard-local: `StatusBadge` (../components/StatusBadge) stays
// untouched for the other screens, its own conversion is #831's, so this
// duplicates StatusBadge's small VARIANT table rather than exporting it —
// the two are expected to diverge until #831 unifies them.
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
