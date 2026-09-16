# Flocks

The houses: create a flock (name, breed, placed date, bird count), see its status and bird
movements, deplete it at end of lay.

## Sub-features

- Flock list with status and current bird count.
- New flock dialog (`tEn("flocks:newFlockButton")`, `newFlockDialogTitle`, `nameLabel`,
  `breedLabel`, `placedLabel`, `birdsLabel`, `addFlockButton`).
- Bird movement ledger per flock, paged (`pagination.spec.ts`, "Sim House A's
  bird-movement ledger reveals its page-two sentinel").
- Flock scoping: a Worker's assignments decide which flocks appear in pickers (#388,
  #613).

## How to get to it (user POV)

Nav: Production, Flocks. Route `/flocks`. Also reachable from Daily entry's "+ new flock".

## Driving it with Playwright

```ts
await signIn(castMember("Manager"));
await page.goto("/flocks");
await page.getByRole("button", { name: tEn("flocks:newFlockButton") }).click();
const dialog = page.getByRole("dialog", { name: tEn("flocks:newFlockDialogTitle") });
await dialog.getByLabel(tEn("flocks:nameLabel")).fill(`E2E Flock ${Date.now()}`);
await dialog.getByLabel(tEn("flocks:breedLabel")).fill("E2E Leghorn");
await dialog.getByLabel(tEn("flocks:placedLabel")).fill(farmToday(farm.timeZoneId));
await dialog.getByLabel(tEn("flocks:birdsLabel"), { exact: true }).fill("50");
const created = page.waitForResponse((r) => r.url().includes("/api/v1/flocks") && r.request().method() === "POST" && r.ok());
await dialog.getByRole("button", { name: tEn("flocks:addFlockButton") }).click();
const { id } = await (await created).json();
```

Proof: the new flock appears in the list and in the Daily entry picker (`commitNamedPicker`
resolves the same id); `manager.spec.ts` continues into an entry, an adjustment and a
void.

## Gotchas

- Names starting with `E2E` sort ahead of the seeded `Sim` ones and persist until the next
  reset; a capture taken after a smoke run photographs test data.
- The Dashboard caps Today at twelve rows, so a new flock on `default-farm` is not visible
  there; prove creation on `/flocks` or in the picker.
