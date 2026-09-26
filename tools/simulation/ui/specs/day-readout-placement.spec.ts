// #962 — the Lay rate day readout stays inside its reserved row, whatever
// sequence of focus, hover and pointer-leave got it there.
//
// WHY A BROWSER, and not `DayStrip.test.tsx`. Every offset in jsdom is 0, so
// the unit suite cannot tell a box inside its row from one hanging 33px out of
// it — and the defect was a LAYOUT feedback loop between the readout's width
// and its placement, not a state bug. The loop itself is explained on `.tip` in
// web/src/styles.css; only a laid-out page can hold the result of it.
//
// WHAT THIS ASSERTS, and why it is not a pixel baseline. Three implementation-
// independent facts:
//
//   1. CONTAINMENT — the box's own rect sits inside the dock's rect at every
//      step. This is the user-visible symptom and it is true of any correct
//      implementation, so the spec survives a rewrite of the placement code.
//   2. RETURN — leaving the strip puts the box back exactly where focusing that
//      same day first put it. A placement that depends on the route taken to a
//      state is stale by definition, even on a step where the box happens to
//      still fit.
//   3. OVER ITS DAY — the box is centred on the selected day, stopped by
//      whichever end of the row it reaches first. 1 and 2 are BOTH satisfied by
//      a readout that never moves at all, which is what a `placeReadout` that
//      stopped running would leave: parked at the row's left edge it is inside
//      the row at every step and identical either side of the hover. Only this
//      third fact fails then, so it is what keeps the other two honest.
//
// It reads rects, never `style.left` or `style.transform`: the defect was fixed
// by moving from one to the other, and a spec naming either would have had to
// change with the fix it exists to hold.
import { expect, test, type Locator, type Page } from "../src/fixtures";
import { readmeFarmOwner } from "../src/cast";
import { LANGUAGES, t, type Language } from "../src/i18n";

// The readout's sentence is UI-language text, and tl runs roughly 30% longer
// than en (#688) — the width dimension is the whole point, so all three ship
// languages run rather than a representative one.
//
// The language arrives by rewriting `/me`'s own answer, not by driving the
// Settings select: the select PERSISTS the choice for that user, and this suite
// shares one seeded database with every other spec, so a red run would leave
// the next one in Tagalog. i18n.spec.ts pays that cost deliberately because
// persistence is what it tests; here the language is only a way to change a
// string's width.
async function speak(page: Page, language: Language): Promise<void> {
  await page.route("**/api/v1/me", async (route) => {
    const response = await route.fetch();
    const body = await response.json();
    await route.fulfill({ response, json: { ...body, language } });
  });
}

interface Placement {
  /** The box's left edge in the dock's own coordinates, so it is comparable across steps. */
  offset: number;
  /**
   * Where the box's left edge belongs (fact 3). Derived from the rects of the
   * day carrying `.on` and of the dock, never from the hook's own
   * `offsetLeft`/`scrollLeft` arithmetic — a rect is already in viewport
   * coordinates, so one expression covers the expanded chart's scrolled,
   * gutter-inset strip too, and reimplementing the production formula would
   * assert only that the code equals itself.
   */
  expected: number;
  /** How far the box hangs out of the dock on each side. Negative means inside. */
  outLeft: number;
  outRight: number;
  width: number;
  dockHeight: number;
  text: string;
}

// Subpixel: a transform and a rect are both fractional, and a box flush with
// its row's edge lands a hundredth of a pixel either side of it. 0.5px is well
// below anything a reader can see and well above the rounding.
const SLACK_PX = 0.5;

// 1px, because the centre of a flex slot and the centre of the box each land on
// a fraction.
const ON_DAY_PX = 1;

async function placementIn(root: Locator): Promise<Placement> {
  await expect(root.locator(".tipdock .tip")).toBeVisible();
  return await root.evaluate((rootEl) => {
    const dockEl = rootEl.querySelector(".tipdock");
    if (!(dockEl instanceof HTMLElement)) throw new Error("no .tipdock in scope");
    const boxEl = dockEl.querySelector(".tip");
    const dayEl = rootEl.querySelector(".day.on");
    if (!(boxEl instanceof HTMLElement)) throw new Error("no .tip inside the dock");
    if (!(dayEl instanceof HTMLElement)) throw new Error("no selected day in scope");
    const d = dockEl.getBoundingClientRect();
    const b = boxEl.getBoundingClientRect();
    const day = dayEl.getBoundingClientRect();
    const centre = day.left + day.width / 2 - d.left;
    return {
      offset: b.left - d.left,
      expected: Math.min(Math.max(centre - b.width / 2, 0), Math.max(0, d.width - b.width)),
      outLeft: d.left - b.left,
      outRight: b.right - d.right,
      width: b.width,
      dockHeight: d.height,
      text: boxEl.textContent ?? "",
    };
  });
}

function expectOverItsDay(p: Placement, step: string): void {
  expect(p.width, `${step}: the readout rendered with no width`).toBeGreaterThan(0);
  expect(p.outLeft, `${step}: the readout hangs ${p.outLeft.toFixed(1)}px past the LEFT of its row ("${p.text}", ${p.width.toFixed(1)}px wide)`)
    .toBeLessThanOrEqual(SLACK_PX);
  expect(p.outRight, `${step}: the readout hangs ${p.outRight.toFixed(1)}px past the RIGHT of its row ("${p.text}", ${p.width.toFixed(1)}px wide)`)
    .toBeLessThanOrEqual(SLACK_PX);
  expect(Math.abs(p.offset - p.expected), `${step}: the readout sits at ${p.offset.toFixed(1)}px, but centred on the selected day and clamped to the row it belongs at ${p.expected.toFixed(1)}px ("${p.text}", ${p.width.toFixed(1)}px wide)`)
    .toBeLessThanOrEqual(ON_DAY_PX);
}

