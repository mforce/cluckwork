import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import {
  adjustExpense, createExpense, createExpenseCategory, formatMoney, getExpense,
  listExpenseCategories, listExpenses, listFlocks, updateExpenseCategory,
} from "../api/cluckwork";
import type { Expense, ExpenseCategory, Flock } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { Dialog } from "../components/Dialog";
import { useFarmToday } from "../farm/useFarm";
import { newId } from "../lib/ids";

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

const PAGE = 100;

// #87 — basic expenses (spec §16 cut): categories + recording + monthly view.
// Admin-only end to end: the route hides for workers and every endpoint
// carries the Admin policy — money data, unlike the production screens.
export function ExpensesPage() {
  // Farm-local, not browser-local: since #35 the API judges "is this date in
  // the future?" against the FARM's day, so the pickers must agree (#123).
  const today = useFarmToday();
  const [categories, setCategories] = useState<ExpenseCategory[]>([]);
  const [flocks, setFlocks] = useState<Flock[]>([]);
  const [items, setItems] = useState<Expense[] | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [total, setTotal] = useState(0);
  const [currency, setCurrency] = useState<{ code: string; minor: number }>({ code: "", minor: 2 });
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // filters
  const [month, setMonth] = useState(today.slice(0, 7)); // YYYY-MM
  const [filterCategory, setFilterCategory] = useState("");

  // add form
  const [date, setDate] = useState(today);
  const [categoryId, setCategoryId] = useState("");
  const [description, setDescription] = useState("");
  const [amount, setAmount] = useState("");
  const [flockId, setFlockId] = useState("");
  const [note, setNote] = useState("");

  // category management
  const [showCategories, setShowCategories] = useState(false);
  const [addingCategory, setAddingCategory] = useState(false); // F131: in a dialog
  const [newCategoryName, setNewCategoryName] = useState("");

  // edit panel (admin correction, version-guarded)
  const [editing, setEditing] = useState<Expense | null>(null);
  const [editDate, setEditDate] = useState("");
  const [editCategory, setEditCategory] = useState("");
  const [editDescription, setEditDescription] = useState("");
  const [editAmount, setEditAmount] = useState("");
  const [editFlock, setEditFlock] = useState("");
  const [editNote, setEditNote] = useState("");

  // Stable idempotency keys per logical mutation. Version-guarded edits rotate
  // on ANY server response (the version base prevents double-apply); only a
  // transport failure keeps the key for an exact replay (HistoryPage pattern).
  const keys = useRef(new Map<string, string>());
  const keyFor = (scope: string) => {
    const existing = keys.current.get(scope);
    if (existing) return existing;
    const fresh = newId();
    keys.current.set(scope, fresh);
    return fresh;
  };
  const clearKey = (scope: string) => keys.current.delete(scope);
  const settleKey = (scope: string, err?: unknown) => {
    if (err === undefined || err instanceof ApiError) clearKey(scope);
  };

  const monthRange = useCallback((m: string) => {
    const [y, mo] = m.split("-").map(Number);
    const last = new Date(Date.UTC(y, mo, 0)).getUTCDate();
    return { from: `${m}-01`, to: `${m}-${String(last).padStart(2, "0")}` };
  }, []);

  // offset 0 replaces the page (fresh view after filters/mutations); a larger
  // offset appends — months can exceed one page and every row must stay
  // reachable for correction (codex review of #88). The total always covers
  // the WHOLE filtered period regardless of paging.
  const load = useCallback(async (offset = 0) => {
    const { from, to } = monthRange(month);
    const list = await listExpenses({
      from, to, categoryId: filterCategory || undefined, limit: PAGE, offset,
    });
    setItems((prev) => (offset === 0 || prev === null) ? list.items : [...prev, ...list.items]);
    setHasMore(list.items.length === PAGE);
    setTotal(list.totalMinorUnits);
    setCurrency({ code: list.currencyCode, minor: list.currencyMinorUnit });
  }, [month, filterCategory, monthRange]);

  useEffect(() => {
    Promise.all([
      listExpenseCategories({ includeInactive: true }),
      listFlocks({ includeArchived: true, limit: 500 }),
    ])
      .then(([c, f]) => {
        setCategories(c);
        setFlocks(f);
      })
      .catch((err) => setError(errText(err)));
  }, []);

  useEffect(() => {
    load().catch((err) => setError(errText(err)));
  }, [load]);

  const categoryName = (id: string) =>
    categories.find((c) => c.id === id)?.name ?? id.slice(0, 8);
  const flockName = (id: string | null) =>
    id === null ? "—" : flocks.find((f) => f.id === id)?.name ?? id.slice(0, 8);
  const activeCategories = categories.filter((c) => c.active);
  // The edit picker offers active categories plus the expense's own (possibly
  // deactivated) one — keeping it must stay legal (grandfathering).
  const editCategories = editing === null
    ? activeCategories
    : categories.filter((c) => c.active || c.id === editing.expenseCategoryId);

  // Exact decimal parsing — float multiplication silently mis-rounds edge
  // amounts and hides excess decimals; the minor unit is the CALLER's because
  // an old expense keeps its snapshotted denomination, which may differ from
  // the account's current one (codex review of #88).
  const toMinorUnits = (display: string, minor: number) => {
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

  async function run(scope: string, fn: () => Promise<void>) {
    if (busy) return;
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      await fn();
      settleKey(scope);
    } catch (err) {
      settleKey(scope, err);
      setError(errText(err));
    } finally {
      setBusy(false);
    }
  }

  function onAdd(e: FormEvent) {
    e.preventDefault();
    void run("add", async () => {
      await createExpense({
        expenseCategoryId: categoryId,
        date,
        description: description.trim(),
        amountMinorUnits: toMinorUnits(amount, currency.minor),
        flockId: flockId || null,
        note: note.trim() || null,
      }, keyFor("add"));
      // Reset BEFORE the refresh: if the reload fails after the write landed,
      // a still-populated form invites a duplicate re-submit under a fresh
      // key (codex review of #88).
      setDescription("");
      setAmount("");
      setNote("");
      await load();
      setMessage("Expense recorded.");
    });
  }

  function startEdit(x: Expense) {
    setEditing(x);
    setEditDate(x.date);
    setEditCategory(x.expenseCategoryId);
    setEditDescription(x.description);
    setEditAmount((x.amountMinorUnits / 10 ** x.currencyMinorUnit).toFixed(x.currencyMinorUnit));
    setEditFlock(x.flockId ?? "");
    setEditNote(x.note ?? "");
    setMessage(null);
    setError(null);
    // F131: the correction form is a dialog now — it takes focus itself, so
    // there is nothing to scroll to.
  }

  function onSaveEdit(e: FormEvent) {
    e.preventDefault();
    if (editing === null) return;
    const target = editing;
    const scope = `edit:${target.id}`;
    void run(scope, async () => {
      try {
        const updated = await adjustExpense(target.id, {
          version: target.version,
          expenseCategoryId: editCategory,
          date: editDate,
          description: editDescription.trim(),
          amountMinorUnits: toMinorUnits(editAmount, target.currencyMinorUnit),
          flockId: editFlock || null,
          note: editNote.trim() || null,
        }, keyFor(scope));
        setEditing(null);
        setItems((prev) => prev?.map((x) => (x.id === updated.id ? updated : x)) ?? null);
        await load();
        setMessage("Expense corrected.");
      } catch (err) {
        // 409: someone else corrected it meanwhile — rebind the panel to the
        // fresh row (only unsent typing is lost, and the banner says why).
        // Fetched by id, not by date: the winning correction may have MOVED
        // the expense to another day (pi review of #88).
        if (err instanceof ApiError && err.status === 409) {
          settleKey(scope, err);
          await load();
          startEdit(await getExpense(target.id));
          throw new Error("This expense was changed by someone else — the form now shows the latest values; re-apply your correction.");
        }
        throw err;
      }
    });
  }

  function onAddCategory(e: FormEvent) {
    e.preventDefault();
    const scope = `add-category:${newCategoryName.trim().toLowerCase()}`;
    void run(scope, async () => {
      await createExpenseCategory({ name: newCategoryName.trim() }, keyFor(scope));
      setNewCategoryName("");
      setAddingCategory(false);
      setCategories(await listExpenseCategories({ includeInactive: true }));
      setMessage("Category created.");
    });
  }

  function onToggleCategory(c: ExpenseCategory) {
    const scope = `toggle-category:${c.id}`;
    void run(scope, async () => {
      await updateExpenseCategory(c.id, { name: c.name, active: !c.active }, keyFor(scope));
      setCategories(await listExpenseCategories({ includeInactive: true }));
      setMessage(c.active ? `Category "${c.name}" deactivated.` : `Category "${c.name}" reactivated.`);
    });
  }

  return (
    <section>
      <h2>Expenses</h2>

      <div className="filters">
        <label>Month
          <input type="month" value={month} max={today.slice(0, 7)}
            onChange={(e) => setMonth(e.target.value)} />
        </label>
        <label>Category
          <select value={filterCategory} onChange={(e) => setFilterCategory(e.target.value)}>
            <option value="">All categories</option>
            {categories.map((c) => (
              <option key={c.id} value={c.id}>{c.name}{c.active ? "" : " (deactivated)"}</option>
            ))}
          </select>
        </label>
        <button className="link" type="button" onClick={() => setShowCategories((v) => !v)}>
          {showCategories ? "hide categories" : "manage categories"}
        </button>
      </div>

      <p><strong>Month total: {formatMoney(total, currency.code, currency.minor)}</strong></p>

      {showCategories && (
        <div className="order-panel">
          <h3>Expense categories</h3>
          <div className="panel-actions">
            <button type="button" onClick={() => { setError(null); setAddingCategory(true); }}>
              New category
            </button>
          </div>

          <Dialog open={addingCategory} title="New expense category" onClose={() => setAddingCategory(false)}>
            <form className="inline-form" onSubmit={onAddCategory}>
              <label>Category name
                <input value={newCategoryName} required
                  onChange={(e) => setNewCategoryName(e.target.value)} />
              </label>
              {error && <p className="error">{error}</p>}
              <div className="dialog-foot">
                <button type="button" className="link" onClick={() => setAddingCategory(false)}>Cancel</button>
                <button type="submit" disabled={busy}>Add category</button>
              </div>
            </form>
          </Dialog>

          <ul>
            {categories.map((c) => (
              <li key={c.id}>
                {c.name}{c.active ? "" : " (deactivated)"}{" "}
                <button className="link" type="button" disabled={busy}
                  onClick={() => onToggleCategory(c)}>
                  {c.active ? "deactivate" : "reactivate"}
                </button>
              </li>
            ))}
            {categories.length === 0 && <li className="muted">No categories yet — add one above.</li>}
          </ul>
        </div>
      )}

      <h3>Record an expense</h3>
      <form className="form-grid" onSubmit={onAdd}>
        <label>Date
          <input type="date" value={date} max={today} required
            onChange={(e) => setDate(e.target.value)} />
        </label>
        <label>Category
          <select value={categoryId} required onChange={(e) => setCategoryId(e.target.value)}>
            <option value="">— pick —</option>
            {activeCategories.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </label>
        <label>Description
          <input value={description} required maxLength={200}
            onChange={(e) => setDescription(e.target.value)} />
        </label>
        <label>Amount ({currency.code || "…"})
          <input type="number" min={(1 / 10 ** currency.minor).toFixed(currency.minor)}
            step="any" value={amount} required
            onChange={(e) => setAmount(e.target.value)} />
        </label>
        <label>Flock (optional)
          <select value={flockId} onChange={(e) => setFlockId(e.target.value)}>
            <option value="">— none —</option>
            {flocks.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}
          </select>
        </label>
        <label>Note (optional)
          <input value={note} maxLength={500} onChange={(e) => setNote(e.target.value)} />
        </label>
        <div className="actions">
          <button type="submit" disabled={busy || activeCategories.length === 0}>Record expense</button>
        </div>
      </form>
      {activeCategories.length === 0 && (
        <p className="muted">Add a category first — every expense needs one.</p>
      )}

      {/* An open dialog renders its own copy of the error. */}
      {error && !addingCategory && editing === null && <p className="error" role="alert">{error}</p>}
      {message && <p className="success" role="status">{message}</p>}

      <Dialog
        open={editing !== null}
        title={editing ? `Correct — ${editing.date}, ${editing.description}` : "Correct expense"}
        onClose={() => setEditing(null)}
        // A 409 rebinds this dialog to the server's newer row; the record
        // identity changing pulls focus back to the first field rather than
        // swapping the form out from under the user's cursor.
        focusKey={editing}
      >
        {editing && (
          <form className="form-grid" onSubmit={onSaveEdit}>
            <label>Date
              <input type="date" value={editDate} max={today} required
                onChange={(e) => setEditDate(e.target.value)} />
            </label>
            <label>Category
              <select value={editCategory} required onChange={(e) => setEditCategory(e.target.value)}>
                {editCategories.map((c) => (
                  <option key={c.id} value={c.id}>{c.name}{c.active ? "" : " (deactivated)"}</option>
                ))}
              </select>
            </label>
            <label>Description
              <input value={editDescription} required maxLength={200}
                onChange={(e) => setEditDescription(e.target.value)} />
            </label>
            <label>Amount ({editing.currencyCode})
              <input type="number"
                min={(1 / 10 ** editing.currencyMinorUnit).toFixed(editing.currencyMinorUnit)}
                step="any" value={editAmount} required
                onChange={(e) => setEditAmount(e.target.value)} />
            </label>
            <label>Flock (optional)
              <select value={editFlock} onChange={(e) => setEditFlock(e.target.value)}>
                <option value="">— none —</option>
                {flocks.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}
              </select>
            </label>
            <label>Note (optional)
              <input value={editNote} maxLength={500} onChange={(e) => setEditNote(e.target.value)} />
            </label>
            {/* The 409 rebind reports through here, so the conflict banner stays
                next to the form it is telling you to re-apply. */}
            {error && <p className="error" role="alert">{error}</p>}
            <div className="dialog-foot">
              <button type="button" className="link" disabled={busy}
                onClick={() => setEditing(null)}>Cancel</button>
              <button type="submit" disabled={busy}>Save correction</button>
            </div>
          </form>
        )}
      </Dialog>

      {items === null ? (
        <p className="muted">Loading…</p>
      ) : items.length === 0 ? (
        <p className="muted">No expenses for this month.</p>
      ) : (
        <table className="data">
          <thead>
            <tr>
              <th>Date</th><th>Category</th><th>Description</th><th>Amount</th>
              <th>Flock</th><th>Note</th><th></th>
            </tr>
          </thead>
          <tbody>
            {items.map((x) => (
              <tr key={x.id}>
                <td>{x.date}</td>
                <td>{categoryName(x.expenseCategoryId)}</td>
                <td>{x.description}</td>
                <td>{formatMoney(x.amountMinorUnits, x.currencyCode, x.currencyMinorUnit)}</td>
                <td>{flockName(x.flockId)}</td>
                <td>{x.note ?? "—"}</td>
                <td>
                  <button className="link" disabled={busy} onClick={() => startEdit(x)}>
                    correct
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {hasMore && (
        <button className="link" disabled={busy}
          onClick={() => void load(items?.length ?? 0).catch((err) => setError(errText(err)))}>
          load more
        </button>
      )}
    </section>
  );
}
