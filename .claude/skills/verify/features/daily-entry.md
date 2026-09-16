# Daily entry

The screen a worker uses standing in the shed: pick the house and the date, count eggs
and losses, grade them, save a draft or submit the day. Submitting creates egg lots.
Field-first on the phone per #830.

## Sub-features

- Context: flock picker (searchable, `commitNamedPicker`), date, "+ new flock".
- Egg counts pane: total eggs, cracked, dirty, discarded, mortality, each a stepper row.
- Grading pane: one stepper row per sellable grade, "Counted N" in the head, the sum row
  that says whether the day adds up.
- Footer: Save draft and Submit day side by side (the #740 exemption; both 48px).
- Draft state line under the title ("Draft, saved HH:MM" in the farm clock).
- Adjust and void after submit happen on History, not here (`manager.spec.ts`).

## How to get to it (user POV)

Nav: Production, Daily entry, or the Daily entry tab on the phone. Route `/daily-entry`.
A Worker sees only assigned flocks in the picker (#388).

## Driving it with Playwright

```ts
import { test, expect } from "../src/fixtures";
import { unrestrictedWorker } from "../src/cast";
import { commitNamedPicker } from "../src/dom";
import { farmToday } from "../src/farm";
import { tEn } from "../src/i18n";

await signIn(unrestrictedWorker());
await page.goto("/daily-entry");
await page.getByLabel(tEn("dailyEntry:dateLabel")).fill(farmToday(farm.timeZoneId));
await commitNamedPicker(page, tEn("dailyEntry:flockLabel"), "Sim House A");
await page.getByLabel(tEn("dailyEntry:totalEggsLabel"), { exact: true }).fill("40");
await page.getByRole("button", { name: tEn("dailyEntry:saveDraftButton") }).click();
await expect(page.getByText(tEn("dailyEntry:draftSavedMessage"))).toBeVisible();
```

Proof: after Submit day, `/stock` shows new lots for that flock and date, and the
Dashboard's Today row for the house reads recorded with the time. `worker.spec.ts` records
by grade; `manager.spec.ts` submits then adjusts and voids on History.

## Gotchas

- Respect the farm clock: fill dates with `farmToday(farm.timeZoneId)`, never the
  machine's date.
- A restricted Worker is refused on an unassigned flock (#388); use
  `unrestrictedWorker()` for write proofs.
- The number field keeps its own stepper logic (#828 owns its conversion); the row grid is
  the screen's. Hold-to-repeat is real, so do not hold a button while asserting.
- The phone footer must stay clear of the tab bar (`phone.spec.ts` pins the margin).
