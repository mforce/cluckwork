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

// Every custom property the stylesheet actually declares. A reference to a token
// that is never declared resolves to nothing at runtime — the rule is inert and
// the row is untinted — so "is it a var()?" is not the question worth asking.
const declaredTokens = new Set<string>();
root.walkDecls((d) => { if (d.prop.startsWith("--")) declaredTokens.add(d.prop); });

describe("tr.discounted td", () => {
  const decls = declarationsFor("tr.discounted td");

  it("tints the row", () => {
    expect(decls.get("background")).toBeDefined();
  });

  it("tints it with a TINT token that the stylesheet actually declares", () => {
    const value = decls.get("background") ?? "";
    const match = /^var\((--[a-z0-9-]+)\)$/.exec(value);
    expect(match, `expected a single var() token, got "${value}"`).not.toBeNull();
    const token = match![1];
    // A tint, not merely any token: `var(--surface)` would satisfy a shape check
    // while leaving a discounted row indistinguishable from every other row.
    expect(token, `"${token}" is not a --tint-* token`).toMatch(/^--tint-/);
    // And one that exists: an undeclared token is an inert rule.
    expect(declaredTokens.has(token), `"${token}" is referenced but never declared`).toBe(true);
  });
});

describe(".discount-note", () => {
  const decls = declarationsFor(".discount-note");

  it("wraps, because it renders prose inside td.num which never wraps", () => {
    expect(decls.get("white-space")).toBe("normal");
  });
});
