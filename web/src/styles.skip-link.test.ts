import { describe, expect, it } from "vitest";
import postcss from "postcss";
import type { Rule } from "postcss";
import { createTheme } from "@mui/material/styles";
import { readStylesheet } from "./test/stylesheet";

// #1277 review — the focused skip link opens over the docked rail. Chromium
// showed the rail (the MUI drawer layer) painting over it; it must still sit
// under a modal dialog, which covers the whole page.
const root = postcss.parse(readStylesheet());

function zIndexOf(selector: string): number {
  let value: string | undefined;
  root.walkRules((rule: Rule) => {
    if (rule.selectors.map((s) => s.trim()).includes(selector))
      rule.walkDecls("z-index", (d) => { value = d.value; });
  });
  if (value === undefined) throw new Error(`${selector} sets no z-index`);
  return Number(value);
}

describe("the skip link's layer", () => {
  it("is above the rail and below a modal dialog", () => {
    const { zIndex } = createTheme();
    const skip = zIndexOf(".skip-link");

    expect(skip).toBeGreaterThan(zIndex.drawer);
    expect(skip).toBeLessThan(zIndex.modal);
  });
});
