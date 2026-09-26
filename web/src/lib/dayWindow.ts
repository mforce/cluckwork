// web/src/lib/dayWindow.ts
//
// #941 — where the expanded Lay rate chart's scrolling window sits over its
// range. One pure answer, read from MEASURED pixels, so the box on the
// overview map, the two edge cues, the strip's tab stop and the "days shown"
// caption cannot each measure the DOM their own way and disagree.

// #912's slot, fixed at every range (owner, 2026-09-24): a bar never stretches
// to fill the column, so a 30-day chart draws the same bar as a 90-day one.
//
// The GAP is not a constant here, because the stylesheet narrows it on a phone
// and two declarations of one number disagreed at exactly 900px, where both
// media queries match. `ExpandedLayRate` reads it back off the strip; this is
// the desktop value it falls back to where there is no stylesheet to read.
export const DAY_SLOT_PX = 22;
export const DAY_GAP_PX = 4;

export interface StripMetrics {
  days: number;
  slot: number;
  gap: number;
  // The scroll region's own width — `clientWidth`, never `scrollWidth`. A
  // strip that fits still reports a few px of `scrollWidth` because the date
  // rule's end label overhangs its slot, and taking that for a clipped chart
  // offset the box and lit an edge cue on a chart hiding nothing (#941, lab
  // finding 3).
  viewport: number;
}

export interface DayWindow {
  fits: boolean;
  firstVisible: number;
  lastVisible: number;
  boxLeftPct: number;
  boxWidthPct: number;
  atStart: boolean;
  atEnd: boolean;
}

// What the strip actually lays out: n slots and n-1 gaps. The week hairline
// adds no margin in the expanded strip (styles.css `.lay-expand .day-week`),
// so this is the whole width — a 9px jog every seventh day would put 100px of
// drift into a 90-day range and every figure below with it.
export function stripWidth({ days, slot, gap }: StripMetrics): number {
  return days <= 0 ? 0 : days * slot + (days - 1) * gap;
}

const clamp = (n: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, n));
const pct = (n: number) => Math.round(n * 100) / 100;

export function dayWindow(metrics: StripMetrics, scrollLeft: number): DayWindow {
  const { days, slot, gap, viewport } = metrics;
  const width = stripWidth(metrics);
  const whole: DayWindow = {
    fits: true,
    firstVisible: 0,
    lastVisible: Math.max(0, days - 1),
    boxLeftPct: 0,
    boxWidthPct: 100,
    atStart: true,
    atEnd: true,
  };
  // An unmeasured region (0) is not a clipped one: before the first resize
  // observation there is nothing to say is hidden, and answering "clipped"
  // there would flash an edge cue on every open.
  if (viewport <= 0 || width <= viewport) return whole;

  const per = slot + gap;
  const max = width - viewport;
  const left = clamp(scrollLeft, 0, max);
  const firstVisible = clamp(Math.floor(left / per), 0, days - 1);
  const boxWidthPct = pct(Math.min(100, (viewport / width) * 100));
  return {
    fits: false,
    firstVisible,
    lastVisible: clamp(Math.ceil((left + viewport) / per) - 1, firstVisible, days - 1),
    boxLeftPct: pct(Math.min(100 - boxWidthPct, (left / width) * 100)),
    boxWidthPct,
    // A pixel of slack: a fling settles on a fractional scrollLeft, and an
    // exact comparison leaves the right-hand cue lit over nothing.
    atStart: left <= 1,
    atEnd: left >= max - 1,
  };
}
