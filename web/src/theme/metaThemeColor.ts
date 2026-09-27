import { MD_UP_QUERY } from "../lib/breakpoints";
import { readThemeTokens, type TokenValues } from "./farmTokens";

/**
 * `meta[name="theme-color"]`'s colour, by layout — #976 round 1 review:
 * measured the real rendered top-of-screen colour in a browser (1280/390,
 * light/dark), walking the DOM's ancestor chain from the top-left pixel to
 * the first opaque background.
 *
 * Desktop (>= MD_UP_QUERY, the same boundary AppLayout's sidebar/tab-bar
 * switch uses): the sidebar spans the full height from y=0, so it is what
 * meets the bar. Its `MuiDrawer` paper is `--lavender` (FarmThemeProvider.tsx)
 * — measured `#f9f0ff`/`#241c2a`, an exact match. `--brand` (mode-independent)
 * and `--surface` (farm-palette-independent) were both ruled out in round 1;
 * `--lavender` is the one token that varies on both axes AND is what's
 * actually painted there.
 *
 * Phone (< MD_UP_QUERY): the sidebar is hidden and the bottom nav sits at
 * the BOTTOM, so neither is at the top. The main content area renders with
 * no background of its own; measured colour at the top-left pixel resolved
 * all the way to `<body>` — `--canvas` (`#f3eee8`/`#302733`, exact match).
 * `--canvas` does not vary by farm palette (no palette block overrides it),
 * but it is what is actually there, which #976 weighs over palette coverage.
 *
 * Neither branch applies while the login screen (`AuthShell.tsx`) or the
 * post-login splash (`BrandSplash.tsx`) is on screen — #976 round 2:
 * measured desktop login at `#f3eee8`, the bar at `#f9f0ff`. Both fill the
 * WHOLE viewport with no sidebar at all, in `--surface-2`/`--canvas`, which
 * are the same literal value in every palette × mode block in styles.css
 * (confirmed by reading the file, not just the fallback object above), so
 * `--canvas` alone covers both without bridging a second token for it.
 */
export function themeColorFrom(tokens: TokenValues, isDesktopLayout: boolean, isAuthSurface: boolean): string {
  if (isAuthSurface) return tokens["--canvas"];
  return isDesktopLayout ? tokens["--lavender"] : tokens["--canvas"];
}

/**
 * `AuthShell.tsx` and `BrandSplash.tsx` mark themselves so this can tell
 * "the sidebar-and-shell route is showing" from "one of the two full-bleed
 * screens that never have a sidebar is showing", regardless of layout width.
 */
function isAuthSurfaceShowing(doc: Document): boolean {
  return doc.querySelector('[data-meta-surface="auth"]') !== null
    || doc.querySelector(".brand-splash-backdrop") !== null;
}

/**
 * `index.html` keeps two static tags that pick a colour by OS
 * `prefers-color-scheme` before JS runs (#974's kept first-paint path).
 * Once resolved, both are overwritten with the SAME app-theme colour rather
 * than replaced by a third element: whichever tag the browser is honouring
 * by OS preference then carries the correct content regardless of which
 * query matched, so an app theme that disagrees with the OS (or a farm
 * palette the OS knows nothing about) still paints right.
 */
export function syncThemeColorMeta(tokens: TokenValues, doc: Document = document): void {
  const view = doc.defaultView ?? window;
  // jsdom has no matchMedia (BottomNav.tsx/lib/theme.ts guard the same way);
  // MUI's own useMediaQuery falls back to "below the breakpoint" there too.
  const isDesktopLayout = typeof view.matchMedia === "function" && view.matchMedia(MD_UP_QUERY).matches;
  const color = themeColorFrom(tokens, isDesktopLayout, isAuthSurfaceShowing(doc));
  doc.querySelectorAll<HTMLMetaElement>('meta[name="theme-color"]').forEach((meta) => {
    meta.content = color;
  });
}

/**
 * `FarmThemeProvider` sits OUTSIDE the router (`App.tsx`, so a farm palette
 * themes the login screen too), so it cannot see a route change or an
 * overlay like `BrandSplash` mount/unmount — #976 round 2. Those two call
 * this directly, on their own mount and unmount, reading live tokens rather
 * than relying on whatever the provider last computed.
 */
export function resyncThemeColorMeta(doc: Document = document): void {
  syncThemeColorMeta(readThemeTokens(), doc);
}
