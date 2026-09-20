import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import postcss from "postcss";
import type { AtRule, Container, Document, Rule } from "postcss";
import selectorParser from "postcss-selector-parser";
import type { Node as SelectorNode, Selector } from "postcss-selector-parser";

// #823 §2.3 — the rules in `styles.css` that style an element by NAME.
//
// Emotion emits one class per component, so a rule whose selector names an
// element and carries no class of its own reaches MUI's DOM on every screen,
// converted or not. Five of them out-specify that single class in their
// compound forms: `button:hover:not(:disabled)` (0,2,1) beats
// `.MuiButton-contained:hover` (0,2,0) and repaints every text button with a
// brand fill; `input[type="checkbox"]` (0,1,1) beats `.MuiSwitchBase-input`
// (0,1,0) and shrinks MUI's full-size hidden hit target to 16px on a PWA whose
// own guards enforce 44.
//
// So #823 wraps each of them in `:where()`, which contributes no specificity.
// The app's own raw controls keep the values — nothing else declares them —
// and any Emotion class now outranks the whole family. Nothing is deleted:
// those raw consumers survive to #833.
//
// THIS WALKS THE FILE rather than checking the selectors #823 happened to
// touch. A hand list is what AGENTS.md's guard rules call the thing a guard
// exists to stop anyone trusting, and the point of this one is that the NEXT
// bare-element rule anybody adds arrives here red.
//
// Four rounds of local review, each against the round before, moved this file
// from a hand-kept element list to a hand-rolled regex parser and finally to a
// real selector AST — AGENTS.md's guard rule for exactly this shape ("two
// misses of the same shape mean the METHOD is wrong"):
//
// 1. A fixed list of fifteen element names let a global `fieldset { ... }` or
//    `img { ... }` pass unseen. Fix: treat any type selector as naming an
//    element, not a hand-kept list.
// 2. "Scoped" meant "the compound's text contains a `.` or `#` anywhere",
//    which read a `:not(.legacy)` argument as scope and never looked inside
//    `:is()` for a nested tag. Fix: strip `:not()`, walk into `:is()`/
//    `:where()`.
// 3. `:is()`/`:where()` alternatives were flattened into one shared list
//    before asking "does anything have scope", so `.legacy`'s class in
//    `:is(button, .legacy)` vouched for the unrelated `button` alternative,
//    and only one level of nesting was ever expanded. Fix: judge scope per
//    alternative, recursively — still on regex.
// 4. The regex's own depth limit and its blindness to WHERE in the selector
//    text a `.`/`#` sits broke again: `:is(.legacy, :is(:is(button)))`
//    defeated the depth-limited capture group, and `input[value=
//    "name@example.com"]` read `.com` inside an ATTRIBUTE VALUE as a class.
//    Both are string-level failures no amount of additional regex patching
//    fixes, because the regex has no concept of "inside an attribute value"
//    versus "a selector". Fix: parse with `postcss-selector-parser` instead
//    of hand-rolled text scanning — a class/id token is only ever read from
//    an actual `class`/`id` AST node, and an attribute's value is a distinct
//    field on an `attribute` node that this walk never treats as selector
//    text. Every one of the eight mutations from all four rounds is a
//    permanent regression case below, plus `!important` (next paragraph).
//
// A parallel finding in the SAME round: the declaration pin recorded only
// property NAMES, so `background: var(--brand) !important` on a demoted rule
// changed nothing the pin could see — and `!important` wins the cascade
// regardless of `:where()`'s zero specificity, defeating the whole technique
// silently. Every rule this walk finds is now also checked for it.

const css = readFileSync(resolve(process.cwd(), "src/styles.css"), "utf8");

