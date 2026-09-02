# #651 + #652 — Elevation hierarchy and the end of tracked all-caps

**Slice:** #651 (elevation hierarchy) and #652 (retire tracked all-caps labels), shipped as one PR.
**Mode:** feature. **Base:** `396ba233c04a84fa3452e9fd7901e4f2429d4bd7` on `main`.
**Why one slice:** both change `web/src/styles.css` and both add a guard to the same `web/src/styles*.test.ts`
family. Split into two PRs they conflict on every hunk. #650 and #657 shipped together for the same reason.

## 1. What already shipped — the fact base

Read at the base commit. **Both issue bodies were written before #654 merged and are stale in ways that
change the work.**

| Issue claim | Verified state at `396ba23` |
|---|---|
| #651: `.stat` carries `--r-card` + `--shadow-card` | **No `.stat` or `.stat-label` selector exists.** #654 replaced the dashboard tiles with `.capture-tile*` (L1564-1620), which are already border-only, no shadow, sentence case. Its own comment names "#651's direction" and "#652's". |
| #651: `.entry-pane` carries `--shadow-card` | **`.entry-pane` (L757) already has no `box-shadow`** — surface + hairline + `--r-card` only. |
| #652: seven rules set `text-transform: uppercase` | **Six do.** `.stat-label` is gone with `.stat`. |
| #652: "audit `.eyebrow`'s call sites" | **`.eyebrow` has zero call sites.** The only `eyebrow` in the SPA is an i18n *key* (`en.ts:2306`) rendered through `.help-kicker` (`HelpPage.tsx:244`), which #657 already made sentence case. `.help-toc .eyebrow` (L2133) is a descendant rule with nothing to select. |

So the live surface is smaller than either issue describes, and `.eyebrow` is dead CSS to delete rather
than a call-site audit to perform.

## 2. Live inventory — every `box-shadow` in the stylesheet

Walked, not recalled. Eleven declarations:

| Line | Selector | Value | Verdict |
|---|---|---|---|
| 555 | `.dialog` | `--shadow-dialog` | float — **keep** |
| 1006 | `.entry-foot` | `--shadow-bar` | float (sticky action bar) — **keep** |
| 1061 | `.toolbar` | `--shadow-card` | **remove; go inset** |
| 1310 | `.tabbar` (`@media`) | `--shadow-bar` | float (mobile tab bar) — **keep** |
| 1522 | `.card, .panel, .order-panel` | `--shadow-card` | **remove** |
| 1715 | `.auth .card` | `--auth-card-shadow` | float — a card on the auth gradient, its own token — **keep** |
| 2064 | `.glossary-entry:target` | `0 0 0 0.6rem var(--tint-accent)` | a focus/target ring, not elevation — **keep** |
| 2167 / 2172 | `.help-toc li a.active` | `inset 2px 0 0` | an inset marker bar — **keep** |
| 2676 | `.palette-option:has(input:checked)` | `inset 0 0 0 1px` | an inset selection ring — **keep** |
| 2712 | `.update-banner` | `--shadow-bar` | float — **keep** |
| 2880 | `.named-picker-listbox` | `--shadow-card` | float (a popover over content) — **keep, but re-point at `--shadow-dialog`** |

**Consequence: `--shadow-card` ends the slice with no consumer.** It is retired — both declarations
(L84 light, L209 dark) deleted. A token with no consumer is precisely the invitation to re-add the
card shadow this slice exists to remove.

## 3. Live inventory — every `text-transform: uppercase`

| Line | Selector | Verdict |
|---|---|---|
| 738 | `.step-n` | sentence case, `letter-spacing: 0`; the pill and its tint stay |
| 1102 | `.badge` | sentence case, ~0.78rem, `letter-spacing: 0`; the tinted pill and weight stay |
| 1139 | `.eyebrow` (rule opens 1134) | **delete the rule**, and delete `.help-toc .eyebrow` (L2133) with it — dead |
| 1214 | `.nav-group-label` | **KEEP** — a real structural divider |
| 1352 | `.more-group-label` | **KEEP** — the same divider in the More sheet |
| 1469 | `table.data th` | ~0.8rem, sentence case, weight 600, `color: var(--ink)`, `letter-spacing: 0` |

## 4. Decisions, and the facts behind them

**D1 — the toolbar keeps its hairline border.** #651 offers "no border or a hairline". Run the
repo's own `contrast()` from `web/src/test/cssTokens.ts` over `--canvas` against `--surface-2` in every
palette and both modes — not eyeballed hex, the same tool §7 uses:

| Palette | light | dark |
|---|---|---|
| default | 1.049 | 1.209 |
| forest | 1.054 | 1.210 |
| slate | 1.058 | 1.213 |
| terracotta | 1.074 | 1.187 |

