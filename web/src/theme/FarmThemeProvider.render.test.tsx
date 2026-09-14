import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { render } from "@testing-library/react";
import { ThemeProvider } from "@mui/material/styles";
import Checkbox from "@mui/material/Checkbox";
import FormControlLabel from "@mui/material/FormControlLabel";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import Button from "@mui/material/Button";
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

describe("FarmThemeProvider against the real DOM (#871 local review)", () => {
  it("keeps a FormControlLabel's checkbox and label side by side", () => {
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
        <FormControlLabel control={<Checkbox />} label="Keep chickens" />
      </ThemeProvider>,
    );
    const label = container.querySelector("label.MuiFormControlLabel-root");
    expect(label, "FormControlLabel root").toBeTruthy();
    const computed = getComputedStyle(label as Element);
    expect(computed.flexDirection, "FormControlLabel flex-direction").toBe("row");
    expect(computed.gap, "FormControlLabel gap").toBe("0px");
  });

  it("resets DialogActions' sibling spacing and adds a vertical gap at phone width", () => {
    const theme = createFarmTheme(tokensFor(DEFAULT_BRAND, "light"), "light");
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
    const css = emotionCssText();
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
});
