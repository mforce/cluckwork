import { useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import CssBaseline from "@mui/material/CssBaseline";
import { ThemeProvider, createTheme } from "@mui/material/styles";
import type { Shadows, Theme } from "@mui/material/styles";
import {
  pixelsFrom, readThemeMode, readThemeTokens,
  type ThemeMode, type ThemeToken, type TokenValues,
} from "./farmTokens";

/** WCAG 2.2 AAA 2.5.5, and the floor `styles.css` already holds phone controls to. */
const PHONE_TOUCH_TARGET_PX = 44;

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
 * Build MUI's theme from one already-resolved set of this app's tokens.
 *
 * Exported for the tests, which assert the mapping directly rather than
 * through a rendered component — the thing worth pinning is that a farm's
 * `--brand` reaches `palette.primary.main`, and going via the DOM would only
 * add jsdom's inability to resolve custom properties to the assertion.
 */
export function createFarmTheme(tokens: TokenValues, mode: ThemeMode): Theme {
  const radius = (token: ThemeToken, fallback: number) => pixelsFrom(tokens[token], fallback);
  const cardRadius = radius("--r-card", 16);
  const pillRadius = radius("--r-pill", 999);

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
      info: { main: tokens["--link"] },
      background: { default: tokens["--canvas"], paper: tokens["--surface"] },
      text: { primary: tokens["--ink"], secondary: tokens["--muted"] },
      divider: tokens["--hairline"],
    },
    spacing: 8,
    // `--r-panel`, not `--r-card`: #651 D2 made 6 / 10 / 16 a nesting hierarchy,
    // and the default has to be the middle of it. Cards, dialogs and inputs take
    // their own radius through the component overrides below.
    shape: { borderRadius: radius("--r-panel", 10) },
    shadows: elevationScale(tokens),
    typography: {
      fontFamily: tokens["--font"],
      // Sizes copied from what the stylesheet already renders, not a new scale.
      // `body1` is what `CssBaseline` applies to `<body>`, so its letterSpacing
      // is reset from MUI's 0.00938em: leaving the default would re-track every
      // paragraph in the app on the day the baseline lands.
      body1: { fontSize: "1rem", lineHeight: 1.5, letterSpacing: 0 },
      body2: { fontSize: "0.95rem" },
      h1: { fontSize: "2rem", fontWeight: 800 },
      h2: { fontSize: "1.9rem", fontWeight: 800, letterSpacing: "-0.02em", lineHeight: 1.15 },
      h3: { fontSize: "1.15rem", fontWeight: 700, letterSpacing: "-0.01em" },
      h4: { fontSize: "1.05rem", fontWeight: 700 },
      subtitle2: { fontSize: "0.85rem", fontWeight: 500, color: tokens["--muted"] },
      caption: { fontSize: "0.78rem", fontWeight: 700, letterSpacing: 0 },
      button: {
        fontSize: "0.95rem", fontWeight: 700, letterSpacing: "0.01em", textTransform: "none",
      },
      overline: { textTransform: "none" },
    },
  });

  const phone = base.breakpoints.down("md");

  return createTheme(base, {
    components: {
      MuiTypography: {
        defaultProps: {
          // MUI maps `subtitle2` onto `<h6>` by default, which would make a
          // field label a heading and break the role-and-name queries the screen
          // tests are written with. The headings keep their own elements.
          variantMapping: {
            h1: "h1", h2: "h2", h3: "h3", h4: "h4",
            subtitle1: "span", subtitle2: "span",
            body1: "p", body2: "p",
          },
        },
      },
      // Flat by default, so a `Paper` added without thought casts no shadow.
      // Deliberately NOT `variant: "outlined"`: `Paper` only sets
      // `--Paper-shadow` for `variant === "elevation"`, and none of the floats
      // pass a variant, so an outlined default would strip the shadow from every
      // dialog, drawer, menu and snackbar.
      MuiPaper: { defaultProps: { elevation: 0 } },
      MuiCard: {
        defaultProps: { variant: "outlined" },
        styleOverrides: { root: { borderRadius: cardRadius } },
      },
      MuiDialog: { styleOverrides: { paper: { borderRadius: cardRadius } } },
      MuiOutlinedInput: { styleOverrides: { root: { borderRadius: radius("--r-input", 6) } } },
      // `Autocomplete` sets no elevation on its listbox paper, so it falls to
      // `Paper`'s default of 1 — which this scale flattens. The picker popover is
      // one of #651's floats, so it takes the dialog shadow explicitly.
      MuiAutocomplete: { styleOverrides: { paper: { boxShadow: base.shadows[8] } } },
      MuiButton: {
        // `Button` reads `shadows[2]`, `[4]`, `[6]` and `[8]` for the contained
        // variant's rest, hover, focus and press states, so under this scale a
        // primary button would cast the bar shadow on hover and the dialog
        // shadow on press.
        defaultProps: { disableElevation: true },
        styleOverrides: {
          root: {
            borderRadius: pillRadius,
            // Phone-scoped, because `Button variant="text"` is where the app's
            // inline row actions land: an unconditional floor would add ~20px to
            // every row of 22 ledger tables at 1280.
            [phone]: { minHeight: PHONE_TOUCH_TARGET_PX },
          },
        },
      },
      MuiChip: { styleOverrides: { root: { borderRadius: pillRadius } } },
      // #740 / D3.4. `DialogActions` sets `alignItems: center`, which would leave
      // stacked buttons at their intrinsic width.
      MuiDialogActions: {
        styleOverrides: { root: { [phone]: { flexDirection: "column", alignItems: "stretch" } } },
      },
    },
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
 * #823 adopts `CssBaseline`, which replaces `styles.css`'s own `*` and `body`
 * resets and re-leads body text from the browser's `normal` to `body1`'s 1.5.
 * Reversible: drop the element below and restore those two rules.
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

  return (
    <ThemeProvider theme={theme}>
      <CssBaseline />
      {children}
    </ThemeProvider>
  );
}