**No palette separates, in either mode** — dark is marginally better and still nowhere near a usable
edge. So the inset toolbar is `--surface-2` fill **plus** `1px solid var(--hairline)`, no shadow,
`--r-panel` radius. Dropping the border would make the filter bar disappear everywhere, not only on the
light palettes. *(Corrected at Phase 4: the first draft claimed the dark palettes separated cleanly and
reasoned from eyeballed hex values. They do not, and the numbers above are why the border is not
optional in night mode either.)*

**D2 — the radius scale is 6 / 10 / 16.** `--r-input: 8px` becomes `6px`, `--r-panel: 10px` is new,
`--r-card: 16px` is unchanged. Keeping `--r-input` at 8 would make an 8/10/16 scale whose first two
steps are indistinguishable, which is not a hierarchy. `--r-pill: 999px` is orthogonal and untouched.
Depth mapping: page-level container 16, nested panel / toolbar 10, controls and chips 6.

**D3 — `--shadow-card` is retired** (see §2). `--shadow-dialog` and `--shadow-bar` stay.

**D4 — no literal radius values are added.** Twelve literal `border-radius` values already exist
(L688, 1193, 1745, 1761, 1908, 2065, 2208, 2570, 2710, 2775, plus `inherit` and `0`). This slice does
not fix them — that is out of scope — so the guard is scoped to the surfaces this slice touches rather
than pinning a repo-wide count.

**D5 — `.auth .card` is unaffected.** It sets `--auth-card-shadow` at L1715, later and more specific
than the group rule at L1516, so removing the group's `box-shadow` leaves it intact.

## 5. Guards — walk everything, exclude deliberately

Per AGENTS.md's guard rules: neither guard is a hand-maintained list of the selectors the author
happened to think of. Both walk the parsed stylesheet with `postcss` and assert over the whole set,
in the idiom `styles.num.test.ts` and `styles.help.test.ts` already use.

**G1 — `web/src/styles.elevation.test.ts`**
- Walk every rule. For each `box-shadow`, **split the value on top-level commas and require EVERY
  layer to be `inset`-prefixed** before excluding the rule. A prefix test on the whole value is not
  enough: `box-shadow: inset 0 0 0 1px var(--hairline), 0 8px 24px rgba(29,21,33,0.12);` starts with
  `inset` and still carries a real drop shadow, which is exactly the regression this guard exists to
  block. Collect the selector of every rule with at least one non-inset layer, and assert that set
  **equals** `{.dialog, .entry-foot, .tabbar, .auth .card, .update-banner, .named-picker-listbox}` plus
  `.glossary-entry:target`.
  *Deliberate exclusion:* all-inset values are markers and selection rings, not elevation — excluded by
  the value, not by name, so a new inset marker does not trip the guard and a new drop shadow does.
- Assert no rule anywhere references `--shadow-card`, and neither `:root` block declares it.
- Assert `.toolbar` declares `background: var(--surface-2)`, a `--hairline` border, `--r-panel`, and
  no `box-shadow`.
- Assert `.card, .panel, .order-panel` declares no `box-shadow`.

**G2 — `web/src/styles.caps.test.ts`**
- Walk every rule. Collect the selector of every `text-transform: uppercase`. Assert that set
  **equals** `{.nav-group-label, .more-group-label}`. No exclusions.
- **Recorded limitation, deliberately not closed here:** G2 guards the CSS layer only. Shouty labels
  can return without tripping it — a `.toUpperCase()` in a component, an uppercase literal typed into
  an i18n string, or `font-variant-caps: all-small-caps` on a new rule. Closing that needs a check on
  the i18n bundles and the TSX, which is outside this slice's stated scope (§9). Named here so a later
  reader does not mistake a green G2 for a guarantee it never made.
- Assert `table.data th` sets `color: var(--ink)` and `letter-spacing: 0`.

**G3 — radius scale**, added to `styles.elevation.test.ts`
- Assert `--r-input`, `--r-panel`, `--r-card` are declared and are three distinct values in
  increasing order.
- Assert `--r-input` resolves to `6px`, `--r-panel` to `10px`, `--r-card` to `16px`.
- Assert each of `.toolbar`, `.card`, `.panel`, `.order-panel`, `.entry-pane`, `.capture-tile`
  resolves its `border-radius` through a `var(--r-…)` reference, not a literal, **and the same for at
  least two `--r-input` consumers — `input, select, textarea` (L337) and `.named-picker-trigger`
  (L2810)**. Without those the "radius scale" guard asserts nothing about the one token whose *value*
  this slice changes, and reads as safety it does not provide. `.named-picker-trigger` additionally
  loses its dead `var(--r-input, 4px)` fallback: `--r-input` is always declared on `:root`, so the
  fallback can never fire and it is the only literal on a surface this slice owns.

**Mutation rows** (each must be run and seen red before the guard is claimed):

