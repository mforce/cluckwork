// web/src/lib/layRateRange.ts
//
// #914 — the window the Dashboard's Lay rate card plots, and the window of the
// same length before it that the hen-day KPI compares against. Kept pure so the
// arithmetic has literal oracles and the screen only renders what it returns.
import { daysBefore, inclusiveDays, isIsoCalendarDate } from "./dates";

// Only what the card can draw ONE BAR A DAY (owner, 2026-09-23). Longer
// ranges, and the display that carries them, are the expanded chart's (#941).
export const RANGE_PRESETS = [7, 14] as const;
// #941 — the expanded chart's own presets. It scrolls, so a bar stays 22px at
// every range and the ceiling is what a farm manager can reason about rather
// than what fits a column.
export const EXPANDED_RANGE_PRESETS = [30] as const;
export type RangePreset = (typeof RANGE_PRESETS)[number] | (typeof EXPANDED_RANGE_PRESETS)[number];

export type LayRateRange =
  | { kind: "preset"; days: RangePreset }
  | { kind: "custom"; from: string; to: string };

export const DEFAULT_RANGE: LayRateRange = { kind: "preset", days: 14 };
export const DEFAULT_EXPANDED_RANGE: LayRateRange = { kind: "preset", days: 30 };

// The widest window the strip can draw at a readable width: a bar plus its gap
// needs 24px on a phone (22px slots, 2px gaps — #912, in 342px of plot), which
// divides to 14. Past it the card would have to change what a bar MEANS, and
// that is #941's job rather than a quiet re-scaling of this one.
export const MAX_RANGE_DAYS = 14;

// The expanded chart's ceiling (#941). A quarter is as far back as the farm's
// own reasoning goes, and 90 slots is 2,336px of strip — long enough to need
// the overview map, short enough that the map still marks single days.
export const MAX_EXPANDED_RANGE_DAYS = 90;

export interface TrendWindow {
  from: string;
  to: string;
  days: number;
  previousFrom: string;
  previousTo: string;
}

// A preset ends YESTERDAY, never today: an unsubmitted today would end the line
// in a false dip (owner decision A, #906). A custom range names its own end and
// is held to the same ceiling by `customRangeError`.
export function trendWindow(range: LayRateRange, today: string): TrendWindow {
  const from = range.kind === "preset" ? daysBefore(today, range.days) : range.from;
  const to = range.kind === "preset" ? daysBefore(today, 1) : range.to;
  const days = inclusiveDays(from, to);
  return { from, to, days, previousFrom: daysBefore(from, days), previousTo: daysBefore(from, 1) };
}

export type CustomRangeError = "incomplete" | "order" | "future" | "tooLong" | "beforeCalendar";

// The first day the proleptic Gregorian calendar the app uses has, and the
// floor `isIsoCalendarDate` already enforces on a single date.
const CALENDAR_START = "0001-01-01";

// `latest` is the newest day the card will plot, which is the farm's yesterday.
// `maxDays` is the ceiling of the SURFACE asking: the card's 14, or the
// expanded chart's 90.
export function customRangeError(
  from: string, to: string, latest: string, maxDays: number = MAX_RANGE_DAYS,
): CustomRangeError | null {
  if (!isIsoCalendarDate(from) || !isIsoCalendarDate(to)) return "incomplete";
  if (from > to) return "order";
  if (to > latest) return "future";
  const days = inclusiveDays(from, to);
  if (days > maxDays) return "tooLong";
  // The card always compares against an equal window directly before this one.
  // A range that leaves no room for it has nothing to compare against, and the
  // report request that would carry it starts outside the calendar (#940
  // review, finding 5).
  if (daysBefore(from, days) < CALENDAR_START) return "beforeCalendar";
  return null;
}

const CUSTOM_SEPARATOR = "..";

// Remembered per device and per farm (#535's account-scoped storage), so one
// browser holding two farms never hands one farm's window to the other.
export const RANGE_STORAGE_KEY = "cluckwork.layRateRange";
// Its own key, so opening the expanded chart never rewrites the card's window
// and the two surfaces are remembered as what they are: two questions.
export const EXPANDED_RANGE_STORAGE_KEY = "cluckwork.layRateExpandedRange";

export function formatStoredRange(range: LayRateRange): string {
  return range.kind === "preset" ? String(range.days) : `${range.from}${CUSTOM_SEPARATOR}${range.to}`;
}

// A compact string rather than JSON: nothing here needs a parser that can throw,
// so the stored value is validated by the same rules the form applies and an
// unrecognised one yields null, which the caller reads as "nothing remembered".
export function parseStoredRange(
  raw: string | null,
  latest: string,
  presets: readonly RangePreset[] = RANGE_PRESETS,
  maxDays: number = MAX_RANGE_DAYS,
): LayRateRange | null {
  if (raw === null) return null;
  const preset = presets.find((days) => String(days) === raw);
  if (preset !== undefined) return { kind: "preset", days: preset };
  const [from, to, ...rest] = raw.split(CUSTOM_SEPARATOR);
  if (rest.length > 0 || to === undefined) return null;
  return customRangeError(from, to, latest, maxDays) === null ? { kind: "custom", from, to } : null;
}
