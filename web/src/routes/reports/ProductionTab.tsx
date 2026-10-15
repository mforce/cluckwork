import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Box, Button, LinearProgress, List, ListItem, Table, TableBody, TableCell, TableFooter, TableHead, TableRow, Typography } from "@mui/material";
import { getProductionReport } from "../../api/cluckwork";
import type { ProductionReport } from "../../api/cluckwork";
import { errText } from "../../lib/errText";
import { HenDayOver100Flag } from "../../components/HenDayOver100Flag";
import { useFormat } from "../../farm/useFormat";
import { FarmDate } from "../../components/FarmDate";
import { ConsoleSubhead, LedgerTableContainer, CONSOLE_PANEL_SX, CONSOLE_PAPER_HEAD_SX, CONSOLE_SPLIT_SX } from "../../components/FieldConsole";

// Prevent short values shrinking under auto table layout; free text can wrap.
const NOWRAP = { whiteSpace: "nowrap" as const };

const FLOCK_CAP = 10;

export function ProductionTab({ from, to }: { from: string; to: string }) {
  const { t } = useTranslation("reports");
  const fmt = useFormat();
  const { t: tc } = useTranslation("common");
  const [production, setProduction] = useState<ProductionReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  // Monotonic request id: a slow response for an OLD range must neither
  // overwrite the current range's figures nor clear its loading state
  // (codex review of #92).
  const requestSeq = useRef(0);

  const load = useCallback(async () => {
    const seq = ++requestSeq.current;
    setLoading(true);
    setError(null);
    setProduction(null);
    try {
      const prod = await getProductionReport(from, to);
      if (seq !== requestSeq.current) return;
      setProduction(prod);
    } catch (err) {
      if (seq === requestSeq.current) setError(errText(err));
    } finally {
      if (seq === requestSeq.current) setLoading(false);
    }
  }, [from, to]);

  useEffect(() => { void load(); }, [load]);

  const [showAllFlocks, setShowAllFlocks] = useState(false);
  const flockRows = production === null ? [] : [...production.flockTotals].sort((a, b) => b.eggs - a.eggs);
  const visibleFlocks = showAllFlocks || flockRows.length <= FLOCK_CAP
    ? flockRows
    : flockRows.slice(0, FLOCK_CAP);

  return (
    <>
      {error && (
        <p className="error" role="alert">
          {error}{" "}
          {/* A 429 from the report throttle (#311) is retryable, but a browser
              reload resets `from`/`to` to the default 7-day window instead of
              rerunning the chosen range — so offer an in-place retry that
              reruns load() with state untouched (review of #311/PR #335),
              same common:retry link pattern DailyEntryPage's prefill-failed
              banner uses. The range now survives a reload in the URL (#1308),
              so the retry is belt-and-braces — kept because the error state
              itself is what a reload clears. */}
          <button type="button" className="link" onClick={() => void load()}>
            {tc("retry")}
          </button>
        </p>
      )}
      {loading && <p className="muted">{tc("loading")}</p>}

      {production && (
        <>
          <Box component="dl" aria-label={t("periodRowLabel")} sx={{ display: "grid", gridAutoFlow: { xs: "column" }, gridAutoColumns: { xs: "115px", md: "minmax(0, 1fr)" }, overflowX: "auto", borderTop: "2px solid var(--ink)", borderBottom: "1px solid var(--rule)", my: 2 }}>
            {[
              [t("eggsHeader"), fmt.count(production.totalEggs)],
              [t("sellableHeader"), fmt.count(production.totalSellable)],
              [t("henDayPctHeader"), production.periodHenDayPct === null ? "—" : <>{fmt.count(production.periodHenDayPct, 1)}%<HenDayOver100Flag ratedEggs={production.totalRatedEggs} recordedHenDays={production.totalRecordedHenDays} /></>],
              // #1297 — server totals and rates, not sums over `days` in the browser.
              [t("lossesHeader"), fmt.count(production.totalLosses)],
              [t("sellablePctHeader"), production.periodSellablePct === null ? "—" : <>{fmt.count(production.periodSellablePct, 1)}%</>],
              [t("lossPctHeader"), production.periodLossPct === null ? "—" : <>{fmt.count(production.periodLossPct, 1)}%</>],
            ].map(([label, value]) => <Box key={String(label)} sx={{ p: 1.5, borderRight: "1px solid var(--rule)" }}>
              <Typography component="dt" variant="body2" sx={{ fontSize: ".7rem", color: "text.secondary" }}>{label}</Typography>
              <Typography component="dd" variant="body2" sx={{ m: 0, mt: .5, fontFamily: "Georgia, serif", fontSize: "1.4rem", fontVariantNumeric: "tabular-nums" }}>{value}</Typography>
            </Box>)}
          </Box>
          <ConsoleSubhead title={t("productionHeading")} caption={t("productionCaption")} />
          <LedgerTableContainer>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell>{t("dateHeader")}</TableCell>
                  <TableCell align="right">{t("eggsHeader")}</TableCell>
                  <TableCell align="right">{t("lossesHeader")}</TableCell>
                  <TableCell align="right">{t("sellableHeader")}</TableCell>
                  {/* Condition is loss stock, separate from the hand-graded Sellable remainder. */}
                  <TableCell align="right">{t("conditionHeader")}</TableCell>
                  <TableCell align="right">{t("deathsHeader")}</TableCell>
                  <TableCell align="right">{t("henDaysHeader")}</TableCell>
                  {/* The rate uses only flocks that recorded; Hen-days includes every live bird. */}
                  <TableCell align="right">{t("recordedHenDaysHeader")}</TableCell>
                  <TableCell align="right">{t("ratedEggsHeader")}</TableCell>
                  <TableCell align="right">{t("henDayPctHeader")}</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {production.days.map((d) => (
                  <TableRow key={d.date}>
                    <TableCell sx={NOWRAP}><FarmDate iso={d.date} /></TableCell>
                    <TableCell align="right">{fmt.count(d.totalEggs)}</TableCell>
                    <TableCell align="right">{fmt.count(d.cracked)}/{fmt.count(d.dirty)}/{fmt.count(d.discarded)}</TableCell>
                    <TableCell align="right">{fmt.count(d.sellable)}</TableCell>
                    <TableCell align="right">{fmt.count(d.fromCounts)}</TableCell>
                    <TableCell align="right">{fmt.count(d.deaths)}</TableCell>
                    <TableCell align="right">{fmt.count(d.henDays)}</TableCell>
                    <TableCell align="right">{fmt.count(d.recordedHenDays)}</TableCell>
                    <TableCell align="right">{fmt.count(d.ratedEggs)}</TableCell>
                    <TableCell align="right">{d.henDayPct === null ? "—" : fmt.count(d.henDayPct, 1)}<HenDayOver100Flag ratedEggs={d.ratedEggs} recordedHenDays={d.recordedHenDays} /></TableCell>
                  </TableRow>
                ))}
              </TableBody>
              <TableFooter>
                <TableRow>
                  <TableCell component="th">{t("periodRowLabel")}</TableCell>
                  <TableCell component="th" align="right">{fmt.count(production.totalEggs)}</TableCell>
                  <TableCell component="th"></TableCell>
                  <TableCell component="th" align="right">{fmt.count(production.totalSellable)}</TableCell>
                  <TableCell component="th" align="right">{fmt.count(production.totalFromCounts)}</TableCell>
                  <TableCell component="th" align="right">{fmt.count(production.totalDeaths)}</TableCell>
                  <TableCell component="th" align="right">{fmt.count(production.totalHenDays)}</TableCell>
                  <TableCell component="th" align="right">{fmt.count(production.totalRecordedHenDays)}</TableCell>
                  <TableCell component="th" align="right">{fmt.count(production.totalRatedEggs)}</TableCell>
                  <TableCell component="th" align="right">{production.periodHenDayPct === null ? "—" : fmt.count(production.periodHenDayPct, 1)}<HenDayOver100Flag ratedEggs={production.totalRatedEggs} recordedHenDays={production.totalRecordedHenDays} /></TableCell>
                </TableRow>
              </TableFooter>
            </Table>
          </LedgerTableContainer>

          {/* #1299 — the same period broken down by flock, one row per flock
              that filed an official entry in the range. Server-computed from
              the same walk as the totals above, never summed in the browser.
              #1308 — capped at the ten highest-egg flocks; the button below
              reveals the rest. The daily table's Period totals row above is
              unaffected. */}
          {production.flockTotals.length > 0 && (
            <>
              <ConsoleSubhead title={t("flockHeading")} caption={t("flockCaption")} />
              <LedgerTableContainer>
                <Table size="small">
                  <TableHead>
                    <TableRow>
                      <TableCell>{t("flockHeader")}</TableCell>
                      <TableCell align="right">{t("eggsHeader")}</TableCell>
                      <TableCell align="right">{t("flockLossesHeader")}</TableCell>
                      <TableCell align="right">{t("sellableHeader")}</TableCell>
                      <TableCell align="right">{t("deathsHeader")}</TableCell>
                      <TableCell align="right">{t("recordedHenDaysHeader")}</TableCell>
                      <TableCell align="right">{t("ratedEggsHeader")}</TableCell>
                      {/* The rate uses only the flocks that recorded; null, not 0, for one with no exposure. */}
                      <TableCell align="right">{t("henDayPctHeader")}</TableCell>
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {visibleFlocks.map((f) => (
                      <TableRow key={f.flockId}>
                        {/* Flock names are NOT unique — the placement date rides
                            beside the name so two same-named rows stay apart. */}
                        <TableCell sx={NOWRAP}>
                          {f.flockName}
                          {/* Secondary line: a step below the ledger cell's own
                              .75rem (FieldConsole's small size), muted, on its
                              own line — the name stays the main text. */}
                          <Box component="span" sx={{ display: "block", fontSize: ".625rem", color: "text.secondary" }}>
                            {t("flockPlacedLabel")}: <FarmDate iso={f.placedOn} />
                          </Box>
                        </TableCell>
                        <TableCell align="right">{fmt.count(f.eggs)}</TableCell>
                        <TableCell align="right">{fmt.count(f.losses)}</TableCell>
                        <TableCell align="right">{fmt.count(f.sellable)}</TableCell>
                        <TableCell align="right">{fmt.count(f.deaths)}</TableCell>
                        <TableCell align="right">{fmt.count(f.recordedHenDays)}</TableCell>
                        <TableCell align="right">{fmt.count(f.ratedEggs)}</TableCell>
                        <TableCell align="right">{f.henDayPct === null ? "—" : fmt.count(f.henDayPct, 1)}<HenDayOver100Flag ratedEggs={f.ratedEggs} recordedHenDays={f.recordedHenDays} /></TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </LedgerTableContainer>
              {flockRows.length > FLOCK_CAP && !showAllFlocks && (
                <Box sx={{ mt: 1 }}>
                  <Button variant="outlined" color="inherit" sx={{ borderRadius: "4px" }} onClick={() => setShowAllFlocks(true)}>
                    {t("showAllFlocksButton", { count: flockRows.length })}
                  </Button>
                </Box>
              )}
            </>
          )}

          {production.gradeTotals.length > 0 && (
            <Box sx={CONSOLE_SPLIT_SX}>
              <Box sx={CONSOLE_PANEL_SX}>
                <Box component="header" sx={CONSOLE_PAPER_HEAD_SX}><h3>{t("gradeTotalsLabel")}</h3></Box>
                <List aria-label={t("gradeTotalsLabel")} disablePadding>
                  {production.gradeTotals.map((grade) => (
                    <ListItem key={grade.name} sx={{ display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto minmax(50px, 1fr) auto", gap: 2, px: 0, py: 1.5, borderBottom: "1px solid var(--rule)" }}>
                      <Typography component="span" sx={{ fontFamily: "Georgia, serif", fontWeight: 600 }}>{grade.name}</Typography>
                      <strong>{fmt.count(grade.quantity)}</strong>
                      <LinearProgress variant="determinate" value={100 * grade.quantity / Math.max(1, ...production.gradeTotals.map((g) => g.quantity))} aria-label={grade.name} sx={{ height: 8, borderRadius: "var(--r-pill)", bgcolor: "var(--surface-2)", "& .MuiLinearProgress-bar": { bgcolor: "var(--stat-accent)" } }} />
                      <Typography component="span" sx={{ fontSize: ".75rem", color: "text.secondary" }}>{t("gradeUnit")}</Typography>
                    </ListItem>
                  ))}
                </List>
              </Box>
            </Box>
          )}
        </>
      )}
    </>
  );
}
