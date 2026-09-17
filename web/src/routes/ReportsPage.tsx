import { useCallback, useEffect, useRef, useState } from "react";
import { Trans, useTranslation } from "react-i18next";
import {
  Table, TableBody, TableCell, TableContainer, TableFooter, TableHead, TableRow, Typography,
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
import { daysBefore } from "../lib/dates";
import { useFarmToday } from "../farm/useFarm";
import { useAuth } from "../auth/useAuth";

// MUI's auto table layout shrinks any wrappable cell below its content width,
// so a short value (a date) is pinned; free text wraps (#897 convention).
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
    <section>
      <Typography variant="h2">{t("title")}</Typography>

      {/* #653 — the only controls on this screen are the date range, so the
          whole bar is the filter bar (Reports has no other filter to keep
          separate, unlike History/Feed/Water below). */}
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
          <h3>{t("productionHeading")}</h3>
          <TableContainer>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell>{t("dateHeader")}</TableCell>
                  <TableCell align="right">{t("eggsHeader")}</TableCell>
                  <TableCell align="right">{t("lossesHeader")}</TableCell>
                  <TableCell align="right">{t("sellableHeader")}</TableCell>
                  {/* #396 — beside Sellable, not folded into it: Sellable is the
                      hand-graded remainder, Condition is what the cracked/dirty
                      counters contributed as stock. */}
                  <TableCell align="right">{t("conditionHeader")}</TableCell>
                  <TableCell align="right">{t("deathsHeader")}</TableCell>
                  <TableCell align="right">{t("henDaysHeader")}</TableCell>
                  {/* #780 — the percentage's own numerator and denominator. Eggs
                      ÷ Hen-days stopped reproducing Hen-day %: Hen-days is every
                      bird alive, the rate divides by the flocks that recorded,
                      and its numerator excludes any flock whose birds it excludes.
                      Showing only one half left the row inviting a division that
                      gives the wrong answer. The gap between Hen-days and Recorded
                      is what the period is missing. */}
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
          </TableContainer>
          {production.gradeTotals.length > 0 && (
            <p className="muted">
              {t("gradeTotalsLabel")}{" "}
              {production.gradeTotals.map((g) => `${g.name} ${fmt.count(g.quantity)}`).join(", ")}
            </p>
          )}
        </>
      )}

      {isAdmin && sales && expenses && profit && (
        <>
          <h3>{t("moneyHeading")}</h3>
          <TableContainer>
            <Table size="small">
              <TableBody>
                <TableRow>
                  <TableCell component="th">{t("salesRowLabel")}</TableCell>
                  <TableCell>
                    {t("salesSummary", {
                      count: sales.confirmedCount,
                      confirmed: fmt.count(sales.confirmedCount),
                      revenue: fmt.money(sales.revenueMinorUnits, sales.currencyCode, sales.currencyMinorUnit),
                      paid: fmt.money(sales.paidMinorUnits, sales.currencyCode, sales.currencyMinorUnit),
                      outstanding: fmt.money(sales.outstandingMinorUnits, sales.currencyCode, sales.currencyMinorUnit),
                    })}
                    {sales.voidedCount > 0 ? t("salesVoidedSuffix", { count: sales.voidedCount, voided: fmt.count(sales.voidedCount) }) : ""}
                  </TableCell>
                </TableRow>
                <TableRow>
                  <TableCell component="th">{t("expensesRowLabel")}</TableCell>
                  <TableCell>
                    {expenses.categories.length === 0
                      ? t("expensesNone")
                      : expenses.categories
                          .map((c) => `${c.name} ${fmt.money(c.totalMinorUnits, expenses.currencyCode, expenses.currencyMinorUnit)}`)
                          .join(", ")}
                    {t("expensesTotalSuffix", {
                      total: fmt.money(expenses.grandTotalMinorUnits, expenses.currencyCode, expenses.currencyMinorUnit),
                    })}
                  </TableCell>
                </TableRow>
                <TableRow>
                  <TableCell component="th">{t("profitRowLabel")}</TableCell>
                  <TableCell>
                    <Trans ns="reports" i18nKey="profitLine"
                      values={{
                        revenue: fmt.money(profit.revenueMinorUnits, profit.currencyCode, profit.currencyMinorUnit),
                        expenses: fmt.money(profit.expensesMinorUnits, profit.currencyCode, profit.currencyMinorUnit),
                        profit: fmt.money(profit.profitMinorUnits, profit.currencyCode, profit.currencyMinorUnit),
                      }}
                      components={{ strong: <strong /> }}
                    />
                  </TableCell>
                </TableRow>
              </TableBody>
            </Table>
          </TableContainer>
          <p className="muted">
            {t("profitFootnote")}
          </p>
        </>
      )}
    </section>
  );
}
