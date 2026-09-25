// web/src/lib/dayWindow.test.ts
import { describe, it, expect } from "vitest";
import { DAY_GAP_PX, DAY_SLOT_PX, dayWindow, stripWidth } from "./dayWindow";

// 1280 desktop: the expanded panel leaves about 1,080px of scroll region
// beside its axis gutter. 90 days is 90x22 + 89x4 = 2,336px, so it scrolls;
// 30 days is 30x22 + 29x4 = 776px, so it does not.
const desktop = (days: number) => ({ days, slot: DAY_SLOT_PX, gap: DAY_GAP_PX, viewport: 1080 });

describe("stripWidth", () => {
  it("lays out n slots and n-1 gaps", () => {
    expect(stripWidth(desktop(90))).toBe(2336);
    expect(stripWidth(desktop(30))).toBe(776);
    expect(stripWidth(desktop(1))).toBe(22);
    expect(stripWidth(desktop(0))).toBe(0);
  });
});

describe("dayWindow (#941)", () => {
  it("decides 'fits' from the layout, never from a measured scrollWidth", () => {
    const w = dayWindow(desktop(30), 0);
    expect(w.fits).toBe(true);
    expect(w.firstVisible).toBe(0);
    expect(w.lastVisible).toBe(29);
    // A range that fits hides nothing, so neither edge cue may light and the
    // box covers the whole map.
    expect(w.atStart).toBe(true);
    expect(w.atEnd).toBe(true);
    expect(w.boxLeftPct).toBe(0);
    expect(w.boxWidthPct).toBe(100);
  });

  it("treats the end-label overhang as fitting, not as three px of clipped chart", () => {
    // 1,080px of region against a 1,062px strip: a real layout fit whose DOM
    // `scrollWidth` reports a few px more once the date rule's end label
    // overhangs its slot.
    expect(stripWidth(desktop(41))).toBe(1062);
    expect(dayWindow(desktop(41), 0).fits).toBe(true);
    // One more day crosses the line and the chart really does clip.
    expect(stripWidth(desktop(42))).toBe(1088);
    expect(dayWindow(desktop(42), 0).fits).toBe(false);
  });

  it("puts the box at the start of the map with the window at the start of the range", () => {
    const w = dayWindow(desktop(90), 0);
    expect(w).toEqual({
      fits: false,
      firstVisible: 0,
      lastVisible: 41,
      boxLeftPct: 0,
      boxWidthPct: 46.23,
      atStart: true,
      atEnd: false,
    });
  });

  it("moves the box with the window, and lights both cues in the middle", () => {
    const w = dayWindow(desktop(90), 628);
    expect(w.firstVisible).toBe(24);
    expect(w.lastVisible).toBe(65);
    expect(w.boxLeftPct).toBe(26.88);
    expect(w.boxWidthPct).toBe(46.23);
    expect(w.atStart).toBe(false);
    expect(w.atEnd).toBe(false);
  });

  it("ends the box flush with the map's right edge at full scroll", () => {
    const w = dayWindow(desktop(90), 2336 - 1080);
    expect(w.firstVisible).toBe(48);
    expect(w.lastVisible).toBe(89);
    expect(w.boxLeftPct).toBe(53.77);
    expect(w.boxLeftPct + w.boxWidthPct).toBe(100);
    expect(w.atStart).toBe(false);
    expect(w.atEnd).toBe(true);
  });

  it("clamps a scroll position past either end rather than running the box off the map", () => {
    expect(dayWindow(desktop(90), -400)).toEqual(dayWindow(desktop(90), 0));
    expect(dayWindow(desktop(90), 99999)).toEqual(dayWindow(desktop(90), 1256));
  });

  it("answers 'fits' for a region nobody has measured yet", () => {
    const w = dayWindow({ days: 90, slot: DAY_SLOT_PX, gap: DAY_GAP_PX, viewport: 0 }, 0);
    expect(w.fits).toBe(true);
    expect(w.atStart).toBe(true);
    expect(w.atEnd).toBe(true);
  });

  it("scrolls at every range on a phone, where the narrower gap still does not save it", () => {
    // 390x844 leaves roughly 300px of region. 30 days is 30x22 + 29x2 = 718px.
    const phone = { days: 30, slot: DAY_SLOT_PX, gap: 2, viewport: 300 };
    expect(stripWidth(phone)).toBe(718);
    const w = dayWindow(phone, 0);
    expect(w.fits).toBe(false);
    expect(w.firstVisible).toBe(0);
    expect(w.lastVisible).toBe(12);
  });
});
