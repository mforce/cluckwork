import { useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import { ThemeProvider, createTheme } from "@mui/material/styles";
import type { Theme } from "@mui/material/styles";
import {
  pixelsFrom, readThemeMode, readThemeTokens,
  type ThemeMode, type TokenValues,
} from "./farmTokens";

/**
 * Build MUI's theme from one already-resolved set of this app's tokens.
 *
 * Exported for the tests, which assert the mapping directly rather than
 * through a rendered component — the thing worth pinning is that a farm's
 * `--brand` reaches `palette.primary.main`, and going via the DOM would only
 * add jsdom's inability to resolve custom properties to the assertion.
 */
export function createFarmTheme(tokens: TokenValues, mode: ThemeMode): Theme {
  return createTheme({
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
      info: { main: tokens["--link"] },
      background: { default: tokens["--canvas"], paper: tokens["--surface"] },
      text: { primary: tokens["--ink"], secondary: tokens["--muted"] },
      divider: tokens["--hairline"],
    },
    shape: { borderRadius: pixelsFrom(tokens["--r-card"], 12) },
    typography: { fontFamily: tokens["--font"] },
  });
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
 * Deliberately NOT rendering MUI's `CssBaseline`: it applies its own global
 * resets for typography, margins and box-sizing, which would fight
 * `styles.css` across all 124 components rather than only where MUI is used.
 * The app keeps its own baseline until (and unless) a later slice decides
 * otherwise — that is a whole-app visual decision, not a side effect of
 * mounting a provider.
 */
export function FarmThemeProvider({ children }: { children: ReactNode }) {
  const [signal, setSignal] = useState(0);

  useEffect(() => {
    const observer = new MutationObserver(() => setSignal((n) => n + 1));
    observer.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ["data-brand", "data-theme"],
    });
    // The attributes can change between first render and this effect running
    // (the pre-paint script is synchronous, but Settings can commit a palette
    // in the same tick a route mounts). Re-read once on attach rather than
    // trusting the render-time read to still be current.
    setSignal((n) => n + 1);
    return () => observer.disconnect();
  }, []);

  const theme = useMemo(
    // `signal` is the dependency that matters: the reads below are of live DOM
    // state, so they must re-run whenever the observer fires.
    () => createFarmTheme(readThemeTokens(), readThemeMode()),
    [signal],
  );

  return <ThemeProvider theme={theme}>{children}</ThemeProvider>;
}
