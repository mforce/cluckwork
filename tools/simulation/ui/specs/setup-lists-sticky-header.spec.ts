import { test, expect } from "../src/fixtures";
import { owner } from "../src/cast";

// #908's bounded table region owns its own scroll, and the header must stay
// pinned above the rows. `position: sticky`'s containing block is whichever
// ancestor actually scrolls, not necessarily the one a class name suggests,
// so this walks up from the header to find it.
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
