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
 * app's: #829 moved the tab bar onto MUI `BottomNavigation`, and
 * `MuiBottomNavigationAction`'s theme override (FarmThemeProvider.tsx) sets
 * `minHeight: 44` explicitly — the same number the CSS `.tab` rule it
 * replaced used to reach via `min-height: 3.4rem` (54.4px, itself over the
 * 44px floor "the rest of the app now holds to").
 *
 * Not a pin on today's geometry: this is a floor, not a fixed size, so the
 * bar can render taller than 44px (icon + label + padding) without moving
 * this number. A change that trips it has shrunk the bar's actual target
 * below the floor the theme sets, which is the change worth failing on.
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
 * Every action row that exists at phone width, with the number of buttons
 * it must hold, the layout it must have, and how to reach it.
 *
 * Each row opens itself, and that is not indirection for its own sake: they
 * live on different routes, so a locator resolved before the walk moves on
 * matches nothing by the time it is measured.
 *
 * `layout` is what makes this a table rather than a loop over two selectors.
 * #823 stacks `.actions` rows below 900px, and two are exempt, for two
 * different reasons. The daily-entry bar: the #864 mockup the owner
 * confirmed keeps its two saves side by side (F134). A dialog footer: #896
 * (owner, 2026-09-17) moved `.dialog .dialog-foot` off the general stacking
 * rule too, row and right-aligned at every width, matching the #892
 * mockups — `MuiDialogActions` carries no phone override at all any more
 * (`FarmThemeProvider.tsx`), so a converted dialog's `DialogActions` and an
 * unconverted screen's raw `.dialog-foot` div now agree. A walk that
 * demanded full width everywhere would fail on both of these rows, which
 * the design says must not be full width.
 */
