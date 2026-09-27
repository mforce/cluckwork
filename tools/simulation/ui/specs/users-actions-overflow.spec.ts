import { test, expect } from "../src/fixtures";
import { owner } from "../src/cast";
import { LANGUAGES, t } from "../src/i18n";

for (const language of LANGUAGES) {
  test(`Users table fits every supported viewport and theme in ${language}`, async ({ page, signIn }) => {
    await signIn(owner());
    await page.goto("/account");
    const languageSelect = page.locator('select:has(option[value="en"]):has(option[value="es"])');
    await languageSelect.selectOption(language);
    await expect(languageSelect).toBeEnabled();
    try {
      await page.goto("/users");
      await expect(page.getByRole("heading", { name: t(language, "users:heading"), exact: true })).toBeVisible();
      const table = page.getByRole("table");
      await page.evaluate(() => document.fonts.ready);
      for (const theme of ["light", "dark"] as const) {
        await page.emulateMedia({ colorScheme: theme });
        await page.evaluate((value) => { document.documentElement.dataset.theme = value; }, theme);
        for (const width of [1024, 1280, 1366, 1440, 390]) {
          await test.step(`${theme}, ${width}px`, async () => {
            await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
            await expect(page.getByRole("heading", { name: t(language, "users:heading"), exact: true })).toBeVisible();
            const rowEdit = table.getByRole("button", { name: t(language, "users:editButton"), exact: true });
            if (width === 390) {
              await expect(rowEdit).toHaveCount(0);
              await table.getByRole("row").nth(1).getByRole("cell").first().click();
              const inspector = page.getByRole("region", {
                name: t(language, "common:inspectorLabel", { entity: t(language, "users:entitySingular") }),
              });
              await expect(inspector.getByRole("button", { name: t(language, "users:editButton"), exact: true })).toBeVisible();
            } else {
              await expect(rowEdit.first()).toBeVisible();
            }
            const geometry = await table.evaluate((element) => {
              const container = element.parentElement;
              if (!container) throw new Error("Users table has no container");
              return {
                clientWidth: container.clientWidth,
                scrollWidth: container.scrollWidth,
                documentWidth: document.documentElement.scrollWidth,
              };
            });
            console.log(JSON.stringify({ width, language, theme, ...geometry }));
            if (width !== 390) expect.soft(geometry.scrollWidth - geometry.clientWidth).toBeLessThanOrEqual(1);
            expect.soft(geometry.documentWidth).toBe(width);
          });
        }
      }
    } finally {
      await page.goto("/account");
      await languageSelect.selectOption("en");
      await expect(languageSelect).toBeEnabled();
    }
  });
}
