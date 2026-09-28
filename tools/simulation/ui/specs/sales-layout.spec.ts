import type { Page } from "@playwright/test";
import { test, expect } from "../src/fixtures";
import { owner } from "../src/cast";
import { tEn } from "../src/i18n";

// #987 — below 900px the orders table is one two-line button per order, and
// its `open` moved into the Details peek that button raises. Above it, the
// row's own `open` button is still there.
async function openOrder(page: Page, customer: string, status: string, phone: boolean) {
  if (!phone) {
    const row = page.getByRole("row").filter({ hasText: customer })
      .filter({ hasText: tEn(`enums:status.${status}` as never) }).first();
    await row.getByRole("button", { name: tEn("sales:open"), exact: true }).click();
    return;
  }
  const peek = page.getByRole("list", { name: tEn("sales:ordersHeading") }).getByRole("button")
    .filter({ hasText: customer }).filter({ hasText: tEn(`enums:status.${status}` as never) }).first();
  await peek.click();
  await page.getByRole("dialog").getByRole("button", { name: tEn("sales:open"), exact: true }).click();
}

test("Sales draft keeps a commercial row in the first desktop frame", async ({ page, signIn }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await signIn(owner());
  await page.goto("/sales");
  const draft = page.getByRole("row")
    .filter({ has: page.getByRole("cell", { name: "Sim Customer 1", exact: true }) })
    .filter({ hasText: tEn("enums:status.Draft") });
  await draft.getByRole("button", { name: tEn("sales:open"), exact: true }).click();
  await expect(page.getByRole("region")).toBeVisible();
  await page.evaluate(() => window.scrollTo(0, 0));
  const firstCommercialRow = page.getByRole("table").last().locator("tbody tr").first();
  const box = await firstCommercialRow.boundingBox();
  expect(box, "the commercial ledger must contain a row").not.toBeNull();
  expect(box!.y + box!.height, "the draft desk pushes the commercial record below the first frame").toBeLessThanOrEqual(800);
});

test("Sales uses Field Console status, links and settlement actions", async ({ page, signIn }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await signIn(owner());
  await page.goto("/sales");
  await expect(page.getByRole("button", { name: tEn("sales:newOrder"), exact: true })).toHaveCSS("border-radius", "4px");
  await expect(page.getByRole("button", { name: tEn("common:clearFiltersButton") })).toBeVisible();
  const draft = page.getByRole("row").filter({ hasText: "Sim Customer 1" }).filter({ hasText: tEn("enums:status.Draft") });
  const status = draft.getByText(tEn("enums:status.Draft"), { exact: true });
  expect(await status.evaluate(el => getComputedStyle(el, "::before").width)).toBe("6px");
  await expect(status).toHaveCSS("background-color", "rgba(0, 0, 0, 0)");
  const open = draft.getByRole("button", { name: tEn("sales:open"), exact: true });
  await expect(open).toHaveCSS("text-decoration-line", "underline");
  await open.click();
  const panel = page.getByRole("region");
  for (const label of ["sales:edit", "sales:remove"] as const) {
    await expect(panel.getByRole("button", { name: tEn(label), exact: true }).first()).toHaveCSS("text-decoration-line", "underline");
  }
  const cancel = panel.getByRole("button", { name: tEn("sales:cancelDraft"), exact: true });
  await expect(cancel).toHaveCSS("background-color", "rgb(255, 255, 255)");
  const actions = panel.getByRole("group", { name: tEn("sales:draftActions") });
  await expect(actions).toHaveCSS("justify-content", "flex-end");
  await expect(cancel).toHaveCSS("flex-grow", "0");
});

