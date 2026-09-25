// web/src/components/DayStrip.focus.test.ts
//
// #941 — keyboard focus on a SELECTED day, which is the only state that
// matters here because focusing a slot also selects it.
//
// Two things were wrong and only one of them was the colour. `.day.on` is
// (0,2,0) and the global `:focus-visible` rule is (0,1,0), so a focused day
// drew the ink ring a HOVERED day draws and there was no focus indicator at
// all. And the accent the global rule would have drawn is 1.20-1.55 against
// the `--ink` a selected bar is filled with, in every brand and both themes,
// so simply raising its specificity would have put a near-invisible ring
// beside a near-black (or near-white) block. The indicator is two-toned for
// that reason, and this file measures both tones.
import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { BRANDS, DEFAULT_BRAND } from "../lib/brand";
import { contrast, resolveTokens } from "../test/cssTokens";
import type { Mode } from "../test/cssTokens";

const CSS_REL = "../styles.css";
const css = readFileSync(fileURLToPath(new URL(CSS_REL, import.meta.url)), "utf8")
  .replace(/\/\*[\s\S]*?\*\//g, "");
const bodyOf = (selector: string) => {
  const match = Array.from(css.matchAll(/([^{}]+)\{([^{}]*)\}/g))
    .find((m) => m[1].trim() === selector);
  expect(match, `${selector} must be declared`).toBeDefined();
  return match![2];
};

const attrFor = (brand: string) => (brand === DEFAULT_BRAND ? null : brand);
const MODES: Mode[] = ["light", "dark"];
// WCAG 2.2 SC 1.4.11 / 2.4.13: a focus indicator needs 3:1 against every
// colour it touches.
const MIN = 3;

describe("keyboard focus on a selected day (#941)", () => {
  it("out-specifies the selection ring, which is what hid it", () => {
    // The ordering claim: `.day.on:focus-visible` is three classes plus a
    // pseudo-class, so it cannot lose to `.day.on` however the file is
    // reordered. A rule written as `.day:focus-visible` would tie and depend
    // on source order.
    expect(css).toMatch(/\.day\.on:focus-visible\s*\{/);
    const ring = bodyOf(".day.on:focus-visible");
    expect(ring).toMatch(/outline:\s*2px solid var\(--focus\)/);
    // Outside the slot, in the gap, so it never lands on the bar it rings.
    const offset = /outline-offset:\s*(-?[\d.]+)px/.exec(ring)?.[1];
    expect(Number(offset)).toBeGreaterThan(0);
  });

  it("puts a second tone against the bar, because the accent cannot be read there", () => {
    expect(bodyOf(".day.on:focus-visible > i")).toMatch(/outline:\s*2px solid var\(--surface\)/);
  });

  describe.each(BRANDS)("%s", (brand) => {
    it.each(MODES)("%s: every adjacency of the indicator clears 3:1", (mode) => {
      const tokens = resolveTokens(attrFor(brand), mode);
      const focus = tokens.get("--focus")!;
      const ink = tokens.get("--ink")!;
      const surface = tokens.get("--surface")!;
      // Outward from the selected bar: ink, the surface tone hugging it, the
      // accent ring, the card behind it.
      expect(contrast(ink, surface), `${brand}/${mode} bar vs inner tone`).toBeGreaterThanOrEqual(MIN);
      expect(contrast(surface, focus), `${brand}/${mode} inner tone vs ring`).toBeGreaterThanOrEqual(MIN);
      expect(contrast(focus, surface), `${brand}/${mode} ring vs card`).toBeGreaterThanOrEqual(MIN);
    });

    it.each(MODES)("%s: the accent alone could not have done it", (mode) => {
      const tokens = resolveTokens(attrFor(brand), mode);
      // Recorded rather than asserted as a target: this is the measurement
      // that makes the second tone load-bearing rather than decorative. If a
      // future palette ever clears 3:1 here, this fails and the inner tone
      // can be reconsidered on the evidence.
      expect(contrast(tokens.get("--focus")!, tokens.get("--ink")!)).toBeLessThan(MIN);
    });
  });
});
