import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import postcss from "postcss";
import type { AtRule, Rule } from "postcss";

// What `max-width` a dialog panel actually ends up with — the guarantee behind
// the `wide` variant, which is CSS-only and therefore invisible to every other
// test in this suite (jsdom computes no layout and evaluates no media query).
//
// It exists because the rule is a cascade trap: `.dialog.wide { max-width:
// 52rem }` (0,2,0) OUTRANKS the ≤900px phone-sheet reset `.dialog { max-width:
// none }` (0,1,0), because a media query contributes nothing to specificity. So
// the wide cap survived on landscape phones between 832px and 900px — bottom
// sheet chrome with the backdrop showing down one side — and it needs an
// explicit override inside the media query to undo it.
//
// THIS IS A CASCADE EVALUATION, NOT A TEXT SEARCH, and that is the whole point.
// Three earlier text-matching versions were each defeated by a reviewer with
// CSS that is valid, equivalent, and spelled differently: a decoy inside a
// comment; a second conflicting rule that the cascade applies but a first-match
// read never saw; `@media (max-width: 900px) and (prefers-reduced-motion)`
// standing in for the plain condition; `.wide.dialog { … !important }`, whose
// text does not contain the substring ".dialog.wide" at all. Selectors are
// matched by asking the DOM, and declarations are read from a parsed AST, so
// none of those spellings can hide.
const css = readFileSync(resolve(process.cwd(), "src/styles.css"), "utf8");
const root = postcss.parse(css);