test("Sales phone manifest fits without hiding price editing", { tag: "@phone" }, async ({ page, signIn }) => {
  await signIn(owner());
  await page.goto("/sales");
  await openOrder(page, "Sim Customer 1", "Draft", true);
  const manifest = page.getByRole("region").getByRole("table").first();
  await expect(manifest.getByRole("columnheader", { name: tEn("sales:unitPrice"), exact: true })).toBeHidden();
  await expect(manifest.getByRole("columnheader", { name: tEn("sales:lineTotal"), exact: true })).toBeHidden();
  expect(await manifest.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
  expect(await manifest.locator("tbody tr").evaluateAll(rows => rows.every(row =>
    Array.from(row.children).filter(cell => getComputedStyle(cell).display !== "none")
      .every(cell => cell.getBoundingClientRect().bottom <= row.getBoundingClientRect().bottom + 1),
  )), "manifest cells must remain inside their row").toBe(true);
  const edit = manifest.getByRole("button", { name: tEn("sales:edit"), exact: true }).first();
  const remove = manifest.getByRole("button", { name: tEn("sales:remove"), exact: true }).first();
  expect((await edit.boundingBox())!.y, "manifest edit/remove share one line").toBe((await remove.boundingBox())!.y);
  await manifest.getByRole("button", { name: tEn("sales:edit"), exact: true }).first().click();
  const price = manifest.getByRole("spinbutton", { name: tEn("sales:editUnitPriceAriaLabel"), exact: true });
  await expect(price).toBeVisible();
  await price.fill("3.00");
  expect(await manifest.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
});

test("Sales payment rail keeps destructive styling and opens the payment dialog", async ({ page, signIn }) => {
  await signIn(owner());
  await page.goto("/sales");
  const confirmed = page.getByRole("row").filter({ hasText: "Sim Customer 3" }).filter({ hasText: tEn("enums:status.Confirmed") }).first();
  await confirmed.getByRole("button", { name: tEn("sales:open"), exact: true }).click();
  const rail = page.getByRole("complementary", { name: tEn("sales:settlementHeading") });
  const destructive = rail.getByRole("button", { name: tEn("sales:voidOrderButton"), exact: true });
  await expect(destructive).toHaveCSS("background-color", "rgba(0, 0, 0, 0)");
  const heading = rail.getByRole("heading", { name: tEn("sales:payments"), exact: true });
  expect(await heading.evaluate(el => getComputedStyle(el).fontFamily)).toContain("Georgia");
  const cue = rail.getByText(tEn("common:swipeColumns"), { exact: true });
  await expect(cue).toHaveCSS("background-color", "rgb(67, 56, 64)");
  await rail.getByRole("button", { name: tEn("sales:recordPayment"), exact: true }).click();
  await expect(page.getByRole("dialog", { name: tEn("sales:recordPayment") })).toBeVisible();
});

test("Sales confirmed phone rows expose their hidden price columns", { tag: "@phone" }, async ({ page, signIn }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await signIn(owner());
  await page.goto("/sales");
  await openOrder(page, "Sim Customer 3", "Confirmed", true);
  const manifest = page.getByRole("region").getByRole("table").first();
  await expect(manifest.getByRole("columnheader", { name: tEn("sales:unitPrice"), exact: true })).toBeHidden();
  await expect(manifest.getByRole("columnheader", { name: tEn("sales:lineTotal"), exact: true })).toBeHidden();
  const row = manifest.getByRole("row", { name: /Sim Large Eggs/ });
  await expect(row).toHaveAccessibleName(/Unit price \$0\.45, Line total \$16\.20/);
  const prices = row.getByText("Unit price $0.45, Line total $16.20", { exact: true });
  await expect(prices).toHaveCSS("position", "absolute");
  await expect(prices).toHaveCSS("width", "1px");
  expect(await manifest.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
});

for (const width of [1280, 390]) {
  test(`Sales keeps incomplete prices visible and edit cells muted ${width === 390 ? "@phone" : ""}`, async ({ page, signIn }) => {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
    await signIn(owner());
    await page.goto("/sales");
    // #987 — line 2 has no room for the note, so on a phone it is the Details
    // peek that must still show it in full rather than the row.
    if (width === 390) {
      await page.getByRole("list", { name: tEn("sales:ordersHeading") }).getByRole("button")
        .filter({ hasText: "Sim Customer 2" }).filter({ hasText: tEn("enums:status.Draft") }).first().click();
    }
    const partial = width === 390
      ? page.getByRole("dialog")
      : page.getByRole("row").filter({ hasText: "Sim Customer 2" }).filter({ hasText: tEn("enums:status.Draft") });
    const note = partial.getByText(tEn("sales:discountPartialNote"), { exact: true });
    await note.scrollIntoViewIfNeeded();
    await expect(note).toBeVisible();
    await expect(note).toHaveCSS("clip-path", "none");
    expect((await note.boundingBox())!.width).toBeGreaterThan(20);
    if (width === 390) await page.getByRole("dialog").getByRole("button", { name: tEn("common:close") }).click();
    await openOrder(page, "Sim Customer 1", "Draft", width === 390);
    const row = page.getByRole("region").getByRole("row", { name: /Sim Medium Eggs/ });
    await row.getByRole("button", { name: tEn("sales:edit"), exact: true }).click();
    const muted = await page.evaluate(() => {
      const probe = document.createElement("span");
      probe.style.color = "var(--muted)";
      document.body.append(probe);
      const color = getComputedStyle(probe).color;
      probe.remove();
      return color;
    });
    for (const index of [2, 3]) await expect(row.getByRole("cell").nth(index)).toHaveCSS("color", muted);
    const size = await row.evaluate(element => {
      const table = element.closest("table")!;
      return { table: table.scrollWidth, panel: table.parentElement!.clientWidth };
    });
    expect(size.table, "inline editing must stay inside the manifest panel").toBeLessThanOrEqual(size.panel);
  });
}
