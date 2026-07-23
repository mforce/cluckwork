import { useCallback, useEffect, useRef, useState } from "react";
import {
  formatMoney, getExpenseSummary, getProductionReport, getProfitReport, getSalesSummary,
} from "../api/cluckwork";
import type {
  ExpenseSummaryReport, ProductionReport, ProfitReport, SalesSummary,
} from "../api/cluckwork";
import { ApiError } from "../api/client";
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
      <h2>Reports</h2>

      <div className="filters">
        <label>From
          <input type="date" value={from} max={to}
            onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label>To
          <input type="date" value={to} max={today}
            onChange={(e) => setTo(e.target.value)} />
        </label>
      </div>

      {error && <p className="error" role="alert">{error}</p>}
      {loading && <p className="muted">Loading…</p>}

      {production && (
        <>
          <h3>Production</h3>
          <table className="data">
            <thead>
              <tr>
                <th>Date</th><th>Eggs</th><th>Losses (cr/di/ds)</th><th>Sellable</th>
                <th>Deaths</th><th>Hen-days</th><th>Hen-day %</th>
              </tr>
            </thead>
            <tbody>
              {production.days.map((d) => (
                <tr key={d.date}>
                  <td>{d.date}</td>
                  <td>{d.totalEggs}</td>
                  <td>{d.cracked}/{d.dirty}/{d.discarded}</td>
                  <td>{d.sellable}</td>
                  <td>{d.deaths}</td>
                  <td>{d.henDays}</td>
                  <td>{d.henDayPct ?? "—"}</td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr>
                <th>Period</th>
                <th>{production.totalEggs}</th>
                <th></th>
                <th>{production.totalSellable}</th>
                <th>{production.totalDeaths}</th>
                <th>{production.totalHenDays}</th>
                <th>{production.periodHenDayPct ?? "—"}</th>
              </tr>
            </tfoot>
          </table>
          {production.gradeTotals.length > 0 && (
            <p className="muted">
              By grade:{" "}
              {production.gradeTotals.map((g) => `${g.name} ${g.quantity}`).join(", ")}
            </p>
          )}
        </>
      )}

      {isAdmin && sales && expenses && profit && (
        <>
          <h3>Money</h3>
          <table className="data">
            <tbody>
              <tr>
                <th>Sales</th>
                <td>
                  {sales.confirmedCount} confirmed order(s) —{" "}
                  revenue {formatMoney(sales.revenueMinorUnits, sales.currencyCode, sales.currencyMinorUnit)},{" "}
                  paid {formatMoney(sales.paidMinorUnits, sales.currencyCode, sales.currencyMinorUnit)},{" "}
                  outstanding {formatMoney(sales.outstandingMinorUnits, sales.currencyCode, sales.currencyMinorUnit)}
                  {sales.voidedCount > 0 ? ` (${sales.voidedCount} voided)` : ""}
                </td>
              </tr>
              <tr>
                <th>Expenses</th>
                <td>
                  {expenses.categories.length === 0
                    ? "none recorded"
                    : expenses.categories
                        .map((c) => `${c.name} ${formatMoney(c.totalMinorUnits, expenses.currencyCode, expenses.currencyMinorUnit)}`)
                        .join(", ")}
                  {" — total "}
                  {formatMoney(expenses.grandTotalMinorUnits, expenses.currencyCode, expenses.currencyMinorUnit)}
                </td>
              </tr>
              <tr>
                <th>Profit (basic)</th>
                <td>
                  revenue {formatMoney(profit.revenueMinorUnits, profit.currencyCode, profit.currencyMinorUnit)}{" "}
                  − expenses {formatMoney(profit.expensesMinorUnits, profit.currencyCode, profit.currencyMinorUnit)}{" "}
                  = <strong>{formatMoney(profit.profitMinorUnits, profit.currencyCode, profit.currencyMinorUnit)}</strong>
                </td>
              </tr>
            </tbody>
          </table>
          <p className="muted">
            "Basic" profit is confirmed revenue minus recorded expenses — no
            cost-of-goods or inventory valuation.
          </p>
        </>
      )}
    </section>
  );
}
