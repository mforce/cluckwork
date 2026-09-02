# Fix increment 1 — close the `filter: drop-shadow()` bypass in the elevation guard

Review round 1, `false-green` seat. **Driver-verified before dispatch**, not taken on the reviewer's word.

## The finding

`selectorsCastingShadow()` walks `box-shadow` declarations only. A drop shadow can also be painted with
`filter: drop-shadow(...)`, which that walk never visits — so the guard's stated invariant, *only a float
casts a shadow*, does not hold.

**Observed by the driver on head `dccfcc6b`:** adding
`filter: drop-shadow(0 8px 24px rgba(29, 21, 33, 0.2));` to the `.card, .panel, .order-panel` rule puts a
visible drop shadow on every screen that uses those three classes, and
`npx vitest run src/styles.elevation.test.ts src/styles.caps.test.ts` stays **26/26 green**. That is a real
regression shipping past a guard whose whole purpose is to stop it.

**Why the fix is cheap:** `grep -n "^\s*filter:" web/src/styles.css` returns nothing and there is no
`backdrop-filter` either. The stylesheet paints no filters at all today, so nothing has to be
allow-listed.

## The shape of the fix, and why this shape

**One set, two mechanisms.** Rather than adding a second, separate assertion, `selectorsCastingShadow()`
learns about the other way a shadow can be painted. The existing `SHADOW_ALLOWED` equality then covers
both mechanisms in one place, so a float that legitimately needs `drop-shadow()` is handled by the same
allow-list as one that uses `box-shadow` — and there is still exactly one assertion to read.

It targets `drop-shadow` specifically, not `filter` wholesale: `filter: blur()` and `filter: brightness()`
are not shadows and banning them would be a guard asserting something its invariant never claimed.
`-webkit-filter` is included because it paints identically in a browser that honours it.

## Files

- Modify `web/src/styles.elevation.test.ts` — **PROTECTED**, transcribe exactly.
- Nothing else. `web/src/styles.css` is NOT touched by this increment: the shipped stylesheet is correct,
  it is the guard that was incomplete.

## Step 1 — RED

Replace the whole `selectorsCastingShadow` function with the block below, and add the one new constant
above it. **PROTECTED — transcribe or STOP.**

```ts
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
const SHADOW_PROPS = ["box-shadow", "filter", "-webkit-filter"];

function selectorsCastingShadow(): string[] {
  const found = new Set<string>();
  for (const prop of SHADOW_PROPS) {
    root.walkDecls(prop, (d) => {
      const casts = prop === "box-shadow"
        ? castsShadow(d.value)
        : d.value.includes("drop-shadow");
      if (!casts) return;
      for (const sel of (d.parent as Rule).selectors) found.add(clean(sel));
    });
  }
  return [...found].sort();
}
```

Then plant the mutation the fix exists to catch, run the guards, and RECORD THE RED:

```bash
cd web
# add this line inside the `.card, .panel, .order-panel { }` rule in src/styles.css:
#   filter: drop-shadow(0 8px 24px rgba(29, 21, 33, 0.2));
npx vitest run src/styles.elevation.test.ts src/styles.caps.test.ts
```

| Named test | Assertion | Stable discriminator | Expected |
|---|---|---|---|
| `#651 elevation: only a float casts a shadow > no rule outside the float set casts a drop shadow` | `expect(selectorsCastingShadow()).toEqual(SHADOW_ALLOWED)` | the received array gains `".card"`, `".panel"` and `".order-panel"` | **RED, 1 failed** |

**Before this increment's change that same mutation is GREEN — the driver observed it.** If it is green
after your change too, the fix did not work: STOP and report, do not adjust anything.

Then `git checkout -- src/styles.css` to remove the planted shadow and confirm 26/26 green again.

## Step 2 — the new mutation rows

Run all four, restoring `src/styles.css` between each with `git checkout -- src/styles.css`, and confirm
`git status --porcelain` shows no change to it afterwards.

| # | Mutation | Must go | On which assertion |
|---|---|---|---|
| M10 | `filter: drop-shadow(0 8px 24px rgba(29, 21, 33, 0.2));` on `.panel` | RED | the non-inset selector set — this is the finding itself |
| M11 | `-webkit-filter: drop-shadow(0 8px 24px rgba(29, 21, 33, 0.2));` on `.panel` | RED | same set — the prefixed property paints the same shadow |
| M12 | `filter: blur(2px);` on `.panel` | **GREEN** | proves the guard targets drop-shadow, not `filter` wholesale. If this reddens the guard overreaches and the fix is wrong. |
| M13 | `filter: drop-shadow(0 24px 64px rgba(0,0,0,.3));` on `.dialog` | **GREEN** | `.dialog` is an allow-listed float, so the one set governs both mechanisms uniformly. This is the row proving the allow-list is shared, not duplicated. |

## Step 3 — gates and commit

Run **G1** then **G2** from the runbook's gate table, both clean against the baseline
(`Test Files 109 passed (109)` / `Tests 2396 passed (2396)`; coverage unmoved).

```bash
git add web/src/styles.elevation.test.ts docs/plans/651-652-spa-elevation-and-caps/02-fix-increment-1.md
git commit -m "test(web): catch a shadow painted through filter: drop-shadow() (#651)"
git push origin feat/651-652-spa-elevation-and-caps
```

## Step 4 — reply on the review thread

Do NOT reply on GitHub. Report back to the driver instead with: the commit SHA, the M10-M13 table with
the results you actually OBSERVED, and G1/G2 output. The driver answers the reviewer.

## Report back

Commit SHA, `git status --short` verbatim, G1 and G2 results, the four mutation rows as observed, and
anything here that turned out to be wrong.
