import type { Dispatch, FormEvent, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { Box, Button, Stack, TextField } from "@mui/material";
import { createExpense } from "../../api/cluckwork";
import type { Expense, ExpenseCategory, Flock } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { CONSOLE_FORM_SX, CONSOLE_LINK_SX, CONSOLE_PANEL_SX, CONSOLE_PAPER_HEAD_SX } from "../../components/FieldConsole";
import { FILTER_PICKER_SX } from "../../components/FilterBar";
import { FlockPicker } from "../../components/FlockPicker";
import type { PickerSnapshot } from "../../components/NamedEntityPicker";
import type { DialogAction } from "../../components/useDialogAction";
import type { usePagedList } from "../../components/usePagedList";
import { toMinorUnits } from "./toMinorUnits";
import i18n from "../../i18n";

type ExpenseListMeta = { total: number; code: string; minor: number };
type ExpensePagedList = ReturnType<typeof usePagedList<Expense, ExpenseListMeta>>;

// The record-expense form of the expenses screen (#87), moved out of
// ExpensesPage (#1215). It renders at exactly the page's old positions — the
// desktop split's left column and the phone record dialog — with the page's
// own add-form state; the flight guard, the idempotency key and the paged
// list's write ticket stay on the page (#703, #469) and are passed in.
export function ExpenseEntryPanel({
  dialog,
  commit,
  keyFor,
  expenses,
  currency,
  setMessage,
  setAddOpen,
  setShowCategories,
  isDesktop,
  today,
  date, setDate,
  categoryId, setCategoryId,
  description, setDescription,
  amount, setAmount,
  note, setNote,
  addFlock, setAddFlock,
  addFlockGen, setAddFlockGen,
  addFlockSnapshot, setAddFlockSnapshot,
  addFlockPickerOpen, setAddFlockPickerOpen,
  activeCategories,
  scaleKnown,
  currencyCode, currencyMinor,
}: {
  dialog: DialogAction<"edit" | "add-category">;
  commit: (keyScope: string, fn: () => Promise<void>) => Promise<void>;
  keyFor: (scope: string) => string;
  expenses: ExpensePagedList;
  currency: { code: string; minor: number } | null;
  setMessage: Dispatch<SetStateAction<string | null>>;
  setAddOpen: Dispatch<SetStateAction<boolean>>;
  setShowCategories: Dispatch<SetStateAction<boolean>>;
  isDesktop: boolean;
  today: string;
  date: string; setDate: Dispatch<SetStateAction<string>>;
  categoryId: string; setCategoryId: Dispatch<SetStateAction<string>>;
  description: string; setDescription: Dispatch<SetStateAction<string>>;
  amount: string; setAmount: Dispatch<SetStateAction<string>>;
  note: string; setNote: Dispatch<SetStateAction<string>>;
  addFlock: Flock | null; setAddFlock: Dispatch<SetStateAction<Flock | null>>;
  addFlockGen: number; setAddFlockGen: Dispatch<SetStateAction<number>>;
  addFlockSnapshot: PickerSnapshot<Flock>;
  setAddFlockSnapshot: (snap: PickerSnapshot<Flock>) => void;
  addFlockPickerOpen: boolean; setAddFlockPickerOpen: Dispatch<SetStateAction<boolean>>;
  activeCategories: ExpenseCategory[];
  scaleKnown: boolean;
  currencyCode: string; currencyMinor: number;
}) {
  const { busy, isPending, run } = dialog;
  const { t } = useTranslation("expenses");

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
      setAddOpen(false);
    }));
  }

  return (
      <Box sx={isDesktop ? CONSOLE_PANEL_SX : { ...CONSOLE_PANEL_SX, border: 0, p: 0, bgcolor: "transparent" }}>
      {isDesktop && <Box component="header" sx={CONSOLE_PAPER_HEAD_SX}>
        <h3>{t("recordExpenseHeading")}</h3>
        <Button sx={{ ...CONSOLE_LINK_SX, fontSize: ".75rem" }} onClick={() => setShowCategories(true)}>{t("manageCategoriesButton")}</Button>
      </Box>}
      <Stack component="form" id="expense-entry-form" sx={CONSOLE_FORM_SX} onSubmit={onAdd}>
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
        {isDesktop && <BusyButton variant="contained" type="submit" busy={isPending("add")}
          disabled={busy || activeCategories.length === 0 || !scaleKnown || !addFlockSnapshot.canSubmit}>
          {t("recordExpenseButton")}
        </BusyButton>}
      </Stack>
      {!isDesktop && <Button size="small" sx={{ ...CONSOLE_LINK_SX, mt: 1.5 }} disabled={busy} onClick={() => setShowCategories(true)}>{t("manageCategoriesButton")}</Button>}
      {activeCategories.length === 0 && (
        <p className="muted">{t("addCategoryFirstMessage")}</p>
      )}
      </Box>
  );
}
