# Runbook — #651 + #652: elevation hierarchy, and the end of tracked all-caps

You are an autonomous coding agent with FULL tools (read, edit, write, bash) in the
`/home/mforce/dev/cluckwork` repo (.NET 10 backend, React 19 + Vite SPA in `web/`; cwd = repo root).
Execute this runbook top to bottom. You do everything except the merge: branch, edit, test, commit,
push, open the PR.

**Goal:** elevation encodes what floats and radius encodes nesting depth (#651); tracked all-caps
survives only on the two nav group dividers (#652).

**Design:** `docs/designs/651-652-spa-elevation-and-caps.md` — read it first. It carries the verified
inventory this runbook acts on, and the reasons behind every decision here.

## Rules

- Transcribe the exact code blocks VERBATIM (comments and whitespace included). Do not reformat,
  rename, or "improve" them. Blocks marked **PROTECTED** are the guards themselves: transcribe or
  STOP, never repair. A guard that has been locally adjusted to pass is worse than no guard.
- Run the commands EXACTLY as given. Do not invent flags.
- After every test command, if it is not clean, STOP and fix before continuing. **An expected RED is a
  clean result** — but only that exact RED: the command as written, the named test, failing at the
  named assertion. Anything else is a STOP however red it looks: a TypeScript error, zero tests
  collected, a different test failing, or a baseline failure that has changed shape.
- **Do NOT touch:**
  - `web/src/styles.test.ts`, `web/src/styles.num.test.ts`, `web/src/styles.help.test.ts`,
    `web/src/components/styles.dialog.test.ts`, `web/src/components/FarmBrand.styles.test.ts` — the
    existing style guards. If one of them goes red, that is a finding, not a thing to edit.
  - Any `.tsx` file. This slice changes no component. If you believe one must change, STOP and report.
  - `web/src/i18n/*` — no string changes. The translations are already sentence case.
  - `tools/simulation/ui/**` — the Playwright suite. The driver runs it at verification; you neither
    run it nor edit it.
  - `web/vite.config.ts` — the coverage floors. See G3: on a trip you STOP, you never re-baseline.
  - `specs/product/GLOSSARY.md` and `web/src/routes/HelpPage.tsx` — see the documentation table below
    for why this slice owes them nothing.
  - `table.data th[scope="row"]` (styles.css ~L1494): it declares `text-transform: none` and
    `letter-spacing: 0`, which become redundant once `table.data th` stops uppercasing. Redundant is
    not wrong, and removing them is churn outside this slice.
  - `.entry-pane`'s `border-radius` (styles.css ~L760): it stays `var(--r-card)`. It is a top-level
    pane on the Daily entry page, not a panel nested inside a card, so the depth mapping puts it at
    16. The guard asserts it resolves through a token, not which token.
  - The twelve pre-existing literal `border-radius` values (L688, 1193, 1745, 1761, 1908, 2065, 2208,
    2570, 2710, 2775). Out of scope, named explicitly in the design.
- **Files you may create or edit — anything else, STOP and report:**
  - `web/src/styles.css` (edit)
  - `web/src/styles.elevation.test.ts` (create)
  - `web/src/styles.caps.test.ts` (create)
- Work only on the new branch. Never commit to `main`.
- A **mutation check** means: plant a bug on purpose, run the suite, and see whether a test notices.
  RED means that test guards the code; GREEN means nothing was watching. Then restore and re-run to
  confirm you are clean. **Restore before re-running, never `--no-build`-style shortcuts** — Vitest
  reads the stylesheet from disk at import time, so a stale run is a real hazard here.
- Run the full test suite in the FOREGROUND and report its final summary line verbatim. Do not
  background it and report before it finishes.
- If a code block here conflicts with an existing test, STOP and report the conflict. Do NOT relax or
  delete that test.
- **Stop-and-report limit:** if you run the same test more than about five times without progress,
  STOP and report. That is information the driver needs, not a failure on your part.

**Protected-block probe — what the driver read before authoring the PROTECTED blocks:**
`postcss` is already a devDependency and already used this way by `web/src/styles.num.test.ts` and
`web/src/styles.help.test.ts` — both parse `src/styles.css` with `postcss.parse` and walk it, because
jsdom computes no layout. Probed and settled from those two files plus `web/src/test/cssTokens.ts`:
(1) `root.walkDecls(prop, cb)` visits declarations inside `@media` at-rules as well as top-level
rules, which is why the shadow walk needs no separate at-rule pass — `styles.help.test.ts` needed an
explicit `walkAtRules` only because it wanted to *distinguish* media context, which this guard does
not; (2) a declaration's `.parent` inside a media query is the `Rule`, not the `AtRule`, so
`(d.parent as Rule).selectors` is correct there too; (3) `rule.selectors` splits a grouped selector
list into its members, which is how `.card, .panel, .order-panel` yields three entries; (4) comments
survive inside selector text, which is why both existing files strip them with the same
`replace(/\/\*[\s\S]*?\*\//g, "")` before comparing — copied verbatim rather than reinvented; (5) there are **two** literal
`:root` blocks, not one — the token block at styles.css L9 and a second inside
`@media (max-width: 900px)` at ~L2231 declaring `--tabbar-h` alone. `declarationsFor(":root")` merges
both, which is harmless here because the key sets do not overlap: `--r-input`, `--r-panel` and
`--r-card` are declared only in the first. Left merged deliberately rather than special-cased. **Do
not "fix" it.** (The driver's first probe for this was `grep -n "^:root {"`, which is anchored to
column zero and therefore could not see the indented second block — corrected before dispatch.)

**Existing instances of this pattern:** `web/src/styles.num.test.ts` (declaration lookup by selector),
`web/src/styles.help.test.ts` (the same, plus media-scoped lookup), `web/src/components/styles.dialog.test.ts`.
The blocks below differ from all three in one way, deliberately: those look **up** declarations for
selectors they name, while these walk **every** declaration and assert over the complete set. That is
AGENTS.md's "walk everything, exclude deliberately" — the named-selector lookups here exist only as
supplementary assertions beside the set equalities, never in place of them.

## Verify prerequisites (run first)

```bash
git -C /home/mforce/dev/cluckwork rev-parse --abbrev-ref HEAD   # expect: main
git -C /home/mforce/dev/cluckwork status --porcelain            # expect: empty, or only untracked docs/ files
node --version                                                  # expect: v26.x  (CI pins node-version: 26)
```

Observed by the driver on 2026-09-02 on this host: branch `main` at
`396ba233c04a84fa3452e9fd7901e4f2429d4bd7`, tree clean apart from the two untracked planning documents
this runbook is one of.

**The commit gate:** `.githooks/pre-commit` is opt-in via `git config core.hooksPath .githooks`. Check
whether it is enabled with `git config --get core.hooksPath` and report what you find. It runs
`npm run typecheck` when `web/` files are staged; it does not run the CSS guards. Do not enable or
disable it.

## Caller ledger

| Increment | Contract changed | Every production caller | What each does AT THIS COMMIT | Same-commit or later? | Observed at that commit (driver, Phase 11) |
|---|---|---|---|---|---|
| 1 | none — no contract change. CSS custom properties are not a code contract; `--shadow-card` is removed and every reference to it is removed in the same commit. | `web/src/styles.css` is the only file that references `--shadow-card` (verified repo-wide) | compiles and renders; no consumer is left dangling | same commit | *(driver fills)* |
| 2 | none — no contract change. `text-transform` is presentational; no DOM text changes. | The eight Playwright `columnheader` queries (`i18n.spec.ts:211`, `reports-range.spec.ts:83`, `worker-sale-allocation.spec.ts:88`, `owner.spec.ts:70,88`, `manager.spec.ts:330`, `sales.spec.ts:107,145`) | all eight omit `exact: true`, so name matching is case-insensitive substring; they resolve the same header before and after | same commit | *(driver fills — the sim-harness run the owner required)* |

## Gate commands

| ID | Gate | Source | Command, verbatim | Baseline on `396ba233` | Clean looks like — and, on a trip |
|---|---|---|---|---|---|
| G1 | typecheck + build | `.github/workflows/ci.yml`, job `web`, step `Typecheck and build` | `npm run build` (in `web/`) | clean — driver-verified 2026-09-02 | exits 0. `tsc -b` first, then `vite build`. **Bare `npx tsc --noEmit` is vacuous here — never substitute it.** |
| G2 | test | `.github/workflows/ci.yml`, job `web`, step `Test with coverage gate` | `npm run test:coverage` (in `web/`) | `Test Files 107 passed (107)` / `Tests 2370 passed (2370)` — driver-verified 2026-09-02, nothing already red | the same two lines with counts risen by the tests you added, and the coverage summary present. Read against the baseline, not against absolute green. |
| G3 | coverage floors | `web/vite.config.ts`, `thresholds` | (same command as G2 — the floors are enforced inside it) | measured on `396ba233`: statements **90.77** (floor 89, headroom 1.77), branches **86.04** (floor 80, headroom 6.04), functions **85.84** (floor 85, **headroom 0.84**), lines **93.67** (floor 92, headroom 1.67) | no threshold error printed. **Functions has under one point of headroom.** Your two new files are `*.test.ts` and are excluded from coverage by `vite.config.ts`, so they should not move any denominator. **On a trip: STOP and report the four measured numbers. Do NOT edit `vite.config.ts` and do NOT add a test to pad a number.** |
| G4 | service-worker guarantees | `.github/workflows/ci.yml`, job `web`, step `Verify service-worker guarantees` | `npm run verify:sw` (in `web/`) | clean — CI-attested on `396ba233`, not driver-verified; runnable by anyone with `web/` deps installed | exits 0. Unaffected by CSS, run once at the end as a regression check. |
| — | GitGuardian, CodeQL, dependency-review | server-side integrations | no command to paste | n/a | reported on the PR; handled by the driver's reviewer sweep, not by you. |

## Documentation surfaces

| Surface | Path / key | Locales | Increment | Verification procedure (run at Phase 11) | Verified by + SHA |
|---|---|---|---|---|---|
| product glossary | `specs/product/GLOSSARY.md` | n/a | **none** | **none owed, and this is the reason, not an omission:** the slice introduces no concept, renames nothing, and changes no term's meaning. Every label's *text* is unchanged — only its rendering is. AGENTS.md treats a missing doc update like a missing test, so the justification goes in the PR body. | *(driver)* |
| in-app Help + glossary | `web/src/routes/HelpPage.tsx`, `web/src/i18n/{en,es,tl}.ts` | en / es / tl (resolved from `web/src/i18n/` — those three files are the whole set; `ls web/src/i18n/*.ts` is the command) | **none** | as above. Additionally: the three bundles are already sentence case, so nothing relied on CSS to capitalise. Confirm by reading, not by rendering: `grep -n "eyebrow" web/src/i18n/*.ts` returns one key per locale, all sentence case. | *(driver)* |
| the screens themselves | every SPA route | all three | 1 and 2 | before/after screenshots, light and night, produced by the **driver** from the sim harness — you do not run it. | *(driver)* |

## Step 0 — branch

```bash
cd /home/mforce/dev/cluckwork
git checkout main
git pull --ff-only
git checkout -b feat/651-652-spa-elevation-and-caps
```

===================================================================================
# INCREMENT 1 — elevation and the radius scale (#651)
===================================================================================

RED → GREEN. Do not reorder: the failing run is the proof the guard can fail at all.

## 1a. RED — add the elevation guard

Create `web/src/styles.elevation.test.ts` with EXACTLY this content. **PROTECTED — transcribe or
STOP.**

```ts
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

function selectorsCastingShadow(): string[] {
  const found = new Set<string>();
  root.walkDecls("box-shadow", (d) => {
    if (!castsShadow(d.value)) return;
    for (const sel of (d.parent as Rule).selectors) found.add(clean(sel));
  });
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
```

Note on the last block: the `--r-input` consumer is listed as `"input"`, not as the grouped selector
`"input, select, textarea"`. `declarationsFor` matches on the MEMBERS of `rule.selectors`, which
postcss splits a grouped list into — so `"input"` matches the `input, select, textarea` rule at
styles.css ~L414 and the grouped string would match nothing. Do not "fix" it to the grouped form.

Run it and RECORD THE FAILURE:

```bash
npx vitest run src/styles.elevation.test.ts
```

| Gate row + narrowing | Named test | Assertion | Stable discriminator | Generated fragments | Also expected to fail |
|---|---|---|---|---|---|
| G2, narrowed to `src/styles.elevation.test.ts` | `#651 elevation: only a float casts a shadow > no rule outside the float set casts a drop shadow` | `expect(selectorsCastingShadow()).toEqual(SHADOW_ALLOWED)` | the received array contains `".toolbar"`, `".card"`, `".panel"` and `".order-panel"` — four entries the expected array does not have | none — every value here is read from a committed file | the toolbar test (`background` is `var(--surface)`), the `--shadow-card` retirement test, the picker-popover test, the three-distinct-steps test (`--r-panel` is `undefined`), and the **`.named-picker-trigger`** radius case (`var(--r-input, 4px)` at styles.css ~L2810 — the `, 4px` fallback breaks the regex's `$` anchor). **`.toolbar`'s radius case PASSES before the edit** — its pre-edit `var(--r-card)` already matches the token regex; it is the toolbar's *background* and *box-shadow* that fail, inside the inset test. **Seven failures out of fourteen tests is the expected RED. Zero failures, a different count, or a failure in another file, is a STOP.** |

**If it passes, STOP and report — do not continue to 1b.** A guard that is green before its code
exists proves nothing.

## 1b. GREEN — apply the elevation and radius changes

Edit `web/src/styles.css` only. Six changes:

1. **The radius tokens** (~L74). Replace:
   ```css
     --r-input: 8px;
     --r-card: 16px;
   ```
   with:
   ```css
     --r-input: 6px;
     --r-panel: 10px;
     --r-card: 16px;
   ```
   Leave `--r-pill: 999px` exactly where it is.

2. **Retire `--shadow-card`.** Delete the whole declaration at ~L84 (light `:root`) and the whole
   declaration at ~L209 (the dark base). Leave `--shadow-dialog` and `--shadow-bar` untouched in both.

3. **`.toolbar`** (~L1052). Replace `background: var(--surface);` with
   `background: var(--surface-2);`, replace `border-radius: var(--r-card);` with
   `border-radius: var(--r-panel);`, and delete the `box-shadow: var(--shadow-card);` line. Keep the
   border, the padding, the flex rules and the margin exactly as they are. Update the section comment
   above it — it currently reads `Toolbar (carded filter bar)` — to say it is inset, and say why the
   border stays: `--canvas` and `--surface-2` sit at 1.05-1.21:1 in every palette, so a borderless
   fill would be invisible.

4. **`.card, .panel, .order-panel`** (~L1516). Delete the `box-shadow: var(--shadow-card);` line.
   Keep the background, border and radius.

5. **`.named-picker-listbox`** (~L2880). Replace `box-shadow: var(--shadow-card);` with
   `box-shadow: var(--shadow-dialog);`.

6. **`.named-picker-trigger`** (~L2810). Replace `border-radius: var(--r-input, 4px);` with
   `border-radius: var(--r-input);`. The fallback can never fire — `--r-input` is always declared on
   `:root` — and it is the only literal on a surface this slice owns.

Then:

```bash
cd /home/mforce/dev/cluckwork/web && npx vitest run src/styles.elevation.test.ts
```
Expect: all tests in that file pass.

Then the full gates, **G1 then G2**, citing their rows above. Both must be clean against the baseline.

## 1c. Commit

```bash
git add web/src/styles.css web/src/styles.elevation.test.ts
git commit -m "feat(web): elevation encodes what floats, radius encodes depth (#651)"
```

===================================================================================
# INCREMENT 2 — sentence-case labels (#652)
===================================================================================

## 2a. RED — add the caps guard

Create `web/src/styles.caps.test.ts` with EXACTLY this content. **PROTECTED — transcribe or STOP.**

```ts
import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import postcss from "postcss";
import type { Rule } from "postcss";
import { contrast, resolveTokens, type Mode } from "./test/cssTokens";
import { BRANDS, DEFAULT_BRAND } from "./lib/brand";

// #652 — caps mark structure, they do not decorate every label. The walk is
// over the WHOLE stylesheet: an equality, not a subset, so both re-adding caps
// somewhere new and dropping them from a divider are failures.
const css = readFileSync(resolve(process.cwd(), "src/styles.css"), "utf8");
const root = postcss.parse(css);
const clean = (s: string) => s.replace(/\/\*[\s\S]*?\*\//g, "").trim();

const attrFor = (brand: string) => (brand === DEFAULT_BRAND ? null : brand);
const MODES: Mode[] = ["light", "dark"];

// The sidebar and the More-sheet group dividers. Nothing else.
const CAPS_ALLOWED = [".more-group-label", ".nav-group-label"].sort();

function selectorsUppercasing(): string[] {
  const found = new Set<string>();
  root.walkDecls("text-transform", (d) => {
    if (clean(d.value) !== "uppercase") return;
    for (const sel of (d.parent as Rule).selectors) found.add(clean(sel));
  });
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

describe("#652 caps: only the nav group dividers shout", () => {
  it("no rule outside the two dividers uppercases", () => {
    expect(selectorsUppercasing()).toEqual(CAPS_ALLOWED);
  });

  it("table headers are readable ink at a readable size, not tracked caps", () => {
    const th = declarationsFor("table.data th");
    expect(th.get("color")).toBe("var(--ink)");
    expect(th.get("letter-spacing")).toBe("0");
    expect(th.get("font-weight")).toBe("600");
  });

  it("the badge and the step pill keep their pill and drop the tracking", () => {
    expect(declarationsFor(".badge").get("letter-spacing")).toBe("0");
    expect(declarationsFor(".badge").get("border-radius")).toBe("var(--r-pill)");
    expect(declarationsFor(".step-n").get("letter-spacing")).toBe("0");
    expect(declarationsFor(".step-n").get("border-radius")).toBe("var(--r-pill)");
  });

  it("the dead .eyebrow rules are gone", () => {
    expect(css).not.toContain(".eyebrow");
  });
});

// A header only becomes more legible if the colour it moves to is legible.
// Asserted with the repo's own contrast(), across every palette and both
// modes, rather than reasoned about from two hex values.
describe.each(BRANDS)("#652 contrast: table headers on %s", (brand) => {
  it.each(MODES)("%s: --ink on --surface clears WCAG AA", (mode) => {
    const t = resolveTokens(attrFor(brand), mode);
    expect(contrast(t.get("--ink")!, t.get("--surface")!)).toBeGreaterThanOrEqual(4.5);
  });
});
```

Run it and RECORD THE FAILURE:

```bash
cd /home/mforce/dev/cluckwork/web && npx vitest run src/styles.caps.test.ts
```

| Gate row + narrowing | Named test | Assertion | Stable discriminator | Generated fragments | Also expected to fail |
|---|---|---|---|---|---|
| G2, narrowed to `src/styles.caps.test.ts` | `#652 caps: only the nav group dividers shout > no rule outside the two dividers uppercases` | `expect(selectorsUppercasing()).toEqual(CAPS_ALLOWED)` | the received array contains `"table.data th"`, `".badge"`, `".step-n"` and `".eyebrow"` — four entries the expected array does not have | none | the table-header test (`color` is `var(--muted)`), the badge/step-pill tracking test, and the `.eyebrow` deletion test. **The eight contrast cases must PASS already** — `--ink` on `--surface` is unchanged by this slice, and a red there means the palette is broken independently of #652, which is a STOP and a report, not something to fix here. Four failures, eight passes, is the expected RED. |

**If the set-equality test passes, STOP and report.**

## 2b. GREEN — apply the caps changes

Edit `web/src/styles.css` only. Four changes:

1. **`.step-n`** (~L730). Delete `text-transform: uppercase;` and change `letter-spacing: 0.08em;` to
   `letter-spacing: 0;`. Keep the pill, the tint, the colour and the weight. The comment above the
   letter-spacing line explains the tracking — rewrite it to say the label is sentence case now.

2. **`.badge`** (~L1096). Delete `text-transform: uppercase;`, change `letter-spacing: 0.03em;` to
   `letter-spacing: 0;`, and change `font-size: 0.72rem;` to `font-size: 0.78rem;`. Keep the pill,
   the weight, the tint and the `white-space: nowrap`.

3. **Delete `.eyebrow` entirely** (~L1134, the whole rule) **and delete `.help-toc .eyebrow`**
   (~L2133, the whole rule). Both are dead: the class has no call site anywhere in `web/src`. The
   only `eyebrow` in the SPA is an i18n key rendered through `.help-kicker`, which is unaffected.
   Remove the section comment that introduces `.eyebrow` along with it.

4. **`table.data th`** (~L1463). Change `color: var(--muted);` to `color: var(--ink);`,
   `font-weight: 700;` to `font-weight: 600;`, `font-size: 0.68rem;` to `font-size: 0.8rem;`, delete
   `text-transform: uppercase;`, and change `letter-spacing: 0.06em;` to `letter-spacing: 0;`. Do NOT
   touch `table.data th[scope="row"]` below it.

Then:

```bash
cd /home/mforce/dev/cluckwork/web && npx vitest run src/styles.caps.test.ts
```
Expect: all tests in that file pass, contrast cases included.

Then **G1** and **G2**, both clean against the baseline. Report G2's summary line and the four
coverage numbers verbatim.

## 2c. Commit

```bash
git add web/src/styles.css web/src/styles.caps.test.ts
git commit -m "feat(web): sentence-case every label outside the nav group dividers (#652)"
```

===================================================================================
# INCREMENT 3 — the local adversarial pass (mutation checks)
===================================================================================

AGENTS.md requires this before the first push: *mutation first, claim second*. Nine rows. For each:
apply the mutation to `web/src/styles.css`, run the named command, record the result, then **restore
the file and re-run to confirm you are clean** before the next row. `git diff` after every restore
must be empty for `styles.css`.

Run each with `cd /home/mforce/dev/cluckwork/web && npx vitest run src/styles.elevation.test.ts src/styles.caps.test.ts`.

| # | Mutation | Must go | On which assertion |
|---|---|---|---|
| M1 | add `box-shadow: var(--shadow-dialog);` to `.panel` | RED | the non-inset selector set |
| M2 | add `text-transform: uppercase;` back to `table.data th` | RED | the uppercase selector set |
| M3 | delete `text-transform: uppercase;` from `.nav-group-label` | RED | the uppercase selector set — it must be an equality, not a subset |
| M4 | change `.toolbar`'s `background` back to `var(--surface)` | RED | the toolbar-inset assertion |
| M5 | set `--r-panel: 16px` | RED | the three-distinct-steps assertion |
| M6 | give `.toolbar` a literal `border-radius: 10px` | RED | the token-reference assertion |
| M7 | add `box-shadow: inset 0 0 0 1px var(--hairline);` to **`.entry-pane`** | **GREEN** | proves the exclusion is by value, not an accident. **Not `.panel`**: a second assertion in the same file requires `.panel`'s `box-shadow` to be undefined whatever its value, so an inset shadow there goes red on that assertion instead and proves nothing about the classifier. `.entry-pane` has no box-shadow assertion of its own, so it isolates the classifier cleanly. If this goes RED the guard is wrong. |
| M8 | give `.panel` `box-shadow: inset 0 0 0 1px var(--hairline), 0 8px 24px rgba(29, 21, 33, 0.12);` | RED | the non-inset selector set — proves the guard splits layers instead of testing the value's prefix. **This is the row that matters most**; a naive prefix check passes it. Expect **two** failures, not one: the `.panel` no-shadow assertion trips as well, because M8 targets a selector it also covers. Both are correct; the named one is the non-inset set. |
| M9 | give `input, select, textarea` a literal `border-radius: 6px` | RED | the token-reference assertion on an `--r-input` consumer |

**A row that does not do what this table says is a finding about the guard, not about the mutation.**
STOP and report it; do not adjust the guard to make the row come out right.

After the last restore, run **G2** once more and confirm the suite is clean.

**The driver already ran this whole runbook once**, on 2026-09-02, in a throwaway worktree at
`396ba233` with `node_modules` symlinked: guards pre-edit gave elevation 7 failures of 14 and caps 4 of
12 with all 8 contrast cases passing; the 1b and 2b edits applied literally gave 26/26; M3, M7 and M8
behaved exactly as this table says; the full suite came to **109 files / 2396 tests** with coverage
unmoved from the baseline. So the numbers above are observed, not predicted. **If yours differ, that is
a real finding — report it rather than adjusting anything.**

===================================================================================
# FINISH — push and open the PR
===================================================================================

```bash
cd /home/mforce/dev/cluckwork
npm --prefix web run verify:sw   # G4
git push -u origin feat/651-652-spa-elevation-and-caps
```

Open the PR with `gh pr create`. **The title is the release note and becomes the squashed commit
subject**, so it must be a conventional-commit subject. Use exactly:

```
feat(web): elevation hierarchy and sentence-case labels (#651, #652)
```

The body must carry, in this order:

1. What changed, split by issue.
2. **The corrections to the issue bodies**: `.stat`/`.stat-label` no longer exist (#654 replaced them
   with `.capture-tile*`, already border-only and sentence case); `.entry-pane` already had no shadow;
   six uppercase rules existed, not seven; `.eyebrow` had zero call sites and was deleted rather than
   audited. Point at `docs/designs/651-652-spa-elevation-and-caps.md` for the verified inventory.
3. **The mutation table from increment 3, with the result you actually observed in each row.** Report
   honestly — never write a red you did not see.
4. G2's summary line verbatim and the four coverage numbers.
5. **Why no glossary or Help change is owed** — the reason from the documentation table above, stated
   in full. A reviewer will otherwise read it as an omission.
6. A line saying the `tools/simulation/ui/` Playwright specs and the before/after screenshots are the
   **driver's** verification step and are not yet done.

Do NOT merge. Do NOT mark the PR ready-for-review or draft either way without being told — leave it in
whatever state `gh pr create` gives you and report which it is.

## Report back

- The branch, the two commit SHAs, and `git status --short` pasted verbatim.
- G1, G2, G3, G4 results, each cited by row ID.
- The mutation table with observed results.
- Anything you stopped on, and anything in this runbook that turned out to be wrong.
