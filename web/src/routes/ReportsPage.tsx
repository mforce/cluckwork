import { useCallback, useEffect, useRef, useState } from "react";
import { Trans, useTranslation } from "react-i18next";
import {
  Box, Button, List, ListItem, LinearProgress, Table, TableBody, TableCell, TableFooter, TableHead, TableRow, Typography,
} from "@mui/material";
import {
  getExpenseSummary, getProductionReport, getProfitReport, getSalesSummary,
} from "../api/cluckwork";
import type {
  ExpenseSummaryReport, ProductionReport, ProfitReport, SalesSummary,
} from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useFormat } from "../farm/useFormat";
import { FarmDate } from "../components/FarmDate";
import { FilterBar, FilterDateField } from "../components/FilterBar";
import { FieldConsole, ConsoleSubhead, CONSOLE_PAPER_HEAD_SX, LedgerTableContainer, CONSOLE_PANEL_SX, CONSOLE_SPLIT_SX } from "../components/FieldConsole";
import { daysBefore } from "../lib/dates";
import { useFarmToday } from "../farm/useFarm";
import { useAuth } from "../auth/useAuth";

// Prevent short values shrinking under auto table layout; free text can wrap.
const NOWRAP = { whiteSpace: "nowrap" as const };

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// #91 — core reports. Production renders for everyone (workers record it,
// workers read it); the money cards are admin-only and the API refuses
// workers on those routes regardless.
export function ReportsPage() {
  const { t } = useTranslation("reports");
  const fmt = useFormat();
  const { t: tc } = useTranslation("common");
  // Farm-local, not browser-local: since #35 the API judges "is this date in
  // the future?" against the FARM's day, so the pickers must agree (#123).
  const today = useFarmToday();
  const { isAdmin } = useAuth();
  const [from, setFrom] = useState(daysBefore(today, 6));
  const [to, setTo] = useState(today);
  const [production, setProduction] = useState<ProductionReport | null>(null);
  const [sales, setSales] = useState<SalesSummary | null>(null);
  const [expenses, setExpenses] = useState<ExpenseSummaryReport | null>(null);
  const [profit, setProfit] = useState<ProfitReport | null>(null);
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
    // All four sections clear together — a partial failure must not leave the
    // new production table next to the previous range's money figures.
    setProduction(null);
    setSales(null);
    setExpenses(null);
    setProfit(null);
    try {
      const prod = await getProductionReport(from, to);
      if (seq !== requestSeq.current) return;
      setProduction(prod);
      if (isAdmin) {
        // Sequential, not racing: each money card is one cheap aggregate.
        const s = await getSalesSummary(from, to);
        const e = await getExpenseSummary(from, to);
        const p = await getProfitReport(from, to);
        if (seq !== requestSeq.current) return;
        setSales(s);
        setExpenses(e);
        setProfit(p);
      }
    } catch (err) {
      if (seq === requestSeq.current) setError(errText(err));
    } finally {
      if (seq === requestSeq.current) setLoading(false);
    }
  }, [from, to, isAdmin]);

  useEffect(() => { void load(); }, [load]);

  return (
    <FieldConsole>
      <Typography variant="h2">{t("title")}</Typography>

      <FilterBar>
        <FilterDateField
          label={t("fromLabel")}
          value={from}
          slotProps={{ htmlInput: { max: to } }}
          onChange={(e) => setFrom(e.target.value)}
        />
        <FilterDateField
          label={t("toLabel")}
          value={to}
          slotProps={{ htmlInput: { max: today } }}
          onChange={(e) => setTo(e.target.value)}
        />
        <Button variant="outlined" color="inherit" sx={{ borderRadius: "4px" }} onClick={() => { setFrom(daysBefore(today, 6)); setTo(today); }}>{tc("clearFiltersButton")}</Button>
      </FilterBar>

      {error && (
        <p className="error" role="alert">
          {error}{" "}
          {/* A 429 from the report throttle (#311) is retryable, but a browser
              reload resets `from`/`to` to the default 7-day window instead of
              rerunning the chosen range — so offer an in-place retry that
              reruns load() with state untouched (review of #311/PR #335),
              same common:retry link pattern DailyEntryPage's prefill-failed
              banner uses. */}
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
              [t("henDayPctHeader"), production.periodHenDayPct === null ? "—" : `${fmt.count(production.periodHenDayPct, 1)}%`],
              ...(isAdmin && profit ? [[t("profitRowLabel"), fmt.money(profit.profitMinorUnits, profit.currencyCode, profit.currencyMinorUnit)]] : []),
              [t("lossesHeader"), fmt.count(production.days.reduce((sum, day) => sum + day.cracked + day.dirty + day.discarded, 0))],
            ].map(([label, value]) => <Box key={label} sx={{ p: 1.5, borderRight: "1px solid var(--rule)" }}>
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
                    <TableCell align="right">{d.henDayPct === null ? "—" : fmt.count(d.henDayPct, 1)}</TableCell>
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
                  <TableCell component="th" align="right">{production.periodHenDayPct === null ? "—" : fmt.count(production.periodHenDayPct, 1)}</TableCell>
                </TableRow>
              </TableFooter>
            </Table>
          </LedgerTableContainer>

        </>
      )}

      <Box sx={CONSOLE_SPLIT_SX}>
        {production && production.gradeTotals.length > 0 && (
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
        )}
        {isAdmin && sales && expenses && profit && (
          <Box component="section" aria-labelledby="reports-money-heading" sx={CONSOLE_PANEL_SX}>
            <Box component="header" sx={CONSOLE_PAPER_HEAD_SX}><h3 id="reports-money-heading">{t("moneyHeading")}</h3></Box>
            <Box component="dl" sx={{ m: 0 }}>
              <Box sx={{ display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto", gap: .5, py: 1, borderBottom: "1px solid var(--rule)" }}>
                <Typography component="dt" sx={{ fontWeight: 700 }}>{t("revenueRowLabel")}</Typography>
                <Typography component="dd" sx={{ m: 0, textAlign: "right", fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>{fmt.money(sales.revenueMinorUnits, sales.currencyCode, sales.currencyMinorUnit)}</Typography>
                <Typography component="dd" sx={{ m: 0, gridColumn: "1 / -1", fontSize: ".8rem", color: "text.secondary" }}>
                  {t("salesSummary", {
                    count: sales.confirmedCount,
                    confirmed: fmt.count(sales.confirmedCount),
                    revenue: fmt.money(sales.revenueMinorUnits, sales.currencyCode, sales.currencyMinorUnit),
                    paid: fmt.money(sales.paidMinorUnits, sales.currencyCode, sales.currencyMinorUnit),
                    outstanding: fmt.money(sales.outstandingMinorUnits, sales.currencyCode, sales.currencyMinorUnit),
                  })}
                  {sales.voidedCount > 0 ? t("salesVoidedSuffix", { count: sales.voidedCount, voided: fmt.count(sales.voidedCount) }) : ""}
                </Typography>
              </Box>
              <Box sx={{ display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto", gap: .5, py: 1, borderBottom: "1px solid var(--rule)" }}>
                <Typography component="dt" sx={{ fontWeight: 700 }}>{t("expensesRowLabel")}</Typography>
                <Typography component="dd" sx={{ m: 0, textAlign: "right", fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>{fmt.money(expenses.grandTotalMinorUnits, expenses.currencyCode, expenses.currencyMinorUnit)}</Typography>
                <Typography component="dd" sx={{ m: 0, gridColumn: "1 / -1", fontSize: ".8rem", color: "text.secondary" }}>
                  {expenses.categories.length === 0
                    ? t("expensesNone")
                    : expenses.categories
                        .map((c) => `${c.name} ${fmt.money(c.totalMinorUnits, expenses.currencyCode, expenses.currencyMinorUnit)}`)
                        .join(", ")}
                  {t("expensesTotalSuffix", {
                    total: fmt.money(expenses.grandTotalMinorUnits, expenses.currencyCode, expenses.currencyMinorUnit),
                  })}
                </Typography>
              </Box>
              <Box sx={{ display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto", gap: .5, py: 1, borderTop: "2px solid var(--ink)", fontWeight: 700 }}>
                <Typography component="dt" sx={{ fontWeight: 700 }}>{t("profitRowLabel")}</Typography>
                <Typography component="dd" sx={{ m: 0, textAlign: "right", fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>{fmt.money(profit.profitMinorUnits, profit.currencyCode, profit.currencyMinorUnit)}</Typography>
                <Typography component="dd" sx={{ m: 0, gridColumn: "1 / -1", fontSize: ".8rem", color: "text.secondary" }}>
                  <Trans ns="reports" i18nKey="profitLine"
                    values={{
                      revenue: fmt.money(profit.revenueMinorUnits, profit.currencyCode, profit.currencyMinorUnit),
                      expenses: fmt.money(profit.expensesMinorUnits, profit.currencyCode, profit.currencyMinorUnit),
                      profit: fmt.money(profit.profitMinorUnits, profit.currencyCode, profit.currencyMinorUnit),
                    }}
                    components={{ strong: <strong /> }}
                  />
                </Typography>
              </Box>
            </Box>
            <p className="muted">
              {t("profitFootnote")}
            </p>
          </Box>
        )}
      </Box>
    </FieldConsole>
  );
}
