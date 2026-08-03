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
// specificity, then source order. Specificity is counted as classes/attributes/
// pseudo-classes, which is all this stylesheet uses for these rules — an id or
// an inline style would need more, and there are none.
function specificityOf(selector: string): number {
  return (selector.match(/[.[:]/g) ?? []).length;
}

/**
 * The winning `max-width` for an element carrying `classes`, considering only
 * rules whose at-rule context passes `applies`.
 */
function effectiveMaxWidth(classes: string[], applies: (chain: AtRule[]) => boolean) {
  const el = document.createElement("div");
  el.className = classes.join(" ");
  document.body.append(el);

  const candidates: Candidate[] = [];
  let order = 0;
  root.walkRules((rule) => {
    order += 1;
    if (!applies(context(rule))) return;
    for (const selector of rule.selectors) {
      // A comment inside a selector is removed at tokenization and leaves
      // nothing behind — `.dialog/* x */.wide` is `.dialog.wide` — so it is
      // stripped rather than turned into a space.
      const clean = selector.replace(/\/\*[\s\S]*?\*\//g, "").trim();
      let hit = false;
      try {
        hit = el.matches(clean);
      } catch {
        // A selector this DOM cannot parse cannot match our element either.
        continue;
      }
      if (!hit) continue;
      rule.walkDecls("max-width", (decl) => {
        candidates.push({
          value: decl.value.trim(),
          important: decl.important === true,
          specificity: specificityOf(clean),
          order,
        });
      });
    }
  });

  el.remove();
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