// MUI's and Emotion's own naming: `MuiButton-root` (component-slot, capital
// after `Mui`) and `Mui-disabled` (global state, hyphen after `Mui`) — never
// a lowercase letter, which this app's own class names would use. `css-
// 1a2b3c` is Emotion's generated class. A rule reaching MUI through one of
// these alone is exactly the unscoped case this guard exists to catch, so
// neither counts as this app's own scope. Selector-parser strips the leading
// `.`/`#` from `class`/`id` node values, so this matches the bare name.
const MUI_OR_EMOTION_OWNED = /^(Mui[A-Z-]|css-)/;

interface Reading {
  /** The element tag this specific reading matches, if any. */
  tag: string | null;
  /** Whether THIS reading, alone, needs no app-owned scope to match it. */
  scoped: boolean;
}

/** One maximal run of simple-selector nodes between combinators. */
function compoundGroups(selector: Selector): SelectorNode[][] {
  const groups: SelectorNode[][] = [];
  let current: SelectorNode[] = [];
  for (const node of selector.nodes) {
    if (node.type === "combinator") {
      if (current.length) groups.push(current);
      current = [];
      continue;
    }
    if (node.type === "comment") continue;
    current.push(node);
  }
  if (current.length) groups.push(current);
  return groups;
}

/**
 * Every way a single compound (the simple-selector nodes between two
 * combinators, e.g. `button:hover:not(:disabled)` or `.card`) can be read.
 *
 * A `class`/`id` node here is real app-owned scope, or MUI's/Emotion's own if
 * it matches their naming — never a string that merely CONTAINS one, so an
 * attribute value like `name@example.com` cannot be misread as a class.
 * `:not()`'s argument is walked (nothing hides unexamined) but contributes
 * neither a tag nor scope — a negation excludes a match, it does not grant
 * one. `:is()`/`:where()` fan out into one reading per alternative,
 * recursively via `chainReadings`, each ANDed with this compound's own scope
 * (which applies regardless of which alternative is chosen).
 */
function compoundReadings(nodes: SelectorNode[]): Reading[] {
  let ownTag: string | null = null;
  let ownScope = false;
  const fanOuts: Reading[][] = [];

  for (const node of nodes) {
    if (node.type === "tag") { ownTag = node.value; continue; }
    if (node.type === "universal") { ownTag = ownTag ?? "*"; continue; }
    if (node.type === "class" || node.type === "id") {
      if (!MUI_OR_EMOTION_OWNED.test(node.value)) ownScope = true;
      continue;
    }
    // `attribute` never contributes: its VALUE is a distinct structured
    // field, not selector text, so `[value="name@example.com"]` grants
    // neither a tag nor a class token.
    if (node.type === "attribute") continue;
    if (node.type === "pseudo") {
      if (node.value === ":is" || node.value === ":where") {
        fanOuts.push(node.nodes.flatMap((alt) => chainReadings(alt)));
      }
      // `:not()`, `:has()` and anything else: visited, contributes nothing.
      // `:not()` excludes a match rather than granting one; `:has()`
      // constrains a DIFFERENT element (a descendant/sibling), not this one.
      continue;
    }
    // nesting ("&"), string, comment: not a simple selector, ignored.
  }

  if (fanOuts.length === 0) return [{ tag: ownTag, scoped: ownScope }];
  // Cross product across multiple :is()/:where() occurrences in ONE compound
  // (rare — none in this file — but correct rather than silently dropped).
  return fanOuts.reduce<Reading[]>(
    (combos, fanOut) => combos.flatMap((base) => fanOut.map((alt) => ({
      tag: alt.tag ?? base.tag,
      scoped: base.scoped || alt.scoped,
    }))),
    [{ tag: ownTag, scoped: ownScope }],
  );
}

/**
 * Every tag this selector (a chain of compounds joined by combinators) can be
 * read as reaching, each already combined with the scope its OWN compound
 * needs AND whatever every OTHER compound in the chain needs — an ancestor
 * compound still constrains which concrete elements this selector can reach,
 * even when the tag-bearing compound itself has no scope of its own. The
 * same function serves the top-level selector and every `:is()`/`:where()`
 * alternative: both are "a chain of compounds", and an alternative's own
 * ancestor compound (`:is(div button)`) needs the identical treatment.
 */
