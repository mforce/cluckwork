// web/src/lib/dashboard.ts
//
// #654 — the Dashboard's data-shaping, kept pure so jsdom (which cannot lay
// out SVG) and the screen can share exact oracles. Nothing here sums report
// rows: the production report is computed server-side in one place
// (IReportQueries.cs — "pages/cards never re-derive or sum rows"), so the
// hen-day figure is the server's `periodHenDayPct` for each window, and the
// only arithmetic on report data is the difference between two such figures.
import type { DailyEntry, Flock, ProductionDay, ProductionReport, StockRow } from "../api/cluckwork";

export interface CaptureTile { flock: Flock; entry: DailyEntry | null }

// Voided entries vacate their day (#82): a voided row never stands in for the
// flock's entry — a day with only voided rows is "no entry yet". The FIRST
// non-voided row for a flock wins, which is the order a scan would have found
// it in.
function entriesByFlock(entries: DailyEntry[]): Map<string, DailyEntry> {
  const byFlock = new Map<string, DailyEntry>();
  for (const entry of entries) {
    if (entry.status === "Voided" || byFlock.has(entry.flockId)) continue;
    byFlock.set(entry.flockId, entry);
  }
  return byFlock;
}

// "no entry" is a missed-capture flag — only meaningful for active flocks.
// Depleted/archived flocks stay visible only if they do have an entry today.
// Missing tiles first (the alarm state), API order preserved inside each group.
//
// One pass to index the entries, then one lookup per flock. A scan per flock
// is O(flocks × entries), and both lists are now drained rather than capped at
// 500: a 10,000-house day would have cost up to 100 million comparisons on the
// main thread, with the reader already waiting on two full drains.
export function captureTiles(flocks: Flock[], entries: DailyEntry[]): CaptureTile[] {
  const byFlock = entriesByFlock(entries);
  const tiles = flocks
    .map((flock) => ({ flock, entry: byFlock.get(flock.id) ?? null }))
    .filter((t) => t.flock.status === "Active" || t.entry !== null);
  return [...tiles.filter((t) => t.entry === null), ...tiles.filter((t) => t.entry !== null)];
}

// #915 — one page of a panel's list, carrying the 1-based bounds its pager
// states ("Houses 1 to 6 of 101"). `page` is clamped rather than trusted: the
// list is refetched under a page number the reader chose against the previous
// one, and a page past the end would otherwise render an empty panel with no
// way back.
export interface PanelPage<T> {
  items: T[];
  page: number;
  pageCount: number;
  first: number;
  last: number;
  total: number;
}

export function panelPage<T>(items: T[], page: number, size: number): PanelPage<T> {
  const pageCount = Math.max(1, Math.ceil(items.length / size));
  const current = Math.min(Math.max(0, page), pageCount - 1);
  const start = current * size;
  const slice = items.slice(start, start + size);
  return {
    items: slice,
    page: current,
    pageCount,
    first: slice.length === 0 ? 0 : start + 1,
    last: start + slice.length,
    total: items.length,
  };
}

// Today's eggs across the farm — the sum the stat card used to show: every
// non-Voided entry, Drafts included (a Draft is captured, just not submitted).
export function todaysEggs(entries: DailyEntry[]): number {
  return entries.filter((e) => e.status !== "Voided").reduce((a, e) => a + e.totalEggs, 0);
}

// One slot per day in the window, whether or not that day has a figure (#777),
// and — since #780 — whether or not anyone recorded it, or only some of them.
//
// Why a strip rather than the line it replaces, stated at the strength the code
// supports: the line mapped a zero day to y = SPARK_H, the floor of the viewBox,
// so a zero WAS drawn as a drop rather than as a plateau. What it never drew was
// the floor itself or the top of the scale, so nothing on screen said the bottom
// meant zero rather than the window's own minimum, and a 3% swing and a 60% one
// made the same picture. It also interpolated between days, implying values
// between them that a daily count does not have.
//
// THREE states, as a discriminated union rather than flags beside a height. The
// two-state version of this shipped in the first draft and was wrong in the same
// direction the issue is about: a day where one house of three filed is not a
// low day, it is a day whose total is a FLOOR, and drawing it like a complete
// day asserts a drop in production that the farm's own records do not claim.
export const DAY_SLOT_KINDS = ["none", "unrecorded", "partial", "recorded"] as const;
type DaySlotKind = (typeof DAY_SLOT_KINDS)[number];

