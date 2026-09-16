import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { contrast, declaredKeys, literalColourIn, luminance, resolveTokens, type Mode } from "./test/cssTokens";
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

// literalColourIn's own cases. The stylesheet scan below is a *sample*: it only
// exercises the branches the current CSS happens to reach, and no rule in it
// uses a var() fallback today, so the fallback branch had no permanent test and
// was only ever hit by a throwaway mutation. These call the helper the way the
// scan does and assert the literal it should name.
describe("literalColourIn", () => {
  it.each([
    ["var(--surface)", null],
    ["var(--a, var(--surface))", null],
    ["color-mix(in oklab, var(--stat-accent) 7%, transparent)", null],
    ["1px dashed var(--hairline)", null],
    ["linear-gradient(var(--canvas), var(--surface))", null],
    ["currentColor", null],
    ["none", null],
  ])("passes %s", (value, expected) => {
    expect(literalColourIn(value)).toBe(expected);
  });

  it.each([
    ["#ff0000", "#ff0000"],
    ["1px solid #abc", "#abc"],
    ["color-mix(in oklab, #ff0000 7%, var(--surface))", "#ff0000"],
    ["color-mix(in oklab, red 7%, var(--surface))", "red"],
    ["rebeccapurple", "rebeccapurple"],
    ["rgb(1 2 3)", "rgb("],
    ["hwb(0 0% 0%)", "hwb("],
    ["oklab(0.5 0.1 0.1)", "oklab("],
    ["color(display-p3 1 0 0)", "color("],
    // The fallback renders whenever the token is undefined, so it is a real
    // colour — and deleting it with the reference is exactly how it hid.
    ["var(--day-fill, red)", "red"],
    ["var(--a, #ff0000)", "#ff0000"],
    ["var(--a, var(--b, red))", "red"],
    ["var(--a, hwb(0 0% 0%))", "hwb("],
  ])("names the literal in %s", (value, expected) => {
    expect(literalColourIn(value)).toBe(expected);
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

  it.each(MODES)("%s: brand fill stays dark enough for on-brand text", (mode) => {
    // #829 — the aubergine nav-rail overlay this test named (`.sidebar nav a`
    // layering white at 7-11% over the brand fill) is gone: the rail is now
    // `--lavender` tinted paper, and `--brand` is left carrying the primary
    // button's fill and `--on-brand` its contrast text (checked directly
    // below). A dark ceiling on the fill is still the margin that keeps that
    // pairing — and any future light-overlay-on-brand surface — legible.
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

  // #834 — the Slack-blue link retirement (DIRECTION.md, owner decision
  // 2026-09-16): a link is --ink text underlined in a --link-rule (28% ink)
  // rule. Checked against every background a link can render on, not just
  // --surface: --surface-2 differs per palette (a toolbar or a help panel),
  // and --link/--link-rule themselves do not (--ink is theme-scoped only),
  // so this is the check that would catch a palette whose --surface-2 got
  // too close to ink.
  it.each(MODES)("%s: link text clears WCAG AA (4.5:1) on --surface and --surface-2", (mode) => {
    const t = resolveTokens(attrFor(brand), mode);
    const at = (k: string) => t.get(k)!;
    // Contrast alone would also pass a different, still-sufficiently-dark
    // blue (Codex review of #884, round 3) — the actual retirement claim is
    // that --link IS --ink now, not merely that whatever it is clears AA.
    expect(at("--link"), `${brand}/${mode} --link must equal --ink, not a separate colour`)
      .toBe(at("--ink"));
    for (const bg of ["--surface", "--surface-2"])
      expect(contrast(at("--link"), at(bg)), `${brand}/${mode} --link vs ${bg}`)
        .toBeGreaterThanOrEqual(4.5);
  });

  // The 28% ink rule is a decorative reinforcement under text that already
  // clears 4.5:1 above, not the sole means of identifying the link — WCAG
  // 1.4.11 (Non-text Contrast) targets UI-component boundaries and states
  // that ARE the only cue, and DIRECTION.md's own interactive state ("full
  // ink on hover and focus") already carries that job. So 3:1 is not applied
  // here: flattened over the surfaces it actually sits on, a literal 28%
  // ink cannot clear 3:1 against a near-white or near-black surface — the
  // measured worst case across all four palettes is ~1.6:1 light / ~2.1:1
  // dark, and reaching 3:1 would need roughly 48% (light) / 38% (dark) ink,
  // a materially bolder rule than DIRECTION.md specified. The floor below is
  // the honest measured minimum, not an invented pass, so a future change
  // that makes the rule fainter still gets caught.
  it.each(MODES)("%s: the 28% ink rule stays visibly above its surface (not a 3:1 pass — see comment)", (mode) => {
    const t = resolveTokens(attrFor(brand), mode);
    const at = (k: string) => t.get(k)!;
    // The contrast floor alone would also pass an unrelated colour above
    // 1.5:1 (Codex review of #884, round 3) — pin the actual flattened-28%-
    // ink value this decision computed, not just a property it happens to
    // have. --link-rule is theme-scoped only, same as --link/--ink.
    expect(at("--link-rule"), `${brand}/${mode} --link-rule value`)
      .toBe(mode === "light" ? "#c0c0c0" : "#5c5560");
    for (const bg of ["--surface", "--surface-2"])
      expect(contrast(at("--link-rule"), at(bg)), `${brand}/${mode} --link-rule vs ${bg}`)
        .toBeGreaterThanOrEqual(1.5);
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

// #834 (CodeRabbit round 1) — DIRECTION.md says full ink on hover AND focus;
// the global `:focus-visible` rule only adds an outline, so a rule that
// switches `text-decoration-color` on `:hover` alone leaves a keyboard-only
// visitor seeing the faint 28% rule instead of full ink. Checked against the
// raw stylesheet text rather than resolved tokens, because the defect is
// about which SELECTOR carries the declaration, not what the declaration
// resolves to — `resolveTokens` only sees `:root` blocks and cannot tell a
// `:hover`-only rule from a `:hover, :focus-visible` one.
//
// Scope, stated so this doesn't overclaim: this covers the three selectors
// that are ALWAYS underlined (rest + hover + focus) — the genuine text
// links. `.glossary-entry dt a:hover` is deliberately excluded: its rest
// state carries no underline at all by design (predates #834 — see its own
// comment), so it was never in this "always underlined, hover/focus go full
// ink" family to begin with; a keyboard visitor still gets the global
// `:focus-visible` outline ring there, just not an underline change.
describe("the full-ink underline applies to keyboard focus, not only mouse hover (#834)", () => {
  const css = readFileSync(resolve(process.cwd(), "src/styles.css"), "utf8");

  // Tolerant of whitespace/line-wrapping so a reformat doesn't break it, but
  // it still ties the SELECTOR to the DECLARATION rather than checking either
  // in isolation — a `:focus-visible` selector that forgot the declaration
  // (or a declaration on a rule missing `:focus-visible`) both fail this.
  it.each([
    ["button.link", /button\.link:hover:not\(:disabled\)\s*,\s*button\.link:focus-visible\s*\{[^}]*\}/],
    [":where(.content a)", /:where\(\.content a\):hover\s*,\s*:where\(\.content a\):focus-visible\s*\{[^}]*\}/],
    [".named-picker-loadmore", /\.named-picker-loadmore:hover:not\(:disabled\)\s*,\s*\.named-picker-loadmore:focus-visible\s*\{[^}]*\}/],
  ] as const)("%s: :hover and :focus-visible share the full-ink rule", (_name, pattern) => {
    const match = pattern.exec(css);
    expect(match, "no combined :hover, :focus-visible rule found").not.toBeNull();
    expect(match![0], "combined rule must set full ink").toContain("text-decoration-color: var(--ink)");
  });

  // Codex CLI review of #884, round 4: no page currently combines the
  // `named-picker-trigger` and `link` classes on one element (grepped —
  // only a test fixture does), but `button.link`'s new rest-state underline
  // would bleed through the SAME equal-specificity, later-wins mechanism the
  // trigger's own comment already defends padding/font-size against, the
  // moment a page ever does. `button.named-picker-trigger` — a form control,
  // not a link — resets it explicitly.
  it("button.named-picker-trigger defends against button.link's underline bleeding through", () => {
    const rule = /button\.named-picker-trigger\s*\{[^}]*\}/.exec(css);
    expect(rule, "button.named-picker-trigger rest-state rule not found").not.toBeNull();
    expect(rule![0], "must reset text-decoration").toContain("text-decoration: none");
  });
});

// #654 — the dashboard's surfaces carry no shadow, no caps, no motion and no
// literal colour: tiles inherit #651's elevation direction and #652's sentence
// case, and the "no animated bars" acceptance is met by never animating.
// Walks EVERY block whose selector starts with a dashboard prefix (a second
// `.capture-tile` block or a `:hover` rule is walked too), rather than a list
// of the blocks the author remembered.
// #829 — "dashboard surfaces (#654, INV-8)" retired here. The Dashboard's own
// rules it asserted (`.capture-*`, `.stock-ledger`, `.dash-list`,
// `.panel-wide`) are deleted with the Dashboard.tsx rewrite (styled through
// `sx` now, D2 pairs 14/19-adjacent). The kept-component rules it also
// asserted (`.trend-*`, `.daystrip`, `.day*`, `.tip*`, `.avgline`,
// `.meter-stack` — DayStrip and StockBar, D2 pair 21) are untouched, and
// their assertions moved to `components/DayStrip.styles.test.ts`, the named
// successor, rather than disappearing with this block.
