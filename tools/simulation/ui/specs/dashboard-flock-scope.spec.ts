// #916/#918 — the Lay rate card's flock scope and partial-day scale fallback,
// against the SIMULATION fixture: its ~100 never-filing flocks leave no day
// complete (the state that used to floor every bar at 2%), which only a real
// server can prove still works. Assertions watch the NETWORK, not the figures.

import { test, expect, type Page, type Locator } from "../src/fixtures";
import { owner, restrictedWorker } from "../src/cast";
import { signInForToken, apiGet } from "../src/api";
import { daysBefore, farmToday } from "../src/farm";
import { tEn } from "../src/i18n";
// Deliberately NOT imported from web/src/lib/productionReportSplit — that
// module transitively pulls in api/client.ts, which this project's own
// (stricter) tsconfig has never had reason to type-check before, and doing
// so surfaces unrelated pre-existing issues. A local, loosely-typed copy of
// the same recomputation keeps this spec self-contained, matching every
// other spec here that defines its own helpers rather than importing SPA
// implementation code.
interface ReportDay { date: string; totalEggs: number; ratedEggs: number; recordedHenDays: number }
function splitProductionReport(report: { days: ReportDay[] }, cutoffDate: string) {
  const earlier: ReportDay[] = [];
  const later: ReportDay[] = [];
  for (const d of report.days) (d.date < cutoffDate ? earlier : later).push(d);
  const summarize = (days: ReportDay[]) => {
    const totalRatedEggs = days.reduce((a, d) => a + d.ratedEggs, 0);
    const totalRecordedHenDays = days.reduce((a, d) => a + d.recordedHenDays, 0);
    return {
      days,
      totalEggs: days.reduce((a, d) => a + d.totalEggs, 0),
      periodHenDayPct: totalRecordedHenDays > 0 ? Math.round((totalRatedEggs * 100 / totalRecordedHenDays) * 10) / 10 : null,
    };
  };
  return { earlier: summarize(earlier), later: summarize(later) };
}

// The fixture's first flock — the one SimulationDataSeeder restricts
// `restrictedWorker()` to, so the same name serves both halves of this spec.
const FIXTURE_FLOCK = "Sim House A";

/** The Lay rate card's strip, and the figures rendered above it. */
function strip(page: Page) {
  return page.locator("figure.trend");
}

/** The card element itself — the strip's closest MUI Card ancestor. */
function layRateCard(page: Page) {
  return page.locator(".MuiCard-root").filter({ has: strip(page) });
}

/**
 * The single mockup selector: a full-width button whose accessible name is
 * "Flock" (the eyebrow) plus the current scope, joined by aria-labelledby.
 * Found by `aria-haspopup="dialog"` rather than by name text — a flock
 * literally named "Sim House A" would otherwise be indistinguishable by name
 * alone from a plain-text match once the dialog's own choice buttons exist.
 * SCOPED to the Lay rate card: the app shell's own phone "More" tab also
 * carries `aria-haspopup="dialog"` (it opens the nav sheet) and Playwright's
 * role queries do not filter by CSS visibility, so an unscoped query is
 * ambiguous even at a desktop viewport where that tab is not shown.
 */
function selectorButton(page: Page) {
  return layRateCard(page).locator('button[aria-haspopup="dialog"]');
}

function pickerDialog(page: Page) {
  return page.getByRole("dialog", { name: tEn("dashboard:chooseFlockTitle") });
}

/** Pinned ABOVE the scrolling result list — never a row inside it. */
function pinnedAllFlocksChoice(page: Page) {
  return page.getByRole("button", { name: new RegExp(`^${tEn("dashboard:allFlocksOption")}`) });
}

function resultsList(page: Page) {
  return page.getByRole("list", { name: tEn("dashboard:flockScopeResultsLabel") });
}

async function openPicker(page: Page) {
  await selectorButton(page).click();
  await expect(pickerDialog(page)).toBeVisible();
}

