import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import postcss from "postcss";
import type { Rule } from "postcss";

// The bounded date-range toolbar (#653) and the one-line provenance column
// (#653) it ships beside. jsdom computes no layout, so the declarations are
// read from the parsed stylesheet — the same approach as
// components/styles.dialog.test.ts and styles.num.test.ts.
const css = readFileSync(resolve(process.cwd(), "src/styles.css"), "utf8");
const root = postcss.parse(css);

function declarationsFor(selector: string): Map<string, string> {
  const decls = new Map<string, string>();
  root.walkRules((rule: Rule) => {
    const selectors = rule.selectors.map((s) => s.replace(/\/\*[\s\S]*?\*\//g, "").trim());
    if (!selectors.includes(selector)) return;
    rule.walkDecls((d) => { decls.set(d.prop, d.value); });
  });
  return decls;
}

describe(".toolbar", () => {
  it("lays its controls out in a row", () => {
    const decls = declarationsFor(".toolbar");
    expect(decls.get("display")).toBe("flex");
    expect(decls.get("align-items")).toBe("end");
  });
});

describe.each(['.toolbar input[type="date"]'])("%s", (selector) => {
  it("caps a date-range control at a bounded width, never the row's full width", () => {
    expect(declarationsFor(selector).get("max-width")).toBe("12rem");
  });
});

describe("table.data td.provenance-cell", () => {
  it("caps the provenance column so it cannot become the widest column again", () => {
    expect(declarationsFor("table.data td.provenance-cell").get("max-width")).toBe("14rem");
  });
});
