import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import {
  adjustExpense, createExpense, createExpenseCategory, formatMoney, getExpense,
  listExpenseCategories, listExpenses, listFlocks, updateExpenseCategory,
} from "../api/cluckwork";
import type { Expense, ExpenseCategory, Flock } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { usePendingAction } from "../components/usePendingAction";
import { useFarmToday } from "../farm/useFarm";
import { newId } from "../lib/ids";
import i18n from "../i18n";

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

const PAGE = 100;

// #87 — basic expenses (spec §16 cut): categories + recording + monthly view.
// Admin-only end to end: the route hides for workers and every endpoint
// carries the Admin policy — money data, unlike the production screens.
export function ExpensesPage() {
  const { t } = useTranslation("expenses");
  const { t: tc } = useTranslation("common");

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
  // #236: the flight guard + per-scope spinner state live in the shared hook;
  // this screen keeps only its idempotency-key discipline below.
  const { busy, isPending, run: runPending } = usePendingAction();

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
    if (!m) throw new Error(i18n.t("expenses:enterValidAmount"));
    const frac = m[2] ?? "";
    if (frac.length > minor)
      throw new Error(minor === 0
        ? i18n.t("expenses:noDecimalPlaces")
        : i18n.t("expenses:atMostDecimals", { count: minor }));
    const v = Number(m[1]) * 10 ** minor + Number(frac.padEnd(minor, "0") || "0");
    if (!Number.isSafeInteger(v) || v <= 0) throw new Error(i18n.t("expenses:enterAmountGreaterThanZero"));
    return v;
  };

  async function run(scope: string, fn: () => Promise<void>) {
    // A skipped run (another flight already open) simply does nothing — no
    // caller here branches on the outcome, so there is no boolean to map.
    await runPending(scope, async () => {
      setError(null);
      setMessage(null);
      try {
        await fn();
        settleKey(scope);
      } catch (err) {
        settleKey(scope, err);
        setError(errText(err));
      }
    });
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
      setMessage(i18n.t("expenses:expenseRecordedMessage"));
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
        setMessage(i18n.t("expenses:expenseCorrectedMessage"));
      } catch (err) {
        // 409: someone else corrected it meanwhile — rebind the panel to the
        // fresh row (only unsent typing is lost, and the banner says why).
        // Fetched by id, not by date: the winning correction may have MOVED
        // the expense to another day (pi review of #88).
        if (err instanceof ApiError && err.status === 409) {
          settleKey(scope, err);
          await load();
          startEdit(await getExpense(target.id));
          throw new Error(i18n.t("expenses:conflictRebindMessage"));
        }
        throw err;
      }
    });
  }

  // The category-create scope is derived from the typed name (it keys the
  // idempotent write); computed once per render so the handler and the submit
  // button's isPending() can never disagree on the string.
  const addCategoryScope = `add-category:${newCategoryName.trim().toLowerCase()}`;

  function onAddCategory(e: FormEvent) {
    e.preventDefault();
    const scope = addCategoryScope;
    void run(scope, async () => {
      await createExpenseCategory({ name: newCategoryName.trim() }, keyFor(scope));
      setNewCategoryName("");
      setAddingCategory(false);
      setCategories(await listExpenseCategories({ includeInactive: true }));
      setMessage(i18n.t("expenses:categoryCreatedMessage"));
    });
  }

  function onToggleCategory(c: ExpenseCategory) {
    const scope = `toggle-category:${c.id}`;
    void run(scope, async () => {
      await updateExpenseCategory(c.id, { name: c.name, active: !c.active }, keyFor(scope));
      setCategories(await listExpenseCategories({ includeInactive: true }));
      setMessage(c.active
        ? i18n.t("expenses:categoryDeactivatedMessage", { name: c.name })
        : i18n.t("expenses:categoryReactivatedMessage", { name: c.name }));
    });
  }

  return (
    <section>
      <h2>{t("title")}</h2>

      <div className="filters">
        <label>{t("monthLabel")}
          <input type="month" value={month} max={today.slice(0, 7)}
            onChange={(e) => setMonth(e.target.value)} />
        </label>
        <label>{t("categoryLabel")}
          <select value={filterCategory} onChange={(e) => setFilterCategory(e.target.value)}>
            <option value="">{t("allCategoriesOption")}</option>
            {categories.map((c) => (
              <option key={c.id} value={c.id}>{c.name}{c.active ? "" : t("deactivatedSuffix")}</option>
            ))}
          </select>
        </label>
        <button className="link" type="button" onClick={() => setShowCategories((v) => !v)}>
          {showCategories ? t("hideCategoriesButton") : t("manageCategoriesButton")}
        </button>
      </div>

      <p><strong>{t("monthTotalLabel", { amount: formatMoney(total, currency.code, currency.minor) })}</strong></p>

      {showCategories && (
        <div className="order-panel">
          <h3>{t("categoriesHeading")}</h3>
          <div className="panel-actions">
            <button type="button" onClick={() => { setError(null); setAddingCategory(true); }}>
              {t("newCategoryButton")}
            </button>
          </div>

          <Dialog open={addingCategory} title={t("newCategoryDialogTitle")} onClose={() => setAddingCategory(false)}>
            <form className="inline-form" onSubmit={onAddCategory}>
              {/* Disabled during any flight: the create scope is derived from
                  this name (addCategoryScope), so editing it mid-flight would
                  re-point isPending at a scope nobody is running and drop the
                  spinner while the request is still open (#242 review). */}
              <label>{t("categoryNameLabel")}
                <input value={newCategoryName} required disabled={busy}
                  onChange={(e) => setNewCategoryName(e.target.value)} />
              </label>
              {error && <p className="error">{error}</p>}
              <div className="dialog-foot">
                <button type="button" className="link" onClick={() => setAddingCategory(false)}>{tc("cancel")}</button>
                <BusyButton type="submit" busy={isPending(addCategoryScope)} disabled={busy}>{t("addCategoryButton")}</BusyButton>
              </div>
            </form>
          </Dialog>

          <ul>
            {categories.map((c) => (
              <li key={c.id}>
                {c.name}{c.active ? "" : t("deactivatedSuffix")}{" "}
                <BusyButton className="link" type="button" busy={isPending(`toggle-category:${c.id}`)}
                  disabled={busy} onClick={() => onToggleCategory(c)}>
                  {c.active ? t("deactivateButton") : t("reactivateButton")}
                </BusyButton>
              </li>
            ))}
            {categories.length === 0 && <li className="muted">{t("noCategoriesMessage")}</li>}
          </ul>
        </div>
      )}

      <h3>{t("recordExpenseHeading")}</h3>
      <form className="form-grid" onSubmit={onAdd}>
        <label>{t("dateLabel")}
          <input type="date" value={date} max={today} required
            onChange={(e) => setDate(e.target.value)} />
        </label>
        <label>{t("categoryLabel")}
          <select value={categoryId} required onChange={(e) => setCategoryId(e.target.value)}>
            <option value="">{t("pickOption")}</option>
            {activeCategories.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </label>
        <label>{t("descriptionLabel")}
          <input value={description} required maxLength={200}
            onChange={(e) => setDescription(e.target.value)} />
        </label>
        <label>{t("amountLabel", { code: currency.code || "…" })}
          <input type="number" min={(1 / 10 ** currency.minor).toFixed(currency.minor)}
            step="any" value={amount} required
            onChange={(e) => setAmount(e.target.value)} />
        </label>
        <label>{t("flockOptionalLabel")}
          <select value={flockId} onChange={(e) => setFlockId(e.target.value)}>
            <option value="">{t("noneOption")}</option>
            {flocks.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}
          </select>
        </label>
        <label>{t("noteOptionalLabel")}
          <input value={note} maxLength={500} onChange={(e) => setNote(e.target.value)} />
        </label>
        <div className="actions">
          <BusyButton type="submit" busy={isPending("add")} disabled={busy || activeCategories.length === 0}>
            {t("recordExpenseButton")}
          </BusyButton>
        </div>
      </form>
      {activeCategories.length === 0 && (
        <p className="muted">{t("addCategoryFirstMessage")}</p>
      )}

      {/* An open dialog renders its own copy of the error. */}
      {error && !addingCategory && editing === null && <p className="error" role="alert">{error}</p>}
      {message && <p className="success" role="status">{message}</p>}

      <Dialog
        open={editing !== null}
        title={editing
          ? t("correctExpenseDialogTitleWithExpense", { date: editing.date, description: editing.description })
          : t("correctExpenseDialogTitle")}
        onClose={() => setEditing(null)}
        // A 409 rebinds this dialog to the server's newer row; the record
        // identity changing pulls focus back to the first field rather than
        // swapping the form out from under the user's cursor.
        focusKey={editing}
      >
        {editing && (
          <form className="form-grid" onSubmit={onSaveEdit}>
            <label>{t("dateLabel")}
              <input type="date" value={editDate} max={today} required
                onChange={(e) => setEditDate(e.target.value)} />
            </label>
            <label>{t("categoryLabel")}
              <select value={editCategory} required onChange={(e) => setEditCategory(e.target.value)}>
                {editCategories.map((c) => (
                  <option key={c.id} value={c.id}>{c.name}{c.active ? "" : t("deactivatedSuffix")}</option>
                ))}
              </select>
            </label>
            <label>{t("descriptionLabel")}
              <input value={editDescription} required maxLength={200}
                onChange={(e) => setEditDescription(e.target.value)} />
            </label>
            <label>{t("amountLabel", { code: editing.currencyCode })}
              <input type="number"
                min={(1 / 10 ** editing.currencyMinorUnit).toFixed(editing.currencyMinorUnit)}
                step="any" value={editAmount} required
                onChange={(e) => setEditAmount(e.target.value)} />
            </label>
            <label>{t("flockOptionalLabel")}
              <select value={editFlock} onChange={(e) => setEditFlock(e.target.value)}>
                <option value="">{t("noneOption")}</option>
                {flocks.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}
              </select>
            </label>
            <label>{t("noteOptionalLabel")}
              <input value={editNote} maxLength={500} onChange={(e) => setEditNote(e.target.value)} />
            </label>
            {/* The 409 rebind reports through here, so the conflict banner stays
                next to the form it is telling you to re-apply. */}
            {error && <p className="error" role="alert">{error}</p>}
            <div className="dialog-foot">
              <button type="button" className="link" disabled={busy}
                onClick={() => setEditing(null)}>{tc("cancel")}</button>
              <BusyButton type="submit" busy={isPending(`edit:${editing.id}`)} disabled={busy}>
                {t("saveCorrectionButton")}
              </BusyButton>
            </div>
          </form>
        )}
      </Dialog>

      {items === null ? (
        <p className="muted">{tc("loading")}</p>
      ) : items.length === 0 ? (
        <p className="muted">{t("noExpensesMessage")}</p>
      ) : (
        <table className="data">
          <thead>
            <tr>
              <th>{t("dateHeader")}</th><th>{t("categoryHeader")}</th><th>{t("descriptionHeader")}</th><th>{t("amountHeader")}</th>
              <th>{t("flockHeader")}</th><th>{t("noteHeader")}</th><th></th>
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
                  {/* Opens the correction dialog — non-mutating, so the
                      spinner belongs to the dialog's Save, not here (#242). */}
                  <button className="link" disabled={busy}
                    onClick={() => startEdit(x)}>
                    {t("correctButton")}
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
          {t("loadMoreButton")}
        </button>
      )}
    </section>
  );
}
