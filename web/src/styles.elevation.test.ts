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

// `declarationsFor` MERGES every matching rule into one map in document
// order, so a selector declared once at the top level and again inside a
// LATER `@media` block silently loses the top-level value to the media one —
// found by a Codex review of #882 (2026-09-16) against the (now retired by
// #827) unconditional `.dialog` radius: a mutation of it passed the old
// assertion because it read back the phone media query's value instead. The
// media-scoped reader that fixed it (`declarationsForAt` plus
// `enclosingMediaParams`) is deleted along with the last selector it was
// built to check — `.dialog` no longer carries a radius in this file at all
// (MUI's own `MuiDialog.styleOverrides.paper` does, pinned in
// `farmTheme.policy.test.ts`), and no other selector here has the same
// unconditional-plus-media-override shape today. Recreate that reader if one
// does.

// Everything allowed to cast a shadow, and why. Four floats plus one ring.
//
// `.tabbar` retired here in #829: the mobile tab bar is now MUI
// `BottomNavigation`, themed with `boxShadow: "none"` (variant B, "ruled" —
// a hairline top rule instead of a shadow, owner pick 2026-09-16). Its
// successor assertion, "gives the tab bar no shadow and a hairline top rule
// instead (variant B)" in `web/src/theme/farmTheme.policy.test.ts`, already
// landed with #864's theme overrides — #829 needs no new row, only this
// retirement.
//
// `.entry-foot` retired here in #830: the Daily entry sticky action bar is
// now a MUI `Paper elevation={4}`, which resolves to `--shadow-bar` through
// the theme's own shadow-index map G2 already pins (#823:
// `AppBar`/`Snackbar` defaults 4/6 -> `--shadow-bar`) — no new G2 row, since
// nothing about that mapping changed, only which component now relies on it.
//
// `.dialog` retired here in #827: the modal shadow now comes from MUI
// `Dialog`'s own Paper (`elevation={24}`, hardcoded by Dialog.js regardless
// of MuiPaper's global `elevation: 0` default), which resolves to
// `--shadow-dialog` through the same G2 shadow-index map (#823:
// `Popover`/`Drawer`/`Dialog` defaults 8/16/24 -> `--shadow-dialog`) — an
// emotion-generated class this static-file walk cannot see, and does not
// need to: G2's index map already governs it, unchanged by this PR.
//
// `.named-picker-listbox` retired here in #826: the picker popover now comes
// from MUI `Autocomplete`'s own Paper (no elevation of its own, so it falls
// to `Paper.js`'s default of 1 — turned "none" by the theme's shadow-index
// map), which resolves to `--shadow-dialog` through the SAME G2 index map
// (#823: `Popover`/`Drawer`/`Dialog` defaults 8/16/24 -> `--shadow-dialog`,
// and `Autocomplete`'s own exception, `MuiAutocomplete.styleOverrides.paper`
// at index 8, already pinned in `farmTheme.policy.test.ts`) — no new G2 row,
// unchanged by this PR.
// `.update-banner` retired here in #828: the service-worker update prompt is
// now a MUI `Snackbar`/`Alert`, whose shadow is an explicit `sx` reading the
// SAME `--shadow-bar` token this rule used to declare (UpdatePrompt.tsx) —
// not a new G2 mapping, so no `farmTheme.policy.test.ts` row follows it.
// Auth uses an elevation-0 Paper with its existing bespoke shadow token.
const SHADOW_ALLOWED = [
  ".glossary-entry:target", // not elevation: a spread-only deep-link halo
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

  // "the picker popover uses the float shadow, not the retired card one"
  // retires here in #826: `.named-picker-listbox` no longer exists (see the
  // SHADOW_ALLOWED comment above), and `declarationsFor` on a selector
  // nothing declares returns an empty map — keeping the assertion would pass
  // vacuously (822's D4). Its successor is the G2 row already pinning
  // `MuiAutocomplete.styleOverrides.paper` at index 8 in
  // `farmTheme.policy.test.ts`, unchanged by this PR.

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

  // Every surface this slice owns, INCLUDING a --r-input consumer. Without
  // it the scale guard would assert nothing about the one token whose value
  // actually changes, and would read as safety it does not provide.
  //
  // #829 retires `.panel` and `.capture-tile` from both lists below: the
  // Dashboard no longer renders either selector (it is `sx`-laid-out MUI),
  // so a radius token on a selector nothing renders would be a guard reading
  // as safety it does not provide (AGENTS.md, "writing a guard"). `.card`
  // and `.order-panel` stay — other screens still convert their own cards
  // in #831 to #833. `.named-picker-trigger` retires here in #826: the
  // closed-state picker no longer renders the page-owned `<button>` this
  // class named — it is an MUI outlined field now, themed through
  // `MuiOutlinedInput` (already asserted elsewhere) — so `input` alone is
  // this list's remaining `--r-input` consumer.
  it.each([
    ".toolbar",
    ".card",
    ".order-panel",
    ".entry-pane",
    "input",
    "button",
  ])("%s resolves its radius through a token, not a literal", (selector) => {
    const radius = declarationsFor(selector).get("border-radius");
    expect(radius).toMatch(/^var\(--r-[a-z]+\)$/);
  });

  // #864 narrows the meaning of --r-card to dialogs and sheets only: every
  // card-like surface reads --r-panel instead. A generic "some r-* token"
  // pattern match (above) would stay green if one of these silently reverted
  // to --r-card, so this pins the SPECIFIC token per surface.
  it.each([
    ".card", ".order-panel", ".entry-pane", ".farm-warning", ".help-hero",
  ])("%s reads --r-panel, not the dialog radius", (selector) => {
    expect(declarationsFor(selector).get("border-radius")).toBe("var(--r-panel)");
  });

  it("keeps raw page actions rectangular", () => {
    expect(declarationsFor("button").get("border-radius")).toBe("var(--r-input)");
  });

  // #827 retired both `.dialog` radius rules — MUI `Dialog`'s own paper now
  // carries `--r-card` through `MuiDialog.styleOverrides.paper.borderRadius`
  // (FarmThemeProvider.tsx), pinned by `farmTheme.policy.test.ts`'s own
  // dialog radius assertion (added with the theme in #823, unchanged by
  // this PR). A selector this file no longer declares would make a
  // CSS-text lookup here pass vacuously — exactly the trap #822 D4 named
  // this rule for — so the checks move to where the declaration actually
  // lives now, rather than staying here as a name that reads like it still
  // means something.
});
