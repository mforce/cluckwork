import { test, expect } from "../src/fixtures";
import { owner } from "../src/cast";

// #939 codex review — the setup lists (#908) bound the table to a fixed-height
// region and dock the inspector below it; only the table's own scroll should
// move, with its header staying pinned above the rows. `position: sticky`'s
// containing block is whichever ancestor actually scrolls, and that need not
// be the element a class name suggests — walking up from the header itself
// to whichever ancestor genuinely has scrollable overflow is what a real
// scroll wheel would hit, and is the only way to find the actual scroller
// regardless of which element that turns out to be. A screenshot at rest
// cannot catch a header stuck to the wrong one; only scrolling and
// re-measuring can.
test("Flocks' table header stays pinned while its own bounded region scrolls", async ({ page, signIn }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await signIn(owner());
  await page.goto("/flocks");

  const header = page.getByRole("columnheader").first();
  const before = await header.boundingBox();
  expect(before, "the header must be visible before scrolling").not.toBeNull();

  const scrolled = await header.evaluate((headerEl) => {
    let el: HTMLElement | null = headerEl.parentElement;
    while (el !== null) {
      if (el.scrollHeight > el.clientHeight) {
        const before = el.scrollTop;
        el.scrollTop = 200;
        if (el.scrollTop !== before) return true;
      }
      el = el.parentElement;
    }
    return false;
  });
  expect(scrolled, "some ancestor of the header must actually scroll").toBe(true);

  const after = await header.boundingBox();
  expect(after, "the header must still be visible after scrolling").not.toBeNull();
  expect(after!.y, "the header's top must not move once its region scrolls").toBeCloseTo(before!.y, 0);
});
