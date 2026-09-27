import type { TokenValues } from "./farmTokens";

/**
 * `meta[name="theme-color"]`'s colour: `--lavender`, the same token
 * `MuiDrawer`'s paper override (FarmThemeProvider.tsx) paints the persistent
 * nav shell with. Unlike `--surface` (identical across farm palettes) or
 * `--brand` (identical between light and dark), `--lavender` varies with
 * BOTH axes, so it carries the resolved farm palette and light/night into
 * the browser chrome the way #974 asks for.
 */
export function themeColorFrom(tokens: TokenValues): string {
  return tokens["--lavender"];
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
  const color = themeColorFrom(tokens);
  doc.querySelectorAll<HTMLMetaElement>('meta[name="theme-color"]').forEach((meta) => {
    meta.content = color;
  });
}