// The week hairline falls every seven days inside the strip.
export const WEEK_DAYS = 7;

export type DayStripSlot =
  // `none` is a day that owed no filing at all — before the first placement, or
  // after the last flock left. It is NOT a gap, and counting it as one made a
  // farm's first fortnight announce fourteen missing days.
  | { kind: Extract<DaySlotKind, "none">; date: string; weekBreak: boolean }
  | { kind: Extract<DaySlotKind, "unrecorded">; date: string; expectedFlocks: number; weekBreak: boolean }
  // `filedFlocks` is how many of the flocks that OWED a count filed one, which
  // is what the readout compares against `expectedFlocks`. It is not
  // `recordedFlocks`: a flock filing outside its lifecycle window is counted
  // there and answers for nobody's expectation.
  | { kind: Extract<DaySlotKind, "partial">; date: string; eggs: number; heightPct: number; filedFlocks: number; expectedFlocks: number; weekBreak: boolean }
  | { kind: Extract<DaySlotKind, "recorded">; date: string; eggs: number; heightPct: number; weekBreak: boolean };

export interface DayStripData {
  slots: DayStripSlot[];
  // The complete-day peak when one exists, else the largest partial total
  // (#916). `scale` names which pool it came from.
  max: number | null;
  // Over the COMPLETE days only — the days every house reported. A partial
  // day's total is a floor, so averaging it in drags the reference line down by
  // however many houses forgot, which is the same defect one layer up. Unlike
  // `max`, this never falls back to the partial pool — that would understate.
  average: number | null;
  // The average as a share of the peak, for the reference line. Separate from
  // `average` because the line is geometry and the figure is a count.
  averagePct: number | null;
  complete: number;
  partial: number;
  unrecorded: number;
  // "complete" is the ordinary rule; "partial" is #916's fallback when no day
  // is complete; "none" means nothing in the window was recorded.
  scale: "complete" | "partial" | "none";
}

const r1 = (n: number) => Math.round(n * 10) / 10;

// A bar is anchored at zero and its height is its share of the peak. Not a
// cropped axis: the panel answers "did production hold", and shortening the
// axis to dramatise a small swing would misstate the ratios between days.
//
// Every day with a figure floors at 2%, including one that produced no eggs —
// that stub at the baseline is the whole visible difference between "the flock
// laid nothing and someone said so" and "nobody looked". A day nobody recorded
// draws no bar at all, so the two can never render alike.
//
// The week break marks each seven-day boundary, which on the fourteen-day
// default falls exactly where it always has.
export function dayStrip({ days }: { days: ProductionDay[] }): DayStripData {
  const empty: DayStripData = {
    slots: [], max: null, average: null, averagePct: null,
    complete: 0, partial: 0, unrecorded: 0, scale: "none",
  };
  if (days.length === 0) return empty;

  // Completeness is `missingFlocks`, never a comparison of the two counts.
  // `recordedFlocks` counts any flock that filed and `expectedFlocks` counts the
  // flocks that owed a count, and those are different SETS: expected {A, B}
  // against filings {A, C} gives 2 and 2, so B's missing filing read as a
  // complete day and its shortfall went into Peak and Avg. The server compares
  // identities and reports the shortfall directly.
  const isComplete = (d: ProductionDay) => d.recordedFlocks > 0 && d.missingFlocks === 0;
  const complete = days.filter(isComplete).map((d) => d.totalEggs);
  const average = complete.length === 0
    ? null
    : r1(complete.reduce((a, v) => a + v, 0) / complete.length);
  // #916 — without this pool a window of only partial days had a null peak, so
  // every bar floored at 2% however they actually compared.
  const recorded = days.filter((d) => d.recordedFlocks > 0).map((d) => d.totalEggs);
  const scale: DayStripData["scale"] = complete.length > 0 ? "complete" : recorded.length > 0 ? "partial" : "none";
  const pool = complete.length > 0 ? complete : recorded;
  const max = pool.length === 0 ? null : Math.max(...pool);

  // Height is a share of `max`, whichever pool it came from. A partial day
  // can therefore exceed 100% — two big houses out of three can beat a quiet
  // complete day — so it is capped rather than allowed to overflow its slot.
  const height = (eggs: number) =>
    max !== null && max > 0 ? Math.min(100, Math.max(2, r1((eggs / max) * 100))) : 2;

  const slots: DayStripSlot[] = days.map((d, i) => {
    const weekBreak = i > 0 && i % WEEK_DAYS === 0;
    if (d.expectedFlocks === 0 && d.recordedFlocks === 0) {
      return { kind: "none", date: d.date, weekBreak };
    }
    if (d.recordedFlocks === 0) {
      return { kind: "unrecorded", date: d.date, expectedFlocks: d.expectedFlocks, weekBreak };
    }
    if (!isComplete(d)) {
      return {
        kind: "partial", date: d.date, eggs: d.totalEggs, heightPct: height(d.totalEggs),
        filedFlocks: d.expectedFlocks - d.missingFlocks, expectedFlocks: d.expectedFlocks, weekBreak,
      };
    }
    return { kind: "recorded", date: d.date, eggs: d.totalEggs, heightPct: height(d.totalEggs), weekBreak };
  });

  return {
    slots,
    max,
    average,
    // A window whose every complete day is zero has a real average of 0 and no
    // scale to place it on, so it gets no line rather than one on the floor.
    averagePct: average === null || max === null || max === 0 ? null : r1((average / max) * 100),
    complete: complete.length,
    partial: slots.filter((s) => s.kind === "partial").length,
    unrecorded: slots.filter((s) => s.kind === "unrecorded").length,
    scale,
  };
}

