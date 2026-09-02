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

export interface SparklineData { points: string; values: number[]; min: number; max: number; last: number }

// Fixed viewBox "0 0 100 32", y inverted (0 at the top). x spreads the points
// across the full width; y scales to the maximum, so a run of zeros is a flat
// baseline at y = 32 rather than a division by zero. Coordinates are rounded
// to 1 dp so the polyline string is stable across engines.
const SPARK_W = 100;
const SPARK_H = 32;
const r1 = (n: number) => Math.round(n * 10) / 10;

export function sparkline(days: ProductionDay[]): SparklineData {
  const values = days.map((d) => d.totalEggs);
  if (values.length === 0) return { points: "", values, min: 0, max: 0, last: 0 };
  const max = Math.max(...values);
  const min = Math.min(...values);
  const step = values.length > 1 ? SPARK_W / (values.length - 1) : 0;
  const points = values
    .map((v, i) => `${r1(i * step)},${r1(max > 0 ? SPARK_H - (v / max) * SPARK_H : SPARK_H)}`)
    .join(" ");
  return { points, values, min, max, last: values[values.length - 1] };
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

export interface StockSegment { eggGradeId: string; gradeName: string; available: number; pct: number; opacity: number }
export interface StockBarData { segments: StockSegment[]; totalAvailable: number; totalRestricted: number }

// The same reduce StockPage uses for its total, so the bar and the Stock
// screen never disagree. Opacity steps down by index so N grades stay
// distinguishable in every palette without a literal colour (floor 0.35).
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
      opacity: Math.max(0.35, Math.round((1 - 0.13 * i) * 100) / 100),
    }));
  return { segments, totalAvailable, totalRestricted };
}
