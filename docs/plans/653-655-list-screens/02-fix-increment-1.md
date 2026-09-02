# Fix increment 1 — the two empty-state variants say the same sentence

Driver review of the #655 dispatch.

## The finding

#655's acceptance: **"Filtered-empty vs truly-empty distinguished on the screens that have filters."**

The two variants are distinguished by **icon and action** — `FilterX` + "Clear filters" versus the
screen's icon + its create action. They are **not** distinguished by the sentence, and the sentence is
the part that tells a user *why* the screen is empty. Every two-variant screen passes the same `message`
to both branches:

| Screen | Both branches say | Reads wrongly as |
|---|---|---|
| `SalesPage.tsx:1116/1128` | `noOrdersMatch` — *"No orders match."* | a farm with **zero orders and no filter** is told "No orders match." |
| `FlocksPage.tsx:366/368` | `noFlocksMessage` — *"No flocks yet — …"* | a **filtered** view is told there are no flocks *at all* |
| `FeedPage.tsx:374/379` | `noRecordsMatch` | truly-empty told "…match" |
| `WaterPage.tsx:484/489` | `noRecordsMatch` | same |
| `StockPage.tsx:548/550` | `noLotsMessage` | filtered told "no lots yet" |
| `HistoryPage.tsx:733/738` | `noEntriesMatch` | truly-empty told "…match" |

Both directions of the error are present, which is what makes it systematic rather than a slip.

**Sales is the clearest case and the cheapest fix**: `noOrdersMessage: "No orders yet."` already exists,
unused by the new code, three lines from the key that is being used for both.

## What exists and what is missing

Driver-checked in `web/src/i18n/en.ts`. Keys appearing twice are per-namespace, one per page — that is
correct and not a duplicate.

| Needed | Status |
|---|---|
| `noOrdersMatch` / `noOrdersMessage` | **both exist** — just use the right one per branch |
| `noFlocksMessage` (truly-empty) | exists; **`noFlocksMatch` missing** |
| `noRecordsMatch` (filtered, feed + water namespaces) | exists; **`noRecordsMessage` missing in both** |
| `noLotsMessage` (truly-empty) | exists; **`noLotsMatch` missing** |
| `noEntriesMatch` (filtered) | exists; **`noEntriesMessage` missing** |

So: **five new keys** (`noFlocksMatch`, `noRecordsMessage` ×2 namespaces, `noLotsMatch`,
`noEntriesMessage`), in `en`, `es` and `tl`.

## Copy

Follow the voice of the keys already there, and the interface's voice generally — say what to do, no
apology, no mood. Note the existing truly-empty strings do more than name the absence: *"No flocks yet —
create one on the Daily entry screen"*, *"No lots yet — corrections target a received lot"*. Match that
usefulness where it applies.

- **Filtered** variants say the filter matched nothing, and the action offers to clear it.
- **Truly-empty** variants say what will appear here and, where the screen has a create action, that it
  is available.

`es` and `tl` inline in the same commit, machine-drafted, flagged for native review where unsure — the
repo's standing policy.

## The guard

`web/src/routes/emptyStates.guard.test.ts` currently pins that the classified sites use `EmptyState`.
**Extend it**: for every screen that renders both variants, assert the two branches resolve to
**different** message keys. That is the acceptance criterion, expressed as a test — without it the same
regression walks back in the first time someone copies a branch.

Write the assertion so it fails on the current code before you fix it, and say in your report that you
saw it fail.

## Not in scope

The single-variant screens — `CustomersPage`, `ProductsPage`, `ExpensesPage`, `Dashboard`,
`StockPage:499` — are correct as they are. `ExpensesPage` has no filtered variant because its month
picker is always set, which the previous dispatch reported and the driver accepts.

## Gates

**G1** and **G2**. Coverage: statements 90.61 / branches 86.29 / functions **85.73** (floor 85) / lines
93.64. You are adding strings and one assertion, so this should not move materially. **On a trip, STOP
and report** — do not edit `vite.config.ts`, do not pad.

## Commit

```bash
git add web/src/routes/ web/src/i18n/ docs/plans/
git commit -m "fix(web): tell an empty screen apart from a filtered one in words, not just icons (#655)"
git push origin feat/653-655-list-screens
```

Then update PR #668's body: this finding, what changed, and the guard.

## Report back

Commit SHA, the five new keys with their `en` copy, confirmation that the new guard assertion failed
before the fix, `git status --short`, and G1/G2 with the four coverage numbers.
