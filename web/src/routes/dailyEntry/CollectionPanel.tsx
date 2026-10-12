import type { Dispatch, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { Box, Typography } from "@mui/material";
import { NumberField } from "../../components/NumberField";
import { useFormat } from "../../farm/useFormat";
import { captionIdFor, EntryRow, SummaryStat, WorkbenchPanel } from "./entryRows";

interface CollectionPanelProps {
  losses: number;
  lossesExceedTotal: boolean;
  sellable: number;
  totalEggs: number;
  setTotalEggs: Dispatch<SetStateAction<number>>;
  cracked: number;
  setCracked: Dispatch<SetStateAction<number>>;
  dirty: number;
  setDirty: Dispatch<SetStateAction<number>>;
  discarded: number;
  setDiscarded: Dispatch<SetStateAction<number>>;
  mortality: number;
  setMortality: Dispatch<SetStateAction<number>>;
  entryLocked: boolean;
  idFor: (name: string) => string;
  stepSize: number;
}

// The egg-counts workbench panel of the daily-entry screen.
export function CollectionPanel({
  losses, lossesExceedTotal, sellable, totalEggs, setTotalEggs, cracked,
  setCracked, dirty, setDirty, discarded, setDiscarded, mortality, setMortality,
  entryLocked, idFor, stepSize,
}: CollectionPanelProps) {
  const { t } = useTranslation(["dailyEntry", "dashboard"]);
  const fmt = useFormat();

  return (
    <WorkbenchPanel
      title={t("collectionHeading")}
      caption={t("collectionCaption")}
      summary={(
        <Box role="group" aria-label={t("collectionTotalsLabel")} sx={{
          display: "grid", gridTemplateColumns: "1fr 1fr", gap: 2,
          px: 2, py: 1.5, backgroundColor: "var(--surface-2)",
        }}>
          <SummaryStat label={t("setAsideLabel")} value={fmt.count(losses)} />
          <SummaryStat
            label={t("sellableTargetLabel")}
            value={lossesExceedTotal ? "—" : fmt.count(sellable)}
            live={!lossesExceedTotal}
          />
          {lossesExceedTotal && (
            <Typography role="alert" sx={{ gridColumn: "1 / -1", color: "var(--error)" }}>
              {t("countsExceedTotalMessage", { losses, total: totalEggs })}
            </Typography>
          )}
        </Box>
      )}
    >
      <EntryRow
        variant="primary"
        htmlFor={idFor("total")}
        label={t("totalEggsLabel")}
        caption={t("totalEggsCaption")}
      >
        <NumberField id={idFor("total")} label={t("totalEggsLabel").toLowerCase()}
          describedBy={captionIdFor(idFor("total"))} value={totalEggs}
          onChange={setTotalEggs} step={stepSize} disabled={entryLocked} />
      </EntryRow>
      <EntryRow htmlFor={idFor("cracked")} label={t("crackedLabel")}>
        <NumberField id={idFor("cracked")} label={t("crackedLabel").toLowerCase()}
          value={cracked} onChange={setCracked} step={stepSize} disabled={entryLocked} />
      </EntryRow>
      <EntryRow htmlFor={idFor("dirty")} label={t("dirtyLabel")}>
        <NumberField id={idFor("dirty")} label={t("dirtyLabel").toLowerCase()}
          value={dirty} onChange={setDirty} step={stepSize} disabled={entryLocked} />
      </EntryRow>
      <EntryRow htmlFor={idFor("discarded")} label={t("discardedLabel")}>
        <NumberField id={idFor("discarded")} label={t("discardedLabel").toLowerCase()}
          value={discarded} onChange={setDiscarded} step={stepSize} disabled={entryLocked} />
      </EntryRow>
      <EntryRow
        variant="flock-event"
        htmlFor={idFor("mortality")}
        label={t("mortalityLabel")}
        caption={t("mortalityEventCaption")}
      >
        <NumberField id={idFor("mortality")} label={t("mortalityLabel").toLowerCase()}
          describedBy={captionIdFor(idFor("mortality"))} value={mortality}
          onChange={setMortality} disabled={entryLocked} />
      </EntryRow>
    </WorkbenchPanel>
  );
}
