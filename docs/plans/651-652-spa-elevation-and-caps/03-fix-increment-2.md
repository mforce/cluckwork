# Fix increment 2 — fold case, and stop enumerating properties

Review round 2, `false-green` seat. **All three holes driver-reproduced before dispatch.**

## The findings

Round 1's fix taught the guard about `filter`. Round 2 walked straight past it three ways. Observed by
the driver on head `2ca6b854`, each injected into `.panel` — a selector that must never cast a shadow:

| Injected | Renders a shadow? | Guard result |
|---|---|---|
| `Filter: drop-shadow(0 8px 24px rgba(0,0,0,.3));` | yes | **26/26 green** — missed |
| `filter: DROP-SHADOW(0 8px 24px rgba(0,0,0,.3));` | yes | **26/26 green** — missed |
| `filter: var(--leaky-shadow);` | yes | **26/26 green** — missed |
| `filter: drop-shadow(0 8px 24px rgba(0,0,0,.3));` (control) | yes | 1 failed — caught |

CSS property names and function names are both case-insensitive; `postcss`'s `walkDecls(prop, cb)` does an
exact `child.prop === prop` string comparison, and `.includes("drop-shadow")` is a literal lowercase
match. So the round-1 fix held only for the exact spelling it was written against.

## The shape of the fix, and why the shape changed

**Two misses of the same shape mean the method is wrong** — that is AGENTS.md's own guard rule, and it now
applies to this guard twice over. Round 1 missed `filter` because the walk enumerated `box-shadow`. Round 2
missed three more spellings because the fix enumerated `["box-shadow", "filter", "-webkit-filter"]`. The
answer is not a longer list. It is to stop listing.

So the walk visits **every declaration in the stylesheet**, folds case on both the property and the value,
and asks one question: does this declaration paint a shadow? That covers `Filter`, `FILTER`,
`-webkit-filter`, any vendor prefix nobody has thought of, and a custom property that *defines* a
drop-shadow for a `var()` to pick up later — the third hole, caught at the point of definition, so a
drop-shadow cannot enter the stylesheet unnoticed at all.

Walking everything means the walk now reaches declarations inside `@font-face`, `@property` and
`@keyframes`, whose parents are not rules and have no `.selectors`. That is a crash, not a miss, so the
parent type is checked explicitly.

## Files

- Modify `web/src/styles.elevation.test.ts` — **PROTECTED**.
- Modify `web/src/styles.caps.test.ts` — **PROTECTED**. It carries the identical case-sensitivity defect
  (`clean(d.value) !== "uppercase"` against a case-insensitive property and value). It was not the
  reported finding, but shipping a fix for one guard while knowingly leaving its twin is precisely the
  "reads as safety" failure AGENTS.md warns about.
- `web/src/styles.css` is NOT modified. The shipped stylesheet has been correct and unchanged since
  `5cd6c387`; every defect in rounds 1 and 2 has been in the guards.

## Step 1 — the elevation guard

Replace the `SHADOW_PROPS` constant and the whole `selectorsCastingShadow` function with this.
**PROTECTED — transcribe or STOP.**

```ts
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
function selectorsCastingShadow(): string[] {
  const found = new Set<string>();
  root.walkDecls((d) => {
    // Walking everything reaches declarations inside @font-face, @property and
    // @keyframes, whose parent is not a rule and has no selectors. That is a
    // crash rather than a miss, so it is excluded explicitly.
    const parent = d.parent;
    if (parent === undefined || parent.type !== "rule") return;

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
```

## Step 2 — the caps guard

In `web/src/styles.caps.test.ts`, replace the whole `selectorsUppercasing` function with this.
**PROTECTED — transcribe or STOP.**

```ts
// Same case-folding defect as the elevation guard, found in review round 2 and
// fixed here rather than left as the twin of a hole we had just closed:
// `TEXT-TRANSFORM: UPPERCASE` renders identically and postcss compares both
// the property and the value as exact, case-sensitive strings.
function selectorsUppercasing(): string[] {
  const found = new Set<string>();
  root.walkDecls((d) => {
    const parent = d.parent;
    if (parent === undefined || parent.type !== "rule") return;
    if (d.prop.toLowerCase() !== "text-transform") return;
    if (clean(d.value).toLowerCase() !== "uppercase") return;
    for (const sel of (parent as Rule).selectors) found.add(clean(sel));
  });
  return [...found].sort();
}
```

## Step 3 — mutation rows

Each: inject into `web/src/styles.css`, run, record, then `git checkout -- web/src/styles.css` and
confirm the restore. **Anchor every injection on a multi-line, unique string and confirm with `grep`
that the text is actually in the file before trusting the run** — a mutation that did not apply must
produce no result at all, never a green one.

Run with `cd web && npx vitest run src/styles.elevation.test.ts src/styles.caps.test.ts`.

| # | Injection (into `.panel` unless stated) | Must go | On which assertion |
|---|---|---|---|
| M14 | `Filter: drop-shadow(0 8px 24px rgba(0,0,0,.3));` | RED | non-inset selector set — round 2 finding 1 |
| M15 | `filter: DROP-SHADOW(0 8px 24px rgba(0,0,0,.3));` | RED | same set — round 2 finding 2 |
| M16 | `--leaky-shadow: drop-shadow(0 8px 24px rgba(0,0,0,.3));` into the top-level `:root` block | RED | same set — round 2 finding 3, caught at the definition. Expect the received array to gain `":root"`. |
| M17 | `filter: blur(2px);` | **GREEN** | proves no overreach: blur is not a shadow |
| M18 | `transition: filter 0.2s ease;` | **GREEN** | proves the value check, not the property name, is what decides |
| M19 | `TEXT-TRANSFORM: UPPERCASE;` on `.badge` | RED | the uppercase selector set — the caps guard's twin defect |
| M20 | `text-transform: capitalize;` on `.badge` | **GREEN** | capitalize is title case, not shouting; the invariant never claimed it |

Four of the seven must stay GREEN. **If any of those four reddens, the fix overreaches and is wrong:
STOP and report rather than adjusting anything.**

## Step 4 — gates and commit

**G1** then **G2**, both clean against the baseline (`Test Files 109 passed (109)` /
`Tests 2396 passed (2396)`, coverage unmoved).

```bash
git add web/src/styles.elevation.test.ts web/src/styles.caps.test.ts docs/plans/651-652-spa-elevation-and-caps/03-fix-increment-2.md
git commit -m "test(web): fold case and walk every declaration in the style guards (#651, #652)"
git push origin feat/651-652-spa-elevation-and-caps
```

Do NOT reply on GitHub, do NOT edit the PR description, do NOT merge.

## Report back

Commit SHA, `git status --short` verbatim, G1 and G2 output, the M14-M20 table as OBSERVED, and anything
here that turned out to be wrong.
