// #386 — the canary-under-load UX probe.
//
// ================== WHAT THIS IS, AND WHAT IT IS NOT ==================
//
// One or two real browsers driving the SPA WHILE k6 (#243) saturates the same
// backend, measuring what k6 structurally cannot see: page load, Core Web Vitals,
// asset loading, and — the part that matters most — whether the screens are still
// CORRECT when the server is under pressure.
//
// **Playwright is never the load generator.** A canary that added meaningful load
// would perturb the very number it exists to observe. k6 stays the crowd; this is
// one customer walking through the shop while it is busy.
//
// It runs standalone too (no k6), which is what makes it useful: the same specs
// produce a quiet-system baseline, and a number is only interesting next to the
// one it should be compared with.
//
// ================== WHY THERE ARE NO LATENCY THRESHOLDS ==================
//
// This asserts CORRECTNESS and RECORDS timing. It does not fail on a slow LCP.
//
// A threshold here would be a flaky test wearing a performance gate's clothes:
// the numbers depend on the host (the sim stack is uncapped and co-located — the
// findings doc says so at length), and under load they are SUPPOSED to get worse.
// A canary that goes red when the backend is busy tells you nothing you did not
// already know, and it trains people to re-run until green.
//
// What IS asserted is the thing a degraded backend must never do: show the farm
// wrong or empty data, or an error, on a screen that has data. That assertion is
// as strict under load as off it.

import { expect, test } from "../src/fixtures";
import { owner } from "../src/cast";
import { UNDER_LOAD } from "../src/env";
import { tEn } from "../src/i18n";
import { installVitals, readVitals, type ScreenSample } from "../src/vitals";
import { daysBefore, farmToday } from "../src/farm";
import { commitNamedPicker } from "../src/dom";

type CanaryPage = import("../src/fixtures").Page;
type CanaryLocator = import("../src/fixtures").Locator;

/**
 * The screens the canary walks, each with the assertion that means "this is
 * genuinely usable" — not "it painted something".
 */
const SCREENS = [
  {
    name: "dashboard",
    path: "/",
    ready: (page: CanaryPage) => page.locator(".capture-grid"),
    // The dashboard carries no table at all since #654 — its per-flock capture
    // tiles are the rows, and they are what a lost `/api/v1/flocks` empties.
    rows: (ready: CanaryLocator) => ready.locator(".capture-tile"),
    // All three data panels, not just the flock one. A tile renders for a flock
    // that filed nothing today, so the tiles alone cannot tell a working stock
    // or sales read from a failed one.
    emptyMessageKeys: ["dashboard:noFlocksMessage", "dashboard:noStockMessage", "dashboard:noOrdersMessage"],
    // The dashboard is a pure readout — it has no control to press. Left null
    // rather than inventing an interaction (a theme toggle, say) that no farmer
    // performs on this screen and whose latency would mean nothing.
    interact: null,
    yieldsEventTiming: false,
  },
  {
    name: "stock",
    path: "/stock",
    ready: (page: CanaryPage) =>
      page.getByRole("table").filter({
        has: page.getByRole("columnheader", { name: tEn("stock:gradeHeader") }),
      }),
    rows: (ready: CanaryLocator) => ready.locator("tbody tr"),
    // `noLotsMessage` covers the INTERACTION: expanding a grade whose lots come
    // back empty renders the heading beside that message, which the grade table
    // alone would not notice (#841).
    emptyMessageKeys: ["stock:noStockMessage", "stock:noLotsMessage"],
    // Expanding a grade's lots fires a fetch and re-renders a table — the most
    // common thing anyone does on this screen.
    interact: async (page: CanaryPage) => {
      await page.getByRole("button", { name: tEn("stock:lotsButton") }).first().click();
      await expect(page.getByRole("heading", { name: tEn("stock:lotsHeading") }).first()).toBeVisible();
    },
    yieldsEventTiming: true,
  },
  {
    name: "reports",
    path: "/reports",
    ready: (page: CanaryPage) =>
      page.getByRole("table").filter({
        has: page.getByRole("columnheader", { name: tEn("reports:dateHeader") }),
      }),
    rows: (ready: CanaryLocator) => ready.locator("tbody tr"),
    emptyMessageKeys: [],
    // Widening the range is the expensive interaction on this screen and the one
    // #311 is about — 30 days rather than the max, because this measures the
    // ordinary case under load, not the boundary.
    interact: async (page: CanaryPage, timeZoneId: string) => {
      const today = farmToday(timeZoneId);
      const from = page.getByLabel(tEn("reports:fromLabel"), { exact: true });
      // ReportsPage loads production first and the three money reports after it,
      // so the table is on the glass while they are still in flight. Waiting for
      // the last of them is what stops the post-interaction check passing just
      // before a 500 lands (#841). Registered BEFORE the fill that triggers it.
      const settled = page.waitForResponse((res) => res.url().includes("/api/v1/reports/profit"));
      // CLICK the field before filling it. `fill()` alone sets the value through
      // a synthetic path that produces NO Event Timing entry, so the interaction
      // metric would stay null while the spec looked like it interacted —
      // measured, not assumed. A user clicks the field and then types, so the
      // click is also the faithful version of this interaction.
      await from.click();
      await from.fill(daysBefore(today, 30));
      await settled;
      // A POSITIVE check, because the money section renders only once all three
      // of its reads land. Asserting the ABSENCE of an error here would pass in
      // the gap between a 500 arriving and React rendering it (#841).
      await expect(page.getByText(tEn("reports:profitRowLabel"))).toBeVisible();
      // The production table alone does not prove production: ReportQueries
      // emits a row per calendar day in the range whether or not anything was
      // recorded, so a report of nothing but zeroes satisfies a row count. The
      // period total is the one figure that cannot be zero-filled (#841).
      await expect(
        page.locator("table.data tfoot th.num").first(),
        "the widened report totals zero eggs — a range with production in it came back empty",
      ).not.toHaveText(/^0$/);
    },
    // MEASURED, and it does not: a `<input type="date">` produces no Event
    // Timing entry for a programmatic click+fill, so this screen's interaction
    // latency is reported as null. The interaction is kept anyway — widening the
    // range is the expensive query on this screen and it is exactly what should
    // be exercised under load — but the spec does not pretend the interaction
    // metric covers it. Found by the instrument-validation assertion below,
    // which is the point of having one.
    yieldsEventTiming: false,
  },
  {
    name: "history",
    path: "/history",
    ready: (page: CanaryPage) =>
      page.getByRole("table").filter({
        has: page.getByRole("columnheader", { name: tEn("history:dateHeader") }),
      }),
    rows: (ready: CanaryLocator) => ready.locator("tbody tr"),
    emptyMessageKeys: ["history:noEntriesMatch"],
    // Filtering to one flock — a re-query plus a re-render.
    interact: async (page: CanaryPage) => {
      // The filter is a #512 searchable picker, not a `<select>`. Committing it
      // clicks the trigger and then the option, so the Event Timing entry this
      // screen's `yieldsEventTiming` asserts on comes from real clicks.
      await commitNamedPicker(page, tEn("history:flockLabel"), "Sim House A");
    },
    yieldsEventTiming: true,
  },
] as const;



