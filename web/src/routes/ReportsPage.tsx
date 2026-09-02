import { useCallback, useEffect, useRef, useState } from "react";
import { Trans, useTranslation } from "react-i18next";
import {
  getExpenseSummary, getProductionReport, getProfitReport, getSalesSummary,
} from "../api/cluckwork";
import type {
  ExpenseSummaryReport, ProductionReport, ProfitReport, SalesSummary,
} from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useFormat } from "../farm/useFormat";
import { FarmDate } from "../components/FarmDate";
import { daysBefore } from "../lib/dates";
import { useFarmToday } from "../farm/useFarm";
import { useAuth } from "../auth/useAuth";

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
      <h2>{t("title")}</h2>

      {/* #653 — the only controls on this screen are the date range, so the
          whole bar is the toolbar (Reports has no other filter to keep
          separate, unlike History/Feed/Water below). */}
      <div className="toolbar">
        <label>{t("fromLabel")}
          <input type="date" value={from} max={to}
            onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label>{t("toLabel")}
          <input type="date" value={to} max={today}
            onChange={(e) => setTo(e.target.value)} />
        </label>
      </div>

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
          <table className="data">
            <thead>
              <tr>
                <th>{t("dateHeader")}</th><th className="num">{t("eggsHeader")}</th><th className="num">{t("lossesHeader")}</th><th className="num">{t("sellableHeader")}</th>
                {/* #396 — beside Sellable, not folded into it: Sellable is the
                    hand-graded remainder, Condition is what the cracked/dirty
                    counters contributed as stock. */}
                <th className="num">{t("conditionHeader")}</th>
                <th className="num">{t("deathsHeader")}</th><th className="num">{t("henDaysHeader")}</th><th className="num">{t("henDayPctHeader")}</th>
              </tr>
            </thead>
            <tbody>
              {production.days.map((d) => (
                <tr key={d.date}>
                  <td className="nowrap"><FarmDate iso={d.date} /></td>
                  <td className="num">{fmt.count(d.totalEggs)}</td>
                  <td className="num">{fmt.count(d.cracked)}/{fmt.count(d.dirty)}/{fmt.count(d.discarded)}</td>
                  <td className="num">{fmt.count(d.sellable)}</td>
                  <td className="num">{fmt.count(d.fromCounts)}</td>
                  <td className="num">{fmt.count(d.deaths)}</td>
                  <td className="num">{fmt.count(d.henDays)}</td>
                  <td className="num">{d.henDayPct === null ? "—" : fmt.count(d.henDayPct, 1)}</td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr>
                <th>{t("periodRowLabel")}</th>
                <th className="num">{fmt.count(production.totalEggs)}</th>
                <th></th>
                <th className="num">{fmt.count(production.totalSellable)}</th>
                <th className="num">{fmt.count(production.totalFromCounts)}</th>
                <th className="num">{fmt.count(production.totalDeaths)}</th>
                <th className="num">{fmt.count(production.totalHenDays)}</th>
                <th className="num">{production.periodHenDayPct === null ? "—" : fmt.count(production.periodHenDayPct, 1)}</th>
              </tr>
            </tfoot>
          </table>
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
          <table className="data">
            <tbody>
              <tr>
                <th>{t("salesRowLabel")}</th>
                <td>
                  {t("salesSummary", {
                    count: sales.confirmedCount,
                    confirmed: fmt.count(sales.confirmedCount),
                    revenue: fmt.money(sales.revenueMinorUnits, sales.currencyCode, sales.currencyMinorUnit),
                    paid: fmt.money(sales.paidMinorUnits, sales.currencyCode, sales.currencyMinorUnit),
                    outstanding: fmt.money(sales.outstandingMinorUnits, sales.currencyCode, sales.currencyMinorUnit),
                  })}
                  {sales.voidedCount > 0 ? t("salesVoidedSuffix", { count: sales.voidedCount, voided: fmt.count(sales.voidedCount) }) : ""}
                </td>
              </tr>
              <tr>
                <th>{t("expensesRowLabel")}</th>
                <td>
                  {expenses.categories.length === 0
                    ? t("expensesNone")
                    : expenses.categories
                        .map((c) => `${c.name} ${fmt.money(c.totalMinorUnits, expenses.currencyCode, expenses.currencyMinorUnit)}`)
                        .join(", ")}
                  {t("expensesTotalSuffix", {
                    total: fmt.money(expenses.grandTotalMinorUnits, expenses.currencyCode, expenses.currencyMinorUnit),
                  })}
                </td>
              </tr>
              <tr>
                <th>{t("profitRowLabel")}</th>
                <td>
                  <Trans ns="reports" i18nKey="profitLine"
                    values={{
                      revenue: fmt.money(profit.revenueMinorUnits, profit.currencyCode, profit.currencyMinorUnit),
                      expenses: fmt.money(profit.expensesMinorUnits, profit.currencyCode, profit.currencyMinorUnit),
                      profit: fmt.money(profit.profitMinorUnits, profit.currencyCode, profit.currencyMinorUnit),
                    }}
                    components={{ strong: <strong /> }}
                  />
                </td>
              </tr>
            </tbody>
          </table>
          <p className="muted">
            {t("profitFootnote")}
          </p>
        </>
      )}
    </section>
  );
}
