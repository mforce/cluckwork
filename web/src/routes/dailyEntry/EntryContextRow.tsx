import { useState } from "react";
import type { Dispatch, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { Box, TextField } from "@mui/material";
import type { Flock } from "../../api/cluckwork";
import { FlockPicker } from "../../components/FlockPicker";
import type { PickerSnapshot } from "../../components/NamedEntityPicker";

interface EntryContextRowProps {
  pickerFlock: Flock | null;
  pickerFlockGen: number;
  flockId: string;
  flocks: Flock[];
  handleFlockSnapshot: (s: PickerSnapshot<Flock>) => void;
  retarget: (apply: () => void) => void;
  setFlockId: (id: string) => void;
  setPickerFlock: Dispatch<SetStateAction<Flock | null>>;
  setPickerFlockGen: Dispatch<SetStateAction<number>>;
  date: string;
  setDate: Dispatch<SetStateAction<string>>;
  today: string;
  isAdmin: boolean;
  openDialog: (scope: "new-flock") => void;
  setShowNewFlock: Dispatch<SetStateAction<boolean>>;
}

// The flock+date context row of the daily-entry screen. The picker's open
// state lives here: it is read and written only by this row's JSX.
export function EntryContextRow({
  pickerFlock, pickerFlockGen, flockId, flocks, handleFlockSnapshot, retarget,
  setFlockId, setPickerFlock, setPickerFlockGen, date, setDate, today, isAdmin,
  openDialog, setShowNewFlock,
}: EntryContextRowProps) {
  const { t } = useTranslation(["dailyEntry", "dashboard"]);

  // #512 (T027) — the picker's open state is page-owned; commit/Escape/
  // outside-click all close it. The capture flock is required: the trigger is
  // the closed-state control and the combobox occupies the same form slot.
  const [flockPickerOpen, setFlockPickerOpen] = useState(false);

// Context, not a step: choosing a flock and a date says WHICH day is
// being recorded, it is not part of recording it. Both fields are
// outlined (owner decision on #898, 2026-09-17, from an A/B against
// the mockup's underlined variant); each component's default, so no
// `variant` is passed. Below `md` the Flock picker stacks full-width
// above Date, because a 110px field in a two-up row could not show a
// flock name (owner, #898). At `md` the Date/button group sizes to
// its content rather than sharing the row 50/50 as the old flat row
// did; that is the frame the owner approved.
  return (
    <Box sx={{
      display: "flex", flexDirection: { xs: "column", md: "row" },
      gap: { xs: 2, md: 5 }, mt: 3, p: 2, alignItems: { xs: "stretch", md: "flex-end" },
      backgroundColor: "var(--surface-2)", border: "1px solid var(--rule)",
      borderRadius: "var(--r-panel)",
    }}
    >
      <Box sx={{ flex: 1, minWidth: 0 }}>
        <FlockPicker
          label={t("flockLabel")}
          eligibility="active-and-depleted"
          required
          open={flockPickerOpen}
          controlledCommitted={pickerFlock}
          controlledGeneration={pickerFlockGen}
          requestedId={pickerFlock ? null : flockId || null}
          onSnapshot={handleFlockSnapshot}
          onCommit={(f) => retarget(() => {
            setFlockId(f.id);
            setPickerFlock(f);
            setPickerFlockGen((g) => g + 1);
          })}
          onEscape={() => setFlockPickerOpen(false)}
          onOutsideClick={() => setFlockPickerOpen(false)}
          trigger={
            <button
              type="button"
              className="named-picker-trigger"
              onClick={() => setFlockPickerOpen(true)}
            >
              {pickerFlock
                ? `${pickerFlock.name} (${pickerFlock.breed})${pickerFlock.status === "Depleted" ? t("depletedFlockSuffix") : ""}`
                : flocks.length === 0
                  ? t("noFlocksYetOption")
                  : t("selectFlockOption")}
            </button>
          }
        />
      </Box>
      <Box sx={{ display: "flex", flexDirection: "row", gap: { xs: 2, md: 5 }, alignItems: "flex-end" }}>
        {/* A native date input shows its own "mm/dd/yyyy" even when empty,
            so the label is forced shrunk or it sits on top of that text. */}
        <TextField
          type="date"
          label={t("dateLabel")}
          value={date}
          onChange={(e) => retarget(() => setDate(e.target.value))}
          slotProps={{ htmlInput: { max: today }, inputLabel: { shrink: true } }}
          size="small"
          sx={{ flex: 1, minWidth: 0 }}
        />
        {isAdmin && (
          <button className="link" type="button" onClick={() => { openDialog("new-flock"); setShowNewFlock(true); }}>
            {t("newFlockButton")}
          </button>
        )}
      </Box>
    </Box>
  );
}
