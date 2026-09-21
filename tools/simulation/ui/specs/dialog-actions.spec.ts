import { test, expect } from "../src/fixtures";
import { owner } from "../src/cast";
import { tEn } from "../src/i18n";

for (const width of [1280, 390]) {
  test(`Expenses correction keeps Save reachable ${width === 390 ? "@phone" : ""}`, async ({ page, signIn }) => {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
    await signIn(owner());
    await page.goto("/expenses");
    await page.getByRole("button", { name: tEn("expenses:correctButton"), exact: true }).first().click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByRole("option").filter({ hasText: "Sim House" }).first()).toBeVisible();
    const save = dialog.getByRole("button", { name: tEn("expenses:saveCorrectionButton"), exact: true });
    const initial = await save.boundingBox();
    expect(initial).not.toBeNull();
    for (const scrollToEnd of [false, true]) {
      if (scrollToEnd) await dialog.locator(".MuiDialogContent-root").evaluate(el => { el.scrollTop = el.scrollHeight; });
      expect(await save.evaluate(button => {
        const rect = button.getBoundingClientRect();
        const hit = document.elementFromPoint(rect.x + rect.width / 2, rect.y + rect.height / 2);
        return hit !== null && button.contains(hit);
      })).toBe(true);
      expect(await save.boundingBox()).toEqual(initial);
    }
    await save.click();
    await expect(dialog).not.toBeVisible();
    await expect(page.getByRole("alert")).toHaveCount(0);
  });
}
