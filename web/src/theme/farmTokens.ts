// #674 — the bridge from this app's CSS custom properties to MUI's theme.
//
// The design constraint, and the reason this file exists at all: `styles.css`
// is the SINGLE source of truth for colour. It carries the four farm palettes
// (#149/#586, keyed by `data-brand`) and both light/night modes (keyed by
// `data-theme`), and `styles.test.ts` walks it to prove every palette × mode
// pair resolves and meets contrast. A second palette declared in TypeScript
// would be a second source of truth that guard cannot see — which is exactly
// the failure mode AGENTS.md's "walk everything, exclude deliberately" rule
// exists to stop.
//
// So nothing is redeclared here. The resolved values are READ back off the
// document at runtime, which means a farm palette added to the stylesheet
// reaches MUI with no change to this file.
//
// Why concrete strings rather than handing MUI `var(--brand)` directly: MUI
// derives hover, selected and disabled states with `alpha()`, which parses the
// colour. A `var()` reference is not parseable and throws at theme-creation
// time. Resolving first is what makes the derived states work.

/**
 * The tokens MUI needs. Deliberately a small subset of the stylesheet's ~50:
 * every entry here has to map onto something the theme decides — the palette,
 * `shape`, or the `shadows` array — and a token with no mapping belongs in the
 * CSS that uses it, not in this list.
 */
export const THEME_TOKENS = [
  "--brand", "--brand-press", "--on-brand",
  "--ink", "--muted", "--canvas", "--surface", "--surface-2", "--hairline",
  "--error", "--success", "--warn", "--danger", "--on-danger",
  "--tint-ok", "--tint-warn", "--tint-danger", "--tint-accent", "--tint-muted",
  "--stat-accent",
  "--link", "--focus", "--font",
  "--r-input", "--r-panel", "--r-card", "--r-pill",
  "--shadow-bar", "--shadow-dialog",
] as const;

export type ThemeToken = (typeof THEME_TOKENS)[number];
export type TokenValues = Readonly<Record<ThemeToken, string>>;

export type ThemeMode = "light" | "dark";

/**
 * Fallbacks for the one environment where `getComputedStyle` resolves nothing:
 * jsdom, which does not load the stylesheet. They are the aubergine light
 * values, so a component test renders in the default palette rather than
 * crashing MUI with an unparseable empty string.
 *
 * These are NOT a second palette — no farm, no mode and no production path
 * reads them. `farmTokens.test.ts` pins that every key here is also declared
 * in `styles.css`, so they cannot drift into being one.
 */
const JSDOM_FALLBACK = {
  "--brand": "#4a154b", "--brand-press": "#611f69", "--on-brand": "#ffffff",
  "--ink": "#1d1d1d", "--muted": "#696969", "--canvas": "#ffffff",
  "--surface": "#ffffff", "--surface-2": "#f6f1f8", "--hairline": "#e6e6e6",
  "--error": "#cc4117", "--success": "#007a5a", "--warn": "#b8730a",
  "--danger": "#cc4117", "--on-danger": "#ffffff",
  "--tint-ok": "#e2f2ec", "--tint-warn": "#f7ecd9", "--tint-danger": "#f9e5df",
  "--tint-accent": "#f3e9f5", "--tint-muted": "#eeeaf0",
  "--stat-accent": "#4a154b",
  "--link": "#1264a3", "--focus": "#4a154b",
  "--font": "system-ui, sans-serif",
  "--r-input": "4px", "--r-panel": "8px", "--r-card": "12px", "--r-pill": "999px",
  "--shadow-bar": "0 -8px 24px rgba(29, 21, 33, 0.08)",
  "--shadow-dialog": "0 24px 64px rgba(29, 21, 33, 0.24)",
} as const satisfies TokenValues;

/**
 * Read the tokens as the document currently resolves them — that is, for
 * whatever `data-brand` and `data-theme` are set right now.
 */
export function readThemeTokens(element: HTMLElement = document.documentElement): TokenValues {
  const computed = getComputedStyle(element);
  // Seeded from the fallback and overwritten per token, rather than built from
  // `Object.fromEntries` — that returns a loose index signature and would need
  // a cast to become `TokenValues`, which is a promise to the compiler rather
  // than a proof. Spreading a complete record keeps it total by construction.
  const resolved: Record<ThemeToken, string> = { ...JSDOM_FALLBACK };
  for (const token of THEME_TOKENS) {
    const raw = computed.getPropertyValue(token).trim();
    if (raw.length > 0) resolved[token] = raw;
  }
  return resolved;
}

/** The document's current mode. `theme-init.js` always writes a concrete value. */
export function readThemeMode(element: HTMLElement = document.documentElement): ThemeMode {
  return element.dataset.theme === "dark" ? "dark" : "light";
}

/** `12px` -> `12`. MUI's `shape.borderRadius` is a number of pixels. */
export function pixelsFrom(value: string, fallback: number): number {
  const parsed = Number.parseFloat(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}
