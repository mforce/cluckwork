import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import {
  formatMoney, getStock, listCustomers, listDailyEntries, listFlocks, listOrders,
} from "../api/cluckwork";
import type { Customer, DailyEntry, Flock, SalesOrder, StockRow } from "../api/cluckwork";
import { todayIso } from "../lib/dates";

const RECENT_ORDERS = 5;

// F5 (#41): landing page — today's production per flock, stock by grade, and
// recent sales, each linking to its full screen. Composed client-side from
// existing read endpoints (5 parallel GETs; aggregate endpoint not warranted).
export function Dashboard() {
  const [flocks, setFlocks] = useState<Flock[]>([]);
  const [entries, setEntries] = useState<DailyEntry[]>([]);
  const [stock, setStock] = useState<StockRow[]>([]);
  const [orders, setOrders] = useState<SalesOrder[]>([]);
  const [customers, setCustomers] = useState<Customer[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const today = todayIso();
    Promise.all([
      listFlocks(),
      listDailyEntries({ from: today, to: today, limit: 200 }),
      getStock(),
      listOrders({ limit: RECENT_ORDERS }),
      listCustomers(),
    ])
      .then(([f, e, s, o, c]) => {
        setFlocks(f); setEntries(e); setStock(s); setOrders(o); setCustomers(c);
      })
      .catch(() => setError("Could not load dashboard. Is the API up?"))
      .finally(() => setLoading(false));
  }, []);

  const entryFor = (flockId: string) => entries.find((e) => e.flockId === flockId);
  const customerName = (id: string) =>
    customers.find((c) => c.id === id)?.name ?? id.slice(0, 8);
  const totalAvailable = stock.reduce((a, r) => a + r.available, 0);

  if (loading) return <section><h2>Dashboard</h2><p className="muted">Loading…</p></section>;
  if (error) return <section><h2>Dashboard</h2><p className="error">{error}</p></section>;

  return (
    <section>
      <h2>Dashboard</h2>
      <p className="muted">{todayIso()}</p>

      <div className="dash-grid">
        <div className="panel">
          <h3><Link to="/daily-entry">Today</Link></h3>
          {flocks.length === 0 ? (
            <p className="muted">No flocks yet — create one on the Daily entry page.</p>
          ) : (
            <table className="data">
              <thead>
                <tr><th>Flock</th><th>Status</th><th>Eggs</th><th>Losses</th><th>Mortality</th></tr>
              </thead>
              <tbody>
                {flocks.map((f) => {
                  const e = entryFor(f.id);
                  return (
                    <tr key={f.id}>
                      <td>{f.name}</td>
                      <td>{e ? e.status : <span className="warn">no entry</span>}</td>
                      <td>{e ? e.totalEggs : "—"}</td>
                      <td>{e ? e.crackedEggs + e.dirtyEggs + e.discardedEggs : "—"}</td>
                      <td>{e ? e.mortalityCount : "—"}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          )}
        </div>

        <div className="panel">
          <h3><Link to="/stock">Stock</Link></h3>
          {stock.length === 0 ? (
            <p className="muted">No stock yet — record and submit a daily entry.</p>
          ) : (
            <>
              <table className="data">
                <thead>
                  <tr><th>Grade</th><th>Available</th><th>Restricted</th></tr>
                </thead>
                <tbody>
                  {stock.map((r) => (
                    <tr key={r.eggGradeId}>
                      <td>{r.gradeName}</td>
                      <td>{r.available}</td>
                      <td>{r.restricted > 0 ? <span className="warn">{r.restricted}</span> : "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <p className="muted">{totalAvailable} eggs available.</p>
            </>
          )}
        </div>

        <div className="panel">
          <h3><Link to="/sales">Recent sales</Link></h3>
          {orders.length === 0 ? (
            <p className="muted">No orders yet.</p>
          ) : (
            <table className="data">
              <thead>
                <tr><th>Ref</th><th>Customer</th><th>Status</th><th>Total</th></tr>
              </thead>
              <tbody>
                {orders.map((o) => (
                  <tr key={o.id}>
                    <td>{o.referenceNumber}</td>
                    <td>{customerName(o.customerId)}</td>
                    <td>{o.status}</td>
                    <td>{formatMoney(o.totalMinorUnits, o.currencyCode, o.currencyMinorUnit)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </div>
    </section>
  );
}