/**
 * Focus one day, hover another, take the pointer off the strip, and check the
 * readout at every step.
 *
 * `hover` is given as an index so both extremes can be driven: the last day is
 * where the old code clamped hardest (and so shrank the wrapping box most), and
 * the first day is the mirror of it.
 */
async function runSequence(
  page: Page,
  scope: Page | Locator,
  slotSelector: string,
  focusAt: number,
  hoverAt: number,
  where: string,
): Promise<void> {
  // The dock and the strip are SIBLINGS, so the element every measurement runs
  // against has to contain both. On the card that is the document; in the
  // expanded chart it is the dialog, which also keeps the card's own dock
  // behind it out of the query.
  const root = "goto" in scope ? scope.locator("body") : scope;
  const slots = root.locator(slotSelector);
  const count = await slots.count();
  expect(count, `${where}: the strip drew no days`).toBeGreaterThan(3);
  // Negative indexes count back from the last day, the way `Array.prototype.at`
  // does — the last day is the interesting one and the strip's length is a
  // fixture detail no caller should have to know.
  const from = (i: number) => (i < 0 ? count + i : i);
  const focusIndex = from(focusAt);
  const hoverIndex = from(hoverAt);

  await slots.nth(focusIndex).focus();
  const parked = await placementIn(root);
  expectOverItsDay(parked, `${where} focus day ${focusIndex}`);

  await slots.nth(hoverIndex).hover();
  await expect(slots.nth(hoverIndex)).toHaveClass(/\bon\b/);
  const hovered = await placementIn(root);
  expectOverItsDay(hovered, `${where} hover day ${hoverIndex}`);

  // Off the strip entirely. The selection hands back to the keyboard, which
  // still holds the focused day (#941), so the box returns to where it parked.
  await page.mouse.move(2, 2);
  await expect(slots.nth(focusIndex)).toHaveClass(/\bon\b/);
  const restored = await placementIn(root);
  expectOverItsDay(restored, `${where} back on day ${focusIndex}`);

  expect(restored.offset, `${where}: the readout came back to ${restored.offset.toFixed(1)}px instead of the ${parked.offset.toFixed(1)}px focusing day ${focusIndex} first gave it`)
    .toBeCloseTo(parked.offset, 1);
  expect(restored.width, `${where}: the readout came back a different width`).toBeCloseTo(parked.width, 1);
  // The row is reserved so the box appearing reflows nothing; a fix that made
  // the box taller would push Avg/Peak around instead of overflowing sideways.
  expect(restored.dockHeight, `${where}: the reserved row changed height`).toBeCloseTo(parked.dockHeight, 1);
}

test.describe("Lay rate day readout placement (#962)", () => {
  for (const language of LANGUAGES) {
    test(`keeps the card's readout in its row through focus, hover and leave (${language})`, async ({ page, signIn }) => {
      await speak(page, language);
      await signIn(readmeFarmOwner());
      await page.goto("/");
      await expect(page.locator(".daystrip")).toBeVisible();

      await runSequence(page, page, ".daystrip > .day", 1, -1, `card/${language} last-day hover`);
      await runSequence(page, page, ".daystrip > .day", -2, 0, `card/${language} first-day hover`);
    });
  }

  test(`keeps the expanded chart's readout in its row through focus, hover and leave`, async ({ page, signIn }) => {
    // tl, the longest of the three catalogs (#688) and so the width the
    // expanded chart's wider dock is least likely to absorb on its own.
    const language: Language = "tl";
    await speak(page, language);
    await signIn(readmeFarmOwner());
    await page.goto("/");
    await expect(page.locator(".daystrip")).toBeVisible();

    // The label resolves through THIS test's language, not English: `speak`
    // just put the whole shell in Tagalog, and `tEn` would wait 45 seconds for
    // a button that is on screen under another name.
    await page.getByRole("button", { name: t(language, "dashboard:expandAriaLabel") }).click();
    const chart = page.getByRole("dialog");
    await expect(chart.locator(".bigstrip .day").first()).toBeVisible();

    // The expanded strip scrolls, so its dock spans the axis gutter as well and
    // a slot's own offset is not yet a dock coordinate. Both ends of the
    // VISIBLE window are driven; the readout has to stay in the row at each.
    await runSequence(page, chart, ".bigstrip > .day", 0, -1, "expanded/tl last-day hover");
    await runSequence(page, chart, ".bigstrip > .day", 6, 0, "expanded/tl first-day hover");
  });

  // One test per language rather than a loop inside one: `signIn` drives the
  // real login FORM, so a second pass inside a signed-in page waits 45 seconds
  // for a field that is no longer on screen.
  for (const language of LANGUAGES) {
    test(`keeps the card's readout in its row on a phone (${language}) @phone`, async ({ page, signIn }) => {
      await speak(page, language);
      await signIn(readmeFarmOwner());
      await page.goto("/");
      await expect(page.locator(".daystrip")).toBeVisible();

      await runSequence(page, page, ".daystrip > .day", 1, -1, `phone/${language} last-day hover`);
      await runSequence(page, page, ".daystrip > .day", -2, 0, `phone/${language} first-day hover`);
    });
  }
});
