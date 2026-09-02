import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import postcss from "postcss";
import type { Rule } from "postcss";

// #651 — elevation encodes what floats; radius encodes nesting depth.
//
// Both halves WALK the whole parsed stylesheet and assert over the complete
// set. They are deliberately not a lookup of the selectors this slice touched:
// AGENTS.md's guard rules call a hand-maintained list of what the author
// happened to think of exactly the thing a guard exists to stop anyone
// trusting. jsdom computes no layout, so the declarations are read from the
// parsed stylesheet — the same approach as styles.num.test.ts.
const css = readFileSync(resolve(process.cwd(), "src/styles.css"), "utf8");
const root = postcss.parse(css);
const clean = (s: string) => s.replace(/\/\*[\s\S]*?\*\//g, "").trim();

// Split a box-shadow value into its comma-separated layers, ignoring commas
// inside parentheses: rgba(29, 21, 33, 0.04) is one token, not four layers.
function layers(value: string): string[] {
  const out: string[] = [];
  let depth = 0;
  let current = "";
  for (const ch of value) {
    if (ch === "(") depth += 1;
    else if (ch === ")") depth -= 1;
    if (ch === "," && depth === 0) {
      out.push(current);
      current = "";
      continue;
    }
    current += ch;
  }
  out.push(current);
  return out.map((layer) => layer.trim()).filter((layer) => layer.length > 0);
}

// A rule casts a drop shadow if ANY layer is non-inset. Testing only the
// value's prefix would pass
//   box-shadow: inset 0 0 0 1px var(--hairline), 0 8px 24px rgba(0,0,0,.12);
// which is a real drop shadow wearing an inset first layer — and that is
// precisely the regression this guard exists to block.
function castsShadow(value: string): boolean {
  if (clean(value) === "none") return false;
  return layers(value).some((layer) => !layer.startsWith("inset"));
}

// A shadow can be painted two ways. `box-shadow` is the obvious one;
// `filter: drop-shadow(...)` renders the same thing and lives in a completely
// different declaration, so a walk that visits only `box-shadow` reports a
// clean stylesheet while a card floats on every screen. Review round 1 proved
// that with a mutation the guard did not notice.
//
// Both mechanisms feed ONE set, so the SHADOW_ALLOWED equality below governs
// both and a float that legitimately needs drop-shadow() is allow-listed
// exactly like one that uses box-shadow.
//
// This targets drop-shadow, not `filter` wholesale: blur() and brightness()
// are not shadows, and banning them would assert something the invariant never
// claimed. The stylesheet declares no filter at all today.
const SHADOW_PROPS = ["box-shadow", "filter", "-webkit-filter"];

function selectorsCastingShadow(): string[] {
  const found = new Set<string>();
  for (const prop of SHADOW_PROPS) {
    root.walkDecls(prop, (d) => {
      const casts = prop === "box-shadow"
        ? castsShadow(d.value)
        : d.value.includes("drop-shadow");
      if (!casts) return;
      for (const sel of (d.parent as Rule).selectors) found.add(clean(sel));
    });
  }
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

// Everything allowed to cast a shadow, and why. Six floats plus one ring.
const SHADOW_ALLOWED = [
  ".auth .card",            // the sign-in card, floating on the auth gradient
  ".dialog",                // a modal, over its backdrop
  ".entry-foot",            // the Daily entry sticky action bar
  ".glossary-entry:target", // not elevation: a spread-only deep-link halo
  ".named-picker-listbox",  // the picker popover, over the form beneath it
  ".tabbar",                // the mobile tab bar
  ".update-banner",         // the service-worker update prompt
].sort();

describe("#651 elevation: only a float casts a shadow", () => {
  it("no rule outside the float set casts a drop shadow", () => {
    expect(selectorsCastingShadow()).toEqual(SHADOW_ALLOWED);
  });

  it("a panel, a card and an order panel carry a border and nothing else", () => {
    for (const selector of [".card", ".panel", ".order-panel"])
      expect(declarationsFor(selector).get("box-shadow")).toBeUndefined();
  });

  it("the toolbar reads as inset, not as a floating card", () => {
    const toolbar = declarationsFor(".toolbar");
    expect(toolbar.get("background")).toBe("var(--surface-2)");
    expect(toolbar.get("border")).toBe("1px solid var(--hairline)");
    expect(toolbar.get("border-radius")).toBe("var(--r-panel)");
    expect(toolbar.get("box-shadow")).toBeUndefined();
  });

  it("the picker popover uses the float shadow, not the retired card one", () => {
    expect(declarationsFor(".named-picker-listbox").get("box-shadow"))
      .toBe("var(--shadow-dialog)");
  });

  it("--shadow-card is retired: not declared, and referenced nowhere", () => {
    expect(css).not.toContain("--shadow-card");
  });
});

describe("#651 radius: a three-step scale, declared as tokens", () => {
  const tokenValue = (name: string): string | undefined =>
    declarationsFor(":root").get(name);

  it("declares three distinct steps in increasing order", () => {
    expect(tokenValue("--r-input")).toBe("6px");
    expect(tokenValue("--r-panel")).toBe("10px");
    expect(tokenValue("--r-card")).toBe("16px");
  });

  // Every surface this slice owns, INCLUDING two --r-input consumers. Without
  // those the scale guard would assert nothing about the one token whose value
  // actually changes, and would read as safety it does not provide.
  it.each([
    ".toolbar",
    ".card",
    ".panel",
    ".order-panel",
    ".entry-pane",
    ".capture-tile",
    "input",
    ".named-picker-trigger",
  ])("%s resolves its radius through a token, not a literal", (selector) => {
    const radius = declarationsFor(selector).get("border-radius");
    expect(radius).toMatch(/^var\(--r-[a-z]+\)$/);
  });
});