async function pickAllFlocks(page: Page) {
  await openPicker(page);
  await pinnedAllFlocksChoice(page).click();
  await expect(pickerDialog(page)).not.toBeVisible();
}

/** Every bar's inline height, in slot order. An unrecorded day contributes nothing. */
async function barHeights(page: Page): Promise<string[]> {
  return strip(page).locator(".daystrip .day i").evaluateAll(
    (els) => els.map((e) => (e as HTMLElement).style.height));
}

/** The strip's DOM position relative to the hen-day KPI, for the DOM-order check. */
async function kpiFollowsStrip(card: Locator): Promise<boolean> {
  return card.evaluate((cardEl) => {
    const stripEl = cardEl.querySelector(".daystrip");
    const kpiEl = cardEl.querySelector(".trend-kpi");
    if (!stripEl || !kpiEl) return false;
    // DOCUMENT_POSITION_FOLLOWING = 4.
    return (stripEl.compareDocumentPosition(kpiEl) & 4) === 4;
  });
}

test.describe("Dashboard Lay rate flock scope", () => {
  test("farm-wide, no complete day: the strip scales to the largest partial day", async ({ page, signIn }) => {
    await signIn(owner());
    await page.goto("/");
    await expect(strip(page)).toBeVisible();

    // Farm-wide is the default — the selector's own accessible name says so.
    await expect(selectorButton(page)).toHaveAccessibleName(`${tEn("dashboard:flockScopeLabel")} ${tEn("dashboard:allFlocksOption")}`);

    // The context caption: "{count} accessible flocks · {range}" — matches
    // the mockup's `.context` line exactly (#918).
    await expect(page.locator(".trend-context")).toContainText("accessible flock");

    // The fixture's own shape: ~100 flocks are placed and never file, so every
    // day in the window owes a count nobody filed. If this stops being true the
    // rest of the test is measuring nothing, so it is asserted, not assumed.
    const caption = strip(page).locator(".trend-scale > span").first();
    await expect(
      caption,
      "no day in this fixture's window should be complete — without that, the partial-day "
        + "fallback under test is never reached",
    ).toHaveText(tEn("dashboard:trendScaleTitlePartial"));

    // THE REGRESSION. Before #916 the peak came from the complete days alone,
    // so this window had none, every bar fell to the 2% floor and Peak read
    // "—". The largest partial day now defines the scale, which means exactly
    // one bar reaches 100% and the strip carries a real figure.
    const peak = strip(page).locator(".trend-peak");
    await expect(peak, "the partial-day peak should be a figure, not an em dash").not.toHaveText("Peak —");

    const heights = await barHeights(page);
    expect(heights.length, "the fixture's window should draw at least a few bars").toBeGreaterThan(1);
    expect(
      heights.filter((h) => h === "100%"),
      `the largest partial day should reach the top of the strip; got ${JSON.stringify(heights)}`,
    ).toHaveLength(1);
    expect(
      new Set(heights).size,
      `every bar is ${heights[0]} — this is the hairline-strip bug #916 fixed`,
    ).toBeGreaterThan(1);

    // The mockup's three-item legend, and the hen-day KPI moved to the
    // bottom of the card (#918 fidelity round).
    const legendItems = await strip(page).locator(".trend-legend li").allInnerTexts();
    expect(legendItems).toEqual([
      tEn("dashboard:legendComplete"), tEn("dashboard:legendPartial"), tEn("dashboard:legendNoEntry"),
    ]);
    expect(await kpiFollowsStrip(layRateCard(page)), "the hen-day KPI should render AFTER the strip").toBe(true);
  });

  test("the picker's All flocks choice is pinned above the scrolling results, and search rescopes the card server-side", async ({ page, signIn }) => {
    await signIn(owner());
    await page.goto("/");
    await expect(strip(page)).toBeVisible();

    const before = {
      caption: await strip(page).locator(".trend-scale > span").first().innerText(),
      peak: await strip(page).locator(".trend-peak").innerText(),
      heights: await barHeights(page),
    };

    await openPicker(page);
    // The mockup's #allChoice: a sibling of .choices, never a row inside it.
    const results = resultsList(page);
    await expect(
      results.getByRole("button", { name: new RegExp(`^${tEn("dashboard:allFlocksOption")}`) }),
      "All flocks must not also appear as a row inside the scrolling results",
    ).toHaveCount(0);
    await expect(pinnedAllFlocksChoice(page)).toBeVisible();

    // The production read must carry the chosen flock. This is the whole
    // API half of #916: a client-side filter of the farm-wide payload would
    // render plausible bars and could not produce a scoped hen-day rate.
    // #918 — Codex review: one combined 14-day request now, not two adjacent
    // 7-day ones (RateLimitingOptions.ReportsConcurrency's shared,
    // unqueued permit cap).
    const scopedReports: string[] = [];
    page.on("request", (r) => {
      const url = r.url();
      if (url.includes("/reports/production") && url.includes("flockId=")) scopedReports.push(url);
    });

    // The expected id is resolved independently, from the server's own flock
    // list rather than scraped out of the DOM — so the assertion below compares
    // the request's `flockId` against a value the page never supplied.
    const ownerTokenForId = await signInForToken(owner());
    const accessible = await apiGet<Array<{ id: string; name: string }>>(ownerTokenForId, "/flocks?limit=500");
    const flockId = accessible.find((f) => f.name === FIXTURE_FLOCK)?.id;
    expect(flockId, `${FIXTURE_FLOCK} should be in the accessible flock list`).toBeTruthy();

    await page.getByRole("searchbox", { name: tEn("dashboard:searchAccessibleFlocksLabel") }).fill(FIXTURE_FLOCK);
    const choice = resultsList(page).getByRole("button", { name: FIXTURE_FLOCK });
    await expect(choice).toBeVisible();
    await choice.click();
    await expect(pickerDialog(page)).not.toBeVisible();

    // The commit closes the dialog and the selector's own name updates.
    await expect(selectorButton(page)).toHaveAccessibleName(`${tEn("dashboard:flockScopeLabel")} ${FIXTURE_FLOCK}`);

    // One flock's own days ARE complete days — it is the only flock expected to
    // file — so the card leaves the partial-day fallback entirely. That is a
    // stronger claim than "the numbers moved": it can only happen if the
    // SERVER recomputed completeness against a one-flock expectation.
    await expect(strip(page).locator(".trend-scale > span").first())
      .toHaveText(tEn("dashboard:trendScaleTitle"));
    await expect(strip(page).locator(".trend-peak")).not.toHaveText("Peak —");
    expect(await barHeights(page), "the scoped strip should not be the farm-wide one")
      .not.toEqual(before.heights);

    expect(
      flockId && scopedReports.filter((u) => u.includes(`flockId=${flockId}`)).length,
      `the production read should carry flockId=${flockId}; saw ${JSON.stringify(scopedReports)}`,
    ).toBeGreaterThanOrEqual(1);

    // And back. The return path is its own assertion: clearing the card's
    // scope has to clear the selector's own displayed value too, or it goes
    // on naming a flock the figures no longer describe (Codex review of
    // #918, finding 1).
    const farmWideReads = page.waitForRequest((r) =>
      r.url().includes("/reports/production") && !r.url().includes("flockId="));
    await pickAllFlocks(page);
    await farmWideReads;

    await expect(selectorButton(page)).toHaveAccessibleName(`${tEn("dashboard:flockScopeLabel")} ${tEn("dashboard:allFlocksOption")}`);
    await expect(strip(page).locator(".trend-scale > span").first()).toHaveText(before.caption);
    await expect(strip(page).locator(".trend-peak")).toHaveText(before.peak);
  });

  // #918 — Codex review, P3-4. MUI's Dialog otherwise focuses the first
  // tabbable control (the close button), not the search box the approved
  // mockup focuses on open; also checks the search box meets the repo's 44px
  // touch-target floor.
  test("focuses the search box on open, and it meets the 44px target floor", async ({ page, signIn }) => {
    await signIn(owner());
    await page.goto("/");
    await openPicker(page);
    const search = page.getByRole("searchbox", { name: tEn("dashboard:searchAccessibleFlocksLabel") });
    await expect(search).toBeFocused();
    // The bordered, clickable target is the MUI input WRAPPER, not the bare
    // `<input>` — its own content-box height is intrinsically shorter than
    // the wrapper around it, which is what a tap actually has to land on.
    const height = await search.evaluate((el) => el.closest(".MuiInputBase-root")!.getBoundingClientRect().height);
    expect(height, `the search box's wrapper is ${height.toFixed(2)}px tall — under the 44px touch-target floor`).toBeGreaterThanOrEqual(44);
  });

  test("the picker shows a no-results state for a name nothing matches", async ({ page, signIn }) => {
    await signIn(owner());
    await page.goto("/");
    await openPicker(page);
    await page.getByRole("searchbox", { name: tEn("dashboard:searchAccessibleFlocksLabel") })
      .fill("no such flock exists anywhere");
    await expect(page.getByText(tEn("dashboard:noMatchingFlocksMessage"))).toBeVisible();
  });

  test("one accessible flock: its name, with no scope control at all", async ({ page, signIn }) => {
    // `restrictedWorker()` is the fixture's single-flock reader: #613's flock-
    // scope query filter narrows the READ as well as the write, so `GET
    // /flocks` answers with exactly one row — what the card keys its
    // plain-text rendering on (src/cast.ts's own note still describes pre-#613).
    await signIn(restrictedWorker());
    await page.goto("/");
    await expect(strip(page)).toBeVisible();

    const card = layRateCard(page);
    await expect(
      card.getByText(FIXTURE_FLOCK, { exact: true }),
      "the one accessible flock should be named in the card",
    ).toBeVisible();

    // No choice to offer, so no control is offered at all — no selector, no
    // chevron, no dialog to open.
    await expect(selectorButton(page)).toHaveCount(0);

    // The figures still arrive, and by the SAME scoped path a picked flock
    // takes — never a second "just show everything" branch.
    await expect(strip(page).locator(".daystrip .day").first()).toBeVisible();
  });

  test("the only-one view and a picked single flock read the same report", async ({ farm }) => {
    // SELECTION.md: "The selected-single and only-one views must show identical
    // data for the same flock." The claim is really about the SERVER: the same
    // flock, asked for by two principals with very different reach, must answer
    // identically — checked via two tokens, since `signIn` supports one context.
    const today = farmToday(farm.timeZoneId);
    const range = `?from=${daysBefore(today, 7)}&to=${daysBefore(today, 1)}`;

    const workerToken = await signInForToken(restrictedWorker());
    const visible = await apiGet<Array<{ id: string; name: string }>>(workerToken, "/flocks?limit=500");
    expect(
      visible.map((f) => f.name),
      "this persona is the single-flock reader; more than one row means the fixture's "
        + "restriction, or #613's read scoping, has changed",
    ).toEqual([FIXTURE_FLOCK]);
    const flockId = visible[0]!.id;

    const ownerToken = await signInForToken(owner());
    const asOwner = await apiGet<unknown>(ownerToken, `/reports/production${range}&flockId=${flockId}`);
    const asWorker = await apiGet<unknown>(workerToken, `/reports/production${range}&flockId=${flockId}`);
    expect(asWorker, "the two views' figures came from different reports").toEqual(asOwner);
  });

  // #918 — Codex review: the Dashboard used to fire two adjacent production
  // requests per load (current week, previous week); together with the
  // Morning collection panel's own yesterday-close fetch, that was three of
  // the account's four shared report-concurrency permits (RateLimitingOptions.
  // ReportsConcurrency: PermitLimit 4, QueueLimit 0), so two workers opening
  // the app together could exceed it. The two windows are now ONE
  // `daysBefore(today,14)..daysBefore(today,1)` request, split client-side
  // by `splitProductionReport`. This is the settling test the fix depends
  // on: the REAL server's combined response, split, must equal what the
  // two separate requests it replaces would have returned — checked against
  // the running simulation server, not a fixture standing in for it.
  test("splitting one 14-day report client-side equals two separate 7-day requests", async ({ farm }) => {
    const today = farmToday(farm.timeZoneId);
    const token = await signInForToken(owner());

    const combined = await apiGet<{ days: ReportDay[] }>(
      token, `/reports/production?from=${daysBefore(today, 14)}&to=${daysBefore(today, 1)}`);
    const { earlier, later } = splitProductionReport(combined, daysBefore(today, 7));

    const previous = await apiGet<{ days: ReportDay[]; totalEggs: number; periodHenDayPct: number | null }>(
      token, `/reports/production?from=${daysBefore(today, 14)}&to=${daysBefore(today, 8)}`);
    const current = await apiGet<{ days: ReportDay[]; totalEggs: number; periodHenDayPct: number | null }>(
      token, `/reports/production?from=${daysBefore(today, 7)}&to=${daysBefore(today, 1)}`);

    expect(earlier.days, "the earlier half's own days should equal the server's previous-week days").toEqual(previous.days);
    expect(earlier.totalEggs, "the earlier half's recomputed total should equal the server's own previous-week total").toBe(previous.totalEggs);
    expect(earlier.periodHenDayPct, "the earlier half's recomputed rate should equal the server's own previous-week rate").toBe(previous.periodHenDayPct);

    expect(later.days, "the later half's own days should equal the server's current-week days").toEqual(current.days);
    expect(later.totalEggs, "the later half's recomputed total should equal the server's own current-week total").toBe(current.totalEggs);
    expect(later.periodHenDayPct, "the later half's recomputed rate should equal the server's own current-week rate").toBe(current.periodHenDayPct);
  });
});

