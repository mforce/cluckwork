import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import {
  createInventoryItem, activateInventoryItem, deactivateInventoryItem, formatMoney,
  listInventoryItems, listInventoryMovements, recordInventoryPurchase, updateInventoryItem,
} from "../api/cluckwork";
import type { InventoryItem, InventoryMovement } from "../api/cluckwork";
import { ApiError } from "../api/client";

// Feed first (spec §12); the rest of the categories get their features later.
const CATEGORIES = [
  "Feed", "Supplement", "Additive", "Medication", "Vaccine",
  "Packaging", "Bedding", "Sanitation", "EquipmentPart", "Other",
];

function todayIso(): string {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// F15 (#66, PR 1): inventory catalog + receiving stock. Items define what and
// how it's measured; lots carry quantities/cost; the movement ledger explains
// every change. Feed usage (consumption) is the follow-up PR.
export function InventoryPage() {
  const [items, setItems] = useState<InventoryItem[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // create form
  const [name, setName] = useState("");
  const [category, setCategory] = useState("Feed");
  const [unit, setUnit] = useState("kg");
  const [defaultCost, setDefaultCost] = useState("");

  // inline edit
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editName, setEditName] = useState("");
  const [editUnit, setEditUnit] = useState("");
  const [editCost, setEditCost] = useState("");

  // open item panel: purchase form + ledger
  const [active, setActive] = useState<InventoryItem | null>(null);
  const [movements, setMovements] = useState<InventoryMovement[]>([]);
  const [purchaseDate, setPurchaseDate] = useState(todayIso());
  const [purchaseQty, setPurchaseQty] = useState("");
  const [purchaseCost, setPurchaseCost] = useState("");
  const [lotNumber, setLotNumber] = useState("");
  const [expiryDate, setExpiryDate] = useState("");
  const [purchaseNote, setPurchaseNote] = useState("");
  // Guards stale ledger responses when switching items quickly.
  const ledgerRequest = useRef(0);

  // Stable idempotency keys per logical mutation, rotated only after the full
  // action (write + refresh) succeeds — same contract as the other screens.
  const keys = useRef(new Map<string, string>());
  const keyFor = (scope: string) => {
    const existing = keys.current.get(scope);
    if (existing) return existing;
    const fresh = crypto.randomUUID();
    keys.current.set(scope, fresh);
    return fresh;
  };
  const clearKey = (scope: string) => keys.current.delete(scope);

  const fetchItems = () => listInventoryItems({ includeInactive: true });

  useEffect(() => {
    fetchItems()
      .then(setItems)
      .catch(() => setError("Could not load inventory. Is the API up?"));
  }, []);

  async function refreshAll(openItemId?: string) {
    const fresh = await fetchItems();
    setItems(fresh);
    const target = openItemId ?? active?.id;
    if (target) {
      const stillThere = fresh.find((i) => i.id === target) ?? null;
      setActive(stillThere);
      if (stillThere) await loadLedger(stillThere.id);
    }
  }

  async function loadLedger(itemId: string) {
    const req = ++ledgerRequest.current;
    const rows = await listInventoryMovements(itemId, { limit: 100 });
    if (ledgerRequest.current === req) setMovements(rows);
  }

  async function run(scope: string, action: (key: string) => Promise<unknown>, openItemId?: string) {
    if (busy) return false;
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      await action(keyFor(scope));
      // The refresh must succeed before the key rotates: if it throws, the key
      // survives and a retry replays the idempotent write instead of repeating it.
      await refreshAll(openItemId);
      clearKey(scope);
      return true;
    } catch (err) {
      setError(errText(err));
      return false;
    } finally {
      setBusy(false);
    }
  }

  function toMinorUnits(text: string, minorUnit: number): number | null {
    if (!text.trim()) return null;
    const parsed = Math.round(parseFloat(text) * 10 ** minorUnit);
    if (!Number.isFinite(parsed) || parsed < 0) throw new Error("Invalid cost.");
    return parsed;
  }

  // Account currency comes back on items that have a default cost; fall back
  // to 2 decimals for input parsing when none is set yet.
  const costMinorUnit = (i?: InventoryItem | null) => i?.defaultCostCurrencyMinorUnit ?? 2;

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    const ok = await run("create-item", (key) =>
      createInventoryItem({
        name, category, unit,
        defaultUnitCostMinorUnits: toMinorUnits(defaultCost, 2),
      }, key));
    if (ok) {
      setName("");
      setDefaultCost("");
      setMessage("Item created.");
    }
  }

  function startEdit(i: InventoryItem) {
    setEditingId(i.id);
    setEditName(i.name);
    setEditUnit(i.unit);
    setEditCost(i.defaultCostMinorUnits === null
      ? ""
      : (i.defaultCostMinorUnits / 10 ** costMinorUnit(i)).toFixed(costMinorUnit(i)));
  }

  async function onSaveEdit(i: InventoryItem) {
    const ok = await run(`update:${i.id}`, (key) =>
      updateInventoryItem(i.id, {
        name: editName, unit: editUnit,
        defaultUnitCostMinorUnits: toMinorUnits(editCost, costMinorUnit(i)),
      }, key));
    if (ok) setEditingId(null);
  }

  async function onOpen(i: InventoryItem) {
    setActive(i);
    setMovements([]);
    try {
      await loadLedger(i.id);
    } catch {
      setError("Could not load the movement ledger.");
    }
  }

  async function onPurchase(e: FormEvent) {
    e.preventDefault();
    if (!active) return;
    const qty = parseFloat(purchaseQty);
    if (!Number.isFinite(qty) || qty <= 0) {
      setError("Quantity must be a positive number.");
      return;
    }
    const ok = await run(`purchase:${active.id}`, (key) =>
      recordInventoryPurchase(active.id, {
        receivedDate: purchaseDate,
        quantity: qty,
        unitCostMinorUnits: toMinorUnits(purchaseCost, costMinorUnit(active)),
        lotNumber: lotNumber.trim() || undefined,
        expiryDate: expiryDate || undefined,
        note: purchaseNote.trim() || undefined,
      }, key), active.id);
    if (ok) {
      setPurchaseQty("");
      setPurchaseCost("");
      setLotNumber("");
      setExpiryDate("");
      setPurchaseNote("");
      setMessage("Purchase recorded — stock received.");
    }
  }

  const costText = (i: InventoryItem) =>
    i.defaultCostMinorUnits !== null && i.defaultCostCurrencyCode
      ? formatMoney(i.defaultCostMinorUnits, i.defaultCostCurrencyCode, i.defaultCostCurrencyMinorUnit ?? 2)
      : "—";

  if (error && items === null) {
    return <section><h2>Feed &amp; inventory</h2><p className="error">{error}</p></section>;
  }
  if (items === null) {
    return <section><h2>Feed &amp; inventory</h2><p className="muted">Loading…</p></section>;
  }

  return (
    <section>
      <h2>Feed &amp; inventory</h2>
      <p className="muted">
        Receive stock as purchases; every change lands in the item's movement
        ledger. Recording feed usage against flocks arrives next.
      </p>

      <form className="inline-form" onSubmit={onCreate}>
        <input placeholder="Item name *" value={name} required maxLength={200}
          onChange={(e) => setName(e.target.value)} />
        <select value={category} onChange={(e) => setCategory(e.target.value)}>
          {CATEGORIES.map((c) => <option key={c} value={c}>{c}</option>)}
        </select>
        <input placeholder="Unit *" value={unit} required maxLength={20} size={6}
          onChange={(e) => setUnit(e.target.value)} />
        <label className="muted">Default cost/unit
          <input className="cell" type="number" min={0} step="0.01" value={defaultCost}
            onChange={(e) => setDefaultCost(e.target.value)} />
        </label>
        <button type="submit" disabled={busy}>Add item</button>
      </form>

      {error && <p className="error">{error}</p>}
      {message && <p className="success">{message}</p>}

      {active && (
        <div className="order-panel">
          <h3>{active.name} — {active.quantityOnHand} {active.unit} on hand</h3>

          <form className="form-grid" onSubmit={onPurchase}>
            <label>Received
              <input type="date" value={purchaseDate} max={todayIso()} required
                onChange={(e) => setPurchaseDate(e.target.value)} />
            </label>
            <label>Quantity ({active.unit})
              <input type="number" min={0.001} step={0.001} value={purchaseQty} required
                onChange={(e) => setPurchaseQty(e.target.value)} />
            </label>
            <label>Unit cost {active.defaultCostCurrencyCode ? `(${active.defaultCostCurrencyCode})` : ""}
              <input type="number" min={0} step="0.01" value={purchaseCost}
                placeholder={active.defaultCostMinorUnits !== null ? "item default" : "required"}
                onChange={(e) => setPurchaseCost(e.target.value)} />
            </label>
            <label>Lot #
              <input value={lotNumber} maxLength={100}
                onChange={(e) => setLotNumber(e.target.value)} />
            </label>
            <label>Expiry
              <input type="date" value={expiryDate} min={purchaseDate}
                onChange={(e) => setExpiryDate(e.target.value)} />
            </label>
            <label>Note
              <input value={purchaseNote} maxLength={500}
                onChange={(e) => setPurchaseNote(e.target.value)} />
            </label>
            <button type="submit" disabled={busy}>Record purchase</button>
          </form>

          {movements.length > 0 ? (
            <table className="data">
              <thead>
                <tr><th>Date</th><th>Type</th><th>Quantity</th><th>Note</th></tr>
              </thead>
              <tbody>
                {movements.map((m) => (
                  <tr key={m.id}>
                    <td>{m.date}</td>
                    <td>{m.type}</td>
                    <td>{m.quantityDelta > 0 ? `+${m.quantityDelta}` : m.quantityDelta} {m.unit}</td>
                    <td>{m.note ?? ""}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : (
            <p className="muted">No movements yet — record a purchase above.</p>
          )}
          <div className="actions">
            <button className="link" onClick={() => setActive(null)}>close</button>
          </div>
        </div>
      )}

      <table className="data">
        <thead>
          <tr><th>Name</th><th>Category</th><th>On hand</th><th>Default cost</th><th>Status</th><th></th></tr>
        </thead>
        <tbody>
          {items.map((i) => (
            <tr key={i.id} className={i.active ? undefined : "inactive"}>
              {editingId === i.id ? (
                <>
                  <td>
                    <input value={editName} maxLength={200}
                      onChange={(e) => setEditName(e.target.value)} />
                  </td>
                  <td>{i.category}</td>
                  <td>
                    {i.quantityOnHand}{" "}
                    <input className="cell" value={editUnit} maxLength={20} size={4}
                      onChange={(e) => setEditUnit(e.target.value)} />
                  </td>
                  <td>
                    <input className="cell" type="number" min={0} step="0.01" value={editCost}
                      onChange={(e) => setEditCost(e.target.value)} />
                  </td>
                  <td>{i.active ? "Active" : "Inactive"}</td>
                  <td>
                    <button className="link" disabled={busy}
                      onClick={() => void onSaveEdit(i)}>save</button>
                    <button className="link" disabled={busy}
                      onClick={() => setEditingId(null)}>cancel</button>
                  </td>
                </>
              ) : (
                <>
                  <td>{i.name}</td>
                  <td>{i.category}</td>
                  <td>{i.quantityOnHand} {i.unit}</td>
                  <td>{costText(i)}</td>
                  <td>{i.active ? "Active" : <span className="warn">Inactive</span>}</td>
                  <td>
                    <button className="link" disabled={busy} onClick={() => void onOpen(i)}>open</button>
                    <button className="link" disabled={busy} onClick={() => startEdit(i)}>edit</button>
                    {i.active ? (
                      <button className="link" disabled={busy}
                        onClick={() => void run(`deactivate:${i.id}`, (key) => deactivateInventoryItem(i.id, key))}>
                        deactivate
                      </button>
                    ) : (
                      <button className="link" disabled={busy}
                        onClick={() => void run(`activate:${i.id}`, (key) => activateInventoryItem(i.id, key))}>
                        activate
                      </button>
                    )}
                  </td>
                </>
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
