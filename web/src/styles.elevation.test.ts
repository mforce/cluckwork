import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import postcss from "postcss";
import type { AtRule, Node, Rule } from "postcss";

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
// A shadow can be painted more ways than a list can keep up with. Round 1 of
// review found `filter: drop-shadow()` escaping a walk that enumerated
// `box-shadow`. Round 2 found `Filter:`, `DROP-SHADOW(` and
// `filter: var(--token)` escaping a walk that enumerated three property names.
// Two misses of the same shape mean the METHOD is wrong, so this walks EVERY
// declaration and asks one question of each, rather than listing the
// properties someone thought of.
//
// Case is folded on both sides: CSS property names and function names are
// case-insensitive, and postcss compares them as exact strings.
//
// A value that merely MENTIONS drop-shadow counts, which is what catches a
// custom property defining one for a later var() to pick up. The shadow is
// caught where it enters the stylesheet, not where it is used.
// A @keyframes frame IS a rule — its selector is `from`, `to` or a percentage —
// so the rule-parent check below does not exclude it, and walking every
// declaration reaches inside animations. An animation that legitimately tweens
// a shadow would otherwise put `from` into the set and fail a guard that is
// about which SURFACES float. A keyframe is a point in time, not a surface.
//
// Found by review round 3 against increment 2's own fix: it does not let a
// regression through, it blocks correct work, which is why it was latent —
// no keyframe animates a shadow today.
//
// The name test is suffix-based so it covers `-webkit-keyframes` too, and it
// walks the whole ancestor chain because a keyframes block can itself be
// nested inside a @media.
function insideKeyframes(node: Node | undefined): boolean {
  let current: Node | undefined = node;
  while (current !== undefined) {
    if (current.type === "atrule" && /(^|-)keyframes$/i.test((current as AtRule).name)) return true;
    current = current.parent as Node | undefined;
  }
  return false;
}

function selectorsCastingShadow(): string[] {
  const found = new Set<string>();
  root.walkDecls((d) => {
    // Walking everything reaches declarations inside @font-face, @property and
    // @keyframes, whose parent is not a rule and has no selectors. That is a
    // crash rather than a miss, so it is excluded explicitly.
    const parent = d.parent;
    if (parent === undefined || parent.type !== "rule") return;
    if (insideKeyframes(parent)) return;

    const prop = d.prop.toLowerCase();
    const value = d.value.toLowerCase();
    const casts = prop === "box-shadow"
      ? castsShadow(value)
      : value.includes("drop-shadow");
    if (!casts) return;

    for (const sel of (parent as Rule).selectors) found.add(clean(sel));
  });
  return [...found].sort();
}

// #823 wrapped the bare-element rules in `:where()` to keep them below one
// Emotion class. `input`'s radius is still declared by the same rule, so this
// looks through the wrapper rather than losing the assertion to a rename.
const unwrap = (selector: string) =>
  selector.replace(/^:where\((.*)\)$/, "$1").split(",").map((s) => s.trim());

function declarationsFor(selector: string): Map<string, string> {
  const decls = new Map<string, string>();
  root.walkRules((rule: Rule) => {
    if (!rule.selectors.map(clean).flatMap(unwrap).includes(selector)) return;
    rule.walkDecls((d) => { decls.set(d.prop, d.value); });
  });
  return decls;
}

// The nearest enclosing `@media` rule's `params`, or `undefined` at the top
// level. Same shape as `insideKeyframes` above: walk the parent chain rather
// than assume one nesting depth.
function enclosingMediaParams(node: Node | undefined): string | undefined {
  let current: Node | undefined = node;
  while (current !== undefined) {
    if (current.type === "atrule" && (current as AtRule).name === "media") {
      return (current as AtRule).params;
    }
    current = current.parent as Node | undefined;
  }
  return undefined;
}

// `declarationsFor` MERGES every matching rule into one map in document
// order, so a selector declared once at the top level and again inside a
// LATER `@media` block silently loses the top-level value to the media one —
// found by a Codex review of #882 (2026-09-16) against the ".dialog" case
// below: a mutation of the unconditional `.dialog` radius (styles.css:634)
// passed the old assertion because it read back the phone media query's
// value instead. This reads only rules whose nearest `@media` matches
// `mediaParams` exactly (`undefined` for "not inside any @media").
function declarationsForAt(selector: string, mediaParams: string | undefined): Map<string, string> {
  const decls = new Map<string, string>();
  root.walkRules((rule: Rule) => {
    if (!rule.selectors.map(clean).flatMap(unwrap).includes(selector)) return;
    if (enclosingMediaParams(rule) !== mediaParams) return;
    rule.walkDecls((d) => { decls.set(d.prop, d.value); });
  });
  return decls;
}

