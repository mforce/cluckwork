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

describe("the discounted-row tint (DISCOUNTED_ROW_SX) is real in every brand and mode", () => {
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

      it("is visibly different from the surface the row would otherwise have", () => {
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

describe("the below-list chip's surface lift (DISCOUNTED_BADGE_SX) stays visible on the row it sits on", () => {
  const chipToken = "--surface";
  const rowToken = "--tint-warn";

  it("does not repaint the chip in the row's own tint", () => {
    expect(chipToken).not.toBe(rowToken);
  });

  describe.each(BRANDS.flatMap((brand) => MODES.map((mode) => [brand, mode] as const)))(
    "%s / %s",
    (brand, mode) => {
      it("resolves to a colour that contrasts with the row tint", () => {
        const resolved = resolveTokens(attrFor(brand), mode);
        const chipColour = resolved.get(chipToken);
        const rowColour = resolved.get(rowToken);
        expect(chipColour, "chip fill does not resolve").toBeDefined();
        expect(rowColour, "row tint does not resolve").toBeDefined();
        expect(contrast(chipColour!, rowColour!), `chip ${chipColour} vs row ${rowColour}`)
          .toBeGreaterThan(1.02);
      });
    },
  );
});

// An over-ceiling line is also below list, so its chip must contrast with the discounted row.
describe(".badge-danger — the over-maximum chip on the discounted row it always sits on", () => {
  const chip = declarationsFor(".badge-danger");
  const rowTintToken = "--tint-warn";

  it("fills from a token, like every other chip", () => {
    expect(chip.get("background")).toMatch(/^var\(--[a-z0-9-]+\)$/);
  });

  it("does not reuse the row tint's own token, which is what forced the below-list override", () => {
    const tok = (v: string | undefined) => /^var\((--[a-z0-9-]+)\)$/.exec(v ?? "")?.[1];
    expect(tok(chip.get("background"))).not.toBe(rowTintToken);
  });

  describe.each(BRANDS.flatMap((brand) => MODES.map((mode) => [brand, mode] as const)))(
    "%s / %s",
    (brand, mode) => {
      it("resolves to a colour that contrasts with the row tint", () => {
        const resolved = resolveTokens(attrFor(brand), mode);
        const tok = (v: string | undefined) => /^var\((--[a-z0-9-]+)\)$/.exec(v ?? "")?.[1];
        const chipColour = resolved.get(tok(chip.get("background"))!);
        const rowColour = resolved.get(rowTintToken);
        expect(chipColour, "chip fill does not resolve").toBeDefined();
        expect(rowColour, "row tint does not resolve").toBeDefined();
        expect(contrast(chipColour!, rowColour!), `chip ${chipColour} vs row ${rowColour}`)
          .toBeGreaterThan(1.02);
      });
    },
  );
});
