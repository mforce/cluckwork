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
import { DAY_GAP_PHONE_PX, DAY_GAP_PX, DAY_SLOT_PX } from "../lib/dayWindow";

const CSS_REL = "../styles.css";
const css = readFileSync(fileURLToPath(new URL(CSS_REL, import.meta.url)), "utf8")
  .replace(/\/\*[\s\S]*?\*\//g, "");
const flat = (text: string) => text.trim().replace(/\s+/g, " ");
const bodyOf = (selector: string) => {
  const match = Array.from(css.matchAll(/([^{}]+)\{([^{}]*)\}/g))
    .find((m) => flat(m[1]) === flat(selector));
  expect(match, `${selector} must be declared`).toBeDefined();
  return match![2];
};
const value = (selector: string, property: string) => {
  const found = new RegExp(`(?:^|;)\\s*${property}:\\s*([^;]+)`).exec(bodyOf(selector))?.[1].trim();
  expect(found, `${selector} must declare ${property}`).toBeDefined();
  return found!;
};

describe("the expanded chart's slot geometry (#941)", () => {
  it("lays the strip out on the same slot and gap the window arithmetic assumes", () => {
    expect(value(".bigstrip", "--slot")).toBe(`${DAY_SLOT_PX}px`);
    expect(value(".bigstrip", "--gap")).toBe(`${DAY_GAP_PX}px`);
    // The date rule is a second row of the same slots, so it shares them.
    expect(value(".datebar", "--slot")).toBe(`${DAY_SLOT_PX}px`);
    expect(value(".datebar", "--gap")).toBe(`${DAY_GAP_PX}px`);
  });

  it("narrows the gap on a phone to the same figure the arithmetic narrows to", () => {
    expect(value(".bigstrip, .datebar", "--gap")).toBe(`${DAY_GAP_PHONE_PX}px`);
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

  it("drops the card's week jog, which `stripWidth` does not account for", () => {
    // 9px every seventh day is over 100px of drift across a quarter, and every
    // figure `dayWindow` returns would carry it.
    expect(value(".lay-expand .day-week", "margin-left")).toBe("0");
  });
});
