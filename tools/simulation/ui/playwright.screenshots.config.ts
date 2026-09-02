// tools/simulation/ui/playwright.screenshots.config.ts — #549.
//
// Captures the README's screenshots from the REAL built SPA over the same
// `seed --profile simulation` fixture every other spec here uses. Deliberately
// a THIRD config rather than a spec in `specs/`: this run WRITES FILES into the
// repo (docs/images/), and a capture that rides along with `npm test` would
// rewrite committed images on every smoke run — turning an unrelated green run
// into a dirty working tree.
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
      use: {
        ...devices["Desktop Chrome"],

        // AFTER the spread, not before, and not in the top-level `use`:
        // `devices["Desktop Chrome"]` carries its own 1280x720 @1, and a
        // project's `use` beats the top-level one. Setting the frame above the
        // spread silently captures at the device's size instead of this one.
        //
        // A fixed frame means a refreshed image differs from its predecessor
        // only where the app changed. 1280x800 is the widest that still reads
        // at GitHub's rendered README width without shrinking the type to
        // noise.
        //
        // Scale 1, measured rather than assumed: at scale 2 the three images
        // are 910 KB, at scale 1 they are 382 KB, and GitHub renders a README
        // image into roughly 890 CSS pixels — so a 1280-wide capture still has
        // ~1.4x the pixels of its slot and stays sharp on a HiDPI display.
        // Every regeneration adds its bytes to git history permanently, which
        // is what makes the 2.4x worth avoiding.
        viewport: { width: 1280, height: 800 },
        deviceScaleFactor: 1,

        launchOptions: executablePath ? { executablePath } : {},
      },
    },
  ],
});
