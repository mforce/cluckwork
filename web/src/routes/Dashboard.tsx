import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import {
  formatMoney, getStock, listCustomers, listDailyEntries, listFlocks, listOrders,
} from "../api/cluckwork";
import type { Customer, DailyEntry, Flock, SalesOrder, StockRow } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { todayIso } from "../lib/dates";

const RECENT_ORDERS = 5;
// Server clamps list limits at 500. One farm won't exceed that in Phase 1.x;
// past 500 flocks/customers the tail silently drops — revisit with real paging
// if that day comes.
const MAX_PAGE = 500;

// F5 (#41): landing page — today's production per flock, stock by grade, and
// recent sales, each linking to its full screen. Composed client-side from
// existing read endpoints (5 parallel GETs; aggregate endpoint not warranted).
// Panels degrade independently: one failed fetch blanks its panel, not the page.
export function Dashboard() {
  // Captured once at mount so the header date always matches the queried day
  // even if the tab stays open across midnight.
  const [today] = useState(todayIso);
  const [flocks, setFlocks] = useState<Flock[] | null>(null);
  const [entries, setEntries] = useState<DailyEntry[] | null>(null);
  const [stock, setStock] = useState<StockRow[] | null>(null);
  const [orders, setOrders] = useState<SalesOrder[] | null>(null);
  const [customers, setCustomers] = useState<Customer[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    Promise.allSettled([
      listFlocks({ limit: MAX_PAGE }),
      listDailyEntries({ from: today, to: today, limit: MAX_PAGE }),
      getStock(),
      listOrders({ limit: RECENT_ORDERS }),
      listCustomers({ limit: MAX_PAGE }),
    ]).then(([f, e, s, o, c]) => {
      if (f.status === "fulfilled") setFlocks(f.value);
      if (e.status === "fulfilled") setEntries(e.value);
      if (s.status === "fulfilled") setStock(s.value);
      if (o.status === "fulfilled") setOrders(o.value);
      if (c.status === "fulfilled") setCustomers(c.value);
      const settled = [f, e, s, o, c];
      if (settled.every((r) => r.status === "rejected")) {
        const reason = (settled[0] as PromiseRejectedResult).reason;
        setError(reason instanceof ApiError ? reason.message : "Could not load dashboard. Is the API up?");
      }
      setLoading(false);
    });
  }, [today]);

  const entryFor = (flockId: string) => entries?.find((e) => e.flockId === flockId);
  const customerName = (id: string) =>
    customers.find((c) => c.id === id)?.name ?? id.slice(0, 8);
  // "no entry" is a missed-capture flag — only meaningful for active flocks.
  // Depleted/archived flocks stay visible only if they do have an entry today.
  const visibleFlocks = (flocks ?? []).filter((f) => f.status === "Active" || entryFor(f.id));
  const totalAvailable = (stock ?? []).reduce((a, r) => a + r.available, 0);

  if (loading) return <section><h2>Dashboard</h2><p className="muted">Loading…</p></section>;
  if (error) return <section><h2>Dashboard</h2><p className="error">{error}</p></section>;

  const panelError = <p className="error">Could not load.</p>;

  return (
    <section>
      <h2>Dashboard</h2>
      <p className="muted">{today}</p>

      <div className="dash-grid">
        <div className="panel">
          <h3><Link to="/daily-entry">Today</Link></h3>
          {flocks === null || entries === null ? panelError : visibleFlocks.length === 0 ? (
            <p className="muted">No flocks yet — create one on the Daily entry page.</p>
          ) : (
            <table className="data">
              <thead>
                <tr><th>Flock</th><th>Status</th><th>Eggs</th><th>Losses</th><th>Mortality</th></tr>
              </thead>
              <tbody>
                {visibleFlocks.map((f) => {
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
          {stock === null ? panelError : stock.length === 0 ? (
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
          {orders === null ? panelError : orders.length === 0 ? (
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
