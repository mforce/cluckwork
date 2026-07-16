import { useCallback, useEffect, useRef, useState } from "react";
import {
  addOrderItem, cancelOrder, confirmOrder, createOrder, formatMoney, getOrder,
  listCustomers, listEggGrades, listOrders, removeOrderItem, updateOrderItem,
} from "../api/cluckwork";
import type { Customer, EggGrade, SalesOrder } from "../api/cluckwork";
import { ApiError } from "../api/client";

const PAGE = 50;

function todayIso(): string {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// #23 + #24 (orders half): create a draft order, add/edit/remove graded lines,
// confirm (FIFO allocation), cancel drafts, browse/filter the order list.
export function SalesPage() {
  const [orders, setOrders] = useState<SalesOrder[] | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [customers, setCustomers] = useState<Customer[]>([]);
  const [grades, setGrades] = useState<EggGrade[]>([]);        // active + saleable (picker)
  const [allGrades, setAllGrades] = useState<EggGrade[]>([]);  // inactive included (display names)
  const [loadError, setLoadError] = useState<string | null>(null);

  // list filters (#24: status/customer/paged)
  const [statusFilter, setStatusFilter] = useState("");
  const [customerFilter, setCustomerFilter] = useState("");

  // create-order form
  const [customerId, setCustomerId] = useState("");
  const [orderDate, setOrderDate] = useState(todayIso());
  // active draft being built
  const [active, setActive] = useState<SalesOrder | null>(null);
  const [gradeId, setGradeId] = useState("");
  const [qty, setQty] = useState(30);
  const [price, setPrice] = useState("0.30");
  // per-row edit state (draft orders)
  const [editItemId, setEditItemId] = useState<string | null>(null);
  const [editQty, setEditQty] = useState(1);
  const [editPrice, setEditPrice] = useState("0");

  const [busy, setBusy] = useState(false);
  const inFlight = useRef(false);
  // Idempotency keys bound to (action, target) and rotated ONLY after the whole
  // action (write + refresh) succeeds: a retry after any failure — including a
  // lost response or a failed follow-up read — replays the same key, so the
  // server dedupes instead of duplicating the write.
  const keys = useRef(new Map<string, string>());
  const keyFor = (scope: string) => {
    const existing = keys.current.get(scope);
    if (existing) return existing;
    const fresh = crypto.randomUUID();
    keys.current.set(scope, fresh);
    return fresh;
  };
  const clearKey = (scope: string) => keys.current.delete(scope);

  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const customerName = (id: string) => customers.find((c) => c.id === id)?.name ?? id.slice(0, 8);
  const gradeName = (id: string) => allGrades.find((g) => g.id === id)?.name ?? id.slice(0, 8);

  const loadOrders = useCallback(async (offset = 0) => {
    const page = await listOrders({
      status: statusFilter || undefined,
      customerId: customerFilter || undefined,
      limit: PAGE,
      offset,
    });
    setHasMore(page.length === PAGE);
    setOrders((prev) => (offset === 0 ? page : [...(prev ?? []), ...page]));
  }, [statusFilter, customerFilter]);

  useEffect(() => {
    // includeInactive: existing order lines may reference deactivated grades,
    // and their names must still resolve. The add-item picker filters back down
    // to active + saleable.
    Promise.all([listCustomers(), listEggGrades({ includeInactive: true })])
      .then(([c, g]) => {
        setCustomers(c);
        setAllGrades(g);
        const saleable = g.filter((x) => x.active && x.isSaleable);
        setGrades(saleable);
        if (c.length > 0) setCustomerId(c[0].id);
        if (saleable.length > 0) setGradeId(saleable[0].id);
      })
      .catch(() => setLoadError("Could not load sales data. Is the API up?"));
  }, []);

  useEffect(() => {
    loadOrders().catch(() => setLoadError("Could not load orders."));
  }, [loadOrders]);

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
    const created = await createOrder({ customerId, orderDate }, keyFor("create-order"));
    setActive(await getOrder(created.id));
    await loadOrders();
    clearKey("create-order");
  });

  const onAddItem = () => run(async () => {
    if (!active) return;
    const minorUnits = Math.round(parseFloat(price) * 10 ** active.currencyMinorUnit);
    if (!Number.isFinite(minorUnits) || minorUnits < 0) throw new Error("Invalid unit price.");
    const scope = `add-item:${active.id}`;
    await addOrderItem(active.id,
      { eggGradeId: gradeId, quantity: qty, unitPriceMinorUnits: minorUnits },
      keyFor(scope));
    setActive(await getOrder(active.id));
    clearKey(scope);
  });

  const onUpdateItem = (itemId: string) => run(async () => {
    if (!active) return;
    const minorUnits = Math.round(parseFloat(editPrice) * 10 ** active.currencyMinorUnit);
    if (!Number.isFinite(minorUnits) || minorUnits < 0) throw new Error("Invalid unit price.");
    const scope = `update-item:${itemId}`;
    await updateOrderItem(active.id, itemId,
      { quantity: editQty, unitPriceMinorUnits: minorUnits }, keyFor(scope));
    setEditItemId(null);
    setActive(await getOrder(active.id));
    clearKey(scope);
  });

  const onRemoveItem = (itemId: string) => run(async () => {
    if (!active) return;
    const scope = `remove-item:${itemId}`;
    await removeOrderItem(active.id, itemId, keyFor(scope));
    setActive(await getOrder(active.id));
    clearKey(scope);
  });

  const onConfirm = () => run(async () => {
    if (!active) return;
    const scope = `confirm:${active.id}`;
    await confirmOrder(active.id, keyFor(scope));
    const refreshed = await getOrder(active.id);
    setActive(refreshed);
    setMessage(`Order ${refreshed.referenceNumber} confirmed — stock allocated (FIFO).`);
    await loadOrders();
    clearKey(scope);
  });

  const onCancel = () => run(async () => {
    if (!active) return;
    const scope = `cancel:${active.id}`;
    await cancelOrder(active.id, keyFor(scope));
    setActive(null);
    setMessage("Draft order cancelled.");
    await loadOrders();
    clearKey(scope);
  });

  // Always fetch fresh on open — the list row may be stale relative to
  // mutations made through the panel since the list was loaded.
  const onOpen = (id: string) => run(async () => {
    setActive(await getOrder(id));
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
              <thead><tr><th>Grade</th><th>Qty</th><th>Unit price</th><th>Line total</th><th></th></tr></thead>
              <tbody>
                {active.items.map((i) => (
                  <tr key={i.id}>
                    <td>{gradeName(i.eggGradeId)}</td>
                    {editItemId === i.id ? (
                      <>
                        <td><input className="cell" type="number" min={1} value={editQty}
                          onChange={(e) => setEditQty(Math.max(1, e.target.valueAsNumber || 1))} /></td>
                        <td><input className="cell" type="number" min={0}
                          step={10 ** -active.currencyMinorUnit} value={editPrice}
                          onChange={(e) => setEditPrice(e.target.value)} /></td>
                        <td>—</td>
                        <td>
                          <button className="link" disabled={busy} onClick={() => onUpdateItem(i.id)}>save</button>
                          <button className="link" onClick={() => setEditItemId(null)}>cancel</button>
                        </td>
                      </>
                    ) : (
                      <>
                        <td>{i.quantity}</td>
                        <td>{formatMoney(i.unitPriceMinorUnits, i.currencyCode, i.currencyMinorUnit)}</td>
                        <td>{formatMoney(i.unitPriceMinorUnits * i.quantity, i.currencyCode, i.currencyMinorUnit)}</td>
                        <td>
                          {active.status === "Draft" && (
                            <>
                              <button className="link" disabled={busy} onClick={() => {
                                setEditItemId(i.id);
                                setEditQty(i.quantity);
                                setEditPrice((i.unitPriceMinorUnits / 10 ** i.currencyMinorUnit)
                                  .toFixed(i.currencyMinorUnit));
                              }}>edit</button>
                              <button className="link" disabled={busy}
                                onClick={() => onRemoveItem(i.id)}>remove</button>
                            </>
                          )}
                        </td>
                      </>
                    )}
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
                <button className="link" disabled={busy} onClick={onCancel}>Cancel draft</button>
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
      <div className="form-grid">
        <label>Status
          <select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)}>
            <option value="">All</option>
            <option value="Draft">Draft</option>
            <option value="Confirmed">Confirmed</option>
            <option value="Cancelled">Cancelled</option>
          </select>
        </label>
        <label>Customer
          <select value={customerFilter} onChange={(e) => setCustomerFilter(e.target.value)}>
            <option value="">All</option>
            {customers.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </label>
      </div>
      {orders.length === 0 ? (
        <p className="muted">No orders match.</p>
      ) : (
        <>
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
                  <td><button className="link" onClick={() => onOpen(o.id)}>open</button></td>
                </tr>
              ))}
            </tbody>
          </table>
          {hasMore && (
            <button className="link" disabled={busy}
              onClick={() => run(() => loadOrders(orders.length))}>load more</button>
          )}
        </>
      )}
    </section>
  );
}
