import { Box, Button } from "@mui/material";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import type { DailyEntry } from "../../api/cluckwork";
import { Dialog } from "../../components/Dialog";
import { CONSOLE_LINK_SX } from "../../components/FieldConsole";
import { PhoneDetailsField } from "../../components/PhoneLedger";
import { ProvenanceSummary } from "../../components/ProvenanceCell";
import { useFormat } from "../../farm/useFormat";

export function HistoryDetailsDialog({
  details, setDetails, rowActions, rowFlockName, conditionStock, gradeList, busy, startAdjust, onVoid,
}: {
  details: DailyEntry | null;
  setDetails: (details: DailyEntry | null) => void;
  rowFlockName: (e: { flockId: string; flockName?: string | null }) => string;
  conditionStock: (e: DailyEntry) => string;
  gradeList: (e: DailyEntry) => string;
  rowActions: (e: DailyEntry) => { viewHistory: boolean; edit: boolean; correct: boolean };
  busy: boolean;
  startAdjust: (e: DailyEntry) => void;
  onVoid: (e: DailyEntry) => Promise<void>;
}) {
  const { t } = useTranslation("history");
  const { t: tc } = useTranslation("common");
  const { t: te } = useTranslation("dailyEntry");
  const fmt = useFormat();

  // #980 — everything the two phone lines leave out, plus the row's own
  // actions. Rendered at every width but only reachable below 900px: the
  // desktop table shows all of it in its own columns.
  return (
    <Dialog
      open={details !== null}
      compactTitle
      // The screen's own heading is the fallback, so the dialog is never
      // nameless during its exit transition (same shape as the adjust
      // dialog's `adjustDialogTitle`).
      title={details
        ? t("detailsDialogTitle", { date: fmt.date(details.date), flock: rowFlockName(details) })
        : t("title")}
      onClose={() => setDetails(null)}
      actions={details && (
        <Box sx={{
          display: "flex", flexWrap: "wrap", alignItems: "center", columnGap: .75,
          borderTop: "1px solid var(--rule)",
          "& .MuiButtonBase-root": { minHeight: 44 },
        }}>
          {rowActions(details).viewHistory && (
            <Button component={Link} size="small" sx={CONSOLE_LINK_SX}
              to={`/audit?entityId=${details.id}`}>
              {tc("recordHistory.viewHistoryLink")}
            </Button>
          )}
          {rowActions(details).edit && (
            <Button component={Link} size="small" sx={CONSOLE_LINK_SX}
              to={`/daily-entry?flockId=${details.flockId}&date=${details.date}`}>
              {t("editButton")}
            </Button>
          )}
          {rowActions(details).correct && (
            <Box sx={{ display: "flex", alignItems: "center", columnGap: .75, borderLeft: "1px solid var(--rule)", pl: .75 }}>
              {/* Each correction replaces this dialog rather than stacking on
                  it: the reason prompt and the adjust form are both about the
                  same entry, and two dialogs deep is a phone trap. */}
              <Button size="small" sx={CONSOLE_LINK_SX} disabled={busy}
                onClick={() => { setDetails(null); startAdjust(details); }}>{t("adjustButton")}</Button>
              <Button size="small" sx={{ ...CONSOLE_LINK_SX, color: "var(--error)" }} disabled={busy}
                onClick={() => { setDetails(null); void onVoid(details); }}>{t("voidButton")}</Button>
            </Box>
          )}
        </Box>
      )}
    >
      {details && (
        <Box component="dl" sx={{ m: 0 }}>
          {([
            [te("crackedLabel"), fmt.count(details.crackedEggs)],
            [te("dirtyLabel"), fmt.count(details.dirtyEggs)],
            [te("discardedLabel"), fmt.count(details.discardedEggs)],
            [t("conditionHeader"), conditionStock(details)],
            [t("gradedHeader"), gradeList(details)],
            [tc("recordHistoryHeader"), <ProvenanceSummary history={details} official="submitted" />],
          ] as const).map(([label, value]) => (
            <PhoneDetailsField key={label} label={label}>{value}</PhoneDetailsField>
          ))}
        </Box>
      )}
    </Dialog>
  );
}
