import { useState } from "react";
import type { Dispatch, FormEvent, SetStateAction } from "react";
import { adjustExpense, getExpense } from "../../api/cluckwork";
import type { Expense, ExpenseCategory, Flock } from "../../api/cluckwork";
import { ApiError } from "../../api/client";
import type { PickerSnapshot } from "../../components/NamedEntityPicker";
import type { DialogAction } from "../../components/useDialogAction";
import type { usePagedList } from "../../components/usePagedList";
import { toMinorUnits } from "./toMinorUnits";
import i18n from "../../i18n";

type ExpenseListMeta = { total: number; code: string; minor: number };
type ExpensePagedList = ReturnType<typeof usePagedList<Expense, ExpenseListMeta>>;

// The admin correction (edit) state machine of the expenses screen, moved out
// of ExpensesPage (#1215): the dialog's state, the row-seeding startEdit, the
// session-edge close and the version-guarded save with its 409 rebind. The
// flight guard, the message slots, the idempotency keys and the paged list's
// write ticket stay on the page (#703, #469, #512) and are passed in; nothing
// here issues a read outside those.
export function useExpenseEdit({
  dialog,
  commit,
  keyFor,
  settleKey,
  expenses,
  flocks,
  categories,
  activeCategories,
  setMessage,
}: {
  dialog: DialogAction<"edit" | "add-category">;
  commit: (keyScope: string, fn: () => Promise<void>) => Promise<void>;
  keyFor: (scope: string) => string;
  settleKey: (scope: string, err?: unknown) => void;
  expenses: ExpensePagedList;
  flocks: Flock[];
  categories: ExpenseCategory[];
  activeCategories: ExpenseCategory[];
  setMessage: Dispatch<SetStateAction<string | null>>;
}) {
  const { busy, run, errors, dismissDialog } = dialog;

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

  // The edit picker offers active categories plus the expense's own (possibly
  // deactivated) one — keeping it must stay legal (grandfathering).
  const editCategories = editing === null
    ? activeCategories
    : categories.filter((c) => c.active || c.id === editing.expenseCategoryId);

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

  return {
    editing,
    editDate, setEditDate,
    editCategory, setEditCategory,
    editDescription, setEditDescription,
    editAmount, setEditAmount,
    editFlockPickerOpen, setEditFlockPickerOpen,
    editFlockEntity, editFlockId,
    editRequestedId, editFlockGen,
    setEditFlockEntity, setEditFlockId, setEditRequestedId, setEditFlockGen,
    editFlockSnapshot, setEditFlockSnapshot,
    editNote, setEditNote,
    editCategories,
    startEdit,
    closeEdit,
    onSaveEdit,
  };
}
