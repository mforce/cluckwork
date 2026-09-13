// tools/simulation/ui/playwright.config.ts — #277/#385.
//
// Drives the REAL built SPA served by the sim stack's app container, against the
// #243 simulation fixture. No dev server, no mocks: the app under test is the
// same Production-config container the k6 baseline loads.

import { defineConfig, devices } from "@playwright/test";
import { resolveBrowser } from "./src/browser";
import { BASE_URL, UNDER_LOAD } from "./src/env";
// `import type`, so this erases completely — the config must not pull the
// fixture module (and through it the cast file and the i18n catalogs) into
// Playwright's config load.
import type { Fixtures } from "./src/fixtures";

const { executablePath } = resolveBrowser();

export default defineConfig<Fixtures>({
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

  // TWO PROJECTS, AND THE ONLY DIFFERENCE BETWEEN THEM IS THE VIEWPORT (#814).
  //
  // Same browser, same launch options, same everything else — deliberately, so
  // that a spec red in one project and green in the other is attributable to
  // WIDTH ALONE. Add a second variable here (a different browser channel, a
  // device descriptor, a longer timeout) and every phone-only failure acquires
  // a second candidate explanation, which is exactly the ambiguity the split
  // exists to remove.
  //
  // The `@phone` tag partitions the suite rather than filtering it: `grep` and
  // `grepInvert` are complements, so every test runs in exactly one project and
  // none runs in both. `npm test` runs both; nothing needs a second command.
  projects: [
    {
      name: "chromium",
      // The desktop shell owns every spec that does not ask for the other one.
      // Untagged is the default on purpose — 42 specs predate #814 and were
      // written against the sidebar.
      grepInvert: /@phone/,
      use: {
        ...devices["Desktop Chrome"],
        shellLayout: "desktop",
        // OMITTED, not set to `undefined` — Playwright's LaunchOptions declares
        // `executablePath?: string`, and under `exactOptionalPropertyTypes` an
        // explicit `undefined` is a type error rather than "use the default".
        // Omitting the key is what actually means "Playwright's own downloaded
        // build", which is the path CI (#387) takes. On NixOS the resolver finds
        // the system Chromium instead, because the downloaded binaries do not
        // launch there at all. See src/browser.ts for the full reasoning.
        launchOptions: executablePath ? { executablePath } : {},
      },
    },
    {
      name: "chromium-phone",
      grep: /@phone/,
      use: {
        ...devices["Desktop Chrome"],

        // AFTER the spread, never before it: `devices["Desktop Chrome"]`
        // carries its own 1280x720 viewport, and a later key wins. Setting the
        // frame above the spread silently runs the phone specs at 1280 — where
        // the tab bar is `display: none`, so they would fail for a reason that
        // has nothing to do with what they assert. The screenshots config
        // carries the same trap, for the same reason.
        //
        // 390x844 is a phone's CSS viewport, comfortably inside the SPA's
        // 900px breakpoint rather than sitting on it.
        viewport: { width: 390, height: 844 },

        shellLayout: "phone",

        // CONSIDERED AND REJECTED: `devices["Pixel 7"]`, or this viewport plus
        // `isMobile: true` / `hasTouch: true`. Both emulation modes were
        // measured against the live stack: the six routes the overflow walk
        // covers reported identical `scrollWidth`/`clientWidth`/`innerWidth`
        // under each, and the tab bar's own rect was identical too. So
        // `isMobile` buys nothing measurable for the layout question this
        // project asks, while adding touch-event dispatch and a mobile user
        // agent — two behavioural differences unrelated to width, which is the
        // one variable this project is meant to isolate.
        //
        // What that costs, stated rather than implied: `isMobile` is what
        // drives Chromium's mobile LAYOUT VIEWPORT sizing, and a #441-class
        // regression — a wide table inflating the layout viewport past the
        // visual one on a real device — is therefore NOT covered here. The
        // overflow walk catches the CSS-viewport half of that defect and not
        // the device half. Reopening this means re-measuring under both modes
        // and showing a difference, not flipping the flag because it sounds
        // more realistic.
        launchOptions: executablePath ? { executablePath } : {},
      },
    },
  ],
});
