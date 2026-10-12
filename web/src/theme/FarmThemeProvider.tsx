import { useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import createCache from "@emotion/cache";
import { CacheProvider } from "@emotion/react";
import { ThemeProvider, createTheme, lighten } from "@mui/material/styles";
import type { Shadows, Theme } from "@mui/material/styles";
import {
  pixelsFrom, readThemeMode, readThemeTokens,
  type ThemeMode, type ThemeToken, type TokenValues,
} from "./farmTokens";
import { syncThemeColorMeta } from "./metaThemeColor";
import { COLOR_TRANSITION_MS, EASE, farmThemeOverrides } from "./farmThemeOverrides";
import { watchDeviceTheme } from "../lib/theme";
import { MD_UP_QUERY } from "../lib/breakpoints";

/**
 * #651's elevation policy expressed as MUI's own array, so a component floats
 * only where MUI's default elevation lands on a mapped index.
 *
 * The indices are the defaults MUI's components pass to `Paper`, read from
 * `@mui/material@9.4.0`: `AppBar` 4, `SnackbarContent` 6, `Popover` (so `Menu`
 * and `Select`) 8, `Drawer` 16, `Dialog` 24. Everything else is flat, which
 * makes an `elevation={12}` a visible bug rather than a silent second opinion
 * about what floats.
 */
const BAR_ELEVATIONS = [4, 6];
const DIALOG_ELEVATIONS = [8, 16, 24];

function elevationScale(tokens: TokenValues): Shadows {
  const scale = Array.from({ length: 25 }, (_, index) => {
    if (BAR_ELEVATIONS.includes(index)) return tokens["--shadow-bar"];
    if (DIALOG_ELEVATIONS.includes(index)) return tokens["--shadow-dialog"];
    return "none";
  });
  // MUI types the array as a 25-tuple; the construction above is that length.
  return scale as Shadows;
}

/**
 * The page's CSP style nonce, or `undefined` when the document does not carry
 * one (#873).
 *
 * Read ONCE, at module load. The server writes this meta into the same response
 * whose `Content-Security-Policy` header names the nonce, so the value cannot
 * change while the document lives and re-reading it would only invite a caller
 * to believe it can.
 *
 * `undefined` is the correct answer, not a degraded one. Vite's dev server
 * serves index.html untouched and applies no policy, so there is nothing to
 * satisfy. In a production build a missing meta means the header's nonce
 * reached nobody, and Emotion's styles are then blocked — which is the
 * fail-closed outcome #873 chose. Do not add a fallback that loosens it.
 */
const cspNonce = document
  .querySelector<HTMLMetaElement>('meta[name="csp-nonce"]')
  ?.content || undefined;

/**
 * One Emotion cache for the whole app, carrying that nonce.
 *
 * MUI styles through Emotion, which injects a `<style>` element at runtime; under
 * `style-src 'self'` with no nonce the browser drops it and no MUI styling
 * reaches the screen at all (#873, measured on the sim harness in #871).
 *
 * `prepend` puts those tags ahead of `styles.css` in `<head>`, so at equal
 * specificity this app's own stylesheet still wins — which is what keeps
 * `styles.css` the source of truth the #674 record says it is.
 */
const emotionCache = createCache({ key: "mui", nonce: cspNonce, prepend: true });

/**
 * Build MUI's theme from one already-resolved set of this app's tokens.
 *
 * Exported for the tests, which assert the mapping directly rather than
 * through a rendered component — the thing worth pinning is that a farm's
 * `--brand` reaches `palette.primary.main`, and going via the DOM would only
 * add jsdom's inability to resolve custom properties to the assertion.
 */
export function createFarmTheme(tokens: TokenValues, mode: ThemeMode): Theme {
  const radius = (token: ThemeToken, fallback: number) => pixelsFrom(tokens[token], fallback);
  const cardRadius = radius("--r-card", 12);
  const panelRadius = radius("--r-panel", 8);
  const pillRadius = radius("--r-pill", 999);
  const controlRadius = radius("--r-input", 4);
  const selectedNavFill = lighten(tokens["--brand"], 0.18);

  const base = createTheme({
    palette: {
      mode,
      primary: {
        main: tokens["--brand"],
        dark: tokens["--brand-press"],
        contrastText: tokens["--on-brand"],
      },
      // `--danger` is this app's destructive-action colour and `--error` its
      // validation colour; they are the same hue today but are separate tokens
      // on purpose. MUI has one slot, and destructive actions are the ones
      // rendered as MUI buttons, so `--danger` is the honest mapping.
      error: { main: tokens["--danger"], contrastText: tokens["--on-danger"] },
      success: { main: tokens["--success"] },
      warning: { main: tokens["--warn"] },
      // #834 — `--link` is now `--ink` (the Slack-blue retirement), and ink is
      // not a colour `Alert severity="info"` can render as. `--stat-accent`
      // is this app's other palette-derived accent (the sidebar's active-item
      // rule, the selected radio edge), so info reads as brand-adjacent
      // rather than as plain body text.
      info: { main: tokens["--stat-accent"] },
      background: { default: tokens["--canvas"], paper: tokens["--surface"] },
      text: { primary: tokens["--ink"], secondary: tokens["--muted"] },
      divider: tokens["--hairline"],
    },
    spacing: 8,
    // `--r-panel`, not `--r-card`: the #864 direction makes 4 / 8 / 12 a
    // nesting hierarchy (controls / cards & panels / dialogs), and the
    // default has to be the middle step. Dialogs and inputs take their own
    // radius through the component overrides below; `MuiCard` also reads
    // `--r-panel` directly since a card is a panel-family surface, not a
    // dialog.
    shape: { borderRadius: radius("--r-panel", 8) },
    shadows: elevationScale(tokens),
    // #864 — MUI's own reduced-motion mechanism (`@mui/material@9.4.0`'s
    // `theme.motion`, consumed by `getTransitionStyles()`/`TouchRipple`):
    // "system" wraps every transition MUI itself builds through that helper
    // in `@media (prefers-reduced-motion: reduce) { transition: none }`. That
    // covers `BottomNavigationAction`'s built-in colour fade and the ripple
    // (disabled below anyway) with no JS matchMedia listener — a pure CSS
    // media query, mirroring the `no-preference` block `styles.css` already
    // gates its own hand-rolled animation behind (~L583). The ONE transition
    // this app adds by hand (`MuiButtonBase`'s press-feedback scale, below)
    // is a plain style object rather than something built through that
    // helper, so it repeats the same media query itself.
    motion: { reducedMotion: "system" },
    transitions: {
      easing: {
        easeInOut: EASE, easeOut: EASE, easeIn: EASE, sharp: EASE,
      },
      duration: { short: COLOR_TRANSITION_MS },
    },
    typography: {
      fontFamily: tokens["--font"],
      // #864 — the confirmed ruled-ledger direction's scale (DIRECTION.md),
      // not a port of today's sizes. Weight 800 and negative tracking are
      // retired; numbers are display 40/44, title 24/28 (phone 28/32),
      // section 15, rows 14/20 desktop / 16/24 phone, caption 12/16.
      // `body1` is what `CssBaseline` would apply to `<body>`; its
      // letterSpacing stays reset from MUI's 0.00938em regardless.
      body1: {
        fontSize: "0.875rem", lineHeight: 20 / 14, letterSpacing: 0,
        // Rows carry tabular numerals so a column of counts aligns by digit
        // (DIRECTION.md); harmless on non-numeral text, which this property
        // does not affect.
        fontVariantNumeric: "tabular-nums",
      },
      body2: { fontSize: "0.95rem" },
      h1: { fontFamily: "Georgia, serif", fontSize: "2.5rem", lineHeight: 44 / 40, fontWeight: 600 },
      h2: { fontFamily: "Georgia, serif", fontSize: "1.5rem", lineHeight: 28 / 24, fontWeight: 600 },
      h3: { fontFamily: tokens["--font"], fontSize: "0.9375rem", fontWeight: 600 },
      h4: { fontSize: "1.05rem", fontWeight: 700 },
      subtitle2: { fontSize: "0.85rem", fontWeight: 500, color: tokens["--muted"] },
      caption: { fontSize: "0.75rem", lineHeight: 16 / 12, fontWeight: 400, letterSpacing: 0 },
      button: {
        fontSize: "0.95rem", fontWeight: 700, letterSpacing: "0.01em", textTransform: "none",
      },
      overline: { textTransform: "none" },
    },
  });

  return createTheme(base,
    farmThemeOverrides({ tokens, mode, base, cardRadius, panelRadius, pillRadius, controlRadius, selectedNavFill }),
  );
}

/**
 * #674 — makes MUI follow the two theming axes this app already has.
 *
 * `data-brand` (the farm's accent, #149/#586) and `data-theme` (the user's
 * light/night choice) are both set on `<html>`, by the pre-paint script before
 * first paint and by Settings/ThemeToggle afterwards. Neither goes through
 * React, so React cannot learn about a change by re-rendering — hence the
 * observer. Without it, switching farms or hitting the night toggle would
 * repaint every hand-styled surface and leave every MUI surface on the old
 * palette, which is the specific split-brain this provider exists to prevent.
 *
 * Still NOT rendering `CssBaseline`, and #823 changed the reason. #822 D6 took
 * the decision to adopt it; the production CSP is `style-src 'self'`
 * (src/Cluckwork.Api/Security/SecurityHeaders.cs), so the browser refuses every
 * stylesheet Emotion injects and the baseline never applied. Measured against
 * the sim stack: the tag is in the document, `html` computes `content-box`, and
 * the console carries "Applying inline style violates ... 'style-src 'self''".
 * That blocks every MUI style, not only this one, so the theme below is
 * groundwork until the CSP is settled. Adopting it is then one element here
 * plus deleting `styles.css`'s `*` and `body` rules.
 */
export function FarmThemeProvider({ children }: { children: ReactNode }) {
  // Held in state, not derived in render: this reads live DOM state, so each
  // trigger below re-reads it.
  const [tokens, setTokens] = useState(readThemeTokens);

  useEffect(() => {
    const reread = () => setTokens(readThemeTokens());
    const observer = new MutationObserver(reread);
    observer.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ["data-brand", "data-theme"],
    });
    // The attributes can change between first render and this effect running
    // (the pre-paint script is synchronous, but Settings can commit a palette
    // in the same tick a route mounts). Re-read once on attach rather than
    // trusting the render-time read to still be current.
    reread();

    // #976 round 1 — follow-device mode must react to a LIVE OS scheme
    // change. watchDeviceTheme writes data-theme directly, which the
    // observer above already watches, so no separate recompute path is
    // needed here.
    const stopWatchingDevice = watchDeviceTheme();

    // The meta colour picks --lavender or --canvas by layout width (#976
    // round 1), which crossing MD_UP_QUERY changes without touching
    // data-theme/data-brand — this is the one trigger the observer can't see.
    // jsdom has no matchMedia (BottomNav.tsx/lib/theme.ts guard the same way).
    const layout = typeof window.matchMedia === "function" ? window.matchMedia(MD_UP_QUERY) : null;
    layout?.addEventListener("change", reread);

    return () => {
      observer.disconnect();
      stopWatchingDevice();
      layout?.removeEventListener("change", reread);
    };
  }, []);

  const theme = useMemo(() => createFarmTheme(tokens, readThemeMode()), [tokens]);

  // #974 — same cadence as the theme itself: on mount, and again whenever the
  // observer above sees data-brand/data-theme change, so the browser chrome
  // never lags the in-page one.
  useEffect(() => {
    syncThemeColorMeta(tokens);
  }, [tokens]);

  return (
    <CacheProvider value={emotionCache}>
      <ThemeProvider theme={theme}>{children}</ThemeProvider>
    </CacheProvider>
  );
}

