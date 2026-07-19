import { useCallback, useEffect, useState } from "react";
import {
  formatMoney, getExpenseSummary, getProductionReport, getProfitReport, getSalesSummary,
} from "../api/cluckwork";
import type {
  ExpenseSummaryReport, ProductionReport, ProfitReport, SalesSummary,
} from "../api/cluckwork";
import { ApiError } from "../api/client";
import { todayIso } from "../lib/dates";
import { useAuth } from "../auth/useAuth";

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

function daysAgoIso(n: number): string {
  const d = new Date();
  d.setUTCDate(d.getUTCDate() - n);
  return d.toISOString().slice(0, 10);
}

// #91 — core reports. Production renders for everyone (workers record it,
// workers read it); the money cards are admin-only and the API refuses
// workers on those routes regardless.
export function ReportsPage() {
  const { isAdmin } = useAuth();
  const [from, setFrom] = useState(daysAgoIso(6));
  const [to, setTo] = useState(todayIso());
  const [production, setProduction] = useState<ProductionReport | null>(null);
  const [sales, setSales] = useState<SalesSummary | null>(null);
  const [expenses, setExpenses] = useState<ExpenseSummaryReport | null>(null);
  const [profit, setProfit] = useState<ProfitReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setProduction(await getProductionReport(from, to));
      if (isAdmin) {
        // Sequential, not racing: each money card is one cheap aggregate.
        setSales(await getSalesSummary(from, to));
        setExpenses(await getExpenseSummary(from, to));
        setProfit(await getProfitReport(from, to));
      }
    } catch (err) {
      setError(errText(err));
    } finally {
      setLoading(false);
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
          <input type="date" value={to} max={todayIso()}
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
