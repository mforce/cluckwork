// The phone shell (#814) — the four guarantees that only exist below 900px.
//
// Runs ONLY in the `chromium-phone` project, at 390x844. The `@phone` tag is
// what routes it there, and it is a structured tag rather than a substring of a
// title: `test.describe("... @phone")` would put the marker in the reporter's
// output and in every failure message, and would break the moment somebody
// reworded the title. The project's `grep`/`grepInvert` pair is a partition, so
// a test that loses its tag does not fall through to a second project — it
// silently stops running at phone width and starts running at 1280, where the
// `phone` fixture refuses to exist.
//
// WHAT THIS FILE IS FOR, stated against what it is not. It is not a visual
// regression suite and it asserts no pixels. Every check here is a LAYOUT
// INVARIANT that a desktop run cannot see at all: which navigation chrome is on
// screen, whether a thumb can hit it, whether the sticky action bar and the
// fixed tab bar are fighting over the same 55 pixels, and whether any screen
// pushes the document wider than the frame.
//
// Owner persona throughout. Owner sees the widest nav model (every group,
// including the admin-only setup destinations), so it is the role whose sheet
// holds the most and whose tables are the widest — the hardest case for all
// four assertions rather than a representative one.

import { expect, test, type Locator } from "../src/fixtures";
import { owner } from "../src/cast";
import { tEn } from "../src/i18n";

/** Owner's four thumb tabs, in the order `tabEntries` picks them (nav.tsx TAB_PRIORITY). */
const OWNER_TABS = ["nav:dailyEntry", "nav:stock", "nav:sales", "nav:history"];

/**
 * The smallest target a thumb can reliably hit, on both axes (WCAG 2.2 AA,
 * 2.5.8 Target Size (Minimum)).
 *
 * Not a pin on today's geometry: the measured tabs are 78 x 54.4, so this has
 * ~34px of margin on the long axis and ~10px on the short one. A change that
 * trips it has shrunk the bar by a third, which is the change worth failing on.
 */
const MIN_TARGET_PX = 44;

/**
 * `boundingBox()` with the null case turned into a sentence.
 *
 * Playwright returns `null` for an element that is not rendered, and a spec
 * that reads `.height` off it dies with "cannot read property of null" — which
 * points at this file rather than at the tab bar having vanished.
 */
async function rectOf(locator: Locator, what: string) {
  const box = await locator.boundingBox();
  if (box === null) {
    throw new Error(`${what} has no bounding box, so it is not rendered at this width.`);
  }
  return box;
}

