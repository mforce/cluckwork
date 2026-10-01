import { expect, type Fixtures, type Page } from "./fixtures";
import { daysBefore, farmToday } from "./farm";
import { tEn } from "./i18n";

export async function openSeededExpenses({ page, farm, shellLayout }: Pick<Fixtures, "farm" | "shellLayout"> & { page: Page }) {
  // A fresh seed's recent expenses can all fall in the previous farm month.
  const to = farmToday(farm.timeZoneId);
  const from = daysBefore(to, 30);
  // At a 31-day month's end, the initial request already has this range.
  const loaded = page.waitForResponse((response) => {
    const url = new URL(response.url());
    return response.request().method() === "GET" && url.pathname === "/api/v1/expenses"
      && url.searchParams.get("from") === from && url.searchParams.get("to") === to;
  });
  await page.goto("/expenses");
  if (shellLayout === "phone") {
    await page.getByRole("button", { name: tEn("expenses:allCategoriesOption"), exact: true }).click();
  }

  await page.getByLabel(tEn("expenses:fromLabel"), { exact: true }).fill(from);
  await page.getByLabel(tEn("expenses:toLabel"), { exact: true }).fill(to);
  const response = await loaded;
  expect(response.ok()).toBe(true);
  await response.finished();

  if (shellLayout === "phone") {
    await page.getByRole("dialog", { name: tEn("expenses:filtersTitle") })
      .getByRole("button", { name: tEn("common:close"), exact: true }).click();
    await expect(page.getByRole("list", { name: tEn("expenses:ledgerHeading") })).toBeVisible();
  } else {
    await expect(page.getByRole("table")).toBeVisible();
  }
}
