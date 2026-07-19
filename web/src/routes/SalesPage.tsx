import { useCallback, useEffect, useRef, useState } from "react";
import {
  addOrderItem, cancelOrder, confirmOrder, createOrder, formatMoney, getOrder,
  listCustomers, listEggGrades, listOrderPayments, listOrders, recordPayment,
  removeOrderItem, updateOrderItem, voidOrder, voidPayment,
} from "../api/cluckwork";
import type { Customer, EggGrade, OrderPayments, SalesOrder } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useAuth } from "../auth/useAuth";

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
  // Void undoes a confirmed sale — admin-only (#73); the API enforces it too.
  const { isAdmin } = useAuth();
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

  // Payments (#89, admin-only money data) — settlement state of the open
  // confirmed order.
  const [payments, setPayments] = useState<OrderPayments | null>(null);
  const [payDate, setPayDate] = useState(todayIso());
  const [payAmount, setPayAmount] = useState("");
  const [payMethod, setPayMethod] = useState("Cash");
  const [payRef, setPayRef] = useState("");
  const [payNote, setPayNote] = useState("");

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

  const activeId = active?.id ?? null;
  const activeStatus = active?.status ?? null;
  useEffect(() => {
    // Cleared FIRST, unconditionally: while the new order's payments load (or
    // if the load fails), stale rows from the previous order must never stay
    // actionable — their Void buttons would target the wrong order's money
    // (codex review of #90).
    setPayments(null);
    if (activeId === null || activeStatus !== "Confirmed" || !isAdmin) return;
    let cancelled = false;
    listOrderPayments(activeId)
      .then((p) => { if (!cancelled) setPayments(p); })
      .catch(() => { if (!cancelled) setError("Could not load this order's payments."); });
    return () => { cancelled = true; };
  }, [activeId, activeStatus, isAdmin]);

  // Exact decimal parsing in the ORDER's denomination (no float multiply —
  // #88 review); excess decimals are rejected, not silently rounded.
  const toMinor = (display: string, minor: number) => {
    const m = display.trim().match(/^(\d+)(?:\.(\d+))?$/);
    if (!m) throw new Error("Enter a valid amount.");
    const frac = m[2] ?? "";
    if (frac.length > minor)
      throw new Error(minor === 0
        ? "This currency has no decimal places."
        : `At most ${minor} decimal places for this currency.`);
    const v = Number(m[1]) * 10 ** minor + Number(frac.padEnd(minor, "0") || "0");
    if (!Number.isSafeInteger(v) || v <= 0) throw new Error("Enter an amount greater than zero.");
    return v;
  };

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

  // One-way actions (#59). Confirm BEFORE run() so buttons don't flash
  // disabled while the user decides.
  const onConfirm = () => {
    if (!window.confirm(
      "Confirm this order? Stock is allocated from inventory (FIFO). "
      + "A mistaken confirm can be undone with Void, which returns the stock.")) return;
    void run(async () => {
      if (!active) return;
      const scope = `confirm:${active.id}`;
      await confirmOrder(active.id, keyFor(scope));
      const refreshed = await getOrder(active.id);
      setActive(refreshed);
      setMessage(`Order ${refreshed.referenceNumber} confirmed — stock allocated (FIFO).`);
      await loadOrders();
      clearKey(scope);
    });
  };

  const onCancel = () => {
    // Cancel is a status change: the order keeps its lines but becomes
    // read-only and can't be confirmed.
    if (!window.confirm(
      "Cancel this draft? The order becomes cancelled and can no longer be edited or confirmed.")) return;
    void run(async () => {
      if (!active) return;
      const scope = `cancel:${active.id}`;
      await cancelOrder(active.id, keyFor(scope));
      setActive(null);
      setMessage("Draft order cancelled.");
      await loadOrders();
      clearKey(scope);
    });
  };

  // Undo of a mistaken confirm (#60). Reason prompt doubles as the confirm
  // dialog, hoisted above run() like the other one-way actions; cancelling the
  // prompt aborts the void.
  const refreshPayments = async (orderId: string) =>
    setPayments(await listOrderPayments(orderId));

  const onRecordPayment = () => void run(async () => {
    if (!active || !payments) return;
    const minorUnits = toMinor(payAmount, payments.currencyMinorUnit);
    const scope = `pay:${active.id}`;
    await recordPayment(active.id, {
      paymentDate: payDate,
      amountMinorUnits: minorUnits,
      method: payMethod,
      referenceNumber: payRef.trim() || null,
      note: payNote.trim() || null,
    }, keyFor(scope));
    // The key rotates the moment the WRITE lands — if it survived until the
    // refresh below succeeded, a failed refresh would make the NEXT payment
    // reuse it and silently replay this 201 instead of recording new money
    // (codex review of #90). The form reset before the refresh (#88 review)
    // covers the duplicate-resubmit direction.
    clearKey(scope);
    setPayAmount("");
    setPayRef("");
    setPayNote("");
    await refreshPayments(active.id);
    setMessage("Payment recorded.");
  });

  const onVoidPayment = (paymentId: string, version: number) => {
    const reason = window.prompt(
      "Void this payment? The order's outstanding amount grows back.\n\nReason (required):");
    if (reason === null) return;
    if (!reason.trim()) {
      setError("A void reason is required.");
      return;
    }
    void run(async () => {
      if (!active) return;
      const scope = `void-payment:${paymentId}`;
      try {
        await voidPayment(paymentId, { version, reason: reason.trim() }, keyFor(scope));
        clearKey(scope);
      } catch (err) {
        // Version-guarded: any SERVER response settles the attempt (the base
        // version prevents double-apply); only transport failures keep the key.
        if (err instanceof ApiError) clearKey(scope);
        throw err;
      }
      await refreshPayments(active.id);
      setMessage("Payment voided — the outstanding amount grew back.");
    });
  };

  const onVoid = () => {
    const reason = window.prompt(
      "Void this confirmed order? The allocated stock returns to the exact "
      + "egg lots it came from.\n\nReason (required):");
    if (reason === null) return;
    if (!reason.trim()) {
      setError("A void reason is required.");
      return;
    }
    void run(async () => {
      if (!active) return;
      const scope = `void:${active.id}`;
      await voidOrder(active.id, reason.trim(), keyFor(scope));
      const refreshed = await getOrder(active.id);
      setActive(refreshed);
      setMessage(`Order ${refreshed.referenceNumber} voided — stock returned to inventory.`);
      await loadOrders();
      clearKey(scope);
    });
  };

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
          {active.status === "Confirmed" && isAdmin && payments && (
            <>
              <h4>Payments</h4>
              {payments.items.length > 0 && (
                <table className="data">
                  <thead>
                    <tr><th>Date</th><th>Amount</th><th>Method</th><th>Reference</th><th></th></tr>
                  </thead>
                  <tbody>
                    {payments.items.map((p) => (
                      <tr key={p.id} className={p.voided ? "inactive" : undefined}
                        title={p.note ?? undefined}>
                        <td>{p.paymentDate}</td>
                        <td>{formatMoney(p.amountMinorUnits, p.currencyCode, p.currencyMinorUnit)}</td>
                        <td>{p.method}</td>
                        <td>{p.referenceNumber ?? "—"}</td>
                        <td>
                          {p.voided
                            ? <span className="warn" title={p.voidReason ?? undefined}>Voided</span>
                            : (
                              <button className="link" disabled={busy}
                                onClick={() => onVoidPayment(p.id, p.version)}>void</button>
                            )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
              <p>
                Paid {formatMoney(payments.paidMinorUnits, payments.currencyCode, payments.currencyMinorUnit)} —{" "}
                <strong>
                  outstanding {formatMoney(payments.outstandingMinorUnits, payments.currencyCode, payments.currencyMinorUnit)}
                </strong>
              </p>
              {payments.outstandingMinorUnits > 0 && (
                <div className="form-grid">
                  <label>Date
                    <input type="date" value={payDate} max={todayIso()}
                      onChange={(e) => setPayDate(e.target.value)} />
                  </label>
                  <label>Amount ({payments.currencyCode})
                    <input type="number"
                      min={(1 / 10 ** payments.currencyMinorUnit).toFixed(payments.currencyMinorUnit)}
                      step="any" value={payAmount}
                      onChange={(e) => setPayAmount(e.target.value)} />
                  </label>
                  <label>Method
                    <select value={payMethod} onChange={(e) => setPayMethod(e.target.value)}>
                      {["Cash", "Check", "Card", "BankTransfer", "MobilePayment", "Other"].map((m) => (
                        <option key={m} value={m}>{m}</option>
                      ))}
                    </select>
                  </label>
                  <label>Reference (optional)
                    <input value={payRef} maxLength={50}
                      onChange={(e) => setPayRef(e.target.value)} />
                  </label>
                  <label>Note (optional)
                    <input value={payNote} maxLength={500}
                      onChange={(e) => setPayNote(e.target.value)} />
                  </label>
                  <button disabled={busy || !payAmount} onClick={onRecordPayment}>
                    Record payment
                  </button>
                </div>
              )}
            </>
          )}
          {active.status === "Voided" && active.voidReason && (
            <p className="muted">Void reason: {active.voidReason}</p>
          )}
          {active.status !== "Draft" && (
            <div className="actions">
              {active.status === "Confirmed" && isAdmin && (
                <button className="link" disabled={busy} onClick={onVoid}>
                  Void order (returns stock)
                </button>
              )}
              {active.status === "Confirmed" && !isAdmin && (
                <span className="muted">Voiding needs an admin.</span>
              )}
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
            <option value="Voided">Voided</option>
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
