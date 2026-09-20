import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import postcss from "postcss";
import type { Rule } from "postcss";
import { BRANDS, DEFAULT_BRAND } from "./lib/brand";
import { contrast, resolveTokens, type Mode } from "./test/cssTokens";

// Resolve token contrast per brand and mode because jsdom does not resolve custom properties.
const css = readFileSync(resolve(process.cwd(), "src/styles.css"), "utf8");
const root = postcss.parse(css);

function declarationsFor(selector: string): Map<string, string> {
  const decls = new Map<string, string>();
  root.walkRules((rule: Rule) => {
    const selectors = rule.selectors.map((s) => s.replace(/\/\*[\s\S]*?\*\//g, "").trim());
    if (!selectors.includes(selector)) return;
    rule.walkDecls((d) => { decls.set(d.prop, d.value); });
  });
  return decls;
}

const MODES: Mode[] = ["light", "dark"];
const attrFor = (brand: string) => (brand === DEFAULT_BRAND ? null : brand);

describe("the confirmation discount-breakdown tint is real in every brand and mode", () => {
  const token = "--tint-warn";

  describe.each(BRANDS.flatMap((brand) => MODES.map((mode) => [brand, mode] as const)))(
    "%s / %s",
    (brand, mode) => {
      const resolved = resolveTokens(attrFor(brand), mode);

      it("resolves the tint to a real colour, not transparent", () => {
        const value = resolved.get(token);
        expect(value, `${token} is not declared for ${brand}/${mode}`).toBeDefined();
        expect(value!.trim().toLowerCase(), `${token} resolves to "${value}"`)
          .not.toMatch(/^(transparent|none|inherit|initial|unset)$/);
      });

      it("is visibly different from the surrounding surface", () => {
        const tint = resolved.get(token)!;
        const surface = resolved.get("--surface") ?? resolved.get("--bg") ?? "#ffffff";
        // Not an accessibility threshold — a sameness check. Identical colours
        // give exactly 1.0, and that is the regression: a "tint" that tints
        // nothing. Anything genuinely different clears this comfortably.
        expect(contrast(tint, surface), `tint ${tint} vs surface ${surface}`).toBeGreaterThan(1.02);
      });
    },
  );
});

describe(".discount-note", () => {
  const decls = declarationsFor(".discount-note");

  it("wraps, because it renders prose inside a right-aligned money cell which never wraps", () => {
    expect(decls.get("white-space")).toBe("normal");
  });
});

describe("below-list exception text", () => {
  const decls = declarationsFor(".discount");
  it("uses warning text without a chip fill", () => {
    expect(decls.get("color")).toBe("var(--warn)");
    expect(decls.has("background")).toBe(false);
    expect(decls.has("background-color")).toBe(false);
  });
  it.each(BRANDS.flatMap(brand => MODES.map(mode => [brand, mode] as const)))(
    "is readable on the %s / %s paper", (brand, mode) => {
      const resolved = resolveTokens(attrFor(brand), mode);
      expect(contrast(resolved.get("--warn")!, resolved.get("--surface")!)).toBeGreaterThanOrEqual(4.5);
    });
});

// Over-ceiling warnings retain their destructive chip on the plain paper.
describe(".badge-danger — over-maximum remains distinct from below-list text", () => {
  const chip = declarationsFor(".badge-danger");
  const paperToken = "--surface";

  it("fills from a token, like every other chip", () => {
    expect(chip.get("background")).toMatch(/^var\(--[a-z0-9-]+\)$/);
  });

  it("does not reuse the paper surface token", () => {
    const tok = (v: string | undefined) => /^var\((--[a-z0-9-]+)\)$/.exec(v ?? "")?.[1];
    expect(tok(chip.get("background"))).not.toBe(paperToken);
  });

  describe.each(BRANDS.flatMap((brand) => MODES.map((mode) => [brand, mode] as const)))(
    "%s / %s",
    (brand, mode) => {
      it("contrasts with the paper surface", () => {
        const resolved = resolveTokens(attrFor(brand), mode);
        const tok = (v: string | undefined) => /^var\((--[a-z0-9-]+)\)$/.exec(v ?? "")?.[1];
        const chipColour = resolved.get(tok(chip.get("background"))!);
        const paperColour = resolved.get(paperToken);
        expect(chipColour, "chip fill does not resolve").toBeDefined();
        expect(paperColour, "row tint does not resolve").toBeDefined();
        expect(contrast(chipColour!, paperColour!), `chip ${chipColour} vs paper ${paperColour}`)
          .toBeGreaterThan(1.02);
      });
    },
  );
});
