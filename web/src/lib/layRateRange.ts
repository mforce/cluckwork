// web/src/lib/layRateRange.ts
//
// #914 — the window the Dashboard's Lay rate card plots, and the window of the
// same length before it that the hen-day KPI compares against. Kept pure so the
// arithmetic has literal oracles and the screen only renders what it returns.
import { daysBefore, inclusiveDays, isIsoCalendarDate } from "./dates";

// 14 is the middle preset rather than the 15 #914 proposed: 15 days cannot be
// drawn one bar per day (MAX_DAY_SLOTS is 14, sized by the card's width), so a
// 15-day preset would collapse to weekly bars of 7, 7 and 1, where 14 keeps a
// full fortnight of daily bars and the phone geometry #912 settled.
export const RANGE_PRESETS = [7, 14, 30] as const;
export type RangePreset = (typeof RANGE_PRESETS)[number];

export type LayRateRange =
  | { kind: "preset"; days: RangePreset }
  | { kind: "custom"; from: string; to: string };

export const DEFAULT_RANGE: LayRateRange = { kind: "preset", days: 14 };

// The widest custom range the card accepts. 90 days is 13 weekly bars, inside
// the 14 the strip can hold, so the cap and the strip's own ceiling agree
// instead of each needing the other to be lenient.
export const MAX_RANGE_DAYS = 90;

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
export function customRangeError(from: string, to: string, latest: string): CustomRangeError | null {
  if (!isIsoCalendarDate(from) || !isIsoCalendarDate(to)) return "incomplete";
  if (from > to) return "order";
  if (to > latest) return "future";
  const days = inclusiveDays(from, to);
  if (days > MAX_RANGE_DAYS) return "tooLong";
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

export function formatStoredRange(range: LayRateRange): string {
  return range.kind === "preset" ? String(range.days) : `${range.from}${CUSTOM_SEPARATOR}${range.to}`;
}

// A compact string rather than JSON: nothing here needs a parser that can throw,
// so the stored value is validated by the same rules the form applies and an
// unrecognised one yields null, which the caller reads as "nothing remembered".
export function parseStoredRange(raw: string | null, latest: string): LayRateRange | null {
  if (raw === null) return null;
  const preset = RANGE_PRESETS.find((days) => String(days) === raw);
  if (preset !== undefined) return { kind: "preset", days: preset };
  const [from, to, ...rest] = raw.split(CUSTOM_SEPARATOR);
  if (rest.length > 0 || to === undefined) return null;
  return customRangeError(from, to, latest) === null ? { kind: "custom", from, to } : null;
}
