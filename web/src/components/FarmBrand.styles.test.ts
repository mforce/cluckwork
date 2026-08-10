/// <reference types="node" />
import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

// #179 piece 1 — the sidebar branding slot's GEOMETRY.
//
// WHAT THIS PROVES, AND WHAT IT DOES NOT. jsdom does not lay out, so no test in
// this suite can assert a wordmark actually renders at its natural aspect —
// getBoundingClientRect is all zeros here. This reads the stylesheet and pins
// the DECLARATIONS instead. It catches exactly one regression: re-pinning
// .brand-logo to a fixed width, which is the #123 shape #179 was filed to undo
// (a wide wordmark kept its aspect and lost its height, so it rendered as a
// sliver). It does not verify rendering, and must not be described as if it
// does. The real-browser check is manual.
//
// EVERY matching rule is collected, at any nesting depth, not just the first
// (codex review of #498). The first version scanned for one block and stopped:
// a later `@media (max-width: …) { .brand-logo { width: 26px } }` wins the
// cascade and would have rendered the square again with this file still green.
// A guard that reads as safety while missing the thing it guards is worse than
// no guard, so the width assertion below is quantified over ALL of them.
//
// The relative path is held in a variable, not an inline literal, for the same
// reason cssTokens.ts does it — see the comment there (Vite rewrites a literal
// `new URL("...", import.meta.url)` into a dev-server asset URL under jsdom).
const CSS_REL = "../styles.css";
const CSS_PATH = fileURLToPath(new URL(CSS_REL, import.meta.url));

interface Rule {
  prelude: string;
  decls: Map<string, string>;
}

function parseDeclarations(body: string): Map<string, string> {
  const decls = new Map<string, string>();
  for (const part of body.split(";")) {
    const colon = part.indexOf(":");
    if (colon === -1) continue;
    decls.set(part.slice(0, colon).trim(), part.slice(colon + 1).trim());
  }
  return decls;
}

// Every style rule in the sheet, including those nested inside @media/@supports.
// Deliberately a small brace-depth walk rather than a real CSS parser:
// cssTokens.ts already owns cascade resolution for the design tokens, and this
// needs only "which rules mention this selector, and what do they declare".
// An at-rule prelude (starting '@') is descended into; anything else is a style
// rule whose body is declarations.
function allRules(css: string): Rule[] {
  const rules: Rule[] = [];
  let prelude = "";
  let i = 0;

  function block(): void {
    let body = "";
    let nestedPrelude = "";
    while (i < css.length) {
      const ch = css[i];
      if (ch === "}") { i += 1; break; }
      if (ch === "{") {
        i += 1;
        const saved = prelude;
        prelude = nestedPrelude.trim();
        block();
        prelude = saved;
        nestedPrelude = "";
        continue;
      }
      nestedPrelude += ch;
      body += ch;
      i += 1;
    }
    // A prelude starting with '@' is an at-rule: its children were captured by
    // the recursion above and its own "body" is not declarations.
    if (prelude !== "" && !prelude.startsWith("@")) {
      rules.push({ prelude, decls: parseDeclarations(body) });
    }
  }

  while (i < css.length) {
    const ch = css[i];
    if (ch === "{") { i += 1; const saved = prelude; prelude = prelude.trim(); block(); prelude = saved; prelude = ""; continue; }
    if (ch === "}") { i += 1; prelude = ""; continue; }
    prelude += ch;
    i += 1;
  }
  return rules;
}

// `.brand-logo` as a whole class token — not `.brand-logo-foo`.
const BRAND_LOGO = /\.brand-logo(?![\w-])/;

const css = readFileSync(CSS_PATH, "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
const brandLogoRules = allRules(css).filter((r) => BRAND_LOGO.test(r.prelude));

describe(".brand-logo geometry (#179)", () => {
  it("has a rule at all — the parser must not silently match nothing", () => {
    // Without this, a renamed class or a broken walk turns every quantified
    // assertion below into a vacuous pass over an empty list.
    expect(brandLogoRules.length).toBeGreaterThan(0);
  });

  it("never pins the logo to a fixed width, in any rule", () => {
    // The #123 shape this replaces was `width: 26px; height: 26px`. A fixed
    // width is the defect: with object-fit: contain a wide wordmark keeps its
    // aspect ratio and collapses vertically to fit. Quantified over every
    // rule, including media overrides, so the cascade cannot reintroduce it.
    for (const rule of brandLogoRules) {
      const width = rule.decls.get("width");
      if (width !== undefined) expect({ prelude: rule.prelude, width }).toEqual({ prelude: rule.prelude, width: "auto" });
    }
  });

  it("declares the height-driven base rule", () => {
    const base = brandLogoRules.find((r) => r.prelude.trim() === ".brand-logo");
    expect(base).toBeDefined();
    expect(base!.decls.get("width")).toBe("auto");
    expect(base!.decls.get("height")).toMatch(/^\d+px$/);

    // Unbounded, a wordmark would push .brand-name out of the 244px sidebar
    // entirely. This only binds for WIDE logos — a square mark stays at the
    // height above and leaves the name its full slot.
    const maxWidth = base!.decls.get("max-width");
    expect(maxWidth).toMatch(/^\d+px$/);
    expect(Number.parseInt(maxWidth!, 10)).toBeLessThan(196); // sidebar minus .brand's padding

    // Independent of the above: `contain` is what stops a non-matching aspect
    // being cut off. #179's point was that contain ALONE was not enough.
    expect(base!.decls.get("object-fit")).toBe("contain");
  });
});
