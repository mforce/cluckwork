import { useCallback, useEffect, useRef, useState } from "react";
import { Plus } from "lucide-react";
import { Trans, useTranslation } from "react-i18next";
import {
  addOrderItem, cancelOrder, confirmOrder, createOrder, formatMoney, getOrder,
  listCustomers, listEggGrades, listOrderPayments, listOrders, listProducts,
  parseMoneyToMinorUnits, recordPayment,
  removeOrderItem, updateOrderItem, voidOrder, voidPayment,
} from "../api/cluckwork";
import type { Customer, OrderPayments, Product, SalesOrder } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useAuth } from "../auth/useAuth";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { useConfirm } from "../components/useConfirm";
import { usePendingAction } from "../components/usePendingAction";
import { StatusBadge } from "../components/StatusBadge";
import { newId } from "../lib/ids";
import { useFarm, useFarmToday } from "../farm/useFarm";
import i18n from "../i18n";
import { statusLabel } from "../i18n/enums";

const PAGE = 50;

// The sole RAW payment-method render site (below) mirrors the SAME six-value
// vocabulary as the payment-method picker in this file (which already renders
// via the translated sales:method* keys) rather than the English-only `enums`
// module — method was deliberately left out of enums in Task 4 because it
// already carries es/tl translations in the `sales` namespace (#182).
type PaymentMethod = "Cash" | "Check" | "Card" | "BankTransfer" | "MobilePayment" | "Other";

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// A stored price as the decimal string the price field holds, at `scale` minor
// units. Blank when the product carries no default, and blank rather than a
// guess when the scale is not known yet — a wrong scale here is a price out by
// a factor of a hundred, and an empty field simply falls back to the server's
// own default for the line.
function priceInput(defaultPriceMinorUnits: number | null, scale: number | null): string {
  if (defaultPriceMinorUnits === null || scale === null) return "";
  return (defaultPriceMinorUnits / 10 ** scale).toFixed(scale);
}

