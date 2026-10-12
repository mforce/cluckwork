import type { Dispatch, FormEvent, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { Box, DialogActions, Divider, List, ListItem, ListItemText, Stack, TextField, Typography } from "@mui/material";
import { createExpenseCategory, listExpenseCategories, updateExpenseCategory } from "../../api/cluckwork";
import type { ExpenseCategory } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import { CONSOLE_LINK_SX } from "../../components/FieldConsole";
import type { DialogAction } from "../../components/useDialogAction";
import { errText } from "../../lib/errText";
import i18n from "../../i18n";

// The categories panel of the expenses screen (#87), moved out of ExpensesPage
// (#1215). It renders at exactly the page's old positions — the desktop body
// and the phone record dialog — from the page's own state; the flight guard,
// the message slots and the idempotency keys stay on the page (#703) and are
// passed in with the two write handlers that only this panel issues.
export function ExpenseCategoriesPanel({
  dialog,
  commit,
  keyFor,
  setMessage,
  setCategories,
  setAddingCategory,
  addingCategory,
  newCategoryName,
  setNewCategoryName,
  categories,
}: {
  dialog: DialogAction<"edit" | "add-category">;
  commit: (keyScope: string, fn: () => Promise<void>) => Promise<void>;
  keyFor: (scope: string) => string;
  setMessage: Dispatch<SetStateAction<string | null>>;
  setCategories: Dispatch<SetStateAction<ExpenseCategory[]>>;
  setAddingCategory: Dispatch<SetStateAction<boolean>>;
  addingCategory: boolean;
  newCategoryName: string;
  setNewCategoryName: Dispatch<SetStateAction<string>>;
  categories: ExpenseCategory[];
}) {
  const { busy, isPending, run, openDialog, dismissDialog, errors } = dialog;
  const { t } = useTranslation("expenses");
  const { t: tc } = useTranslation("common");

  function closeAddCategory() {
    setAddingCategory(false);
    dismissDialog("add-category");
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
                  <BusyButton variant="contained" type="submit" busy={isPending("add-category")} disabled={busy}>{t("addCategoryButton")}</BusyButton>
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
                    <BusyButton variant="text" type="button" sx={CONSOLE_LINK_SX} busy={isPending(`toggle-category:${c.id}`)}
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
  );
}
