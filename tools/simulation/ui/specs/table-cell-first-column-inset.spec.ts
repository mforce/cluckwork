import { test, expect } from "../src/fixtures";
import { owner } from "../src/cast";

// A cell's own boundingBox includes padding a later, higher-specificity rule
// may have silently cancelled, so this combines the box with the LIVE
// computed padding rather than trusting the theme value in isolation.
async function contentEdges(header: import("@playwright/test").Locator) {
  const box = await header.boundingBox();
  if (box === null) throw new Error("header cell has no box");
  const padding = await header.evaluate((el) => {
    const cs = getComputedStyle(el);
    return { left: parseFloat(cs.paddingLeft), right: parseFloat(cs.paddingRight) };
  });
  return { left: box.x + padding.left, right: box.x + box.width - padding.right };
}

// FieldConsole-wrapped ledgers (History, Stock, Expenses, ...) already had
// their own smaller, symmetric 8px scale before this fix and are untouched by
// it; their floor here is only "clearly not flush", not the theme's own 1rem.
const THEME_SCALE_MIN_GAP = 12;
const FIELD_CONSOLE_SCALE_MIN_GAP = 6;

async function checkFirstColumnInset(page: import("@playwright/test").Page, route: string, minGap: number) {
  await page.goto(route);
  // The TABLE's own box, not its scrolling container: a table can be a few
  // pixels wider than its container (needing a little horizontal scroll)
  // independently of this padding fix, and comparing against the container
  // in that case would read a real, unrelated overflow as an asymmetric inset.
  const table = page.locator("table").first();
  const containerBox = await table.boundingBox();
  if (containerBox === null) throw new Error("table has no box");
  const headers = page.getByRole("columnheader");
  const count = await headers.count();
  expect(count, "at least two columns").toBeGreaterThanOrEqual(2);

  // The first column always carries a real label in every screen this spec
  // drives; the last column is whatever the screen declares (often a blank
  // Actions header over real row buttons) — the padding fix applies to it
  // either way, so it stays unfiltered rather than skipped for being empty.
  const first = await contentEdges(headers.first());
  const last = await contentEdges(headers.last());
  const leftGap = first.left - containerBox.x;
  const rightGap = (containerBox.x + containerBox.width) - last.right;
  expect(leftGap, "first column's text left gap").toBeGreaterThanOrEqual(minGap);
  expect(Math.abs(leftGap - rightGap), "left gap must match the right gap").toBeLessThanOrEqual(2);
}

test("Flocks' first column has a left inset matching the last column's right inset", async ({ page, signIn }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await signIn(owner());
  await checkFirstColumnInset(page, "/flocks", THEME_SCALE_MIN_GAP);
});

test("History's first column has a left inset matching the last column's right inset", async ({ page, signIn }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await signIn(owner());
  await checkFirstColumnInset(page, "/history", FIELD_CONSOLE_SCALE_MIN_GAP);
});
