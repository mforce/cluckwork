import { test, expect } from "../src/fixtures";
import { owner } from "../src/cast";
import { tEn } from "../src/i18n";

test("Sales draft keeps a commercial row in the first desktop frame", async ({ page, signIn }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await signIn(owner());
  await page.goto("/sales");
  const draft = page.getByRole("row")
    .filter({ has: page.getByRole("cell", { name: "Sim Customer 1", exact: true }) })
    .filter({ hasText: tEn("enums:status.Draft") });
  await draft.getByRole("button", { name: tEn("sales:open"), exact: true }).click();
  await expect(page.getByRole("region")).toBeVisible();
  await page.evaluate(() => window.scrollTo(0, 0));
  const firstCommercialRow = page.getByRole("table").last().locator("tbody tr").first();
  const box = await firstCommercialRow.boundingBox();
  expect(box, "the commercial ledger must contain a row").not.toBeNull();
  expect(box!.y + box!.height, "the draft desk pushes the commercial record below the first frame").toBeLessThanOrEqual(800);
});
