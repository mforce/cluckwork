# Fix increment 3 — a keyframe is not a surface

Review round 3, CodeRabbit on head `1ac69b3b`. **Driver-reproduced before dispatch.**

## The finding

Fix increment 2 stopped enumerating property names and walked every declaration instead. Walking
everything reaches `@keyframes`, and a keyframe frame **is** a postcss rule — its selector is `from`, `to`
or a percentage — so the `parent.type !== "rule"` guard added in that increment does not exclude it.

**Observed by the driver on head `1ac69b3b`:** adding
`from { box-shadow: 0 8px 24px rgba(0,0,0,0.2); }` to `@keyframes dialog-rise` — a perfectly legitimate
animation of a shadow the dialog is already allowed to cast — turns the guard **RED**, because `from`
lands in the casting-shadow set and is not in `SHADOW_ALLOWED`.

That is a false alarm the fix introduced, and it is the more damaging kind: it does not let a regression
through, it blocks correct work. Nobody hits it today because no keyframe animates a shadow, so it would
have sat latent until the first person tried.

**The guard's invariant is about which SURFACES float.** A keyframe is a point in time, not a surface.

## Also corrected here, from the same review round

1. `03-fix-increment-2.md` says "Four of the seven must stay GREEN". Its own table marks three — M17, M18
   and M20. The implementer noticed and followed the table. Prose corrected to match.
2. `03-fix-increment-2.md`'s findings table claims `filter: var(--leaky-shadow)` renders a shadow. It does
   not: `--leaky-shadow` is defined nowhere, so the value resolves to nothing. The `var()` indirection gap
   is real, but that row overstated the demonstration. Corrected to say what was actually observed —
   the guard did not flag it, and the reason it renders nothing is that the token is undefined. **M16
   remains valid and unchanged**: it injects the custom property into `:root`, which is the form that both
   renders and is caught.

**Refuted, with the reason, and NOT changed:** the same round asked for `G1` to be redefined as
`cd web && npm test`. G1 is the build gate and G2 the test gate, fixed by
`01-implementer-runbook.md`'s gate table and copied from `.github/workflows/ci.yml`'s `web` job
(`npm run build`, `npm run test:coverage`). Renumbering them here would put two different definitions of
G1 in one plan directory. The suggestion to avoid `git checkout -- web/src/styles.css` for mutation
cleanup is also declined: the runbook already requires a clean tree before mutating, and a targeted
restore is exactly what makes "no net diff" checkable afterwards.

## Files

- Modify `web/src/styles.elevation.test.ts` — **PROTECTED**.
- Modify `web/src/styles.caps.test.ts` — **PROTECTED**.
- Modify `docs/plans/651-652-spa-elevation-and-caps/03-fix-increment-2.md` — the two corrections above.
- `web/src/styles.css` is NOT modified.

## Step 1 — the shared exclusion

In **both** guard files, add this helper immediately above the `selectorsCastingShadow` /
`selectorsUppercasing` function, and extend the import on line 5 of each file to
`import type { AtRule, Node, Rule } from "postcss";`. **PROTECTED — transcribe or STOP.**

```ts
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
```

## Step 2 — use it in both walkers

In `web/src/styles.elevation.test.ts`, inside `selectorsCastingShadow`'s callback, immediately after the
existing `if (parent === undefined || parent.type !== "rule") return;` line, add:

```ts
    if (insideKeyframes(parent)) return;
```

In `web/src/styles.caps.test.ts`, inside `selectorsUppercasing`'s callback, in the same position, add the
identical line.

## Step 3 — mutation rows

Same discipline: unique multi-line anchor, `grep` to confirm the injection is in the file, no result line
for a row that did not apply, `git checkout -- web/src/styles.css` between rows.

| # | Injection | Must go | Why |
|---|---|---|---|
| M21 | `from { box-shadow: 0 8px 24px rgba(0,0,0,0.2); }` inside `@keyframes dialog-rise` | **GREEN** | the finding itself. **Before this increment it is RED — the driver observed that on `1ac69b3b`.** |
| M22 | `from { filter: drop-shadow(0 8px 24px rgba(0,0,0,0.2)); }` inside `@keyframes dialog-rise` | **GREEN** | the same exclusion via the other mechanism |
| M23 | `from { text-transform: uppercase; }` inside `@keyframes dialog-rise` | **GREEN** | the caps guard's half of the same exclusion |
| M24 | `box-shadow: var(--shadow-dialog);` on `.panel` | RED | **the regression control.** Proves the keyframes exclusion did not blind the guard to an ordinary rule. If this goes green the fix has broken the guard entirely. |
| M25 | `filter: drop-shadow(0 8px 24px rgba(0,0,0,.3));` on `.panel` | RED | the same control through the filter mechanism |

**M24 and M25 are the rows that matter.** An exclusion that silences the guard everywhere would make
M21-M23 pass for the wrong reason; those two are what tell the difference.

## Step 4 — the two documentation corrections

In `docs/plans/651-652-spa-elevation-and-caps/03-fix-increment-2.md`:

1. Change `**Four of the seven** must stay GREEN` to `**Three of the seven** must stay GREEN`, and
   `If any of those four reddens` to `If any of those three reddens`.
2. In the findings table at the top, change the `filter: var(--leaky-shadow);` row's second column from
   `yes` to `no — the token is undefined, so it resolves to nothing`, and leave its third column
   (`26/26 green — missed`) as it is, adding after the table:
   `The var() row is the weakest of the three: it shows the guard does not read through a custom property, but the injected value rendered nothing because the token was never defined. M16 is the form that both renders and is caught.`

## Step 5 — gates and commit

**G1** then **G2** as defined in `01-implementer-runbook.md`'s gate table — unchanged, do not redefine them.

```bash
git add web/src/styles.elevation.test.ts web/src/styles.caps.test.ts docs/plans/651-652-spa-elevation-and-caps/
git commit -m "test(web): a keyframe is not a surface that floats (#651, #652)"
git push origin feat/651-652-spa-elevation-and-caps
```

Do NOT reply on GitHub, do NOT edit the PR description, do NOT merge.

## Report back

Commit SHA, `git status --short`, G1 and G2 output, the M21-M25 table as OBSERVED, and anything here that
turned out to be wrong.