type CanaryScreen = (typeof SCREENS)[number];

/**
 * The correctness contract, asserted on load and again after the screen's
 * interaction. A saturated backend must degrade by being SLOW, never by showing
 * a farm an empty table or an error where its data should be. That confusion is
 * the failure worth catching: "no stock today" reads as a fact about the farm,
 * not as a fact about the server.
 */
async function assertScreenIsCorrect(
  screen: CanaryScreen,
  page: CanaryPage,
  ready: CanaryLocator,
  when: string,
): Promise<void> {
  await expect(
    screen.rows(ready),
    `${screen.name} rendered no rows ${when}, against a populated fixture`,
  ).not.toHaveCount(0);

  // `.error` as well as `role="alert"`: the dashboard's per-panel failure is a
  // plain `<p class="error">`, so a role query walks straight past a panel whose
  // fetch failed (#841).
  await expect(
    page.locator('.error, [role="alert"]'),
    `${screen.name} rendered an error ${when} — the backend's load became the user's problem`,
  ).toHaveCount(0);

  for (const key of screen.emptyMessageKeys) {
    await expect(
      page.getByText(tEn(key as `dashboard:${string}`)),
      `${screen.name} showed an EMPTY state ${when} against a populated fixture — `
        + `a failed fetch is being presented to the farm as "you have no data"`,
    ).toBeHidden();
  }
}

