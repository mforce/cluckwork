import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import postcss from "postcss";
import type { AtRule, Container, Document, Rule } from "postcss";

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
// That applies to the ELEMENT NAMES too, and the first version of this file got
// it wrong: it matched a fixed list of fifteen, so a global `fieldset { ... }`
// or `img { ... }` would have passed without being looked at. Any type selector
// counts now, which is the same "walk everything, exclude deliberately" rule
// turned on the guard's own input. The one exclusion is written down below.

const css = readFileSync(resolve(process.cwd(), "src/styles.css"), "utf8");

/** Split one selector into its compounds, ignoring combinators inside `()` or `[]`. */
function compounds(selector: string): string[] {
  const out: string[] = [];
  let depth = 0;
  let current = "";
  for (const ch of selector) {
    if (ch === "(" || ch === "[") depth += 1;
    else if (ch === ")" || ch === "]") depth -= 1;
    if (depth === 0 && (ch === " " || ch === ">" || ch === "+" || ch === "~")) {
      if (current) out.push(current);
      current = "";
      continue;
    }
    current += ch;
  }
  if (current) out.push(current);
  return out;
}

const WHERE = /:where\(([^()]*(?:\([^()]*\)[^()]*)*)\)/g;

/**
 * Every compound a selector can match on, `:where()` arguments included.
 *
 * Reading the demoted form as "no longer names an element" would make this
 * guard blind to the very rules it exists to hold: `:where(button)` still
 * styles every raw `<button>` in the app.
 */
function allCompounds(selector: string): string[] {
  const inner = [...selector.matchAll(WHERE)].flatMap((m) => m[1].split(",").map((s) => s.trim()));
  const outer = selector.replace(WHERE, "");
  return [outer, ...inner].filter(Boolean).flatMap(compounds);
}

const tagOf = (compound: string) => /^(\*|[a-zA-Z][a-zA-Z0-9-]*)/.exec(compound)?.[1] ?? null;

interface BareRule {
  selector: string;
  /** No class and no id anywhere, so it can match MUI's DOM on any screen. */
  global: boolean;
  props: string;
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

function bareElementRules(): BareRule[] {
  const found: BareRule[] = [];
  postcss.parse(css).walkRules((rule) => {
    if (inKeyframes(rule)) return;
    for (const selector of rule.selectors) {
      const parts = allCompounds(selector);
      if (!parts.some((part) => tagOf(part) !== null)) continue;
      found.push({
        selector,
        global: !parts.some((part) => /[.#]/.test(part)),
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
 * and it is supposed to reach MUI's transitions as well as the app's own.
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
    // moves with every screen slice and 181 rules name an element today.
    expect(rules.length).toBeGreaterThan(150);
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
      expect(allCompounds(rule.selector).some((part) => /[.#]/.test(part)),
        `${rule.selector} names an element and is scoped by nothing`).toBe(true);
    }
  });
});
