import { describe, expect, it } from "vitest";
import type { Theme } from "@mui/material/styles";
import { BRANDS } from "../lib/brand";
import type { Mode } from "../test/cssTokens";
import { pixelsFrom } from "./farmTokens";
import { createFarmTheme } from "./FarmThemeProvider";
import { tokensFor } from "./farmTokens.test";

// G2 (#822 §4) — the theme IS the policy, so the policy is asserted on the
// theme rather than on the stylesheet it was built from.
//
// #651 settled two rules that #823 moves into MUI: elevation encodes what
// floats, and radius encodes nesting depth. Both were enforced by walking
// `styles.css`, which Emotion never writes to — so from the moment a screen
// renders a `Paper`, that walk sees nothing. This is where the rules live now.
//
// EVERY PALETTE AND MODE, not the default one: the two shadow tokens and the
// three radii are read off the document, so a palette that declared a
// different `--r-card` would give one farm a different nesting scale. The
// tokens resolve from the real stylesheet through `tokensFor`, never from the
// jsdom fallback, six of whose values differ from the light palette (#822 §2.6).

const MODES: Mode[] = ["light", "dark"];

function everyTheme(): Array<{ label: string; theme: Theme; bar: string; dialog: string;
  input: number; panel: number; card: number; pill: number }> {
  const out = [];
  for (const brand of BRANDS) {
    for (const mode of MODES) {
      const tokens = tokensFor(brand, mode);
      out.push({
        label: `${brand}/${mode}`,
        theme: createFarmTheme(tokens, mode),
        bar: tokens["--shadow-bar"],
        dialog: tokens["--shadow-dialog"],
        input: pixelsFrom(tokens["--r-input"], -1),
        panel: pixelsFrom(tokens["--r-panel"], -1),
        card: pixelsFrom(tokens["--r-card"], -1),
        pill: pixelsFrom(tokens["--r-pill"], -1),
      });
    }
  }
  return out;
}

/**
 * A `styleOverrides` slot as the plain object this theme writes.
 *
 * MUI also allows a callback, and reading one as `{}` would make every
 * assertion below pass over a slot whose contents were never inspected.
 */
function slot(value: unknown, what: string): Record<string, unknown> {
  expect(value, `${what} is missing`).toBeDefined();
  expect(typeof value, `${what} is not a plain style object`).toBe("object");
  return value as Record<string, unknown>;
}

