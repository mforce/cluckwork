import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { contrast, declaredKeys, resolveTokens, type Mode } from "./test/cssTokens";
import { BRANDS, DEFAULT_BRAND } from "./lib/brand";
import { GRADE_COLOURS } from "./lib/dashboard";

// #777 — the Dashboard's stock bar encodes egg grade as a HUE, and the hues are
// deliberately outside the farm-palette system. A farm's palette identifies the
// farm; these identify grades, and a grade that changed colour with the
// deployment's palette would make two farms' screenshots uncomparable.
//
// The set is also what replaced an opacity ramp that reached its floor at the
// sixth grade and gave a seventh and eighth the same fill, so "eight distinct
// values, in both modes" is the property that matters, not the specific hexes.
const CSS_REL = "./styles.css";
const css = readFileSync(fileURLToPath(new URL(CSS_REL, import.meta.url)), "utf8");
const MODES: Mode[] = ["light", "dark"];
const TOKENS = Array.from({ length: GRADE_COLOURS }, (_, i) => `--grade-${i + 1}`);

describe.each(MODES)("grade hues: %s", (mode) => {
  const resolved = resolveTokens(null, mode);

  it("declares one token per colour the code can emit", () => {
    // Driven off GRADE_COLOURS, which stockBar() cycles the index on, so adding
    // a ninth colour there without a ninth token fails here rather than
    // rendering a transparent band.
    for (const token of TOKENS) expect(resolved.get(token), token).toMatch(/^#[0-9a-f]{6}$/i);
  });

  it("declares them IN this mode's own base block, never by inheritance", () => {
    // The dark base is `:root[data-theme="dark"]`, so a token it omits resolves
    // to the LIGHT value rather than to nothing — a dark hue left on a dark
    // panel, with every contrast and distinctness check above still green. The
    // same trap DARK_REQUIRED in styles.test.ts exists for, and the mutation
    // that dropped --grade-8 from the dark block walked past this file until
    // this case was added.
    const declared = declaredKeys(null, mode);
    for (const token of TOKENS) expect(declared, `${mode} base`).toContain(token);
  });

  it("resolves all of them to distinct colours", () => {
    const values = TOKENS.map((t) => resolved.get(t));
    expect(new Set(values).size).toBe(GRADE_COLOURS);
  });

  it("keeps every hue clear of its own surface", () => {
    // A band 14px tall against the panel: the floor is the 3:1 non-text
    // contrast bar, which is what makes a sub-percent sliver findable at all.
    const surface = resolved.get("--surface")!;
    for (const token of TOKENS) expect(contrast(resolved.get(token)!, surface), token).toBeGreaterThanOrEqual(3);
  });

  it("is identical under every farm palette, which is the point of them", () => {
    for (const brand of BRANDS) {
      if (brand === DEFAULT_BRAND) continue;
      // Not redeclared anywhere brand-scoped...
      const declared = declaredKeys(brand, mode);
      for (const token of TOKENS) expect(declared, `${brand}/${mode}`).not.toContain(token);
      // ...and therefore resolves to the same value the base declares.
      const under = resolveTokens(brand, mode);
      for (const token of TOKENS) expect(under.get(token), `${brand}/${mode} ${token}`).toBe(resolved.get(token));
    }
  });
});

describe("grade hues: the classes that consume them", () => {
  it("styles every index stockBar can emit, on both the band and the swatch", () => {
    for (let i = 1; i <= GRADE_COLOURS; i++) {
      expect(css, `grade-${i}`).toContain(`.meter-stack > span.grade-${i}, .stock-ledger .swatch.grade-${i} { background: var(--grade-${i}); }`);
    }
  });

  it("styles no index it cannot emit, which would read as a colour that exists", () => {
    expect(css).not.toContain(`.grade-${GRADE_COLOURS + 1}`);
  });
});
