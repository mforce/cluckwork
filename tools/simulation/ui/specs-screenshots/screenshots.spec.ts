// tools/simulation/ui/specs-screenshots/screenshots.spec.ts — #549.
//
// Captures the three images the root README embeds, from the REAL built SPA
// over the #243 simulation fixture. Run it deliberately:
//
//     npm run screenshots        # from tools/simulation/ui, with the sim stack up
//
// ================== THE STALENESS CONTRACT ==================
//
// These images are DOCUMENTATION ARTEFACTS, not test baselines, and **nothing
// enforces that they match the current UI**. A change to any of the three
// screens below silently invalidates them; a reviewer looking at a stale
// screenshot cannot tell. The honest mitigation is the one this file can
// actually keep: the capture is scripted, so refreshing is one command rather
// than a hunt for whoever took the last one. Re-run it when a captured screen
// changes, and at each release.
//
// Deliberately NOT a visual-regression gate. Rendering is not byte-deterministic
// across fonts, antialiasing and GPU paths, so a byte-diff check in the shape of
// the #417 schema-docs gate would flake — and a flaky gate gets disabled, which
// is worse than an honest "no gate".
//
// ================== WHY IT ASSERTS BEFORE IT CAPTURES ==================
//
// A screenshot of a half-rendered page is worse than no screenshot: it is a
// published claim that the app looks like that. Every capture below waits for a
// signal that the screen's DATA has arrived — not merely that the route
// rendered — because the panels fetch independently and each degrades to its
// own error text. `page.screenshot` also waits for fonts, so the assertions are
// the only part that needs saying out loud.
//
// The suite's three standing rules (#277/#385) apply here as they do to every
// other spec: no hardcoded credential (personas come from the git-ignored
// `.sim-cast.json`), no hardcoded English (selector text resolves through the
// SPA's own catalogs), and respect the farm clock.

import { mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { expect, test, type Page } from "../src/fixtures";
import { castMember } from "../src/cast";
import { daysBefore, farmToday } from "../src/farm";
import { tEn } from "../src/i18n";

// tools/simulation/ui/specs-screenshots/ -> repo root -> docs/images/
const IMAGE_DIR = fileURLToPath(new URL("../../../../docs/images/", import.meta.url));

function imagePath(name: string): string {
  mkdirSync(IMAGE_DIR, { recursive: true });
  return `${IMAGE_DIR}${name}`;
}

// `animations: "disabled"` finishes CSS transitions instead of freezing them
// mid-flight, so a capture cannot land on a half-faded panel.
//
// Blurring first is not cosmetic pedantry: a field this spec typed into keeps
// the focus ring AND its text selection, so the published image shows a form
// mid-edit with half a date highlighted — which reads as a screenshot somebody
// took by accident.
async function capture(page: Page, name: string): Promise<void> {
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await page.screenshot({ path: imagePath(name), animations: "disabled" });
}

test.describe("README screenshots", () => {
  // ================== THE DASHBOARD SHOT (#654) ==================
  //
  // #549 left the Dashboard out because its Today panel was empty in every
  // fixture capture. Since #654 that emptiness IS the feature: a house with
  // no entry is the alarm state the screen exists to show, so the tiles are
  // captured as the fixture leaves them — twelve "no entry" tiles and the
  // "N more flocks" link (the fixture seeds ~100 active flocks for the picker
  // catalog, #627).
  //
  // The image IS committed and the README embeds it (#660, #663) — all four
  // captures this file produces are now tracked, so an untracked fifth image
  // would be the anomaly, not this one. The alarm state is still what gets
  // photographed: on the current fixture the twelve leading tiles are all
  // catalog flocks with no entries (#627 seeds ~100), so the two real houses
  // sit behind the "N more flocks" link. #660 gave this test its own project
  // in playwright.screenshots.config.ts at a taller 1280x1180 frame, so the
  // trend, stock and recent-sales panels are captured below the tiles instead
  // of falling off a 1280x800 fold.

  test("dashboard — the morning view: capture status, the fortnight, stock by grade", async ({ page, signIn }) => {
    await signIn(castMember("Manager"));
    await page.goto("/");

    // The page has ONE loading gate over six reads (Promise.allSettled), so
    // nothing below renders half-loaded — but every panel degrades on its own
    // failed read, so an errored panel would be photographed as if it were
    // the product. Assert the absence of both error texts first.
    await expect(page.getByText(tEn("dashboard:loadFailed"))).toHaveCount(0);
    await expect(page.getByText(tEn("dashboard:panelLoadError"))).toHaveCount(0);

    // Tiles: at least one rendered. Class locator — the tile's accessible
    // name interpolates a flock name this spec does not know.
    await expect(page.locator(".capture-tile").first()).toBeVisible();

    // Trend: the sparkline is there AND not flat — the fixture seeds 90 days
    // of production, so a flat line means the report did not arrive.
    const line = page.locator("svg.sparkline");
    await expect(line).toBeVisible();
    const ys = (await line.locator("polyline").getAttribute("points"))!.split(" ").map((p) => p.split(",")[1]);
    expect(new Set(ys).size).toBeGreaterThan(1);

    // Stock: at least one segment on the bar.
    await expect(page.locator(".meter-stack > span").first()).toBeVisible();

    // Sales: a Manager sees the panel and the fixture has orders — scoped to
    // the sales list, never the shell's own list items.
    await expect(page.locator(".dash-list li").first()).toBeVisible();

    await capture(page, "dashboard.png");
  });

  test("daily entry — a recorded day, by grade", async ({ page, signIn, farm }) => {
    await signIn(castMember("Manager"));

    // YESTERDAY, not today, and this is the whole point of the capture. The
    // fixture seeds history up to the previous farm-local day, so today's form
    // is an empty one — a screenshot of five zeroes that says nothing about
    // what the screen is for. Yesterday has a real submitted entry: counts,
    // grades, and the sellable arithmetic that makes the screen legible.
    //
    // Farm-local, via the farm fixture: `America/Chicago` is behind UTC, so a
    // UTC "yesterday" is sometimes today's date on the farm and the capture
    // would silently be the empty form again.
    const day = daysBefore(farmToday(farm.timeZoneId), 1);

    // #446: the page fires a prefill that RESETS every count when it settles.
    // Capturing before that lands photographs numbers the app is about to throw
    // away. Register the wait BEFORE the navigation that triggers it.
    const prefill = page.waitForResponse((r) => r.url().includes("/daily-entries") && r.ok());
    await page.goto("/daily-entry");
    await page.getByLabel(tEn("dailyEntry:dateLabel")).fill(day);
    await prefill;

    // The assertion IS the point of choosing yesterday: a non-zero total proves
    // the seeded day loaded, which is the whole difference between this capture
    // and a screenshot of an empty form. Waiting on the route or on a label
    // would pass just as happily on the empty one.
    //
    // Three other signals were refuted first, and each failure mode is worth
    // knowing: "Sellable" resolves to two elements (totals label and summary
    // line — strict-mode violation); `allGradedShort` is present but CSS-hidden
    // here, so waiting on it times out claiming the data never arrived when it
    // had; and `entryLockedBanner` never renders for this day at all.
    await expect(
      page.getByRole("spinbutton", { name: tEn("dailyEntry:totalEggsLabel") }),
    ).not.toHaveValue("0");

    // Read-only on purpose: this capture must not submit, adjust or void
    // anything. It shares one seeded database with every other spec here.
    await capture(page, "daily-entry.png");
  });

  test("reports — production and money over the seeded period", async ({ page, signIn, farm }) => {
    await signIn(castMember("Owner"));
    await page.goto("/reports");

    // The default range ends TODAY, and the fixture's most recent days are
    // drafts — which a report does not count. The default view is therefore
    // three trailing rows of zeroes, which is a true statement about the
    // fixture and a misleading one about the product.
    //
    // So the range is set explicitly to a window that is entirely submitted
    // days. The offsets are fixture knowledge, not a general truth: the seeder
    // leaves roughly the last few days as drafts. If that changes, the
    // assertion below is what fails.
    const today = farmToday(farm.timeZoneId);
    // `exact` on both, and the reason is the SECOND one: the sidebar's theme
    // toggle is labelled "Switch to night mode", which contains "To" — so a
    // substring match on the To field is ambiguous with a button that changes
    // the whole page's colours.
    await page.getByLabel(tEn("reports:fromLabel"), { exact: true }).fill(daysBefore(today, 9));
    await page.getByLabel(tEn("reports:toLabel"), { exact: true }).fill(daysBefore(today, 3));

    await expect(page.getByRole("heading", { name: tEn("reports:productionHeading") })).toBeVisible();
    await expect(page.getByRole("heading", { name: tEn("reports:moneyHeading") })).toBeVisible();

    // Renders only when the period produced something (ReportsPage guards on
    // `gradeTotals.length > 0`), so it proves the window is not empty.
    await expect(page.getByText(tEn("reports:gradeTotalsLabel"))).toBeVisible();

    // ...but a non-empty PERIOD can still end in zero rows, which is exactly
    // the thing being avoided. The last row's Eggs cell is the specific check:
    // the window's final day must itself have production.
    const lastRow = page.getByRole("table").first().locator("tbody tr").last();
    await expect(lastRow.locator("td").nth(1)).not.toHaveText("0");

    await capture(page, "reports.png");
  });

  test("sales — orders across their lifecycle", async ({ page, signIn }) => {
    await signIn(castMember("Sales"));
    await page.goto("/sales");

    await expect(page.getByRole("heading", { name: tEn("sales:ordersHeading") })).toBeVisible();

    // The heading and the absence of "Loading…" are NOT enough, and the failure
    // they miss is the one that matters: when the initial /sales request fails,
    // `usePagedList` empties the rows and clears its loading flag, so the page
    // settles into an error plus the empty-state message with the heading still
    // there. Both weak signals pass, and the run overwrites the committed image
    // with a picture of an error while reporting success.
    //
    // So: the empty state must be absent, and the table must actually hold
    // orders in the two lifecycle states this capture exists to show.
    // `sales:noOrdersMatch`, not `dashboard:noOrdersMessage` — two different
    // empty states in two namespaces, and this page renders the former. The
    // wrong one is not a silent miss: src/i18n.ts throws on an absent key
    // rather than falling back, which is how this was caught.
    await expect(page.getByText(tEn("sales:noOrdersMatch"))).toHaveCount(0);
    const orders = page.getByRole("table").first();
    await expect(orders.getByRole("row").filter({ hasText: tEn("enums:status.Confirmed") }))
      .not.toHaveCount(0);
    await expect(orders.getByRole("row").filter({ hasText: tEn("enums:status.Draft") }))
      .not.toHaveCount(0);

    // The orders list and the page's SETUP data (customers, products, grades)
    // are two independent loads, and the rows render either way: `customerName`
    // falls back to `id.slice(0, 8)`, so a capture taken between them shows a
    // column of GUID fragments where customer names belong. Worse, a setup
    // failure replaces the whole screen with `setupError` — after these row
    // assertions have already passed.
    //
    // The New order button is the setup-derived signal: it renders only past
    // that gate, so it is absent in both the mid-load and the error state.
    await expect(page.getByRole("button", { name: tEn("sales:newOrder") })).toBeVisible();

    await capture(page, "sales.png");
  });
});

