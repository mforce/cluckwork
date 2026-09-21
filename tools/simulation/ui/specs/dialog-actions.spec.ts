import { test, expect } from "../src/fixtures";
import { owner } from "../src/cast";
import { tEn } from "../src/i18n";

for (const width of [1280, 390]) {
  test(`Expenses correction keeps Save reachable ${width === 390 ? "@phone" : ""}`, async ({ page, signIn }) => {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
    await signIn(owner());
    await page.goto("/expenses");
    await page.getByRole("row").filter({ hasText: "Sim Feeder Replacement Part" })
      .getByRole("button", { name: tEn("expenses:correctButton"), exact: true }).click();
    const dialog = page.getByRole("dialog");
    const flock = () => dialog.locator("input").and(dialog.getByLabel(tEn("expenses:flockOptionalLabel"), { exact: true }));
    await expect(flock()).toHaveValue("Sim House B");
    await expect(dialog.getByRole("listbox")).toHaveCount(0);
    await expect(flock()).toHaveAttribute("aria-expanded", "false");
    await flock().click();
    await dialog.getByRole("option", { name: "Sim House A", exact: true }).click();
    await expect(flock()).toHaveValue("Sim House A");
    await expect(dialog.getByRole("listbox")).toHaveCount(0);
    await flock().click();
    await dialog.getByRole("option", { name: "Sim House B", exact: true }).click();
    await expect(dialog.getByRole("listbox")).toHaveCount(0);
    await flock().click();
    await page.keyboard.press("Escape");
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole("listbox")).toHaveCount(0);
    await flock().click();
    await dialog.getByLabel(tEn("expenses:descriptionLabel"), { exact: true }).click();
    await expect(dialog.getByRole("listbox")).toHaveCount(0);
    await expect(flock()).toHaveValue("Sim House B");
    await flock().click();
    await expect(dialog.getByRole("listbox")).toBeVisible();
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
