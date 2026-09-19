# Dashboard

The morning screen uses the Operations Desk composition from #906. It shows the farm's
captured date and time, a morning brief, collection progress, stock by grade, recent
orders and lay rate. The stock response has no floor configuration, so the study's
low-stock warning is deliberately absent.

## Sub-features

- Morning brief: missing houses, folding into "+N more", beside today's total.
- Morning collection: missing-first rows, an outlined Record action in the count column for an unrecorded house, Continue for
  a draft, and submitted-entry links. Progress counts any non-voided entry, including
  drafts. The list is capped at twelve rows. Yesterday's total appears only for a
  complete day.
- Available stock: grade composition and a Grade, Count, Share table. Tab focuses each
  whole row; hover and focus add an outline and underline as well as background colour.
- Recent orders: customer and line details left, amount and status right. The first
  line shows quantity and current grade name, followed by +N for additional lines.
- Lay rate: the server's hen-day figure and comparison, followed by fourteen daily
  bars. Missing days are empty, partial days hatched, complete days solid. On a phone
  all fourteen days stay in one row, with 22×80px slots, the average and week break.
  The owner accepted the narrower targets on 2026-09-19: a wrong tap only changes
  the day readout, and one tab stop plus arrow keys reaches every day.

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

- On `default-farm` every catalog flock is unrecorded, so the collection list has twelve missing rows and a draft you create never reaches it; use `readme-farm` for anything that needs
  a recorded or draft row.
- The fortnight strip hatches days where any active house did not file; on the demo farm
  the first seven days are hatched by the seed (#886).
- Section headings retain their existing accessible names and linked destinations.
