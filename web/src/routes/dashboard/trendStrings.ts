import { useTranslation } from "react-i18next";
import type { DayStripData, DayStripSlot } from "../../lib/dashboard";
import { useFormat } from "../../farm/useFormat";
import type { DASHBOARD_DELTA_CLASSES } from "../Dashboard";

// The Lay rate card's catalog strings (#650): every figure is farm-locale
// formatted before it reaches a catalog string, built once so the card and
// the expanded chart cannot describe the same days differently.
export function useTrendStrings() {
  const { t } = useTranslation("dashboard");
  const fmt = useFormat();

  // Every figure is farm-locale formatted before it reaches a catalog string (#650).
  const deltaText = (delta: number | null) =>
    delta === null ? "—"
      : delta < 0 ? t("henDayDeltaDown", { delta: fmt.count(Math.abs(delta), 1) })
        : t("henDayDeltaUp", { delta: fmt.count(delta, 1) });
  // A missing day is not zero production; its accessible label must say so.
  // #916 — branches on `line.scale`, never a null check: `max` is non-null
  // under BOTH "complete" and "partial", so a null check alone used to say
  // "no peak or average" beside a caption and Peak figure showing a real number.
  const trendLabel = (line: DayStripData, span: string) => {
    if (line.scale === "none") {
      // A window where no flock ever owed a filing is not a window of missing
      // ones — a new farm's strip used to draw fourteen blank-but-blameless
      // slots and announce that none had an entry. `partial` is always 0 here:
      // a partial slot requires a recorded figure, which "none" has none of.
      return line.partial === 0 && line.unrecorded === 0
        ? t("trendStripLabelNoFlocks", { range: span })
        : t("trendStripLabelNone", { range: span });
    }
    if (line.scale === "partial") {
      // The fallback peak: no complete day exists, so there is still no
      // average (that stays complete-day-only), but Peak is real and the
      // sentence must say so, not fall back to "no peak or average".
      return t("trendStripLabelPartialScale", { range: span, max: fmt.count(line.max!) });
    }
    const figures = { range: span, max: fmt.count(line.max!), avg: fmt.count(line.average!, 1) };
    const gaps = line.partial + line.unrecorded;
    return gaps === 0
      ? t("trendStripLabel", figures)
      : t("trendStripLabelBlanks", { ...figures, count: gaps, blank: fmt.count(gaps) });
  };
  // One day's readout, and the accessible name of its slot. Every figure is
  // farm-locale formatted before it reaches a catalog string (#650). The
  // unrecorded arm carries no count at all — that is the point of #780 — and
  // the partial arm says its total is a floor rather than the day's output.
  const trendTip = (slot: DayStripSlot) => {
    const date = fmt.date(slot.date);
    switch (slot.kind) {
      case "none":
        return t("trendDayTipNoFlocks", { date });
      case "unrecorded":
        return t("trendDayTipNone", { date });
      case "partial":
        return t("trendDayTipPartial", {
          date, count: slot.eggs, total: fmt.count(slot.eggs),
          recorded: fmt.count(slot.filedFlocks), expected: fmt.count(slot.expectedFlocks),
        });
      case "recorded":
        return t("trendDayTip", { date, count: slot.eggs, total: fmt.count(slot.eggs) });
      default: {
        const _exhaustive: never = slot;
        return _exhaustive;
      }
    }
  };
  // Every string a strip is handed, built once so the card and the expanded
  // chart cannot describe the same days differently.
  const stripStrings = (line: DayStripData, span: string) => ({
    label: trendLabel(line, span),
    title: t(line.scale === "partial" ? "trendScaleTitlePartial"
      : line.scale === "none" ? "trendScaleTitleNone" : "trendScaleTitle"),
    peak: t("trendPeak", { total: line.max === null ? "—" : fmt.count(line.max) }),
    average: line.average === null ? t("trendNoCompleteAvg")
      : t("trendCompleteAvg", { total: fmt.count(line.average, 1) }),
    legend: { complete: t("legendComplete"), partial: t("legendPartial"), noEntry: t("legendNoEntry") },
    tip: trendTip,
  });
  const deltaClass = (delta: number | null): (typeof DASHBOARD_DELTA_CLASSES)[number] =>
    delta === null || delta === 0 ? "trend-delta" : delta < 0 ? "trend-delta is-down" : "trend-delta is-up";

  return { deltaText, trendLabel, trendTip, stripStrings, deltaClass };
}