test.describe("Phone shell", { tag: "@phone" }, () => {
  test.beforeEach(async ({ signIn }) => {
    await signIn(owner());
  });

  test("the tab bar is the navigation at this width", async ({ page, phone }) => {
    // Carries a custom message, and that is not decoration. Without one,
    // Playwright's failure line is its locator — `getByRole('navigation',
    // { name: 'Sections' })` — so mutation-check.sh's EXPECT_MSG_FOR entry
    // would have to declare an ENGLISH label as the text the mutant must die
    // on, which is the one thing this suite refuses to hardcode. A message
    // names the guarantee instead, and survives a relabel and a translation.
    await expect(phone.tabbar, "there is no tab bar at phone width, so nothing can be navigated to")
      .toBeVisible();

    // The other half of the same guarantee, and the half that makes it a
    // guarantee at all. "The tab bar is visible" would pass with BOTH shells on
    // screen — which is the pre-BottomNav state, where the sidebar wrapped into
    // a top bar and ate ~298px of an 844px screen. The sidebar's `<aside>` is
    // the `complementary` landmark, matched with no accessible name for the
    // same reason signIn used to: naming it would tie this to English.
    await expect(page.getByRole("complementary")).toBeHidden();

    // FIVE controls: four thumb tabs plus More. Counted as 4 links + 1 button
    // rather than as one total, because the split is the claim — More is a
    // <button> that opens a dialog, and a regression that turned it into a
    // sixth link would keep any total-of-five check green while the sheet
    // stopped existing.
    await expect(phone.tabbar.getByRole("link")).toHaveCount(4);
    await expect(phone.tabbar.getByRole("button")).toHaveCount(1);

    // Every control is thumb-sized, named one by one so a failure says WHICH.
    // Measured: each tab is 78 x 54.390625 at this viewport.
    for (const key of OWNER_TABS) {
      const box = await rectOf(phone.tab(key), `the ${tEn(key as `nav:${string}`)} tab`);
      expect.soft(box.width, `the ${tEn(key as `nav:${string}`)} tab is too narrow to hit`)
        .toBeGreaterThanOrEqual(MIN_TARGET_PX);
      expect.soft(box.height, `the ${tEn(key as `nav:${string}`)} tab is too short to hit`)
        .toBeGreaterThanOrEqual(MIN_TARGET_PX);
    }
    const moreBox = await rectOf(phone.more, "the More button");
    expect.soft(moreBox.width, "the More button is too narrow to hit").toBeGreaterThanOrEqual(MIN_TARGET_PX);
    expect.soft(moreBox.height, "the More button is too short to hit").toBeGreaterThanOrEqual(MIN_TARGET_PX);
  });

  test("a destination that is not a tab is reachable only through More", async ({ page, phone }) => {
    // The control for the test's own name: Customers is genuinely NOT one of
    // Owner's four tabs, so reaching it through the sheet is the only route.
    // Without this the test would still pass if Customers were promoted to a
    // tab and the sheet quietly stopped mattering.
    await expect(phone.tab("nav:customers")).toHaveCount(0);

    const sheet = await phone.openMore();
    await expect(sheet.link("nav:customers")).toBeVisible();

    // Sign out lives in the sheet foot and nowhere else at this width — the
    // sidebar foot that holds it on desktop is not rendered. If the sheet did
    // not carry it, a phone user could not sign out at all.
    await expect(sheet.signOut).toBeVisible();

    await sheet.link("nav:customers").click();
    await expect(page).toHaveURL(/\/customers$/);

    // BottomNav closes the sheet on a link click, and that is not cosmetic: the
    // sheet is a modal Dialog with a backdrop, so a sheet left open turns every
    // later tap on the destination it just navigated to into a click through
    // that backdrop — which dismisses the sheet instead of pressing anything.
    // The screen would look navigated and be inert.
    await expect(sheet.dialog).toBeHidden();
  });

  test("the daily-entry action bar stays clear of the tab bar", async ({ page, phone }) => {
    await page.goto("/daily-entry");

    const foot = page.locator(".entry-foot");
    await expect(foot).toBeVisible();

    // `.entry-foot` is the sticky element, NOT `.entry-foot .actions` — that
    // inner row is deliberately `position: static` (web/src/styles.css), so
    // measuring it would measure something the CSS never parks anywhere.
    const footBox = await rectOf(foot, "the daily-entry action bar");
    const barBox = await rectOf(phone.tabbar, "the tab bar");

    // MEASURED MARGIN, and it is 2.2px: the action bar's bottom edge sits at
    // 786.40625 and the tab bar's top edge at 788.609375. So this assertion is
    // tight by construction rather than by choice — there is no slack to pick.
    // That is the whole point of the rule it guards: `.entry-foot` parks at
    // `bottom: var(--tabbar-h)` and `.content` reserves exactly the same token
    // as bottom padding, so the two are designed to meet, not to overlap. Any
    // change that drops either half puts the Submit button under the tab bar,
    // where a thumb hits Sections instead.
    expect(
      footBox.y + footBox.height,
      "the daily-entry action bar overlaps the tab bar — its Submit and Save buttons are under it",
    ).toBeLessThanOrEqual(barBox.y);
  });

  test("no action control is taller than it is wide", async ({ page }) => {
    // THE #740 SHAPE, which is the defect this whole issue was opened over. A
    // label too long for its column wraps, the pill grows downwards, and
    // `border-radius: 999px` clamps into an ellipse with the text outside its
    // own background. A control taller than it is wide is that state, and it is
    // the one geometry a 1280 run can never reach — at desktop these rows are
    // ~974px and every label stays on one line.
    //
    // MEASURED at 390: both buttons are 170.6 x 65.2, a ratio of 2.62, so this
    // has 1.62 of margin and is not a pin on today's rendering. Under
    // `phone-action-label-wrapped` they become 170.6 x 217.2, a ratio of 0.79.
    // That mutant applies identically at 1280 and leaves the whole desktop
    // suite green — checked by `MUST_STAY_GREEN_ON`, not asserted here —
    // because the row is ~974px there, so a longer label grows SIDEWAYS.
    await page.goto("/daily-entry");
    const foot = page.locator(".entry-foot");
    await expect(foot).toBeVisible();

    const buttons = foot.getByRole("button");
    // Non-vacuity: a walk over an empty set passes for free, and this bar is the
    // only thing being walked. Two saves, always.
    await expect(buttons).toHaveCount(2);

    const measured = await buttons.evaluateAll((els) =>
      els.map((el) => {
        const r = el.getBoundingClientRect();
        return { name: (el.textContent ?? "").trim(), width: r.width, height: r.height };
      }));

    for (const b of measured) {
      // Soft, so one run names both buttons rather than stopping at the first —
      // they share a flex row, so whatever reshapes one reshapes the other.
      expect.soft(
        b.width,
        `"${b.name}" is ${b.width.toFixed(1)}x${b.height.toFixed(1)} at phone width — `
          + "taller than it is wide, so its pill clamps into an ellipse and the label leaves its background",
      ).toBeGreaterThanOrEqual(b.height);
    }
  });

  // NOT WALKED HERE, and not because it is clean: the Sales draft-order panel
  // fails this today. Measured at 390 in ENGLISH, on a draft order —
  //     Confirm order (allocates stock)   91.5 x 103.2   ratio 0.89   radius 999px
  //     Cancel draft                      89.8 x 103.2   ratio 0.87
  //     Close                             89.9 x 103.2   ratio 0.87
  // — against 285.7 x 40.1 for the first of those at 1280. That is #740, which
  // is filed as an es/tl defect and reproduces in en at this width; #674 owns
  // the remedy because it is a decision about how action buttons lay out on a
  // phone, not a fix this gate should make. Extend the walk to `/sales` when
  // #740 lands, and delete this comment with it.

  test("no walked screen overflows the viewport horizontally", async ({ page }) => {
    const viewport = page.viewportSize();
    if (viewport === null) throw new Error("this project runs with a fixed viewport; none was set.");

    // THIS LIST ASSERTS, IT DOES NOT DISCOVER. It is six routes somebody
    // measured, not every route the app has, so adding a screen does not get it
    // covered here — measure the new route at 390 first, then add it. A route
    // added blind would either pass for free or arrive red, and neither
    // outcome tells you anything about the route.
    //
    // `/` IS DELIBERATELY EXCLUDED, and not because it is clean. It measures
    // 443/390 at this width today, traced to a `<span class="num">` holding
    // `$9,999,999.99` inside `ul.dash-list > li` — a row named
    // "E2E Worker Sale …", which is RESIDUE THIS SUITE WROTE
    // (worker-sale-allocation.spec.ts, #612). So the dashboard's overflow is
    // run-order dependent: it depends on whether that spec has run yet against
    // this fixture. An assertion whose verdict changes with spec order is a
    // flake with a good reason, which is still a flake. The underlying layout
    // weakness — a long money string in a dash-list row having no way to wrap
    // or truncate — is real and is tracked separately as #816.
    const ROUTES = ["/sales", "/daily-entry", "/customers", "/flocks", "/stock", "/history"];

    for (const route of ROUTES) {
      await page.goto(route);
      await expect(page.locator("main#main-content")).toBeVisible();
      // The shell mounting is not enough: the widest thing on most of these
      // screens is a data table that arrives with its fetch, so measuring on
      // paint would measure an empty page and pass for free. `networkidle` is
      // discouraged for element assertions and is the right tool here, because
      // the thing being waited for genuinely is "the reads that populate this
      // screen have finished".
      await page.waitForLoadState("networkidle");

      const measured = await page.evaluate(() => ({
        scrollWidth: document.documentElement.scrollWidth,
        innerWidth: window.innerWidth,
      }));

      // SOFT, so one run names EVERY offending route instead of stopping at the
      // first. That is not a preference: the containment these assert is
      // per-element, so a regression typically hits some screens and not
      // others — measured under the table-overflow mutant, /sales, /history,
      // /flocks and /customers all overflow while /daily-entry and /stock stay
      // exactly 390. A hard assertion would report one quarter of that and
      // send the reader to fix one screen.
      //
      // Both messages name the route, and the mutation harness greps for them.
      // They are load-bearing text, not decoration.
      expect.soft(
        measured.scrollWidth,
        `${route} scrolls sideways at phone width — the document is wider than the ${viewport.width}px frame`,
      ).toBeLessThanOrEqual(viewport.width);

      // The second half of #441's defect, and a different failure from the
      // first: a wide element can inflate the LAYOUT viewport itself, which is
      // what anchors BottomNav's `position: fixed`. When that happens the tab
      // bar renders below the visible screen while the document looks fine.
      expect.soft(
        measured.innerWidth,
        `${route} inflated the layout viewport at phone width — innerWidth left the ${viewport.width}px frame`,
      ).toBe(viewport.width);
    }
  });
});