// Everything allowed to cast a shadow, and why. Five floats plus one ring.
//
// `.tabbar` retired here in #829: the mobile tab bar is now MUI
// `BottomNavigation`, themed with `boxShadow: "none"` (variant B, "ruled" —
// a hairline top rule instead of a shadow, owner pick 2026-09-16). Its
// successor assertion, "gives the tab bar no shadow and a hairline top rule
// instead (variant B)" in `web/src/theme/farmTheme.policy.test.ts`, already
// landed with #864's theme overrides — #829 needs no new row, only this
// retirement.
const SHADOW_ALLOWED = [
  ".auth .card",            // the sign-in card, floating on the auth gradient
  ".dialog",                // a modal, over its backdrop
  ".entry-foot",            // the Daily entry sticky action bar
  ".glossary-entry:target", // not elevation: a spread-only deep-link halo
  ".named-picker-listbox",  // the picker popover, over the form beneath it
  ".update-banner",         // the service-worker update prompt
].sort();

describe("#651 elevation: only a float casts a shadow", () => {
  it("no rule outside the float set casts a drop shadow", () => {
    expect(selectorsCastingShadow()).toEqual(SHADOW_ALLOWED);
  });

  // #829 — `.panel` retired from this list: the selector no longer exists
  // (the Dashboard's cards are gone, not migrated onto `MuiCard`), and
  // `declarationsFor` on a selector nothing declares returns an empty map,
  // so keeping it here would pass vacuously — exactly the trap 822's D4
  // named for this rule ("passes vacuously once those selectors are gone").
  // No MuiCard successor exists for `.panel` specifically because nothing
  // replaced it with a card; `.card`/`.order-panel` below still do, and
  // `farmTheme.policy.test.ts`'s "makes a Card a hairline box" is their G2.
  it("a card and an order panel carry a border and nothing else", () => {
    for (const selector of [".card", ".order-panel"])
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
    // #864 (owner decision, 2026-09-16): controls 4px, cards/panels 8px,
    // dialogs 12px. D6's "the scale does not move" is superseded.
    expect(tokenValue("--r-input")).toBe("4px");
    expect(tokenValue("--r-panel")).toBe("8px");
    expect(tokenValue("--r-card")).toBe("12px");
  });

  // Every surface this slice owns, INCLUDING two --r-input consumers. Without
  // those the scale guard would assert nothing about the one token whose value
  // actually changes, and would read as safety it does not provide.
  //
  // #829 retires `.panel` and `.capture-tile` from both lists below: the
  // Dashboard no longer renders either selector (it is `sx`-laid-out MUI),
  // so a radius token on a selector nothing renders would be a guard reading
  // as safety it does not provide (AGENTS.md, "writing a guard"). `.card`
  // and `.order-panel` stay — other screens still convert their own cards
  // in #831 to #833.
  it.each([
    ".toolbar",
    ".card",
    ".order-panel",
    ".entry-pane",
    "input",
    ".named-picker-trigger",
  ])("%s resolves its radius through a token, not a literal", (selector) => {
    const radius = declarationsFor(selector).get("border-radius");
    expect(radius).toMatch(/^var\(--r-[a-z]+\)$/);
  });

  // #864 narrows the meaning of --r-card to dialogs and sheets only: every
  // card-like surface reads --r-panel instead. A generic "some r-* token"
  // pattern match (above) would stay green if one of these silently reverted
  // to --r-card, so this pins the SPECIFIC token per surface.
  it.each([
    ".card", ".order-panel", ".entry-pane",
    ".help-hero", ".logo-preview", ".banner-preview", ".farm-warning",
    ".palette-picker",
  ])("%s reads --r-panel, not the dialog radius", (selector) => {
    expect(declarationsFor(selector).get("border-radius")).toBe("var(--r-panel)");
  });

  // The dialog family is the one place --r-card still belongs: a modal and
  // its phone-sheet variant. Checked as two SEPARATE declarations, not one
  // merged lookup — declarationsFor(".dialog") would report only the phone
  // media query's value here, because it is declared later in the file and
  // overwrites the unconditional one in the merged map.
  it("the unconditional dialog radius keeps --r-card", () => {
    expect(declarationsForAt(".dialog", undefined).get("border-radius")).toBe("var(--r-card)");
  });

  it("the phone dialog-sheet radius keeps --r-card", () => {
    expect(declarationsForAt(".dialog", "(max-width: 900px)").get("border-radius"))
      .toBe("var(--r-card) var(--r-card) 0 0");
  });
});
