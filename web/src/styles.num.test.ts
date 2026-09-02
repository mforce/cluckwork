import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import postcss from "postcss";
import type { Rule } from "postcss";

// Numeric table cells (#650): right-aligned, tabular figures, never wrapped.
// jsdom computes no layout, so the declarations are read from the parsed
// stylesheet — the same approach as components/styles.dialog.test.ts.
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

describe.each(["table.data th.num", "table.data td.num"])("%s", (selector) => {
  const decls = declarationsFor(selector);

  it("right-aligns the figure under its header", () => {
    expect(decls.get("text-align")).toBe("right");
  });

  it("uses tabular figures so columns of numbers line up", () => {
    expect(decls.get("font-variant-numeric")).toBe("tabular-nums");
  });

  it("never wraps a number or a money string mid-token", () => {
    expect(decls.get("white-space")).toBe("nowrap");
  });
});

describe("table.data td.nowrap", () => {
  it("keeps a date cell on one line", () => {
    expect(declarationsFor("table.data td.nowrap").get("white-space")).toBe("nowrap");
  });
});
