import { test, expect } from "../src/fixtures";
import { owner, castMember } from "../src/cast";

const screens = [
  { route: "users", actions: ["edit", "role", "password", "change email", "flocks", "disable"] },
  { route: "customers", actions: ["edit"] },
  { route: "grades", actions: ["edit", "Audit history", "deactivate"] },
  { route: "flocks", actions: ["edit", "birds", "Audit history", "deplete", "archive"] },
  { route: "products", actions: ["edit", "deactivate"] },
];

for (const { route, actions } of screens) {
  for (const theme of ["light", "dark"] as const) {
    test(`${route} inspector uses its surface, ordered actions and available phone height in ${theme} @phone`, async ({ page, signIn }) => {
      await page.emulateMedia({ colorScheme: theme });
      await signIn(owner());
      await page.goto(`/${route}`);
      const table = page.getByRole("table").first();
      const row = route === "users"
        ? table.getByRole("row").filter({ hasText: castMember("Manager").email })
        : table.locator("tbody tr").first();
      await row.focus();
      await page.keyboard.press("Enter");
      const inspector = page.getByRole("region", { name: / details$/ }).filter({ has: page.getByRole("heading") });
      await expect(inspector.getByRole("heading")).toBeFocused();
      await page.evaluate(() => document.fonts.ready);
      const styles = await inspector.getByRole("heading").evaluate((heading) => ({
        color: getComputedStyle(heading).color,
        panelColor: getComputedStyle(heading.closest("aside")!).color,
        background: getComputedStyle(heading.parentElement!).backgroundColor,
      }));
      expect.soft(styles.color).toBe(styles.panelColor);
      expect.soft(styles.background).toBe("rgba(0, 0, 0, 0)");
      expect.soft(await inspector.locator("button, a").allTextContents()).toEqual(actions.map((action) => expect.stringMatching(new RegExp(`^\\s*${action}\\s*$`, "i"))));
      if (route !== "customers") {
        await expect(inspector.locator("button, a").last().locator("..")).toHaveCSS("border-left-width", "1px");
      }
      const pane = inspector.locator("../..");
      const nav = page.locator('nav').filter({ has: page.locator('.MuiBottomNavigation-root') });
      const paneBounds = await pane.boundingBox();
      const navBounds = await nav.boundingBox();
      expect.soft(navBounds!.y - (paneBounds!.y + paneBounds!.height)).toBeGreaterThanOrEqual(12);
      expect.soft(navBounds!.y - (paneBounds!.y + paneBounds!.height)).toBeLessThanOrEqual(20);
      await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
      await inspector.locator("button, a").last().scrollIntoViewIfNeeded();
      const actionBounds = await inspector.locator("button, a").last().boundingBox();
      expect(actionBounds!.y + actionBounds!.height).toBeLessThanOrEqual(navBounds!.y);
    });
  }
}
