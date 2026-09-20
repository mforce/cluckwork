// #916 — the Dashboard Lay rate card's flock scope, and the partial-day scale
// it falls back to.
//
// Both halves are about the SIMULATION fixture specifically, which is why they
// live here rather than in web/'s unit suite. `default-farm` seeds ~100 catalog
// flocks that never file, so no day in the fortnight window is recorded by
// every flock — the exact state where the strip used to collapse every bar to
// the 2% floor and show Peak "—". A unit test can construct that shape; only
// this fixture proves the SERVER still produces it, and that the card scales to
// the largest partial day when it does.
//
// The scope control is proved against the network, not against the figures
// alone: the point of #916's API half is that the browser never filters an
// unscoped payload itself, so a spec that only watched the rendered numbers
// would stay green if the filtering moved back into the client.

import { test, expect, type Page } from "../src/fixtures";
import { owner, restrictedWorker } from "../src/cast";
import { signInForToken, apiGet } from "../src/api";
import { commitNamedPicker } from "../src/dom";
import { daysBefore, farmToday } from "../src/farm";
import { tEn } from "../src/i18n";

// The fixture's first flock — the one SimulationDataSeeder restricts
// `restrictedWorker()` to, so the same name serves both halves of this spec.
const FIXTURE_FLOCK = "Sim House A";

/** The Lay rate card's strip, and the figures rendered above it. */
function strip(page: Page) {
  return page.locator("figure.trend");
}

/** The `All flocks` toggle that returns the card to farm-wide. */
function allFlocksButton(page: Page) {
  return page.getByRole("button", { name: tEn("dashboard:allFlocksOption"), exact: true });
}

/**
 * The scope picker's CLOSED state: a read-only MUI field labelled
 * `dashboard:flockScopeLabel` whose value is the current scope's name. Its
 * absence is what the one-flock case asserts, so it is a locator rather than
 * an inline expression in one test.
 */
function scopeField(page: Page) {
  return page.getByRole("textbox", { name: tEn("dashboard:flockScopeLabel") });
}

/** Every bar's inline height, in slot order. An unrecorded day contributes nothing. */
async function barHeights(page: Page): Promise<string[]> {
  return strip(page).locator(".daystrip .day i").evaluateAll(
    (els) => els.map((e) => (e as HTMLElement).style.height));
}

