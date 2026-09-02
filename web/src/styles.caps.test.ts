import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import postcss from "postcss";
import type { Rule } from "postcss";
import { contrast, resolveTokens, type Mode } from "./test/cssTokens";
import { BRANDS, DEFAULT_BRAND } from "./lib/brand";

// #652 — caps mark structure, they do not decorate every label. The walk is
// over the WHOLE stylesheet: an equality, not a subset, so both re-adding caps
// somewhere new and dropping them from a divider are failures.
const css = readFileSync(resolve(process.cwd(), "src/styles.css"), "utf8");
const root = postcss.parse(css);
const clean = (s: string) => s.replace(/\/\*[\s\S]*?\*\//g, "").trim();

const attrFor = (brand: string) => (brand === DEFAULT_BRAND ? null : brand);
const MODES: Mode[] = ["light", "dark"];

// The sidebar and the More-sheet group dividers. Nothing else.
const CAPS_ALLOWED = [".more-group-label", ".nav-group-label"].sort();

// Same case-folding defect as the elevation guard, found in review round 2 and
// fixed here rather than left as the twin of a hole we had just closed:
// `TEXT-TRANSFORM: UPPERCASE` renders identically and postcss compares both
// the property and the value as exact, case-sensitive strings.
function selectorsUppercasing(): string[] {
  const found = new Set<string>();
  root.walkDecls((d) => {
    const parent = d.parent;
    if (parent === undefined || parent.type !== "rule") return;
    if (d.prop.toLowerCase() !== "text-transform") return;
    if (clean(d.value).toLowerCase() !== "uppercase") return;
    for (const sel of (parent as Rule).selectors) found.add(clean(sel));
  });
  return [...found].sort();
}

function declarationsFor(selector: string): Map<string, string> {
  const decls = new Map<string, string>();
  root.walkRules((rule: Rule) => {
    if (!rule.selectors.map(clean).includes(selector)) return;
    rule.walkDecls((d) => { decls.set(d.prop, d.value); });
  });
  return decls;
}

describe("#652 caps: only the nav group dividers shout", () => {
  it("no rule outside the two dividers uppercases", () => {
    expect(selectorsUppercasing()).toEqual(CAPS_ALLOWED);
  });

  it("table headers are readable ink at a readable size, not tracked caps", () => {
    const th = declarationsFor("table.data th");
    expect(th.get("color")).toBe("var(--ink)");
    expect(th.get("letter-spacing")).toBe("0");
    expect(th.get("font-weight")).toBe("600");
  });

  it("the badge and the step pill keep their pill and drop the tracking", () => {
    expect(declarationsFor(".badge").get("letter-spacing")).toBe("0");
    expect(declarationsFor(".badge").get("border-radius")).toBe("var(--r-pill)");
    expect(declarationsFor(".step-n").get("letter-spacing")).toBe("0");
    expect(declarationsFor(".step-n").get("border-radius")).toBe("var(--r-pill)");
  });

  it("the dead .eyebrow rules are gone", () => {
    expect(css).not.toContain(".eyebrow");
  });
});

// A header only becomes more legible if the colour it moves to is legible.
// Asserted with the repo's own contrast(), across every palette and both
// modes, rather than reasoned about from two hex values.
describe.each(BRANDS)("#652 contrast: table headers on %s", (brand) => {
  it.each(MODES)("%s: --ink on --surface clears WCAG AA", (mode) => {
    const t = resolveTokens(attrFor(brand), mode);
    expect(contrast(t.get("--ink")!, t.get("--surface")!)).toBeGreaterThanOrEqual(4.5);
  });
});
