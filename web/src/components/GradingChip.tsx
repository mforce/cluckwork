import { useTranslation } from "react-i18next";
import type { DragEventHandler } from "react";
import type { GradingTone } from "../lib/grading";

// Marks OUR drag payload. Rows accept a drop only when they see this type, so
// a file or a bit of text dragged in from elsewhere cannot assign the day
// (codex review of PR #137).
export const REMAINDER_DRAG = "application/x-cluckwork-remainder";

interface GradingChipProps {
  tone: GradingTone;
  /** null when there is no number worth showing (the counts are broken). */
  count: number | null;
  /** Already-translated wording for the current tone. */
  says: string;
  /** Whether handing the remainder to one grade is possible right now. */
  canAssign: boolean;
  remaining: number;
  assigning: boolean;
  onAssigningChange: (on: boolean) => void;
}

// F134's reconciliation chip: how many eggs are still unaccounted for — the
// number the grading pane is working towards — plus the gesture that hands the
// whole remainder to one grade. Shared by the Daily entry screen and History's
// adjust dialog, which mirrors it (a correction replaces the day's official
// numbers, so it is held to the same reconciliation).
export function GradingChip({
  tone, count, says, canAssign, remaining, assigning, onAssigningChange,
}: GradingChipProps) {
  const { t } = useTranslation("dailyEntry");

  return (
    <div className={`entry-chip ${tone}`}>
      {/* role=status on the text alone: the chip also holds a control, and a
          live region containing a button re-announces the button every time the
          number ticks. The space between the count and the wording is real, not
          the flex gap — CSS contributes no whitespace to the accessible name, so
          this used to be read out as "60left to grade". */}
      <span className="entry-chip-text" role="status">
        {count !== null && <><b>{count}</b>{" "}</>}
        <span>{says}</span>
      </span>
      {canAssign && (
        <button
          type="button"
          className="entry-chip-grab"
          draggable
          aria-pressed={assigning}
          aria-label={assigning
            ? t("disarmAriaLabel")
            : t("armAriaLabel", { count: remaining })}
          onDragStart={(e) => {
            e.dataTransfer.effectAllowed = "move";
            e.dataTransfer.setData(REMAINDER_DRAG, String(remaining));
            // Firefox refuses to start a drag with an empty payload.
            e.dataTransfer.setData("text/plain", String(remaining));
            onAssigningChange(true);
          }}
          onDragEnd={() => onAssigningChange(false)}
          onClick={() => onAssigningChange(!assigning)}
        >
          {assigning ? t("disarmButton") : t("armButton")}
        </button>
      )}
    </div>
  );
}

// Dragging alone would be a dead end on the phone this screen is used on, and
// unreachable by keyboard (WCAG 2.5.7) — so arming turns every grade row into a
// plain button too. It sits BESIDE the field rather than replacing it: which
// grade should take the rest is a decision made by looking at what each one
// already holds.
export function TakeRemainderButton(
  { remaining, grade, onTake }: { remaining: number; grade: string; onTake: () => void },
) {
  const { t } = useTranslation("dailyEntry");
  return (
    <button type="button" className="entry-take"
      aria-label={t("takeRemainderAriaLabel", { count: remaining, grade })}
      onClick={onTake}>
      {t("takeRemainderButton", { count: remaining })}
    </button>
  );
}

// The row half of the same gesture: accept the drop only while armed, and only
// for our own payload type.
export function remainderDropProps(
  assigning: boolean,
  onTake: () => void,
): { onDragOver?: DragEventHandler; onDrop?: DragEventHandler } {
  if (!assigning) return {};
  return {
    onDragOver: (e) => {
      if (e.dataTransfer.types.includes(REMAINDER_DRAG)) e.preventDefault();
    },
    onDrop: (e) => {
      if (!e.dataTransfer.types.includes(REMAINDER_DRAG)) return;
      e.preventDefault();
      onTake();
    },
  };
}