test.describe("Dashboard Lay rate flock scope", () => {
  test("farm-wide, no complete day: the strip scales to the largest partial day", async ({ page, signIn }) => {
    await signIn(owner());
    await page.goto("/");
    await expect(strip(page)).toBeVisible();

    // Farm-wide is the default, and it is a PRESSED toggle rather than merely
    // the absence of a selection — a reader has to be able to see which scope
    // produced the figures.
    await expect(allFlocksButton(page)).toHaveAttribute("aria-pressed", "true");
    await expect(scopeField(page)).toHaveValue(tEn("dashboard:allFlocksOption"));

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
    await expect(peak, "the partial-day peak should be a figure, not an em dash").not.toHaveText("—");

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
  });

  test("the picker searches, and choosing a flock rescopes the card server-side", async ({ page, signIn }) => {
    await signIn(owner());
    await page.goto("/");
    await expect(strip(page)).toBeVisible();

    const before = {
      caption: await strip(page).locator(".trend-scale > span").first().innerText(),
      peak: await strip(page).locator(".trend-peak").innerText(),
      heights: await barHeights(page),
    };

    // The two production reads must carry the chosen flock. This is the whole
    // API half of #916: a client-side filter of the farm-wide payload would
    // render plausible bars and could not produce a scoped hen-day rate.
    const scopedReports: string[] = [];
    page.on("request", (r) => {
      const url = r.url();
      if (url.includes("/reports/production") && url.includes("flockId=")) scopedReports.push(url);
    });

    // Opens the closed field, types, and commits the sole match — the same
    // three steps a user takes, and the helper every other picker spec uses.
    const flockId = await commitNamedPicker(page, tEn("dashboard:flockScopeLabel"), FIXTURE_FLOCK);

    // The commit closes the search (matches every other FlockPicker caller in
    // the app), so the committed value shows on the closed, read-only field.
    await expect(scopeField(page)).toHaveValue(FIXTURE_FLOCK);
    await expect(
      allFlocksButton(page),
      "All flocks should un-press once a single flock owns the card",
    ).toHaveAttribute("aria-pressed", "false");

    // One flock's own days ARE complete days — it is the only flock expected to
    // file — so the card leaves the partial-day fallback entirely. That is a
    // stronger claim than "the numbers moved": it can only happen if the
    // SERVER recomputed completeness against a one-flock expectation.
    await expect(strip(page).locator(".trend-scale > span").first())
      .toHaveText(tEn("dashboard:trendScaleTitle"));
    await expect(strip(page).locator(".trend-peak")).not.toHaveText("—");
    expect(await barHeights(page), "the scoped strip should not be the farm-wide one")
      .not.toEqual(before.heights);

    expect(
      scopedReports.filter((u) => u.includes(`flockId=${flockId}`)).length,
      `both production reads should carry flockId=${flockId}; saw ${JSON.stringify(scopedReports)}`,
    ).toBeGreaterThanOrEqual(2);

    // And back. The return path is its own assertion because the picker retains
    // a committed entity: clearing the card's scope has to clear that too, or
    // the field goes on naming a flock the figures no longer describe.
    const farmWideReads = page.waitForRequest((r) =>
      r.url().includes("/reports/production") && !r.url().includes("flockId="));
    await allFlocksButton(page).click();
    await farmWideReads;

    await expect(allFlocksButton(page)).toHaveAttribute("aria-pressed", "true");
    await expect(scopeField(page)).toHaveValue(tEn("dashboard:allFlocksOption"));
    await expect(strip(page).locator(".trend-scale > span").first()).toHaveText(before.caption);
    await expect(strip(page).locator(".trend-peak")).toHaveText(before.peak);
  });

  test("one accessible flock: its name, with no scope control at all", async ({ page, signIn }) => {
    // `restrictedWorker()` is the fixture's single-flock reader. #613's
    // structural flock-scope query filter narrows the READ as well as the
    // write, so `GET /flocks` answers this persona with exactly one row — which
    // is what the card keys the plain-text rendering on. (src/cast.ts's own note
    // still describes the pre-#613 behaviour, where every read answered as an
    // unrestricted worker's did.)
    await signIn(restrictedWorker());
    await page.goto("/");
    await expect(strip(page)).toBeVisible();

    const card = page.locator(".MuiCard-root").filter({ has: strip(page) });
    await expect(
      card.getByText(FIXTURE_FLOCK, { exact: true }),
      "the one accessible flock should be named in the card",
    ).toBeVisible();

    // No choice to offer, so no control is offered: neither the toggle nor the
    // picker field (whose chevron is what signals "this reopens a search").
    await expect(allFlocksButton(page)).toHaveCount(0);
    await expect(scopeField(page)).toHaveCount(0);
    await expect(page.getByRole("combobox", { name: tEn("dashboard:flockScopeLabel") })).toHaveCount(0);

    // The figures still arrive, and by the SAME scoped path a picked flock
    // takes — never a second "just show everything" branch.
    await expect(strip(page).locator(".daystrip .day").first()).toBeVisible();
  });

  test("the only-one view and a picked single flock read the same report", async ({ farm }) => {
    // SELECTION.md: "The selected-single and only-one views must show identical
    // data for the same flock." Both cards render one production report, so the
    // claim is really about the SERVER: the same flock, asked for by two
    // principals with very different reach, must answer identically. Checked
    // here rather than through two sign-ins in one browser context, which the
    // `signIn` fixture does not support.
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
});

test.describe("Dashboard Lay rate flock scope", { tag: "@phone" }, () => {
  test("the scope control and the 14-day strip stay usable at 390", async ({ page, signIn }) => {
    await signIn(owner());
    await page.goto("/");
    await expect(strip(page)).toBeVisible();

    const card = page.locator(".MuiCard-root").filter({ has: strip(page) });
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

    // Both scope controls are on screen and not stacked on top of each other.
    // TARGET SIZE IS DELIBERATELY NOT ASSERTED HERE: phone.spec.ts's "dashboard
    // actions meet the 44px target floor" already walks every visible link and
    // button in `main` against the repo's 44px floor, and a second copy of that
    // rule here could only ever agree with it or contradict it.
    const toggle = await allFlocksButton(page).boundingBox();
    const field = await scopeField(page).boundingBox();
    expect(toggle, "the All flocks toggle is not on screen at 390").not.toBeNull();
    expect(field, "the flock scope field is not on screen at 390").not.toBeNull();
    expect(
      toggle!.y + toggle!.height <= field!.y || toggle!.x + toggle!.width <= field!.x,
      "the two scope controls overlap at 390",
    ).toBe(true);

    // And the picker actually opens here, which the measurements above do not
    // prove: a control laid out correctly behind an overlapping card would pass
    // every box check and open nothing.
    await scopeField(page).click();
    await expect(page.getByRole("combobox", { name: tEn("dashboard:flockScopeLabel") })).toBeVisible();
  });
});
