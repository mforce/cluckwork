import { useEffect, useRef } from "react";
import type { Dispatch, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { Box, Typography } from "@mui/material";
import type { EggGrade } from "../../api/cluckwork";
import { GradingChip, TakeRemainderButton, remainderDropProps } from "../../components/GradingChip";
import { NumberField } from "../../components/NumberField";
import { useFormat } from "../../farm/useFormat";
import { armedState } from "../../lib/grading";
import type { GradingTone } from "../../lib/grading";
import { captionIdFor, EntryRow, SummaryStat, WorkbenchPanel } from "./entryRows";

interface GradingPanelProps {
  assigning: boolean;
  setAssigning: Dispatch<SetStateAction<boolean>>;
  remaining: number;
  entryLocked: boolean;
  prefillPending: boolean;
  prefillFailed: boolean;
  setGradesTouched: Dispatch<SetStateAction<boolean>>;
  setTotalEggs: Dispatch<SetStateAction<number>>;
  setGradeQty: Dispatch<SetStateAction<Record<string, number>>>;
  losses: number;
  gradeQty: Record<string, number>;
  visibleGrades: EggGrade[];
  gradesSum: number;
  idFor: (name: string) => string;
  stepSize: number;
  grading: { tone: GradingTone; count: number | null; says: string; short: string };
  sellable: number;
  lossesExceedTotal: boolean;
}

// The grading workbench panel of the daily-entry screen, with the grade
// updater and the remainder-assignment gesture that only this panel uses.
export function GradingPanel({
  assigning, setAssigning, remaining, entryLocked, prefillPending, prefillFailed,
  setGradesTouched, setTotalEggs, setGradeQty, losses, gradeQty, visibleGrades,
  gradesSum, idFor, stepSize, grading, sellable, lossesExceedTotal,
}: GradingPanelProps) {
  const { t } = useTranslation(["dailyEntry", "dashboard"]);
  const fmt = useFormat();

  // #443 — setGrade reads this instead of the `gradeQty` closure so a
  // hold-to-repeat burst (each tick its own onChange call, still against the
  // SAME setGrade closure captured at press-time — see NumberField's own
  // `live` ref for the identical problem) sees every earlier tick's write,
  // not just the value from the render the hold began on. Synced on every
  // render AND written immediately inside setGrade, matching NumberField's
  // pattern: the render-time write alone would still lag one tick behind
  // during a burst faster than a commit.
  const gradeQtyRef = useRef(gradeQty);
  gradeQtyRef.current = gradeQty;

  // The counts are plain useState setters, so NumberField takes them as-is.
  // A grade lives in a record, so its updater is adapted here — still the
  // functional form, which the hold-to-repeat depends on.
  function assignRest(gradeId: string) {
    if (remaining <= 0 || entryLocked) return;
    setGrade(gradeId)((prev) => prev + remaining);
    setAssigning(false);
  }

  // #443 — grading may run ahead of the total (counted the grades before
  // adding them up); this used to be capped by NumberField's `max` instead.
  // Now a grade bump that would push the graded sum past what step 1's total
  // currently allows raises the total to match, computed here (not via a
  // `remaining`-watching effect) because an effect can't tell a total raised
  // BY this grade edit apart from `remaining` going negative because the
  // total itself was just lowered on step 1 — the latter must never be
  // fought back up.
  //
  // Gated on `newSum > prevSum` (codex review of #449) — not just "still over
  // the total" — for the same reason: after the user lowers the total below
  // an already-graded sum, correcting the grade back DOWN with − is also
  // "still over" on every step until it lands, and without this check each
  // decrement would ratchet the total back up toward the old sum, undoing
  // the very edit the user just made on step 1.
  const setGrade = (gradeId: string) => (next: number | ((prev: number) => number)) => {
    setGradesTouched(true);
    const current = gradeQtyRef.current;
    const prevSum = Object.values(current).reduce((a, b) => a + (b || 0), 0);
    const updated = {
      ...current,
      [gradeId]: typeof next === "function" ? next(current[gradeId] ?? 0) : next,
    };
    gradeQtyRef.current = updated;
    setGradeQty(updated);
    const newSum = Object.values(updated).reduce((a, b) => a + (b || 0), 0);
    if (newSum > prevSum) setTotalEggs((t) => Math.max(t, newSum + losses));
  };

  // Not while the prefill is unsettled: the remainder is computed from counts
  // that are about to be replaced, and handing those to a grade would assign
  // a figure belonging to the previous day.
  const canAssign = remaining > 0 && !entryLocked && !prefillPending && !prefillFailed;
  // DERIVED, not the raw flag (codex round 2 of #403, found on History's mirror
  // of this pane and applied here too): the day can reconcile — or lock, or
  // start a prefill — by something other than the gesture itself, and the
  // effect below only catches up on the NEXT render. Between the two, reading
  // `assigning` directly left rows armed with nothing to place, and on the
  // capture screen it also meant a "+0 here" button over a locked day.
  const armed = armedState(assigning, canAssign);
  // The effect still clears the stale flag, so becoming assignable again does
  // not silently re-arm rows the user is no longer aiming at. setAssigning is
  // the page's useState setter (stable) passed down, so it is listed explicitly.
  useEffect(() => {
    if (!canAssign && assigning) setAssigning(false);
  }, [canAssign, assigning, setAssigning]);

  return (
    <WorkbenchPanel
      title={t("gradingWorkbenchHeading")}
      caption={t("gradingTargetCaption", {
        target: lossesExceedTotal ? "—" : fmt.count(sellable),
      })}
      summary={(
        <Box role="group" aria-label={t("gradingTotalsLabel")} sx={{
          px: 2, py: 1.5, backgroundColor: "var(--surface-2)",
        }}>
          <Box sx={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 2 }}>
            <SummaryStat label={t("gradedLabel")} value={fmt.count(gradesSum)} />
            <SummaryStat
              label={t("stillToGradeLabel")}
              value={lossesExceedTotal ? "—" : fmt.count(Math.max(0, remaining))}
            />
          </Box>
          <Box sx={{
            display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto",
            alignItems: "center", gap: 1.5, mt: 1.5, pt: 1.5,
            borderTop: "1px solid var(--rule-strong)",
            "& .entry-chip": {
              display: "flex", width: "100%", alignItems: "baseline",
              background: "none !important", borderRadius: "0 !important", padding: "0 !important",
            },
            "& .entry-chip-text": { display: "flex", gap: "0.4rem", minWidth: 0 },
            "& .entry-chip-text > span::before": {
              content: '""', display: "inline-block", width: 8, height: 8,
              borderRadius: "50%", marginRight: "6px", verticalAlign: "middle",
              background: "var(--warn)",
            },
            "& .entry-chip.done .entry-chip-text > span::before": { background: "var(--success)" },
          }}>
            <GradingChip tone={grading.tone} count={grading.count} says={grading.says}
              canAssign={canAssign} remaining={remaining}
              assigning={armed} onAssigningChange={setAssigning} />
            <Typography component="strong" sx={{ whiteSpace: "nowrap", fontVariantNumeric: "tabular-nums" }}>
              {t("gradingProgress", {
                graded: fmt.count(gradesSum),
                target: lossesExceedTotal ? "—" : fmt.count(sellable),
              })}
            </Typography>
          </Box>
        </Box>
      )}
    >
      {visibleGrades.map((g) => (
        <EntryRow key={g.id} htmlFor={idFor(g.id)}
          label={g.name} caption={g.active ? undefined : t("deactivatedGradeSuffix")}
          groupLabel={t("gradeRowLabel", { grade: g.name })} armed={armed}
          dropProps={remainderDropProps(armed, () => assignRest(g.id))}
        >
          <NumberField id={idFor(g.id)} label={g.name.toLowerCase()}
            describedBy={g.active ? undefined : captionIdFor(idFor(g.id))}
            value={gradeQty[g.id] ?? 0} onChange={setGrade(g.id)}
            step={stepSize} disabled={entryLocked} />
          {armed && (
            <TakeRemainderButton remaining={remaining} grade={g.name}
              onTake={() => assignRest(g.id)} />
          )}
        </EntryRow>
      ))}
    </WorkbenchPanel>
  );
}
