import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { Plus } from "lucide-react";
import {
  activateProduct, createProduct, deactivateProduct, formatMoney,
  getAccount, listEggGrades, listEggUnitConversions, listProducts,
  updateEggUnitConversion, updateProduct,
} from "../api/cluckwork";
import type { EggGrade, EggUnitConversion, Product } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useAuth } from "../auth/useAuth";
import { Dialog } from "../components/Dialog";
import { StatusBadge } from "../components/StatusBadge";

// Spec §10.1 default_unit values usable for egg products; packed units resolve
// through the conversions below at sale time (part 2 of #97).
// "Other" is deliberately absent: it has no packed-unit conversion row this
// phase, so an Other product could never resolve to eggs (codex review of #98).
const EGG_UNITS = ["Egg", "Dozen", "Flat", "Tray", "Carton", "Case"];

function errorMessage(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// #97 (part 1): the product catalog — what the farm sells. Egg products map to
// an egg grade; sales screens switch from raw grades to products in part 2.
// No hard delete — future sold lines reference products forever.
export function ProductsPage() {
  const { isAdmin } = useAuth();
  const [products, setProducts] = useState<Product[] | null>(null);
  const [grades, setGrades] = useState<EggGrade[]>([]);
  const [conversions, setConversions] = useState<EggUnitConversion[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // create form (F131: in a dialog)
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState("");
  const [unit, setUnit] = useState("Dozen");
  const [gradeId, setGradeId] = useState("");
  const [price, setPrice] = useState("");
  const [notes, setNotes] = useState("");

  // edit (products) — dialog seeded from the row
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editName, setEditName] = useState("");
  const [editUnit, setEditUnit] = useState("Dozen");
  const [editGradeId, setEditGradeId] = useState("");
  const [editPrice, setEditPrice] = useState("");
  const [editNotes, setEditNotes] = useState("");

  // edit (conversions) — dialog seeded from the row
  const [editingConvId, setEditingConvId] = useState<string | null>(null);
  const [editEggs, setEditEggs] = useState(1);
  const [editConvActive, setEditConvActive] = useState(true);

  // CREATE prices parse with the ACCOUNT currency (what the new product will
  // snapshot); EDIT prices parse with that product's own snapshot — never
  // another row's precision (codex review of #98).
  const [currency, setCurrency] = useState<{ code: string; minor: number }>({ code: "", minor: 2 });

  const keys = useRef(new Map<string, string>());
  const keyFor = (scope: string) => {
    const existing = keys.current.get(scope);
    if (existing) return existing;
    const fresh = crypto.randomUUID();
    keys.current.set(scope, fresh);
    return fresh;
  };
  const clearKey = (scope: string) => keys.current.delete(scope);

  const refresh = async () => {
    const [p, c] = await Promise.all([
      listProducts({ includeInactive: true }),
      listEggUnitConversions(),
    ]);
    setProducts(p);
    setConversions(c);
  };

  useEffect(() => {
    Promise.all([
      listProducts({ includeInactive: true }),
      listEggUnitConversions(),
      listEggGrades(),
      getAccount(),
    ])
      .then(([p, c, g, a]) => {
        setProducts(p);
        setConversions(c);
        setGrades(g.filter((x) => x.isSaleable));
        setCurrency({ code: a.currencyCode, minor: a.currencyMinorUnit });
      })
      .catch(() => setError("Could not load the catalog. Is the API up?"));
  }, []);

  // Exact string parsing — never float × 10^n (money rule).
  const toMinorUnits = (display: string, minor: number): number | null => {
    const trimmed = display.trim();
    if (!trimmed) return null;
    const match = /^(\d+)(?:\.(\d+))?$/.exec(trimmed);
    if (!match) throw new Error("Enter the price as a plain number.");
    const frac = match[2] ?? "";
    if (frac.length > minor)
      throw new Error(minor === 0
        ? "This currency has no decimal places."
        : `At most ${minor} decimal places for this currency.`);
    return Number(match[1]) * 10 ** minor + Number(frac.padEnd(minor, "0") || 0);
  };

  async function run(scope: string, action: (key: string) => Promise<unknown>) {
    if (busy) return false;
    setBusy(true);
    setError(null);
    try {
      await action(keyFor(scope));
      // Refresh must succeed before the key rotates (idempotent retry contract).
      await refresh();
      clearKey(scope);
      return true;
    } catch (err) {
      setError(errorMessage(err));
      return false;
    } finally {
      setBusy(false);
    }
  }

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    let priceMinor: number | null;
    try {
      priceMinor = toMinorUnits(price, currency.minor);
    } catch (err) {
      setError(errorMessage(err));
      return;
    }
    const ok = await run("create-product", (key) =>
      createProduct({
        name,
        productType: "Egg",
        defaultUnit: unit,
        defaultPriceMinorUnits: priceMinor,
        eggGradeId: gradeId,
        notes: notes.trim() || null,
      }, key));
    if (ok) {
      setName("");
      setPrice("");
      setNotes("");
      setCreating(false);
    }
  }

  function startEdit(p: Product) {
    setError(null);
    setCreating(false);
    setEditingConvId(null);
    setEditingId(p.id);
    setEditName(p.name);
    setEditUnit(p.defaultUnit);
    setEditGradeId(p.eggGradeId ?? "");
    setEditPrice(p.defaultPriceMinorUnits === null
      ? ""
      : (p.defaultPriceMinorUnits / 10 ** p.currencyMinorUnit).toFixed(p.currencyMinorUnit));
    setEditNotes(p.notes ?? "");
  }

  async function onSaveEdit(e: FormEvent) {
    e.preventDefault();
    const id = editingId;
    if (id === null) return;
    const target = products?.find((p) => p.id === id);
    let priceMinor: number | null;
    try {
      priceMinor = toMinorUnits(editPrice, target?.currencyMinorUnit ?? currency.minor);
    } catch (err) {
      setError(errorMessage(err));
      return;
    }
    const ok = await run(`update:${id}`, (key) =>
      updateProduct(id, {
        name: editName,
        defaultUnit: editUnit,
        defaultPriceMinorUnits: priceMinor,
        eggGradeId: editGradeId,
        notes: editNotes.trim() || null,
      }, key));
    if (ok) setEditingId(null);
  }

  async function onSaveConversion(e: FormEvent) {
    e.preventDefault();
    const id = editingConvId;
    if (id === null) return;
    const ok = await run(`conv:${id}`, (key) =>
      updateEggUnitConversion(id, { eggsPerUnit: editEggs, active: editConvActive }, key));
    if (ok) setEditingConvId(null);
  }

  const gradeName = (id: string | null) =>
    grades.find((g) => g.id === id)?.name ?? (id ? id.slice(0, 8) : "—");

  if (error && products === null) {
    return <section><h2>Products</h2><p className="error">{error}</p></section>;
  }
  if (products === null) {
    return <section><h2>Products</h2><p className="muted">Loading…</p></section>;
  }

  const editingProduct = products.find((p) => p.id === editingId) ?? null;
  const editingConv = conversions.find((c) => c.id === editingConvId) ?? null;
  const dialogOpen = creating || editingProduct !== null || editingConv !== null;

  return (
    <section>
      <div className="page-head">
        <h2>Products</h2>
        {isAdmin && (
          <button type="button" onClick={() => { setError(null); setEditingId(null); setEditingConvId(null); setCreating(true); }}>
            <Plus size={16} aria-hidden /> New product
          </button>
        )}
      </div>
      <p className="muted">
        What the farm sells. Each egg product maps to an egg grade — sales draw
        stock from that grade&apos;s lots. Deactivating removes a product from
        pickers; history keeps its name.
      </p>

      {/* A dialog renders its own copy of the error; don't double it. */}
      {error && !dialogOpen && <p className="error" role="alert">{error}</p>}

      <Dialog open={creating} title="New product" onClose={() => setCreating(false)}>
        <form onSubmit={(e) => void onCreate(e)} className="inline-form">
          <label>Name
            <input value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} />
          </label>
          <label>Grade
            <select value={gradeId} onChange={(e) => setGradeId(e.target.value)} required>
              <option value="">Pick a grade…</option>
              {grades.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
            </select>
          </label>
          <label>Sold per
            <select value={unit} onChange={(e) => setUnit(e.target.value)}>
              {EGG_UNITS.map((u) => <option key={u} value={u}>{u}</option>)}
            </select>
          </label>
          <label>Default price{currency.code ? ` (${currency.code})` : ""}
            <input type="number" min="0" step={(1 / 10 ** currency.minor).toFixed(currency.minor)}
              value={price} onChange={(e) => setPrice(e.target.value)} placeholder="optional" />
          </label>
          <label>Notes
            <input value={notes} onChange={(e) => setNotes(e.target.value)} maxLength={500} />
          </label>
          {error && <p className="error" role="alert">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={() => setCreating(false)}>Cancel</button>
            <button disabled={busy}>Add product</button>
          </div>
        </form>
      </Dialog>

      <Dialog open={editingProduct !== null} title="Edit product" onClose={() => setEditingId(null)}>
        <form onSubmit={(e) => void onSaveEdit(e)} className="inline-form">
          <label>Name
            <input value={editName} onChange={(e) => setEditName(e.target.value)} maxLength={100} />
          </label>
          <label>Grade
            <select value={editGradeId} onChange={(e) => setEditGradeId(e.target.value)}>
              {grades.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
            </select>
          </label>
          <label>Sold per
            <select value={editUnit} onChange={(e) => setEditUnit(e.target.value)}>
              {EGG_UNITS.map((u) => <option key={u} value={u}>{u}</option>)}
            </select>
          </label>
          {/* Stepped by THIS product's snapshot precision, not the account's. */}
          <label>Default price{editingProduct ? ` (${editingProduct.currencyCode})` : ""}
            <input type="number" min="0"
              step={editingProduct ? (1 / 10 ** editingProduct.currencyMinorUnit).toFixed(editingProduct.currencyMinorUnit) : "0.01"}
              value={editPrice} onChange={(e) => setEditPrice(e.target.value)} />
          </label>
          <label>Notes
            <input value={editNotes} onChange={(e) => setEditNotes(e.target.value)} maxLength={500} />
          </label>
          {error && <p className="error" role="alert">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={() => setEditingId(null)}>Cancel</button>
            <button type="submit" disabled={busy}>Save</button>
          </div>
        </form>
      </Dialog>

      <Dialog
        open={editingConv !== null}
        title={editingConv ? `Eggs per ${editingConv.unitCode}` : "Packed unit"}
        onClose={() => setEditingConvId(null)}
      >
        <form onSubmit={(e) => void onSaveConversion(e)} className="inline-form">
          <label>Eggs per unit
            <input type="number" min={1} value={editEggs}
              onChange={(e) => setEditEggs(Number(e.target.value))} />
          </label>
          <label className="check">
            <input type="checkbox" checked={editConvActive}
              onChange={(e) => setEditConvActive(e.target.checked)} /> active
          </label>
          {error && <p className="error" role="alert">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={() => setEditingConvId(null)}>Cancel</button>
            <button type="submit" disabled={busy}>Save</button>
          </div>
        </form>
      </Dialog>

      {products.length === 0 ? (
        <p className="muted">No products yet.</p>
      ) : (
        <table className="data">
          <thead>
            <tr><th>Name</th><th>Grade</th><th>Sold per</th><th>Default price</th><th>Status</th>{isAdmin && <th>Actions</th>}</tr>
          </thead>
          <tbody>
            {products.map((p) => (
              <tr key={p.id} className={p.active ? undefined : "muted"}>
                <td title={p.notes ?? undefined}>{p.name}</td>
                <td>{gradeName(p.eggGradeId)}</td>
                <td>{p.defaultUnit}</td>
                <td>{p.defaultPriceMinorUnits === null
                  ? "—"
                  : formatMoney(p.defaultPriceMinorUnits, p.currencyCode, p.currencyMinorUnit)}</td>
                <td><StatusBadge status={p.active ? "Active" : "Inactive"} /></td>
                {isAdmin && (
                  <td>
                    <button className="link" disabled={busy} onClick={() => startEdit(p)}>edit</button>{" "}
                    {p.active ? (
                      <button className="link" disabled={busy}
                        onClick={() => void run(`deact:${p.id}`, (key) => deactivateProduct(p.id, key))}>
                        deactivate
                      </button>
                    ) : (
                      <button className="link" disabled={busy}
                        onClick={() => void run(`act:${p.id}`, (key) => activateProduct(p.id, key))}>
                        activate
                      </button>
                    )}
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <h3>Packed units</h3>
      <p className="muted">
        How many eggs each unit holds when selling (a carton is 12, 18, or 30
        depending on your market — set yours). Changing a unit only affects
        future sales; recorded orders keep the count they were sold with.
      </p>
      <table className="data">
        <thead>
          <tr><th>Unit</th><th>Eggs per unit</th><th>Status</th>{isAdmin && <th>Actions</th>}</tr>
        </thead>
        <tbody>
          {conversions.map((c) => (
            <tr key={c.id} className={c.active ? undefined : "muted"}>
              <td>{c.unitCode}</td>
              <td>{c.eggsPerUnit}</td>
              <td>{c.active ? "Active" : "Inactive"}</td>
              {isAdmin && (
                <td>
                  {c.unitCode === "Individual" ? (
                    <span className="muted">always 1</span>
                  ) : (
                    <button className="link" disabled={busy}
                      onClick={() => {
                        setError(null);
                        setCreating(false);
                        setEditingId(null);
                        setEditingConvId(c.id);
                        setEditEggs(c.eggsPerUnit);
                        setEditConvActive(c.active);
                      }}>
                      edit
                    </button>
                  )}
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
