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

/**
 * The one motion curve for the whole app (#864). Borrowed from the
 * high-end-visual-design skill's easing proposal — the only one of that
 * skill's ideas compatible with the confirmed ruled-ledger direction — and
 * confirmed on the issue (2026-09-16). Every component that asks
 * `theme.transitions` for an easing gets this curve rather than MUI's
 * Material default (`cubic-bezier(0.4, 0, 0.2, 1)` and its three siblings).
 */
const EASE = "cubic-bezier(.32,.72,0,1)";

/**
 * Colour transitions: 160ms. This reuses `theme.transitions.duration.short`
 * rather than a new key: `Button` and `BottomNavigationAction` already read
 * `duration.short` for their own background-color/color/padding transitions
 * (`@mui/material@9.4.0`'s `Button.js` and `BottomNavigationAction.js`), so
 * overriding the one key they already consume gets the direction's number
 * onto every current and future MUI colour transition for free — the same
 * "read what MUI already defaults to" move `elevationScale` makes for shadows.
 */
const COLOR_TRANSITION_MS = 160;

/**
 * Transform transitions (the press-feedback scale): 240ms. MUI has no
 * built-in consumer of a "transform" duration key, so this is a literal used
 * directly in the `MuiButtonBase` override below instead of a `duration.*`
 * key nothing else would ever read.
 */
