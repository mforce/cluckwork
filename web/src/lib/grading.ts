// The day's reconciliation arithmetic, shared by the two screens that capture
// it: Daily entry (F134's two-step layout) and History's adjust dialog, which
// mirrors that layout field for field.
//
// It lives here because #394 made the two agree on ONE rule — grading must
// equal sellable exactly before an entry may be submitted or adjusted — and a
// second hand-rolled copy of `sellable`/`remaining` in the dialog is exactly
// how the button's disabled state and the readouts drift apart.
//
// Returns catalog KEYS, not text: the caller owns the namespace (and its `t`),
// so this stays a pure function that can be tested without i18n mounted.

export interface GradingInput {
  totalEggs: number;
  cracked: number;
  dirty: number;
  discarded: number;
  /** Sum of every grade line's quantity. */
  gradesSum: number;
}

/** "" = still counting down, "done" = reconciles exactly, "over" = unusable. */
export type GradingTone = "" | "done" | "over";

/** dailyEntry-namespace keys — a union so the typed t() still checks them. */
export type GradingSaysKey =
  | "fixCountsFirst" | "overSellableCount" | "gradedDayAddsUp" | "leftToGrade";
export type GradingShortKey =
  | "fixCountsShort" | "overShort" | "allGradedShort" | "leftShort";

export interface GradingState {
  losses: number;
  sellable: number;
  /** Losses past the total: `sellable` goes negative and nothing else is meaningful. */
  lossesExceedTotal: boolean;
  /** Counts DOWN to zero; negative once the grading overshoots. */
  remaining: number;
  tone: GradingTone;
  /** The figure the chip shows, or null when there is no number worth showing. */
  count: number | null;
  saysKey: GradingSaysKey;
  shortKey: GradingShortKey;
  /** #394's gate: submit (and any adjustment) requires this. */
  reconciled: boolean;
}

export function gradingState(
  { totalEggs, cracked, dirty, discarded, gradesSum }: GradingInput,
): GradingState {
  const losses = cracked + dirty + discarded;
  const sellable = totalEggs - losses;
  const lossesExceedTotal = losses > totalEggs;
  const remaining = sellable - gradesSum;
  // Order matters: a negative sellable makes "3 left to grade" a lie, so the
  // broken counts are reported before anything derived from them.
  const base = lossesExceedTotal
    ? { tone: "over" as const, count: null, saysKey: "fixCountsFirst" as const, shortKey: "fixCountsShort" as const }
    : remaining < 0
      ? { tone: "over" as const, count: -remaining, saysKey: "overSellableCount" as const, shortKey: "overShort" as const }
      : remaining === 0
        ? { tone: "done" as const, count: sellable, saysKey: "gradedDayAddsUp" as const, shortKey: "allGradedShort" as const }
        : { tone: "" as const, count: remaining, saysKey: "leftToGrade" as const, shortKey: "leftShort" as const };

  return {
    losses,
    sellable,
    lossesExceedTotal,
    remaining,
    ...base,
    // NOT `tone === "done"`: the counts being broken is its own "over" state,
    // and a 0-egg day with 0 graded reconciles legitimately.
    reconciled: !lossesExceedTotal && remaining === 0,
  };
}
