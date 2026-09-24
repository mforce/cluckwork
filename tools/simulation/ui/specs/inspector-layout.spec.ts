import { test, expect } from "../src/fixtures";
import { owner, castMember } from "../src/cast";
import { t } from "../src/i18n";

const darkPalettes = {
  aubergine: { accent: [226, 180, 230], row: [102, 82, 105] },
  forest: { accent: [168, 220, 187], row: [82, 96, 89] },
  slate: { accent: [174, 207, 235], row: [84, 91, 106] },
  terracotta: { accent: [242, 183, 156], row: [107, 83, 79] },
};

const screens = [
  { route: "users", actions: ["edit", "role", "password", "change email", "flocks", "disable"] },
  { route: "customers", actions: ["edit"] },
  { route: "grades", actions: ["edit", "Audit history", "deactivate"] },
  { route: "flocks", actions: ["edit", "birds", "Audit history", "deplete", "archive"] },
  { route: "products", actions: ["edit", "deactivate"] },
];

for (const { route, actions } of screens) {
  test(`${route} inspector keeps the chosen header, readable selection, ordered actions and available phone height in both themes @phone`, async ({ page, signIn }) => {
    await signIn(owner());
    await page.goto(`/${route}`);
    const table = page.getByRole("table").first();
    const row = route === "users"
      ? table.getByRole("row").filter({ hasText: castMember("Manager").email })
      : table.locator("tbody tr").first();
    await row.focus();
    await page.keyboard.press("Enter");
    const inspector = page.getByRole("region", { name: / details$/ }).filter({ has: page.getByRole("heading") });
    await page.evaluate(() => document.fonts.ready);
    for (const theme of ["light", "dark"] as const) {
      await test.step(theme, async () => {
        await page.emulateMedia({ colorScheme: theme });
        await page.evaluate((value) => {
          document.documentElement.dataset.theme = value;
          delete document.documentElement.dataset.brand;
          window.scrollTo(0, 0);
        }, theme);
        await expect(inspector.getByRole("heading")).toBeFocused();
        for (const [brand, palette] of Object.entries(darkPalettes)) {
          await page.evaluate((value) => { document.documentElement.dataset.brand = value; }, brand);
          const styles = await inspector.getByRole("heading").evaluate((heading) => {
            const text = getComputedStyle(heading);
            const header = getComputedStyle(heading.parentElement!);
            const canvas = document.createElement("canvas");
            canvas.width = canvas.height = 1;
            const context = canvas.getContext("2d")!;
            const rgb = (color: string) => {
              context.fillStyle = color;
              context.fillRect(0, 0, 1, 1);
              return [...context.getImageData(0, 0, 1, 1).data].slice(0, 3);
            };
            const luminance = (color: string) => {
              const channels = rgb(color).map((value) => {
                const channel = value / 255;
                return channel <= .04045 ? channel / 12.92 : ((channel + .055) / 1.055) ** 2.4;
              });
              return .2126 * channels[0]! + .7152 * channels[1]! + .0722 * channels[2]!;
            };
            const contrast = (a: string, b: string) => {
              const [first, second] = [luminance(a), luminance(b)];
              return (Math.max(first, second) + .05) / (Math.min(first, second) + .05);
            };
            const surface = (element: Element): string => {
              const color = getComputedStyle(element).backgroundColor;
              return color === "rgba(0, 0, 0, 0)" ? surface(element.parentElement!) : color;
            };
            const selectedRow = document.querySelector('tr[aria-selected="true"]')!;
            const selected = getComputedStyle(selectedRow);
            const accent = selected.boxShadow.match(/rgba?\([^)]+\)/)![0];
            const panel = surface(heading.parentElement!.parentElement!);
            const inspector = heading.closest("aside")!;
            const dock = inspector.parentElement!;
            const paneBounds = dock.parentElement!.getBoundingClientRect();
            const dockBounds = dock.getBoundingClientRect();
            const bandBounds = heading.parentElement!.getBoundingClientRect();
            const textBounds = heading.getBoundingClientRect();
            const field = getComputedStyle(inspector.querySelector("dl > div")!);
            return {
              color: rgb(text.color), background: rgb(header.backgroundColor),
              row: rgb(selected.backgroundColor), accent: rgb(accent),
              accentWidth: selected.boxShadow.replace(/rgba?\([^)]+\)/, "").trim(),
              panel: rgb(panel), fieldRule: rgb(field.borderBottomColor),
              fieldRowHeight: inspector.querySelector("dl > div")!.getBoundingClientRect().height,
              frame: {
                borders: [header.borderTopWidth, header.borderRightWidth, header.borderBottomWidth, header.borderLeftWidth],
                outline: header.outlineStyle, nameOutline: text.outlineStyle, shadow: header.boxShadow,
                left: bandBounds.left - paneBounds.left, right: paneBounds.right - bandBounds.right,
                top: bandBounds.top - dockBounds.top + dock.scrollTop,
              },
              textCenterOffset: (textBounds.top + textBounds.bottom - bandBounds.top - bandBounds.bottom) / 2,
              textOpacity: text.opacity, headerOpacity: header.opacity,
              contrast: contrast(text.color, header.backgroundColor),
              accentRowContrast: contrast(accent, selected.backgroundColor),
              accentSurfaceContrast: contrast(accent, surface(selectedRow.closest("table")!)),
              bandPanelContrast: contrast(header.backgroundColor, panel),
              fieldRuleContrast: contrast(field.borderBottomColor, panel),
              height: heading.parentElement!.getBoundingClientRect().height,
              children: heading.parentElement!.children.length,
              paddingTop: header.paddingTop, paddingBottom: header.paddingBottom,
            };
          });
          console.log(JSON.stringify({ route, theme, brand, ...styles }));
          expect.soft(styles.fieldRowHeight, `${brand} main field row height`).toBeCloseTo(37.78125, 4);
          expect.soft(styles.accentRowContrast, `${brand} selection against row`).toBeGreaterThanOrEqual(3);
          expect.soft(styles.accentSurfaceContrast, `${brand} selection against surface`).toBeGreaterThanOrEqual(3);
          expect.soft(styles.frame, `${brand} uninterrupted edge-to-edge band`).toEqual({
            borders: ["0px", "0px", "0px", "0px"], outline: "none", nameOutline: "none", shadow: "none",
            left: 0, right: 0, top: 0,
          });
          expect.soft(styles.textCenterOffset, `${brand} vertically centered name`).toBe(0);
          expect.soft(styles.accentWidth, brand).toBe(`${theme === "dark" ? 8 : 3}px 0px 0px 0px inset`);
          expect.soft(styles.panel, brand).toEqual(theme === "dark" ? [48, 39, 51] : [255, 253, 249]);
          if (theme === "dark") {
            expect.soft(styles.row, brand).toEqual(palette.row);
            expect.soft(styles.accent, brand).toEqual(palette.accent);
            expect.soft(styles.fieldRule, brand).toEqual([179, 168, 184]);
            expect.soft(styles.bandPanelContrast, `${brand} header band against panel`).toBeGreaterThanOrEqual(3);
            expect.soft(styles.fieldRuleContrast, `${brand} field rules against panel`).toBeGreaterThanOrEqual(3);
          }
          expect.soft(styles.color, brand).toEqual(theme === "dark" ? [35, 29, 37] : [255, 255, 255]);
          expect.soft(styles.background, brand).toEqual(theme === "dark" ? palette.accent : [44, 36, 41]);
          expect.soft(styles.contrast, brand).toBeGreaterThanOrEqual(4.5);
          expect.soft(styles.textOpacity, brand).toBe("1");
          expect.soft(styles.headerOpacity, brand).toBe("1");
          expect.soft(styles.children, brand).toBe(1);
          expect.soft(styles.paddingTop, brand).toBe("7.5px");
          expect.soft(styles.paddingBottom, brand).toBe("7.5px");
          expect.soft(styles.height, brand).toBeGreaterThanOrEqual(36);
          expect.soft(styles.height, brand).toBeLessThanOrEqual(37);
        }
        const footer = inspector.locator(":scope > div").last();
        await expect(footer).toHaveCSS("border-top-width", "1px");
        await expect(footer).toHaveCSS("background-color", theme === "dark" ? "rgb(48, 39, 51)" : "rgb(255, 253, 249)");
        await expect(footer).toHaveCSS("border-top-color", theme === "dark" ? "rgb(179, 168, 184)" : "rgb(222, 211, 202)");
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
  });
}


test("inspector fields scroll independently with edge fades and main typography @phone", async ({ page, signIn }) => {
  await signIn(owner());
  for (const language of ["en", "tl"] as const) {
    await page.goto("/account");
    const languageSelect = page.locator('select:has(option[value="en"]):has(option[value="es"])');
    await languageSelect.selectOption(language);
    await expect(languageSelect).toBeEnabled();
    for (const { route } of screens) {
      await page.setViewportSize({ width: 390, height: 844 });
      await page.goto(`/${route}`);
      const table = page.getByRole("table").first();
      const row = route === "users"
        ? table.getByRole("row").filter({ hasText: castMember("Manager").email })
        : table.locator("tbody tr").first();
      await row.focus();
      await page.keyboard.press("Enter");
      const inspector = page.locator("aside").filter({ has: page.getByRole("heading", { level: 3 }) });
      await page.evaluate(() => document.fonts.ready);
      const dock = inspector.locator("..");
      const header = inspector.getByRole("heading").locator("..");
      const lastAction = inspector.locator("button, a").last();
      const bounds = await dock.boundingBox();
      const actionBounds = await lastAction.boundingBox();
      expect.soft(actionBounds!.y + actionBounds!.height, `${language} ${route} fixed actions`).toBeLessThanOrEqual(bounds!.y + bounds!.height);
      expect.soft(await dock.evaluate(e => e.scrollTop), "panel never scrolls").toBe(0);
      const typography = await inspector.locator("dt, dd").evaluateAll(elements => elements.map(e => {
        const style = getComputedStyle(e);
        return { tag: e.tagName, size: style.fontSize, weight: style.fontWeight, lineHeight: style.lineHeight };
      }));
      for (const style of typography) {
        expect.soft(style).toEqual({ tag: style.tag, size: "16px", weight: style.tag === "DT" ? "400" : "650", lineHeight: "24px" });
      }
      const rows = await table.evaluate(element => {
        const container = element.parentElement!;
        container.scrollTop = 0;
        const box = container.getBoundingClientRect();
        const top = Math.max(box.top, element.querySelector("thead")!.getBoundingClientRect().bottom);
        const rows = [...element.querySelectorAll("tbody tr")].map(e => e.getBoundingClientRect());
        return { full: rows.filter(r => r.top >= top - 1 && r.bottom <= box.bottom + 1).length,
          includingPartial: rows.filter(r => r.bottom > top + 1 && r.top < box.bottom - 1).length };
      });
      console.log(JSON.stringify({ language, route, ...rows }));
      const details = inspector.getByRole("region", { name: t(language, "audit:detailsHeader"), exact: true });
      expect.soft(await details.count(), `${language} ${route} named scrolling details`).toBe(1);
      if (await details.count() === 0) continue;
      await expect(details).toBeVisible();
      const overflowing = await details.evaluate(e => e.scrollHeight > e.clientHeight);
      await expect(details).toHaveAttribute("tabindex", overflowing ? "0" : "-1");
      if (route === "products") expect(overflowing).toBe(true);
      if (overflowing) {
        await expect(details).toHaveCSS("background-image", /linear-gradient/);
        const topFade = await details.evaluate(e => getComputedStyle(e).backgroundImage);
        expect(topFade.startsWith("none,")).toBe(true);
        const headerBefore = await header.boundingBox();
        const actionBefore = await lastAction.boundingBox();
        await details.focus();
        await expect(details).toHaveCSS("outline-style", "solid");
        await page.keyboard.press("End");
        await expect.poll(() => details.evaluate(e => e.scrollHeight - e.clientHeight - e.scrollTop)).toBeLessThanOrEqual(1);
        await expect.poll(() => details.evaluate(e => getComputedStyle(e).backgroundImage.endsWith(", none"))).toBe(true);
        expect(await header.boundingBox()).toEqual(headerBefore);
        expect(await lastAction.boundingBox()).toEqual(actionBefore);
        expect(await dock.evaluate(e => e.scrollTop)).toBe(0);
        await page.keyboard.press("Home");
        await expect.poll(() => details.evaluate(e => e.scrollTop)).toBe(0);
        await expect.poll(() => details.evaluate(e => getComputedStyle(e).backgroundImage)).toBe(topFade);
      } else {
        await expect(details).toHaveCSS("background-image", "none, none");
      }
      const footer = inspector.locator(":scope > div").last();
      await expect(footer).toHaveCSS("border-top-width", "1px");
      await expect(footer).toHaveCSS("background-color", "rgb(255, 253, 249)");
      if (route === "products") {
        await page.setViewportSize({ width: 1280, height: 800 });
        if (language === "en") {
          await expect.poll(() => details.evaluate(e => e.scrollHeight - e.clientHeight)).toBe(0);
          await expect(details).toHaveAttribute("tabindex", "-1");
          await expect(details).toHaveCSS("background-image", "none, none");
        }
        const text = await inspector.locator("dt, dd").evaluateAll(elements => elements.map(e => {
          const style = getComputedStyle(e);
          return { tag: e.tagName, size: style.fontSize, weight: style.fontWeight, lineHeight: style.lineHeight };
        }));
        for (const style of text) expect(style).toEqual({ tag: style.tag, size: "12px", weight: style.tag === "DT" ? "400" : "650", lineHeight: "17.1429px" });
        await expect(inspector.locator("dl > div").first()).toHaveCSS("padding-top", "6.4px");
        await page.setViewportSize({ width: 390, height: 844 });
        await page.emulateMedia({ colorScheme: "dark" });
        await page.evaluate(() => { document.documentElement.dataset.theme = "dark"; });
        await expect.poll(() => details.evaluate(e => getComputedStyle(e).backgroundImage.startsWith("none,"))).toBe(true);
        await expect(details).toHaveCSS("background-image", /linear-gradient/);
        await details.evaluate(e => { e.scrollTop = e.scrollHeight; });
        await expect.poll(() => details.evaluate(e => getComputedStyle(e).backgroundImage.endsWith(", none"))).toBe(true);
        await expect(details).toHaveCSS("background-image", /linear-gradient/);
        await page.emulateMedia({ colorScheme: "light" });
        await page.evaluate(() => { document.documentElement.dataset.theme = "light"; });
      }
    }
  }
  await page.goto("/account");
  await page.locator('select:has(option[value="en"]):has(option[value="es"])').selectOption("en");
});
