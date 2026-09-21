import { test, expect } from "../src/fixtures";
import { owner } from "../src/cast";
import { tEn } from "../src/i18n";

for (const theme of ["light", "dark"] as const) {
  test(`Field Console row links and headings follow the shared ${theme} theme`, async ({ page, signIn }) => {
    await page.emulateMedia({ colorScheme: theme });
    await signIn(owner());
    await page.goto("/history");
    const brand = await page.evaluate(() => {
      const probe = document.createElement("span");
      probe.style.color = "var(--brand)";
      document.body.append(probe);
      const color = getComputedStyle(probe).color;
      probe.remove();
      return color;
    });
    expect(brand).toBe("rgb(74, 21, 75)");
    for (const label of [tEn("common:recordHistory.viewHistoryLink"), tEn("history:editButton")]) {
      await expect(page.getByRole("link", { name: label, exact: true }).first()).toHaveCSS("color", brand);
    }
    await expect(page.getByRole("button", { name: tEn("history:adjustButton"), exact: true }).first()).toHaveCSS("color", brand);
    await page.goto("/reports");
    const section = page.getByRole("heading", { name: tEn("reports:productionHeading"), exact: true });
    await expect(section).toHaveCSS("font-size", "15px");
    expect(await section.evaluate(el => getComputedStyle(el).fontFamily)).not.toContain("Georgia");
    await page.goto("/water");
    await expect(page.getByRole("button", { name: tEn("water:correctButton"), exact: true }).first()).toHaveCSS("color", brand);
  });

  test(`payment dialog uses the shared h2 typography in ${theme}`, async ({ page, signIn }) => {
    await page.emulateMedia({ colorScheme: theme });
    await signIn(owner());
    await page.goto("/sales");
    const confirmed = page.getByRole("row").filter({ hasText: "Sim Customer 3" })
      .filter({ hasText: tEn("enums:status.Confirmed") }).first();
    await confirmed.getByRole("button", { name: tEn("sales:open"), exact: true }).click();
    await page.getByRole("button", { name: tEn("sales:recordPayment"), exact: true }).click();
    const title = page.getByRole("dialog").getByRole("heading", { name: tEn("sales:recordPayment"), exact: true });
    expect(await title.evaluate(el => el.tagName)).toBe("H2");
    await expect(title).toHaveCSS("font-family", "Georgia, serif");
  });
}