const PHONE_ACTION_ROWS: ReadonlyArray<{
  what: string;
  buttons: number;
  layout: "stacked" | "side by side";
  open: (page: Page) => Promise<Locator>;
}> = [
  {
    what: "the daily-entry save bar",
    buttons: 2,
    layout: "side by side",
    open: async (page) => {
      await page.goto("/daily-entry");
      // #830 — the bar converted to a MUI `Paper component="footer"`, the
      // only `<footer>` in the app; `.entry-actions` is a bare hook class
      // with no styles.css rule (sx owns the visuals), kept so this walk
      // measures the button row and not the footer's own outer padding.
      const foot = page.locator("footer");
      await expect(foot).toBeVisible();
      return foot.locator(".entry-actions");
    },
  },
  {
    what: "the Sales draft-order panel",
    buttons: 3,
    layout: "stacked",
    open: async (page) => {
      await page.goto("/sales");
      // The fixture seeds two draft orders and never confirms them
      // (SimulationDataSeeder), so this opens existing state instead of minting
      // an order and drawing stock out of the fixture on every phone run.
      const draft = page.getByRole("row").filter({ hasText: tEn("enums:status.Draft") }).first();
      await expect(draft, "the fixture has no draft order, so the #740 row cannot be measured")
        .toBeVisible();
      await draft.getByRole("button", { name: tEn("sales:open") }).click();
      // #831 — `.order-panel` retired; the draft panel is now a named
      // `role="region"` landmark (SalesPage.tsx), so scope through that
      // instead of the class the CSS selector used to key on. `.actions`
      // itself is unchanged — a bare hook class with a real phone-stacking
      // rule (styles.css) — so the row this measures is the same one.
      const row = page.getByRole("region").locator(".actions");
      await expect(row).toBeVisible();
      return row;
    },
  },
  {
    // #832/#896 — one open CRUD dialog stands in for all of them: every
    // dialog footer on Customers/Products/Grades/Flocks/Users converted from
    // the raw `.dialog-foot` div to MUI `DialogActions` in the same PR that
    // proved `MuiDialogActions` carries no phone override (see the comment
    // above `PHONE_ACTION_ROWS`), so one row is representative rather than a
    // sample of one. Grades' "New grade" dialog is the smallest of the five.
    what: "the Grades \"New grade\" dialog footer",
    buttons: 2,
    layout: "side by side",
    open: async (page) => {
      await page.goto("/grades");
      await page.getByRole("button", { name: tEn("grades:newGradeButton") }).click();
      const dialog = page.getByRole("dialog", { name: tEn("grades:newGradeDialogTitle") });
      await expect(dialog).toBeVisible();
      // `DialogActions` carries no ARIA role of its own; MUI's own generated
      // class is the stable hook, the same technique
      // `FarmThemeProvider.render.test.tsx` uses for `.MuiButton-contained`.
      const row = dialog.locator(".MuiDialogActions-root");
      await expect(row).toBeVisible();
      return row;
    },
  },
];

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

    // #830 — the bar is now a MUI `Paper component="footer"`, the only
    // `<footer>` in the app; measuring it directly is the same "outer sticky
    // element, not the inner button row" distinction the old `.entry-foot`
    // (not `.entry-foot .actions`) comment made.
    const foot = page.locator("footer");
    await expect(foot).toBeVisible();

    const footBox = await rectOf(foot, "the daily-entry action bar");
    const barBox = await rectOf(phone.tabbar, "the tab bar");

    // MEASURED MARGIN before #830, and it was 2.2px: the action bar's bottom
    // edge sat at 786.40625 and the tab bar's top edge at 788.609375 — tight
    // by construction, because `.entry-foot` parked at `bottom:
    // var(--tabbar-h)` and `.content` reserved exactly the same token as
    // bottom padding, so the two were designed to meet, not to overlap. #830
    // drops the matching negative-margin cancel the old rule carried (its
    // `Paper` sits in normal flow with #830's own spacing instead), so this
    // is re-measured against the CURRENT stack rather than assumed unchanged
    // — see this test's own failure message if the margin re-opens or closes
    // to zero. Any change that drops either half puts the Submit button
    // under the tab bar, where a thumb hits Sections instead.
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
    // #823 closed it by stacking: below 900px `.actions` lays out in a
    // column and every button fills the row, so no label can reshape the
    // control. BOTH halves are asserted, because the ratio alone stays green
    // for a button that stacked and then collapsed to its intrinsic width —
    // which is the same defect one step on. `.dialog .dialog-foot` and
    // `DialogActions` are no longer part of that stacking rule at all (#896;
    // see the comment above `PHONE_ACTION_ROWS`) — a dialog footer stays row
    // and right-aligned at every width, the same shape as the daily-entry
    // bar, for a different reason.
    //
    // MEASURED at 390 after #823, recorded here where this suite keeps its
    // measurements. Sales draft: all three 295.2 wide, 46.2 and 44.2 tall, 100%
    // of a 295.2 row — against 91.5x103.2, 89.8x103.2 and 89.9x103.2 before,
    // which is the #740 ellipse. Daily entry: both saves 170.6x65.2, 48% each
    // of a 353.2 row, unchanged from before #823 because that row is exempt.
    //
    // Under `phone-action-label-wrapped` the Sales row goes back to side by
    // side and every button drops to a fraction of its container: 51% and 22%,
    // with `close` at 54.0x65.2 — taller than it is wide, so the ratio
    // assertion fires there too. The daily-entry row and the dialog footer are
    // untouched by that mutant and are not evidence for it.
    //
    // ALL THREE ROWS ARE WALKED, and the Sales one is why the walk exists: the
    // daily-entry bar passed the ratio check before #823 and the Sales draft
    // panel did not, so a walk that stopped at the bar asserted the one row
    // that was never broken. The dialog footer joined in #832, as one open
    // CRUD dialog standing in for all of them (see its own entry above).
    for (const { what, buttons, layout, open } of PHONE_ACTION_ROWS) {
      const row = await open(page);
      // Non-vacuity: a walk over an empty set passes for free.
      await expect(row.getByRole("button"), `${what} renders no buttons`).toHaveCount(buttons);

      const measured = await row.evaluate((el) => ({
        container: el.getBoundingClientRect().width,
        flexDirection: getComputedStyle(el).flexDirection,
        buttons: Array.from(el.querySelectorAll("button")).map((b) => {
          const r = b.getBoundingClientRect();
          return { name: (b.textContent ?? "").trim(), width: r.width, height: r.height, top: r.top };
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

        // The width share is asserted in BOTH directions, one per layout, so
        // neither can drift into the other unnoticed.
        //
        // 90% and 60% rather than 100% and 50%: a row carries its own padding
        // and gap, and a floor that pinned the exact width would go red on a
        // gutter change that reshapes nothing. Measured, a stacked button is
        // 100% and a side-by-side pair is ~47% each, so both bounds sit far
        // from what they separate.
        if (layout === "stacked") {
          expect.soft(
            share,
            `"${b.name}" in ${what} spans ${(100 * share).toFixed(0)}% of its row — the action row `
              + "is side by side again, which is what #740 was",
          ).toBeGreaterThanOrEqual(0.9);
        } else {
          expect.soft(
            share,
            `"${b.name}" in ${what} spans ${(100 * share).toFixed(0)}% of its row — that row is `
              + "meant to stay side by side (F134, and the confirmed #864 mockup), and it stacked",
          ).toBeLessThan(0.6);
        }
      }

      // #897 codex review — the width-share checks above are a per-button
      // ratio and pass for free on a STACKED column of narrow, intrinsic-width
      // buttons too: a button that does not fill its row is <60% of the
      // container whether it sits beside its sibling or above it. Neither
      // half of "side by side" that name actually claims — same row, same
      // reading order — was ever checked. This asserts the row geometry
      // directly: the row's own flex-direction is "row", and every button
      // shares some common vertical band with every other (a real intersection
      // of their [top, bottom] ranges, not an edge-equality check — Grades'
      // own footer pairs a text `Cancel` link against a taller pill `Add
      // grade` button, `alignItems: "center"`, so their TOPS legitimately
      // differ by several px while they are genuinely on one line; a column
      // can never produce an intersecting band, however each button aligns
      // within it). `phone-dialog-footer-stacked` is the mutant for this.
      if (layout === "side by side" && measured.buttons.length > 1) {
        // Soft, like every other check in this walk — so a mutant that breaks
        // both halves reports both, rather than the first throwing and hiding
        // the second from the log (which EXPECT_MSG_FOR would then declare
        // wrongly: a message that never gets the chance to appear).
        expect.soft(
          measured.flexDirection,
          `${what}'s row is not laid out as a row (computed flex-direction: ${measured.flexDirection}) `
            + "— its buttons may still be a narrow stacked column rather than side by side",
        ).toBe("row");

        const tops = measured.buttons.map((b) => b.top);
        const bottoms = measured.buttons.map((b) => b.top + b.height);
        const bandStart = Math.max(...tops);
        const bandEnd = Math.min(...bottoms);
        expect.soft(
          bandEnd - bandStart,
          `${what}'s buttons share no common vertical band (closest they get is `
            + `${(bandEnd - bandStart).toFixed(1)}px) — they read as side by side by width alone but `
            + "are actually stacked",
        ).toBeGreaterThan(0);
      }
    }
  });

  // #830 (owner's screenshot review of #888), fix 1. The ratio check above
  // (no control taller than it is wide) already passed on this row before
  // this fix — a wrapped label inside a fixed-height MUI button still isn't
  // TALLER than it is WIDE at this row's width, so it could not have caught
  // the #740 shape the owner actually saw: "Save & submit (creates egg lots)"
  // wrapped to three lines inside the pill. This asserts the more direct
  // thing — the label fits on one line — by comparing the rendered height
  // against the font's own line-height, which a wrap doubles and a fixed
  // min-height with padding does not.
  test("the daily-entry footer buttons render their label on one line", async ({ page }) => {
    await page.goto("/daily-entry");
    const foot = page.locator("footer");
    await expect(foot).toBeVisible();
    const buttons = foot.locator(".entry-actions button");
    await expect(buttons, "the daily-entry footer rendered no buttons to measure").toHaveCount(2);

    const measured = await buttons.evaluateAll((els) => els.map((el) => {
      const style = getComputedStyle(el);
      const parsedLineHeight = parseFloat(style.lineHeight);
      // `line-height: normal` computes as the string "normal", not a px
      // value — fall back to the CSS-typical 1.2x font-size multiplier.
      const lineHeight = Number.isNaN(parsedLineHeight)
        ? parseFloat(style.fontSize) * 1.2
        : parsedLineHeight;
      return { name: (el.textContent ?? "").trim(), height: el.getBoundingClientRect().height, lineHeight };
    }));

    for (const b of measured) {
      // At least 44px (WCAG 2.2 AAA 2.5.5), matching MIN_TARGET_PX above.
      expect.soft(
        b.height,
        `"${b.name}" in the daily-entry footer is ${b.height.toFixed(1)}px tall — under the 44px touch-target floor`,
      ).toBeGreaterThanOrEqual(44);
      // At most one line taller than the font's own line-height: a single
      // line plus the control's padding fits well under this; a wrapped
      // label (two text lines) does not.
      expect.soft(
        b.height,
        `"${b.name}" in the daily-entry footer is ${b.height.toFixed(1)}px tall against a `
          + `${b.lineHeight.toFixed(1)}px line-height — its label wrapped onto a second line`,
      ).toBeLessThanOrEqual(b.lineHeight * 2);
    }
  });

  // #830 (owner's screenshot review of #888), fix 2. Before this fix each row
  // was a flex `justify-content: space-between` pair, so a label wide enough
  // to overflow squeezed the stepper beside it by a different amount per
  // row — the owner saw this as each row's minus button sitting at a
  // different x. `.numfield > button:first-child` / `:last-child` are the
  // minus/plus buttons structurally (NumberField.tsx), not by their
  // (translated) aria-label, per this suite's own no-hardcoded-English rule.
  // At 390 both panes stack into one column (DailyEntryPage.tsx's `gridTemplateColumns`
  // switches from `repeat(2, ...)` to `1fr` below `md`), so every row in the
  // walk shares one container width and a real fix lines every button in
  // BOTH panes up to a single x, not just within one pane.
  test("the daily-entry stepper rows line their minus and plus buttons up in one column", async ({ page }) => {
    await page.goto("/daily-entry");
    const minusButtons = page.locator(".numfield > button:first-child");
    const plusButtons = page.locator(".numfield > button:last-child");
    // `.count()` does not auto-wait like an assertion does — it reads
    // whatever is in the DOM the instant it runs, and the flocks/grades
    // fetch that gates this form (DailyEntryPage's `loading` state) has not
    // always settled by then. Wait for the first row before counting.
    await expect(minusButtons.first(), "the daily-entry screen rendered no stepper rows to measure")
      .toBeVisible();
    const rowCount = await minusButtons.count();
    expect(rowCount, "the daily-entry screen rendered only one stepper row to measure").toBeGreaterThan(1);
    expect(await plusButtons.count(), `${rowCount} minus buttons but a different number of plus buttons: a row is missing one`)
      .toBe(rowCount);

    const roundedXs = async (locator: Locator) =>
      new Set((await locator.evaluateAll((els) => els.map((el) => el.getBoundingClientRect().x)))
        .map((x) => Math.round(x)));

    const minusXs = await roundedXs(minusButtons);
    const plusXs = await roundedXs(plusButtons);

    expect(
      minusXs.size,
      `minus buttons sit at ${[...minusXs].join(", ")}px — not one shared x across the ${rowCount} rows`,
    ).toBe(1);
    expect(
      plusXs.size,
      `plus buttons sit at ${[...plusXs].join(", ")}px — not one shared x across the ${rowCount} rows`,
    ).toBe(1);
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
    // holding `$9,999,999.99` inside the recent-sales list, pushed 53px off
    // screen. That was real, and it was STALE BYTES: the sim stack had been up
    // 28 hours, so it was serving an image built before #781
    // (`7193ebe fix(dashboard): give recent sales real columns…`, 2026-09-12),
    // which is the commit that gave this list its container-query narrow
    // layout and is seven commits behind this branch's base. Rebuilding the
    // stack made the dashboard measure 390/390 with those same rows present.
    // #829 replaced that container-query grid with an `sx`-laid-out flex row
    // (`ul.dash-list` is gone), so this walk now measures the row's own
    // flex-wrapped content instead — still against `ul.dash-sales-list`, the
    // stable, unstyled locator hook Dashboard.tsx carries for exactly this.
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
      { path: "/", content: "ul.dash-sales-list", what: "the recent-sales list" },
      { path: "/daily-entry", content: "footer", what: "the entry form's sticky foot" },
      // #832 — Customers and Flocks moved their table onto MUI's `Table`, which
      // carries no `.data` class (the whole point of the conversion: the
      // screen's TSX carries no className `styles.css` still declares). A
      // role locator survives the conversion of the OTHER three routes too,
      // whenever their turn comes — `role=table` matches a real `<table>`
      // either way, `table.data` or MUI's.
      { path: "/customers", content: "role=table", what: "the customer book" },
      { path: "/flocks", content: "role=table", what: "the flock table" },
      // #831 keeps grade comparisons in a named board and history in a table.
      { path: "/stock", content: `role=list[name="${tEn("stock:title")}"]`, what: "the stock board" },
      { path: "/history", content: "role=table", what: "the entry history table" },
      { path: "/sales", content: "role=table", what: "the orders table" },
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

  // #883 round 5 — the owner's read of the PR's screenshots: at 390 the
  // dashboard's recent-sales row used to force customer name, order number,
  // amount and status onto one line, and `.cust` (styles.css:
  // `overflow:hidden;white-space:nowrap;text-overflow:ellipsis`) clipped the
  // name to "KC…". Dashboard.tsx now stacks the row at this width — name and
  // amount on one line, status and action on the next — and drops `.cust`'s
  // clipping there, so the name gets the row's full width instead of a
  // shrunk share of four cells. `scrollWidth === clientWidth` is the direct
  // test of "not clipped": an ellipsis-truncated element's content
  // (`scrollWidth`) is wider than its box (`clientWidth`) by construction,
  // and an untruncated one never is.
  test("the recent-sales customer name is not truncated at phone width", async ({ page }) => {
    await page.goto("/");
    const salesList = page.locator("ul.dash-sales-list");
    const firstRow = salesList.locator("li").first();
    await expect(firstRow, "the dashboard rendered no recent-sales rows to measure").toBeVisible();

    const customerName = firstRow.getByRole("link").first();
    const [scrollWidth, clientWidth] = await customerName.evaluate((el) => [el.scrollWidth, el.clientWidth]);
    expect(
      scrollWidth,
      `the customer name's scrollWidth (${scrollWidth}) exceeds its clientWidth (${clientWidth}) — `
        + "it is clipped by ellipsis truncation",
    ).toBe(clientWidth);
  });
});
