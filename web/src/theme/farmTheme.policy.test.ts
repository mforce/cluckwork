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

  it("puts the 44px touch floor on phones only, and the pill on every button", () => {
    for (const { label, theme, pill } of themes) {
      const root = slot(theme.components?.MuiButton?.styleOverrides?.root, `${label} MuiButton root`);
      expect(root.borderRadius, `${label} MuiButton radius`).toBe(pill);
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
      expect(slot(theme.components?.MuiCard?.styleOverrides?.root, `${label} card`)
        .borderRadius, `${label} card radius`).toBe(card);
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
});
