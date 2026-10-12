# Products

What the farm sells (#97 part 1) and the packed-unit conversions behind it:
one route with two tabs (#908). Each egg product maps to an egg grade — sales
draw stock from that grade's lots. There is no hard delete; history keeps a
deactivated product's name.

## Sub-features

- Products tab: table (name, grade, sold per, default price, status) plus an
  Actions column for admins, a selectable row -> `RecordInspector` with
  activate/deactivate, and the "New product" page-head button. On an empty
  catalog the button is withheld and the `EmptyState` offers the same action
  (#655). Admins hide the last column on the phone (`ProductsPage.tsx` sx).
- Create/edit dialogs (`web/src/routes/ProductDialogs.tsx`): name, grade
  (saleable grades only, `ProductsPage.tsx`), sold per, default price, notes.
  Prices are parsed as exact strings into minor units; create uses the
  account currency, edit uses the row's own snapshot (codex review of #98).
- Packed units tab: egg-unit conversions (unit, eggs per unit, status).
  "Individual" always reads 1 and offers no edit (`products:alwaysOneMessage`).
- Deactivation is soft: the product leaves sales pickers; recorded orders keep
  the name and price they were sold with.

## How to get to it (user POV)

Nav: Setup, Products (`web/src/routes/nav.tsx`, shown to the `isAdmin` tier —
Admin or Manager, `web/src/auth/claims.ts:currentUserIsAdmin`). Route
`/products`.

## Driving it with Playwright

```ts
import { test, expect } from "../src/fixtures";
import { owner } from "../src/cast";
import { tEn } from "../src/i18n";

await signIn(owner());
await page.goto("/products");
const rows = page.getByRole("region", { name: tEn("products:title") }).getByRole("row");
await rows.nth(1).getByRole("cell").first().click();
await expect(rows.nth(1)).toHaveAttribute("aria-selected", "true");
await page.getByRole("tab", { name: tEn("products:packedUnitsHeading") }).click();
```

Selecting a row opens the inspector (the same pattern `phone.spec.ts` uses);
the inspector closes via the button named `tEn("audit:closeInspector")`.

Labels come through `tEn("products:…")` (`products:newProductButton`,
`products:editButton`, `products:deactivateButton`,
`products:packedUnitsHeading`); status cells route through
`enums:statusLabel`, not the catalog.

Proof: after deactivating a product its row reads Inactive (`StatusBadge`) and
the Activate action replaces Deactivate; after editing a packed unit the
inspector's eggs-per-unit value changes and the write lands in the audit log.
`inspector-layout.spec.ts` drives the products inspector (selection, action
order, dark palettes); `phone.spec.ts` proves the narrow ledger and inspector
on `/products`.

## Fixture data

- `default-farm`: Sim Large Eggs (45), Sim Medium Eggs (38), Sim Small Eggs
  (30), Sim Cracked Eggs (18), Sim Dirty Eggs (22), plus Sim Unpriced Eggs
  with no default price (#720) — `CatalogWanted` in
  `src/Cluckwork.Infrastructure/Persistence/SimulationDataSeeder.cs`.
- `readme-farm`: Large Eggs (45), Medium Eggs (38), Cracked Eggs (18, note
  "Sold at a discount") — `DemoProducts` in
  `src/Cluckwork.Infrastructure/Persistence/DemoDataSeeder.cs`.
- Packed units come from the base-reference migration, not a seeder:
  Individual 1, Dozen 12, Flat 30, Tray 30, Carton 12, Case 360
  (`InsertUnitConversionIfMissing`, `20260801190854_InitialCreate.cs`).

## Gotchas

- There is no "New product" button on an empty catalog — the EmptyState is the
  action (#655). Assert the EmptyState's button, not the page head.
- Price fields reject floats and excess decimals with
  `products:enterPriceAsNumber` / `products:atMostDecimals`; drive them with
  plain decimal strings and never `value * 100`.
- Mixed-currency rows are legal: the create dialog prices in the account
  currency while an edit dialog uses that row's snapshot — do not assume one
  currency across the table.
- Changing a packed unit only affects future sales; recorded orders keep the
  count they were sold with (`products:packedUnitsIntro`).
- On a phone viewport an admin's Actions column is hidden — drive row actions
  from the inspector, as `phone.spec.ts` does.
