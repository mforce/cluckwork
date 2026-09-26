// tools/simulation/ui/playwright.screenshots.config.ts — #549.
//
// Captures the README's screenshots from the built SPA over the simulation
// and demo farms. This separate config keeps `npm test` from rewriting the
// committed PNGs in docs/images/ on every smoke run.
//
// NOT a visual-regression suite. Nothing here asserts on pixels; rendering is
// not byte-deterministic across fonts and antialiasing, so a byte-diff gate
// would flake. The images are documentation artefacts with a manual refresh:
// see specs-screenshots/screenshots.spec.ts for the staleness contract.

import { defineConfig, devices } from "@playwright/test";
import { resolveBrowser } from "./src/browser";
import { BASE_URL } from "./src/env";

const { executablePath } = resolveBrowser();

export default defineConfig({
  testDir: "./specs-screenshots",
  // #664 added palettes.spec.ts beside this file's spec, writing an
  // uncommitted capture through its own sibling config
  // (playwright.palettes.config.ts) — matched here so `npm run screenshots`
  // does not also run it.
  testMatch: /screenshots\.spec\.ts$/,

  // Same reasoning as the smoke config: the stack is owned by reset.sh, not by
  // Playwright. No webServer.
  globalSetup: "./src/preflight.ts",

  // One worker: the captures sign three personas in and out of one seeded
  // database, and a second worker would interleave those sessions.
  workers: 1,
  fullyParallel: false,
  retries: 0,
  forbidOnly: true,

  reporter: [["list"]],

  timeout: 60_000,
  expect: { timeout: 15_000 },

  use: {
    baseURL: BASE_URL,
    trace: "retain-on-failure",
    video: "off",
    ignoreHTTPSErrors: true,
  },

  projects: [
    {
      name: "chromium",
      // The dashboard uses its own taller viewport below.
      grepInvert: /dashboard — the morning view/,
      use: {
        ...devices["Desktop Chrome"],

        // AFTER the spread, not before, and not in the top-level `use`:
        // `devices["Desktop Chrome"]` carries its own 1280x720 @1, and a
        // project's `use` beats the top-level one. Setting the frame above the
        // spread silently captures at the device's size instead of this one.
        //
        // Daily entry uses this height; Reports and Sales set their own
        // viewports in the spec to fit their content.
        //
        // Scale 1 keeps the committed PNGs at their CSS pixel size.
        viewport: { width: 1280, height: 1000 },
        deviceScaleFactor: 1,

        launchOptions: executablePath ? { executablePath } : {},
      },
    },
    {
      name: "chromium-dashboard",
      // #920: Morning collection, stock, recent orders, and the Lay rate
      // KPI all fit in this 1:1 frame after the dashboard redesign.
      grep: /dashboard — the morning view/,
      use: {
        ...devices["Desktop Chrome"],
        viewport: { width: 1280, height: 1300 },
        deviceScaleFactor: 1,
        launchOptions: executablePath ? { executablePath } : {},
      },
    },
  ],
});