test.describe("canary", () => {
  for (const screen of SCREENS) {
    test(`${screen.name} stays correct and is measured${UNDER_LOAD ? " under load" : ""}`, async ({
      page,
      signIn,
      farm,
    }, testInfo) => {
      // Collectors must be installed before the navigation they measure.
      await installVitals(page);
      await signIn(owner());

      const startedAt = Date.now();
      await page.goto(screen.path);

      // "Usable" is the screen's own data being on the glass — not `load`, which
      // fires while the tables are still fetching. Under load this is the gap
      // that actually widens, and the one a farmer would describe as "slow".
      const ready = screen.ready(page);
      await expect(ready).toBeVisible();
      // POPULATED, not merely present. A container rendered with headers and no
      // rows is exactly what a degraded backend produces, and "the header exists"
      // was green for it (PR #391 review). Reports has no empty-state message at
      // all, so for that screen this is the ONLY thing standing between a
      // valid-but-empty report and a passing canary. Each screen names its own
      // rows because they are not all table rows (#841): the dashboard's are
      // tiles, and a hardcoded `tbody tr` counted 0 there forever.
      await assertScreenIsCorrect(screen, page, ready, "on load");
      const usableInMs = Date.now() - startedAt;

      // One representative interaction, so `longestInteractionMs` measures
      // something. Without it the metric is reported as null (see vitals.ts) —
      // deliberately, because a 0 there would read as "instant" when it means
      // "never measured".
      //
      // The screen must still be CORRECT afterwards. Every interaction here is a
      // re-query plus a re-render, and without this second check a filter that
      // came back empty or failed would leave the canary green on the strength
      // of the pre-interaction state it had already left behind (#841).
      if (screen.interact) {
        await screen.interact(page, farm.timeZoneId);
        await assertScreenIsCorrect(screen, page, ready, "after the interaction");
      }

      const vitals = await readVitals(page);

      // THE INSTRUMENT MUST HAVE WORKED. Vitals were previously only recorded,
      // never checked — so deleting every PerformanceObserver would have yielded
      // a table of nulls and a green Core-Web-Vitals canary (PR #391 review).
      // Navigation and paint timings are always available in Chromium, so their
      // absence means collection itself is broken, not that the page was fast.
      // Navigation and paint timings are always produced by Chromium for a real
      // document, so their absence means collection itself is broken.
      expect(vitals.ttfbMs, `${screen.name}: no navigation timing was collected`).not.toBeNull();
      expect(vitals.fcpMs, `${screen.name}: no paint timing was collected`).not.toBeNull();
      // LCP is deliberately NOT required. A screen with no large text block or
      // image has no LCP candidate, and the read is taken before finalisation —
      // so a null here can be a fact about the page rather than about the
      // instrument (PR #391 review round 2). Requiring it would fail a
      // legitimately sparse screen for a reason that has nothing to do with the
      // observer. The two above already catch a deleted observer.
      // Only where the interaction is known to PRODUCE an Event Timing entry.
      // Not every real interaction does — see the reports screen — and asserting
      // it there would force either a fake interaction or a deleted check. The
      // flag makes which screens are measurable an explicit, reviewable fact
      // rather than something inferred from a null in the output.
      if (screen.yieldsEventTiming) {
        expect(
          vitals.interactionCount,
          `${screen.name}: an interaction that should register did not — the Event Timing `
            + `observer is not working`,
        ).toBeGreaterThan(0);
      }

      const sample: ScreenSample = {
        screen: screen.name,
        // The completeness manifest, written into EVERY file rather than kept as
        // a constant in run-baseline.sh. Derived from SCREENS, so it cannot drift
        // from what this run actually intended to sample (PR #392 review round 3).
        expectedScreens: SCREENS.map(({ name }) => name),
        underLoad: UNDER_LOAD,
        usableInMs,
        ...vitals,
      };

      // Written per-screen, NOT accumulated into a module-level array flushed by
      // one afterAll. With CANARY_BROWSERS=2 — the mode this config exists for —
      // Playwright runs the tests in TWO worker PROCESSES. Each would get its own
      // copy of that array and its own afterAll, and both would write the same
      // path: last writer wins, silently discarding the other worker's screens,
      // with no error and exit 0 (PR #391 review, found by two reviewers).
      //
      // One file per screen has no such race — each test owns its own filename —
      // and run-baseline.sh reads the directory.
      await writeSample(sample);

      // Both halves of the owner's "where do the numbers go" decision: the raw
      // artifact rides in the Playwright report...
      await testInfo.attach(`vitals-${screen.name}.json`, {
        body: JSON.stringify(sample, null, 2),
        contentType: "application/json",
      });
      testInfo.annotations.push({
        type: "vitals",
        description:
          `${screen.name}: LCP ${vitals.lcpMs ?? "—"}ms, FCP ${vitals.fcpMs ?? "—"}ms, `
          + `TTFB ${vitals.ttfbMs ?? "—"}ms, CLS<=${vitals.clsUpperBound ?? "—"}, usable ${usableInMs}ms`,
      });
    });
  }

});

/**
 * Persist one screen's sample where run-baseline.sh can fold it into the #243
 * findings doc, so browser experience and server percentiles from the SAME run
 * sit next to each other. That adjacency is the entire reason to run them
 * concurrently; two separate documents leave the correlation to a reader who
 * does not have both open.
 *
 * The directory is cleared by run-canary.sh before a run, not here — a test
 * clearing shared output would race the other worker in exactly the way this
 * per-file scheme exists to avoid.
 *
 * `sample` already carries `underLoad`, so it is not re-added here; spreading it
 * alongside a second copy silently let one overwrite the other.
 */
async function writeSample(sample: ScreenSample): Promise<void> {
  const { writeFile, mkdir } = await import("node:fs/promises");
  const { fileURLToPath } = await import("node:url");
  const outDir = fileURLToPath(new URL("../../out/canary-vitals", import.meta.url));
  await mkdir(outDir, { recursive: true });
  await writeFile(
    `${outDir}/${sample.screen}.json`,
    JSON.stringify(sample, null, 2),
    "utf8",
  );
}
