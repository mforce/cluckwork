import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { FilterX, Plus, Receipt } from "lucide-react";
import {
  Box, Button, DialogActions, Divider, List, ListItem, ListItemText, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography,
} from "@mui/material";
import {
  adjustExpense, createExpense, createExpenseCategory, getExpense,
  listExpenseCategories, listExpenses, listFlocks, updateExpenseCategory,
} from "../api/cluckwork";
import type { Expense, ExpenseCategory, Flock } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useFormat } from "../farm/useFormat";
import { FarmDate } from "../components/FarmDate";
import { FieldConsole, ConsoleSubhead, CONSOLE_LINK_SX, CONSOLE_PAPER_HEAD_SX, LedgerTableContainer, ConsoleSummary, CONSOLE_PANEL_SX, CONSOLE_SPLIT_SX, CONSOLE_FORM_SX, CONSOLE_RAIL_SX } from "../components/FieldConsole";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { EmptyState } from "../components/EmptyState";
import { FilterBar, FilterDateField, FILTER_PICKER_SX } from "../components/FilterBar";
import { FlockPicker } from "../components/FlockPicker";
import type { PickerSnapshot } from "../components/NamedEntityPicker";
import { DialogError } from "../components/DialogError";
import { ProvenanceCell } from "../components/ProvenanceCell";
import { useDialogAction } from "../components/useDialogAction";
import { usePagedList } from "../components/usePagedList";
import { useFarm, useFarmToday } from "../farm/useFarm";
import { newId } from "../lib/ids";
import i18n from "../i18n";

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

const PAGE = 100;
const NOWRAP = { whiteSpace: "nowrap" as const };