// #23 + #24 (orders half): create a draft order, add/edit/remove graded lines,
// confirm (FIFO allocation), cancel drafts, browse/filter the order list.
export function SalesPage() {
  const { t } = useTranslation("sales");
  const { t: tc } = useTranslation("common");
  // Farm-local, not browser-local: since #35 the API judges "is this date in
  // the future?" against the FARM's day, so the pickers must agree (#123).
  const today = useFarmToday();
  const { farm } = useFarm();
  // Void undoes a confirmed sale — admin-only (#73); the API enforces it too.
  const { isAdmin, role } = useAuth();
  // Payments are the Sales tier (#104): Owner/Manager/Sales see and record;
  // voiding a payment stays corrective (Owner/Manager) like every other undo.
  const canSettle = isAdmin || role === "Sales";
  const { confirm, askReason, confirmDialog } = useConfirm();
  const [orders, setOrders] = useState<SalesOrder[] | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [customers, setCustomers] = useState<Customer[]>([]);
  // #99: lines sell PRODUCTS. Active ones feed the picker; the full list
  // (inactive included) resolves display names on existing lines.
  const [products, setProducts] = useState<Product[]>([]);
  const [allProducts, setAllProducts] = useState<Product[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);

  // list filters (#24: status/customer/paged)
  const [statusFilter, setStatusFilter] = useState("");
  const [customerFilter, setCustomerFilter] = useState("");

  // create-order form
  // F131: starting an order and taking a payment are discrete actions, not the
  // order builder itself — they open dialogs. Adding lines stays inline: the
  // draft panel IS the work surface.
  const [creatingOrder, setCreatingOrder] = useState(false);
  const [paying, setPaying] = useState(false);
  const [customerId, setCustomerId] = useState("");
  const [orderDate, setOrderDate] = useState(today);
  // active draft being built
  const [active, setActive] = useState<SalesOrder | null>(null);
  const [productId, setProductId] = useState("");
  const [unit, setUnit] = useState("Egg");
  const [qty, setQty] = useState(30);
  const [price, setPrice] = useState("");

  // ONE scale for every price this screen reads or writes (#123). The two
  // sources are the same currency by construction — an order is created in the
  // farm's currency, and since #159 a priced product locks the farm to it — so
  // this is one number reached two ways, not a choice between two answers.
  //
  // It used to be a choice: the prefill divided by the PRODUCT's minor unit
  // while the submit multiplied by the ORDER's. Latent while they cannot
  // differ, and an accepted prefill 100x out on the day they can — arriving as
  // an EXPLICIT price, which is exactly the case the server's
  // ProductPriceCurrencyMismatch guard does not fire on.
  const farmScale = farm?.currencyMinorUnit ?? null;
  const priceScale = active?.currencyMinorUnit ?? farmScale;

  // per-row edit state (draft orders)
  const [editItemId, setEditItemId] = useState<string | null>(null);
  const [editQty, setEditQty] = useState(1);
  const [editPrice, setEditPrice] = useState("0");

  // #236 — the shared flight guard (the hook's internal ref replaced the old
  // inFlight ref here). `busy` inerts every trigger; isPending(scope) spins
  // exactly the clicked one, so a row with two verbs never lies about which
  // is working.
  const { busy, isPending, run: runPending } = usePendingAction();
  // Idempotency keys bound to (action, target) and rotated ONLY after the whole
  // action (write + refresh) succeeds: a retry after any failure — including a
  // lost response or a failed follow-up read — replays the same key, so the
  // server dedupes instead of duplicating the write.
  const keys = useRef(new Map<string, string>());
  const keyFor = (scope: string) => {
    const existing = keys.current.get(scope);
    if (existing) return existing;
    const fresh = newId();
    keys.current.set(scope, fresh);
    return fresh;
  };
  const clearKey = (scope: string) => keys.current.delete(scope);

  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  // Payments (#89, admin-only money data) — settlement state of the open
  // confirmed order.
  const [payments, setPayments] = useState<OrderPayments | null>(null);
  const [payDate, setPayDate] = useState(today);
  const [payAmount, setPayAmount] = useState("");
  const [payMethod, setPayMethod] = useState("Cash");
  const [payRef, setPayRef] = useState("");
  const [payNote, setPayNote] = useState("");

  const customerName = (id: string) => customers.find((c) => c.id === id)?.name ?? id.slice(0, 8);
  const productName = (id: string) => allProducts.find((p) => p.id === id)?.name ?? id.slice(0, 8);

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
    // includeInactive: existing order lines may reference deactivated
    // products, and their names must still resolve. The add-item picker
    // filters back down to sellable: active products whose mapped grade is
    // still active + saleable — same rule the server enforces, so the picker
    // never offers something Add line would 422 (codex review of #100).
    Promise.all([
      listCustomers(),
      listProducts({ includeInactive: true }),
      listEggGrades(),
    ])
      .then(([c, p, g]) => {
        setCustomers(c);
        setAllProducts(p);
        const saleableGrades = new Set(g.filter((x) => x.isSaleable).map((x) => x.id));
        const sellable = p.filter(
          (x) => x.active && x.eggGradeId !== null && saleableGrades.has(x.eggGradeId));
        setProducts(sellable);
        if (c.length > 0) setCustomerId(c[0].id);
        if (sellable.length > 0) {
          const first = sellable[0];
          setProductId(first.id);
          setUnit(first.defaultUnit);
          // Prefill the price from the FIRST product too — the hard-coded
          // starter value used to shadow the product default until the user
          // changed the selection (codex review of #100). At the farm's scale:
          // there is no order yet, and the order this will be typed into will
          // carry the farm's currency.
          setPrice(priceInput(first.defaultPriceMinorUnits, farmScale));
        }
      })
      .catch(() => setLoadError(i18n.t("sales:loadSalesDataFailed")));
  }, []);

  useEffect(() => {
    loadOrders().catch(() => setLoadError(i18n.t("sales:loadOrdersFailed")));
  }, [loadOrders]);

  const activeId = active?.id ?? null;
  const activeStatus = active?.status ?? null;
  useEffect(() => {
    // Cleared FIRST, unconditionally: while the new order's payments load (or
    // if the load fails), stale rows from the previous order must never stay
    // actionable — their Void buttons would target the wrong order's money
    // (codex review of #90).
    setPayments(null);
    if (activeId === null || activeStatus !== "Confirmed" || !canSettle) return;
    let cancelled = false;
    listOrderPayments(activeId)
      .then((p) => { if (!cancelled) setPayments(p); })
      .catch(() => { if (!cancelled) setError(i18n.t("sales:loadPaymentsFailed")); });
    return () => { cancelled = true; };
  }, [activeId, activeStatus, canSettle]);

  // Exact decimal parsing in the ORDER's denomination (no float multiply —
  // #88 review); excess decimals are rejected, not silently rounded.
  const toMinor = (display: string, minor: number) => {
    const m = display.trim().match(/^(\d+)(?:\.(\d+))?$/);
    if (!m) throw new Error(i18n.t("sales:enterValidAmount"));
    const frac = m[2] ?? "";
    if (frac.length > minor)
      throw new Error(minor === 0
        ? i18n.t("sales:noDecimalPlaces")
        : i18n.t("sales:atMostDecimals", { count: minor }));
    const v = Number(m[1]) * 10 ** minor + Number(frac.padEnd(minor, "0") || "0");
    if (!Number.isSafeInteger(v) || v <= 0) throw new Error(i18n.t("sales:enterAmountGreaterThanZero"));
    return v;
  };

  // Rebased on usePendingAction (#236), gaining the scope parameter the old
  // scopeless helper lacked. Pending scopes are independent of the idempotency
  // key scopes built inside each action (nothing couples the two). The helper
  // also wraps two READS — "open:<id>" and "more" — which keep the flight
  // guard but deliberately get no BusyButton treatment (#236 is writes).
  const run = (scope: string, fn: () => Promise<void>) =>
    runPending(scope, async () => {
      setError(null);
      setMessage(null);
      try {
        await fn();
      } catch (err) {
        setError(errText(err));
      }
    });

  const onCreateOrder = () => run("create-order", async () => {
    const created = await createOrder({ customerId, orderDate }, keyFor("create-order"));
    setActive(await getOrder(created.id));
    await loadOrders();
    clearKey("create-order");
    setCreatingOrder(false); // only on success — a throw keeps the dialog up
  });

  const onAddItem = () => run("add-item", async () => {
    if (!active) return;
    // Empty price → omit it: the server falls back to the product's default.
    let minorUnits: number | undefined;
    if (price.trim() !== "") {
      minorUnits = parseMoneyToMinorUnits(price, active.currencyMinorUnit);
      if (!Number.isFinite(minorUnits) || minorUnits < 0) throw new Error(i18n.t("sales:invalidUnitPrice"));
    }
    const scope = `add-item:${active.id}`;
    await addOrderItem(active.id,
      { productId, quantity: qty, unit, unitPriceMinorUnits: minorUnits },
      keyFor(scope));
    setActive(await getOrder(active.id));
    clearKey(scope);
  });

  const onUpdateItem = (itemId: string) => run(`update-item:${itemId}`, async () => {
    if (!active) return;
    const minorUnits = parseMoneyToMinorUnits(editPrice, active.currencyMinorUnit);
    if (!Number.isFinite(minorUnits) || minorUnits < 0) throw new Error(i18n.t("sales:invalidUnitPrice"));
    const scope = `update-item:${itemId}`;
    await updateOrderItem(active.id, itemId,
      { quantity: editQty, unitPriceMinorUnits: minorUnits }, keyFor(scope));
    setEditItemId(null);
    setActive(await getOrder(active.id));
    clearKey(scope);
  });

  const onRemoveItem = (itemId: string) => run(`remove-item:${itemId}`, async () => {
    if (!active) return;
    const scope = `remove-item:${itemId}`;
    await removeOrderItem(active.id, itemId, keyFor(scope));
    setActive(await getOrder(active.id));
    clearKey(scope);
  });

  // One-way actions (#59). Confirm BEFORE run() so buttons don't flash
  // disabled while the user decides.
  const onConfirm = async () => {
    const ok = await confirm({
      title: i18n.t("sales:confirmOrderTitle"),
      body: i18n.t("sales:confirmOrderBody"),
      confirmLabel: i18n.t("sales:confirmOrderConfirmLabel"),
    });
    if (!ok || !active) return;
    void run(`confirm:${active.id}`, async () => {
      const scope = `confirm:${active.id}`;
      await confirmOrder(active.id, keyFor(scope));
      const refreshed = await getOrder(active.id);
      setActive(refreshed);
      setMessage(i18n.t("sales:orderConfirmed", { ref: refreshed.referenceNumber }));
      await loadOrders();
      clearKey(scope);
    });
  };

  const onCancel = async () => {
    // Cancel is a status change: the order keeps its lines but becomes
    // read-only and can't be confirmed.
    const ok = await confirm({
      title: i18n.t("sales:cancelDraftTitle"),
      body: i18n.t("sales:cancelDraftBody"),
      confirmLabel: i18n.t("sales:cancelDraft"),
      destructive: true,
    });
    if (!ok || !active) return;
    void run(`cancel:${active.id}`, async () => {
      const scope = `cancel:${active.id}`;
      await cancelOrder(active.id, keyFor(scope));
      setActive(null);
      setMessage(i18n.t("sales:draftOrderCancelled"));
      await loadOrders();
      clearKey(scope);
    });
  };

  // Undo of a mistaken confirm (#60). Reason prompt doubles as the confirm
  // dialog, hoisted above run() like the other one-way actions; cancelling the
  // prompt aborts the void.
  const refreshPayments = async (orderId: string) =>
    setPayments(await listOrderPayments(orderId));

  const onRecordPayment = () => void run("record-payment", async () => {
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
    setMessage(i18n.t("sales:paymentRecorded"));
    setPaying(false); // only on success — a throw keeps the dialog up
  });

  const onVoidPayment = async (paymentId: string, version: number) => {
    const reason = await askReason({
      title: i18n.t("sales:voidPaymentTitle"),
      body: i18n.t("sales:voidPaymentBody"),
      confirmLabel: i18n.t("sales:voidPaymentConfirmLabel"),
      destructive: true,
    });
    if (reason === null) return;
    void run(`void-payment:${paymentId}`, async () => {
      if (!active) return;
      const scope = `void-payment:${paymentId}`;
      try {
        await voidPayment(paymentId, { version, reason }, keyFor(scope));
        clearKey(scope);
      } catch (err) {
        // Version-guarded: any SERVER response settles the attempt (the base
        // version prevents double-apply); only transport failures keep the key.
        if (err instanceof ApiError) clearKey(scope);
        throw err;
      }
      await refreshPayments(active.id);
      setMessage(i18n.t("sales:paymentVoided"));
    });
  };

  const onVoid = async () => {
    const reason = await askReason({
      title: i18n.t("sales:voidOrderTitle"),
      body: i18n.t("sales:voidOrderBody"),
      confirmLabel: i18n.t("sales:voidOrderConfirmLabel"),
      destructive: true,
    });
    if (reason === null || !active) return;
    void run(`void:${active.id}`, async () => {
      const scope = `void:${active.id}`;
      await voidOrder(active.id, reason, keyFor(scope));
      const refreshed = await getOrder(active.id);
      setActive(refreshed);
      setMessage(i18n.t("sales:orderVoided", { ref: refreshed.referenceNumber }));
      await loadOrders();
      clearKey(scope);
    });
  };

  // Always fetch fresh on open — the list row may be stale relative to
  // mutations made through the panel since the list was loaded.
  const onOpen = (id: string) => run(`open:${id}`, async () => {
    setActive(await getOrder(id));
  });

  if (loadError) return <section><h2>{t("title")}</h2><p className="error">{loadError}</p></section>;
  if (orders === null) return <section><h2>{t("title")}</h2><p className="muted">{t("loading")}</p></section>;

  return (
    <section>
      <div className="page-head">
        <h2>{t("title")}</h2>
        {customers.length > 0 && (
          // Re-seeded on open, not only at mount: a tab left open across
          // farm-midnight would otherwise offer yesterday as the order date
          // while the picker's own ceiling had already moved on (codex review
          // of #123).
          <button type="button" onClick={() => {
            setError(null); setOrderDate(today); setCreatingOrder(true);
          }}>
            <Plus size={16} aria-hidden /> {t("newOrder")}
          </button>
        )}
      </div>

      {customers.length === 0 && (
        <p className="muted">{t("addCustomerFirst")}</p>
      )}

      {/* Deliberately NOT a <form>: these controls were button-driven, so
          wrapping them in one would newly enforce min/step and swallow the
          screen's own money messages (codex review of #132). */}
      <Dialog open={creatingOrder} title={t("newOrder")} onClose={() => setCreatingOrder(false)}>
        <div className="form-grid">
          <label>{t("customer")}
            <select value={customerId} onChange={(e) => setCustomerId(e.target.value)}>
              {customers.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          </label>
          <label>{t("date")}
            <input type="date" value={orderDate} max={today}
              onChange={(e) => setOrderDate(e.target.value)} />
          </label>
          {/* An open dialog renders its own copy of the error. */}
      {error && !creatingOrder && !paying && <p className="error">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={() => setCreatingOrder(false)}>{tc("cancel")}</button>
            <BusyButton disabled={busy || !customerId} busy={isPending("create-order")}
              onClick={onCreateOrder}>{t("newDraftOrder")}</BusyButton>
          </div>
        </div>
      </Dialog>

      {active && (
        <div className="order-panel">
          <h3>
            {active.referenceNumber} — {customerName(active.customerId)}{" "}
            <span className={active.status === "Draft" ? "muted" : "warn"}>
              [{statusLabel(active.status)}]
            </span>
          </h3>

          {active.items.length > 0 && (
            <table className="data">
              <thead><tr><th>{t("product")}</th><th>{t("qty")}</th><th>{t("eggs")}</th><th>{t("unitPrice")}</th><th>{t("lineTotal")}</th><th></th></tr></thead>
              <tbody>
                {active.items.map((i) => (
                  <tr key={i.id}>
                    <td>{productName(i.productId)}{" "}
                      <span className="muted">{t("perUnit", { unit: i.unit.toLowerCase() })}
                        {i.baseUnitFactor > 1 ? ` ${t("eggsCount", { count: i.baseUnitFactor })}` : ""}</span></td>
                    {editItemId === i.id ? (
                      <>
                        <td><input className="cell" type="number" min={1} value={editQty}
                          aria-label={t("editQuantityAriaLabel")}
                          onChange={(e) => setEditQty(Math.max(1, e.target.valueAsNumber || 1))} /></td>
                        <td>—</td>
                        <td><input className="cell" type="number" min={0}
                          aria-label={t("editUnitPriceAriaLabel")}
                          step={10 ** -active.currencyMinorUnit} value={editPrice}
                          onChange={(e) => setEditPrice(e.target.value)} /></td>
                        <td>—</td>
                        <td>
                          <BusyButton className="link" disabled={busy} busy={isPending(`update-item:${i.id}`)}
                            onClick={() => onUpdateItem(i.id)}>{t("save")}</BusyButton>
                          <button className="link" onClick={() => setEditItemId(null)}>{t("cancelEdit")}</button>
                        </td>
                      </>
                    ) : (
                      <>
                        <td>{i.quantity}</td>
                        <td>{i.quantityBase}</td>
                        <td>{formatMoney(i.unitPriceMinorUnits, i.currencyCode, i.currencyMinorUnit)}</td>
                        <td>{formatMoney(i.unitPriceMinorUnits * i.quantity, i.currencyCode, i.currencyMinorUnit)}</td>
                        <td>
                          {active.status === "Draft" && (
                            <>
                              <button className="link" disabled={busy} onClick={() => {
                                setEditItemId(i.id);
                                setEditQty(i.quantity);
                                // The ORDER's scale, not the line's own
                                // snapshot: the edit is submitted at the
                                // order's, and reading the row's would be the
                                // same two-scales bug one field over.
                                setEditPrice(priceInput(i.unitPriceMinorUnits, priceScale));
                              }}>{t("edit")}</button>
                              <BusyButton className="link" disabled={busy} busy={isPending(`remove-item:${i.id}`)}
                                onClick={() => onRemoveItem(i.id)}>{t("remove")}</BusyButton>
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
          <p><strong>{t("orderTotal", { amount: formatMoney(active.totalMinorUnits, active.currencyCode, active.currencyMinorUnit) })}</strong></p>

          {active.status === "Draft" && (
            <>
              <div className="form-grid">
                <label>{t("product")}
                  <select value={productId} onChange={(e) => {
                    setProductId(e.target.value);
                    const p = products.find((x) => x.id === e.target.value);
                    if (p) {
                      setUnit(p.defaultUnit);
                      setPrice(priceInput(p.defaultPriceMinorUnits, priceScale));
                    }
                  }}>
                    {products.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
                  </select>
                </label>
                <label>{t("perLabel")}
                  <select value={unit} onChange={(e) => setUnit(e.target.value)}>
                    {(["Egg", "Dozen", "Flat", "Tray", "Carton", "Case"] as const).map((u) =>
                      <option key={u} value={u}>{t(`unit${u}`)}</option>)}
                  </select>
                </label>
                <label>{t("quantity")}
                  <input type="number" min={1} value={qty}
                    onChange={(e) => setQty(Math.max(1, e.target.valueAsNumber || 1))} />
                </label>
                <label>{t("unitPriceWithCurrency", { code: active.currencyCode })}
                  <input type="number" min={0} step={10 ** -active.currencyMinorUnit} value={price}
                    onChange={(e) => setPrice(e.target.value)} />
                </label>
                <BusyButton disabled={busy || !productId} busy={isPending("add-item")}
                  onClick={onAddItem}>{t("addLine")}</BusyButton>
              </div>
              <div className="actions">
                <BusyButton disabled={busy || active.items.length === 0}
                  busy={isPending(`confirm:${active.id}`)} onClick={() => void onConfirm()}>
                  {t("confirmOrderButton")}
                </BusyButton>
                <BusyButton className="link" disabled={busy} busy={isPending(`cancel:${active.id}`)}
                  onClick={() => void onCancel()}>{t("cancelDraft")}</BusyButton>
                <button className="link" onClick={() => setActive(null)}>{t("close")}</button>
              </div>
            </>
          )}
          {active.status === "Confirmed" && canSettle && payments && (
            <>
              <h4>{t("payments")}</h4>
              {payments.items.length > 0 && (
                <table className="data">
                  <thead>
                    <tr><th>{t("date")}</th><th>{t("amount")}</th><th>{t("method")}</th><th>{t("reference")}</th><th></th></tr>
                  </thead>
                  <tbody>
                    {payments.items.map((p) => (
                      <tr key={p.id} className={p.voided ? "inactive" : undefined}
                        title={p.note ?? undefined}>
                        <td>{p.paymentDate}</td>
                        <td>{formatMoney(p.amountMinorUnits, p.currencyCode, p.currencyMinorUnit)}</td>
                        <td>{t(`method${p.method as PaymentMethod}`)}</td>
                        <td>{p.referenceNumber ?? "—"}</td>
                        <td>
                          {p.voided
                            ? <span className="badge badge-danger" title={p.voidReason ?? undefined}>{t("statusVoided")}</span>
                            : isAdmin ? (
                              <BusyButton className="link" disabled={busy} busy={isPending(`void-payment:${p.id}`)}
                                onClick={() => void onVoidPayment(p.id, p.version)}>{t("voidPaymentButton")}</BusyButton>
                            ) : null}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
              <p>
                <Trans
                  ns="sales"
                  i18nKey="paymentsSummary"
                  values={{
                    paid: formatMoney(payments.paidMinorUnits, payments.currencyCode, payments.currencyMinorUnit),
                    outstanding: formatMoney(payments.outstandingMinorUnits, payments.currencyCode, payments.currencyMinorUnit),
                  }}
                  components={{ strong: <strong /> }}
                />
              </p>
              {payments.outstandingMinorUnits > 0 && (
                <div className="panel-actions">
                  <button type="button" onClick={() => {
                    setError(null); setPayDate(today); setPaying(true);
                  }}>
                    {t("recordPayment")}
                  </button>
                </div>
              )}

              <Dialog open={paying} title={t("recordPayment")} onClose={() => setPaying(false)}>
                <div className="form-grid">
                  <label>{t("date")}
                    <input type="date" value={payDate} max={today}
                      onChange={(e) => setPayDate(e.target.value)} />
                  </label>
                  <label>{t("amountWithCurrency", { code: payments.currencyCode })}
                    <input type="number"
                      min={(1 / 10 ** payments.currencyMinorUnit).toFixed(payments.currencyMinorUnit)}
                      step="any" value={payAmount}
                      onChange={(e) => setPayAmount(e.target.value)} />
                  </label>
                  <label>{t("method")}
                    <select value={payMethod} onChange={(e) => setPayMethod(e.target.value)}>
                      {(["Cash", "Check", "Card", "BankTransfer", "MobilePayment", "Other"] as const).map((m) => (
                        <option key={m} value={m}>{t(`method${m}`)}</option>
                      ))}
                    </select>
                  </label>
                  <label>{t("referenceOptional")}
                    <input value={payRef} maxLength={50}
                      onChange={(e) => setPayRef(e.target.value)} />
                  </label>
                  <label>{t("noteOptional")}
                    <input value={payNote} maxLength={500}
                      onChange={(e) => setPayNote(e.target.value)} />
                  </label>
                  {/* An open dialog renders its own copy of the error. */}
      {error && !creatingOrder && !paying && <p className="error">{error}</p>}
                  <div className="dialog-foot">
                    <button type="button" className="link" onClick={() => setPaying(false)}>{tc("cancel")}</button>
                    <BusyButton disabled={busy || !payAmount} busy={isPending("record-payment")}
                      onClick={onRecordPayment}>
                      {t("recordPayment")}
                    </BusyButton>
                  </div>
                </div>
              </Dialog>
            </>
          )}
          {active.status === "Voided" && active.voidReason && (
            <p className="muted">{t("voidReasonLabel", { reason: active.voidReason })}</p>
          )}
          {active.status !== "Draft" && (
            <div className="actions">
              {active.status === "Confirmed" && isAdmin && (
                <BusyButton className="link" disabled={busy} busy={isPending(`void:${active.id}`)}
                  onClick={() => void onVoid()}>
                  {t("voidOrderButton")}
                </BusyButton>
              )}
              {active.status === "Confirmed" && !isAdmin && (
                <span className="muted">{t("voidingNeedsAdmin")}</span>
              )}
              <button className="link" onClick={() => setActive(null)}>{t("close")}</button>
            </div>
          )}
        </div>
      )}

      {/* An open dialog renders its own copy of the error. */}
      {error && !creatingOrder && !paying && <p className="error">{error}</p>}
      {message && <p className="success">{message}</p>}

      <h3>{t("ordersHeading")}</h3>
      <div className="form-grid">
        <label>{t("status")}
          <select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)}>
            <option value="">{t("allOption")}</option>
            <option value="Draft">{t("statusDraft")}</option>
            <option value="Confirmed">{t("statusConfirmed")}</option>
            <option value="Cancelled">{t("statusCancelled")}</option>
            <option value="Voided">{t("statusVoided")}</option>
          </select>
        </label>
        <label>{t("customer")}
          <select value={customerFilter} onChange={(e) => setCustomerFilter(e.target.value)}>
            <option value="">{t("allOption")}</option>
            {customers.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </label>
      </div>
      {orders.length === 0 ? (
        <p className="muted">{t("noOrdersMatch")}</p>
      ) : (
        <>
          <table className="data">
            <thead>
              <tr><th>{t("reference")}</th><th>{t("date")}</th><th>{t("customer")}</th><th>{t("status")}</th><th>{t("total")}</th><th></th></tr>
            </thead>
            <tbody>
              {orders.map((o) => (
                <tr key={o.id}>
                  <td>{o.referenceNumber}</td>
                  <td>{o.orderDate}</td>
                  <td>{customerName(o.customerId)}</td>
                  <td><StatusBadge status={o.status} label={statusLabel(o.status)} /></td>
                  <td>{formatMoney(o.totalMinorUnits, o.currencyCode, o.currencyMinorUnit)}</td>
                  <td><button className="link" onClick={() => onOpen(o.id)}>{t("open")}</button></td>
                </tr>
              ))}
            </tbody>
          </table>
          {hasMore && (
            // A guarded READ ("more") — flight-scoped but no BusyButton (#236).
            <button className="link" disabled={busy}
              onClick={() => run("more", () => loadOrders(orders.length))}>{t("loadMore")}</button>
          )}
        </>
      )}

      {confirmDialog}
    </section>
  );
}
