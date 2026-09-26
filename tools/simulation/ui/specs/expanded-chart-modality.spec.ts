// #941, #958 review round 1 — what the expanded Lay rate chart claims about
// being modal, and the one place its arithmetic and its layout can disagree.
//
// Both belong in a browser. `inert`/`aria-hidden` containment is a focus
// question jsdom cannot answer (#501), and the 900px case is about which media
// query a real engine applied.
import { expect, test } from "../src/fixtures";
import { readmeFarmOwner } from "../src/cast";
import { tEn } from "../src/i18n";
import { daysBefore, farmToday } from "../src/farm";

const open = async (page: import("@playwright/test").Page) => {
  await page.getByRole("button", { name: tEn("dashboard:expandAriaLabel") }).click();
  const chart = page.getByRole("dialog");
  await expect(chart).toBeVisible();
  await expect(chart.locator(".bigstrip .day").first()).toBeVisible();
  return chart;
};

test.describe("Expanded Lay rate chart modality (#941)", () => {
  test("keeps Tab inside the chart even after a click on its backdrop", async ({ page, signIn }) => {
    await signIn(readmeFarmOwner());
    await page.goto("/");
    const chart = await open(page);

    // The backdrop's own top-left corner: outside the panel, inside the frame.
    // A click there used to leave focus on <body>, where the hand-rolled trap
    // could not see the next Tab and the sidebar was the browser's next stop.
    await page.mouse.click(8, 8);
    for (let press = 0; press < 12; press += 1) {
      await page.keyboard.press("Tab");
      const inside = await page.evaluate(() => {
        const dialog = document.querySelector('[role="dialog"]');
        return dialog !== null && dialog.contains(document.activeElement);
      });
      expect(inside, `focus left the chart on Tab ${press + 1}`).toBe(true);
    }
    await expect(chart).toBeVisible();
  });

  test("locks the page behind it, and gives the scroll back on close", async ({ page, signIn }) => {
    await signIn(readmeFarmOwner());
    await page.goto("/");
    await page.evaluate(() => window.scrollTo(0, 0));
    await open(page);

    expect(await page.evaluate(() => getComputedStyle(document.body).overflow)).toBe("hidden");
    // A real attempt to move the page behind the chart, not a style reading.
    // Measured as a CHANGE: opening the chart can leave the page a pixel or
    // two off where it was, and the claim is that the wheel moves nothing.
    const parked = await page.evaluate(() => window.scrollY);
    await page.mouse.move(8, 400);
    await page.mouse.wheel(0, 600);
    await page.waitForTimeout(300);
    expect(await page.evaluate(() => window.scrollY)).toBe(parked);

    await page.getByRole("button", { name: tEn("dashboard:expandClose") }).click();
    await expect(page.getByRole("dialog")).toBeHidden();
    expect(await page.evaluate(() => getComputedStyle(document.body).overflow)).not.toBe("hidden");
    await page.mouse.wheel(0, 600);
    await page.waitForTimeout(300);
    expect(await page.evaluate(() => window.scrollY)).toBeGreaterThan(parked);
  });

  test("agrees with its own layout at exactly 900px, where both media queries match", async ({ page, signIn, farm }) => {
    // `MD_UP_QUERY` is `(min-width: 900px)` and the stylesheet's phone block is
    // `(max-width: 900px)`. At exactly 900 the strip lays out on the phone's
    // 2px gap; a JS-side 4px made a 30-day range that fits read as clipped,
    // with a lit right cue and a live pager over a chart hiding nothing.
    await page.setViewportSize({ width: 900, height: 800 });
    await signIn(readmeFarmOwner());
    await page.goto("/");
    const chart = await open(page);

    // 32 days is inside the window the two answers disagree over: on the
    // laid-out 2px gap it is 32x22 + 31x2 = 766px and fits, on a 4px gap it
    // computes to 828px and does not.
    const latest = daysBefore(farmToday(farm.timeZoneId), 1);
    await chart.getByLabel(tEn("dashboard:rangeLabel"), { exact: true }).selectOption("custom");
    await chart.getByLabel(tEn("dashboard:rangeFromLabel")).fill(daysBefore(latest, 31));
    await chart.getByLabel(tEn("dashboard:rangeToLabel")).fill(latest);
    await chart.getByRole("button", { name: tEn("dashboard:rangeApply") }).click();
    await expect(chart.locator(".bigstrip .day")).toHaveCount(32);

    const measured = await chart.locator(".lay-expand-scroll").evaluate((region) => ({
      client: region.clientWidth, scroll: region.scrollWidth,
    }));
    expect(measured.scroll, "32 days lays out inside this region").toBeLessThanOrEqual(measured.client);

    await expect(chart.getByRole("button", { name: tEn("dashboard:expandPrev") })).toBeDisabled();
    await expect(chart.getByRole("button", { name: tEn("dashboard:expandNext") })).toBeDisabled();
    for (const side of ["left", "right"] as const) {
      await expect(chart.locator(`.scroll-cue.${side}`)).toBeHidden();
    }
  });
});