// The scopes that own a dialog (#703). `run` routes a failure by this and gates
// a success by it; a scope outside the list — the record-expense form on the
// page, the category toggles in the panel — reports to the page and is never
// superseded.
const DIALOG_SCOPES = ["edit", "add-category"] as const;

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
  const [message, setMessage] = useState<string | null>(null);
  // #703 — the flight guard (#236), the per-place message slots (#479: the
  // record-expense form sits on the page, same as the mount read and the
  // category-toggle writes; "add-category" and the correction dialog get
  // their own) and the dialog-session generation (#477 part 2) come from one
  // shared hook; this screen keeps only its idempotency-key discipline below,
  // and says which scopes own a dialog. The page's message clears as each
  // attempt starts, exactly where the old wrapper cleared it.
  const { busy, isPending, errors, run, openDialog, dismissDialog } = useDialogAction(
    DIALOG_SCOPES,
    { onAttempt: () => setMessage(null) },
  );
  const setPageError = errors.setPage;

  // filters
  // #667 — a from/to pair, like every sibling list screen. It DEFAULTS to the
  // current farm month rather than opening blank as the siblings do, and that
  // divergence is deliberate: this screen shows a period TOTAL, so opening
  // blank would silently change the default view from "this month's spend" to
  // "every expense ever recorded, and a total to match". The user can widen or
  // clear it; the default preserves what shipped.
  const monthStart = `${today.slice(0, 7)}-01`;
  const monthEnd = (() => {
    const [y, mo] = today.slice(0, 7).split("-").map(Number);
    const last = new Date(Date.UTC(y, mo, 0)).getUTCDate();
    return `${today.slice(0, 7)}-${String(last).padStart(2, "0")}`;
  })();
  const [from, setFrom] = useState(monthStart);
  const [to, setTo] = useState(monthEnd);
  const [filterCategory, setFilterCategory] = useState("");

  // #679 — "clear" on this screen means RESTORE THE DEFAULT, not blank the
  // range (owner decision, 2026-09-05). Blanking would leave periodTotalLabel
  // describing an unbounded total across every expense ever recorded, which is
  // the framing #667 deliberately declined to make the default. All-time stays
  // reachable, but as its own labelled action in the empty state below rather
  // than as the meaning of this button.
  const isFiltered = from !== monthStart || to !== monthEnd || filterCategory !== "";
  const resetFilters = () => {
    setFrom(monthStart);
    setTo(monthEnd);
    setFilterCategory("");
  };
  const showAllTime = () => {
    setFrom("");
    setTo("");
    setFilterCategory("");
  };

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
  const [editFlockPickerOpen, setEditFlockPickerOpen] = useState(false);
  // #512 (T028/T038): resolve the row's flock exactly, including archived IDs.
  // An unavailable identity blocks Save; a blank account-wide choice is valid.
  const [editFlockEntity, setEditFlockEntity] = useState<Flock | null>(null);
  // The row-owned id while it is unresolved (archived / outside the window);
  // null once committed, cleared, or when the row owns no flock.
  const [editFlockId, setEditFlockId] = useState<string | null>(null);
  // #512: resolve the saved identity until a user replaces or clears it.
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

  // offset 0 replaces the page (fresh view after filters/mutations); a larger
  // offset appends — months can exceed one page and every row must stay
  // reachable for correction (codex review of #88). The total always covers
  // the WHOLE filtered period regardless of paging.
  // #469 — this list had no request sequencing, on the screen where it hurts
  // most: a failed filter change used to leave the PREVIOUS month's rows and
  // total under the new month's picker, reading as a legitimate figure for a
  // period it never described. The total rides as page metadata so it is
  // ticket-protected exactly like the rows and cleared with them.
  const expenses = usePagedList<Expense, { total: number; code: string; minor: number }>({
    fetchPage: useCallback(async (offset: number, limit: number) => {
      const list = await listExpenses({
        from: from || undefined,
        to: to || undefined,
        categoryId: filterCategory || undefined,
        limit,
        offset,
      });
      return {
        items: list.items,
        meta: {
          total: list.totalMinorUnits,
          code: list.currencyCode,
          minor: list.currencyMinorUnit,
        },
      };
      // INV-3 — every value the request body uses. #469's ticket discipline
      // keys the whole "previous window's total under the new window's control"
      // protection on this identity.
    }, [from, to, filterCategory]),
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

  // The key policy every write on this screen shares (see settleKey): a server
  // response — success or ApiError — spends the key, a transport failure keeps
  // it for an exact replay. It is a fact about the world, so it runs whether
  // or not the dialog that started the write is still on screen (#703 — the
  // superseded-safe rule). The flight guard, the failure routing and the
  // success gate are the hook's; this helper is called INSIDE `run`, and what
  // it rethrows lands in the slot `run` was given.
  async function commit(keyScope: string, fn: () => Promise<void>): Promise<void> {
    try {
      await fn();
    } catch (err) {
      settleKey(keyScope, err);
      throw err;
    }
    settleKey(keyScope);
  }

  function onAdd(e: FormEvent) {
    e.preventDefault();
    // #512 (T028) — canSubmit gates the write even though the selection is
    // OPTIONAL: an exploring/uninitialized picker must not submit a stale or
    // not-yet-committed flock even if the control is bypassed. A valid blank
    // selection still submits (canSubmit is true for it).
    if (busy || !addFlockSnapshot.canSubmit) return;
    void run("add", () => commit("add", async () => {
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
    }));
  }

  // Seeds the dialog from a row. It is NOT a session edge on its own (#703):
  // the row button that opens a correction calls `openDialog("edit")` first —
  // a switch straight from one bound expense to another, with no Cancel or
  // close in between, is reachable to a screen reader's virtual cursor (#480),
  // and that call ends the displaced session — while the 409 rebind below
  // calls this on the SAME id to reopen the SAME session with the winner's
  // values; ending the session there would gate off the very report the
  // rebind is about to make.
  function startEdit(x: Expense) {
    setEditFlockPickerOpen(false);
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

  // Dismissal is one of the two session edges (#703): it mutes the attempt
  // still out, so a late failure lands nowhere, and ends the session, so a
  // late success cannot act on the dialog the user opens next.
  function closeEdit() {
    setEditFlockPickerOpen(false);
    dismissDialog("edit");
    setEditing(null);
    setEditFlockEntity(null);
    setEditFlockId(null);
    setEditRequestedId(null);
    setEditFlockGen((g) => g + 1);
    setEditFlockSnapshot({ committed: null, selectionPhase: "uninitialized", exploring: false, canSubmit: true });
  }

  function closeAddCategory() {
    setAddingCategory(false);
    dismissDialog("add-category");
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
    // The run scope is the dialog's; the idempotency key stays per expense.
    const scope = `edit:${target.id}`;
    void run("edit", (current) => commit(scope, async () => {
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
          // The correction's superseded case is unreachable through the UI
          // (the row's correct button is `disabled={busy}`); the gate stays for
          // INV-1 and against a future change that enables the button — pinned
          // by the wiring test "a successful correction closes its dialog and
          // refreshes the list".
          if (current()) setEditing(null);
        });
        if (current()) setMessage(i18n.t("expenses:expenseCorrectedMessage"));
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
          // the winner's values and no word of why (codex on #491). The app
          // is reopening the dialog uninvited and saying something new, so it
          // un-mutes the slot it reports into — the one explicit
          // `beginAttempt` a migrated screen keeps (#703; History's rebind does
          // the same). The slot is the dialog's fixed "edit" now.
          errors.beginAttempt("edit");
          throw new Error(i18n.t("expenses:conflictRebindMessage"));
        }
        throw err;
      }
    }));
  }

  // The category-create KEY scope is derived from the typed name (it keys the
  // idempotent write); the run scope — the spinner's and the slot's — is the
  // dialog's fixed "add-category" since #703.
  const addCategoryScope = `add-category:${newCategoryName.trim().toLowerCase()}`;

  function onAddCategory(e: FormEvent) {
    e.preventDefault();
    const scope = addCategoryScope;
    void run("add-category", (current) => commit(scope, async () => {
      await createExpenseCategory({ name: newCategoryName.trim() }, keyFor(scope));
      // Superseded (#703): the category exists and the list below will show
      // it; the reset, the close and the message are the replacement
      // session's.
      if (current()) {
        setNewCategoryName("");
        setAddingCategory(false);
        setMessage(i18n.t("expenses:categoryCreatedMessage"));
      }
      // The dialog closed above, or belongs to another session now, so its
      // slot is not where this attempt's news can land: a refresh failure
      // reported to it would leave the user with a stale category list and no
      // message at all (codex on #491). The write already succeeded, so this
      // is the screen's problem now, not the form's.
      try {
        setCategories(await listExpenseCategories({ includeInactive: true }));
      } catch (err) {
        errors.setPage(errText(err));
      }
    }));
  }

  function onToggleCategory(c: ExpenseCategory) {
    const scope = `toggle-category:${c.id}`;
    void run(scope, () => commit(scope, async () => {
      await updateExpenseCategory(c.id, { name: c.name, active: !c.active }, keyFor(scope));
      // The toggle is durable the moment the server answers, so its key is
      // spent whatever the reload below does: a reload failure is the page's,
      // not the write's — reported there, exactly as onAddCategory's is — and
      // must not keep a spent key for a later toggle to reuse under a
      // different body, which the server refuses as key reuse (CodeRabbit on
      // #706). Pinned by "spends the toggle's key when the update lands but
      // the categories reload fails".
      try {
        setCategories(await listExpenseCategories({ includeInactive: true }));
      } catch (err) {
        errors.setPage(errText(err));
      }
      setMessage(c.active
        ? i18n.t("expenses:categoryDeactivatedMessage", { name: c.name })
        : i18n.t("expenses:categoryReactivatedMessage", { name: c.name }));
    }));
  }

  return (
    <FieldConsole>
      <Stack component="header" direction={{ xs: "column", md: "row" }} sx={{ justifyContent: "space-between", gap: 1, mb: 2 }}>
        <Box><Typography variant="h2">{t("title")}</Typography>
          <Typography component="p" sx={{ m: 0, maxWidth: 700, color: "text.secondary", fontSize: "13px", lineHeight: 1.45 }}>{t("intro")}</Typography>
        </Box>
        <Button variant="contained" startIcon={showCategories ? undefined : <Plus size={16} aria-hidden="true" />} sx={{ minHeight: 44, borderRadius: "4px", flexShrink: 0, alignSelf: { md: "flex-start" } }} onClick={() => setShowCategories((v) => !v)}>
          {showCategories ? t("hideCategoriesButton") : t("manageCategoriesButton")}
        </Button>
      </Stack>
      <ConsoleSummary label={t("contextLabel")} items={[
        { label: t("periodHeading"), value: !expenses.reloading && expenses.meta !== null ? fmt.money(expenses.meta.total, currencyCode, currencyMinor) : "—" },
        { label: t("fromLabel"), value: from ? fmt.date(from) : "—" },
        { label: t("toLabel"), value: to ? fmt.date(to) : "—" },
        { label: t("categoryLabel"), value: categories.find((category) => category.id === filterCategory)?.name ?? t("allCategoriesOption") },
      ]} />

      <FilterBar>
        {/* Do not cap at today: the default end date is month-end. */}
        <FilterDateField label={t("fromLabel")} value={from} onChange={(e) => setFrom(e.target.value)} />
        <FilterDateField label={t("toLabel")} value={to} onChange={(e) => setTo(e.target.value)} />
        <TextField
          select
          label={t("categoryLabel")}
          value={filterCategory}
          size="small"
          // Shrink the label so it does not overlap the empty-value placeholder.
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
          onChange={(e) => setFilterCategory(e.target.value)}
        >
          <option value="">{t("allCategoriesOption")}</option>
          {categories.map((c) => (
            <option key={c.id} value={c.id}>{c.name}{c.active ? "" : t("deactivatedSuffix")}</option>
          ))}
        </TextField>
        <Button variant="outlined" color="inherit" sx={{ borderRadius: "4px" }} onClick={resetFilters}>{tc("clearFiltersButton")}</Button>
      </FilterBar>

      {showCategories && (
        <Box sx={{ my: 3 }}>
          <Divider />
          <Box sx={{ py: 3 }}>
            <Typography variant="h3" component="h3">{t("categoriesHeading")}</Typography>
            <Stack direction="row" useFlexGap spacing={1.5} sx={{ flexWrap: "wrap", alignItems: "center", my: 1.5 }}>
              <button type="button" onClick={() => { openDialog("add-category"); setAddingCategory(true); }}>
                {t("newCategoryButton")}
              </button>
            </Stack>

            <Dialog open={addingCategory} title={t("newCategoryDialogTitle")} onClose={closeAddCategory}
              actions={(
                <DialogActions>
                  <button type="button" className="link" onClick={closeAddCategory}>{tc("cancel")}</button>
                  <BusyButton type="submit" busy={isPending("add-category")} disabled={busy}>{t("addCategoryButton")}</BusyButton>
                </DialogActions>
              )}
              formProps={{ onSubmit: onAddCategory }}
            >
              <Stack spacing={2}>
                <TextField
                  label={t("categoryNameLabel")}
                  value={newCategoryName}
                  disabled={busy}
                  slotProps={{ htmlInput: { required: true } }}
                  onChange={(e) => setNewCategoryName(e.target.value)}
                />
                <DialogError errors={errors} scope="add-category" />
              </Stack>
            </Dialog>

            <List disablePadding>
              {categories.map((c, i) => (
                <ListItem key={c.id} disableGutters divider={i < categories.length - 1}
                  secondaryAction={
                    <BusyButton className="link" type="button" busy={isPending(`toggle-category:${c.id}`)}
                      disabled={busy} onClick={() => onToggleCategory(c)}>
                      {c.active ? t("deactivateButton") : t("reactivateButton")}
                    </BusyButton>
                  }
                >
                  <ListItemText primary={`${c.name}${c.active ? "" : t("deactivatedSuffix")}`} />
                </ListItem>
              ))}
              {categories.length === 0 && (
                <ListItem disableGutters>
                  <ListItemText primary={t("noCategoriesMessage")} slotProps={{ primary: { color: "text.secondary" } }} />
                </ListItem>
              )}
            </List>
          </Box>
          <Divider />
        </Box>
      )}

      <Box sx={{ ...CONSOLE_SPLIT_SX, gridTemplateColumns: { xs: "minmax(0, 1fr)", md: "minmax(0, .9fr) minmax(0, 1.1fr)" } }}>
      <Box sx={CONSOLE_PANEL_SX}>
      <Box component="header" sx={CONSOLE_PAPER_HEAD_SX}>
        <h3>{t("recordExpenseHeading")}</h3>
        <Button sx={{ ...CONSOLE_LINK_SX, fontSize: ".75rem" }} onClick={() => setShowCategories(true)}>{t("manageCategoriesButton")}</Button>
      </Box>
      <Stack component="form" sx={CONSOLE_FORM_SX} onSubmit={onAdd}>
        <TextField
          type="date"
          label={t("dateLabel")}
          value={date}
          size="small"
          slotProps={{ htmlInput: { max: today, required: true }, inputLabel: { shrink: true } }}
          onChange={(e) => setDate(e.target.value)}
        />
        <TextField
          select
          label={t("categoryLabel")}
          value={categoryId}
          size="small"
          // Shrink the label so it does not overlap the empty-value placeholder.
          slotProps={{ select: { native: true }, htmlInput: { required: true }, inputLabel: { shrink: true } }}
          onChange={(e) => setCategoryId(e.target.value)}
        >
          <option value="">{t("pickOption")}</option>
          {activeCategories.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
        </TextField>
        <TextField
          label={t("descriptionLabel")}
          value={description}
          size="small"
          slotProps={{ htmlInput: { required: true, maxLength: 200 } }}
          onChange={(e) => setDescription(e.target.value)}
        />
        <TextField
          type="number"
          label={t("amountLabel", { code: currencyCode || "…" })}
          value={amount}
          size="small"
          slotProps={{ htmlInput: { min: (1 / 10 ** currencyMinor).toFixed(currencyMinor), step: "any", required: true } }}
          onChange={(e) => setAmount(e.target.value)}
        />
        <Box sx={FILTER_PICKER_SX}>
          <FlockPicker
            label={t("flockOptionalLabel")}
            eligibility="all"
            required={false}
            open={addFlockPickerOpen}
            // Sync only after a successful reset so Escape cannot restore the saved flock.
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
        </Box>
        <TextField
          label={t("noteOptionalLabel")}
          value={note}
          size="small"
          slotProps={{ htmlInput: { maxLength: 500 } }}
          onChange={(e) => setNote(e.target.value)}
        />
        {/* Unknown currency precision or an unresolved flock prevents a valid write. */}
        <BusyButton component={Button} variant="contained" type="submit" busy={isPending("add")}
          disabled={busy || activeCategories.length === 0 || !scaleKnown || !addFlockSnapshot.canSubmit}>
          {t("recordExpenseButton")}
        </BusyButton>
      </Stack>
      {activeCategories.length === 0 && (
        <p className="muted">{t("addCategoryFirstMessage")}</p>
      )}
      </Box>
      <Box component="aside" sx={CONSOLE_RAIL_SX}
        aria-label={!expenses.reloading && expenses.meta !== null
          ? t("periodTotalLabel", { amount: fmt.money(expenses.meta.total, currencyCode, currencyMinor) })
          : undefined}>
        {!expenses.reloading && expenses.meta !== null && (
          <>
            <Typography component="p" variant="body2">{t("periodHeading")}</Typography>
            <Typography component="p" variant="body2" sx={{ fontSize: "2rem", fontWeight: 700, fontVariantNumeric: "tabular-nums", my: 1 }}>
              {fmt.money(expenses.meta.total, currencyCode, currencyMinor)}
            </Typography>
            <Typography sx={{ fontSize: ".8rem" }}>{t("wholePeriod")}</Typography>
          </>
        )}
      </Box>
      </Box>

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
        actions={editing && (
          <DialogActions>
            <button type="button" className="link" disabled={busy}
              onClick={closeEdit}>{tc("cancel")}</button>
            {/* #512 (T028): canSubmit also gates the visible control; the
                handler guard above is the real boundary. */}
            <BusyButton type="submit" busy={isPending("edit")}
              disabled={busy || !editFlockSnapshot.canSubmit}>
              {t("saveCorrectionButton")}
            </BusyButton>
          </DialogActions>
        )}
        formProps={{ onSubmit: onSaveEdit }}
      >
        {editing && (
          <Stack spacing={2}>
            <TextField
              type="date"
              label={t("dateLabel")}
              value={editDate}
              slotProps={{ htmlInput: { max: today, required: true }, inputLabel: { shrink: true } }}
              onChange={(e) => setEditDate(e.target.value)}
            />
            <TextField
              select
              label={t("categoryLabel")}
              value={editCategory}
              slotProps={{ select: { native: true }, htmlInput: { required: true } }}
              onChange={(e) => setEditCategory(e.target.value)}
            >
              {editCategories.map((c) => (
                <option key={c.id} value={c.id}>{c.name}{c.active ? "" : t("deactivatedSuffix")}</option>
              ))}
            </TextField>
            <TextField
              label={t("descriptionLabel")}
              value={editDescription}
              slotProps={{ htmlInput: { required: true, maxLength: 200 } }}
              onChange={(e) => setEditDescription(e.target.value)}
            />
            <TextField
              type="number"
              label={t("amountLabel", { code: editing.currencyCode })}
              value={editAmount}
              slotProps={{ htmlInput: {
                min: (1 / 10 ** editing.currencyMinorUnit).toFixed(editing.currencyMinorUnit),
                step: "any", required: true,
              } }}
              onChange={(e) => setEditAmount(e.target.value)}
            />
            {/* Resolve the saved flock by ID, including archived flocks outside discovery. */}
            <Box onKeyDown={(event) => {
              // Let the picker cancel exploration before suppressing the dialog's Escape.
              if (event.key === "Escape" && editFlockPickerOpen) event.stopPropagation();
            }}>
              <FlockPicker
                label={t("flockOptionalLabel")}
                eligibility="all"
                required={false}
                // #512: exact-ID resolution works while collapsed; only discovery needs an open list.
                open={editFlockPickerOpen}
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
                  setEditFlockPickerOpen(false);
                  setEditFlockEntity(f);
                  setEditRequestedId(f.id);
                  setEditFlockId(null);
                  setEditFlockGen((g) => g + 1);
                }}
                onClear={() => {
                  setEditFlockPickerOpen(false);
                  setEditRequestedId(null);
                  setEditFlockEntity(null);
                  setEditFlockId(null);
                  setEditFlockGen((g) => g + 1);
                }}
                onEscape={() => setEditFlockPickerOpen(false)}
                onOutsideClick={() => setEditFlockPickerOpen(false)}
                trigger={
                  <button type="button" className="named-picker-trigger"
                    onClick={() => setEditFlockPickerOpen(true)}>{editFlockEntity
                      ? editFlockEntity.name
                      : editFlockId !== null && editFlockSnapshot.selectionPhase === "unavailable"
                        ? t("flockUnavailable")
                        : t("noneOption")}</button>
                }
              />
            </Box>
            <TextField
              label={t("noteOptionalLabel")}
              value={editNote}
              slotProps={{ htmlInput: { maxLength: 500 } }}
              onChange={(e) => setEditNote(e.target.value)}
            />
            {/* The 409 rebind reports through here, so the conflict banner stays
                next to the form it is telling you to re-apply. */}
            <DialogError errors={errors} scope="edit" />
          </Stack>
        )}
      </Dialog>

      {expenses.error && <p className="error" role="alert">{expenses.error}</p>}

      <ConsoleSubhead title={t("ledgerHeading")} caption={t("ledgerCaption")} />
      {expenses.rows === null || expenses.reloading ? (
        <p className="muted">{tc("loading")}</p>
      ) : expenses.rows.length === 0 ? (
        (from || to || filterCategory)
          ? <EmptyState icon={FilterX} message={t("noExpensesMatch")}
              action={isFiltered
                ? undefined
                : { label: t("showAllTimeButton"), onClick: showAllTime }} />
          : <EmptyState icon={Receipt} message={t("noExpensesMessage")} />
      ) : (
        <LedgerTableContainer>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell sx={NOWRAP}>{t("dateHeader")}</TableCell>
                <TableCell sx={NOWRAP}>{t("categoryHeader")}</TableCell>
                <TableCell>{t("descriptionHeader")}</TableCell>
                <TableCell align="right" sx={NOWRAP}>{t("amountHeader")}</TableCell>
                <TableCell sx={NOWRAP}>{t("flockHeader")}</TableCell>
                <TableCell>{t("noteHeader")}</TableCell>
                <TableCell sx={NOWRAP}>{tc("recordHistoryHeader")}</TableCell>
                <TableCell></TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {expenses.rows.map((x) => (
                <TableRow key={x.id}>
                  <TableCell sx={NOWRAP}><FarmDate iso={x.date} /></TableCell>
                  <TableCell sx={NOWRAP}>{categoryName(x.expenseCategoryId)}</TableCell>
                  <TableCell>{x.description}</TableCell>
                  <TableCell align="right" sx={NOWRAP}>{fmt.money(x.amountMinorUnits, x.currencyCode, x.currencyMinorUnit)}</TableCell>
                  <TableCell sx={NOWRAP}>{rowFlockName(x)}</TableCell>
                  <TableCell>{x.note ?? "—"}</TableCell>
                  <ProvenanceCell history={x} />
                  <TableCell sx={NOWRAP}>
                    <Link className="link" to={`/audit?entityId=${x.id}`}>
                      {tc("recordHistory.viewHistoryLink")}
                    </Link>
                    <Button size="small" sx={{ ...CONSOLE_LINK_SX, ml: "9px" }} disabled={busy}
                      onClick={() => { openDialog("edit"); startEdit(x); }}>
                      {t("correctButton")}
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </LedgerTableContainer>
      )}
      {expenses.canLoadMore && (
        <button className="link" disabled={busy}
          onClick={() => void expenses.loadMore()}>
          {t("loadMoreButton")}
        </button>
      )}
    </FieldConsole>
  );
}
