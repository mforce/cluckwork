import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { Plus } from "lucide-react";
import {
  createInventoryItem, activateInventoryItem, deactivateInventoryItem, formatMoney, getAccount,
  listFlocks, listInventoryItems, listInventoryLots, listInventoryMovements, parseMoneyToMinorUnits,
  recordFeedUsage, recordInventoryAdjustment, recordInventoryPurchase, updateInventoryItem,
} from "../api/cluckwork";
import type { Account, Flock, InventoryItem, InventoryLot, InventoryMovement } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useAuth } from "../auth/useAuth";
import { Dialog } from "../components/Dialog";
import { StatusBadge } from "../components/StatusBadge";

// Feed first (spec §12); the rest of the categories get their features later.
const CATEGORIES = [
  "Feed", "Supplement", "Additive", "Medication", "Vaccine",
  "Packaging", "Bedding", "Sanitation", "EquipmentPart", "Other",
];

// Only these can be recorded as flock feed usage (mirrors the API gate).
const FEEDABLE_CATEGORIES = ["Feed", "Supplement", "Additive"];

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
  // Purchases and feed usage are the day's work — open to everyone. The item
  // catalog and stock corrections are admin-only (#73).
  const { isAdmin } = useAuth();
  const [items, setItems] = useState<InventoryItem[] | null>(null);
  // Account currency drives ALL money parsing/formatting here — costs may not
  // exist on an item yet, and assuming 2 decimals corrupts JPY/KWD amounts.
  const [account, setAccount] = useState<Account | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // create form (F131: every capture form on this screen is a dialog)
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState("");
  const [category, setCategory] = useState("Feed");
  const [unit, setUnit] = useState("kg");
  const [defaultCost, setDefaultCost] = useState("");

  // edit — dialog seeded from the row
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editName, setEditName] = useState("");
  const [editUnit, setEditUnit] = useState("");
  const [editCost, setEditCost] = useState("");

  // open item panel: purchase/usage/adjust forms + ledger
  const [active, setActive] = useState<InventoryItem | null>(null);
  const [movements, setMovements] = useState<InventoryMovement[]>([]);
  const [lots, setLots] = useState<InventoryLot[]>([]);
  const [flocks, setFlocks] = useState<Flock[]>([]);
  // the open item's three capture dialogs
  const [purchasing, setPurchasing] = useState(false);
  const [usingStock, setUsingStock] = useState(false);
  const [adjusting, setAdjusting] = useState(false);
  // usage form
  const [usageFlockId, setUsageFlockId] = useState("");
  const [usageDate, setUsageDate] = useState(todayIso());
  const [usageQty, setUsageQty] = useState("");
  const [usageNote, setUsageNote] = useState("");
  // adjustment form
  const [adjustLotId, setAdjustLotId] = useState("");
  const [adjustType, setAdjustType] = useState("Adjustment");
  const [adjustQty, setAdjustQty] = useState("");
  const [adjustReason, setAdjustReason] = useState("");
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
    Promise.all([fetchItems(), getAccount(), listFlocks()])
      .then(([list, acct, flockList]) => {
        setItems(list);
        setAccount(acct);
        // Active + depleted: depleted flocks still take backfilled feed up to
        // their depletion date (the API gates the exact dates). Archived are out.
        const feedable = flockList.filter((f) => f.status !== "Archived");
        setFlocks(feedable);
        const firstActive = feedable.find((f) => f.status === "Active") ?? feedable[0];
        if (firstActive) setUsageFlockId(firstActive.id);
      })
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
    const [rows, lotRows] = await Promise.all([
      listInventoryMovements(itemId, { limit: 100 }),
      listInventoryLots(itemId),
    ]);
    if (ledgerRequest.current !== req) return;
    setMovements(rows);
    setLots(lotRows);
    setAdjustLotId((prev) => lotRows.some((l) => l.id === prev) ? prev : (lotRows[0]?.id ?? ""));
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

  const minorUnit = account?.currencyMinorUnit ?? 2;
  const costStep = 10 ** -minorUnit;

  function toMinorUnits(text: string): number | null {
    if (!text.trim()) return null;
    const parsed = parseMoneyToMinorUnits(text, minorUnit);
    if (!Number.isFinite(parsed) || parsed < 0) throw new Error("Invalid cost.");
    return parsed;
  }

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    const ok = await run("create-item", (key) =>
      createInventoryItem({
        name, category, unit,
        defaultUnitCostMinorUnits: toMinorUnits(defaultCost),
      }, key));
    if (ok) {
      setName("");
      setDefaultCost("");
      setMessage("Item created.");
      setCreating(false);
    }
  }

  function startEdit(i: InventoryItem) {
    setError(null);
    setCreating(false);
    setEditingId(i.id);
    setEditName(i.name);
    setEditUnit(i.unit);
    setEditCost(i.defaultCostMinorUnits === null
      ? ""
      : (i.defaultCostMinorUnits / 10 ** minorUnit).toFixed(minorUnit));
  }

  async function onSaveEdit(e: FormEvent) {
    e.preventDefault();
    const id = editingId;
    if (id === null) return;
    const ok = await run(`update:${id}`, (key) =>
      updateInventoryItem(id, {
        name: editName, unit: editUnit,
        defaultUnitCostMinorUnits: toMinorUnits(editCost),
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
        unitCostMinorUnits: toMinorUnits(purchaseCost),
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
      setPurchasing(false);
    }
  }

  async function onRecordUsage(e: FormEvent) {
    e.preventDefault();
    if (!active) return;
    const qty = parseFloat(usageQty);
    if (!Number.isFinite(qty) || qty <= 0) {
      setError("Quantity must be a positive number.");
      return;
    }
    const ok = await run(`usage:${active.id}`, (key) =>
      recordFeedUsage(active.id, {
        flockId: usageFlockId,
        date: usageDate,
        quantity: qty,
        note: usageNote.trim() || undefined,
      }, key), active.id);
    if (ok) {
      setUsageQty("");
      setUsageNote("");
      setMessage("Feed usage recorded — stock drained oldest lots first.");
      setUsingStock(false);
    }
  }

  async function onAdjust(e: FormEvent) {
    e.preventDefault();
    if (!active) return;
    const delta = parseFloat(adjustQty);
    if (!Number.isFinite(delta) || delta === 0) {
      setError("Adjustment quantity must be a non-zero number (negative removes stock).");
      return;
    }
    if (!adjustReason.trim()) {
      setError("A reason is required for corrections.");
      return;
    }
    const ok = await run(`adjust:${active.id}:${adjustLotId}`, (key) =>
      recordInventoryAdjustment(active.id, {
        inventoryLotId: adjustLotId,
        date: todayIso(),
        type: adjustType,
        quantityDelta: adjustType === "Discard" ? -Math.abs(delta) : delta,
        reason: adjustReason.trim(),
      }, key), active.id);
    if (ok) {
      setAdjustQty("");
      setAdjustReason("");
      setMessage("Correction recorded in the ledger.");
      setAdjusting(false);
    }
  }

  const lotLabel = (l: InventoryLot) =>
    `${l.receivedDate}${l.lotNumber ? ` · ${l.lotNumber}` : ""} — ${l.quantityAvailable}/${l.quantityReceived}`;

  const costText = (i: InventoryItem) =>
    i.defaultCostMinorUnits !== null && i.defaultCostCurrencyCode
      ? formatMoney(i.defaultCostMinorUnits, i.defaultCostCurrencyCode,
          i.defaultCostCurrencyMinorUnit ?? minorUnit)
      : "—";

  if (error && items === null) {
    return <section><h2>Feed &amp; inventory</h2><p className="error">{error}</p></section>;
  }
  if (items === null) {
    return <section><h2>Feed &amp; inventory</h2><p className="muted">Loading…</p></section>;
  }

  const dialogOpen = creating || editingId !== null || purchasing || usingStock || adjusting;
  const canFeed = active !== null && FEEDABLE_CATEGORIES.includes(active.category);

  return (
    <section>
      <div className="page-head">
        <h2>Feed &amp; inventory</h2>
        {isAdmin && (
          <button type="button" onClick={() => { setError(null); setEditingId(null); setCreating(true); }}>
            <Plus size={16} aria-hidden /> New item
          </button>
        )}
      </div>
      <p className="muted">
        Receive stock as purchases; every change lands in the item's movement
        ledger. Recording feed usage against flocks arrives next.
      </p>

      {/* Gated like the inline form was: a role change mid-edit closes it. */}
      <Dialog open={creating && isAdmin} title="New inventory item" onClose={() => setCreating(false)}>
        <form className="inline-form" onSubmit={onCreate}>
          <label>Item name *
            <input value={name} required maxLength={200}
              onChange={(e) => setName(e.target.value)} />
          </label>
          <label>Category
            <select value={category} onChange={(e) => setCategory(e.target.value)}>
              {CATEGORIES.map((c) => <option key={c} value={c}>{c}</option>)}
            </select>
          </label>
          <label>Unit *
            <input value={unit} required maxLength={20}
              onChange={(e) => setUnit(e.target.value)} />
          </label>
          <label>Default cost/unit
            <input className="cell" type="number" min={0} step={costStep} value={defaultCost}
              onChange={(e) => setDefaultCost(e.target.value)} />
          </label>
          {error && <p className="error">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={() => setCreating(false)}>Cancel</button>
            <button type="submit" disabled={busy}>Add item</button>
          </div>
        </form>
      </Dialog>

      <Dialog open={editingId !== null && isAdmin} title="Edit item" onClose={() => setEditingId(null)}>
        {/* noValidate: the row's save used to be a plain button, so the browser
            never enforced min/step — toMinorUnits' own message did. */}
        <form className="inline-form" noValidate onSubmit={onSaveEdit}>
          <label>Item name
            <input value={editName} maxLength={200}
              onChange={(e) => setEditName(e.target.value)} />
          </label>
          <label>Unit
            <input value={editUnit} maxLength={20}
              onChange={(e) => setEditUnit(e.target.value)} />
          </label>
          <label>Default cost/unit
            <input className="cell" type="number" min={0} step={costStep} value={editCost}
              onChange={(e) => setEditCost(e.target.value)} />
          </label>
          {error && <p className="error">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={() => setEditingId(null)}>Cancel</button>
            <button type="submit" disabled={busy}>Save</button>
          </div>
        </form>
      </Dialog>

      {/* Whichever dialog is open renders its own copy of the error. */}
      {error && !dialogOpen && <p className="error">{error}</p>}
      {message && <p className="success">{message}</p>}

      {active && (
        <div className="order-panel">
          <h3>{active.name} — {active.quantityOnHand} {active.unit} on hand</h3>

          {/* One row of actions; each opens its own dialog so the ledger below
              stays put instead of being pushed down by three stacked forms. */}
          <div className="panel-actions">
            <button type="button" onClick={() => { setError(null); setPurchasing(true); }}>
              <Plus size={16} aria-hidden /> Record purchase
            </button>
            {canFeed && flocks.length > 0 && (
              <button type="button" onClick={() => { setError(null); setUsingStock(true); }}>
                Record usage
              </button>
            )}
            {isAdmin && lots.length > 0 && (
              <button type="button" className="link" onClick={() => { setError(null); setAdjusting(true); }}>
                Correct stock
              </button>
            )}
          </div>

          {/* Why an action is unavailable, in the place the button would be. */}
          {!canFeed && (
            <p className="muted">
              {active.category} items aren&apos;t fed to flocks — usage applies to
              Feed, Supplement, and Additive items only.
            </p>
          )}
          {canFeed && flocks.length === 0 && (
            <p className="muted">No flocks — usage needs a flock to feed.</p>
          )}
          {!isAdmin ? (
            <p className="muted">Stock corrections need an admin.</p>
          ) : lots.length === 0 ? (
            <p className="muted">No lots yet — corrections target a received lot.</p>
          ) : null}

          <Dialog open={purchasing} title={`Record purchase — ${active.name}`} onClose={() => setPurchasing(false)}>
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
                <input type="number" min={0} step={costStep} value={purchaseCost}
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
              {error && <p className="error">{error}</p>}
              <div className="dialog-foot">
                <button type="button" className="link" onClick={() => setPurchasing(false)}>Cancel</button>
                <button type="submit" disabled={busy}>Record purchase</button>
              </div>
            </form>
          </Dialog>

          <Dialog open={usingStock} title={`Record usage — ${active.name}`} onClose={() => setUsingStock(false)}>
            <form className="form-grid" onSubmit={onRecordUsage}>
              <label>Flock
                <select value={usageFlockId} onChange={(e) => setUsageFlockId(e.target.value)}>
                  {flocks.map((f) => (
                    <option key={f.id} value={f.id}>
                      {f.name}{f.status === "Depleted" ? " (depleted — backfill only)" : ""}
                    </option>
                  ))}
                </select>
              </label>
              <label>Date
                <input type="date" value={usageDate} max={todayIso()} required
                  onChange={(e) => setUsageDate(e.target.value)} />
              </label>
              <label>Quantity ({active.unit})
                <input type="number" min={0.001} step={0.001} value={usageQty} required
                  onChange={(e) => setUsageQty(e.target.value)} />
              </label>
              <label>Note
                <input value={usageNote} maxLength={500}
                  onChange={(e) => setUsageNote(e.target.value)} />
              </label>
              {error && <p className="error">{error}</p>}
              <div className="dialog-foot">
                <button type="button" className="link" onClick={() => setUsingStock(false)}>Cancel</button>
                <button type="submit" disabled={busy || !usageFlockId}>Record usage</button>
              </div>
            </form>
          </Dialog>

          <Dialog open={adjusting && isAdmin} title={`Correct stock — ${active.name}`} onClose={() => setAdjusting(false)}>
            <form className="form-grid" onSubmit={onAdjust}>
              <label>Lot
                <select value={adjustLotId} onChange={(e) => setAdjustLotId(e.target.value)}>
                  {lots.map((l) => <option key={l.id} value={l.id}>{lotLabel(l)}</option>)}
                </select>
              </label>
              <label>Type
                <select value={adjustType} onChange={(e) => setAdjustType(e.target.value)}>
                  <option value="Adjustment">Adjustment (±)</option>
                  <option value="Discard">Discard (write-off)</option>
                </select>
              </label>
              <label>Quantity ({active.unit})
                <input type="number" step={0.001} value={adjustQty} required
                  placeholder={adjustType === "Discard" ? "amount discarded" : "± correction"}
                  onChange={(e) => setAdjustQty(e.target.value)} />
              </label>
              <label>Reason *
                <input value={adjustReason} maxLength={500} required
                  onChange={(e) => setAdjustReason(e.target.value)} />
              </label>
              {error && <p className="error">{error}</p>}
              <div className="dialog-foot">
                <button type="button" className="link" onClick={() => setAdjusting(false)}>Cancel</button>
                <button type="submit" disabled={busy || !adjustLotId}>Record correction</button>
              </div>
            </form>
          </Dialog>

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
              <td>{i.name}</td>
              <td>{i.category}</td>
              <td>{i.quantityOnHand} {i.unit}</td>
              <td>{costText(i)}</td>
              <td><StatusBadge status={i.active ? "Active" : "Inactive"} /></td>
              <td>
                <button className="link" disabled={busy} onClick={() => void onOpen(i)}>open</button>
                {isAdmin && (
                  <>
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
                  </>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
