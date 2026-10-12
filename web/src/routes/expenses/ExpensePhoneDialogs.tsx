import type { Dispatch, SetStateAction } from "react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { Box, Button, Stack } from "@mui/material";
import type { Expense, ExpenseCategory, Flock } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { CONSOLE_LINK_SX } from "../../components/FieldConsole";
import { PhoneDetailsField } from "../../components/PhoneLedger";
import type { PickerSnapshot } from "../../components/NamedEntityPicker";
import { ProvenanceSummary } from "../../components/ProvenanceCell";
import type { DialogAction } from "../../components/useDialogAction";
import { useFormat } from "../../farm/useFormat";
import type { useExpenseEdit } from "./useExpenseEdit";

// The phone-only dialogs of the expenses screen, moved out of ExpensesPage
// (#1215): the filter sheet, the record sheet and the row-details sheet. They
// render at exactly the page's old position — under the page's own
// `!isDesktop` gate, so nothing mounts on desktop — and share the page's
// flight guard, message slots and entry/panel elements (#703).
export function ExpensePhoneDialogs({
  dialog,
  edit,
  filters,
  categoriesPanel,
  entryPanel,
  from,
  to,
  filterOpen, setFilterOpen,
  addOpen, setAddOpen,
  showCategories, setShowCategories,
  activeCategories,
  scaleKnown,
  addFlockSnapshot,
  message,
  details, setDetails,
  categories,
  filterCategory,
  categoryName,
  rowFlockName,
}: {
  dialog: DialogAction<"edit" | "add-category">;
  edit: ReturnType<typeof useExpenseEdit>;
  filters: ReactNode;
  categoriesPanel: ReactNode;
  entryPanel: ReactNode;
  from: string;
  to: string;
  filterOpen: boolean; setFilterOpen: Dispatch<SetStateAction<boolean>>;
  addOpen: boolean; setAddOpen: Dispatch<SetStateAction<boolean>>;
  showCategories: boolean; setShowCategories: Dispatch<SetStateAction<boolean>>;
  activeCategories: ExpenseCategory[];
  scaleKnown: boolean;
  addFlockSnapshot: PickerSnapshot<Flock>;
  message: string | null;
  details: Expense | null; setDetails: Dispatch<SetStateAction<Expense | null>>;
  categories: ExpenseCategory[];
  filterCategory: string;
  categoryName: (id: string) => string;
  rowFlockName: (x: Expense) => string;
}) {
  const { busy, isPending, openDialog, errors } = dialog;
  const { startEdit } = edit;
  const { t } = useTranslation("expenses");
  const { t: tc } = useTranslation("common");
  const fmt = useFormat();

  return (
    <>
        <Stack direction="row" sx={{ gap: 1, mb: 1.5, flexWrap: "wrap" }}>
          <Button variant="outlined" color="inherit" sx={{ borderRadius: "100px", minHeight: 36, fontSize: ".75rem" }} onClick={() => setFilterOpen(true)}>{t("rangeChip", { from: from ? fmt.date(from) : "…", to: to ? fmt.date(to) : "…" })}</Button>
          <Button variant="outlined" color="inherit" sx={{ borderRadius: "100px", minHeight: 36, fontSize: ".75rem" }} onClick={() => setFilterOpen(true)}>{categories.find((c) => c.id === filterCategory)?.name ?? t("allCategoriesOption")}</Button>
        </Stack>
        <Dialog open={filterOpen} title={t("filtersTitle")} onClose={() => setFilterOpen(false)}>{filters}</Dialog>
        <Dialog open={addOpen} title={t("recordExpenseHeading")} onClose={() => { setAddOpen(false); setShowCategories(false); }} closeDisabled={busy}
          actions={!showCategories && <Stack direction="row" sx={{ justifyContent: "flex-end", gap: 1 }}>
            <Button size="small" sx={CONSOLE_LINK_SX} disabled={busy} onClick={() => { setAddOpen(false); setShowCategories(false); }}>{tc("cancel")}</Button>
            <BusyButton variant="contained" type="submit" form="expense-entry-form" busy={isPending("add")}
              disabled={busy || activeCategories.length === 0 || !scaleKnown || !addFlockSnapshot.canSubmit}>{t("recordExpenseButton")}</BusyButton>
          </Stack>}>
          {errors.page && <p className="error" role="alert">{errors.page}</p>}
          {message && <p className="success" role="status">{message}</p>}
          {showCategories ? <><Button size="small" sx={CONSOLE_LINK_SX} onClick={() => setShowCategories(false)}>{t("hideCategoriesButton")}</Button>{categoriesPanel}</> : entryPanel}
        </Dialog>
        <Dialog open={details !== null} compactTitle
          title={details ? t("detailsDialogTitle", { date: fmt.date(details.date), description: details.description }) : t("title")}
          onClose={() => setDetails(null)}
          actions={details && <Box sx={{ display: "flex", gap: 1, borderTop: "1px solid var(--rule)", "& .MuiButtonBase-root": { minHeight: 44 } }}>
            <Button component={Link} size="small" sx={CONSOLE_LINK_SX} to={`/audit?entityId=${details.id}`}>{tc("recordHistory.viewHistoryLink")}</Button>
            <Button size="small" sx={CONSOLE_LINK_SX} disabled={busy} onClick={() => { openDialog("edit"); startEdit(details); setDetails(null); }}>{t("correctButton")}</Button>
          </Box>}>
          {details && <Box component="dl" sx={{ m: 0 }}>
            <PhoneDetailsField label={t("categoryHeader")}>{categoryName(details.expenseCategoryId)}</PhoneDetailsField>
            <PhoneDetailsField label={t("amountHeader")}>{fmt.money(details.amountMinorUnits, details.currencyCode, details.currencyMinorUnit)}</PhoneDetailsField>
            <PhoneDetailsField label={t("flockHeader")}>{rowFlockName(details)}</PhoneDetailsField>
            <PhoneDetailsField label={t("noteHeader")}>{details.note ?? "—"}</PhoneDetailsField>
            <PhoneDetailsField label={tc("recordHistoryHeader")}><ProvenanceSummary history={details} /></PhoneDetailsField>
          </Box>}
        </Dialog>
    </>
  );
}
