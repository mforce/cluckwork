// #1161 — the expanded Lay rate chart stretches a range that fits and scrolls
// one that does not. These are layout questions jsdom cannot answer: where a
// stretched date cell lands, where the readout sits after the slots move, and
// whether 365 marks still have a width in a phone's overview map.
import { expect, test } from "../src/fixtures";
import { readmeFarmOwner } from "../src/cast";
import { tEn } from "../src/i18n";
import { daysBefore, farmToday } from "../src/farm";
import type { Locator, Page } from "@playwright/test";

const open = async (page: Page) => {
  await page.getByRole("button", { name: tEn("dashboard:expandAriaLabel") }).click();
  const chart = page.getByRole("dialog");
  await expect(chart.locator(".bigstrip .day").first()).toBeVisible();
  return chart;
};

const chooseDays = async (chart: Locator, timeZoneId: string, days: number) => {
  const latest = daysBefore(farmToday(timeZoneId), 1);
  await chart.getByLabel(tEn("dashboard:rangeLabel"), { exact: true }).selectOption("custom");
  await chart.getByLabel(tEn("dashboard:rangeFromLabel")).fill(daysBefore(latest, days - 1));
  await chart.getByLabel(tEn("dashboard:rangeToLabel")).fill(latest);
  await chart.getByRole("button", { name: tEn("dashboard:rangeApply") }).click();
  await expect(chart.locator(".bigstrip .day")).toHaveCount(days);
};

// The largest distance between a date cell's centre and its own bar's.
const dateDrift = (chart: Locator) => chart.locator(".lay-expand-scroll").evaluate((region) => {
  const centre = (el: Element) => { const r = el.getBoundingClientRect(); return r.left + r.width / 2; };
  const days = [...region.querySelectorAll(".bigstrip > .day")];
  const cells = [...region.querySelectorAll(".datebar > span")];
  return Math.max(...days.map((day, i) => Math.abs(centre(day) - centre(cells[i]!))));
});

const readoutDrift = (chart: Locator) => chart.evaluate((root) => {
  const centre = (el: Element) => { const r = el.getBoundingClientRect(); return r.left + r.width / 2; };
  return Math.abs(centre(root.querySelector(".tipdock .tip")!) - centre(root.querySelector(".bigstrip > .day.on")!));
});

test.describe("Expanded Lay rate chart fit (#1161)", () => {
  test("keeps each date under its own bar when the range stretches", async ({ page, signIn, farm }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await signIn(readmeFarmOwner());
    await page.goto("/");
    const chart = await open(page);
    const region = chart.locator(".lay-expand-scroll");

    await expect(region).toHaveClass(/is-fit/);
    expect(await dateDrift(chart), "30 days").toBeLessThan(0.5);

    // 41 x 22 + 40 x 4 = 1,062px against a 1,076px region: it just fits.
    await chooseDays(chart, farm.timeZoneId, 41);
    await expect(region).toHaveClass(/is-fit/);
    expect(await dateDrift(chart), "41 days").toBeLessThan(0.5);
  });

  test("moves the readout with its day across the fit threshold, both ways", async ({ page, signIn }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await signIn(readmeFarmOwner());
    await page.goto("/");
    const chart = await open(page);
    const region = chart.locator(".lay-expand-scroll");
    await expect(region).toHaveClass(/is-fit/);

    // Focus keeps the day selected once the pointer leaves the strip.
    await chart.locator(".bigstrip > .day").nth(15).click();
    await page.mouse.move(4, 4);
    expect(await readoutDrift(chart), "stretched").toBeLessThan(1);

    await page.setViewportSize({ width: 760, height: 800 });
    await expect(region).not.toHaveClass(/is-fit/);
    expect(await readoutDrift(chart), "narrowed to fixed slots").toBeLessThan(1);

    await page.setViewportSize({ width: 1280, height: 800 });
    await expect(region).toHaveClass(/is-fit/);
    expect(await readoutDrift(chart), "widened back").toBeLessThan(1);
  });
});

test.describe("Expanded Lay rate chart at phone width (#1161)", { tag: "@phone" }, () => {
  test("draws every day of a year in the overview map, and jumps where it is tapped", async ({ page, signIn, farm }) => {
    await signIn(readmeFarmOwner());
    await page.goto("/");
    const chart = await open(page);
    await chooseDays(chart, farm.timeZoneId, 365);

    const map = chart.locator(".daymap");
    const marks = await map.evaluate((el) => {
      const box = el.getBoundingClientRect();
      const rects = [...el.querySelectorAll("i")].map((i) => i.getBoundingClientRect());
      return { count: rects.length, narrowest: Math.min(...rects.map((r) => r.width)),
        overhang: Math.max(...rects.map((r) => r.right)) - box.right };
    });
    expect(marks.count).toBe(365);
    expect(marks.narrowest).toBeGreaterThan(0);
    expect(marks.overhang).toBeLessThanOrEqual(0.5);

    // A press at the middle of the map centres the window on the middle of the
    // range, and the box follows it there. A click, because this project does
    // not emulate touch; the map listens for pointerdown either way.
    const box = (await map.boundingBox())!;
    await map.click({ position: { x: box.width / 2, y: box.height / 2 } });
    await expect.poll(() => chart.evaluate((root) => {
      const r = root.querySelector(".lay-expand-scroll")!;
      return (r.scrollLeft + r.clientWidth / 2) / r.scrollWidth;
    })).toBeCloseTo(0.5, 1);
    const windowBox = (await chart.locator(".window-box").boundingBox())!;
    expect(Math.abs(windowBox.x + windowBox.width / 2 - (box.x + box.width / 2))).toBeLessThan(2);
    expect(windowBox.x).toBeGreaterThanOrEqual(box.x - 3);
    expect(windowBox.x + windowBox.width).toBeLessThanOrEqual(box.x + box.width + 3);
  });
});
