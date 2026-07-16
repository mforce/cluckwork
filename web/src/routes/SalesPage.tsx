import { useEffect, useRef, useState } from "react";
import {
  addOrderItem, confirmOrder, createOrder, formatMoney, getOrder,
  listCustomers, listEggGrades, listOrders,
} from "../api/cluckwork";
import type { Customer, EggGrade, SalesOrder } from "../api/cluckwork";
import { ApiError } from "../api/client";

function todayIso(): string {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// #23 + #24 (orders half): create a draft order, add graded lines, confirm
// (FIFO allocation against stock), plus the browsable order list.
export function SalesPage() {
  const [orders, setOrders] = useState<SalesOrder[] | null>(null);
  const [customers, setCustomers] = useState<Customer[]>([]);
  const [grades, setGrades] = useState<EggGrade[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);

  // create-order form
  const [customerId, setCustomerId] = useState("");
  const [orderDate, setOrderDate] = useState(todayIso());
  // active draft being built
  const [active, setActive] = useState<SalesOrder | null>(null);
  const [gradeId, setGradeId] = useState("");
  const [qty, setQty] = useState(30);
  const [price, setPrice] = useState("0.30");

  const [busy, setBusy] = useState(false);
  const inFlight = useRef(false);
  const orderKey = useRef<string>(crypto.randomUUID());
  const itemKey = useRef<string>(crypto.randomUUID());
  const confirmKey = useRef<string>(crypto.randomUUID());
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const customerName = (id: string) => customers.find((c) => c.id === id)?.name ?? id.slice(0, 8);
  const gradeName = (id: string) => grades.find((g) => g.id === id)?.name ?? id.slice(0, 8);

  const reloadOrders = () =>
    listOrders({ limit: 50 }).then(setOrders).catch(() => setLoadError("Could not load orders."));

  useEffect(() => {
    Promise.all([listOrders({ limit: 50 }), listCustomers(), listEggGrades()])
      .then(([o, c, g]) => {
        setOrders(o);
        setCustomers(c);
        const saleable = g.filter((x) => x.isSaleable);
        setGrades(saleable);
        if (c.length > 0) setCustomerId(c[0].id);
        if (saleable.length > 0) setGradeId(saleable[0].id);
      })
      .catch(() => setLoadError("Could not load sales data. Is the API up?"));
  }, []);

  async function run(fn: () => Promise<void>) {
    if (inFlight.current) return;
    inFlight.current = true;
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      await fn();
    } catch (err) {
      setError(errText(err));
    } finally {
      inFlight.current = false;
      setBusy(false);
    }
  }

  const onCreateOrder = () => run(async () => {
    const created = await createOrder({ customerId, orderDate }, orderKey.current);
    orderKey.current = crypto.randomUUID();
    setActive(await getOrder(created.id));
    await reloadOrders();
  });

  const onAddItem = () => run(async () => {
    if (!active) return;
    const minorUnits = Math.round(parseFloat(price) * 10 ** active.currencyMinorUnit);
    if (!Number.isFinite(minorUnits) || minorUnits < 0) throw new Error("Invalid unit price.");
    await addOrderItem(active.id,
      { eggGradeId: gradeId, quantity: qty, unitPriceMinorUnits: minorUnits },
      itemKey.current);
    itemKey.current = crypto.randomUUID();
    setActive(await getOrder(active.id));
  });

  const onConfirm = () => run(async () => {
    if (!active) return;
    await confirmOrder(active.id, confirmKey.current);
    confirmKey.current = crypto.randomUUID();
    const refreshed = await getOrder(active.id);
    setActive(refreshed);
    setMessage(`Order ${refreshed.referenceNumber} confirmed — stock allocated (FIFO).`);
    await reloadOrders();
  });

  if (loadError) return <section><h2>Sales</h2><p className="error">{loadError}</p></section>;
  if (orders === null) return <section><h2>Sales</h2><p className="muted">Loading…</p></section>;

  return (
    <section>
      <h2>Sales</h2>

      {customers.length === 0 ? (
        <p className="muted">Add a customer first (Customers page), then create an order.</p>
      ) : (
        <div className="form-grid">
          <label>Customer
            <select value={customerId} onChange={(e) => setCustomerId(e.target.value)}>
              {customers.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          </label>
          <label>Date
            <input type="date" value={orderDate} max={todayIso()}
              onChange={(e) => setOrderDate(e.target.value)} />
          </label>
          <button disabled={busy || !customerId} onClick={onCreateOrder}>New draft order</button>
        </div>
      )}

      {active && (
        <div className="order-panel">
          <h3>
            {active.referenceNumber} — {customerName(active.customerId)}{" "}
            <span className={active.status === "Draft" ? "muted" : "warn"}>[{active.status}]</span>
          </h3>

          {active.items.length > 0 && (
            <table className="data">
              <thead><tr><th>Grade</th><th>Qty</th><th>Unit price</th><th>Line total</th></tr></thead>
              <tbody>
                {active.items.map((i) => (
                  <tr key={i.id}>
                    <td>{gradeName(i.eggGradeId)}</td>
                    <td>{i.quantity}</td>
                    <td>{formatMoney(i.unitPriceMinorUnits, i.currencyCode, i.currencyMinorUnit)}</td>
                    <td>{formatMoney(i.unitPriceMinorUnits * i.quantity, i.currencyCode, i.currencyMinorUnit)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          <p><strong>Total: {formatMoney(active.totalMinorUnits, active.currencyCode, active.currencyMinorUnit)}</strong></p>

          {active.status === "Draft" && (
            <>
              <div className="form-grid">
                <label>Grade
                  <select value={gradeId} onChange={(e) => setGradeId(e.target.value)}>
                    {grades.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
                  </select>
                </label>
                <label>Quantity
                  <input type="number" min={1} value={qty}
                    onChange={(e) => setQty(Math.max(1, e.target.valueAsNumber || 1))} />
                </label>
                <label>Unit price ({active.currencyCode})
                  <input type="number" min={0} step={10 ** -active.currencyMinorUnit} value={price}
                    onChange={(e) => setPrice(e.target.value)} />
                </label>
                <button disabled={busy || !gradeId} onClick={onAddItem}>Add line</button>
              </div>
              <div className="actions">
                <button disabled={busy || active.items.length === 0} onClick={onConfirm}>
                  Confirm order (allocates stock)
                </button>
                <button className="link" onClick={() => setActive(null)}>close</button>
              </div>
            </>
          )}
          {active.status !== "Draft" && (
            <div className="actions">
              <button className="link" onClick={() => setActive(null)}>close</button>
            </div>
          )}
        </div>
      )}

      {error && <p className="error">{error}</p>}
      {message && <p className="success">{message}</p>}

      <h3>Orders</h3>
      {orders.length === 0 ? (
        <p className="muted">No orders yet.</p>
      ) : (
        <table className="data">
          <thead>
            <tr><th>Reference</th><th>Date</th><th>Customer</th><th>Status</th><th>Total</th><th></th></tr>
          </thead>
          <tbody>
            {orders.map((o) => (
              <tr key={o.id}>
                <td>{o.referenceNumber}</td>
                <td>{o.orderDate}</td>
                <td>{customerName(o.customerId)}</td>
                <td>{o.status}</td>
                <td>{formatMoney(o.totalMinorUnits, o.currencyCode, o.currencyMinorUnit)}</td>
                <td><button className="link" onClick={() => setActive(o)}>open</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