function chainReadings(selector: Selector): Reading[] {
  const groups = compoundGroups(selector);
  const perCompound = groups.map(compoundReadings);
  const results: Reading[] = [];
  perCompound.forEach((readings, index) => {
    const everyOtherCompoundHasAnUnscopedReading = perCompound.every(
      (other, otherIndex) => otherIndex === index || other.some((r) => !r.scoped),
    );
    for (const reading of readings) {
      if (reading.tag === null) continue;
      results.push({
        tag: reading.tag,
        scoped: reading.scoped || !everyOtherCompoundHasAnUnscopedReading,
      });
    }
  });
  return results;
}

interface BareRule {
  selector: string;
  /** Whether some reading of this selector names an element with no app-owned scope anywhere it needs one. */
  global: boolean;
  props: string;
  /** Whether any declaration in this rule carries `!important`, which wins regardless of `:where()`'s zero specificity. */
  important: boolean;
  /** The one deliberate exception to the `!important` check — see `inReducedMotionOverride`. */
  reducedMotionOverride: boolean;
}

/**
 * The deliberate exclusion: `@keyframes` selectors.
 *
 * `from` and `to` parse as type selectors and are not — they name a position on
 * a timeline and match no element, so counting them would put six phantom rows
 * in a pin whose whole job is to say which rules reach MUI's DOM. A percentage
 * keyframe (`50%`) does not parse as a tag and needs no exclusion.
 */
const inKeyframes = (rule: Rule) => {
  for (let node: Container | Document | undefined = rule.parent; node; node = node.parent) {
    if (node.type === "atrule" && /keyframes$/.test((node as AtRule).name)) return true;
  }
  return false;
};

/**
 * The deliberate exclusion from the `!important` check: this app's own
 * `prefers-reduced-motion: reduce` override. It MUST win regardless of
 * specificity — that is the entire point of `!important` here, not a bypass
 * of it, so it is a structural exemption (which `@media` this rule sits
 * inside) rather than a text match on the selector, and the test below
 * pins exactly what it excuses so a second rule cannot silently ride along.
 */
const inReducedMotionOverride = (rule: Rule) => {
  for (let node: Container | Document | undefined = rule.parent; node; node = node.parent) {
    if (node.type === "atrule" && (node as AtRule).name === "media"
      && /prefers-reduced-motion:\s*reduce/.test((node as AtRule).params)) return true;
  }
  return false;
};

/** `source` defaults to the real file; the mutation tests pass a synthetic addition. */
function bareElementRules(source: string = css): BareRule[] {
  const found: BareRule[] = [];
  postcss.parse(source).walkRules((rule) => {
    if (inKeyframes(rule)) return;
    const important = rule.nodes
      .filter((node) => node.type === "decl")
      .some((node) => node.important === true);
    const reducedMotionOverride = inReducedMotionOverride(rule);
    for (const selector of rule.selectors) {
      const readings = chainReadings(selectorParser().astSync(selector).at(0));
      if (readings.length === 0) continue;

      found.push({
        selector,
        global: readings.some((r) => !r.scoped),
        important,
        reducedMotionOverride,
        props: rule.nodes
          .filter((node) => node.type === "decl")
          .map((node) => node.prop)
          .join(" "),
      });
    }
  });
  return found;
}

/**
 * The rules that reach MUI's DOM anywhere in the app, with what each declares.
 *
 * Every entry is `:where()`-demoted except the one named below it. The
 * property list is part of the pin on purpose: adding a declaration to a rule
 * that already styles MUI's DOM is the same event as adding the rule.
 */
