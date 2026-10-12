import { useId } from "react";
import type { Dispatch, FormEvent, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { Box, Button, DialogActions, Stack, TextField, Typography } from "@mui/material";
import type { DailyEntry, EggGrade } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import type { DialogErrors } from "../../components/useDialogErrors";
import { EntryRow } from "../../components/EntryRow";
import { GradingChip, TakeRemainderButton, remainderDropProps } from "../../components/GradingChip";
import { NumberField } from "../../components/NumberField";
import { useFormat } from "../../farm/useFormat";
import { gradingState } from "../../lib/grading";
import { resolveStepperUnit } from "../../lib/stepperUnit";

export function HistoryAdjustDialog({
  adjusting, rowFlockName, closeAdjust, busy, isPending, errors, onAdjustSubmit, gradesReconciled,
  lossesExceedTotal, sellable, gradesSum, grading, total, setTotal, cracked, setCracked,
  dirty, setDirty, discarded, setDiscarded, mortality, setMortality, stepSize, stepperUnit,
  panelGrades, lineQty, setLine, armed, canAssign, remaining, assignRest, reason, setReason,
  setAssigning,
}: {
  adjusting: DailyEntry | null;
  rowFlockName: (e: { flockId: string; flockName?: string | null }) => string;
  closeAdjust: () => void;
  busy: boolean;
  isPending: (scope: string) => boolean;
  errors: DialogErrors;
  onAdjustSubmit: (ev: FormEvent) => Promise<void>;
  gradesReconciled: boolean;
  lossesExceedTotal: boolean;
  sellable: number;
  gradesSum: number;
  grading: ReturnType<typeof gradingState>;
  total: number;
  setTotal: Dispatch<SetStateAction<number>>;
  cracked: number;
  setCracked: Dispatch<SetStateAction<number>>;
  dirty: number;
  setDirty: Dispatch<SetStateAction<number>>;
  discarded: number;
  setDiscarded: Dispatch<SetStateAction<number>>;
  mortality: number;
  setMortality: Dispatch<SetStateAction<number>>;
  stepSize: number;
  stepperUnit: ReturnType<typeof resolveStepperUnit>;
  panelGrades: (e: DailyEntry) => EggGrade[];
  lineQty: Record<string, number>;
  setLine: (gradeId: string) => (next: number | ((prev: number) => number)) => void;
  armed: boolean;
  canAssign: boolean;
  remaining: number;
  assignRest: (gradeId: string) => void;
  reason: string;
  setReason: (reason: string) => void;
  setAssigning: Dispatch<SetStateAction<boolean>>;
}) {
  const { t } = useTranslation("history");
  const { t: tc } = useTranslation("common");
  const { t: te } = useTranslation("dailyEntry");
  const fmt = useFormat();

  // F131: the correction form is a dialog — it takes focus itself, so the
  // scroll-and-focus dance the old above-the-table panel needed is gone
  // (codex, PR #81).

  // NumberField owns its own input, so the labels point at it by id (#250,
  // same F134 idiom as the daily-entry screen this form mirrors).
  const fieldId = useId();
  const idFor = (name: string) => `${fieldId}-${name}`;

  return (
    <Dialog
      open={adjusting !== null}
      title={adjusting
        ? t("adjustDialogTitleWithEntry", { date: fmt.date(adjusting.date), flock: rowFlockName(adjusting) })
        : t("adjustDialogTitle")}
      onClose={closeAdjust}
      // Two panes side by side need the room; on a phone the dialog is a
      // full-width sheet and they stack, exactly as the capture screen does.
      wide
      // A 409 swaps the server's newer entry into the open dialog; the record
      // identity changing pulls focus back to the first field, so the form is
      // not silently replaced under the user's cursor.
      focusKey={adjusting}
      actions={adjusting && (
        <DialogActions sx={{ display: "grid", gridTemplateColumns: "repeat(2, minmax(0, 1fr))", gap: 1, p: 0, "& > :not(style) ~ :not(style)": { ml: 0 }, "& button": { width: "100%", height: 44, minHeight: 44, px: 1, fontSize: ".75rem", whiteSpace: "nowrap" } }}>
          <Button variant="outlined" color="inherit" type="button" onClick={closeAdjust}>{tc("cancel")}</Button>
          {/* #394: an adjustment has no draft state — Save stays disabled
              until grading reconciles exactly, the same rule Daily
              Entry's submit uses. */}
          <BusyButton variant="contained" type="submit" busy={isPending("adjust")}
            disabled={busy || !reason.trim() || !gradesReconciled}>{t("saveAdjustmentButton")}</BusyButton>
        </DialogActions>
      )}
      formProps={{ onSubmit: onAdjustSubmit }}
    >
      {adjusting && (
        <>
          {adjusting.adjustedFrom && (
            <p className="muted">
              {t("previouslyAdjusted", {
                total: adjusting.adjustedFrom.totalEggs,
                mortality: adjusting.adjustedFrom.mortalityCount,
                // adjustReason is typed string | null; a set adjustedFrom
                // always carries a reason in practice, but the fallback
                // keeps this a plain string for the interpolation type.
                reason: adjusting.adjustReason ?? "",
              })}
            </p>
          )}
          <Stack spacing={2}>
          {!lossesExceedTotal && <Box role="group" aria-label={t("reconciliation")} sx={{ p: 1.5, bgcolor: gradesReconciled ? "var(--tint-ok)" : "var(--tint-warn)", color: gradesReconciled ? "var(--success)" : "var(--warn)", fontWeight: 700, fontVariantNumeric: "tabular-nums", borderRadius: "var(--r-input)" }}>
            {t("reconciliationLine", {
              total: fmt.count(total), cracked: fmt.count(cracked), dirty: fmt.count(dirty), discarded: fmt.count(discarded),
              sellable: fmt.count(sellable), graded: fmt.count(gradesSum), comparison: gradesReconciled ? "=" : "≠",
            })}
          </Box>}
          {/* #444 — same caption as the capture screen; the dialog IS that
              form, so the taps count the same way and say so the same way. */}
          {stepSize > 1 && (
            <p className="hint">
              {te("stepperUnitCaption", { unit: stepperUnit.unitCode, count: stepSize })}
            </p>
          )}
          <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", md: "repeat(2, minmax(0, 1fr))" }, gap: { xs: 3.5, md: 4 } }}>
            <Box component="section">
              {/* Accessible-name computation trims nested spans; keep word spaces in the heading. */}
              <Typography variant="h3" component="h3">
                <span className="step-n">{te("stepLabel", { n: 1 })}</span> <span className="sr-only">{te("stepOfTotal")}</span> {te("eggCountsHeading")}
              </Typography>
              <EntryRow htmlFor={idFor("total")} label={te("totalEggsLabel")}>
                <NumberField id={idFor("total")} label={te("totalEggsLabel").toLowerCase()}
                  value={total} onChange={setTotal} step={stepSize} />
              </EntryRow>
              <EntryRow htmlFor={idFor("cracked")} label={te("crackedLabel")}>
                <NumberField id={idFor("cracked")} label={te("crackedLabel").toLowerCase()}
                  value={cracked} onChange={setCracked} step={stepSize} />
              </EntryRow>
              <EntryRow htmlFor={idFor("dirty")} label={te("dirtyLabel")}>
                <NumberField id={idFor("dirty")} label={te("dirtyLabel").toLowerCase()}
                  value={dirty} onChange={setDirty} step={stepSize} />
              </EntryRow>
              <EntryRow htmlFor={idFor("discarded")} label={te("discardedLabel")}>
                <NumberField id={idFor("discarded")} label={te("discardedLabel").toLowerCase()}
                  value={discarded} onChange={setDiscarded} step={stepSize} />
              </EntryRow>
              {/* Mortality counts individual birds, regardless of the egg step size. */}
              <EntryRow htmlFor={idFor("mortality")} label={te("mortalityLabel")}>
                <NumberField id={idFor("mortality")} label={te("mortalityLabel").toLowerCase()}
                  value={mortality} onChange={setMortality} />
              </EntryRow>

              {lossesExceedTotal ? (
                <Typography role="alert" sx={{ mt: 2, color: "var(--error)" }}>
                  {te("countsExceedTotalMessage", { losses: grading.losses, total })}
                </Typography>
              ) : (
                <Typography component="p" role="status" sx={{ display: "flex", justifyContent: "space-between", alignItems: "baseline", gap: 2, mt: 2, pt: 1.5, borderTop: "1px dashed var(--hairline)" }}>
                  <span className="muted">{te("sellableLabel")}<br />{te("sellableFormula", { total, cracked, dirty, discarded })}</span>
                  <Box component="span" sx={{
                    fontSize: "1.5rem", fontWeight: 700, fontVariantNumeric: "tabular-nums",
                    // #835/#948 — one of the stat figures; clears the root's text-cut pin.
                    fontVariationSettings: "normal",
                  }}>{sellable}</Box>
                </Typography>
              )}
            </Box>

            <Box component="section">
              <Typography variant="h3" component="h3">
                <span className="step-n">{te("stepLabel", { n: 2 })}</span> <span className="sr-only">{te("stepOfTotal")}</span> {te("gradingHeading")}
              </Typography>
              {panelGrades(adjusting).map((g) => (
                <EntryRow key={g.id} htmlFor={idFor(`grade-${g.id}`)}
                  label={g.name} caption={g.active ? undefined : t("inactiveGradeSuffix")}
                  groupLabel={te("gradeRowLabel", { grade: g.name })} armed={armed}
                  dropProps={remainderDropProps(armed, () => assignRest(g.id))}
                >
                  {/* Allow a grade to exceed the current total; setLine raises the total to fit. */}
                  <NumberField id={idFor(`grade-${g.id}`)} label={g.name.toLowerCase()}
                    value={lineQty[g.id] ?? 0} onChange={setLine(g.id)} step={stepSize} />
                  {armed && (
                    <TakeRemainderButton remaining={remaining} grade={g.name}
                      onTake={() => assignRest(g.id)} />
                  )}
                </EntryRow>
              ))}

              <GradingChip tone={grading.tone} count={grading.count}
                says={te(grading.saysKey)}
                canAssign={canAssign} remaining={remaining}
                assigning={armed} onAssigningChange={setAssigning} />
            </Box>
          </Box>

          <TextField fullWidth
            label={t("reasonLabel")}
            value={reason}
            slotProps={{ htmlInput: { maxLength: 500, required: true } }}
            onChange={(e) => setReason(e.target.value)}
          />
          {/* The 409 rebind reports here, beside the form it asks you to re-apply. */}
          <DialogError errors={errors} scope="adjust" />
          </Stack>
        </>
      )}
    </Dialog>
  );
}
