// tools/simulation/ui/src/browser.ts — where the Chromium binary comes from.
//
// THIS FILE EXISTS BECAUSE THERE ARE TWO CORRECT ANSWERS, on two different
// kinds of host, and each one is wrong on the other.
//
//   * The dev box this suite was written on is NixOS. Playwright's downloaded
//     browser bundles are dynamically linked against FHS paths (/usr/lib/...)
//     that do not exist there, so they fail to launch — usually with a loader
//     error that says nothing about the real cause. Probed live while building
//     this suite: the bundled Firefox does not start at all. A system Chromium
//     from the Nix store DOES, and drives the SPA correctly.
//   * A GitHub runner is the mirror image: Ubuntu, no system Chromium in the
//     image by default, and `npx playwright install chromium` is the working
//     path. An executablePath pinned to a Nix store path would simply not
//     exist there.
//
// So resolution is: an explicit override, else the first system Chromium that
// is actually present and executable, else `undefined` — which is Playwright's
// own downloaded browser. `undefined` is the RIGHT fallback, not a failure:
// it is what the CI job (#387) uses.
//
// WHAT THIS DELIBERATELY DOES NOT DO: pin a browser version. tools/simulation/
// k6/shell.nix pins k6 to an exact nixpkgs revision, and the reasoning there is
// specific — baseline.js's drain gap depends on empirically-probed VU
// scheduling, so a silent k6 upgrade would invalidate a guarantee with nothing
// noticing. Nothing here rests on Chromium's version that way: these are
// functional assertions about what a user can see and do, and a browser upgrade
// that changes those is a finding, not noise. The canary's Core Web Vitals
// (#386) are the one place where a version change WOULD move numbers, which is
// why the canary records the browser version alongside them rather than
// pretending the number is version-free.

import { existsSync, accessSync, constants } from "node:fs";

/**
 * Candidate system Chromium locations, most specific first.
 *
 * `/run/current-system/sw/bin/chromium` is the NixOS system profile — a stable
 * symlink into whatever the current generation provides, which is why it is
 * listed rather than a raw /nix/store path (those are per-build and would rot
 * on the next `nixos-rebuild`). The rest are the ordinary Linux locations, so
 * a non-Nix Linux box with Chromium installed also works without configuration.
 */
const SYSTEM_CHROMIUM_CANDIDATES = [
  "/run/current-system/sw/bin/chromium",
  "/usr/bin/chromium",
  "/usr/bin/chromium-browser",
  "/usr/bin/google-chrome",
];

/** The env var an operator sets to force one specific binary. */
export const CHROMIUM_PATH_ENV = "CLUCKWORK_E2E_CHROMIUM";

/** The env var that forces Playwright's own downloaded browser, ignoring any system one. */
export const BUNDLED_ONLY_ENV = "CLUCKWORK_E2E_BUNDLED_BROWSER";

function isExecutableFile(path: string): boolean {
  if (!existsSync(path)) return false;
  try {
    accessSync(path, constants.X_OK);
    return true;
  } catch {
    return false;
  }
}

export interface BrowserResolution {
  /** Passed straight to Playwright's `launchOptions.executablePath`. `undefined` = use the bundled download. */
  executablePath: string | undefined;
  /** How it was chosen — reported at run start so a surprising result is visible, not inferred. */
  source: "override" | "system" | "bundled";
}

/**
 * Resolve which Chromium to launch.
 *
 * An explicit `CLUCKWORK_E2E_CHROMIUM` that does not point at an executable is
 * a HARD FAILURE, not a silent fall-through to the bundled browser: somebody
 * asked for a specific binary, and quietly running a different one would make
 * every result attributable to the wrong thing. The two implicit paths (system,
 * bundled) are allowed to fall through, because neither was named by a human.
 */
export function resolveBrowser(env: NodeJS.ProcessEnv = process.env): BrowserResolution {
  const override = env[CHROMIUM_PATH_ENV]?.trim();
  if (override) {
    if (!isExecutableFile(override)) {
      throw new Error(
        `${CHROMIUM_PATH_ENV} is set to "${override}", which is not an executable file. `
          + `Unset it to fall back to a system Chromium or Playwright's own download.`,
      );
    }
    return { executablePath: override, source: "override" };
  }

  if (env[BUNDLED_ONLY_ENV]?.trim()) {
    return { executablePath: undefined, source: "bundled" };
  }

  const system = SYSTEM_CHROMIUM_CANDIDATES.find(isExecutableFile);
  if (system) return { executablePath: system, source: "system" };

  return { executablePath: undefined, source: "bundled" };
}

/** One line for the run header, so which binary ran is in the log, not in somebody's memory. */
export function describeBrowser(resolution: BrowserResolution): string {
  switch (resolution.source) {
    case "override":
      return `chromium: ${resolution.executablePath} (from ${CHROMIUM_PATH_ENV})`;
    case "system":
      return `chromium: ${resolution.executablePath} (system)`;
    case "bundled":
      return "chromium: Playwright's downloaded build";
  }
}
