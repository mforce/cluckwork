// web/src/components/DayStrip.styles.test.ts
//
// #829 — successor to `styles.test.ts`'s "dashboard surfaces (#654, INV-8)"
// block, which retired in this PR (D8: `#829` deletes the Dashboard's own
// `.capture-*`/`.stock-ledger`/`.dash-list`/`.panel-wide` rules, all styled
// through `sx` now). `DayStrip` and `StockBar` are KEPT components (D2 pair
// 21 — bespoke data marks with no MUI equivalent), and their CSS
// (`.trend-*`, `.daystrip`, `.day*`, `.tip*`, `.avgline`, `.meter-stack`)
// stays untouched, so the assertions ABOUT that CSS move here rather than
// disappearing with the block that used to carry them.
import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { literalColourIn } from "../test/cssTokens";

// Relative path held in a variable, not an inline literal: Vite's
// import-analysis plugin statically pattern-matches
// `new URL("literal", import.meta.url)` and rewrites it to a dev-server asset
// URL under the jsdom test environment, which fileURLToPath() then rejects as
// "not scheme file" (see src/test/cssTokens.ts).
const CSS_REL = "../styles.css";
const css = readFileSync(fileURLToPath(new URL(CSS_REL, import.meta.url)), "utf8")
  .replace(/\/\*[\s\S]*?\*\//g, "");
// Any rule that APPLIES to one of these surfaces, not only one whose selector
// starts with it: `.unrelated, .day:hover { … }` reaches the slot just as
// surely, and an anchored match would walk straight past it.
const TOUCHES = /(^|[\s,>+~])\.(trend[a-z-]*|daystrip|day|day-week|tip|tipdock|avgline|meter-stack)\b/;
const blocks = Array.from(css.matchAll(/([^{}]+)\{([^{}]*)\}/g))
  .map((m) => ({ selector: m[1].trim(), body: m[2] }))
  .filter((b) => TOUCHES.test(b.selector));
const bodyOf = (selector: string) => {
  const b = blocks.find((x) => x.selector === selector);
  expect(b, `${selector} must be declared`).toBeDefined();
  return b!.body;
};

describe("DayStrip / StockBar surfaces (#654, INV-8, #829)", () => {
  it("declares the day strip and stacked meter rules", () => {
    for (const s of [
      ".trend-scale", ".daystrip", ".day", ".day > i", ".day.on", ".day.on > i", ".day.on::after",
      ".day-partial > i", ".day.on.day-partial > i",
      ".day-week", ".avgline", ".tipdock", ".tip", ".trend-rule", ".trend-kpi",
      ".meter-stack", ".meter-stack > span",
    ])
      bodyOf(s);
    expect(blocks.length).toBeGreaterThanOrEqual(14);
  });

  it("carries no box-shadow, text-transform, transition, animation or literal colour on any of them", () => {
    for (const b of blocks) {
      expect(b.body, b.selector).not.toMatch(/box-shadow|text-transform|transition|animation/);
      // Enumerating colour syntaxes is a losing game (hex, rgb(), hsl(), named,
      // oklch(), color-mix()), so require the opposite: every colour-valued
      // declaration resolves through a token, or is one of the few keywords
      // that carry no colour of their own.
      for (const decl of b.body.split(";")) {
        const [rawProp, ...rest] = decl.split(":");
        const prop = rawProp.trim();
        const value = rest.join(":").trim();
        if (!value) continue;
        // Only properties that can carry a COLOUR. `border(-<side>)?` is the
        // shorthand; `border-radius` / `-width` / `-style` are not colours and
        // matching them here made a plain `3px 3px 0 0` look untokenised
        // (#777). `border-color` is already caught by the `-color$` branch, and
        // `border-image` is listed because it is the one other border longhand
        // that can carry one.
        if (!/(^|-)color$|^background(-color|-image)?$|^border(-(top|right|bottom|left|block|inline|image))?$|^stroke$|^fill$|^outline(-color)?$/.test(prop)) continue;
        const tokenised = value.includes("var(--")
          || /^(inherit|initial|unset|revert|none|transparent|currentColor)$/i.test(value);
        expect(tokenised, `${b.selector}: "${prop}: ${value}" must resolve through a token`).toBe(true);
        // Containing a token is necessary but not sufficient: a value can mix a
        // literal INTO one. `color-mix(in oklab, #ff0000 7%, var(--surface))`
        // passed the check above, and #777 introduced this stylesheet's first
        // color-mix, so the hole went from inert to live on a dashboard surface.
        // The first patch enumerated hex and four colour functions and missed
        // NAMED colours, so `color-mix(in oklab, red 7%, var(--surface))` still
        // walked through; literalColourIn strips the var() references and
        // inspects whatever is left.
        const literal = literalColourIn(value);
        expect(literal, `${b.selector}: "${prop}: ${value}" carries the literal colour "${literal}" beside its token`).toBeNull();
      }
    }
  });

  // #780 — the arrow under the day readout shipped 4/5ths hidden behind the box
  // it points from, and every guard here stayed green: the selector was
  // declared and its colours were tokenised, which is all this file used to
  // ask. Both facts that made it wrong are asserted now.
  //
  // A CSS triangle's visible wedge is its TOP border, so a bottom border is
  // 5px of invisible box between the wedge and what it points at — with
  // `bottom: 100%` pinning the box to the slot, that gap pushes the wedge up
  // behind the readout. The third side must be zero.
  it("points the day readout's arrow at the slot, with no bottom border to push it off", () => {
    const arrow = bodyOf(".day.on::after");
    expect(arrow).toMatch(/bottom:\s*100%/);
    // A visible wedge: the top border carries the ink, the sides are clear.
    expect(arrow).toMatch(/border-top-color:\s*var\(--ink\)/);
    expect(arrow).toMatch(/border-color:\s*transparent/);
    // Three values, the last a bare 0 — `5px 5px 0`. A fourth value, or a
    // single one, reinstates the bottom border and the arrow disappears again.
    const width = /border-width:\s*([^;]+)/.exec(arrow)?.[1].trim();
    expect(width, "border-width must be the three-value form").toBeDefined();
    const parts = width!.split(/\s+/);
    expect(parts).toHaveLength(3);
    expect(parts[2], "the bottom border must be 0 or the arrow hides behind the box").toBe("0");
    // Nothing may move it back up: `bottom: 100%` means a positive
    // `margin-bottom` pushes AWAY from the slot, which is how this shipped.
    expect(arrow).not.toMatch(/margin-bottom/);
  });

  // The selection ring must not be drawn inside the slot: a bar is 18px in a
  // ~20px slot and `.day.on > i` paints it the ring's own colour, so an inset
  // ring vanished on the peak day — the one most likely to be inspected.
  it("draws the selected day's ring outside the slot, clear of its own bar", () => {
    const offset = /outline-offset:\s*(-?[\d.]+)px/.exec(bodyOf(".day.on"))?.[1];
    expect(offset, "outline-offset must be declared in px").toBeDefined();
    expect(Number(offset)).toBeGreaterThan(0);
    // And inside the 4px inter-slot gap, so two adjacent rings cannot touch.
    expect(Number(offset)).toBeLessThan(2);
  });

  // A recorded day that produced nothing is a 2% bar, which is 1.58px on a 5rem
  // strip — the entire visible difference between "the flock laid nothing" and
  // "nobody looked". A percentage floor alone is not a legible mark.
  it("gives the shortest bar a pixel floor, not only a percentage one", () => {
    expect(bodyOf(".day > i")).toMatch(/min-height:\s*[3-9]px/);
  });

  it("keeps the production bar on the accent token and the grade meter unopinionated", () => {
    // Eggs per day is the farm's own measure, so the bar carries the brand
    // accent. Grade bands deliberately do NOT (see the --grade-N tokens in
    // styles.grades.test.ts): a grade must not change colour with the farm's
    // palette, so the shared track rule itself declares no background.
    expect(bodyOf(".day > i")).toMatch(/background:\s*var\(--stat-accent\)/);
    expect(bodyOf(".meter-stack > span")).not.toMatch(/background/);
  });
});
