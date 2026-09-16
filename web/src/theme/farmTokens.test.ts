import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { resolveTokens, type Mode } from "../test/cssTokens";
import { BRANDS, DEFAULT_BRAND } from "../lib/brand";
import { THEME_TOKENS, pixelsFrom, type ThemeToken, type TokenValues } from "./farmTokens";
import { createFarmTheme } from "./FarmThemeProvider";

const MODES: Mode[] = ["light", "dark"];
// The default palette carries no data-brand attribute; the others do. Same
// convention as styles.test.ts, which is the guard this file sits beside.
const attrFor = (brand: string) => (brand === DEFAULT_BRAND ? null : brand);

/** Resolve the real stylesheet for one palette × mode into the bridge's shape. */
export function tokensFor(brand: string, mode: Mode): TokenValues {
  const resolved = resolveTokens(attrFor(brand), mode);
  // Seeded from the token list itself rather than from a second literal: the
  // list grows every time a slice bridges another token, and a hand-kept copy
  // here would be a second source of truth for what the bridge carries.
  const out = Object.fromEntries(
    THEME_TOKENS.map((token) => {
      const value = resolved.get(token);
      expect(value, `${token} must resolve for ${brand}/${mode}`).toBeDefined();
      return [token, value ?? ""];
    }),
  ) as Record<ThemeToken, string>;
  return out;
}

describe("MUI theme bridge (#674)", () => {
  // The point of the bridge. If this fails, MUI is being handed a colour from
  // somewhere other than the stylesheet, which is the second-source-of-truth
  // failure the whole design exists to avoid.
  it("carries every farm palette and mode through to MUI's palette", () => {
    for (const brand of BRANDS) {
      for (const mode of MODES) {
        const tokens = tokensFor(brand, mode);
        const theme = createFarmTheme(tokens, mode);
        expect(theme.palette.primary.main, `${brand}/${mode} primary`)
          .toBe(tokens["--brand"]);
        expect(theme.palette.background.paper).toBe(tokens["--surface"]);
        expect(theme.palette.text.primary).toBe(tokens["--ink"]);
        expect(theme.palette.mode).toBe(mode);
      }
    }
  });

  // createTheme derives hover/selected/disabled states by parsing each colour
  // with alpha(). An unparseable value throws HERE rather than at the first
  // hover in production, which is the whole reason the bridge resolves tokens
  // to concrete strings instead of passing `var(--brand)` through.
  it("produces derived states for every palette without throwing", () => {
    for (const brand of BRANDS) {
      for (const mode of MODES) {
        const theme = createFarmTheme(tokensFor(brand, mode), mode);
        expect(theme.palette.primary.light).toMatch(/^(#|rgb)/);
        expect(theme.palette.primary.dark).toMatch(/^(#|rgb)/);
        expect(theme.palette.action.hover).toMatch(/^(#|rgb)/);
      }
    }
  });

  // Two farms must not render the same accent — the guard against a bridge
  // that silently resolves everything to the default palette.
  it("gives each farm palette a distinct primary", () => {
    const primaries = BRANDS.map(
      (brand) => createFarmTheme(tokensFor(brand, "light"), "light").palette.primary.main,
    );
    expect(new Set(primaries).size).toBe(BRANDS.length);
  });

  // The jsdom fallback exists so component tests can render; it must never
  // become a palette of its own. Every token it names has to be a token the
  // stylesheet actually declares, so a rename in styles.css surfaces here
  // instead of leaving a stale literal quietly serving tests.
  it("names only tokens the stylesheet declares", () => {
    const css = readFileSync(resolve(process.cwd(), "src/styles.css"), "utf8");
    for (const token of THEME_TOKENS) {
      expect(css, `${token} is not declared in styles.css`).toContain(`${token}:`);
    }
  });

  it.each([
    ["12px", 12],
    ["0.5rem", 0.5],
    ["", 99],
    ["auto", 99],
  ])("pixelsFrom(%s)", (value, expected) => {
    expect(pixelsFrom(value, 99)).toBe(expected);
  });
});
