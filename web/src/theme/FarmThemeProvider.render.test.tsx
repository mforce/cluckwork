import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { render } from "@testing-library/react";
import { ThemeProvider } from "@mui/material/styles";
import Checkbox from "@mui/material/Checkbox";
import FormControlLabel from "@mui/material/FormControlLabel";
import type { FormControlLabelProps } from "@mui/material/FormControlLabel";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import Button from "@mui/material/Button";
import BottomNavigation from "@mui/material/BottomNavigation";
import BottomNavigationAction from "@mui/material/BottomNavigationAction";
import { Egg } from "lucide-react";
import { DEFAULT_BRAND } from "../lib/brand";
import { createFarmTheme } from "./FarmThemeProvider";
import { tokensFor } from "./farmTokens.test";

// A local review of #871 (CodeRabbit rate-limited that round) found two bugs
// invisible to farmTheme.policy.test.ts, which reads what `createFarmTheme`
// DECLARES rather than the DOM it eventually reaches:
//
// - `MuiDialogActions`'s override declared `flexDirection`/`alignItems`
//   correctly, but `DialogActions` ALSO carries its own sibling-combinator
//   spacing rule (`& > :not(style) ~ :not(style) { marginLeft: 8 }`, from
//   MUI's `variants`, not from `styleOverrides.root`), which the object-read
//   test never looked at and which survived the override untouched: a
//   stacked column of buttons still pushed every one after the first 8px
//   right, with no vertical gap.
// - `MuiFormControlLabel` had no override at all. `styles.css`'s
//   `:where(label)` sets `flex-direction: column; gap: 0.35rem` on every raw
//   `<label>`, demoted to zero specificity so any MUI class beats it — but
//   `FormControlLabel`'s own root never declares `flex-direction`/`gap`, so
//   the zero-specificity rule was the ONLY source and won by being the only
//   declaration, stacking a checkbox above its label instead of beside it.
//
// A SECOND local round, over the fix for the finding above, found the fix
// itself was wrong for three of `FormControlLabel`'s four placements: an
// unconditional `root.flexDirection` sits ahead of `FormControlLabel`'s own
// `variants` for `labelPlacement="start"|"top"|"bottom"` (which set
// `row-reverse`/`column-reverse`/`column` with real specificity that already
// beat `:where(label)` on its own) and wins regardless — a real render of all
// four placements computed `row` for every one. The reset is now scoped to
// the `labelPlacementEnd` slot, which MUI composes only when that IS the
// resolved placement, leaving the other three to MUI's own variants.
//
// These render the real component tree and read the actual cascade result
// instead of the theme object, so both classes of bug are visible here.
//
// jsdom does not evaluate `@media` when resolving `getComputedStyle` —
// verified directly: forcing `window.innerWidth` to 390 and reading a
// phone-scoped property back still returns the unconditional value, and
// `window.matchMedia` is not even defined in this test environment. So the
// DialogActions case reads the generated Emotion CSS text instead of asking
// the DOM to lay itself out at a width jsdom cannot actually emulate.

function emotionCssText(): string {
  return Array.from(document.querySelectorAll("style[data-emotion]"))
    .map((tag) => tag.textContent ?? "")
    .join("\n");
}

// MUI's own direction per placement, from its own `variants` rather than this
// app's theme — the thing the second local round found the first fix overrode.
const EXPECTED_DIRECTION: Record<NonNullable<FormControlLabelProps["labelPlacement"]>, string> = {
  end: "row",
  start: "row-reverse",
  top: "column-reverse",
  bottom: "column",
};

