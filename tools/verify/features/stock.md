# Stock

Eggs on hand by grade, the lots behind each grade, and the movement ledger: allocations
to orders, write-offs, adjustments.

## Sub-features

- Stock by grade with the stacked bar (`StockBar`, also on the Dashboard).
- Lots per grade ("Lots" button, `tEn("stock:lotsHeading")`), paged with "Load more"
  (#465).
- Write-off from a lot (`tEn("stock:writeOffButton")`, then the submit button).
- Movement ledger with a date range (`tEn("stock:fromLabel")`, `tEn("stock:toLabel")`).
- General inventory (feed and supplies) is a separate screen at `/inventory`.

## How to get to it (user POV)

Nav: Sales & stock, Stock; the Stock tab on the phone. Route `/stock`.

## Driving it with Playwright

```ts
await signIn(castMember("Manager"));
await page.goto("/stock");
await page.getByRole("button", { name: tEn("stock:lotsButton") }).first().click();
await expect(page.getByRole("heading", { name: tEn("stock:lotsHeading") })).toBeVisible();
await page.getByRole("button", { name: tEn("stock:writeOffButton") }).first().click();
// fill the quantity and reason, then
await page.getByRole("button", { name: tEn("stock:writeOffSubmitButton") }).click();
```

Proof: the grade's available count drops by the written-off quantity, the ledger shows the
movement with the actor, and the daily entry that created the lot is untouched
(`manager.spec.ts` "writes off lost stock from its own lot without touching the entry").
`readonly.spec.ts` proves a ReadOnly user pages lots with Load more and cannot write.

## Gotchas

- A deep grade has more lots than one page; assert after "Load more", not before.
- Write-offs need a lot with quantity; pick the first lot of the largest grade on a fresh
  reset.
- The bar's grade hues are the only place grade colour appears (direction); do not assert
  on colour elsewhere.