export interface HenDayTrend { current: number | null; previous: number | null; delta: number | null }

// Two server figures and their difference in percentage points, 1 dp. null
// (a window with no hen-days) propagates — never a zero.
export function henDayTrend(current: ProductionReport, previous: ProductionReport): HenDayTrend {
  const c = current.periodHenDayPct;
  const p = previous.periodHenDayPct;
  const delta = c === null || p === null ? null : r1(c - p);
  return { current: c, previous: p, delta };
}

// Grade is a CATEGORICAL dimension, so it gets a categorical encoding (#777):
// `colorIndex` picks one of this many distinct hues, declared in styles.css for
// both themes and deliberately independent of the farm's brand palette. Egg
// grades are user-editable and unbounded, so past this count the hues repeat —
// the ledger beside the bar, not the colour, is what names every grade. The
// opacity ramp this replaces reached its floor at the sixth grade and gave a
// seventh and eighth literally the same fill.
export const GRADE_COLOURS = 8;

export interface StockSegment { eggGradeId: string; gradeName: string; available: number; pct: number; colorIndex: number }
// `ledger` is every grade the stock read returned, in its own order; `segments`
// is the subset the BAR can draw (#950 review round 1). A grade at zero has no
// width, but it is exactly the grade a low-stock floor is about, so the text of
// record keeps naming it.
export interface StockBarData {
  segments: StockSegment[];
  ledger: StockSegment[];
  totalAvailable: number;
  totalRestricted: number;
}

// The same reduce StockPage uses for its total, so the bar and the Stock
// screen never disagree.
export function stockBar(rows: StockRow[]): StockBarData {
  const totalAvailable = rows.reduce((a, r) => a + r.available, 0);
  const totalRestricted = rows.reduce((a, r) => a + r.restricted, 0);
  // The hue comes from the grade's position in the FULL row set, before the
  // empty grades are dropped. Off the filtered index it would be positional
  // rather than identity-bearing: [Large, Medium, Small] gives 1, 2, 3, and the
  // day Large sells out the same farm's Medium becomes 1 and Small becomes 2 —
  // the same grade, a different colour, between two screenshots of one farm.
  // That is the defect the "not brand-scoped" rule above exists to prevent,
  // and it bites harder here because it needs only one sale, not two
  // deployments. The opacity ramp did want the filtered index (a compressed
  // ramp beats one with holes); a categorical encoding wants the stable one.
  const ledger = rows.map((r, i) => ({
    eggGradeId: r.eggGradeId,
    gradeName: r.gradeName,
    available: r.available,
    // An empty farm has no whole to take a share of.
    pct: totalAvailable > 0 ? r1((r.available / totalAvailable) * 100) : 0,
    colorIndex: (i % GRADE_COLOURS) + 1,
  }));
  return {
    segments: ledger.filter((seg) => seg.available > 0),
    ledger,
    totalAvailable,
    totalRestricted,
  };
}