describe("farm theme policy (#823 G2)", () => {
  const themes = everyTheme();

  it("covers every palette and both modes", () => {
    expect(themes.length).toBe(BRANDS.length * MODES.length);
  });

  it("casts a shadow only at the five indices MUI's own floats default to", () => {
    for (const { label, theme, bar, dialog } of themes) {
      const expected = Array.from({ length: 25 }, (_, index) => {
        if (index === 4 || index === 6) return bar;
        if (index === 8 || index === 16 || index === 24) return dialog;
        return "none";
      });
      expect(Array.from(theme.shadows), `${label} shadows`).toEqual(expected);
      // The tokens are the source, so a stylesheet that stopped declaring them
      // would otherwise pin "none" everywhere and read as a passing policy.
      expect(bar, `${label} --shadow-bar`).not.toBe("none");
      expect(dialog, `${label} --shadow-dialog`).not.toBe("none");
    }
  });

  it("writes buttons and overlines in sentence case", () => {
    for (const { label, theme } of themes) {
      expect(theme.typography.button.textTransform, `${label} button`).toBe("none");
      expect(theme.typography.overline.textTransform, `${label} overline`).toBe("none");
    }
  });

  it("makes a bare Paper flat and leaves its variant alone", () => {
    for (const { label, theme } of themes) {
      const paper = theme.components?.MuiPaper?.defaultProps;
      expect(paper?.elevation, `${label} MuiPaper elevation`).toBe(0);
      // `Paper` sets `--Paper-shadow` only for `variant === "elevation"`, and no
      // float passes a variant — so an outlined default would silently strip the
      // shadow from every dialog, drawer, menu and snackbar.
      expect(paper, `${label} MuiPaper variant`).not.toHaveProperty("variant");
    }
  });

  it("makes a Card a hairline box", () => {
    for (const { label, theme } of themes) {
      expect(theme.components?.MuiCard?.defaultProps?.variant, `${label} MuiCard`).toBe("outlined");
    }
  });

  it("gives the picker popover the dialog shadow it would otherwise lose", () => {
    for (const { label, theme, dialog } of themes) {
      const paper = slot(theme.components?.MuiAutocomplete?.styleOverrides?.paper,
        `${label} MuiAutocomplete paper`);
      expect(paper.boxShadow, `${label} MuiAutocomplete paper`).toBe(dialog);
      expect(paper.boxShadow).toBe(theme.shadows[8]);
    }
  });

  it("keeps the contained button off the shadow array", () => {
    for (const { label, theme } of themes) {
      // Button reads shadows[2], [4], [6] and [8] for rest, hover, focus and
      // press, so without this a primary button casts the bar shadow on hover
      // and the dialog shadow on press.
      expect(theme.components?.MuiButton?.defaultProps?.disableElevation, `${label}`).toBe(true);
    }
  });

  it("puts the 44px touch floor on phones only, and the control radius on every button (#864 amendment)", () => {
    for (const { label, theme, input } of themes) {
      const root = slot(theme.components?.MuiButton?.styleOverrides?.root, `${label} MuiButton root`);
      // DIRECTION.md line 17: controls take the 4px control radius (--r-input),
      // not the pill. The mockup's buttons render as 4px rectangles, and
      // #883's after-screenshots caught this still reading the pill radius
      // MuiChip alone keeps. MuiChip is unchanged.
      expect(root.borderRadius, `${label} MuiButton radius`).toBe(input);
      expect(root, `${label} MuiButton minHeight at every width`).not.toHaveProperty("minHeight");

      const phone = theme.breakpoints.down("md");
      expect(phone, `${label} breakpoint`).toContain("max-width");
      const narrow = slot(root[phone], `${label} MuiButton ${phone}`);
      expect(narrow.minHeight, `${label} MuiButton phone floor`).toBe(44);
    }
  });

  it("stacks dialog actions at phone width", () => {
    for (const { label, theme } of themes) {
      const root = slot(theme.components?.MuiDialogActions?.styleOverrides?.root,
        `${label} MuiDialogActions root`);
      const narrow = slot(root[theme.breakpoints.down("md")], `${label} MuiDialogActions phone`);
      expect(narrow.flexDirection, `${label}`).toBe("column");
      // `DialogActions` sets `alignItems: center`, which leaves a stacked button
      // at its intrinsic width — half of #740 rather than all of it.
      expect(narrow.alignItems, `${label}`).toBe("stretch");
      // `DialogActions` ALSO carries its own sibling-combinator spacing
      // (`& > :not(style) ~ :not(style) { marginLeft: 8 }`, from its own
      // `variants`, not from this override), which stacking direction alone
      // does not touch: a column of buttons still pushed every one after the
      // first 8px right, with no space between rows. This object read is not
      // the full proof — the nested selector shares the sibling-margin
      // property with several unrelated rules a theme walk cannot tell apart
      // by intent — so FarmThemeProvider.render.test.tsx reads the actual
      // generated CSS for the property this reset REPLACES.
      const spacing = slot(narrow["& > :not(style) ~ :not(style)"], `${label} MuiDialogActions phone spacing`);
      expect(spacing.marginLeft, `${label} sibling margin reset`).toBe(0);
      expect(narrow.gap, `${label} vertical gap between stacked buttons`).toBe(8);
    }
  });

  it("keeps radius a three-step nesting scale", () => {
    for (const { label, theme, input, panel, card } of themes) {
      expect(theme.shape.borderRadius, `${label} default radius`).toBe(panel);
      expect(slot(theme.components?.MuiOutlinedInput?.styleOverrides?.root, `${label} input`)
        .borderRadius, `${label} input radius`).toBe(input);
      // A card is a panel-family surface, not a dialog: #864 repoints MuiCard
      // at --r-panel and reserves --r-card for dialogs and sheets only.
      expect(slot(theme.components?.MuiCard?.styleOverrides?.root, `${label} card`)
        .borderRadius, `${label} card radius`).toBe(panel);
      expect(slot(theme.components?.MuiDialog?.styleOverrides?.paper, `${label} dialog`)
        .borderRadius, `${label} dialog radius`).toBe(card);
      // Three distinct steps, increasing. One number cannot carry a hierarchy,
      // which is the defect #823 inherited: `shape.borderRadius` read `--r-card`,
      // so every text field rendered at 16px.
      expect([input, panel, card], `${label} nesting scale`).toEqual([...new Set([input, panel, card])]);
      expect(input, `${label} input < panel`).toBeLessThan(panel);
      expect(panel, `${label} panel < card`).toBeLessThan(card);
    }
  });

  // #864 — the visual language borrow-list (issue comments, 2026-09-16).

  const EASE = "cubic-bezier(.32,.72,0,1)";

  it("uses one easing curve everywhere and reuses MUI's own colour-duration key", () => {
    for (const { label, theme } of themes) {
      expect(theme.transitions.easing.easeInOut, `${label} easeInOut`).toBe(EASE);
      expect(theme.transitions.easing.easeOut, `${label} easeOut`).toBe(EASE);
      expect(theme.transitions.easing.easeIn, `${label} easeIn`).toBe(EASE);
      expect(theme.transitions.easing.sharp, `${label} sharp`).toBe(EASE);
      // `Button` and `BottomNavigationAction` both read `duration.short` for
      // their own colour transitions (`@mui/material@9.4.0`), so this is
      // where "colour transitions 160ms" has to land to reach them for free.
      expect(theme.transitions.duration.short, `${label} colour duration`).toBe(160);
    }
  });

  it("wraps MUI's own transitions in a system prefers-reduced-motion query", () => {
    for (const { label, theme } of themes) {
      expect(theme.motion.reducedMotion, `${label} motion.reducedMotion`).toBe("system");
    }
  });

  it("disables the ripple on every ButtonBase descendant", () => {
    for (const { label, theme } of themes) {
      expect(theme.components?.MuiButtonBase?.defaultProps?.disableRipple, `${label}`).toBe(true);
    }
  });

  it("scopes press feedback to contained/outlined buttons, with its own reduced-motion guard", () => {
    for (const { label, theme } of themes) {
      const root = slot(theme.components?.MuiButtonBase?.styleOverrides?.root, `${label} MuiButtonBase root`);
      const pressed = slot(root["&.MuiButton-contained, &.MuiButton-outlined"], `${label} press-feedback scope`);
      // `transition` is a shorthand: naming only `transform` here would
      // REPLACE `Button.js`'s own background-color/box-shadow/border-color/
      // color transition rather than add to it, because this compound-class
      // selector outranks `Button.js`'s single-class one. Found by a Codex
      // review of #882 (2026-09-16) against a real render (see
      // FarmThemeProvider.render.test.tsx); this pins every property the fix
      // must keep, not just the one this slice added.
      expect(pressed.transition, `${label} press transition`).toBe(
        "transform 240ms cubic-bezier(.32,.72,0,1), "
        + "background-color 160ms cubic-bezier(.32,.72,0,1), "
        + "box-shadow 160ms cubic-bezier(.32,.72,0,1), "
        + "border-color 160ms cubic-bezier(.32,.72,0,1), "
        + "color 160ms cubic-bezier(.32,.72,0,1)",
      );
      const active = slot(pressed["&:active"], `${label} press active state`);
      expect(active.transform, `${label} press scale`).toBe("scale(0.98)");
      // This is a plain style object, not one MUI builds through
      // `getTransitionStyles()`, so `motion.reducedMotion` above does not
      // reach it automatically — it needs its own media query.
      const reduced = slot(pressed["@media (prefers-reduced-motion: reduce)"], `${label} press reduced-motion`);
      expect(reduced.transition, `${label} press reduced-motion transition`).toBe("none");
      // Never on a bare `ButtonBase` (an `IconButton`) or `MuiButton-text` —
      // the owner's desktop render showed the tint reading as a smudge behind
      // underlined ruled-text actions.
      expect(root, `${label} no unscoped transform`).not.toHaveProperty("transition");
      expect(Object.keys(root).some((key) => key.includes("MuiButton-text")),
        `${label} text buttons excluded`).toBe(false);
    }
  });

  it("gives the tab bar no shadow and a hairline top rule instead (variant B)", () => {
    for (const { label, theme } of themes) {
      const root = slot(theme.components?.MuiBottomNavigation?.styleOverrides?.root,
        `${label} MuiBottomNavigation root`);
      expect(root.boxShadow, `${label} tab bar shadow`).toBe("none");
      expect(root.borderTop, `${label} tab bar hairline`).toMatch(/^1px solid /);
    }
  });

  it("marks the selected tab with a 2px rule, sizes its icon, and holds one label size", () => {
    for (const { label, theme } of themes) {
      const root = slot(theme.components?.MuiBottomNavigationAction?.styleOverrides?.root,
        `${label} MuiBottomNavigationAction root`);
      expect(root.minHeight, `${label} tab min height`).toBe(44);
      const icon = slot(root["& > svg"], `${label} tab icon sizing`);
      expect(icon.width, `${label} tab icon width`).toBe(24);
      expect(icon.height, `${label} tab icon height`).toBe(24);
      const selected = slot(root["&.Mui-selected"], `${label} tab selected rule`);
      expect(selected.boxShadow, `${label} tab selected rule value`).toMatch(/^inset 0 2px 0 0 /);

      const labelStyle = slot(theme.components?.MuiBottomNavigationAction?.styleOverrides?.label,
        `${label} MuiBottomNavigationAction label`);
      expect(labelStyle.fontSize, `${label} tab label size`).toBe(11);
      expect(labelStyle.fontWeight, `${label} tab label weight`).toBe(500);
      // MUI's own `&.selected` rule bumps 12px to 14px; this direction wants
      // one size whether selected or not.
      const selectedLabel = slot(labelStyle["&.Mui-selected"], `${label} tab selected label`);
      expect(selectedLabel.fontSize, `${label} tab selected label size`).toBe(11);
    }
  });

  it("sets desktop/phone ledger row heights", () => {
    for (const { label, theme } of themes) {
      const root = slot(theme.components?.MuiTableRow?.styleOverrides?.root, `${label} MuiTableRow root`);
      expect(root.height, `${label} desktop row height`).toBe(36);
      const phone = theme.breakpoints.down("md");
      const narrow = slot(root[phone], `${label} MuiTableRow phone`);
      expect(narrow.height, `${label} phone row height`).toBe(52);
    }
  });

  it("sets the direction's type scale (display/title/section/rows/caption)", () => {
    for (const { label, theme } of themes) {
      const phone = theme.breakpoints.down("md");

      expect(theme.typography.h1.fontSize, `${label} display size`).toBe("2.5rem");
      expect(theme.typography.h1.fontWeight, `${label} display weight`).toBe(600);

      expect(theme.typography.h2.fontSize, `${label} title size`).toBe("1.5rem");
      expect(theme.typography.h2.fontWeight, `${label} title weight`).toBe(600);
      const h2Phone = slot((theme.typography.h2 as Record<string, unknown>)[phone], `${label} title phone`);
      expect(h2Phone.fontSize, `${label} title phone size`).toBe("1.75rem");

      expect(theme.typography.h3.fontSize, `${label} section size`).toBe("0.8125rem");
      expect(theme.typography.h3.fontWeight, `${label} section weight`).toBe(600);

      expect(theme.typography.body1.fontSize, `${label} row size`).toBe("0.875rem");
      expect(theme.typography.body1.fontVariantNumeric, `${label} row tabular numerals`).toBe("tabular-nums");
      const body1Phone = slot((theme.typography.body1 as Record<string, unknown>)[phone], `${label} row phone`);
      expect(body1Phone.fontSize, `${label} row phone size`).toBe("1rem");

      expect(theme.typography.caption.fontSize, `${label} caption size`).toBe("0.75rem");
    }
  });
});