test.describe("Dashboard Lay rate flock scope", { tag: "@phone" }, () => {
  test("the selector, the picker dialog and the 14-day strip stay usable at 390", async ({ page, signIn }) => {
    await signIn(owner());
    await page.goto("/");
    await expect(strip(page)).toBeVisible();

    const card = layRateCard(page);
    const cardBox = await card.boundingBox();
    expect(cardBox, "the Lay rate card should have a box at 390").not.toBeNull();

    // Nothing in the card may reach past the viewport. The strip is the usual
    // offender — fourteen slots plus a week break in 390px — and #814's own
    // decision is that all of them stay in ONE row rather than wrapping.
    const overflow = await card.evaluate((el) => ({
      scrollWidth: el.scrollWidth, clientWidth: el.clientWidth,
    }));
    expect(overflow.scrollWidth, "the Lay rate card scrolls sideways at 390").toBeLessThanOrEqual(overflow.clientWidth + 1);

    const rows = await strip(page).locator(".daystrip .day").evaluateAll(
      (els) => [...new Set(els.map((e) => Math.round((e as HTMLElement).getBoundingClientRect().top)))]);
    expect(rows, `the strip wrapped into ${rows.length} rows at 390`).toHaveLength(1);

    // The single selector is on screen. TARGET SIZE IS DELIBERATELY NOT
    // ASSERTED HERE: phone.spec.ts's own 44px-floor test already walks every
    // visible link/button in `main` against it, and a second copy here could
    // only ever agree with it or contradict it.
    await expect(selectorButton(page)).toBeVisible();

    // And the picker actually opens here, full width, with its own controls
    // reachable — which the box checks above do not prove: a control laid
    // out correctly behind an overlapping card would pass every box check
    // and open nothing.
    await openPicker(page);
    await expect(page.getByRole("searchbox", { name: tEn("dashboard:searchAccessibleFlocksLabel") })).toBeVisible();
    await expect(pinnedAllFlocksChoice(page)).toBeVisible();
  });
});
