import type { Fixtures, Page } from "./fixtures";
import { daysBefore, farmToday } from "./farm";
import { tEn } from "./i18n";

export async function openSeededExpenses({ page, farm, shellLayout }: Pick<Fixtures, "farm" | "shellLayout"> & { page: Page }) {
  await page.goto("/expenses");
  if (shellLayout === "phone") {
    await page.getByRole("button", { name: tEn("expenses:allCategoriesOption"), exact: true }).click();
  }

  // Match the preflight's 30-day window; the current month can contain no seed expenses.
  const to = farmToday(farm.timeZoneId);
  await page.getByLabel(tEn("expenses:fromLabel"), { exact: true }).fill(daysBefore(to, 30));
  await page.getByLabel(tEn("expenses:toLabel"), { exact: true }).fill(to);

  if (shellLayout === "phone") {
    await page.getByRole("dialog", { name: tEn("expenses:filtersTitle") })
      .getByRole("button", { name: tEn("common:close"), exact: true }).click();
  }
}