const DEMOTED: ReadonlyArray<readonly [selector: string, props: string]> = [
  // Field Console headings use the same serif outside MUI Typography. Help is
  // the only screen that renders raw headings today, so this stays at h1–h3.
  [":where(h1, h2, h3)", "font-family"],
  [":where(h1, h2, h3, h4)", "font-weight letter-spacing line-height"],
  [
    ":where(button)",
    "padding border border-radius background color font font-size font-weight "
      + "letter-spacing cursor transition",
  ],
  [":where(button:hover:not(:disabled))", "background"],
  [":where(button:active:not(:disabled))", "transform"],
  [":where(button:disabled)", "opacity cursor"],
  [":where(label)", "display flex-direction gap font-size font-weight color"],
  [":where(input, select, textarea)", "padding border border-radius font font-size color background"],
  [":where(input:focus, select:focus, textarea:focus)", "outline outline-offset border-color"],
  [':where(input[type="checkbox"])', "accent-color width height"],
  [':where(button:disabled[aria-busy="true"])', "opacity"],
];

/**
 * The global rules that are deliberately NOT demoted.
 *
 * The first two are the app's baseline, and they are exactly what MUI's own
 * `CssBaseline` sets from these same tokens — so they are supposed to reach
 * MUI's DOM. #823 tried to hand them over and could not: the production CSP is
 * `style-src 'self'`, so the browser refuses Emotion's injected stylesheet and
 * the baseline never applies. They go when that is settled.
 *
 * `prefers-reduced-motion` is an accessibility override carrying `!important`,
 * and it is supposed to reach MUI's transitions as well as the app's own —
 * the one place in the file `!important` is correct rather than a bypass, and
 * it is asserted below rather than just excused.
 */
const DELIBERATE: ReadonlyArray<readonly [selector: string, props: string]> = [
  ["*", "box-sizing"],
  ["body", "margin background color -webkit-font-smoothing"],
  ["*", "transition animation"],
];

describe("bare element selectors against MUI's DOM (#823)", () => {
  const rules = bareElementRules();

  it("is walking a stylesheet it can actually see", () => {
    // Non-vacuity. A broken parse or a renamed file would otherwise turn every
    // assertion below into a walk over nothing. A floor, not a pin: the count
    // moves with every screen slice and 149 rules name an element today (down
    // from 181 — #826 retired the picker's `.named-picker-control input`
    // family and `button.named-picker-trigger` and its states, seven SCOPED
    // readings of `input`/`button` that never counted toward `global` here).
    expect(rules.length).toBeGreaterThan(120);
    expect(rules.filter((rule) => rule.global).length).toBe(DEMOTED.length + DELIBERATE.length);
  });

  it("pins every rule that styles an element by name across the whole app", () => {
    const actual = rules.filter((rule) => rule.global).map((rule) => [rule.selector, rule.props]);
    const expected = [...DEMOTED, ...DELIBERATE].map(([selector, props]) => [selector, props]);
    // Sorted, because the pin is about the SET and its declarations, not about
    // where in a 3,600-line file each rule happens to sit (#632).
    const key = (row: readonly [string, string] | string[]) => `${row[0]} {${row[1]}}`;
    expect(actual.map(key).sort()).toEqual(expected.map(key).sort());
  });

  it("gives every pinned rule zero specificity, so one Emotion class outranks it", () => {
    for (const [selector] of DEMOTED) {
      expect(selector, `${selector} still out-specifies a component class`)
        .toMatch(/^:where\(.*\)$/);
    }
  });

  it("keeps every other bare-element rule inside a container this app owns", () => {
    // The other 108 reach MUI's DOM only under a class `styles.css` declares
    // (`.dialog input`, `table.data td`, `.form-grid label`), so each dies with
    // its screen's slice rather than needing neutralising now. A new rule with
    // no such container lands in the pin above instead, and fails there.
    for (const rule of rules.filter((r) => !r.global)) {
      expect(rule.global, `${rule.selector} names an element and is scoped by nothing this app owns`)
        .toBe(false);
    }
  });

  it("carries no !important on a rule that reaches MUI's DOM, except the reduced-motion override", () => {
    // `!important` wins the cascade outright, so it defeats `:where()`'s zero
    // specificity exactly as completely as dropping the `:where()` would —
    // the property pin above never saw it, because it only ever recorded
    // property NAMES.
    const exempt = rules.filter((rule) => rule.reducedMotionOverride);
    // Pin exactly what the exemption excuses, so a second rule landing inside
    // `@media (prefers-reduced-motion: reduce)` cannot silently ride along
    // with it — this is the DELIBERATE list's third row, the one place
    // `!important` is correct rather than a bypass.
    expect(exempt.map((rule) => [rule.selector, rule.props])).toEqual([["*", "transition animation"]]);

    for (const rule of rules) {
      if (rule.reducedMotionOverride) continue;
      expect(rule.important, `${rule.selector} uses !important, which wins regardless of specificity`)
        .toBe(false);
    }
  });
});

