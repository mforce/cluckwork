// tools/simulation/ui/playwright.config.ts — #277/#385.
//
// Drives the REAL built SPA served by the sim stack's app container, against the
// #243 simulation fixture. No dev server, no mocks: the app under test is the
// same Production-config container the k6 baseline loads.

import { defineConfig, devices } from "@playwright/test";
import { resolveBrowser } from "./src/browser";
import { BASE_URL, UNDER_LOAD } from "./src/env";

const { executablePath } = resolveBrowser();

export default defineConfig({
  testDir: "./specs",

  // NO webServer. Playwright will not start anything: the stack is docker
  // compose under the `cluckwork-sim` project, owned by reset.sh, and a
  // Playwright-managed lifecycle here would be a second thing that believes it
  // owns that stack — including, eventually, one that tears it down.
  globalSetup: "./src/preflight.ts",

  // Serial by default. These specs share ONE seeded database, and the write
  // flows (Manager submit, Sales confirm + payment) mutate it. Parallel workers
  // against shared mutable state produce failures that reproduce ~40% of the
  // time, which is worse than a slower suite: an intermittent red gets re-run
  // until it is green and then believed. The canary (#386) opts back into
  // concurrency deliberately, because measuring under contention is its job.
  workers: 1,
  fullyParallel: false,

  // A flake here is a finding, not something to paper over. Retries would hide
  // exactly the intermittent failures this suite exists to surface — most of all
  // under the canary, where "it passed on the second try" IS the degradation
  // being measured. Zero, in both modes.
  retries: 0,

  // Refuse to pass a run that contains a stray `test.only` — the classic way a
  // suite silently shrinks to one spec and stays green forever.
  forbidOnly: true,

  reporter: [
    ["list"],
    // The raw artifact half of the CWV decision (#386): traces and timings live
    // here; the summary is folded into the #243 findings doc separately.
    ["html", { outputFolder: "playwright-report", open: "never" }],
  ],

  // Under load the backend is saturated ON PURPOSE, so the same timeout would
  // convert measured slowness into functional failures and drown the real
  // signal. Raised, not removed — a request that never returns is still a bug.
  timeout: UNDER_LOAD ? 120_000 : 45_000,
  expect: { timeout: UNDER_LOAD ? 30_000 : 10_000 },

  use: {
    baseURL: BASE_URL,

    // Keep the evidence for a failure and nothing for a pass — a green run
    // writing traces for 30 specs fills a disk to prove nothing.
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    video: "retain-on-failure",

    // The stack is plain HTTP on loopback. It has no certificate to validate,
    // and the refresh cookie is `Secure` — which the browser accepts on
    // localhost, so the session works here exactly as it does over TLS.
    ignoreHTTPSErrors: true,
  },

  projects: [
    {
      name: "chromium",
      use: {
        ...devices["Desktop Chrome"],
        // `undefined` = Playwright's own downloaded build, which is what CI
        // (#387) uses. On NixOS this resolves to the system Chromium instead,
        // because the downloaded binaries do not launch there at all. See
        // src/browser.ts for the full reasoning.
        launchOptions: { executablePath },
      },
    },
  ],
});
