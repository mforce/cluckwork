import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import postcss from "postcss";
import type { Rule } from "postcss";
import { contrast, declaredKeys, deltaE, resolveTokens, type Mode } from "./test/cssTokens";
import { BRANDS, DEFAULT_BRAND } from "./lib/brand";
import { GRADE_COLOURS } from "./lib/dashboard";

// #777 — the Dashboard's stock bar encodes egg grade as a HUE, and the hues are
// deliberately outside the farm-palette system. A farm's palette identifies the
// farm; these identify grades, and a grade that changed colour with the
// deployment's palette would make two farms' screenshots uncomparable.
//
// The first version of this file asserted `new Set(values).size` and the
// presence of a rule's TEXT, and both were vacuous: `--grade-8: #2076a1` beside
// `--grade-1: #2076a0` passed, and so did appending a rule that repainted every
// band one colour. It measures perceptual distance and cascade now.
const CSS_REL = "./styles.css";
const css = readFileSync(fileURLToPath(new URL(CSS_REL, import.meta.url)), "utf8");
const root = postcss.parse(css);
const MODES: Mode[] = ["light", "dark"];
const TOKENS = Array.from({ length: GRADE_COLOURS }, (_, i) => `--grade-${i + 1}`);

// The floor is set from the failures that produced it, not from a standard.
// The first draft shipped grade-6 at dE 4.3 from --success and grade-5 at 11.9
// from --warn (light), and grade-4 at 19.0 from --error (dark) — a stock band
// reading as a status signal on a screen that shows both. 20 excludes every one
// of those and is met by the palette with room.
const MIN_DELTA_E = 20;

// Palette-independent. Farm palettes redeclare --stat-accent but not these.
const SEMANTIC = ["--success", "--warn", "--error", "--danger"];

describe.each(MODES)("grade hues: %s", (mode) => {
  const resolved = resolveTokens(null, mode);
  const hues = TOKENS.map((t) => resolved.get(t)!);

  it("declares one token per colour the code can emit", () => {
    // Driven off GRADE_COLOURS, which stockBar() cycles the index on, so adding
    // a ninth colour there without a ninth token fails here rather than
    // rendering a band with no background at all.
    for (const token of TOKENS) expect(resolved.get(token), token).toMatch(/^#[0-9a-f]{6}$/i);
  });

  it("declares them IN this mode's own base block, never by inheritance", () => {
    // The dark base is `:root[data-theme="dark"]`, so a token it omits resolves
    // to the LIGHT value rather than to nothing — a dark hue left on a dark
    // panel, with every distinctness and contrast check below still green. The
    // same trap DARK_REQUIRED in styles.test.ts exists for, and a mutation
    // dropping --grade-8 from the dark block walked past this file until this
    // case was added.
    const declared = declaredKeys(null, mode);
    for (const token of TOKENS) expect(declared, `${mode} base`).toContain(token);
  });

  it("keeps every pair perceptibly apart, which is the whole job of the encoding", () => {
    for (let i = 0; i < hues.length; i++) {
      for (let j = i + 1; j < hues.length; j++) {
        const d = deltaE(hues[i], hues[j]);
        expect(d, `${TOKENS[i]} ${hues[i]} vs ${TOKENS[j]} ${hues[j]}`).toBeGreaterThanOrEqual(MIN_DELTA_E);
      }
    }
  });

  it("keeps every hue clear of the SEMANTIC colours, so a band never reads as a status", () => {
    // The Dashboard shows the stock bar beside status badges tinted with these.
    for (const [i, hue] of hues.entries()) {
      for (const token of SEMANTIC) {
        const d = deltaE(hue, resolved.get(token)!);
        expect(d, `${TOKENS[i]} ${hue} vs ${token} ${resolved.get(token)}`).toBeGreaterThanOrEqual(MIN_DELTA_E);
      }
    }
  });

  it("keeps every hue clear of EVERY farm palette's accent, not just the default's", () => {
    // --stat-accent is the one colour on this screen that moves per palette, so
    // a hue safe on aubergine can collide on terracotta.
    for (const brand of BRANDS) {
      const accent = resolveTokens(brand === DEFAULT_BRAND ? null : brand, mode).get("--stat-accent")!;
      for (const [i, hue] of hues.entries()) {
        expect(deltaE(hue, accent), `${TOKENS[i]} ${hue} vs ${brand} --stat-accent ${accent}`)
          .toBeGreaterThanOrEqual(MIN_DELTA_E);
      }
    }
  });

  it("clears both surfaces a band actually sits on", () => {
    // --surface is the panel; --surface-2 is the meter track showing through
    // the 2px gaps. The floor is the 3:1 non-text contrast bar, which is what
    // makes a sub-percent sliver findable at all.
    for (const surface of ["--surface", "--surface-2"]) {
      for (const [i, hue] of hues.entries()) {
        expect(contrast(hue, resolved.get(surface)!), `${TOKENS[i]} on ${surface}`).toBeGreaterThanOrEqual(3);
      }
    }
  });

  it("is identical under every farm palette, which is the point of them", () => {
    for (const brand of BRANDS) {
      if (brand === DEFAULT_BRAND) continue;
      const declared = declaredKeys(brand, mode);
      for (const token of TOKENS) expect(declared, `${brand}/${mode}`).not.toContain(token);
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

  it("lets nothing else paint a band, so the rules above actually win the cascade", () => {
    // Asserting a rule's TEXT is present says nothing about whether it applies:
    // appending `.meter-stack > span[class] { background: var(--error); }` made
    // every band one colour with this file green. Any rule that can reach a band
    // and sets a background must be one of the eight.
    //
    // Scope, stated honestly: this sees selectors naming .meter-stack. A rule
    // reaching a band without naming it (`.panel span { background: … }`) is
    // outside it, and only a browser catches that.
    const offenders: string[] = [];
    root.walkRules((rule: Rule) => {
      // The track itself legitimately paints `--surface-2`; only rules reaching
      // a band inside it are in scope.
      if (!/\.meter-stack\s*[>\s]/.test(rule.selector)) return;
      if (/^\.meter-stack > span\.grade-\d, \.stock-ledger \.swatch\.grade-\d$/.test(rule.selector)) return;
      rule.walkDecls(/^background(-color|-image)?$/, (d) => { offenders.push(`${rule.selector} { ${d.prop}: ${d.value} }`); });
    });
    expect(offenders).toEqual([]);
  });
});
