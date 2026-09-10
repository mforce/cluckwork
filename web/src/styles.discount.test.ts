import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import postcss from "postcss";
import type { Rule } from "postcss";
import { BRANDS, DEFAULT_BRAND } from "./lib/brand";
import { contrast, resolveTokens, type Mode } from "./test/cssTokens";

// #723 — the discount treatment's two stylesheet-only declarations. Neither is
// reachable from jsdom (it computes no layout), so without this file a mutant
// that deletes either one leaves the entire suite green — which is exactly what
// the driver's Phase 11 mutation M13 observed against the row tint.
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

// Three revisions of this guard asserted, in turn, that the value was a var(),
// then that it was a --tint-* var, then that the token was declared. Each was
// one level further from the only thing that matters — whether a discounted row
// LOOKS different from an undiscounted one — and each was defeated by a change
// one level further out. This resolves the token per brand and mode, the way
// styles.caps.test.ts already does, and asserts the thing itself.
describe("tr.discounted td — the row tint is real in every brand and mode", () => {
  const decls = declarationsFor("tr.discounted td");

  it("tints the row through a token", () => {
    expect(decls.get("background")).toMatch(/^var\(--[a-z0-9-]+\)$/);
  });

  const token = /^var\((--[a-z0-9-]+)\)$/.exec(decls.get("background") ?? "")?.[1];

  describe.each(BRANDS.flatMap((brand) => MODES.map((mode) => [brand, mode] as const)))(
    "%s / %s",
    (brand, mode) => {
      const resolved = resolveTokens(attrFor(brand), mode);

      it("resolves the tint to a real colour, not transparent", () => {
        const value = resolved.get(token!);
        expect(value, `${token} is not declared for ${brand}/${mode}`).toBeDefined();
        expect(value!.trim().toLowerCase(), `${token} resolves to "${value}"`)
          .not.toMatch(/^(transparent|none|inherit|initial|unset)$/);
      });

      it("is visibly different from the surface the row would otherwise have", () => {
        const tint = resolved.get(token!)!;
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

  it("wraps, because it renders prose inside td.num which never wraps", () => {
    expect(decls.get("white-space")).toBe("normal");
  });
});
