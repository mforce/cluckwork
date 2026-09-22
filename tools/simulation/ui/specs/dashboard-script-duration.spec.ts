// tools/simulation/ui/specs/dashboard-script-duration.spec.ts — #825.
//
// Measures Dashboard's script execution cost on the real seeded stack. #674's
// decision record found bundle bytes flat under CPU throttling but script
// time rising with MUI component count (+27% for eleven components) — the
// cost a byte budget cannot see. This spec is that second, script-duration
// signal, run continuously instead of by hand in a throwaway branch.
//
// CDP Performance.getMetrics, same domain ax.ts already uses for the
// accessibility tree (see that file for why CDP over a higher-level API).
// Measured around a CLIENT-SIDE route change (a nav Link click), not
// page.goto: the access token lives in a module-level variable (#145,
// fixtures.ts), so a full browser navigation would sign the persona out
// before the second run. Client-side navigation also isolates Dashboard's own
// mount cost from the app shell's one-time bootstrap.
//
// This is a MEASUREMENT with a wide regression tripwire, not a tight budget.
// A CI runner's CPU is shared and variable run to run, so a tight ceiling
// here would be exactly the "wrong guard that reads as safety" AGENTS.md
// warns against for every other guard in this repo. The tripwire is sized to
// catch a genuine multi-fold regression — the record names Autocomplete and a
// data grid as the untested cases — while absorbing ordinary CI noise.

import { test, expect } from "../src/fixtures";
import { owner } from "../src/cast";

const RUNS = 5;

// #674 measured 107ms -> 136ms (bare -> 11 MUI components) at 6x CPU
// throttle on a real device. This spec runs unthrottled on a shared CI
// runner, so its absolute numbers are not comparable to that record, only to
// this spec's own history. Set from the one real measurement taken while
// writing this spec, against an isolated build of the seeded stack
// (median 124.5ms, runs 56/122/124/127/141ms), times roughly 4.8 — CI
// runners vary more than a single local run can show, so this is a
// deliberately wide margin, not a tight budget. Tighten only against a
// measured trend recorded in the PR that does it, per #825.
const SCRIPT_DURATION_CEILING_MS = 600;

test.describe("Dashboard script duration", () => {
  test("median script execution time across N Dashboard mounts stays under the tripwire", async ({ page, signIn, nav }) => {
    await signIn(owner());

    const durationsMs: number[] = [];
    for (let i = 0; i < RUNS; i++) {
      // Leave Dashboard so the next click is a real mount, not a no-op nav.
      await nav.link("nav:flocks").click();
      await page.waitForURL("/flocks");

      const cdp = await page.context().newCDPSession(page);
      await cdp.send("Performance.enable");
      const before = await cdp.send("Performance.getMetrics");

      await nav.link("nav:dashboard").click();
      // The Lay rate card is the last of Dashboard's four panels to settle
      // (#916's scoped trend read); its strip replacing the LinearProgress is
      // "the last Dashboard panel visible" the runtime baseline record times to.
      await expect(page.locator("figure.trend")).toBeVisible();

      const after = await cdp.send("Performance.getMetrics");
      await cdp.detach();

      const scriptBefore = before.metrics.find((m) => m.name === "ScriptDuration")?.value ?? 0;
      const scriptAfter = after.metrics.find((m) => m.name === "ScriptDuration")?.value ?? 0;
      durationsMs.push((scriptAfter - scriptBefore) * 1000);
    }

    const sorted = [...durationsMs].sort((a, b) => a - b);
    // RUNS is a positive compile-time constant, so `sorted` is never empty.
    const median = sorted[Math.floor(sorted.length / 2)] ?? 0;
    console.log(
      `[dashboard script duration] runs=${sorted.map((d) => Math.round(d)).join(",")}ms medianMs=${median.toFixed(1)}`,
    );

    expect(
      median,
      `Dashboard script time regressed: ${median.toFixed(1)}ms median over ${RUNS} runs, ` +
        `ceiling ${SCRIPT_DURATION_CEILING_MS}ms. Re-measure with #674's throttled-device method ` +
        "before tightening this tripwire. If this is real growth from a new component " +
        "(Autocomplete, a data grid), name it in the PR body per #825.",
    ).toBeLessThan(SCRIPT_DURATION_CEILING_MS);
  });
});