describe("bare-element guard catches its own bypasses", () => {
  // A control: an unscoped element rule this guard has always caught. Proves
  // the harness below can go red at all before trusting the ones it could
  // not, pre-fix — see the file header for what each round missed and why.
  it("catches an unscoped rule naming an element outright", () => {
    const mutated = bareElementRules(`${css}\nfieldset { border: none; }`);
    expect(mutated.some((rule) => rule.selector === "fieldset" && rule.global)).toBe(true);
  });

  it("catches a :not() argument used as if it were scope", () => {
    const mutated = bareElementRules(`${css}\nbutton:not(.legacy) { background: red; }`);
    expect(mutated.some((rule) => rule.selector === "button:not(.legacy)" && rule.global)).toBe(true);
  });

  it("catches a type selector nested inside :is()", () => {
    const mutated = bareElementRules(`${css}\n:is(button) { background: red; }`);
    expect(mutated.some((rule) => rule.selector === ":is(button)" && rule.global)).toBe(true);
  });

  it("does not flag a rule scoped by an ancestor's own app class", () => {
    const mutated = bareElementRules(`${css}\n.card button:not(.legacy) { background: red; }`);
    expect(mutated.some((rule) => rule.selector === ".card button:not(.legacy)" && rule.global))
      .toBe(false);
  });

  it("does not accept a MUI-owned class as this app's scope", () => {
    const mutated = bareElementRules(`${css}\n.MuiButton-root button { background: red; }`);
    expect(mutated.some((rule) => rule.selector === ".MuiButton-root button" && rule.global))
      .toBe(true);
  });

  it("does not let one :is() alternative's class vouch for a sibling alternative", () => {
    const mutated = bareElementRules(`${css}\n:is(button, .legacy) { background: red; }`);
    expect(mutated.some((rule) => rule.selector === ":is(button, .legacy)" && rule.global))
      .toBe(true);
  });

  it("catches a type selector nested two levels inside :is()", () => {
    const mutated = bareElementRules(`${css}\n:is(:is(button)) { background: red; }`);
    expect(mutated.some((rule) => rule.selector === ":is(:is(button))" && rule.global))
      .toBe(true);
  });

  it("catches a class buried inside a nested :is() alternative list", () => {
    const mutated = bareElementRules(`${css}\n:is(.legacy, :is(:is(button))) { background: red; }`);
    expect(mutated.some((rule) => rule.selector === ":is(.legacy, :is(:is(button)))" && rule.global))
      .toBe(true);
  });

  it("does not read an attribute VALUE as a class", () => {
    const mutated = bareElementRules(`${css}\ninput[value="name@example.com"] { background: red; }`);
    expect(mutated.some((rule) => rule.selector === 'input[value="name@example.com"]' && rule.global))
      .toBe(true);
  });

  it("catches !important, which wins regardless of :where()'s zero specificity", () => {
    const mutated = bareElementRules(`${css}\n:where(button) { color: red !important; }`);
    expect(mutated.some((rule) => rule.selector === ":where(button)" && rule.important)).toBe(true);
  });
});
