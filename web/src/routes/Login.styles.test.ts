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
// below 44px in EITHER axis. The app is horizontal writing-mode throughout, so
// block/inline ARE the two visual axes: a smaller declaration in either one
// anywhere in the cascade would shrink the touch target in some viewport, so
// this file fails on ANY sub-44px minimum rather than computing a winner. The guard also refuses at-rule contexts it
// does not understand (it walks into @media of any shape, including nested
// ones, but throws on @layer/@supports) rather than silently skipping them.
const css = readFileSync(resolve(process.cwd(), "src/styles.css"), "utf8");
const root = postcss.parse(css);

// Comments inside a selector are removed at tokenization and leave nothing
// behind — `.auth/* x */.auth-forget-farm` IS `.auth .auth-forget-farm` — so
// they are stripped rather than turned into whitespace.
const cleanSelector = (selector: string) => selector.replace(/\/\*[\s\S]*?\*\//g, "").trim();

// jsdom's Element.matches cannot see interaction pseudo-classes — `:hover`,
// `:focus`, `:focus-visible`, `:focus-within`, `:active` all report false on a
// static tree — so a floor spelled on such a selector would be silently
// dropped by a raw `el.matches`. This guard is a static floor sweep ("any
// matching rule must not floor below 44px"), and an interaction state can
// still shrink the touch target the instant the pointer arrives, so those
// pseudo-classes are treated as APPLICABLE: the selector is re-checked
// without them. Deliberately not a selector engine — only the listed
// interaction pseudo-classes are rewritten, and only as a whole
// `:pseudo` or `:pseudo(...)` piece (a class name merely containing `hover`
// is untouched).
// Longest names FIRST in the alternation: `focus` would otherwise consume
// the prefix of `:focus-visible`/`:focus-within` and leave a dangling
// `-visible`/`-within` in the normalised selector, which then matches nothing.
const INTERACTION_PSEUDOS = /:(focus-visible|focus-within|hover|focus|active)(\([^)]*\))?/g;
const applicableSelector = (selector: string) =>
  selector.replace(INTERACTION_PSEUDOS, "").trim();

/** The matching half of the size guard, shared by every size evaluator. */
function selectorMatches(el: Element, selector: string): boolean {
  const cleaned = cleanSelector(selector);
  try {
    return el.matches(cleaned) || el.matches(applicableSelector(cleaned));
  } catch {
    // A selector this DOM cannot parse cannot match our element either.
    return false;
  }
}

// The physical properties and their logical twins: the app is horizontal
// writing-mode throughout, so `min-height` floors the block axis exactly as
// `min-block-size` does, and `min-width` the inline axis as `min-inline-size`.
// Both axes are guarded: the inline axis is not a layout concern here — it is
// the SECOND axis of the 44px touch target, which a width alone does not
// guarantee (a 2.25rem-wide control is only 36px wide).
const FLOORS = [
  "min-block-size", "min-height",
  "min-inline-size", "min-width",
];

// The TRUE login ancestor chain (Login.tsx): the Forget button sits in the
// picker entry, in the picker, in the form card, in the auth page. Every
// selector-evaluation helper below must evaluate against this chain, so a
// later override spelled against any real ancestor — e.g. `.auth .card
// .auth-forget-farm` — is recognised instead of silently unmatched.
function buildForgetControl() {
  const main = document.createElement("div");
  main.className = "auth";
  const form = document.createElement("form");
  form.className = "card";
  const picker = document.createElement("div");
  picker.className = "auth-farm-picker";
  const group = document.createElement("div");
  group.setAttribute("role", "group");
  const entry = document.createElement("span");
  entry.className = "auth-farm-picker-entry";
  const el = document.createElement("button");
  el.type = "button";
  el.className = "auth-forget-farm";
  entry.append(el);
  group.append(entry);
  picker.append(group);
  form.append(picker);
  main.append(form);
  document.body.append(main);
  return { main, el };
}

