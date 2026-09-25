// web/src/components/ExpandedLayRate.styles.test.ts
//
// #941 — the expanded chart's geometry is stated twice on purpose: once in the
// stylesheet, which lays the strip out, and once in `lib/dayWindow.ts`, whose
// arithmetic places the overview box, the edge cues and the tab stop. They are
// the same three numbers, and nothing at runtime would notice them drifting —
// the box would simply sit a few percent off the window it claims to mark.
// This file is what notices.
import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { DAY_GAP_PX, DAY_SLOT_PX } from "../lib/dayWindow";

const CSS_REL = "../styles.css";
const css = readFileSync(fileURLToPath(new URL(CSS_REL, import.meta.url)), "utf8")
  .replace(/\/\*[\s\S]*?\*\//g, "");
const flat = (text: string) => text.trim().replace(/\s+/g, " ");
// The phone block is `@media (max-width: 900px)`, which declares its own copy
// of several of these rules; `{ phone: true }` reads that copy rather than the
// first one in the file.
const PHONE_BLOCK = /@media \(max-width: 900px\) \{([\s\S]*?)\n\}/;
const bodyOf = (selector: string, opts: { phone?: boolean } = {}) => {
  const source = opts.phone === true
    ? (PHONE_BLOCK.exec(css)?.[1] ?? expect.fail("the phone media block must exist"))
    : css.replace(PHONE_BLOCK, "");
  const match = Array.from(source.matchAll(/([^{}]+)\{([^{}]*)\}/g))
    .find((m) => flat(m[1]) === flat(selector));
  expect(match, `${selector} must be declared`).toBeDefined();
  return match![2];
};
const value = (selector: string, property: string, opts: { phone?: boolean } = {}) => {
  const found = new RegExp(`(?:^|;)\\s*${property}:\\s*([^;]+)`).exec(bodyOf(selector, opts))?.[1].trim();
  expect(found, `${selector} must declare ${property}`).toBeDefined();
  return found!;
};

describe("the expanded chart's slot geometry (#941)", () => {
  it("lays the strip out on the slot the window arithmetic assumes, and one gap for both rows", () => {
    expect(value(".bigstrip", "--slot")).toBe(`${DAY_SLOT_PX}px`);
    expect(value(".bigstrip", "--gap")).toBe(`${DAY_GAP_PX}px`);
    // The date rule is a second row of the same slots, so it shares them.
    expect(value(".datebar", "--slot")).toBe(`${DAY_SLOT_PX}px`);
    expect(value(".datebar", "--gap")).toBe(`${DAY_GAP_PX}px`);
  });

  it("narrows the gap on a phone for BOTH rows at once", () => {
    // The component reads the gap back off the strip, so no JS constant has to
    // match this one. What still has to hold is that the date rule narrows with
    // the bars: a row of labels laid out on a different pitch drifts off the
    // days it names.
    const phone = value(".bigstrip, .datebar", "--gap", { phone: true });
    expect(phone).toBe("2px");
    expect(phone).not.toBe(value(".bigstrip", "--gap"));
  });

  it("packs the days to the right, so the newest sits at the edge whatever the range", () => {
    // The owner's rule, 2026-09-24: bars are never stretched, and a range too
    // short to fill the region leaves its empty space before the OLDEST day.
    expect(bodyOf(".bigstrip")).toMatch(/justify-content:\s*flex-end/);
    expect(bodyOf(".bigstrip")).toMatch(/width:\s*max-content/);
    expect(bodyOf(".datebar")).toMatch(/justify-content:\s*flex-end/);
  });

  it("keeps the date rule's end label out of the region's scrollable width", () => {
    // Measured at 1280: a 41-day range that fits reported `scrollWidth` 1140
    // against `clientWidth` 1076, because the end label's layout box hangs
    // past the strip even though a transform paints it back inside. The chart
    // opened scrolled 64px past its own first bars.
    expect(value(".datebar", "overflow")).toBe("hidden");
  });

  // #958 review round 1, P3: the phone taps the map and never drags it, so
  // claiming both axes there killed a vertical swipe that happened to start on
  // the 34px strip — which on a short phone is how the column is scrolled.
  it("claims only the horizontal axis on the map where the phone cannot drag it", () => {
    expect(value(".daymap", "touch-action")).toBe("none");
    expect(value(".daymap", "touch-action", { phone: true })).toBe("pan-y");
  });

  // #958 review round 1, P2: a vertical pan inside either scroll region has
  // nothing to scroll there, so without this it chains to the document behind
  // and Chrome Android's pull-to-refresh can fire over an open chart.
  it("stops either scroll region chaining to the document behind it", () => {
    expect(value(".lay-expand", "overscroll-behavior")).toBe("contain");
    expect(value(".lay-expand-scroll", "overscroll-behavior")).toBe("contain");
  });

  // #958 review round 1, P2: the frame is a MUI `Modal` child now, so it takes
  // `theme.zIndex.modal` and the stylesheet carries no literal to drift.
  it("declares no z-index of its own, leaving the modal layer to the theme", () => {
    expect(bodyOf(".lay-expand-backdrop")).not.toMatch(/z-index/);
  });

  it("drops the card's week jog, which `stripWidth` does not account for", () => {
    // 9px every seventh day is over 100px of drift across a quarter, and every
    // figure `dayWindow` returns would carry it.
    expect(value(".lay-expand .day-week", "margin-left")).toBe("0");
  });
});
