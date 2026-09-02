import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import {
  adjustExpense, createExpense, createExpenseCategory, getExpense,
  listExpenseCategories, listExpenses, listFlocks, updateExpenseCategory,
} from "../api/cluckwork";
import type { Expense, ExpenseCategory, Flock } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useFormat } from "../farm/useFormat";
import { FarmDate } from "../components/FarmDate";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { FlockPicker } from "../components/FlockPicker";
import type { PickerSnapshot } from "../components/NamedEntityPicker";
import { DialogError } from "../components/DialogError";
import { ProvenanceCell } from "../components/ProvenanceCell";
import { useDialogErrors } from "../components/useDialogErrors";
import { usePagedList } from "../components/usePagedList";
import { usePendingAction } from "../components/usePendingAction";
import { useFarm, useFarmToday } from "../farm/useFarm";
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
  const fmt = useFormat();
  const { t: tc } = useTranslation("common");

  // Farm-local, not browser-local: since #35 the API judges "is this date in
  // the future?" against the FARM's day, so the pickers must agree (#123).
  const today = useFarmToday();
  const { farm } = useFarm();
  const [categories, setCategories] = useState<ExpenseCategory[]>([]);
  const [flocks, setFlocks] = useState<Flock[]>([]);
  // #479 — one slot per PLACE a message can appear. The record-expense form
  // sits on the page (not in a dialog), same as the mount read and the
  // category-toggle writes; "add-category" and each correction dialog get
  // their own.
  const errors = useDialogErrors();
  const setPageError = errors.setPage;
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
  // #512 (T028/T038) — the optional flock is committed through FlockPicker.
  // A blank (account-wide) selection is VALID: the picker's `canSubmit` is
  // the write guard (exploring/unavailable blocks it), never "must have a
  // flock". `addFlock` mirrors the engine's committed entity via onCommit and
  // is reset explicitly on success (the page never syncs it back — no
  // controlled generation — so the picker's own discovery lifecycle is never
  // disturbed).
  const [addFlock, setAddFlock] = useState<Flock | null>(null);
  // Bumped after the post-success reset so a later Escape cannot resurrect
  // the just-saved flock (engine controlled-sync, US2).
  const [addFlockGen, setAddFlockGen] = useState(0);
  // The INITIAL snapshot is the page's own honest state: no committed entity
  // and no exact read issued yet, so the write is withheld (canSubmit false)
  // until the picker's real snapshot lands. The picker is OPTIONAL, so once
  // it initializes the blank selection submits (a valid account-wide choice).
  const [addFlockSnapshot, setAddFlockSnapshot] = useState<PickerSnapshot<Flock>>({
    committed: null, selectionPhase: "uninitialized", exploring: false, canSubmit: false,
  });
  const [addFlockPickerOpen, setAddFlockPickerOpen] = useState(false);
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
  // #512 (T028/T038) — the correction's flock is a ROW-OWNED identity: the
  // picker's `requestedId` resolves it exactly (including archived flocks
  // outside the discovery window), a failed exact read enters the explicit
  // `unavailable` state (never a first-result substitution), and
  // `editFlockSnapshot.canSubmit` gates BOTH the Save button and onSaveEdit.
  // `editFlockEntity` is the FULL entity (committed from the mount list, from
  // the exact GET, or from a user pick); `editFlockId` holds only an id that
  // the picker has not resolved yet. A blank row owns neither (account-wide).
  const [editFlockEntity, setEditFlockEntity] = useState<Flock | null>(null);
  // The row-owned id while it is unresolved (archived / outside the window);
  // null once committed, cleared, or when the row owns no flock.
  const [editFlockId, setEditFlockId] = useState<string | null>(null);
  // The row-owned id that startEdit handed over — the page-side mirror of
  // `editFlockId`, frozen at open time: it keeps the engine's requestedId
  // effect pinned to that exact identity even if the user's CLEAR commits a
  // different flock in the meantime (the engine resolves the REQUESTED id,
  // never the current selection).
  const [editRequestedId, setEditRequestedId] = useState<string | null>(null);
  const [editFlockGen, setEditFlockGen] = useState(0);
  // HONEST initial state: a blank row's picker needs no exact read, so its
  // optional blank is safe to save from the first render — canSubmit true.
  // A row OWNING an id is withheld (canSubmit false) until its exact read
  // commits or reports unavailable.
  const [editFlockSnapshot, setEditFlockSnapshot] = useState<PickerSnapshot<Flock>>({
    committed: null, selectionPhase: "uninitialized", exploring: false, canSubmit: true,
  });
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
  // #469 — this list had no request sequencing, on the screen where it hurts
  // most: a failed month change used to leave the PREVIOUS month's rows and
  // total under the new month's picker, reading as a legitimate figure for a
  // period it never described. The total rides as page metadata so it is
  // ticket-protected exactly like the rows and cleared with them.
  const expenses = usePagedList<Expense, { total: number; code: string; minor: number }>({
    fetchPage: useCallback(async (offset: number, limit: number) => {
      const { from, to } = monthRange(month);
      const list = await listExpenses({
        from, to, categoryId: filterCategory || undefined, limit, offset,
      });
      return {
        items: list.items,
        meta: {
          total: list.totalMinorUnits,
          code: list.currencyCode,
          minor: list.currencyMinorUnit,
        },
      };
    }, [month, filterCategory, monthRange]),
    pageSize: PAGE,
  });
  // MONEY SCALE — never guessed, always the freshest authority available.
  // Three review rounds landed on this order, each for its own failure:
  //
  //   1. the CURRENT list response, because its envelope carries the
  //      account's live currency (the endpoint reads the account per
  //      request) — while `farm` is only the snapshot this tab booted with,
  //      so a currency changed elsewhere reaches this screen through the
  //      list first and the snapshot would convert at a retired scale;
  //   2. the last list scale seen, retained across a failed load so a blip
  //      cannot un-know a scale that was already established;
  //   3. the farm snapshot, for the case where no list has ever landed;
  //   4. nothing — and then the form REFUSES to record (see scaleKnown),
  //      because denominating a typed amount at an assumed two decimals is
  //      how a 3-decimal farm stores 1.000 as 100 minor units.
  const lastListScale = useRef<{ code: string; minor: number } | null>(null);
  if (expenses.meta !== null) {
    lastListScale.current = { code: expenses.meta.code, minor: expenses.meta.minor };
  }
  const currency = expenses.meta !== null
    ? { code: expenses.meta.code, minor: expenses.meta.minor }
    // Retained across a failed load, so a blip cannot un-know the scale.
    : lastListScale.current
      ?? (farm !== null
        ? { code: farm.currencyCode, minor: farm.currencyMinorUnit }
        : null);
  const scaleKnown = currency !== null;
  // Display-only fallbacks; nothing below CONVERTS with these.
  const currencyCode = currency?.code ?? "";
  const currencyMinor = currency?.minor ?? 2;

  useEffect(() => {
    Promise.all([
      listExpenseCategories({ includeInactive: true }),
      listFlocks({ includeArchived: true, limit: 500 }),
    ])
      .then(([c, f]) => {
        setCategories(c);
        setFlocks(f);
      })
      .catch((err) => setPageError(errText(err)));
  }, [setPageError]);


  const categoryName = (id: string) =>
    categories.find((c) => c.id === id)?.name ?? id.slice(0, 8);
  // #512 US4 (T049/T051) — the row renders the flock name its OWN record
  // carries (the endpoint's one scoped bulk read per page), never the
  // picker's capped discovery results and never an id fragment
  // (contracts/http-api.md: "Required names are never replaced with
  // identifier fragments"). `flockId === null` is the deliberate
  // account-wide expense; a non-null `flockId` with a null `flockName` is
  // the defensive out-of-scope case.
  const rowFlockName = (x: Expense) =>
    x.flockId === null ? "—" : (x.flockName ?? t("flockUnavailable"));
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

  // `dialogScope` names the DIALOG this attempt's failure belongs to — `null`
  // for the record-expense form and the category-toggle writes, neither of
  // which is behind a dialog.
  async function run(scope: string, dialogScope: string | null, fn: () => Promise<void>) {
    // A skipped run (another flight already open) simply does nothing — no
    // caller here branches on the outcome, so there is no boolean to map.
    await runPending(scope, async () => {
      errors.beginAttempt(dialogScope);
      setMessage(null);
      try {
        await fn();
        settleKey(scope);
      } catch (err) {
        settleKey(scope, err);
        errors.report(dialogScope, errText(err));
      }
    });
  }

  function onAdd(e: FormEvent) {
    e.preventDefault();
    // #512 (T028) — canSubmit gates the write even though the selection is
    // OPTIONAL: an exploring/uninitialized picker must not submit a stale or
    // not-yet-committed flock even if the control is bypassed. A valid blank
    // selection still submits (canSubmit is true for it).
    if (busy || !addFlockSnapshot.canSubmit) return;
    void run("add", null, async () => {
      // runWrite claims the list's ticket before the POST, so a month or
      // category change made while it is in flight keeps the view (#469).
      await expenses.runWrite(async () => {
        await createExpense({
          expenseCategoryId: categoryId,
          date,
          description: description.trim(),
          // Guarded by the disabled submit below; asserted here because this
          // is the line that turns a typed string into stored money.
          amountMinorUnits: toMinorUnits(amount, currency!.minor),
          // #512 (T028) — the picker's committed entity IS the flock: a blank
          // (account-wide) selection stays null.
          flockId: addFlock?.id ?? null,
          note: note.trim() || null,
        }, keyFor("add"));
        // Reset BEFORE the refresh: if the reload fails after the write
        // landed, a still-populated form invites a duplicate re-submit under
        // a fresh key (codex review of #88).
        setDescription("");
        setAmount("");
        setNote("");
        // The add form's optional flock resets to blank (account-wide); the
        // bumped gen re-syncs the engine so a later Escape cannot resurrect
        // the just-saved flock.
        setAddFlock(null);
        setAddFlockGen((g) => g + 1);
      });
      setMessage(i18n.t("expenses:expenseRecordedMessage"));
    });
  }

  // A switch straight from one bound expense to another, with no Cancel or
  // close in between, abandons the one being displaced, so its stale verdict
  // cannot resurface next time THAT expense is reopened. The backdrop stops a
  // mouse from reaching the row buttons underneath, so this is not the common
  // path — but #480 established that it does not stop a screen reader's
  // virtual cursor, which is the same reason the per-dialog map exists at all.
  // The 409 rebind below also calls this, on the
  // SAME id, so this must not fire there — abandoning would mute the very
  // report the rebind is about to make.
  function startEdit(x: Expense) {
    if (editing !== null && editing.id !== x.id) errors.abandon(`edit:${editing.id}`);
    setEditing(x);
    setEditDate(x.date);
    setEditCategory(x.expenseCategoryId);
    setEditDescription(x.description);
    setEditAmount((x.amountMinorUnits / 10 ** x.currencyMinorUnit).toFixed(x.currencyMinorUnit));
    // #512 (T038) — the row-owned flock is committed EXACT, including an
    // Archived row whose id is absent from the discovery window; a flock the
    // mount read never listed resolves through the picker's exact GET.
    const owned = x.flockId !== null
      ? flocks.find((f) => f.id === x.flockId)
      : undefined;
    // #512 (T038) — the mount read (listFlocks limit 500) may not have landed
    // yet when a correction is opened: an id it would have contained must
    // still resolve EXACTLY through the picker's exact GET, never from a list
    // that is still empty.
    const listSettled = flocks.length > 0;
    if (owned && listSettled) {
      // Already a full entity from the page's own mount list: admitted via
      // controlledCommitted, as-is. `editRequestedId` MUST be null here — the
      // picker's requestedId effect fires independently of the controlled
      // sync (same render, same generation bump), and a stale non-null id
      // would issue a spurious exact GET for data the page already has.
      setEditRequestedId(null);
      setEditFlockEntity(owned);
      setEditFlockId(null);
      setEditFlockGen((g) => g + 1);
      // Committed from the mount list: safe to save — no exact read is owed.
      setEditFlockSnapshot({ committed: owned, selectionPhase: "committed", exploring: false, canSubmit: true });
    } else {
      // No full entity (archived / outside the window): hand the id to the
      // picker's requestedId effect — it resolves exactly (or enters
      // `unavailable`) — and withhold the write until it lands. A blank row
      // owns no id: nothing is in flight and the optional blank is safe to
      // save from the first render.
      setEditRequestedId(x.flockId);
      setEditFlockEntity(null);
      setEditFlockId(x.flockId);
      setEditFlockGen((g) => g + 1);
      setEditFlockSnapshot({
        committed: null,
        selectionPhase: x.flockId === null ? "blank" : "uninitialized",
        exploring: false,
        canSubmit: x.flockId === null,
      });
    }
    setEditNote(x.note ?? "");
    setMessage(null);
    // F131: the correction form is a dialog now — it takes focus itself, so
    // there is nothing to scroll to.
  }

  function closeEdit() {
    if (editing !== null) errors.abandon(`edit:${editing.id}`);
    setEditing(null);
    setEditFlockEntity(null);
    setEditFlockId(null);
    setEditRequestedId(null);
    setEditFlockGen((g) => g + 1);
    setEditFlockSnapshot({ committed: null, selectionPhase: "uninitialized", exploring: false, canSubmit: true });
  }

  function closeAddCategory() {
    setAddingCategory(false);
    errors.abandon("add-category");
  }

  function onSaveEdit(e: FormEvent) {
    e.preventDefault();
    if (editing === null) return;
    // #512 (T028) — the picker's canSubmit is the write guard, not the button's
    // disabled attribute: an exploring/unavailable/uninitialized picker must
    // not submit the row's stale flock even if the control is bypassed. A
    // valid blank (account-wide) selection still saves (canSubmit true for
    // optional blank), so the guard never blocks a legal edit.
    if (busy || !editFlockSnapshot.canSubmit) return;
    const target = editing;
    const scope = `edit:${target.id}`;
    void run(scope, scope, async () => {
      try {
        // The refresh that follows replaces the row wholesale, so the old
        // optimistic splice into `items` is gone with the local list state.
        await expenses.runWrite(async () => {
          await adjustExpense(target.id, {
            version: target.version,
            expenseCategoryId: editCategory,
            date: editDate,
            description: editDescription.trim(),
            amountMinorUnits: toMinorUnits(editAmount, target.currencyMinorUnit),
            // #512 (T028) — exact row-owned identity (possibly archived / out of
            // the discovery window); a blank stays null (account-wide).
            flockId: editFlockEntity?.id ?? null,
            note: editNote.trim() || null,
          }, keyFor(scope));
          setEditing(null);
        });
        setMessage(i18n.t("expenses:expenseCorrectedMessage"));
      } catch (err) {
        // 409: someone else corrected it meanwhile — rebind the panel to the
        // fresh row (only unsent typing is lost, and the banner says why).
        // Fetched by id, not by date: the winning correction may have MOVED
        // the expense to another day (pi review of #88).
        if (err instanceof ApiError && err.status === 409) {
          settleKey(scope, err);
          // No reload of our own: runWrite already re-read the loaded WINDOW
          // before rethrowing. A second read here is page-one only, so for a
          // user who had paged deeper it collapses the window that refresh
          // just restored — and clears it outright if it fails (#469).
          startEdit(await getExpense(target.id));
          // startEdit may have just REOPENED a dialog the user dismissed while
          // this GET was out, and that dismissal muted this scope — so the
          // message below would be dropped and the panel would reappear with
          // the winner's values and no word of why (codex on #491). A forced
          // reopen is a new session, so un-mute it. Same id as the scope `run`
          // reports on, so this re-enables that report and nothing else.
          errors.beginAttempt(scope);
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
    void run(scope, "add-category", async () => {
      await createExpenseCategory({ name: newCategoryName.trim() }, keyFor(scope));
      setNewCategoryName("");
      setAddingCategory(false);
      setMessage(i18n.t("expenses:categoryCreatedMessage"));
      // The dialog closed two lines ago, so its slot renders NOWHERE from here
      // on: a refresh failure reported to it would leave the user with a stale
      // category list and no message at all (codex on #491). The write already
      // succeeded, so this is the screen's problem now, not the form's.
      try {
        setCategories(await listExpenseCategories({ includeInactive: true }));
      } catch (err) {
        errors.setPage(errText(err));
      }
    });
  }

  function onToggleCategory(c: ExpenseCategory) {
    const scope = `toggle-category:${c.id}`;
    void run(scope, null, async () => {
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

      {/* The total belongs to the rows below it: it lands and clears with
          them, so it can never describe a period they do not (#469). It is
          also WITHHELD while a replacement is in flight — the hook keeps the
          previous window until the new one lands, and a figure from last
          month sitting under this month's picker is the very thing this
          change exists to stop, pending or settled (codex review). */}
      {/* ...and only when there IS an authoritative figure. A failed load
          clears the metadata, and `?? 0` then rendered a definitive
          "Month total: 0.00" beside the error — stating that a period whose
          spend is UNKNOWN is zero, which on a money screen is a wrong number
          rather than a degraded display (codex review). */}
      {!expenses.reloading && expenses.meta !== null && (
        <p><strong>{t("monthTotalLabel", {
          amount: fmt.money(expenses.meta.total, currencyCode, currencyMinor),
        })}</strong></p>
      )}

      {showCategories && (
        <div className="order-panel">
          <h3>{t("categoriesHeading")}</h3>
          <div className="panel-actions">
            <button type="button" onClick={() => setAddingCategory(true)}>
              {t("newCategoryButton")}
            </button>
          </div>

          <Dialog open={addingCategory} title={t("newCategoryDialogTitle")} onClose={closeAddCategory}>
            <form className="inline-form" onSubmit={onAddCategory}>
              {/* Disabled during any flight: the create scope is derived from
                  this name (addCategoryScope), so editing it mid-flight would
                  re-point isPending at a scope nobody is running and drop the
                  spinner while the request is still open (#242 review). */}
              <label>{t("categoryNameLabel")}
                <input value={newCategoryName} required disabled={busy}
                  onChange={(e) => setNewCategoryName(e.target.value)} />
              </label>
              <DialogError errors={errors} scope="add-category" />
              <div className="dialog-foot">
                <button type="button" className="link" onClick={closeAddCategory}>{tc("cancel")}</button>
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
        <label>{t("amountLabel", { code: currencyCode || "…" })}
          <input type="number" min={(1 / 10 ** currencyMinor).toFixed(currencyMinor)}
            step="any" value={amount} required
            onChange={(e) => setAmount(e.target.value)} />
        </label>
        <FlockPicker
          label={t("flockOptionalLabel")}
          eligibility="all"
          required={false}
          open={addFlockPickerOpen}
          // Controlled sync only for the post-success reset (gen bump): the
          // engine owns its discovery lifecycle otherwise, so a later Escape
          // cannot resurrect the just-saved flock (US2).
          controlledCommitted={addFlock}
          controlledGeneration={addFlockGen}
          onSnapshot={setAddFlockSnapshot}
          onCommit={(f) => {
            setAddFlock(f);
            setAddFlockPickerOpen(false);
          }}
          onClear={() => setAddFlock(null)}
          onEscape={() => setAddFlockPickerOpen(false)}
          onOutsideClick={() => setAddFlockPickerOpen(false)}
          trigger={
            <button type="button" className="named-picker-trigger"
              onClick={() => setAddFlockPickerOpen(true)}>
              {addFlock ? addFlock.name : t("noneOption")}
            </button>
          }
        />
        <label>{t("noteOptionalLabel")}
          <input value={note} maxLength={500} onChange={(e) => setNote(e.target.value)} />
        </label>
        <div className="actions">
          {/* No known denomination means no recording: converting the typed
              amount would have to guess the scale (#469 codex review).
              #512 (T028): the picker's canSubmit gates the write too — an
              exploring/uninitialized picker must not submit a stale flock. */}
          <BusyButton type="submit" busy={isPending("add")}
            disabled={busy || activeCategories.length === 0 || !scaleKnown || !addFlockSnapshot.canSubmit}>
            {t("recordExpenseButton")}
          </BusyButton>
        </div>
      </form>
      {activeCategories.length === 0 && (
        <p className="muted">{t("addCategoryFirstMessage")}</p>
      )}

      {/* Unconditional since #479: this slot is the page's alone now, so there
          is nothing a dialog's own message could double up with. */}
      {errors.page && <p className="error" role="alert">{errors.page}</p>}
      {message && <p className="success" role="status">{message}</p>}

      <Dialog
        open={editing !== null}
        title={editing
          ? t("correctExpenseDialogTitleWithExpense", { date: editing.date, description: editing.description })
          : t("correctExpenseDialogTitle")}
        onClose={closeEdit}
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
            {/* #512 (T038) — the correction's flock is a ROW-OWNED identity:
                requestedId resolves it exactly (archived / outside the
                discovery window included), a failed exact read enters the
                explicit unavailable state with a Retry, and the picker's
                clear restores the account-wide (blank) choice. */}
            <FlockPicker
              label={t("flockOptionalLabel")}
              eligibility="all"
              required={false}
              // #512 (T038) — the picker's discovery is OPEN-DRIVEN: the engine
              // discovers only while `open`, and this dialog's picker never
              // toggles its own open state (the dialog owns focus and
              // dismissal). So it rides the dialog: while the dialog is up the
              // picker is live and its requestedId effect can resolve the
              // row-owned id (or report it unavailable); on close the dialog
              // unmounts the form, so nothing lingers.
              open={true}
              controlledCommitted={editFlockEntity}
              controlledGeneration={editFlockGen}
              requestedId={editRequestedId}
              onSnapshot={(snap) => {
                setEditFlockSnapshot(snap);
                if (snap.committed) {
                  setEditFlockEntity(snap.committed);
                  setEditFlockId(null);
                }
              }}
              onCommit={(f) => {
                setEditFlockEntity(f);
                setEditFlockId(null);
                setEditFlockGen((g) => g + 1);
              }}
              onClear={() => {
                setEditFlockEntity(null);
                setEditFlockId(null);
                setEditFlockGen((g) => g + 1);
              }}
              onEscape={() => {}}
              onOutsideClick={() => {}}
              trigger={
                <span className="named-picker-trigger">{editFlockEntity
                    ? editFlockEntity.name
                    : editFlockId !== null && editFlockSnapshot.selectionPhase === "unavailable"
                      ? t("flockUnavailable")
                      : t("noneOption")}</span>
              }
            />
            <label>{t("noteOptionalLabel")}
              <input value={editNote} maxLength={500} onChange={(e) => setEditNote(e.target.value)} />
            </label>
            {/* The 409 rebind reports through here, so the conflict banner stays
                next to the form it is telling you to re-apply. */}
            <DialogError errors={errors} scope={`edit:${editing.id}`} />
            <div className="dialog-foot">
              <button type="button" className="link" disabled={busy}
                onClick={closeEdit}>{tc("cancel")}</button>
              {/* #512 (T028): canSubmit also gates the visible control; the
                  handler guard above is the real boundary. */}
              <BusyButton type="submit" busy={isPending(`edit:${editing.id}`)}
                disabled={busy || !editFlockSnapshot.canSubmit}>
                {t("saveCorrectionButton")}
              </BusyButton>
            </div>
          </form>
        )}
      </Dialog>

      {expenses.error && <p className="error" role="alert">{expenses.error}</p>}

      {expenses.rows === null || expenses.reloading ? (
        <p className="muted">{tc("loading")}</p>
      ) : expenses.rows.length === 0 ? (
        <p className="muted">{t("noExpensesMessage")}</p>
      ) : (
        <table className="data">
          <thead>
            <tr>
              <th>{t("dateHeader")}</th><th>{t("categoryHeader")}</th><th>{t("descriptionHeader")}</th><th className="num">{t("amountHeader")}</th>
              <th>{t("flockHeader")}</th><th>{t("noteHeader")}</th>
              <th>{tc("recordHistoryHeader")}</th><th></th>
            </tr>
          </thead>
          <tbody>
            {expenses.rows.map((x) => (
              <tr key={x.id}>
                <td className="nowrap"><FarmDate iso={x.date} /></td>
                <td>{categoryName(x.expenseCategoryId)}</td>
                <td>{x.description}</td>
                <td className="num">{fmt.money(x.amountMinorUnits, x.currencyCode, x.currencyMinorUnit)}</td>
                <td>{rowFlockName(x)}</td>
                <td>{x.note ?? "—"}</td>
                <ProvenanceCell history={x} />
                <td>
                  {/* #493 — full audit trail for this record, distinct from
                      the created/last-changed summary in ProvenanceCell. */}
                  <Link className="link" to={`/audit?entityId=${x.id}`}>
                    {tc("recordHistory.viewHistoryLink")}
                  </Link>
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
      {expenses.canLoadMore && (
        <button className="link" disabled={busy}
          onClick={() => void expenses.loadMore()}>
          {t("loadMoreButton")}
        </button>
      )}
    </section>
  );
}
