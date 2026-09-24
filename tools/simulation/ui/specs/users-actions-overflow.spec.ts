import { test, expect } from "../src/fixtures";
import { owner } from "../src/cast";
import { LANGUAGES, t } from "../src/i18n";

for (const language of LANGUAGES) {
  for (const theme of ["light", "dark"] as const) {
    for (const width of [390, 1024, 1280, 1366, 1440]) {
    test(`Users table fits at ${width} in ${language}, ${theme}${width === 390 ? " @phone" : ""}`, async ({ page, signIn }) => {
      await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
      await page.emulateMedia({ colorScheme: theme });
      await signIn(owner());
      await page.goto("/account");
      const languageSelect = page.locator('select:has(option[value="en"]):has(option[value="es"])');
      await languageSelect.selectOption(language);
      await expect(languageSelect).toBeEnabled();
      try {
        await page.goto("/users");
        await expect(page.getByRole("heading", { name: t(language, "users:heading"), exact: true })).toBeVisible();
        const table = page.getByRole("table");
        await expect(table.getByRole("button", { name: t(language, "users:editButton"), exact: true }).first()).toBeVisible();
        await page.evaluate(() => document.fonts.ready);
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
        if (width !== 390) expect(geometry.scrollWidth - geometry.clientWidth).toBeLessThanOrEqual(1);
        expect(geometry.documentWidth).toBe(width);
      } finally {
        await page.goto("/account");
        await languageSelect.selectOption("en");
        await expect(languageSelect).toBeEnabled();
      }
    });
    }
  }
}
