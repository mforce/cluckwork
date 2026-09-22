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

  it("keeps default component elevations flat", () => {
    for (const { label, theme } of themes) {
      const elevations = Object.entries(theme.components ?? {}).flatMap(([component, policy]) => {
        const elevation = policy?.defaultProps && "elevation" in policy.defaultProps
          ? policy.defaultProps.elevation : undefined;
        return elevation === undefined ? [] : [`${component}:${String(elevation)}`];
      });
      expect(elevations.sort(), label).toEqual(["MuiAccordion:0", "MuiPaper:0"]);
    }
  });

  it("makes a Card a hairline box", () => {
    for (const { label, theme } of themes) {
      expect(theme.components?.MuiCard?.defaultProps?.variant, `${label} MuiCard`).toBe("outlined");
    }
  });

  // MUI's own Tooltip default is a hardcoded dark grey with no farm token
  // behind it — the one float in the app that did not read from the
  // palette. It now reads like every other float instead.
  it("reads the Tooltip surface from the farm palette, not MUI's hardcoded grey", () => {
    for (const { label, theme, panel, dialog } of themes) {
      const tooltip = slot(theme.components?.MuiTooltip?.styleOverrides?.tooltip,
        `${label} MuiTooltip tooltip`);
      expect(tooltip.backgroundColor, `${label} tooltip background`).toBe(theme.palette.background.paper);
      expect(tooltip.color, `${label} tooltip text`).toBe(theme.palette.text.primary);
      expect(tooltip.border, `${label} tooltip border`).toBe(`1px solid ${theme.palette.divider}`);
      expect(tooltip.borderRadius, `${label} tooltip radius`).toBe(panel);
      expect(tooltip.boxShadow, `${label} tooltip shadow`).toBe(dialog);
      // A `\n`-joined multi-line title (ProvenanceCell's stamp, Audit's raw
      // JSON) must read as line breaks, the same as a native `title` renders
      // one — MUI's own default (`normal`) collapses them to spaces.
      expect(tooltip.whiteSpace, `${label} tooltip whiteSpace`).toBe("pre-line");
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

  it("uses serif page headings and a sans 15px subsection heading", () => {
    for (const { label, theme } of themes) {
      for (const variant of ["h1", "h2"] as const)
        expect(theme.typography[variant].fontFamily, `${label} ${variant}`).toBe("Georgia, serif");
      expect(theme.typography.h3.fontFamily, `${label} h3 family`).toBe(theme.typography.fontFamily);
      expect(theme.typography.h3.fontSize, `${label} h3 size`).toBe("0.9375rem");
    }
  });

  it("renders accordions as separated bordered panels with tinted summary bands", () => {
    for (const { label, theme, input } of themes) {
      const root = slot(theme.components?.MuiAccordion?.styleOverrides?.root, `${label} MuiAccordion root`);
      expect(root.border, `${label} accordion border`).toBe(`1px solid ${theme.palette.divider}`);
      expect(root.borderRadius, `${label} accordion radius`).toBe(input);
      expect(root.marginBottom, `${label} accordion separation`).toBe(12);
      const summary = slot(theme.components?.MuiAccordionSummary?.styleOverrides?.root,
        `${label} MuiAccordionSummary root`);
      expect(summary.backgroundColor, `${label} summary tint`).toBe(theme.palette.background.default);
    }
  });

  it("gives the selected sidebar item a filled farm-derived state with matching icon colour", () => {
    for (const { label, theme } of themes) {
      const root = slot(theme.components?.MuiListItemButton?.styleOverrides?.root,
        `${label} MuiListItemButton root`);
      const selected = slot(root["&.Mui-selected, &.Mui-selected:hover"], `${label} selected sidebar item`);
      expect(selected.backgroundColor, `${label} selected fill`).not.toBe("transparent");
      expect(selected.backgroundColor, `${label} selected fill`).not.toBe(theme.palette.primary.main);
      expect(selected.color, `${label} selected text`).toBe(theme.palette.primary.contrastText);
      const icon = slot(selected["& .MuiListItemIcon-root"], `${label} selected sidebar icon`);
      expect(icon.color, `${label} selected icon colour`).toBe("inherit");
    }
  });

  // #832 — retired: #896 (owner, 2026-09-17) made a dialog footer row/
  // right-aligned at phone width, not stacked, so `MuiDialogActions` carries
  // no phone override at all any more (see the comment beside where this
  // block used to sit in FarmThemeProvider.tsx). There is nothing app-owned
  // left to pin here — the resulting layout is MUI's own unmodified
  // `DialogActions` default at every width, a library fact rather than an
  // app policy, and this is a deliberate coverage reduction rather than a
  // guard with a successor. `FarmThemeProvider.render.test.tsx`'s matching
  // "resets DialogActions' sibling spacing…" render test is retired with it.

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
      // The selected label and icon take the same accent as the rule above them:
      // MUI's default is palette.primary (the brand), which vanishes on the dark bar.
      expect(selected.boxShadow, `${label} tab selected colour matches its rule`).toBe(`inset 0 2px 0 0 ${selected.color}`);

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

  // #832 — a real #441 repro: `TableContainer`'s own default (`overflow-x:
  // auto`) scrolls a wide table WITHIN itself but does not stop a mobile
  // browser's initial layout-viewport sizing from measuring the table's
  // raw content width, which is what actually pushed `/flocks` to a
  // 941px `document.documentElement.scrollWidth` at a 390px frame
  // (`phone.spec.ts`'s overflow walk, run against a real build). `contain:
  // layout` is the fix `styles.css`'s own `table.data` phone rule already
  // carries; this pins the same property on `MuiTableContainer`.
  it("stops a phone-width table's own layout from inflating the viewport (#441)", () => {
    for (const { label, theme } of themes) {
      const root = slot(theme.components?.MuiTableContainer?.styleOverrides?.root, `${label} MuiTableContainer root`);
      const phone = theme.breakpoints.down("md");
      const narrow = slot(root[phone], `${label} MuiTableContainer phone`);
      expect(narrow.contain, `${label} phone table containment`).toBe("layout");
    }
  });

  // #832 — the first slice to mount a real MUI `Table`. Pinned so a later
  // change cannot silently widen `TableCell` back to `body2`'s 0.95rem/1.43
  // (the pre-#832 default, ~21.7px tall) or drop the tabular numerals pair 9
  // asks for, both of which `TableRow`'s height floor above would hide —
  // it is a floor, not a ceiling, so an over-tall cell renders wrong with
  // every one of these assertions still green.
  //
  // Padding is pinned as well: `size="small"`'s default is 16px per cell
  // wider than `table.data td`'s, enough to push Flocks' widest row off-screen
  // at 1280 with no cell wrapping, which no other guard here would catch.
  it("sizes table cells to the row scale, gives every cell tabular numerals, and matches table.data's padding", () => {
    for (const { label, theme } of themes) {
      const root = slot(theme.components?.MuiTableCell?.styleOverrides?.root, `${label} MuiTableCell root`);
      expect(root.fontSize, `${label} row size`).toBe("0.875rem");
      expect(root.lineHeight, `${label} row line-height`).toBe(20 / 14);
      expect(root.fontVariantNumeric, `${label} tabular numerals`).toBe("tabular-nums");
      expect(root.padding, `${label} cell padding`).toBe("0.6rem 1rem 0.6rem 0");
      const phone = theme.breakpoints.down("md");
      const narrow = slot(root[phone], `${label} MuiTableCell phone`);
      expect(narrow.fontSize, `${label} phone row size`).toBe("1rem");
      expect(narrow.lineHeight, `${label} phone row line-height`).toBe(24 / 16);
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

      expect(theme.typography.h3.fontSize, `${label} section size`).toBe("0.9375rem");
      expect(theme.typography.h3.fontWeight, `${label} section weight`).toBe(600);

      expect(theme.typography.body1.fontSize, `${label} row size`).toBe("0.875rem");
      expect(theme.typography.body1.fontVariantNumeric, `${label} row tabular numerals`).toBe("tabular-nums");
      const body1Phone = slot((theme.typography.body1 as Record<string, unknown>)[phone], `${label} row phone`);
      expect(body1Phone.fontSize, `${label} row phone size`).toBe("1rem");

      expect(theme.typography.caption.fontSize, `${label} caption size`).toBe("0.75rem");
    }
  });

  // #834 — DIRECTION.md's link language, on the one MUI component nothing
  // renders yet: a real `Link` is ink, underlined in the 28% ink rule at
  // rest, full ink on hover and focus. Pinned here (not just in
  // styles.test.ts) because `--link`/`--link-rule` reaching `styles.css` says
  // nothing about whether MUI's own `Link` picks them up — that only happens
  // through this override.
  it("colours a real MUI Link ink, underlined in the rule colour, full ink on hover and focus", () => {
    for (const brand of BRANDS) {
      for (const mode of MODES) {
        const tokens = tokensFor(brand, mode);
        const theme = createFarmTheme(tokens, mode);
        const label = `${brand}/${mode}`;
        expect(theme.components?.MuiLink?.defaultProps?.underline, `${label} MuiLink underline`).toBe("always");
        const root = slot(theme.components?.MuiLink?.styleOverrides?.root, `${label} MuiLink root`);
        expect(root.color, `${label} MuiLink rest colour`).toBe(tokens["--ink"]);
        expect(root.textDecorationColor, `${label} MuiLink rest underline colour`).toBe(tokens["--link-rule"]);
        expect(root.textUnderlineOffset, `${label} MuiLink underline offset`).toBe("2px");
        const interactive = slot(root["&:hover, &:focus-visible"], `${label} MuiLink hover/focus`);
        expect(interactive.textDecorationColor, `${label} MuiLink hover/focus underline colour`).toBe(tokens["--ink"]);
      }
    }
  });
});
