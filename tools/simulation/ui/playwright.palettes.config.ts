// tools/simulation/ui/playwright.palettes.config.ts — #664.
//
// A sibling of playwright.screenshots.config.ts, not a shared config: that one
// writes committed documentation artefacts into docs/images/, one worker at a
// time, matched to exactly screenshots.spec.ts. This one writes an
// UNCOMMITTED visual-review capture into out-palettes/ (see .gitignore) and is
// matched to exactly palettes.spec.ts, so `npm run screenshots` and
// `npm run screenshots:palettes` never pick up each other's spec file.
//
// Same reasoning as the sibling config on every other point: real built SPA,
// same fixture, no webServer (reset.sh owns the stack), one worker (multiple
// personas signing in and out of one seeded database), 1280x800 @1 so a
// refreshed image differs from its predecessor only where the app changed.

import { defineConfig, devices } from "@playwright/test";
import { resolveBrowser } from "./src/browser";
import { BASE_URL } from "./src/env";

const { executablePath } = resolveBrowser();

export default defineConfig({
  testDir: "./specs-screenshots",
  testMatch: /palettes\.spec\.ts$/,

  globalSetup: "./src/preflight.ts",

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
        viewport: { width: 1280, height: 800 },
        deviceScaleFactor: 1,
        launchOptions: executablePath ? { executablePath } : {},
      },
    },
  ],
});
