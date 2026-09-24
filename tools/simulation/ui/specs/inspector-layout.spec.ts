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
    test(`${route} inspector keeps a thin dark header, readable text, ordered actions and available phone height in ${theme} @phone`, async ({ page, signIn }) => {
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
      for (const brand of ["aubergine", "forest", "slate", "terracotta"]) {
        await page.evaluate((value) => { document.documentElement.dataset.brand = value; }, brand);
        const styles = await inspector.getByRole("heading").evaluate((heading) => {
          const text = getComputedStyle(heading);
          const header = getComputedStyle(heading.parentElement!);
          const luminance = (color: string) => {
            const channels = color.match(/[\d.]+/g)!.slice(0, 3).map((value) => {
              const channel = Number(value) / 255;
              return channel <= .04045 ? channel / 12.92 : ((channel + .055) / 1.055) ** 2.4;
            });
            return .2126 * channels[0]! + .7152 * channels[1]! + .0722 * channels[2]!;
          };
          const foreground = luminance(text.color);
          const background = luminance(header.backgroundColor);
          return {
            color: text.color, background: header.backgroundColor,
            textOpacity: text.opacity, headerOpacity: header.opacity,
            contrast: (Math.max(foreground, background) + .05) / (Math.min(foreground, background) + .05),
            height: heading.parentElement!.getBoundingClientRect().height,
            children: heading.parentElement!.children.length,
            paddingTop: header.paddingTop, paddingBottom: header.paddingBottom,
          };
        });
        expect.soft(styles.color, brand).toBe("rgb(255, 255, 255)");
        expect.soft(styles.background, brand).toBe("rgb(44, 36, 41)");
        expect.soft(styles.contrast, brand).toBeGreaterThanOrEqual(4.5);
        expect.soft(styles.textOpacity, brand).toBe("1");
        expect.soft(styles.headerOpacity, brand).toBe("1");
        expect.soft(styles.children, brand).toBe(1);
        expect.soft(styles.paddingTop, brand).toBe("4px");
        expect.soft(styles.paddingBottom, brand).toBe("4px");
        expect.soft(styles.height, brand).toBeLessThanOrEqual(40);
      }
      const actionLabels = await inspector.locator("button, a").allTextContents();
      expect.soft(actionLabels.map((label) => label.trim())).toEqual(actions);
      if (route !== "customers") {
        await expect(inspector.locator("button, a").last().locator("..")).toHaveCSS("border-left-width", "1px");
      }
      const pane = inspector.locator("../..");
      const nav = page.locator('nav').filter({ has: page.locator('.MuiBottomNavigation-root') });
      const paneBounds = await pane.boundingBox();
      const navBounds = await nav.boundingBox();
      expect.soft(navBounds!.y - (paneBounds!.y + paneBounds!.height)).toBeGreaterThanOrEqual(6);
      expect.soft(navBounds!.y - (paneBounds!.y + paneBounds!.height)).toBeLessThanOrEqual(10);
      await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
      await inspector.locator("button, a").last().scrollIntoViewIfNeeded();
      const actionBounds = await inspector.locator("button, a").last().boundingBox();
      expect(actionBounds!.y + actionBounds!.height).toBeLessThanOrEqual(navBounds!.y);
    });
  }
}