const TRANSFORM_TRANSITION_MS = 240;

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

  const phone = base.breakpoints.down("md");

  return createTheme(base, {
    // Phone-scoped type sizes (title 28/32, rows 16/24). `[phone]` needs
    // `base.breakpoints`, which does not exist until `base` is built, so
    // these live here rather than in the `typography` block above — this
    // second `createTheme` call deep-merges into `base.typography.h2`/
    // `body1` rather than replacing them.
    typography: {
      h2: { [phone]: { fontSize: "1.75rem", lineHeight: 32 / 28 } },
      body1: { [phone]: { fontSize: "1rem", lineHeight: 24 / 16 } },
    },
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
        styleOverrides: { root: { borderRadius: panelRadius } },
      },
      MuiAccordion: {
        defaultProps: { elevation: 0 },
        styleOverrides: {
          root: {
            border: `1px solid ${tokens["--hairline"]}`,
            borderRadius: controlRadius,
            marginBottom: 12,
            overflow: "hidden",
            "&:before": { display: "none" },
            "&.Mui-expanded": { margin: "0 0 12px" },
            "&:last-of-type": { marginBottom: 0 },
          },
        },
      },
      MuiAccordionSummary: {
        styleOverrides: {
          root: { backgroundColor: tokens["--surface-2"] },
        },
      },
      MuiAccordionDetails: {
        styleOverrides: { root: { padding: 18 } },
      },
      MuiDialog: { styleOverrides: { paper: { borderRadius: cardRadius } } },
      MuiOutlinedInput: { styleOverrides: { root: { borderRadius: controlRadius } } },
      // `Autocomplete` sets no elevation on its listbox paper, so it falls to
      // `Paper`'s default of 1 — which this scale flattens. The picker popover is
      // one of #651's floats, so it takes the dialog shadow explicitly.
      MuiAutocomplete: { styleOverrides: { paper: { boxShadow: base.shadows[8] } } },
      // #828 — MUI's own Tooltip default is a hardcoded dark grey
      // (`rgba(97, 97, 97, 0.92)`), the one floating surface in the app that
      // read from no farm token at all. This reads like every other float
      // (Autocomplete's listbox, Dialog's paper): the app's own surface,
      // ink and hairline tokens, the dialog shadow, and the panel radius
      // (a tooltip is an info popover, not a control). `whiteSpace: pre-line`
      // is theme-wide rather than per-callsite `sx`: it preserves a `\n`-
      // joined multi-line `title` (ProvenanceCell's stamp, Audit's JSON)
      // exactly like a native `title` attribute renders one, and a plain
      // single-line tooltip wraps exactly the same under it as under the
      // default `normal`.
      MuiTooltip: {
        styleOverrides: {
          tooltip: {
            backgroundColor: tokens["--surface"],
            color: tokens["--ink"],
            border: `1px solid ${tokens["--hairline"]}`,
            borderRadius: panelRadius,
            boxShadow: base.shadows[8],
            whiteSpace: "pre-line",
          },
          arrow: { color: tokens["--surface"] },
        },
      },
      MuiButton: {
        // `Button` reads `shadows[2]`, `[4]`, `[6]` and `[8]` for the contained
        // variant's rest, hover, focus and press states, so under this scale a
        // primary button would cast the bar shadow on hover and the dialog
        // shadow on press.
        defaultProps: { disableElevation: true },
        styleOverrides: {
          root: {
            // DIRECTION.md line 17 (#864): controls take the 4px control
            // radius, not the pill — the mockup's buttons are 4px rectangles.
            // MuiChip alone keeps the pill.
            borderRadius: controlRadius,
            // Phone-scoped, because `Button variant="text"` is where the app's
            // inline row actions land: an unconditional floor would add ~20px to
            // every row of 22 ledger tables at 1280.
            [phone]: { minHeight: PHONE_TOUCH_TARGET_PX },
          },
        },
      },
      MuiChip: { styleOverrides: { root: { borderRadius: pillRadius } } },
      // No `MuiDialogActions` override: dialog footers stay a right-aligned
      // row at every width (#896, owner decision 2026-09-17, a D3.4 exception
      // like the Daily entry footer), which is the component's own default.
      // A raw `<label>` styles.css still targets (§2.3's `:where(label)`
      // demotion neutralises it only where MUI itself declares the property —
      // `FormControlLabel` never declares `flex-direction`/`gap` on its own
      // root, so the zero-specificity rule was the only source and stacked its
      // checkbox above its label instead of beside it.
      //
      // `flexDirection` and `gap` need different scopes, because MUI's own
      // coverage of the two differs. `FormControlLabel` carries its own
      // `variants` for `labelPlacement="start"|"top"|"bottom"` (`row-reverse`,
      // `column-reverse`, `column`), each with real specificity that already
      // beats `:where(label)`'s zero — so `flexDirection` on `root`
      // unconditionally would sit AHEAD of those variants and win regardless
      // of placement; measured directly, it computed `row` for all four.
      // Scoped to the `labelPlacementEnd` slot instead: MUI composes
      // `styles[labelPlacementEnd]` only when that IS the resolved placement
      // (`end`, the default and the only one this app uses today), leaving
      // the other three to MUI's own variants untouched. `gap` gets no such
      // variant from MUI at ANY placement — it spaces the control from the
      // label with `margin` instead (visible in the root's own
      // `margin-left`/`margin-right`) — so `:where(label)`'s 0.35rem leaks
      // at every placement equally and the reset belongs on `root`.
      MuiFormControlLabel: {
        styleOverrides: {
          root: { gap: 0 },
          labelPlacementEnd: { flexDirection: "row" },
        },
      },
      // #864 — no ripple by default anywhere: motion here is this direction's
      // own choice, not Material's canned ink-spread. `MuiButtonBase` is the
      // base every `Button`, `IconButton`, `Tab`, `Chip` and
      // `BottomNavigationAction` extends, so setting it once here reaches all
      // of them without a per-component default.
      MuiButtonBase: {
        defaultProps: { disableRipple: true },
        styleOverrides: {
          root: {
            // Press feedback (#864 borrow-list item 3): a tap reads as a
            // press. Scoped to the sibling `MuiButton-contained`/`-outlined`
            // classes MUI composes onto this SAME node, never the bare
            // `MuiButtonBase` root or a `MuiButton-text` sibling — the
            // owner's desktop render showed a filled-button tint behind
            // underlined ruled-text actions read as a smudge, so a ruled-text
            // row action (and any other bare `ButtonBase`, e.g. an
            // `IconButton`) gets no transform at all.
            "&.MuiButton-contained, &.MuiButton-outlined": {
              // `transition` is a shorthand for every property in one
              // declaration: naming only `transform` here does not ADD a
              // press transition alongside `Button.js`'s own
              // `background-color`/`box-shadow`/`border-color`/`color` list
              // (`duration.short`, i.e. `COLOR_TRANSITION_MS`) — it REPLACES
              // that whole declaration, because this compound-class selector
              // has higher specificity than `Button.js`'s own single-class
              // rule and both target the same `transition` property. Found by
              // a Codex review of #882 (2026-09-16): a rendered contained
              // button computed `transition: transform 240ms ...` with no
              // colour transition at all, silently dropping the "colour
              // transitions reuse duration.short at 160ms" policy for every
              // contained/outlined button.
              transition: [
                `transform ${TRANSFORM_TRANSITION_MS}ms ${EASE}`,
                `background-color ${COLOR_TRANSITION_MS}ms ${EASE}`,
                `box-shadow ${COLOR_TRANSITION_MS}ms ${EASE}`,
                `border-color ${COLOR_TRANSITION_MS}ms ${EASE}`,
                `color ${COLOR_TRANSITION_MS}ms ${EASE}`,
              ].join(", "),
              "&:active": { transform: "scale(0.98)" },
              // `theme.motion.reducedMotion: "system"` above covers every
              // transition MUI itself builds through `getTransitionStyles()`;
              // this one is a plain style object, not built through that
              // helper, so it repeats the same media query by hand.
              "@media (prefers-reduced-motion: reduce)": { transition: "none" },
            },
          },
        },
      },
      // Tab bar variant B ("ruled"), owner pick (2026-09-16): no bar shadow,
      // a hairline top rule instead. #829 mounts the component; this is its
      // theme, so the conversion inherits the pick rather than deciding it.
      MuiBottomNavigation: {
        styleOverrides: {
          root: {
            boxShadow: "none",
            borderTop: `1px solid ${tokens["--hairline"]}`,
            backgroundColor: tokens["--surface"],
          },
        },
      },
      MuiBottomNavigationAction: {
        styleOverrides: {
          root: {
            minHeight: PHONE_TOUCH_TARGET_PX,
            // The `icon` prop renders as a direct child with no wrapper class
            // (`bottomNavigationActionClasses` names only `root`, `iconOnly`,
            // `selected`, `label` — no `icon` slot), so sizing it has to reach
            // through to whatever SVG the caller passes, the same way
            // `MuiButton`'s pill radius above reaches every button regardless
            // of what rendered it.
            "& > svg": { width: 24, height: 24 },
            // The 2px `--stat-accent` rule above the active tab (variant B) —
            // the same device the sidebar's active-item rule uses (`.sidebar
            // nav a.active`, `styles.css`), so the two shells say "active"
            // the same way.
            "&.Mui-selected": {
              color: tokens["--stat-accent"],
              boxShadow: `inset 0 2px 0 0 ${tokens["--stat-accent"]}`,
            },
          },
          label: {
            // One label size at every state. MUI's own default bumps a
            // selected label from 12px to 14px
            // (`BottomNavigationAction.js`'s `&.selected` rule), which this
            // direction does not want: labels are 500-weight 11px whether
            // selected or not, and only the colour (MUI's own
            // `palette.primary.main` on `.Mui-selected`) and the rule above
            // the icon carry "active".
            fontSize: 11,
            fontWeight: 500,
            "&.Mui-selected": { fontSize: 11 },
          },
        },
      },
      // `Tabs`' own default (`textColor="primary"`) paints the selected label
      // with `palette.primary.main` (raw `--brand`), which styles.css keeps
      // out of the dark block on purpose and so reads near-invisible on a
      // dark surface. `--stat-accent` is the same "active" device
      // `MuiBottomNavigationAction` and the sidebar already use.
      MuiTabs: {
        styleOverrides: {
          indicator: { backgroundColor: tokens["--stat-accent"] },
        },
      },
      MuiTab: {
        styleOverrides: {
          root: {
            "&.Mui-selected": { color: tokens["--stat-accent"] },
          },
        },
      },
      // Ledger row heights (DIRECTION.md): 36px desktop, 52px phone.
      MuiTableRow: {
        styleOverrides: {
          root: { height: 36, [phone]: { height: 52 } },
        },
      },
      // #832 — real-device #441 repro, reproduced again on this PR's first
      // Playwright pass: `TableContainer`'s own default (`width: 100%;
      // overflow-x: auto`, `TableContainer.js`) correctly scrolls the table
      // WITHIN itself, but mobile browsers' initial LAYOUT viewport sizing
      // still measures the un-clipped table's raw content width and inflates
      // `window.innerWidth` past the visual viewport — `overflow-x` on every
      // ancestor does not stop it, confirmed on `/flocks` at 390 (measured
      // `document.documentElement.scrollWidth` 941px against a 390px frame,
      // `phone.spec.ts`'s "no walked screen overflows" walk). `contain:
      // layout` is what closes the gap: it tells the browser this element's
      // internal layout can never affect an ancestor's size. `styles.css`'s
      // own `table.data` phone rule (§2.2) already carries this for every
      // unconverted ledger; this is the same fix for MUI's `TableContainer`,
      // the component D3.2 names as this app's phone table treatment going
      // forward.
      MuiTableContainer: {
        styleOverrides: {
          root: {
            [phone]: {
              contain: "layout",
              // Local covers hide the scroll shadows when the corresponding edge is reached.
              background:
                "linear-gradient(to right, var(--surface) 40%, transparent) 0 0 / 2.25rem 100% no-repeat local,"
                + "linear-gradient(to left, var(--surface) 40%, transparent) 100% 0 / 2.25rem 100% no-repeat local,"
                + "linear-gradient(to right, var(--scroll-cue), transparent) 0 0 / 0.85rem 100% no-repeat scroll,"
                + "linear-gradient(to left, var(--scroll-cue), transparent) 100% 0 / 0.85rem 100% no-repeat scroll",
            },
          },
        },
      },
      // #832 — closes the gap the comment above used to carry: this is the
      // first slice to mount a real MUI `Table` (Customers/Products/Grades/
      // Flocks/Users). `TableCell` spreads `theme.typography.body2` as its
      // base (`TableCell.js`) — 0.95rem at MUI's default 1.43 line-height
      // (~21.7px), never this theme's row scale — so left alone it would sit
      // taller than DIRECTION.md's 14/20 desktop / 16/24 phone rows and carry
      // no tabular numerals. Pinning `fontSize`/`lineHeight` here to the same
      // numbers `body1` already carries (rather than pointing `variant` at
      // `body1`, which would also move the `variantMapping` element) reaches
      // every `TableCell` regardless of context.
      //
      // Padding is `table.data td`'s own value rather than `size="small"`'s
      // symmetric default: the default's extra 16px per cell pushed Flocks'
      // widest row past its container at 1280.
      MuiTableCell: {
        styleOverrides: {
          root: {
            fontSize: "0.875rem",
            lineHeight: 20 / 14,
            fontVariantNumeric: "tabular-nums",
            padding: "0.6rem 1rem 0.6rem 0",
            [phone]: { fontSize: "1rem", lineHeight: 24 / 16 },
          },
        },
      },
      // Sidebar shell (D2 pair 12, #829). `Drawer`'s paper defaults to
      // `background.paper`; DIRECTION.md's confirmed nav rail is `--lavender`
      // tinted paper with `--stat-accent` text, not the aubergine `--brand`
      // slab `.sidebar` painted before this slice — brand appears in exactly
      // four places now (farm name, active nav item, primary button, focus
      // ring), and this is the rail's share of that.
      MuiDrawer: {
        styleOverrides: {
          paper: {
            backgroundColor: tokens["--lavender"],
            borderRight: `1px solid ${tokens["--hairline"]}`,
          },
        },
      },
      // The group heading keeps its own CSS class (`.nav-group-label` /
      // `.more-group-label`, styles.css — D4/#824's guard keys on those two
      // selectors for the caps casing), so this only neutralises MUI's own
      // subheader chrome (its sticky positioning and background) rather than
      // fighting the class for size and colour.
      MuiListSubheader: {
        styleOverrides: {
          root: {
            position: "static",
            backgroundColor: "transparent",
            lineHeight: "inherit",
          },
        },
      },
      MuiListItemButton: {
        styleOverrides: {
          root: {
            borderLeft: "3px solid transparent",
            gap: 8,
            "&.Mui-selected, &.Mui-selected:hover": {
              backgroundColor: selectedNavFill,
              borderLeftColor: selectedNavFill,
              color: tokens["--on-brand"],
              fontWeight: 600,
              "& .MuiListItemIcon-root": { color: "inherit" },
            },
            "&:hover": { backgroundColor: tokens["--surface-2"] },
          },
        },
      },
      // #834 — DIRECTION.md's link language for a real MUI `Link`: ink text,
      // underlined in the 28% ink rule at rest, full ink on hover and focus.
      // No screen renders `@mui/material`'s `Link` today (they render
      // `Typography component={Link}` from react-router, styled by
      // `:where(.content a)` in styles.css instead), so this is groundwork —
      // it has to exist before a screen can adopt the real component, and
      // `farmTheme.policy.test.ts` is where it is pinned.
      MuiLink: {
        defaultProps: { underline: "always" },
        styleOverrides: {
          root: {
            color: tokens["--ink"],
            textDecorationColor: tokens["--link-rule"],
            textUnderlineOffset: "2px",
            "&:hover, &:focus-visible": {
              textDecorationColor: tokens["--ink"],
            },
          },
        },
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
    <CacheProvider value={emotionCache}>
      <ThemeProvider theme={theme}>{children}</ThemeProvider>
    </CacheProvider>
  );
}
