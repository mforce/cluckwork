# Products

What the farm sells (#97 part 1) and the packed-unit conversions behind it:
one route with two tabs (#908). Each egg product maps to an egg grade — sales
draw stock from that grade's lots. There is no hard delete; a deactivated
product stays referenced.

## Sub-features

- Products tab: table (name, grade, sold per, default price, status) plus an
  Actions column for admins, a selectable row -> `RecordInspector` with
  activate/deactivate, and the "New product" page-head button. On an empty
  catalog the button is withheld and the `EmptyState` offers the same action
  (#655). Admins hide the last column on the phone (`ProductsPage.tsx` sx).
- Create dialog (`web/src/routes/ProductDialogs.tsx`): name, grade (saleable
  grades only, `ProductsPage.tsx`), sold per, default price, notes. Notes
  exist on CREATE only — the edit dialog has no notes field (its comment says
  so explicitly); editing round-trips the stored notes untouched.
- Packed units tab: egg-unit conversions (unit, eggs per unit, status), now
  its own panel (`web/src/routes/PackedUnitsPanel.tsx`, rendered by
  `ProductsPage.tsx:486-487`). "Individual" always reads 1 and offers no
  edit (`products:alwaysOneMessage`).
- Edits are optimistic: the catalog responses carry `version`
  (`web/src/api/cluckwork/catalog.ts:17,25`), each edit dialog seeds it from
  its row (`startEdit`/`startEditConversion`, `ProductsPage.tsx:227,243`)
  and sends it back with the save (`version: editVersion` /
  `version: editConvVersion`, `ProductsPage.tsx:273,307`; the request
  bodies are typed with `version` at `catalog.ts:41,59`).
- Deactivation is soft: the product leaves the sale pickers (the endpoint
  summary in `ProductEndpoints.cs`) and the referenced product and saved
  price stay on recorded lines. An order ROW's name is not a snapshot —
  `SalesPage.tsx` resolves names from the current catalog (it loads
  `includeInactive: true` so deactivated references still resolve), so a
  renamed product shows its new name on existing orders. Only the audit
  payloads snapshot product names (`AuditEventRows.tsx`).

## How to get to it (user POV)

Nav: Setup, Products (`web/src/routes/nav.tsx`, shown to the `isAdmin` tier —
Admin or Manager, `web/src/auth/claims.ts:currentUserIsAdmin`). Route
`/products`. The route itself is authenticated, not role-gated: the catalog
and conversion reads accept any signed-in role, while every write
(create/update/activate/deactivate) is `AdminOnly`
(`src/Cluckwork.Api/Modules/Commerce/Catalog/ProductEndpoints.cs`), so
another role can deep-link here and read.

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
`products:packedUnitsHeading`). Status cells render through the
`statusLabel` function (`web/src/i18n/enums-status.ts`), which resolves the
keys `enums:status.Active` / `enums:status.Inactive` — assert with those
`tEn` keys, not a hardcoded "Active".

Proof: after deactivating a product its row reads Inactive (`StatusBadge`) and
the Activate action replaces Deactivate; after editing a packed unit the
inspector's eggs-per-unit value changes and the write lands in the audit log.
`inspector-layout.spec.ts` drives the products inspector (selection, action
order, dark palettes); `phone.spec.ts` proves the narrow ledger and inspector
on `/products`.

## Fixture data

- `default-farm`: Sim Large Eggs (45), Sim Medium Eggs (38), Sim Small Eggs
  (30), Sim Cracked Eggs (18), Sim Dirty Eggs (22), plus Sim Unpriced Eggs
  with no default price (#720) — prices in MINOR units, per `CatalogWanted`
  in `src/Cluckwork.Infrastructure/Persistence/SimulationDataSeeder.Sales.cs:30`.
- `readme-farm`: Large Eggs (45), Medium Eggs (38), Cracked Eggs (18, note
  "Sold at a discount") — `DemoProducts` in
  `src/Cluckwork.Infrastructure/Persistence/DemoDataSeeder.cs`, also minor
  units.
- Packed units for the DEFAULT farm come from the base-reference migration,
  which inserts rows for that fixed AccountId only: Individual 1, Dozen 12,
  Flat 30, Tray 30, Carton 12, Case 360 (`InsertUnitConversionIfMissing`,
  `20260801190854_InitialCreate.cs`). `readme-farm` is provisioned by
  `provision-account` (`tools/simulation/reset.sh`), whose
  `AccountProvisioner` calls `StageDefaults()` ->
  `EggUnitConversion.Defaults` (`EggUnitConversionProvisioning.cs`) — the
  same six values, from provisioning rather than the migration.

## Gotchas

- There is no "New product" button on an empty catalog — the EmptyState is the
  action (#655). Assert the EmptyState's button, not the page head.
- Prices are parsed as exact decimal strings into minor units
  (`toMinorUnits`, `ProductsPage.tsx`): plain decimals like "0.45" are fine;
  malformed input and excess precision are rejected. The error differs by
  dialog — CREATE keeps the browser's `min`/`step` validation
  (`ProductDialogs.tsx`), so the browser may stop it first, while EDIT is
  `noValidate` and always reaches the parser's message
  (`products:atMostDecimals`, or `products:noDecimalPlaces` for a
  zero-decimal currency). Never compute expected values with `value * 100`.
- A stale edit — two tabs open, or another admin saving first — is refused,
  not silently overwritten. The handlers compare the row's loaded `Version`
  with the command's and fail with `Product.VersionMismatch`
  (`src/Cluckwork.Application/Modules/Commerce/Catalog/UpdateProduct/UpdateProductHandler.cs:22-24`)
  or `EggUnitConversion.VersionMismatch`
  (`.../UpdateEggUnitConversion/UpdateEggUnitConversionHandler.cs:17-19`);
  the endpoint answers HTTP 409 with that code as the ProblemDetails title
  (`src/Cluckwork.Api/Modules/Commerce/Catalog/ProductEndpoints.cs:143-145`),
  and the EF concurrency token can 409 with the literal title
  "Concurrency conflict" racing the handler
  (`src/Cluckwork.Api/Program.cs:404,422`). The dialogs treat ONLY those
  three titles as version conflicts (`VERSION_CONFLICT_TITLES`,
  `ProductsPage.tsx:40-44`) — any other 409 (duplicate name, say) keeps the
  form and its idempotency key. On a real conflict the loser rebinds: it
  clears its key, refreshes, reopens the dialog seeded from the winner's
  values (`ProductsPage.tsx:281-290` products, `:309-317` conversions) and
  shows `products:conflictRebindMessage` /
  `products:packedUnitConflictRebindMessage` (`web/src/i18n/en.ts:1621-1627`) —
  "This product was changed by someone else — the form now shows the latest
  values; re-apply your changes."
- Mixed-currency rows are legal: the create dialog prices in the account
  currency while an edit dialog uses that row's snapshot — do not assume one
  currency across the table.
- Changing a packed unit only affects future sales; snapshotted sale lines
  keep their factor (`products:packedUnitsIntro`, `ProductEndpoints.cs`).
- On a phone viewport an admin's Actions column is hidden — drive row actions
  from the inspector, as `phone.spec.ts` does.
