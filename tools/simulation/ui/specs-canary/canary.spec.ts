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
import { selectOptionContaining } from "../src/dom";

type CanaryPage = import("../src/fixtures").Page;

/**
 * The screens the canary walks, each with the assertion that means "this is
 * genuinely usable" — not "it painted something".
 */
const SCREENS = [
  {
    name: "dashboard",
    path: "/",
    ready: (page: CanaryPage) =>
      page.getByRole("table").filter({
        has: page.getByRole("columnheader", { name: tEn("dashboard:flockHeader") }),
      }),
    emptyMessageKey: "dashboard:noFlocksMessage",
    // The dashboard is a pure readout — it has no control to press. Left null
    // rather than inventing an interaction (a theme toggle, say) that no farmer
    // performs on this screen and whose latency would mean nothing.
    interact: null,
  },
  {
    name: "stock",
    path: "/stock",
    ready: (page: CanaryPage) =>
      page.getByRole("table").filter({
        has: page.getByRole("columnheader", { name: tEn("stock:gradeHeader") }),
      }),
    emptyMessageKey: "stock:noStockMessage",
    // Expanding a grade's lots fires a fetch and re-renders a table — the most
    // common thing anyone does on this screen.
    interact: async (page: CanaryPage) => {
      await page.getByRole("button", { name: tEn("stock:lotsButton") }).first().click();
      await expect(page.getByRole("heading", { name: tEn("stock:lotsHeading") }).first()).toBeVisible();
    },
  },
  {
    name: "reports",
    path: "/reports",
    ready: (page: CanaryPage) =>
      page.getByRole("table").filter({
        has: page.getByRole("columnheader", { name: tEn("reports:dateHeader") }),
      }),
    emptyMessageKey: null,
    // Widening the range is the expensive interaction on this screen and the one
    // #311 is about — 30 days rather than the max, because this measures the
    // ordinary case under load, not the boundary.
    interact: async (page: CanaryPage, timeZoneId: string) => {
      const today = farmToday(timeZoneId);
      const from = page.getByLabel(tEn("reports:fromLabel"), { exact: true });
      // CLICK the field before filling it. `fill()` alone sets the value through
      // a synthetic path that produces NO Event Timing entry, so the interaction
      // metric would stay null while the spec looked like it interacted —
      // measured, not assumed. A user clicks the field and then types, so the
      // click is also the faithful version of this interaction.
      await from.click();
      await from.fill(daysBefore(today, 30));
      await expect(page.getByRole("alert")).toBeHidden();
    },
  },
  {
    name: "history",
    path: "/history",
    ready: (page: CanaryPage) =>
      page.getByRole("table").filter({
        has: page.getByRole("columnheader", { name: tEn("history:dateHeader") }),
      }),
    emptyMessageKey: "history:noEntriesMatch",
    // Filtering to one flock — a re-query plus a re-render.
    interact: async (page: CanaryPage) => {
      const flock = page.getByLabel(tEn("history:flockLabel"));
      // Same reason as the reports field: `selectOption()` on its own emits no
      // Event Timing entry. Opening the control first is what a user does.
      await flock.click();
      await selectOptionContaining(flock, "Sim House A");
    },
  },
] as const;

const samples: ScreenSample[] = [];

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
      await expect(screen.ready(page)).toBeVisible();
      const usableInMs = Date.now() - startedAt;

      // THE CORRECTNESS ASSERTIONS — as strict under load as off it.
      //
      // A saturated backend must degrade by being SLOW, never by showing a farm
      // an empty table or an error where its data should be. That confusion is
      // the failure worth catching: "no stock today" reads as a fact about the
      // farm, not as a fact about the server.
      await expect(
        page.getByRole("alert"),
        `${screen.name} rendered an error — the backend's load became the user's problem`,
      ).toBeHidden();
      if (screen.emptyMessageKey) {
        await expect(
          page.getByText(tEn(screen.emptyMessageKey as `dashboard:${string}`)),
          `${screen.name} showed its EMPTY state against a populated fixture — under load, `
            + `a failed fetch is being presented to the farm as "you have no data"`,
        ).toBeHidden();
      }

      // One representative interaction, so `longestInteractionMs` measures
      // something. Without it the metric is reported as null (see vitals.ts) —
      // deliberately, because a 0 there would read as "instant" when it means
      // "never measured".
      if (screen.interact) await screen.interact(page, farm.timeZoneId);

      const vitals = await readVitals(page);
      const sample: ScreenSample = {
        screen: screen.name,
        underLoad: UNDER_LOAD,
        usableInMs,
        ...vitals,
      };
      samples.push(sample);

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

  // ...and the summary is written where run-canary.sh can fold it into the #243
  // findings doc, so browser experience and server percentiles from the SAME run
  // sit next to each other. That adjacency is the entire reason to run them
  // concurrently; two separate documents would leave the correlation to a reader
  // who does not have both open.
  test.afterAll(async () => {
    const { writeFile, mkdir } = await import("node:fs/promises");
    const { fileURLToPath } = await import("node:url");
    const outDir = fileURLToPath(new URL("../../out", import.meta.url));
    await mkdir(outDir, { recursive: true });
    await writeFile(
      `${outDir}/canary-vitals.json`,
      JSON.stringify({ underLoad: UNDER_LOAD, samples }, null, 2),
      "utf8",
    );
  });
});