// Comments inside a selector are removed at tokenization and leave nothing
// behind — `.dialog/* x */.wide` IS `.dialog.wide` — so they are stripped
// rather than turned into whitespace.
const cleanSelector = (selector: string) => selector.replace(/\/\*[\s\S]*?\*\//g, "").trim();

// The physical property and its logical twin: the app is horizontal
// writing-mode throughout, so `max-inline-size` caps this panel exactly as
// `max-width` does and must not read as "no cap at all" (round 5).
const CAPS = ["max-width", "max-inline-size"];

const capsWidth = (rule: Rule): boolean =>
  rule.nodes.some((n) => n.type === "decl" && CAPS.includes(n.prop));

/** Does this rule set a width cap on something matching the panel element? */
function capsPanel(rule: Rule, el: Element): boolean {
  if (!capsWidth(rule)) return false;
  return rule.selectors.some((selector) => {
    try {
      return el.matches(cleanSelector(selector));
    } catch {
      return false;
    }
  });
}

/** A rule's at-rule ancestry, innermost last. */
function context(rule: Rule): AtRule[] {
  const chain: AtRule[] = [];
  for (let node = rule.parent; node && node.type !== "root"; node = node.parent) {
    if (node.type === "atrule") chain.unshift(node as AtRule);
  }
  return chain;
}

// Only the plain ≤900px phone condition counts. A narrower one (…and
// (prefers-reduced-motion: reduce)) or an extra @supports/@layer wrapper does
// NOT apply to an ordinary phone, so a fix scoped that way must not read as
// fixed. Written as an exact parse of the condition rather than a text match,
// so spacing and the `(width <= 900px)` range spelling both land here.
function isPhoneOnly(chain: AtRule[]): boolean {
  if (chain.length !== 1) return false;
  const [at] = chain;
  if (at.name !== "media") return false;
  const params = at.params.replace(/\s+/g, "");
  return params === "(max-width:900px)" || params === "(width<=900px)";
}

interface Candidate {
  value: string;
  important: boolean;
  specificity: number;
  order: number;
}

// Enough of the cascade for one property on one element: importance first, then
// specificity, then source order.
//
// The counter is deliberately narrow — classes, ids, elements — and REFUSES
// anything it cannot count faithfully rather than guessing. `:is()`/`:not()`
// take the specificity of their most specific argument, `:where()` takes none,
// and an attribute selector counts as a class; approximating those would let
// this file report a winner that a browser disagrees with, which is worse than
// no test (round 5). Nothing in this stylesheet needs them today; the day one
// does, this fails loudly and asks for a real selector parser.
const UNCOUNTABLE = /:is\(|:not\(|:where\(|:has\(|\[|::/;

function specificityOf(selector: string): number {
  if (UNCOUNTABLE.test(selector)) {
    throw new Error(
      `styles.dialog.test cannot compute specificity for "${selector}". `
      + "Add a real selector parser (postcss-selector-parser) rather than "
      + "letting this test approximate the cascade.",
    );
  }
  if (selector.includes("#")) throw new Error(`unsupported id selector "${selector}"`);
  // 100 per id (rejected above), 10 per class or pseudo-class, 1 per element.
  const classes = (selector.match(/\.[\w-]+|:[\w-]+/g) ?? []).length;
  const elements = (selector.match(/(^|[\s>+~])[a-zA-Z][\w-]*/g) ?? []).length;
  return classes * 10 + elements;
}

/**
 * The winning `max-width` for an element carrying `classes`, considering only
 * rules whose at-rule context passes `applies`.
 */
function effectiveMaxWidth(classes: string[], applies: (chain: AtRule[]) => boolean) {
  // The real topology, not a bare div: the panel is a role="dialog" element
  // inside .dialog-backdrop, and a selector like
  // `.dialog-backdrop > .dialog[role="dialog"]` would cap production while
  // silently failing to match a detached div here (round 5).
  const backdrop = document.createElement("div");
  backdrop.className = "dialog-backdrop";
  const el = document.createElement("div");
  el.className = classes.join(" ");
  el.setAttribute("role", "dialog");
  el.setAttribute("aria-modal", "true");
  el.tabIndex = -1;
  backdrop.append(el);
  document.body.append(backdrop);

  const candidates: Candidate[] = [];
  let order = 0;
  root.walkRules((rule) => {
    order += 1;
    const chain = context(rule);
    if (!applies(chain)) {
      // A context this file does not model (@layer, @supports, a nested media)
      // is not silently skipped when it could actually constrain the panel:
      // that is how a nested `!important` cap would pass unnoticed. Refuse.
      if (chain.length > 0 && !isPhoneOnly(chain) && capsPanel(rule, el)) {
        throw new Error(
          `styles.dialog.test cannot evaluate a rule for this panel inside `
          + `@${chain.map((a) => a.name).join(" > @")} ("${rule.selector}"). `
          + "Teach the evaluator that context, or assert the width in a real browser.",
        );
      }
      return;
    }
    for (const selector of rule.selectors) {
      const clean = cleanSelector(selector);
      let hit = false;
      try {
        hit = el.matches(clean);
      } catch {
        // A selector this DOM cannot parse cannot match our element either.
        continue;
      }
      if (!hit) continue;
      for (const prop of CAPS) {
        rule.walkDecls(prop, (decl) => {
          candidates.push({
            value: decl.value.trim(),
            important: decl.important === true,
            specificity: specificityOf(clean),
            order,
          });
        });
      }
    }
  });

  backdrop.remove();
  if (candidates.length === 0) return { value: null, count: 0 };
  const winner = candidates.reduce((best, next) =>
    (next.important !== best.important
      ? (next.important ? next : best)
      : next.specificity !== best.specificity
        ? (next.specificity > best.specificity ? next : best)
        : next.order >= best.order ? next : best));
  return { value: winner.value, count: candidates.length };
}

const unconditional = (chain: AtRule[]) => chain.length === 0;

// A phone viewport sees the unconditional rules TOO — the media block adds to
// them, it does not replace them. Scoping the phone case to media rules alone
// hid the very regression this file exists for: with the override deleted, the
// only phone-context rule left was `.dialog { max-width: none }` and the test
// read "none" while a real phone was reading 52rem off the desktop rule.
const onPhone = (chain: AtRule[]) => unconditional(chain) || isPhoneOnly(chain);

describe("dialog width rules", () => {
  it("caps the wide variant on a desktop viewport", () => {
    expect(effectiveMaxWidth(["dialog", "wide"], unconditional).value).toBe("52rem");
  });

  // The regression itself: the panel must be free to fill a phone sheet.
  it("releases that cap on a phone viewport", () => {
    expect(effectiveMaxWidth(["dialog", "wide"], onPhone).value).toBe("none");
  });

  it("leaves an ordinary dialog uncapped on a phone too, so the two agree", () => {
    expect(effectiveMaxWidth(["dialog"], onPhone).value).toBe("none");
  });

  // A rule that only applies under a narrower condition cannot stand in for the
  // phone rule. Asserted from the other side as well: SOMETHING must set the
  // property there, so an empty result reads as unfixed rather than as
  // "nothing to see".
  it("has a phone rule at all, rather than passing on absence", () => {
    expect(effectiveMaxWidth(["dialog", "wide"], isPhoneOnly).count).toBeGreaterThan(0);
  });
});