/** Does this rule match the Forget control and declare a size floor? */
function floorDecls(rule: Rule, el: Element) {
  const matched = rule.selectors.some((selector) => selectorMatches(el, selector));
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
  // The true login topology (see buildForgetControl) — not a bare div: the
  // button must sit in its entry, group, picker, card and auth page for any
  // descendant selector the stylesheet (or a future override) may spell.
  const { main, el } = buildForgetControl();
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

  // The 44px floor is a TWO-AXIS requirement: a rule that declares a block
  // axis and says nothing about width (or vice versa) does not itself shrink
  // anything — but the only rule that ever establishes an axis's floor must
  // do so at 44px or above, in BOTH axes. Checked as a union, not per rule:
  // a later phone override declaring just one axis would otherwise look like
  // a rule missing the other.
  it.each(["block", "inline"])(
    "establishes a minimum of at least 44px on its %s axis, in some matching rule, in some context",
    (axis) => {
      const props = axis === "block" ? ["min-block-size", "min-height"] : ["min-inline-size", "min-width"];
      // Take the strongest (largest) floor declared on this axis by any
      // matching rule, in any context — that is what the element would end
      // up with if nothing stronger overrode it later.
      const entries = collectAxisFloors(props);
      expect(entries, `no matching rule declares any of ${props.join("/")} for .auth-forget-farm`).not.toHaveLength(0);
      let largest = -Infinity;
      let largestEntry: { value: string; selector: string } = entries[0];
      for (const entry of entries) {
        const pixels = px(entry.value);
        if (pixels !== null && pixels > largest) {
          largest = pixels;
          largestEntry = entry;
        }
      }
      expect(
        largest,
        `the strongest ${axis}-axis floor on the Forget target is "${largestEntry.value}" in "${largestEntry.selector}"; nothing reaches 44px on that axis`,
      ).toBeGreaterThanOrEqual(44);
    },
  );

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

  // Every floor declaration a matching rule makes for the given axis's
  // properties, with its selector.
  function collectAxisFloors(props: string[]): { value: string; selector: string }[] {
    // Same true topology as forgetFloors() (see buildForgetControl) — the
    // button must sit in its full login chain for the descendant selector
    // to match.
    const { main, el } = buildForgetControl();
    const found: { value: string; selector: string }[] = [];
    root.walkRules((rule) => {
      const matched = rule.selectors.some((selector) => selectorMatches(el, selector));
      if (!matched) return;
      for (const node of rule.nodes) {
        if (node.type === "decl" && props.includes(node.prop))
          found.push({ value: node.value, selector: rule.selector });
      }
    });
    main.remove();
    return found;
  }

  // PROOF that the chain above is real, not just plausible: a selector that
  // depends on the `form.card` ancestor (absent from the old flat fixture) is
  // recognised by the very evaluation the size guard uses. Without this, a
  // fixture regression back to a bare `.auth > span` would leave `.auth .card
  // .auth-forget-farm` unmatched and a 30px override riding on it would keep
  // the 44px guard green.
  it("recognises a selector that depends on the form.card ancestor, so a .card-scoped override cannot evade the guard", () => {
    const { main, el } = buildForgetControl();
    try {
      const probe = postcss.parse(
        ".auth .card .auth-forget-farm { min-inline-size: 30px; }\n"
        + "form.card .auth-forget-farm { min-block-size: 30px; }",
      );
      const rules: Rule[] = [];
      probe.walkRules((rule) => {
        rules.push(rule);
      });
      const override = rules[0];
      if (override === undefined) throw new Error("probe CSS parsed to no rule");
      const matched: { selector: string; hit: boolean }[] = [];
      for (const s of rules.flatMap((rule) => rule.selectors)) {
        matched.push({ selector: s, hit: selectorMatches(el, s) });
      }
      // The probe must have actually parsed to both override selectors — a
      // silent parse-to-empty would make every assertion below pass vacuously.
      expect(matched).toHaveLength(2);
      for (const { selector, hit } of matched) {
        expect(hit, `the guard's fixture chain does not match the real login topology's "${selector}"`).toBe(true);
      }
      // And the recognition has teeth: if the stylesheet carried that 30px
      // override, the guard's own evaluator would surface it as a floor.
      expect(floorDecls(override, el).length).toBe(1);
    } finally {
      main.remove();
    }
  });

  // Interaction-state floors are the P2 gap in this guard: jsdom's
  // Element.matches reports :hover/:focus/:focus-visible/:focus-within/
  // :active as false on a static tree, so a floor spelled on one of those
  // selectors used to be silently skipped by floorDecls/collectAxisFloors —
  // a 30px floor riding on :hover would keep every other case green. The
  // static sweep treats those pseudo-classes as applicable (the pointer
  // arriving IS an interaction state), and the rewrite must be conservative:
  // only a whole :pseudo piece is removed, never a class that merely
  // contains the word, and the raw-selector path is kept as the primary
  // check.
  it("treats interaction-state selectors as applicable, so a :hover floor cannot evade the sweep", () => {
    const probe = postcss.parse(
      ".auth .card .auth-forget-farm:hover { min-inline-size: 30px; }\n"
      + ".auth .auth-forget-farm:focus-visible { min-block-size: 30px; }\n"
      + ".auth .auth-forget-farm:hover:not(:disabled) { min-width: 30px; }",
    );
    const rules: Rule[] = [];
    probe.walkRules((rule) => {
      rules.push(rule);
    });
    expect(rules, "probe CSS parsed to no rules").toHaveLength(3);

    const { main, el } = buildForgetControl();
    try {
      // The raw DOM does not see interaction pseudo-classes — the premise the
      // guard's normalization exists to override. Without this, a future
      // jsdom that DOES report :hover would change the guard's behaviour
      // silently.
      expect(el.matches(".auth .card .auth-forget-farm:hover")).toBe(false);

      const [hoverRule, focusVisibleRule, hoverNotDisabledRule] = rules;
      expect(hoverRule).toBeDefined();
      expect(focusVisibleRule).toBeDefined();
      expect(hoverNotDisabledRule).toBeDefined();

      // Each probe variant is asserted directly through the guard's own
      // matching: floorDecls must surface the sub-44px floor on every one of
      // them, not just the plain :hover case. The :focus-visible variant is
      // the one that pins the alternation order — a `focus`-before-`focus-`
      // `visible` regex leaves a dangling "-visible" that matches nothing.
      expect(floorDecls(hoverRule as Rule, el).map((d) => [d.prop, d.value])).toEqual([
        ["min-inline-size", "30px"],
      ]);
      expect(floorDecls(focusVisibleRule as Rule, el).map((d) => [d.prop, d.value])).toEqual([
        ["min-block-size", "30px"],
      ]);
      expect(
        floorDecls(hoverNotDisabledRule as Rule, el).map((d) => [d.prop, d.value]),
      ).toEqual([
        ["min-width", "30px"],
      ]);

      // Conservatism of the rewrite: it removes whole :pseudo pieces only,
      // leaving everything else — including a class that merely contains a
      // pseudo's name — token-for-token intact.
      expect(applicableSelector(".auth .hover .auth-forget-farm")).toBe(
        ".auth .hover .auth-forget-farm",
      );
      expect(applicableSelector(".auth .auth-forget-farm:hover:not(:disabled)"))
        .toBe(".auth .auth-forget-farm:not(:disabled)");
      // Longest-first: the hyphenated twins must normalise to nothing, not
      // to a dangling "-visible"/"-within" suffix.
      expect(applicableSelector(".auth .auth-forget-farm:focus-visible")).toBe(
        ".auth .auth-forget-farm",
      );
      expect(applicableSelector(".auth .auth-forget-farm:focus-within")).toBe(
        ".auth .auth-forget-farm",
      );
      // Plain :focus and :active are treated the same as the rest.
      expect(applicableSelector(".auth .auth-forget-farm:focus")).toBe(
        ".auth .auth-forget-farm",
      );
      expect(applicableSelector(".auth .auth-forget-farm:active")).toBe(
        ".auth .auth-forget-farm",
      );
    } finally {
      main.remove();
    }
  });

  it("does not make the farm-selection chip destructive", () => {
    // The select chip shares the entry wrapper; a later edit that moves the
    // destructive colour onto the chip would pass the size guard above and
    // still mislead the operator. Assert the chip declares no --danger colour.
    // (The chip lives in the same true chain as the Forget button, minus the
    // button itself — the descendant selectors the chip guard cares about do
    // not reference the button.)
    const { main, el } = buildForgetControl();
    const chip = el.cloneNode() as HTMLElement;
    chip.className = "auth-farm-picker-select";
    el.replaceWith(chip);
    const colours: string[] = [];
    root.walkRules((rule) => {
      const matched = rule.selectors.some((selector) => {
        try {
          return chip.matches(cleanSelector(selector));
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