describe("FarmThemeProvider against the real DOM (#871 local review)", () => {
  it.each(Object.entries(EXPECTED_DIRECTION))(
    "keeps FormControlLabel's own direction at labelPlacement=%s, with no stylesheet gap",
    (placement, expectedDirection) => {
      // The real stylesheet, injected exactly as `main.tsx` loads it — this is
      // the cascade the bug depends on, and a component test that never loads
      // `styles.css` would not see it.
      const css = readFileSync(resolve(process.cwd(), "src/styles.css"), "utf8");
      const style = document.createElement("style");
      style.textContent = css;
      document.head.appendChild(style);

      const theme = createFarmTheme(tokensFor(DEFAULT_BRAND, "light"), "light");
      const { container } = render(
        <ThemeProvider theme={theme}>
          <FormControlLabel
            control={<Checkbox />}
            label="Keep chickens"
            labelPlacement={placement as FormControlLabelProps["labelPlacement"]}
          />
        </ThemeProvider>,
      );
      const label = container.querySelector("label.MuiFormControlLabel-root");
      expect(label, `FormControlLabel root at ${placement}`).toBeTruthy();
      const computed = getComputedStyle(label as Element);
      // `end` is this app's own reset; the other three are MUI's OWN
      // `variants` for `labelPlacement`, which the reset must not reach —
      // this is the exact regression: an unconditional `root.flexDirection`
      // sits ahead of them and computed `row` for every placement.
      expect(computed.flexDirection, `${placement} flex-direction`).toBe(expectedDirection);
      // `:where(label)`'s `gap: 0.35rem` leaks equally at every placement —
      // MUI spaces the control from the label with `margin`, never `gap`, at
      // any placement — so the reset belongs on `root` regardless of which
      // direction wins.
      expect(computed.gap, `${placement} gap`).toBe("0px");
    },
  );

  it("resets DialogActions' sibling spacing and adds a vertical gap at phone width", () => {
    const theme = createFarmTheme(tokensFor(DEFAULT_BRAND, "light"), "light");
    // Emotion's injected `<style>` tags are not React-managed DOM, so the
    // `afterEach(cleanup)` in `test/setup.ts` unmounts trees but never removes
    // them — they accumulate across every test in this file. #864 gave
    // `body1` its own phone-scoped rule, and `FormControlLabel`'s string
    // `label` renders as a `Typography variant="body1"`, so the placement
    // tests above now ALSO emit an `@media (max-width:899.95px)` block before
    // this test runs. Snapshotting the length before rendering and slicing
    // off everything already present keeps this test looking only at CSS
    // ITS OWN render produced, regardless of what ran earlier in the file.
    const alreadyInjected = emotionCssText().length;
    render(
      <ThemeProvider theme={theme}>
        <Dialog open>
          <DialogActions>
            <Button>Cancel</Button>
            <Button>Confirm</Button>
          </DialogActions>
        </Dialog>
      </ThemeProvider>,
    );

    const phoneQuery = theme.breakpoints.down("md");
    const css = emotionCssText().slice(alreadyInjected);
    const queryStart = css.indexOf(phoneQuery);
    expect(queryStart, `${phoneQuery} block in the generated CSS`).toBeGreaterThanOrEqual(0);
    // The query's declaration block: from its own `{` to the matching `}` one
    // brace level down (the block holds two rules, each with one nesting
    // level of its own, hence depth 0 is "still inside the media query").
    const body = css.slice(queryStart);
    const openIndex = body.indexOf("{");
    let depth = 0;
    let closeIndex = -1;
    for (let i = openIndex; i < body.length; i += 1) {
      if (body[i] === "{") depth += 1;
      else if (body[i] === "}") {
        depth -= 1;
        if (depth === 0) { closeIndex = i; break; }
      }
    }
    expect(closeIndex, "closing brace of the phone media query").toBeGreaterThan(openIndex);
    const phoneBlock = body.slice(openIndex, closeIndex + 1);

    expect(phoneBlock, "phone block resets DialogActions' sibling margin to 0")
      .toMatch(/>\s*:not\(style\)\s*~\s*:not\(style\)\s*\{\s*margin-left:\s*0;?\s*\}/);
    expect(phoneBlock, "phone block adds no vertical gap between stacked buttons")
      .toMatch(/\bgap:\s*8px/);
  });

  // #864 — `BottomNavigationAction`'s OWN `&.selected` rule bumps a label
  // from 12px to 14px (`BottomNavigationAction.js`), a real rule with real
  // specificity, not a zero-specificity leak like the `FormControlLabel` case
  // above — so an object read of `farmTheme.policy.test.ts` proves the theme
  // DECLARES 11px on both states but cannot prove which one the cascade
  // actually renders. Same for the icon: the `icon` prop has no wrapper
  // class, so sizing it depends on a descendant-combinator selector
  // (`& > svg`) actually reaching a real SVG the caller sized differently.
  it("holds the tab bar's icon size and one label size against MUI's own rules", () => {
    const theme = createFarmTheme(tokensFor(DEFAULT_BRAND, "light"), "light");
    const { container } = render(
      <ThemeProvider theme={theme}>
        <BottomNavigation value={0} showLabels>
          {/* `size={20}`, not 24 — proving the theme's `& > svg` override
              wins regardless of what the caller passes, the same way
              `MuiButton`'s pill radius ignores what rendered it. */}
          <BottomNavigationAction label="Dashboard" icon={<Egg size={20} />} />
          <BottomNavigationAction label="Sales" icon={<Egg size={20} />} />
        </BottomNavigation>
      </ThemeProvider>,
    );

    const labels = container.querySelectorAll(".MuiBottomNavigationAction-label");
    const selectedLabel = container.querySelector(".MuiBottomNavigationAction-label.Mui-selected");
    expect(selectedLabel, "a selected label exists").not.toBeNull();
    expect(getComputedStyle(selectedLabel!).fontSize, "selected label size").toBe("11px");
    expect(getComputedStyle(labels[1]!).fontSize, "unselected label size").toBe("11px");

    const icon = container.querySelector("svg");
    expect(icon, "the tab icon renders").not.toBeNull();
    expect(getComputedStyle(icon!).width, "tab icon width").toBe("24px");
    expect(getComputedStyle(icon!).height, "tab icon height").toBe("24px");

    const selectedRoot = container.querySelector(".MuiBottomNavigationAction-root.Mui-selected");
    // jsdom passes `box-shadow` through unnormalized (no browser-style
    // `px`/`rgb()` canonicalization), unlike a real engine — so this compares
    // against the literal the theme declares, not a browser-computed form.
    expect(getComputedStyle(selectedRoot!).boxShadow, "selected tab rule")
      .toBe("inset 0 2px 0 0 #4a154b");
  });

  // Found by a Codex review of #882 (2026-09-16): `transition` is a
  // shorthand, so the press-feedback override's `&.MuiButton-contained,
  // &.MuiButton-outlined` rule REPLACED `Button.js`'s own background-color/
  // box-shadow/border-color/color transition rather than adding a transform
  // transition alongside it — an object read of the theme (`farmTheme.
  // policy.test.ts`) can prove the override is declared but not that it
  // still carries MUI's own properties once the cascade actually resolves.
  it("keeps MUI's own colour transition on a contained button, not just the added transform", () => {
    const theme = createFarmTheme(tokensFor(DEFAULT_BRAND, "light"), "light");
    const { container } = render(
      <ThemeProvider theme={theme}>
        <Button variant="contained">Save</Button>
      </ThemeProvider>,
    );
    const button = container.querySelector(".MuiButton-contained");
    expect(button, "a contained button renders").not.toBeNull();
    const transition = getComputedStyle(button!).transition;
    expect(transition, "keeps the added press transform").toContain("transform 240ms");
    expect(transition, "keeps MUI's own background-color transition").toContain("background-color 160ms");
    expect(transition, "keeps MUI's own border-color transition").toContain("border-color 160ms");
    expect(transition, "keeps MUI's own color transition")
      .toMatch(/(?:^|,\s*)color 160ms/);
  });
});
