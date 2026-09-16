# Dashboard

The morning screen: which houses have filed today, eggs on hand by grade, the last
fortnight's production, recent orders. Since #883 it is the ruled ledger from
`docs/designs/864-visual-language/` at 1280 and a ruled house list at 390.

## Sub-features

- Attention line under the title: missing houses first, folding into "+N more" (two items
  at 1280, one at 390).
- Today: one ruled row per active house with entry state and time, a Record button (the
  single filled button on the page) on an unrecorded house, Continue as ruled text on a
  draft, a count on the right, "Today so far" with "Yesterday by close: N" under it. Capped
  at twelve rows.
- Recent sales: customer with the order number under it, amount, status as dot plus word,
  "Review to confirm" on a draft. No eggs-and-grade cell yet (#887).
- Stock: eggs available, the stacked grade bar, the grade table.
- Last 14 days: the day strip with the hen-day figure and delta.

## How to get to it (user POV)

Sign in; it is the landing route `/`. On the phone it lives under More in the tab bar
(Dashboard is not one of the four tabs).

## Driving it with Playwright

```ts
import { test, expect } from "../src/fixtures";
import { owner, readmeFarmOwner } from "../src/cast";
import { tEn } from "../src/i18n";

await signIn(readmeFarmOwner());          // demo farm: two houses, one draft, real orders
await page.goto("/");
await expect(page.getByRole("heading", { name: tEn("dashboard:title") })).toBeVisible();
const row = page.getByRole("group", { name: "House 2 layers" });        // one Today row
await expect(row.getByRole("link", { name: /Record/ })).toBeVisible();  // the filled action
const sales = page.getByRole("list", { name: tEn("dashboard:salesPanelTitle") });
await expect(sales.getByRole("listitem").first()).toBeVisible();
```

Proof: the row's count changes after a daily entry is submitted for that house (drive
`/daily-entry`, come back, read the number); a draft order shows "Review to confirm" and
the link lands on `/sales` filtered to that customer.

## Gotchas

- On `default-farm` every catalog flock is unrecorded, so the Today list is twelve amber
  bands and a draft you create never reaches it; use `readme-farm` for anything that needs
  a recorded or draft row.
- The fortnight strip hatches days where any active house did not file; on the demo farm
  the first seven days are hatched by the seed (#886).
- Section headings that are links are underlined by design (#884).
