import { describe, it, expect } from "vitest";
import { contrast, declaredKeys, luminance, resolveTokens, type Mode } from "./test/cssTokens";
import { BRANDS, DEFAULT_BRAND } from "./lib/brand";

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

describe.each(BRANDS)("palette: %s", (brand) => {
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

  it.each(MODES)("%s: the login Forget glyph clears WCAG AA on its rest fill", (mode) => {
    // #587 — .auth-forget-farm draws its × over --surface-2 at rest. The
    // destructive FILL token (--danger) does not clear 4.5:1 for that glyph in
    // the dark theme (2.76:1 over aubergine's dark --surface-2), so the at-rest
    // colour is the TEXT token --error, which clears in every theme and
    // palette. The hover state fills with --danger and its white label is
    // checked here too, so a hover edit that darkened the fill cannot
    // silently break the pair.
    const t = resolveTokens(attrFor(brand), mode);
    const at = (k: string) => t.get(k)!;
    expect(contrast(at("--error"), at("--surface-2"))).toBeGreaterThanOrEqual(4.5);
    expect(contrast(at("--on-danger"), at("--danger"))).toBeGreaterThanOrEqual(4.5);
  });
});

// Key presence and contrast are both satisfied by a palette that declares the
// WRONG colour — forest's dark block holding terracotta's accent would pass
// every other assertion in this file. These pin the intended values.
//
// EVERY brand-scoped token is pinned, not three representative ones: a golden
// set covering only --brand/--stat-accent/--focus leaves --auth-brand,
// --brand-press, --on-brand-mute, --tint-accent and the tinted neutrals free to
// hold another palette's value and still go green.
//
// --auth-bg is deliberately absent: it is a four-stop gradient, not a colour,
// and pinning its whole string here would duplicate the stylesheet rather than
// assert anything. The resolver still substitutes the var() inside it, so an
// unresolved reference there throws.
const GOLDEN: Record<string, { light: Record<string, string>; dark: Record<string, string> }> = {
  aubergine: {
    light: {
      "--brand": "#4a154b", "--brand-press": "#611f69", "--brand-tint": "#592466",
      "--on-brand": "#ffffff", "--on-brand-mute": "#d9bdde",
      "--stat-accent": "#4a154b", "--focus": "#4a154b", "--auth-brand": "#4a154b",
      "--tint-accent": "#f3e9f5", "--canvas": "#faf7fc", "--surface-2": "#f6f1f8",
      "--row-hover": "#faf5fc", "--lavender": "#f9f0ff",
    },
    dark: {
      "--brand": "#4a154b", "--brand-press": "#611f69", "--brand-tint": "#592466",
      "--on-brand": "#ffffff", "--on-brand-mute": "#d9bdde",
      "--stat-accent": "#e6c7ec", "--focus": "#e6c7ec", "--auth-brand": "#e6c7ec",
      "--tint-accent": "#33203a", "--canvas": "#17121a", "--surface-2": "#2b2231",
      "--row-hover": "#2b2231", "--lavender": "#241c2a",
    },
  },
  forest: {
    light: {
      "--brand": "#14432a", "--brand-press": "#1b5a38", "--brand-tint": "#1a5133",
      "--on-brand": "#ffffff", "--on-brand-mute": "#bcd9c6",
      "--stat-accent": "#14432a", "--focus": "#14432a", "--auth-brand": "#14432a",
      "--tint-accent": "#e6f2ea", "--canvas": "#f7fbf8", "--surface-2": "#eef6f1",
      "--row-hover": "#f5faf7", "--lavender": "#eefaf1",
    },
    dark: {
      "--brand": "#14432a", "--brand-press": "#1b5a38", "--brand-tint": "#1a5133",
      "--on-brand": "#ffffff", "--on-brand-mute": "#bcd9c6",
      "--stat-accent": "#a8dcbb", "--focus": "#a8dcbb", "--auth-brand": "#a8dcbb",
      "--tint-accent": "#16301f", "--canvas": "#111814", "--surface-2": "#1e2a22",
      "--row-hover": "#1e2a22", "--lavender": "#17241b",
    },
  },
  slate: {
    light: {
      "--brand": "#1b3a5c", "--brand-press": "#254e79", "--brand-tint": "#22456b",
      "--on-brand": "#ffffff", "--on-brand-mute": "#c0d4e6",
      "--stat-accent": "#1b3a5c", "--focus": "#1b3a5c", "--auth-brand": "#1b3a5c",
      "--tint-accent": "#e7eff7", "--canvas": "#f7f9fc", "--surface-2": "#eef3f9",
      "--row-hover": "#f5f8fc", "--lavender": "#eef4fb",
    },
    dark: {
      "--brand": "#1b3a5c", "--brand-press": "#254e79", "--brand-tint": "#22456b",
      "--on-brand": "#ffffff", "--on-brand-mute": "#c0d4e6",
      "--stat-accent": "#aecfeb", "--focus": "#aecfeb", "--auth-brand": "#aecfeb",
      "--tint-accent": "#182a3b", "--canvas": "#101519", "--surface-2": "#1d2731",
      "--row-hover": "#1d2731", "--lavender": "#16202a",
    },
  },
  terracotta: {
    light: {
      "--brand": "#6b2716", "--brand-press": "#8a3520", "--brand-tint": "#7d2f1c",
      "--on-brand": "#ffffff", "--on-brand-mute": "#eec3b3",
      "--stat-accent": "#6b2716", "--focus": "#6b2716", "--auth-brand": "#6b2716",
      "--tint-accent": "#f8eae4", "--canvas": "#fdf8f6", "--surface-2": "#f9efea",
      "--row-hover": "#fdf6f3", "--lavender": "#fdf0e9",
    },
    dark: {
      "--brand": "#6b2716", "--brand-press": "#8a3520", "--brand-tint": "#7d2f1c",
      "--on-brand": "#ffffff", "--on-brand-mute": "#eec3b3",
      "--stat-accent": "#f2b79c", "--focus": "#f2b79c", "--auth-brand": "#f2b79c",
      "--tint-accent": "#36211a", "--canvas": "#1a1210", "--surface-2": "#2e211c",
      "--row-hover": "#2e211c", "--lavender": "#251a16",
    },
  },
};

describe.each(BRANDS)("palette %s resolves to its intended colours", (brand) => {
  it.each(MODES)("%s", (mode) => {
    const tokens = resolveTokens(attrFor(brand), mode);
    for (const [token, expected] of Object.entries(GOLDEN[brand][mode]))
      expect(tokens.get(token)).toBe(expected);
  });
});

// Nothing brand-scoped may be left unpinned: a token added to a palette block
// without a golden entry would otherwise be free to hold any value.
it("pins every brand-scoped token a palette block can declare", () => {
  const pinned = new Set(Object.keys(GOLDEN[DEFAULT_BRAND].dark));
  for (const token of [...DARK_REQUIRED, ...LIGHT_REQUIRED])
    if (token !== "--auth-bg" && token !== "--auth-card-shadow")
      expect(pinned).toContain(token);
});
