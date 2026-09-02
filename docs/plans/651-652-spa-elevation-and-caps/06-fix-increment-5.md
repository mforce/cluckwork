# Fix increment 5 — say in the stylesheet that `.toolbar` is waiting for #653

Owner decision, 2026-09-02, after reviewing before/after screenshots: keep `.toolbar` and `--r-panel`,
and record why they exist so the next reader does not delete them as dead code.

## The finding

`.toolbar` has **zero consumers**. `grep -rn "toolbar" web/src --include='*.tsx' --include='*.ts'` returns
only this slice's own guard file; the built JS contains no such class name. `--r-panel` has exactly one
consumer, that same unrendered rule.

This is the third selector in this slice whose issue text described a stylesheet that had moved on —
after `.stat`, which no longer exists, and `.eyebrow`, which was dead and got deleted. The difference is
that `.toolbar` is **not** dead by mistake: the delivery contract's ownership map records #653 ("table
layout — date-range filters into a bounded toolbar") as the slice that introduces its consumers. So it
is groundwork, and the owner chose to keep it.

Nothing about that is inferable from the stylesheet, which is why it goes in the stylesheet.

## Files

- `web/src/styles.css` — a comment only. **No declaration changes.** The rule keeps every property it has.

## Step 1

The comment block above `.toolbar` currently reads:

```css
/* -------------------------------------------- Toolbar (inset filter bar) */
/* --canvas and --surface-2 sit at 1.05-1.21:1 in every palette, so a
   borderless fill would be invisible — the hairline border stays for that
   reason, not because this is a floating card. */
```

Replace those two comment blocks with this one. **PROTECTED — transcribe or STOP.** Do not touch the
`.toolbar { ... }` declarations beneath it.

```css
/* -------------------------------------------- Toolbar (inset filter bar) */
/* NOT DEAD CODE, and not rendered yet either: nothing in web/src applies
   `.toolbar` today. It is styled here ahead of #653, which moves the
   date-range filters into a bounded toolbar and is the slice that adds the
   consumers. Deleting it would make #653 re-derive the two decisions below.
   Check `grep -rn toolbar web/src --include='*.tsx'` before assuming it is
   still unused — the day #653 lands, this note is stale.
   --r-panel likewise has this rule as its only consumer today.

   Why it is inset rather than carded (#651): elevation encodes what floats,
   and a filter bar does not. Why it keeps a hairline border despite being
   inset: --canvas and --surface-2 sit between 1.05:1 and 1.21:1 in every
   palette and both modes — measured with the repo's own contrast() helper —
   so a borderless fill is invisible on every one of them, not just the light
   ones. */
```

## Step 2 — gates and commit

**G1** then **G2**, both clean. A comment change moves nothing, so the counts stay
`Test Files 110 passed (110)` / `Tests 2411 passed (2411)`.

Confirm with `git diff --stat` that exactly one file changed and that
`git diff web/src/styles.css | grep -c '^[+-][^+-]'` counts only comment lines — **if any declaration
line appears in the diff, STOP and report**, because this increment must not change rendering.

```bash
git add web/src/styles.css docs/plans/651-652-spa-elevation-and-caps/06-fix-increment-5.md
git commit -m "docs(web): record that .toolbar is styled ahead of #653, not dead (#651)"
git push origin feat/651-652-spa-elevation-and-caps
```

Do NOT reply on GitHub, do NOT edit the PR description, do NOT merge.

## Report back

Commit SHA, `git status --short`, G1 and G2 output, and the output of the declaration-line check above.
