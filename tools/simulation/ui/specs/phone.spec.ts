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

import { expect, test, type Locator, type Page } from "../src/fixtures";
import { owner } from "../src/cast";
import { tEn } from "../src/i18n";

/** Owner's four thumb tabs, in the order `tabEntries` picks them (nav.tsx TAB_PRIORITY). */
const OWNER_TABS = ["nav:dailyEntry", "nav:stock", "nav:sales", "nav:history"];

/**
 * The smallest target a thumb can reliably hit, on both axes.
 *
 * This is WCAG 2.2 **AAA** 2.5.5 Target Size (Enhanced). It is NOT the AA
 * criterion: 2.5.8 Target Size (Minimum) asks for 24px, with exceptions, and an
 * earlier version of this comment cited it for 44 — attributing the app's own,
 * stricter choice to a standard that does not require it. The floor is the
 * app's: `.tab` carries `min-height: 3.4rem` "clears the 44px touch target the
 * rest of the app now holds to" (web/src/styles.css).
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

/**
 * Every `.actions` row that exists at phone width, with the container each
 * button has to fill.
 *
 * It navigates, because the Sales row only exists while a draft order is open
 * — which is why #740 reproduced on a screen the first version of this walk
 * could not reach.
 */
async function phoneActionRows(page: Page) {
  await page.goto("/daily-entry");
  const foot = page.locator(".entry-foot");
  await expect(foot).toBeVisible();

  await page.goto("/sales");
  // The fixture seeds two draft orders and never confirms them
  // (SimulationDataSeeder), so this opens existing state instead of minting an
  // order and drawing stock out of the fixture on every phone run.
  const draft = page.getByRole("row").filter({ hasText: tEn("enums:status.Draft") }).first();
  await expect(draft, "the fixture has no draft order, so the #740 row cannot be measured")
    .toBeVisible();
  await draft.getByRole("button", { name: tEn("sales:open") }).click();

  const panel = page.locator(".order-panel .actions");
  await expect(panel).toBeVisible();

  return [
    { what: "the daily-entry save bar", row: foot.locator(".actions"), buttons: 2 },
    { what: "the Sales draft-order panel", row: panel, buttons: 3 },
  ];
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

    // AND IT HAS TO ACTUALLY NAVIGATE. Everything above measures the bar
    // without ever using it, and an adversarial read found the hole: a
    // phone-only `.tabbar a { pointer-events: none }` preserves visibility, the
    // 4+1 split and every bounding box, so all of it stays green while none of
    // the four tabs does anything. The test is called "the tab bar IS the
    // navigation", and until this line it never once navigated.
    //
    // `phone-tabs-inert` is the mutant for exactly that.
    //
    // HIT-TESTED FIRST, and not by clicking. `.click()` under
    // `pointer-events: none` does not fail — it waits for actionability until
    // the test times out, which the mutation harness correctly refuses to
    // count ("the spec failed, but NOT on an assertion"). Measured: the first
    // version of this line produced `locator.click: Test timeout of 45000ms
    // exceeded` and would have been reported INCONCLUSIVE.
    //
    // `elementFromPoint` at the tab's centre is the same question asked as a
    // measurement: does a tap there land on this tab? It fails as an ordinary
    // assertion, in milliseconds, and it is what a thumb actually does.
    const salesTab = phone.tab("nav:sales");
    const hit = await salesTab.evaluate((el) => {
      const r = el.getBoundingClientRect();
      const top = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
      return top === el || el.contains(top);
    });
    expect(
      hit,
      "a tap at the centre of the Sales tab does not land on it — the bar renders and measures "
        + "correctly but cannot be used",
    ).toBe(true);

    // Then actually navigate, because being hittable is not the same as being
    // wired to anything.
    await salesTab.click();
    await expect(
      page,
      "a thumb tab did not navigate — the bar renders and is hittable but goes nowhere",
    ).toHaveURL(/\/sales$/);
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
    // #823 closed it by stacking: below 900px `.actions` and
    // `.dialog .dialog-foot` lay out in a column and every button fills the
    // row, so no label can reshape the control. BOTH halves are asserted,
    // because the ratio alone stays green for a button that stacked and then
    // collapsed to its intrinsic width — which is the same defect one step on.
    //
    // MEASURED at 390 after #823 and recorded here, where this suite keeps its
    // measurements. Under `phone-action-label-wrapped` each row returns to side
    // by side and every button drops to a third or a half of its container.
    //
    // BOTH ROWS ARE WALKED, and the Sales one is why the walk exists: the
    // daily-entry bar passed the ratio check before #823 and the Sales draft
    // panel did not, so a walk that stopped at the bar asserted the one row
    // that was never broken.
    for (const { what, row, buttons } of await phoneActionRows(page)) {
      // Non-vacuity: a walk over an empty set passes for free.
      await expect(row.getByRole("button"), `${what} renders no buttons`).toHaveCount(buttons);

      const measured = await row.evaluate((el) => ({
        container: el.getBoundingClientRect().width,
        buttons: Array.from(el.querySelectorAll("button")).map((b) => {
          const r = b.getBoundingClientRect();
          return { name: (b.textContent ?? "").trim(), width: r.width, height: r.height };
        }),
      }));

      for (const b of measured.buttons) {
        const share = b.width / measured.container;
        // Soft, so one run names every button in the row rather than stopping
        // at the first — they share one flex container, so whatever reshapes
        // one reshapes them all.
        expect.soft(
          b.width,
          `"${b.name}" in ${what} is ${b.width.toFixed(1)}x${b.height.toFixed(1)} at phone width `
            + "— taller than it is wide, so its pill clamps into an ellipse and the label leaves "
            + "its own background",
        ).toBeGreaterThanOrEqual(b.height);

        // 90%, not 100%: a row can carry its own padding, and a floor that
        // pinned the exact width would go red on a gutter change that reshapes
        // nothing. Side by side lands a pair at ~47% and a trio at ~32%, so
        // this separates the two layouts rather than pinning today's pixels.
        expect.soft(
          share,
          `"${b.name}" in ${what} spans ${(100 * share).toFixed(0)}% of its row — the action row `
            + "is side by side again, which is what #740 was",
        ).toBeGreaterThanOrEqual(0.9);
      }
    }
  });

  test("no walked screen overflows the viewport horizontally", async ({ page }) => {
    const viewport = page.viewportSize();
    if (viewport === null) throw new Error("this project runs with a fixed viewport; none was set.");

    // THIS LIST ASSERTS, IT DOES NOT DISCOVER, and the selection rule is
    // stated so the omissions are a decision rather than a memory. Covered:
    // every route whose main content is a `table.data`, because a wide table is
    // what #441's containment holds in — plus `/`, which carries no table but
    // does carry the widest intrinsic content in the app, a money string in a
    // `max-content` grid track. Not covered, by that same rule:
    // `/reports` and `/expenses` (a filter bar over summary panels), `/feed`
    // and `/water` (a flock picker over a narrow ledger), `/users`, `/audit`
    // and `/settings`. If one of those grows a wide table it belongs here —
    // measure it at 390 first, then add it, because a route added blind either
    // passes for free or arrives red and neither result says anything about
    // the route.
    //
    // `/inventory` is the one judgement call: it renders a table and is left
    // out because its own movement ledger is reached through `/flocks`, which
    // IS walked. Add it if that stops being true.
    //
    // `/` IS WALKED, and the story of why it briefly was not is worth keeping.
    //
    // It measured 443/390 during this PR's first probe — a `<span class="num">`
    // holding `$9,999,999.99` inside `ul.dash-list > li`, pushed 53px off
    // screen. That was real, and it was STALE BYTES: the sim stack had been up
    // 28 hours, so it was serving an image built before #781
    // (`7193ebe fix(dashboard): give recent sales real columns…`, 2026-09-12),
    // which is the commit that gave this list its container-query narrow
    // layout and is seven commits behind this branch's base. Rebuilding the
    // stack made the dashboard measure 390/390 with those same rows present,
    // the list resolving to `grid-template-columns: 179.219px 112px` and the
    // widest amount sitting at x=346.6 inside a 353px panel.
    //
    // AGENTS.md says this in as many words — a long-running sim stack serves
    // the bytes it was built from, not the branch under review — and it is the
    // rule this PR's own description quotes. Rebuild before believing a
    // rendered measurement.
    // Each route names the content whose width is actually being judged, rather
    // than sharing one selector. A blanket `table.data` was tried and was wrong
    // on the first run: `/daily-entry` renders a grade-entry grid and a sticky
    // foot, no data table at all, so the precondition failed there while the
    // route is one of the more interesting ones to measure.
    const ROUTES: ReadonlyArray<{ path: string; content: string; what: string }> = [
      // The dashboard is first because it is the screen the phone context is
      // FOR, and its recent-sales list is the widest intrinsic content in the
      // app — a money string in a `max-content` track beside a name.
      { path: "/", content: "ul.dash-list", what: "the recent-sales list" },
      { path: "/sales", content: "table.data", what: "the orders table" },
      { path: "/daily-entry", content: ".entry-foot", what: "the entry form's sticky foot" },
      { path: "/customers", content: "table.data", what: "the customer book" },
      { path: "/flocks", content: "table.data", what: "the flock table" },
      { path: "/stock", content: "table.data", what: "the stock table" },
      { path: "/history", content: "table.data", what: "the entry history table" },
    ];

    for (const { path: route, content, what } of ROUTES) {
      await page.goto(route);

      // THE CONTENT HAS TO BE ON SCREEN BEFORE ITS WIDTH MEANS ANYTHING, and
      // this is the second hole an adversarial read found. `main#main-content`
      // proves the shell mounted, and `networkidle` proves the requests
      // settled — neither proves the table rendered. Both SalesPage and
      // CustomersPage answer a failed read with a small error section and NO
      // table (SalesPage.tsx, CustomersPage.tsx), which is a perfectly
      // 390px-wide screen. So every route on this walk could break, render an
      // error, measure exactly 390, and report clean.
      //
      // Asserting the table is present makes the measurement mean what the
      // test says it means: this screen's real content fits the frame.
      await expect(
        page.locator(content).first(),
        `${route} rendered no ${what} at phone width, so measuring its width proves nothing — `
          + "the screen is empty, erroring or still loading",
      ).toBeVisible();
      // The shell mounting is not enough: the widest thing on most of these
      // screens is a data table that arrives with its fetch, so measuring on
      // paint would measure an empty page and pass for free. `networkidle` is
      // discouraged for element assertions and is the right tool here, because
      // the thing being waited for genuinely is "the reads that populate this
      // screen have finished".
      await page.waitForLoadState("networkidle");

      const measured = await page.evaluate(() => ({
        scrollWidth: document.documentElement.scrollWidth,
      }));

      // SOFT, so one run names EVERY offending route instead of stopping at the
      // first. That is not a preference: the containment these assert is
      // per-element, so a regression typically hits some screens and not
      // others — measured under the table-overflow mutant, /sales, /history,
      // /flocks and /customers all overflow while /daily-entry and /stock stay
      // exactly 390. A hard assertion would report one quarter of that and
      // send the reader to fix one screen.
      //
      // The message names the route, and the mutation harness greps for it.
      // Load-bearing text, not decoration.
      expect.soft(
        measured.scrollWidth,
        `${route} scrolls sideways at phone width — the document is wider than the ${viewport.width}px frame`,
      ).toBeLessThanOrEqual(viewport.width);

      // THERE IS DELIBERATELY NO `window.innerWidth` ASSERTION HERE, and the
      // reason is worth keeping because the missing one looks obviously right.
      //
      // #441's defect has two halves. One is the document scrolling sideways,
      // asserted above. The other is a wide element inflating the LAYOUT
      // viewport itself — which is what anchors BottomNav's `position: fixed`,
      // so the tab bar renders below the visible screen while the document
      // looks fine. That half is what `contain: layout` exists for.
      //
      // An `expect(innerWidth).toBe(viewport.width)` for it shipped here for
      // two commits and COULD NOT FAIL. Layout-viewport inflation is mobile
      // emulation behaviour, and this project runs `devices["Desktop Chrome"]`
      // with a viewport override and no `isMobile` (see playwright.config.ts,
      // which says so). Under desktop emulation `innerWidth` tracks the frame
      // whatever the content does. Proof rather than reasoning: under
      // `phone-table-overflow-unclipped`, with /sales at scrollWidth 1420
      // against a 390 frame, that assertion fired ZERO times across all six
      // routes while four of them tripped the one above.
      //
      // So it was a guard that read as #441 coverage and was incapable of
      // providing any — and it contradicted the config, which already states
      // the device half is not covered. Restoring it means enabling `isMobile`
      // and re-measuring, not re-adding the line.
    }
  });
});