| # | Mutation | Guard that must redden | On the assertion named |
|---|---|---|---|
| M1 | re-add `box-shadow: var(--shadow-dialog)` to `.panel` | G1 | the non-inset selector set |
| M2 | re-add `text-transform: uppercase` to `table.data th` | G2 | the uppercase selector set |
| M3 | remove `text-transform: uppercase` from `.nav-group-label` | G2 | the same set (it must be an equality, not a subset) |
| M4 | change `.toolbar` fill back to `var(--surface)` | G1 | the toolbar inset assertion |
| M5 | set `--r-panel` to `16px` (equal to `--r-card`) | G3 | the three-distinct-increasing assertion |
| M6 | give `.toolbar` a literal `border-radius: 10px` | G3 | the token-reference assertion |
| M7 | add an all-`inset` shadow to a new rule | G1 | must stay **green** — proves the exclusion is by value, not an accident |
| M8 | give `.panel` `box-shadow: inset 0 0 0 1px var(--hairline), 0 8px 24px rgba(29,21,33,0.12)` | G1 | the non-inset selector set — proves the guard splits layers rather than testing the value's prefix |
| M9 | give `input, select, textarea` a literal `border-radius: 6px` | G3 | the token-reference assertion on an `--r-input` consumer |

## 6. Callers — what #394 says to read rather than run

Eight Playwright call sites query a table header by accessible name: `i18n.spec.ts:211`,
`reports-range.spec.ts:83`, `worker-sale-allocation.spec.ts:88`, `owner.spec.ts:70,88`,
`manager.spec.ts:330`, `sales.spec.ts:107,145`. All eight pass a `t(...)`/`tEn(...)` translation
lookup rather than a literal, and **none passes `exact: true`**.

That omission is the safety argument, and it is the only one that holds. Playwright's `name` option
matches the accessible name **case-insensitively and as a substring** unless `exact: true` is set —
a behaviour this repo already knows and uses deliberately, with `reports-range.spec.ts:71` carrying a
comment explaining why "From" and "To" need the flag. So these eight sites resolve the same header
whether or not the engine folds `text-transform` into the accessible name, before the change and after
it. The DOM text they ultimately match is untouched by this slice.

*(Corrected at Phase 4: the first draft argued that the specs passing today proved Chromium does not
fold the transform into the accessible name. It proves no such thing — case-insensitive matching makes
both answers consistent with a green run. The conclusion survives; the reasoning that reached it did
not.)*

No spec hardcodes an uppercase literal — `grep -rn "toUpperCase\|uppercase"` over
`tools/simulation/ui/` returns nothing outside `node_modules`. **`.badge` has no Playwright caller at
all.** Net caller risk identified from reading: none. The `caller-breakage` review seat exists to
refute that, not to confirm it.

**This reading does not replace a run.** `tools/simulation/ui/` is deliberately not in CI, so nothing
else will exercise it. Before the merge ask, the six specs above —
`{owner,manager,sales,reports-range,worker-sale-allocation,i18n}.spec.ts` — are run against the sim
harness and the result recorded in the PR body. A read is the argument; the run is the evidence.

## 7. Contrast

`table.data th` moves from `--muted` to `--ink`. Light: `#1d1d1d` on `#ffffff`. Night: `#f2ecf4` on
`#221a26`. Both are far above 4.5:1, and the move is strictly an increase — `--muted` (`#696969` on
white, `#b3a8b8` on `#221a26`) already cleared it. The repo's own `contrast()` helper in
`web/src/test/cssTokens.ts` is the check; assert it across all four palettes in both modes rather than
reasoning about it.

## 8. Documentation

`specs/product/GLOSSARY.md` and the SPA Help page take **no change**, and the reason is recorded rather
than assumed: this slice introduces no concept, renames nothing, and changes no term's meaning — it
changes the rendering of labels whose text is unchanged. Per AGENTS.md's docs-in-sync rule a missing
doc update is treated like a missing test, so the justification belongs in the PR body.

## 9. Simplicity ceiling

**Smallest viable implementation:** delete two `box-shadow` declarations, re-point one at
`--shadow-dialog`, retire `--shadow-card`, add `--r-panel` and lower `--r-input`, make the toolbar
inset, sentence-case four rules, delete two dead `.eyebrow` rules, and add two guard files.

**Complexity budget:** `web/src/styles.css` and two new `web/src/styles.*.test.ts` files. No new
concepts. No component or TSX change. **Non-goals:** the twelve pre-existing literal radii, any
`--shadow-bar`/`--shadow-dialog` retuning, the auth screen, motion, and the other five sibling issues.
Anything beyond this stops and reports rather than absorbing more design.

## 10. Out of scope

#653 (table layout), #655 (empty states), #656 (visual identity) — sibling slices with their own
surfaces. A finding against one of those is filed there.
