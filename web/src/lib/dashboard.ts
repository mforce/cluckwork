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

// The grid shows at most this many tiles; the rest are one "N more flocks"
// link. Missing-first ordering (below) is what makes the cap safe: a house
// with no entry is never behind the link while 12 or fewer are missing.
export const TILE_CAP = 12;

// Voided entries vacate their day (#82): a voided row never stands in for the
// flock's entry — a day with only voided rows is "no entry yet".
const entryFor = (entries: DailyEntry[], flockId: string): DailyEntry | null =>
  entries.find((e) => e.flockId === flockId && e.status !== "Voided") ?? null;

// "no entry" is a missed-capture flag — only meaningful for active flocks.
// Depleted/archived flocks stay visible only if they do have an entry today.
// Missing tiles first (the alarm state), API order preserved inside each group.
export function captureTiles(flocks: Flock[], entries: DailyEntry[]): CaptureTile[] {
  const tiles = flocks
    .map((flock) => ({ flock, entry: entryFor(entries, flock.id) }))
    .filter((t) => t.flock.status === "Active" || t.entry !== null);
  return [...tiles.filter((t) => t.entry === null), ...tiles.filter((t) => t.entry !== null)];
}

export function visibleTiles(tiles: CaptureTile[]): { shown: CaptureTile[]; hidden: number } {
  return { shown: tiles.slice(0, TILE_CAP), hidden: Math.max(0, tiles.length - TILE_CAP) };
}

// Today's eggs across the farm — the sum the stat card used to show: every
// non-Voided entry, Drafts included (a Draft is captured, just not submitted).
export function todaysEggs(entries: DailyEntry[]): number {
  return entries.filter((e) => e.status !== "Voided").reduce((a, e) => a + e.totalEggs, 0);
}

// One slot per day in the window, whether or not that day has a figure (#777).
// `heightPct` is the bar's share of the tallest day; `recorded` is false for a
// day that produced nothing, which the strip draws as an empty slot rather than
// as a point on a flat line.
//
// KNOWN GAP (#777, tracked separately): the production report cannot yet say
// whether a day was ENTERED. `ReportQueries` walks `for (d = from; d <= to;
// d = d.AddDays(1))` and emits every calendar day with `total = row?.Total ?? 0`,
// so a day nobody recorded and a day that genuinely produced zero eggs arrive
// identical. `recorded: false` therefore means "no eggs", not "no entry", and
// nothing here may average over the window until the server can tell them apart.
export interface DayStripSlot { date: string; value: number; heightPct: number; recorded: boolean; weekBreak: boolean }
export interface DayStripData { slots: DayStripSlot[]; min: number; max: number; last: number }

const r1 = (n: number) => Math.round(n * 10) / 10;

// A bar is anchored at zero and its height is its share of the peak. Not a
// cropped axis: the panel answers "did production hold", and shortening the
// axis to dramatise a small swing would misstate the ratios between days.
// A recorded day floors at 2% so the shortest real day is still a bar.
// `recentCount` is the length of the LATER of the two windows the panel fetches,
// so the strip's divider falls exactly where the hen-day caption's comparison
// does. 0 (or the whole window) draws no divider.
export function dayStrip(days: ProductionDay[], recentCount = 0): DayStripData {
  if (days.length === 0) return { slots: [], min: 0, max: 0, last: 0 };
  const values = days.map((d) => d.totalEggs);
  const max = Math.max(...values);
  const breakAt = recentCount > 0 && recentCount < days.length ? days.length - recentCount : -1;
  const slots = days.map((d, i) => ({
    date: d.date,
    value: d.totalEggs,
    heightPct: d.totalEggs > 0 && max > 0 ? Math.max(2, r1((d.totalEggs / max) * 100)) : 0,
    recorded: d.totalEggs > 0,
    weekBreak: i === breakAt,
  }));
  return { slots, min: Math.min(...values), max, last: values[values.length - 1] };
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
// the legend beside the bar, not the colour, is what names every grade. The
// opacity ramp this replaces reached its floor at the sixth grade and gave a
// seventh and eighth literally the same fill.
export const GRADE_COLOURS = 8;

export interface StockSegment { eggGradeId: string; gradeName: string; available: number; pct: number; colorIndex: number }
export interface StockBarData { segments: StockSegment[]; totalAvailable: number; totalRestricted: number }

// The same reduce StockPage uses for its total, so the bar and the Stock
// screen never disagree.
export function stockBar(rows: StockRow[]): StockBarData {
  const totalAvailable = rows.reduce((a, r) => a + r.available, 0);
  const totalRestricted = rows.reduce((a, r) => a + r.restricted, 0);
  const segments = rows
    .filter((r) => r.available > 0)
    .map((r, i) => ({
      eggGradeId: r.eggGradeId,
      gradeName: r.gradeName,
      available: r.available,
      pct: r1((r.available / totalAvailable) * 100),
      colorIndex: (i % GRADE_COLOURS) + 1,
    }));
  return { segments, totalAvailable, totalRestricted };
}
