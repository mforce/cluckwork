// tools/simulation/ui/playwright.canary.config.ts — #386, the canary-under-load
// probe. Separate from the smoke suite's config on purpose.
//
// The smoke suite (playwright.config.ts) runs `workers: 1` because its specs
// share one mutable fixture. The canary is the opposite case: it is READ-ONLY,
// and running one or two browsers at once is the point — a single browser cannot
// show whether contention between clients degrades the experience.
//
// Two is the ceiling, and it is a deliberate ceiling. More browsers would stop
// being a canary and start being load, which would perturb the measurement it
// exists to take. k6 is the crowd (#243).

import { defineConfig, devices } from "@playwright/test";
import { resolveBrowser } from "./src/browser";
import { BASE_URL } from "./src/env";

const { executablePath } = resolveBrowser();

/** 1 or 2. Anything higher is rejected rather than silently clamped. */
const requested = Number(process.env.CANARY_BROWSERS ?? "1");
if (!Number.isInteger(requested) || requested < 1 || requested > 2) {
  throw new Error(
    `CANARY_BROWSERS must be 1 or 2 (got "${process.env.CANARY_BROWSERS}"). `
      + `The canary measures the experience under load; past two browsers it becomes load.`,
  );
}

export default defineConfig({
  testDir: "./specs-canary",
  globalSetup: "./src/preflight.ts",

  workers: requested,
  fullyParallel: true,

  // Zero retries, and here it is load-bearing rather than a preference: under
  // load, "it passed on the second attempt" IS the degradation being measured.
  // A retry would convert the finding into a green tick.
  retries: 0,
  forbidOnly: true,

  reporter: [
    ["list"],
    ["html", { outputFolder: "playwright-report-canary", open: "never" }],
  ],

  // Generous, because the backend is expected to be saturated. Still finite: a
  // request that never returns is a bug, not slowness.
  timeout: 180_000,
  expect: { timeout: 60_000 },

  use: {
    baseURL: BASE_URL,
    // Always trace the canary — unlike the smoke suite, a PASSING canary run is
    // itself the artifact worth keeping, since the timings are the deliverable.
    trace: "on",
    screenshot: "only-on-failure",
    video: "retain-on-failure",
    ignoreHTTPSErrors: true,
  },

  projects: [
    {
      name: "chromium",
      use: {
        ...devices["Desktop Chrome"],
        ...(executablePath ? { launchOptions: { executablePath } } : {}),
      },
    },
  ],
});
