# Fix increment 2 — a red CI check, a real filter bug, and a guard that proves less than it claims

Three sources: CI, CodeRabbit on head `3e58fc6b`, and the driver. **All verified before dispatch.**

## 1. CI is red — and it is right (#394)

`Playwright smoke over the simulation fixture` fails:

```text
✘ specs/manager.spec.ts:188 › Manager › writes off lost stock from its own lot...
  Error: expect(locator).toBeVisible() failed
  Locator: getByText('No lots for this grade yet.')
  > 465 |  await expect(page.getByText(tEn("stock:noLotsMessage"))).toBeVisible();
```

Read what that spec does at line 462-465: it fills the lots date filter with `2000-01-01`/`2000-01-02`,
a window that cannot contain the lot, **so the list is filtered to nothing** — and then asserts the
truly-empty copy. Fix increment 1 correctly made that branch render `stock:noLotsMatch`.

**So the spec was pinning the wrong copy and CI caught the change.** Update line 465 to
`tEn("stock:noLotsMatch")`. Do not change the component to satisfy the old assertion.

**This was a driver miss:** increment 1's runbook told you to update the guard and the unit tests and
never told you to re-sweep the Playwright callers after changing user-visible strings, which is exactly
what #394 exists for. The driver has since swept all of them — the other seven assertions
(`readonly.spec.ts:40,54`, `owner.spec.ts:39,44,50`) are single-variant or `toBeHidden` and are
unaffected. Confirm that yourself with a grep rather than taking it.

## 2. A real functional bug — the status filter is invisible to the empty state

`SalesPage.tsx:1115` decides which variant to show with `customerFilter` **alone**. But `statusFilter`
exists (line 110) and is sent to the API (line 289). Two consequences, both user-visible:

- A **status-only** filter that matches nothing shows the *truly-empty* variant — "No orders yet." — on a
  farm that has orders.
- With **both** filters set, "Clear filters" clears only `customerId`, so the list stays empty after the
  user does exactly what the button said.

Fix: the filtered variant applies when **either** filter is set, and its action clears **both**.

**Then walk the other screens for the same shape.** Any screen whose empty-state condition tests a subset
of the filters actually applied to its query has this bug. Check every screen you converted, list what
you checked in your report, and fix what you find. This is the "walk everything, exclude deliberately"
rule — one instance found by review usually is not the only one.

## 3. The guard proves less than it claims

`emptyStates.guard.test.ts:33` checks that a route **imports** `EmptyState`. A route can import it,
never use it, still render a bare `<p className="muted">`, and pass.

Assert instead that each classified site has an `<EmptyState>` whose `message` prop is `t("<key>")` for
that site's key. **Verify by mutation, and report it**: revert one converted site to
`<p className="muted">{t("...")}</p>` while leaving the import in place, see the guard go red, restore.
If it stays green with the import present, the guard is still wrong.

## 4. Documentation

- `docs/plans/653-655-list-screens/02-fix-increment-1.md:7` — `#655's acceptance` at line start trips
  markdownlint MD018. Reword so the line does not begin with `#`.
- Same file, the key table says five new keys; six were added, because `noOrdersMessage` turned out to
  live in the `dashboard` namespace, not `sales`. **The driver wrote that table and it was wrong** —
  correct it, and keep the note about why, since the namespace trap is the useful part.

## Gates

**G1**, **G2**, and the Playwright smoke must go green. You cannot run Playwright locally; CI is the
check. Coverage baseline: statements 90.61 / branches 86.29 / functions 85.73 (floor 85) / lines 93.64.

## Commit and push

Separate commits for the spec fix, the filter fix, the guard fix and the docs — they answer different
findings and the threads are replied to individually.

```bash
git push origin feat/653-655-list-screens
```

Do NOT merge, do NOT mark ready or draft. Do NOT reply on GitHub — the driver answers the threads.

## Report back

Every commit SHA, the guard mutation result (red before, green after, with the exact assertion), the list
of screens you checked for the subset-filter bug and what you found, `git status --short`, and G1/G2.
