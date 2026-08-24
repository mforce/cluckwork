import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import postcss from "postcss";
import type { AtRule, Rule } from "postcss";

// What `min-block-size` / `min-height` the login screen's Forget control
// actually ends up with — the guarantee behind #587's "reachable on a phone"
// requirement, which is CSS-only and therefore invisible to the DOM tests in
// Login.test.tsx (jsdom computes no layout and evaluates no media query).
//
// THIS IS A CASCADE-RELEVANT EVALUATION, NOT A TEXT SEARCH: selectors are
// matched by asking the DOM and declarations are read from a parsed AST, so a
// later phone override spelled differently (an equivalent selector, a comment
// inside the selector, an extra media condition) cannot hide.
//
// Deliberately NOT the full dialog-evaluator cascade: #587's invariant is that
// NO matching rule — at any nesting depth, in any context — declares a minimum
// below 44px. A smaller declaration anywhere in the cascade would shrink the
// touch target in some viewport, so this file fails on ANY sub-44px minimum
// rather than computing a winner. The guard also refuses at-rule contexts it
// does not understand (it walks into @media of any shape, including nested
// ones, but throws on @layer/@supports) rather than silently skipping them.
const css = readFileSync(resolve(process.cwd(), "src/styles.css"), "utf8");
const root = postcss.parse(css);

// Comments inside a selector are removed at tokenization and leave nothing
// behind — `.auth/* x */.auth-forget-farm` IS `.auth .auth-forget-farm` — so
// they are stripped rather than turned into whitespace.
const cleanSelector = (selector: string) => selector.replace(/\/\*[\s\S]*?\*\//g, "").trim();

// The physical property and its logical twin: the app is horizontal
// writing-mode throughout, so `min-height` floors the target exactly as
// `min-block-size` does.
const FLOORS = ["min-block-size", "min-height"];

/** Does this rule match the Forget control and declare a size floor? */
function floorDecls(rule: Rule, el: Element) {
  const matched = rule.selectors.some((selector) => {
    try {
      return el.matches(cleanSelector(selector));
    } catch {
      // A selector this DOM cannot parse cannot match our element either.
      return false;
    }
  });
  if (!matched) return [];
  return rule.nodes.filter(
    (n) => n.type === "decl" && FLOORS.includes(n.prop),
  ) as postcss.Declaration[];
}

const px = (value: string) => {
  const parsed = /^(\d+(?:\.\d+)?)px$/.exec(value.trim());
  return parsed ? Number(parsed[1]) : null;
};

/** Every floor declaration a Forget control would read, at any depth. */
function forgetFloors() {
  // The real topology, not a bare div: the button is a `.auth-forget-farm`
  // button inside a picker entry inside the auth card.
  const main = document.createElement("div");
  main.className = "auth";
  const entry = document.createElement("span");
  entry.className = "auth-farm-picker-entry";
  const el = document.createElement("button");
  el.type = "button";
  el.className = "auth-forget-farm";
  entry.append(el);
  main.append(entry);
  document.body.append(main);

  const found: { value: string; selector: string }[] = [];
  root.walkRules((rule) => {
    // Refuse at-rule contexts this file does not model: a nested @supports or
    // @layer could constrain the target in a way an unconditional read would
    // pass over, and silently skipping it is exactly the vacuous guard this
    // file exists to not be.
    for (let node = rule.parent; node && node.type !== "root"; node = node.parent) {
      // @keyframes cannot match an element and cannot hold a size floor — skip
      // it; anything else besides media is refused below.
      if (node.type === "atrule" && (node as AtRule).name === "keyframes") continue;
      if (node.type === "atrule" && (node as AtRule).name !== "media") {
        throw new Error(
          `Login.styles.test cannot evaluate a rule inside @${(node as AtRule).name} `
          + `("${rule.selector}"). Teach the evaluator that context, or assert the `
          + "target in a real browser.",
        );
      }
    }
    for (const decl of floorDecls(rule, el)) {
      found.push({ value: decl.value, selector: rule.selector });
    }
  });

  main.remove();
  return found;
}

describe("login Forget control touch target (#587)", () => {
  it("has a matching Forget rule at all, rather than passing on absence", () => {
    expect(forgetFloors()).not.toHaveLength(0);
  });

  it("declares a minimum of at least 44px in every matching rule, in every context", () => {
    for (const { value, selector } of forgetFloors()) {
      const pixels = px(value);
      expect(
        pixels,
        `"${selector}" declares ${FLOORS.join("/")} as "${value}", which this `
        + "guard can only evaluate as px. Use px, and keep it >= 44.",
      ).not.toBeNull();
      expect(pixels, `"${selector}" declares a Forget target below 44px (${value}).`).toBeGreaterThanOrEqual(44);
    }
  });

  it("does not make the farm-selection chip destructive", () => {
    // The select chip shares the entry wrapper; a later edit that moves the
    // destructive colour onto the chip would pass the size guard above and
    // still mislead the operator. Assert the chip declares no --danger colour.
    const main = document.createElement("div");
    main.className = "auth";
    const el = document.createElement("button");
    el.className = "auth-farm-picker-select";
    main.append(el);
    document.body.append(main);
    const colours: string[] = [];
    root.walkRules((rule) => {
      const matched = rule.selectors.some((selector) => {
        try {
          return el.matches(cleanSelector(selector));
        } catch {
          return false;
        }
      });
      if (!matched) return;
      for (const node of rule.nodes) {
        if (node.type === "decl" && (node.prop === "color" || node.prop === "background")) {
          colours.push(`${node.prop}: ${node.value}`);
        }
      }
    });
    main.remove();
    for (const colour of colours) {
      expect(colour, `the farm-selection chip is styled with a destructive "${colour}"`).not.toContain("--danger");
    }
  });
});
