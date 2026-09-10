import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import postcss from "postcss";
import type { Rule } from "postcss";

// #723 — the discount treatment's two stylesheet-only declarations. Neither is
// reachable from jsdom (it computes no layout), so without this file a mutant
// that deletes either one leaves the entire suite green — which is exactly what
// the driver's Phase 11 mutation M13 observed against the row tint.
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

describe("tr.discounted td", () => {
  const decls = declarationsFor("tr.discounted td");

  it("tints the row", () => {
    expect(decls.get("background")).toBeDefined();
  });

  it("tints it through a token, never a raw literal", () => {
    // A raw colour here would bypass the per-brand palettes styles.test.ts pins.
    expect(decls.get("background")).toMatch(/^var\(--[a-z-]+\)$/);
  });
});

describe(".discount-note", () => {
  const decls = declarationsFor(".discount-note");

  it("wraps, because it renders prose inside td.num which never wraps", () => {
    expect(decls.get("white-space")).toBe("normal");
  });
});
