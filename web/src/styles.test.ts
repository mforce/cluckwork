import { describe, it, expect } from "vitest";
import { contrast, declaredKeys, luminance, resolveTokens, type Mode } from "./test/cssTokens";
// BRANDS itself isn't imported yet: the loop below is narrowed to
// [DEFAULT_BRAND] until Task 5 lands the other palettes' CSS, and an unused
// import fails the strict noUnusedLocals typecheck gate. Task 5 re-adds it
// alongside widening the loop.
import { DEFAULT_BRAND } from "./lib/brand";

// Non-default palettes carry a data-brand attribute; the default carries none.
const attrFor = (brand: string) => (brand === DEFAULT_BRAND ? null : brand);
const MODES: Mode[] = ["light", "dark"];

// The dark base declares these as LITERAL colours, breaking the var() chain
// that carries the palette through in light. Every palette's dark block must
// therefore redeclare all of them — the omission this set exists to catch is a
// forest farm rendering aubergine-pink accents at night.
const DARK_REQUIRED = [
  "--stat-accent", "--auth-brand", "--tint-accent", "--canvas",
  "--surface-2", "--row-hover", "--lavender", "--auth-bg", "--auth-card-shadow",
];

// The light base's brand-scoped LITERALS: the brand family plus the
// accent-tinted neutrals and auth surfaces. --stat-accent, --focus and
// --auth-brand are var() aliases in light, so they are carried automatically
// and are deliberately absent here. The brand family appears here and NOT in
// DARK_REQUIRED because the dark base does not redeclare it — one declaration
// in the light block covers both modes.
const LIGHT_REQUIRED = [
  "--brand", "--brand-press", "--brand-tint", "--on-brand", "--on-brand-mute",
  "--tint-accent", "--canvas", "--surface-2", "--row-hover", "--lavender",
  "--auth-bg", "--auth-card-shadow",
];

describe("design tokens: the resolver itself", () => {
  it("resolves a var() alias to a concrete colour", () => {
    // --focus: var(--stat-accent) and, in light, --stat-accent: var(--brand).
    // A two-hop chain, which is exactly what raw text parsing cannot do.
    const light = resolveTokens(null, "light");
    expect(light.get("--brand")).toBe("#4a154b");
    expect(light.get("--stat-accent")).toBe("#4a154b");
    expect(light.get("--focus")).toBe("#4a154b");
  });

  it("applies the dark base over the light base", () => {
    const dark = resolveTokens(null, "dark");
    expect(dark.get("--stat-accent")).toBe("#e6c7ec");
    // Not redeclared in dark — inherited from :root, which is what lets a
    // palette set the brand fill once and have it apply in both modes.
    expect(dark.get("--brand")).toBe("#4a154b");
  });
});

// Task 5 widens this to BRANDS once the other palettes exist.
describe.each([DEFAULT_BRAND])("palette: %s", (brand) => {
  it("light block declares every brand-scoped literal the light base declares", () => {
    if (brand === DEFAULT_BRAND) return; // the default IS the base
    const declared = declaredKeys(brand, "light");
    for (const token of LIGHT_REQUIRED) expect(declared).toContain(token);
  });

  it("dark block declares every brand-scoped literal the dark base declares", () => {
    if (brand === DEFAULT_BRAND) return;
    const declared = declaredKeys(brand, "dark");
    // Omitting one here silently falls back to the aubergine dark value.
    for (const token of DARK_REQUIRED) expect(declared).toContain(token);
  });

  it.each(MODES)("%s: brand fill stays dark enough for the sidebar overlay", (mode) => {
    // .sidebar nav a layers white at 7-11% over the brand fill (a deliberate
    // hardcode, not a token). Too light a fill and hover/active stop reading.
    const tokens = resolveTokens(attrFor(brand), mode);
    expect(luminance(tokens.get("--brand")!)).toBeLessThanOrEqual(0.18);
  });

  it.each(MODES)("%s: accent pairs clear WCAG AA", (mode) => {
    const t = resolveTokens(attrFor(brand), mode);
    const at = (k: string) => t.get(k)!;

    // Text on the brand fill, including muted text on a PRESSED fill — the
    // tightest real pair (terracotta 5.01) and the one a new palette is most
    // likely to fail.
    expect(contrast(at("--on-brand"), at("--brand"))).toBeGreaterThanOrEqual(4.5);
    expect(contrast(at("--on-brand-mute"), at("--brand"))).toBeGreaterThanOrEqual(4.5);
    expect(contrast(at("--on-brand"), at("--brand-press"))).toBeGreaterThanOrEqual(4.5);
    expect(contrast(at("--on-brand-mute"), at("--brand-press"))).toBeGreaterThanOrEqual(4.5);

    // Accent-on-surface, and the badge fill it sits in.
    expect(contrast(at("--stat-accent"), at("--surface"))).toBeGreaterThanOrEqual(4.5);
    expect(contrast(at("--stat-accent"), at("--tint-accent"))).toBeGreaterThanOrEqual(4.5);

    // --focus is var(--stat-accent) and is never redeclared per palette, so it
    // is checked against every background it can land on.
    for (const bg of ["--surface", "--surface-2", "--canvas"])
      expect(contrast(at("--focus"), at(bg))).toBeGreaterThanOrEqual(4.5);
  });
});
